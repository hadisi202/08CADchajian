using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using FurniturePlugin.Optimization;

namespace FurniturePlugin;

public static class PaiBanCommandService
{
    private sealed class SourcePart
    {
        public string Name { get; set; } = "";
        public string HandleText { get; set; } = "";
        public string Material { get; set; } = "";
        public double Length { get; set; }
        public double Width { get; set; }
        public double Thickness { get; set; }
        public TextureDirection TextureDirection { get; set; } = TextureDirection.AlongLength;
        public string EdgeInfo { get; set; } = "";
        public string SkipReason { get; set; } = "";
        public string CabinetId { get; set; } = "";
        public string PanelNumber { get; set; } = "";
        public double CuttingLength { get; set; }
        public double CuttingWidth { get; set; }
        public double CuttingThickness { get; set; }
        public List<Pt2d> LocalOutline { get; set; }
        public List<List<Pt2d>> LocalInnerOutlines { get; set; }
    }

    private sealed class ExtractionResult
    {
        public List<SourcePart> RectangularParts { get; } = new();
        public List<SourcePart> IrregularParts { get; } = new();
        public List<SourcePart> SkippedParts { get; } = new();
        public HashSet<string> AllMaterials { get; } = new(StringComparer.OrdinalIgnoreCase);

        // FLATSHOT 流水线统计
        public int FlatshotAttempts { get; set; }
        public int FlatshotSuccesses { get; set; }
        public int InnerHolesDetected { get; set; }
        public double MaxNormalAngleDeg { get; set; }
        public double MinEdgeChainRate { get; set; } = 1.0;
        public List<string> ExtractionDiagnostics { get; } = new();

        public double FlatshotSuccessRate =>
            FlatshotAttempts == 0 ? 1.0 : (double)FlatshotSuccesses / FlatshotAttempts;
    }

    public static void Run()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        Editor editor = doc.Editor;
        try
        {
            Database db = doc.Database;

            PromptSelectionOptions selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\n选择需要排版的板件: ",
                AllowDuplicates = false
            };

            PromptSelectionResult selectionResult = editor.GetSelection(selectionOptions);
            if (selectionResult.Status != PromptStatus.OK || selectionResult.Value == null)
                return;

            ExtractionResult extraction = ExtractParts(db, selectionResult.Value.GetObjectIds());
            if (extraction.RectangularParts.Count == 0 && extraction.IrregularParts.Count == 0)
            {
                editor.WriteMessage(
                    $"\n未找到可排版的板件。跳过 {extraction.SkippedParts.Count} 个。");
                return;
            }

            PromptPointResult insertPointResult = editor.GetPoint("\n指定排版图插入点: ");
            if (insertPointResult.Status != PromptStatus.OK)
                return;

            List<NestingService.NestingItem> nestingItems = ConvertToNestingItems(extraction.RectangularParts);
            List<NestingService.NestingItem> irregularItems = ConvertToNestingItems(extraction.IrregularParts);

            var allItems = new List<NestingService.NestingItem>();
            allItems.AddRange(nestingItems);
            allItems.AddRange(irregularItems);

            NestingService.NestingResult result = null;
            NestingService.NestingConfig config = null;
            bool exportCsv = true;
            double partSpacing = 5;
            PaiBanConfigSnapshot lastSnapshot = null;

            bool firstConfig = true;
            List<UnplaceableItem> acknowledgedPrecheckItems = new();
            while (true)
            {
                if (!TryShowConfigDialog(editor, out PaiBanWindow window, extraction.AllMaterials, !firstConfig, lastSnapshot))
                    return;
                firstConfig = false;

                lastSnapshot = window.CreateSnapshot();

                exportCsv = window.ExportCsv;
                partSpacing = window.PartSpacing;

                var sheetSpecs = BuildSheetSpecs(window);

                var materialSpecs = new Dictionary<string, List<NestingService.SheetSpec>>(StringComparer.OrdinalIgnoreCase);
                if (window.MaterialSheetSpecConfigs != null)
                {
                    foreach (var kvp in window.MaterialSheetSpecConfigs)
                    {
                        if (kvp.Value == null || kvp.Value.Count == 0) continue;
                        materialSpecs[kvp.Key] = kvp.Value
                            .Select(s => new NestingService.SheetSpec { Width = s.Width, Height = s.Height })
                            .ToList();
                    }
                }

                config = new NestingService.NestingConfig
                {
                    SheetWidth = window.SheetWidth,
                    SheetHeight = window.SheetHeight,
                    AvailableSheetSpecs = sheetSpecs,
                    MaterialSheetSpecs = materialSpecs,
                    PartSpacing = window.PartSpacing,
                    EdgeMargin = window.EdgeMargin,
                    AllowRotation = window.AllowRotate,
                    GroupByMaterial = window.GroupByMaterial,
                    MaxSheets = 100,
                    Allow360RotationMaterials = window.Allow360Materials ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                };

                // 先做“超尺寸预检”：避免先启动排版计算窗口，再因为超尺寸中断流程。
                var preOversized = FindOversizedItems(allItems, config);
                var solveItems = allItems;
                if (preOversized.Count > 0)
                {
                    var preDialog = new UnplaceableDialog(preOversized, null, true);
                    bool? preResult = Application.ShowModalWindow(preDialog);
                    if (preResult == true && preDialog.SkipAndContinue)
                    {
                        acknowledgedPrecheckItems = preOversized
                            .Select(item => new UnplaceableItem
                            {
                                Handle = item.Handle,
                                OrderId = item.OrderId,
                                CabinetId = item.CabinetId,
                                Material = item.Material,
                                Name = item.Name,
                                Length = item.Length,
                                Width = item.Width,
                                Thickness = item.Thickness,
                                PartType = item.PartType,
                                Reason = item.Reason,
                                Category = item.Category
                            })
                            .ToList();
                        // 忽略继续：将超尺寸件从本次排版输入中剔除。
                        var oversizedHandles = new HashSet<string>(
                            preOversized.Select(x => x.Handle ?? ""),
                            StringComparer.OrdinalIgnoreCase);
                        solveItems = allItems
                            .Where(i => i != null && !oversizedHandles.Contains(i.EntityHandle ?? ""))
                            .ToList();
                        if (solveItems.Count == 0)
                        {
                            editor.WriteMessage("\n全部板件均超出当前大板规格，已无可排版板件。");
                            return;
                        }
                    }
                    else if (preResult == true && preDialog.GoBack)
                    {
                        // 返回上一步：回到配置窗口，保持快照继承。
                        continue;
                    }
                    else
                    {
                        editor.WriteMessage("\n用户取消排版。请修改大板规格或板件数据后重试。");
                        return;
                    }
                }

                if (window.SolveMode == NestingSolveMode.GlobalCpSat)
                {
                    if (!IsOrToolsAvailable(out var ortoolsReason))
                    {
                        editor.WriteMessage($"\n[全局优化] OR-Tools不可用，已自动回退启发式。原因: {ortoolsReason}");
                        result = NestingService.Nest(solveItems, config);
                        result = RunContinuousOptimization(result, solveItems, config, editor);
                    }
                    else
                    {
                        var globalOptimizer = new GlobalOptimizationService();
                        var optOptions = new GlobalOptimizationOptions
                        {
                            TimeLimitSeconds = window.GlobalOptTimeLimitSeconds,
                            ShowRunWindow = window.ShowOptimizationRunWindow,
                            EnableIrregularExactPoc = window.EnableIrregularExactPoc,
                            MaxIrregularExactCount = window.IrregularExactPocThreshold
                        };

                        using var cts = new CancellationTokenSource();
                        OptimizationRunWindow runWindow = null;
                        bool cancelRollbackRequested = false;
                        bool adoptCurrentBestRequested = false;

                        if (optOptions.ShowRunWindow)
                        {
                            runWindow = new OptimizationRunWindow();
                            globalOptimizer.ProgressChanged += progress =>
                            {
                                try
                                {
                                    runWindow.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        runWindow.UpdateProgress(progress);
                                    }));
                                }
                                catch { }
                                try
                                {
                                    if (!string.IsNullOrWhiteSpace(progress.Message))
                                        editor.WriteMessage($"\n[全局优化] {progress.Message} 利用率 {progress.BestUtilization * 100:F1}%");
                                }
                                catch { }
                            };
                        }
                        else
                        {
                            globalOptimizer.ProgressChanged += progress =>
                            {
                                try
                                {
                                    if (!string.IsNullOrWhiteSpace(progress.Message))
                                        editor.WriteMessage($"\n[全局优化] {progress.Message} 利用率 {progress.BestUtilization * 100:F1}%");
                                }
                                catch { }
                            };
                        }

                        var solveTask = Task.Run(() => globalOptimizer.Solve(solveItems, config, optOptions, cts.Token));
                        if (runWindow != null)
                        {
                            DateTime runWindowShownAt = DateTime.Now;
                            const double minShowSeconds = 10.0;
                            var pollTimer = new System.Windows.Threading.DispatcherTimer
                            {
                                Interval = TimeSpan.FromMilliseconds(200)
                            };
                            pollTimer.Tick += (_, __) =>
                            {
                                if (runWindow.RequestCancelAndRollback && !cancelRollbackRequested)
                                {
                                    cancelRollbackRequested = true;
                                    cts.Cancel();
                                }
                                if (runWindow.RequestAdoptCurrentBest && !adoptCurrentBestRequested)
                                {
                                    adoptCurrentBestRequested = true;
                                    cts.Cancel();
                                }
                                if (solveTask.IsCompleted)
                                {
                                    var shownSec = (DateTime.Now - runWindowShownAt).TotalSeconds;
                                    if (shownSec >= minShowSeconds)
                                    {
                                        pollTimer.Stop();
                                        try { runWindow.SetCompleted("求解结束，正在整理结果..."); } catch { }
                                        if (runWindow.IsVisible) runWindow.Close();
                                    }
                                }
                            };
                            pollTimer.Start();

                            try
                            {
                                Application.ShowModalWindow(runWindow);
                            }
                            finally
                            {
                                pollTimer.Stop();
                            }
                        }

                        GlobalOptimizationResult optResult;
                        try
                        {
                            optResult = solveTask.GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            editor.WriteMessage($"\n[全局优化] 执行失败，已自动回退启发式。原因: {ex.Message}");
                            result = NestingService.Nest(solveItems, config);
                            result = RunContinuousOptimization(result, solveItems, config, editor);
                            optResult = null;
                        }

                        if (cancelRollbackRequested)
                        {
                            editor.WriteMessage("\n[全局优化] 用户取消并回退，本次排版已终止。");
                            return;
                        }

                        if (optResult != null)
                        {
                            result = optResult.NestingResult ?? new NestingService.NestingResult();
                            if (adoptCurrentBestRequested)
                                editor.WriteMessage("\n[全局优化] 用户选择采用当前最优并停止。");

                            editor.WriteMessage($"\n[全局优化] {optResult.SolverSummary} 耗时 {optResult.ElapsedSeconds:F1}s");

                            if (optResult.StopReason == OptimizeStopReason.Error)
                            {
                                editor.WriteMessage("\n[全局优化] 求解器返回错误状态，已自动回退启发式。");
                                result = NestingService.Nest(solveItems, config);
                                result = RunContinuousOptimization(result, solveItems, config, editor);
                            }
                        }
                    }
                }
                else
                {
                    result = NestingService.Nest(solveItems, config);
                    result = RunContinuousOptimization(result, solveItems, config, editor);
                }
                if (result.UnplacedCount > 0)
                {
                    var unplaceableList = BuildUnplaceableList(result);
                    var dialog = new UnplaceableDialog(acknowledgedPrecheckItems, unplaceableList, false);
                    bool? dlgResult = Application.ShowModalWindow(dialog);

                    if (dlgResult == true && dialog.SkipAndContinue)
                    {
                        result.UnplacedItems.Clear();
                        result.UnplacedIrregularItems.Clear();
                        ComputeStatsOnlyPlaced(result);
                        break;
                    }
                    else if (dlgResult == true && dialog.GoBack)
                    {
                        continue;
                    }
                    else
                    {
                        editor.WriteMessage("\n用户取消排版。请修改大板规格或板件数据后重试。");
                        return;
                    }
                }
                else
                {
                    break;
                }
            }

            // 余料区计算与绘制已下线：依用户要求，常规矩形与异形件都专注省料算法本身，
            // 不再绘制绿色余料矩形/标注。
            var remnants = new List<(int SheetIndex, RemnantService.RemnantArea Remnant)>();
            NormalizeResultReportData(result);

            using Transaction tr = db.TransactionManager.StartTransaction();
            BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace =
                (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            DrawResult(tr, modelSpace, insertPointResult.Value, config, result, extraction, remnants);
            tr.Commit();

            string csvPath = null;
            if (exportCsv)
            {
                csvPath = ExportCsv(result, extraction);
            }

            StringBuilder summary = new StringBuilder();
            int totalSheets = result.Sheets.Count + result.IrregularSheets.Count;
            int totalPlaced = PlacedCount(result);
            summary.Append($"\n排版完成: {totalSheets} 张板, 已放入 {totalPlaced} 件");
            if (result.UnplacedCount > 0)
                summary.Append($", 未放入 {result.UnplacedCount} 件(已忽略)");
            if (result.IrregularSheets.Count > 0)
                summary.Append($", 异形件已排 {result.IrregularSheets.Count} 张板");
            if (extraction.SkippedParts.Count > 0)
                summary.Append($", 跳过 {extraction.SkippedParts.Count} 件");
            summary.Append($", 总利用率 {result.TotalUtilization * 100.0:F1}%");
            AppendSkippedReasonSummary(summary, extraction);
            if (extraction.FlatshotAttempts > 0)
            {
                summary.Append($"\n[FLATSHOT] 异形提取 {extraction.FlatshotSuccesses}/{extraction.FlatshotAttempts}" +
                               $" 成功率 {extraction.FlatshotSuccessRate * 100:F1}%, " +
                               $"内孔 {extraction.InnerHolesDetected} 个, " +
                               $"BZBJ最大法向角 {extraction.MaxNormalAngleDeg:F4}°, " +
                               $"边链最低成功率 {extraction.MinEdgeChainRate * 100:F1}%");
                // 列出每个失败件的原因（最多 5 行，方便定位）
                if (extraction.ExtractionDiagnostics.Count > 0)
                {
                    int show = Math.Min(5, extraction.ExtractionDiagnostics.Count);
                    for (int i = 0; i < show; i++)
                        summary.Append($"\n  · {extraction.ExtractionDiagnostics[i]}");
                    if (extraction.ExtractionDiagnostics.Count > show)
                        summary.Append($"\n  · ... 另 {extraction.ExtractionDiagnostics.Count - show} 件，详见日志");
                }
            }
            var spacingDiag = NestingService.LastIrregularSpacingDiag;
            if (spacingDiag != null && spacingDiag.SheetCount > 0)
            {
                summary.Append($"\n[间距校验] 设定 {partSpacing}mm, 实测最小 {spacingDiag.SpacingActualMin:F2}mm, " +
                               $"最大偏差 {spacingDiag.SpacingErrorMaxMm:F2}mm, " +
                               $"{(spacingDiag.WithinTolerance ? "✓ 全部 ≤0.5mm" : "✗ 超差")}");
            }
            if (!string.IsNullOrWhiteSpace(csvPath))
                summary.Append($"\nCSV: {csvPath}");
            summary.Append("\n渐进式排版：小板优先，异形件按真实2D轮廓+内孔排版。");
            editor.WriteMessage(summary.ToString());
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nPAIBAN 执行失败: {ex.Message}\n{ex.StackTrace}");
        }
    }

    #region 持续优化

    /// <summary>
    /// 持续优化排版：后台搜索更优方案，3秒后提示用户选择
    /// </summary>
    private static NestingService.NestingResult RunContinuousOptimization(
        NestingService.NestingResult initialResult,
        List<NestingService.NestingItem> items,
        NestingService.NestingConfig config,
        Editor editor)
    {
        if (initialResult.TotalUtilization >= 0.98)
        {
            editor.WriteMessage($"\n初始排版利用率已达 {initialResult.TotalUtilization * 100:F1}%，无需优化。");
            return initialResult;
        }

        using var optimizer = new NestingOptimizer();
        var bestResult = initialResult;
        bool foundBetter = false;
        int betterCount = 0;

        optimizer.OnBetterResultFound += update =>
        {
            foundBetter = true;
            betterCount++;
            bestResult = update.Result;
        };

        optimizer.StartOptimization(items, config, initialResult);

        Thread.Sleep(3000);
        optimizer.Stop();

        if (foundBetter)
        {
            double oldUtil = initialResult.TotalUtilization * 100;
            double newUtil = bestResult.TotalUtilization * 100;
            int iterations = optimizer.IterationCount;

            var msgResult = MessageBox.Show(
                $"持续优化完成（{iterations} 次迭代）\n\n" +
                $"初始利用率: {oldUtil:F1}%\n" +
                $"优化后利用率: {newUtil:F1}%\n" +
                $"提升: +{newUtil - oldUtil:F1}%\n\n" +
                $"是否使用优化后的方案？",
                "排版优化", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (msgResult == MessageBoxResult.Yes)
            {
                editor.WriteMessage($"\n使用优化方案：{oldUtil:F1}% → {newUtil:F1}%（+{newUtil - oldUtil:F1}%）");
                return bestResult;
            }
            else
            {
                editor.WriteMessage($"\n使用初始方案：{oldUtil:F1}%");
                return initialResult;
            }
        }

        editor.WriteMessage($"\n优化未发现更优方案（{optimizer.IterationCount} 次迭代），使用初始排版。");
        return initialResult;
    }

    #endregion

    private static bool IsOrToolsAvailable(out string reason)
    {
        reason = "";
        try
        {
            var t = Type.GetType("Google.OrTools.Sat.CpModel, Google.OrTools", throwOnError: false);
            if (t != null)
                return true;
            reason = "未找到 Google.OrTools 程序集。";
            return false;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static List<UnplaceableItem> FindOversizedItems(
        List<NestingService.NestingItem> items,
        NestingService.NestingConfig config)
    {
        var list = new List<UnplaceableItem>();
        if (items == null || config == null) return list;

        foreach (var item in items)
        {
            if (item == null) continue;
            if (item.Length <= 0 || item.Width <= 0) continue;

            var specs = config.GetSheetSpecsForMaterial(item.Material)
                .Where(s => s != null && s.Width > 0 && s.Height > 0)
                .ToList();
            if (specs.Count == 0) continue;

            bool canRotate = config.AllowRotation &&
                (item.TextureDirection == TextureDirection.AlongLength ||
                 (config.Allow360RotationMaterials != null &&
                  config.Allow360RotationMaterials.Contains(item.Material ?? "")));

            bool fitsAny = false;
            foreach (var spec in specs)
            {
                bool fitNormal = item.Length + 2 * config.EdgeMargin <= spec.Width &&
                                 item.Width + 2 * config.EdgeMargin <= spec.Height;
                bool fitRot = canRotate &&
                              item.Width + 2 * config.EdgeMargin <= spec.Width &&
                              item.Length + 2 * config.EdgeMargin <= spec.Height;
                if (fitNormal || fitRot)
                {
                    fitsAny = true;
                    break;
                }
            }

            if (!fitsAny)
            {
                list.Add(new UnplaceableItem
                {
                    Handle = item.EntityHandle ?? "",
                    OrderId = item.PanelNumber ?? "",
                    CabinetId = item.CabinetId ?? "",
                    Material = item.Material ?? "",
                    Name = item.Name ?? "",
                    Length = item.Length,
                    Width = item.Width,
                    Thickness = item.Thickness,
                    PartType = item.IsIrregular ? "异形" : "矩形",
                    Reason = "超出当前大板规格",
                    Category = "数据预检"
                });
            }
        }

        return list;
    }

    private static void AppendSkippedReasonSummary(StringBuilder summary, ExtractionResult extraction)
    {
        if (summary == null || extraction?.SkippedParts == null || extraction.SkippedParts.Count == 0)
            return;

        var grouped = extraction.SkippedParts
            .Where(p => !string.IsNullOrWhiteSpace(p?.SkipReason))
            .GroupBy(p => p.SkipReason.Trim())
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (grouped.Count == 0)
            return;

        summary.Append("\n[跳过汇总]");
        foreach (var group in grouped)
        {
            summary.Append($"\n  · {group.Key}: {group.Count()} 件");
        }
    }

    private static void NormalizeResultReportData(NestingService.NestingResult result)
    {
        if (result == null)
            return;

        foreach (var sheet in result.Sheets.Concat(result.IrregularSheets).Where(s => s != null))
        {
            var firstItem = sheet.PlacedItems?
                .Select(p => p?.Item)
                .FirstOrDefault(i => i != null);

            if (string.IsNullOrWhiteSpace(sheet.Material))
                sheet.Material = firstItem?.Material ?? "";

            if (sheet.Thickness <= 0)
                sheet.Thickness = firstItem?.CuttingThickness > 0
                    ? firstItem.CuttingThickness
                    : firstItem?.Thickness ?? 0;

            if (string.IsNullOrWhiteSpace(sheet.SheetSpecLabel) && sheet.Width > 0 && sheet.Height > 0)
                sheet.SheetSpecLabel = $"{sheet.Width:F0}×{sheet.Height:F0}";
        }

        result.MaterialSummaries = BuildMaterialSummaries(result);

        double totalArea = result.Sheets.Sum(s => s?.TotalArea ?? 0) +
                           result.IrregularSheets.Sum(s => s?.TotalArea ?? 0);
        double usedArea = result.Sheets.Sum(s => s?.UsedArea ?? 0) +
                          result.IrregularSheets.Sum(s => s?.UsedArea ?? 0);
        result.TotalUtilization = totalArea > 0 ? usedArea / totalArea : 0;
    }

    private static List<NestingService.MaterialSummary> BuildMaterialSummaries(NestingService.NestingResult result)
    {
        var summaries = new Dictionary<string, NestingService.MaterialSummary>(StringComparer.OrdinalIgnoreCase);
        if (result == null)
            return new List<NestingService.MaterialSummary>();

        foreach (var sheet in result.Sheets.Concat(result.IrregularSheets).Where(s => s != null))
        {
            string material = sheet.Material ?? "";
            string spec = !string.IsNullOrWhiteSpace(sheet.SheetSpecLabel)
                ? sheet.SheetSpecLabel
                : (sheet.Width > 0 && sheet.Height > 0 ? $"{sheet.Width:F0}×{sheet.Height:F0}" : "");
            string key = $"{material}|{sheet.Thickness:F1}|{spec}";

            if (!summaries.TryGetValue(key, out var summary))
            {
                summary = new NestingService.MaterialSummary
                {
                    Material = material,
                    Thickness = sheet.Thickness,
                    SheetSpec = spec
                };
                summaries[key] = summary;
            }

            summary.SheetCount++;
            summary.PanelCount += sheet.PanelCount;
            summary.TotalArea += sheet.TotalArea;
            summary.UsedArea += sheet.UsedArea;
        }

        foreach (var summary in summaries.Values)
            summary.Utilization = summary.TotalArea > 0 ? summary.UsedArea / summary.TotalArea : 0;

        return summaries.Values
            .OrderBy(s => s.Material ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Thickness)
            .ThenBy(s => s.SheetSpec ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<NestingService.SheetSpec> BuildSheetSpecs(PaiBanWindow window)
    {
        var specs = new List<NestingService.SheetSpec>();
        if (window.SheetSpecs != null && window.SheetSpecs.Count > 0)
        {
            foreach (var spec in window.SheetSpecs)
            {
                if (spec.Width > 0 && spec.Height > 0)
                    specs.Add(new NestingService.SheetSpec { Width = spec.Width, Height = spec.Height });
            }
        }
        if (specs.Count == 0)
        {
            specs.Add(new NestingService.SheetSpec { Width = window.SheetWidth, Height = window.SheetHeight });
        }
        return specs;
    }

    private static List<NestingService.NestingItem> ConvertToNestingItems(List<SourcePart> parts)
    {
        return parts.Select(part => new NestingService.NestingItem
        {
            Name = part.Name,
            Material = part.Material,
            Length = part.CuttingLength,
            Width = part.CuttingWidth,
            Thickness = part.CuttingThickness,
            TextureDirection = part.TextureDirection,
            EdgeInfo = part.EdgeInfo,
            CabinetId = part.CabinetId,
            PanelNumber = part.PanelNumber,
            EntityHandle = part.HandleText,
            CuttingLength = part.CuttingLength,
            CuttingWidth = part.CuttingWidth,
            CuttingThickness = part.CuttingThickness,
            IsIrregular = part.LocalOutline != null && part.LocalOutline.Count > 0,
            Outline = part.LocalOutline,
            InnerOutlines = part.LocalInnerOutlines
        }).ToList();
    }

    private static bool TryShowConfigDialog(Editor editor, out PaiBanWindow window, HashSet<string> materials, bool isReconfig, PaiBanConfigSnapshot snapshot = null)
    {
        window = null;
        try
        {
            window = new PaiBanWindow(materials, snapshot);
            if (isReconfig)
                window.Title = "板件排样 (PAIBAN) — 重新配置大板规格";
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nPAIBAN 配置窗口初始化失败: {ex.Message}");
            return false;
        }

        try
        {
            bool? dialogResult = Application.ShowModalWindow(window);
            return dialogResult == true;
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nPAIBAN 配置窗口打开失败: {ex.Message}");
            return false;
        }
    }

    #region 板件提取

    private static ExtractionResult ExtractParts(Database db, IEnumerable<ObjectId> objectIds)
    {
        ExtractionResult result = new ExtractionResult();

        using Transaction tr = db.TransactionManager.StartTransaction();
        foreach (ObjectId objectId in objectIds ?? Enumerable.Empty<ObjectId>())
        {
            Entity entity = tr.GetObject(objectId, OpenMode.ForRead, false) as Entity;
            if (entity == null) continue;

            PanelInfo info = PanelInfoService.GetPanelInfo(entity, tr);

            if (info?.EntityKind == EntityKind.Hardware || info?.ExcludeFromPaiBan == true)
            {
                result.SkippedParts.Add(CreateSkippedPart(entity, info,
                    info?.EntityKind == EntityKind.Hardware
                        ? "五金实体(不参与排版)"
                        : "已标记为排版忽略"));
                continue;
            }

            if (info == null)
            {
                result.SkippedParts.Add(CreateSkippedPart(entity, info, "未找到板件信息"));
                continue;
            }

            if (info.EntityKind != EntityKind.Panel)
            {
                result.SkippedParts.Add(CreateSkippedPart(entity, info, "实体用途不是板件"));
                continue;
            }

            if (entity is not Solid3d)
            {
                result.SkippedParts.Add(CreateSkippedPart(entity, info, "仅支持板件三维实体参与排版"));
                continue;
            }

            if (!TryGetCuttingSize(info, out double cuttingLength, out double cuttingWidth, out double cuttingThickness))
            {
                result.SkippedParts.Add(CreateSkippedPart(entity, info, "缺少有效裁切尺寸"));
                continue;
            }

            string material = info?.Material ?? "";
            if (!string.IsNullOrWhiteSpace(material))
                result.AllMaterials.Add(material.Trim());

            // ★ 圆弧板件：展开后是矩形，走矩形排版路径，使用展开尺寸
            if (info?.IsArcPanel == true ||
                info?.CalculationType == PanelCalculationType.ArcPanel ||
                info?.CalculationType == PanelCalculationType.SplinePanel)
            {
                SourcePart arcPart = BuildBasicPart(entity, info);
                ApplyCuttingSize(arcPart, cuttingLength, cuttingWidth, cuttingThickness);
                // 圆弧板件展开后为矩形，不需要异形轮廓
                result.RectangularParts.Add(arcPart);
                continue;
            }

            // ★ 其他异形板件：BZBJ 摆正(克隆体) → FLATSHOT 投影 → 闭合多段线
            if (LooksIrregular(entity, info))
            {
                SourcePart irregularPart = BuildBasicPart(entity, info);
                ApplyCuttingSize(irregularPart, cuttingLength, cuttingWidth, cuttingThickness);
                irregularPart.SkipReason = "异形件";

                var profile = OutlineService.ExtractOutlineProfile(entity, cuttingLength, cuttingWidth);
                irregularPart.LocalOutline = profile.Outer;
                irregularPart.LocalInnerOutlines = profile.Inners;

                if (entity is Solid3d)
                {
                    result.FlatshotAttempts++;
                    if (profile.FlatshotSuccess) result.FlatshotSuccesses++;
                    result.InnerHolesDetected += profile.Inners?.Count ?? 0;
                    if (profile.NormalAngleToZDeg > result.MaxNormalAngleDeg)
                        result.MaxNormalAngleDeg = profile.NormalAngleToZDeg;
                    if (profile.EdgeChainRate > 0 && profile.EdgeChainRate < result.MinEdgeChainRate)
                        result.MinEdgeChainRate = profile.EdgeChainRate;
                    if (!profile.FlatshotSuccess && !string.IsNullOrWhiteSpace(profile.DiagnosticMessage))
                        result.ExtractionDiagnostics.Add($"{irregularPart.Name}: {profile.DiagnosticMessage}");
                }

                result.IrregularParts.Add(irregularPart);
                continue;
            }

            if (TryBuildRectangularPart(entity, info, out SourcePart rectangularPart))
            {
                ApplyCuttingSize(rectangularPart, cuttingLength, cuttingWidth, cuttingThickness);
                result.RectangularParts.Add(rectangularPart);
                continue;
            }

            if (entity is Solid3d)
            {
                SourcePart catchAll = BuildBasicPart(entity, info);
                ApplyCuttingSize(catchAll, cuttingLength, cuttingWidth, cuttingThickness);
                catchAll.SkipReason = "异形件(几何检测)";

                var profile = OutlineService.ExtractOutlineProfile(entity, cuttingLength, cuttingWidth);
                catchAll.LocalOutline = profile.Outer;
                catchAll.LocalInnerOutlines = profile.Inners;

                if (entity is Solid3d)
                {
                    result.FlatshotAttempts++;
                    if (profile.FlatshotSuccess) result.FlatshotSuccesses++;
                    result.InnerHolesDetected += profile.Inners?.Count ?? 0;
                    if (profile.NormalAngleToZDeg > result.MaxNormalAngleDeg)
                        result.MaxNormalAngleDeg = profile.NormalAngleToZDeg;
                    if (profile.EdgeChainRate > 0 && profile.EdgeChainRate < result.MinEdgeChainRate)
                        result.MinEdgeChainRate = profile.EdgeChainRate;
                }

                result.IrregularParts.Add(catchAll);
                continue;
            }

            result.SkippedParts.Add(CreateSkippedPart(entity, info, "缺少有效尺寸或未识别为矩形板件"));
        }

        tr.Commit();
        return result;
    }

    #endregion

    #region 板件分类辅助

    private static bool TryBuildRectangularPart(Entity entity, PanelInfo info, out SourcePart part)
    {
        part = null;
        if (entity == null || entity is not Solid3d || info == null) return false;
        if (!TryGetCuttingSize(info, out double length, out double width, out double thickness))
            return false;

        part = BuildBasicPart(entity, info);
        ApplyCuttingSize(part, length, width, thickness);
        return true;
    }

    private static SourcePart BuildBasicPart(Entity entity, PanelInfo info)
    {
        return new SourcePart
        {
            Name = BuildPartName(entity, info),
            HandleText = entity?.Handle.ToString() ?? "",
            Material = info?.Material ?? "",
            Length = info?.Length ?? 0,
            Width = info?.Width ?? 0,
            Thickness = info?.Height ?? 0,
            TextureDirection = info?.TextureDirection ?? TextureDirection.AlongLength,
            EdgeInfo = BuildEdgeInfo(info),
            CabinetId = info?.CabinetId ?? "",
            PanelNumber = info?.OrderId ?? "",
            CuttingLength = info?.ExtraLength ?? 0,
            CuttingWidth = info?.ExtraWidth ?? 0,
            CuttingThickness = info?.ExtraHeight ?? 0
        };
    }

    private static SourcePart CreateSkippedPart(Entity entity, PanelInfo info, string reason)
    {
        SourcePart part = BuildBasicPart(entity, info);
        part.SkipReason = reason ?? "";
        return part;
    }

    private static bool TryGetCuttingSize(PanelInfo info, out double length, out double width, out double thickness)
    {
        length = 0;
        width = 0;
        thickness = 0;
        if (info == null)
            return false;

        double rawL = info.ExtraLength;
        double rawW = info.ExtraWidth;
        double rawT = info.ExtraHeight;
        if (rawL <= 0 || rawW <= 0 || rawT <= 0)
            return false;

        length = Math.Max(rawL, rawW);
        width = Math.Min(rawL, rawW);
        thickness = rawT;
        return true;
    }

    private static void ApplyCuttingSize(SourcePart part, double length, double width, double thickness)
    {
        if (part == null)
            return;

        part.Length = length;
        part.Width = width;
        part.Thickness = thickness;
        part.CuttingLength = length;
        part.CuttingWidth = width;
        part.CuttingThickness = thickness;
    }

    private static string BuildPartName(Entity entity, PanelInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info?.PanelName))
            return info.PanelName;
        return entity == null ? "未命名板件" : $"板件_{entity.Handle}";
    }

    private static string BuildEdgeInfo(PanelInfo info)
    {
        if (info == null) return "";
        List<string> segments = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.EdgeTop)) segments.Add($"上:{info.EdgeTop}");
        if (!string.IsNullOrWhiteSpace(info.EdgeBottom)) segments.Add($"下:{info.EdgeBottom}");
        if (!string.IsNullOrWhiteSpace(info.EdgeLeft)) segments.Add($"左:{info.EdgeLeft}");
        if (!string.IsNullOrWhiteSpace(info.EdgeRight)) segments.Add($"右:{info.EdgeRight}");
        if (segments.Count > 0) return string.Join(" ", segments);
        return info.EdgeBanding ?? "";
    }

    private static bool TryGetBoundingBox(Entity entity, out double length, out double width)
    {
        length = 0; width = 0;
        if (entity == null) return false;
        try
        {
            Extents3d extents = entity.GeometricExtents;
            double dx = extents.MaxPoint.X - extents.MinPoint.X;
            double dy = extents.MaxPoint.Y - extents.MinPoint.Y;
            if (dx > 0 && dy > 0)
            {
                length = Math.Max(dx, dy);
                width = Math.Min(dx, dy);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool LooksIrregular(Entity entity, PanelInfo info)
    {
        if (entity is Circle || entity is Ellipse || entity is Spline || entity is Region) return true;
        if (entity is Polyline polyline)
        {
            for (int i = 0; i < polyline.NumberOfVertices; i++)
            {
                if (Math.Abs(polyline.GetBulgeAt(i)) > 1e-9) return true;
            }
        }

        // ★ Solid3d异形检测：体积与包围盒体积比 < 88% 则为异形
        if (entity is Solid3d solid)
        {
            try
            {
                // 先做拓扑复杂度识别：存在圆弧/样条/多闭环时视为异形（可覆盖内部造型场景）
                if (HasComplexSolidProfiles(solid))
                    return true;

                double vol = solid.MassProperties.Volume;
                Extents3d ext = solid.GeometricExtents;
                double bboxVol = (ext.MaxPoint.X - ext.MinPoint.X)
                               * (ext.MaxPoint.Y - ext.MinPoint.Y)
                               * (ext.MaxPoint.Z - ext.MinPoint.Z);
                if (bboxVol > 0 && vol / bboxVol < 0.88)
                    return true;

                // 轮廓复杂度兜底：若提取出的2D轮廓不是标准四边矩形，也按异形处理
                double bl = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, ext.MaxPoint.Y - ext.MinPoint.Y);
                double bw = Math.Min(ext.MaxPoint.X - ext.MinPoint.X, ext.MaxPoint.Y - ext.MinPoint.Y);
                var outline = OutlineService.ExtractOutline(solid, bl, bw);
                if (outline != null && outline.Count >= 5)
                    return true;
            }
            catch { }
        }

        return false;
    }

    private static bool HasComplexSolidProfiles(Solid3d solid)
    {
        if (solid == null) return false;
        DBObjectCollection exploded = null;
        try
        {
            exploded = new DBObjectCollection();
            solid.Explode(exploded);
            int closedLoopCount = 0;
            foreach (DBObject obj in exploded)
            {
                if (obj is Arc || obj is Circle || obj is Ellipse || obj is Spline)
                    return true;

                if (obj is Polyline pl)
                {
                    bool hasBulge = false;
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                    {
                        if (Math.Abs(pl.GetBulgeAt(i)) > 1e-9)
                        {
                            hasBulge = true;
                            break;
                        }
                    }
                    if (hasBulge) return true;
                    if (pl.Closed) closedLoopCount++;
                }
                else if (obj is Region)
                {
                    closedLoopCount++;
                }
            }

            // 多闭环通常意味着内孔/内造型
            return closedLoopCount >= 2;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (exploded != null)
            {
                foreach (DBObject obj in exploded)
                {
                    try { obj?.Dispose(); } catch { }
                }
            }
        }
    }

    private static bool TryGetRectangleSizeFromPolyline(Entity entity, out double length, out double width)
    {
        length = 0; width = 0;
        Polyline polyline = entity as Polyline;
        if (polyline == null || !polyline.Closed || polyline.NumberOfVertices != 4)
            return false;

        List<Point2d> vertices = new List<Point2d>();
        for (int i = 0; i < polyline.NumberOfVertices; i++)
        {
            if (Math.Abs(polyline.GetBulgeAt(i)) > 1e-9) return false;
            vertices.Add(polyline.GetPoint2dAt(i));
        }

        List<double> edgeLengths = new List<double>();
        for (int i = 0; i < vertices.Count; i++)
        {
            Point2d current = vertices[i];
            Point2d next = vertices[(i + 1) % vertices.Count];
            Vector2d edge = next - current;
            if (edge.Length <= 1e-6) return false;
            Point2d nextNext = vertices[(i + 2) % vertices.Count];
            Vector2d nextEdge = nextNext - next;
            if (Math.Abs(edge.DotProduct(nextEdge)) > 1e-4) return false;
            edgeLengths.Add(edge.Length);
        }

        length = Math.Max(edgeLengths[0], edgeLengths[1]);
        width = Math.Min(edgeLengths[0], edgeLengths[1]);
        return length > 0 && width > 0;
    }

    #endregion

    #region 绘制排版结果

    private static void DrawResult(
        Transaction tr, BlockTableRecord modelSpace, Point3d insertPoint,
        NestingService.NestingConfig config, NestingService.NestingResult result,
        ExtractionResult extraction, List<(int SheetIndex, RemnantService.RemnantArea Remnant)> remnants)
    {
        double sheetGap = Math.Max(100.0, (config.SheetWidth > 0 ? config.SheetWidth : 2440) * 0.1);
        double currentX = insertPoint.X;
        int paibanBlockIndex = GetNextPaibanBlockIndex(tr, modelSpace.Database);

        var sheetPositions = new Dictionary<int, Point3d>();

        for (int sheetIndex = 0; sheetIndex < result.Sheets.Count; sheetIndex++)
        {
            sheetPositions[result.Sheets[sheetIndex].SheetIndex] = new Point3d(currentX, insertPoint.Y, 0);
            DrawSingleSheet(tr, modelSpace, ref currentX, insertPoint.Y, sheetIndex, ref paibanBlockIndex,
                result.Sheets[sheetIndex], sheetGap);
        }

        for (int sheetIndex = 0; sheetIndex < result.IrregularSheets.Count; sheetIndex++)
        {
            sheetPositions[result.IrregularSheets[sheetIndex].SheetIndex] = new Point3d(currentX, insertPoint.Y, 0);
            DrawSingleSheet(tr, modelSpace, ref currentX, insertPoint.Y,
                result.Sheets.Count + sheetIndex, ref paibanBlockIndex, result.IrregularSheets[sheetIndex], sheetGap, isIrregular: true);
        }

        // （已下线）余料区标注绘制：DrawRemnants 不再调用
        double reportY = insertPoint.Y - 60;
        DrawReportTable(tr, modelSpace, new Point3d(insertPoint.X, reportY, insertPoint.Z), result, extraction);
    }

    /// <summary>绘制余料区域标注（绿色虚线矩形+尺寸文字）</summary>
    private static void DrawRemnants(Transaction tr, BlockTableRecord modelSpace,
        NestingService.NestingResult result,
        List<(int SheetIndex, RemnantService.RemnantArea Remnant)> remnants,
        Dictionary<int, Point3d> sheetPositions, Point3d insertPoint)
    {
        foreach (var (sheetIdx, remnant) in remnants)
        {
            if (!sheetPositions.TryGetValue(sheetIdx, out var sheetOrigin))
            {
                var allSheets = result.Sheets.Concat(result.IrregularSheets).ToList();
                int seqIdx = allSheets.FindIndex(s => s.SheetIndex == sheetIdx);
                if (seqIdx < 0) continue;
                sheetOrigin = insertPoint;
            }

            double rx = sheetOrigin.X + remnant.X;
            double ry = sheetOrigin.Y + remnant.Y;

            Polyline pline = new Polyline();
            pline.AddVertexAt(0, new Point2d(rx, ry), 0, 0, 0);
            pline.AddVertexAt(1, new Point2d(rx + remnant.Width, ry), 0, 0, 0);
            pline.AddVertexAt(2, new Point2d(rx + remnant.Width, ry + remnant.Height), 0, 0, 0);
            pline.AddVertexAt(3, new Point2d(rx, ry + remnant.Height), 0, 0, 0);
            pline.Closed = true;
            pline.Color = Color.FromColorIndex(ColorMethod.ByAci, 3); // 绿色
            pline.LinetypeScale = 10;
            modelSpace.AppendEntity(pline);
            tr.AddNewlyCreatedDBObject(pline, true);

            string label = $"余料区\\P{remnant.Width:F0}×{remnant.Height:F0}mm";
            double textH = Math.Max(10, Math.Min(20, Math.Min(remnant.Width, remnant.Height) * 0.12));
            AddText(tr, modelSpace, label,
                new Point3d(rx + remnant.Width / 2, ry + remnant.Height / 2, 0),
                textH, AttachmentPoint.MiddleCenter, 3);
        }
    }

    private static void DrawSingleSheet(
        Transaction tr, BlockTableRecord modelSpace,
        ref double currentX, double baseY, int displayIndex, ref int paibanBlockIndex,
        NestingService.NestingSheet sheet, double sheetGap, bool isIrregular = false)
    {
        Point3d sheetOrigin = new Point3d(currentX, baseY, 0);

        AddSheetOutline(tr, modelSpace, sheetOrigin, sheet.Width, sheet.Height);

        string headerText = $"板{displayIndex + 1}  {sheet.Material}  {sheet.SheetSpecLabel}  " +
                           $"厚{sheet.Thickness:F0}mm  {sheet.PanelCount}件  " +
                           $"利用率{sheet.Utilization * 100:F1}%" +
                           (isIrregular ? " [异形]" : "");
        double headerHeight = Math.Max(16.0, Math.Min(28.0, sheet.Height * 0.035));
        AddText(tr, modelSpace, headerText,
            new Point3d(sheetOrigin.X + 5, sheetOrigin.Y + sheet.Height + headerHeight + 3, 0),
            headerHeight, AttachmentPoint.BottomLeft);

        foreach (var placed in sheet.PlacedItems ?? Enumerable.Empty<NestingService.PlacedItem>())
        {
            if (placed?.Item == null) continue;

            double partWidth = placed.IsRotated ? placed.Item.Width : placed.Item.Length;
            double partHeight = placed.IsRotated ? placed.Item.Length : placed.Item.Width;
            Point3d partOrigin = new Point3d(sheetOrigin.X + placed.X, sheetOrigin.Y + placed.Y, 0);

            short colorIndex = ResolveColorIndex(placed.Item.Material);

            // 板件轮廓统一写入块，避免线条散落。
            AddPanelAsBlock(tr, modelSpace, partOrigin, partWidth, partHeight, colorIndex,
                placed.Item.IsIrregular, placed.Item.Outline, placed.Item.InnerOutlines, placed.IsRotated, ref paibanBlockIndex);

            double textHeight = Math.Max(10.0, Math.Min(22.0, Math.Min(partWidth, partHeight) * 0.15));
            string label = BuildPanelLabel(placed.Item);
            AddText(tr, modelSpace, label,
                new Point3d(partOrigin.X + partWidth * 0.5, partOrigin.Y + partHeight * 0.5, 0),
                textHeight, AttachmentPoint.MiddleCenter);
        }

        currentX += sheet.Width + sheetGap;
    }

    private static int GetNextPaibanBlockIndex(Transaction tr, Database db)
    {
        if (tr == null || db == null) return 0;
        int maxIdx = -1;
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in bt)
        {
            var btr = tr.GetObject(id, OpenMode.ForRead, false) as BlockTableRecord;
            if (btr == null || string.IsNullOrWhiteSpace(btr.Name)) continue;
            if (!btr.Name.StartsWith("paiban-", StringComparison.OrdinalIgnoreCase)) continue;
            string suffix = btr.Name.Substring("paiban-".Length);
            if (int.TryParse(suffix, out int idx) && idx > maxIdx)
                maxIdx = idx;
        }
        return maxIdx + 1;
    }

    private static string CreatePaibanBlockName(Transaction tr, Database db, ref int index)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        while (true)
        {
            string name = $"paiban-{index}";
            index++;
            if (!bt.Has(name)) return name;
        }
    }

    private static void AddPanelAsBlock(
        Transaction tr, BlockTableRecord modelSpace, Point3d origin,
        double placedW, double placedH, short colorIndex, bool isIrregular,
        List<Pt2d> localOutline, List<List<Pt2d>> localInnerOutlines, bool isRotated,
        ref int paibanBlockIndex)
    {
        var db = modelSpace.Database;
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
        string blockName = CreatePaibanBlockName(tr, db, ref paibanBlockIndex);

        var def = new BlockTableRecord
        {
            Name = blockName,
            Origin = Point3d.Origin
        };
        bt.Add(def);
        tr.AddNewlyCreatedDBObject(def, true);

        if (isIrregular && localOutline != null && localOutline.Count > 2)
        {
            var outer = BuildOutlinePolyline(localOutline, placedW, isRotated, colorIndex);
            if (outer != null)
            {
                def.AppendEntity(outer);
                tr.AddNewlyCreatedDBObject(outer, true);
            }

            foreach (var inner in localInnerOutlines ?? Enumerable.Empty<List<Pt2d>>())
            {
                var hole = BuildOutlinePolyline(inner, placedW, isRotated, colorIndex);
                if (hole == null) continue;
                def.AppendEntity(hole);
                tr.AddNewlyCreatedDBObject(hole, true);
            }
        }
        else
        {
            Polyline polyline = new Polyline();
            polyline.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
            polyline.AddVertexAt(1, new Point2d(placedW, 0), 0, 0, 0);
            polyline.AddVertexAt(2, new Point2d(placedW, placedH), 0, 0, 0);
            polyline.AddVertexAt(3, new Point2d(0, placedH), 0, 0, 0);
            polyline.Closed = true;
            polyline.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
            def.AppendEntity(polyline);
            tr.AddNewlyCreatedDBObject(polyline, true);
        }

        var bref = new BlockReference(origin, def.ObjectId);
        modelSpace.AppendEntity(bref);
        tr.AddNewlyCreatedDBObject(bref, true);
    }

    private static Polyline BuildOutlinePolyline(List<Pt2d> loop, double placedW, bool isRotated, short colorIndex)
    {
        if (loop == null || loop.Count < 3) return null;
        Polyline pline = new Polyline();
        for (int i = 0; i < loop.Count; i++)
        {
            double x = loop[i].X;
            double y = loop[i].Y;
            if (isRotated)
            {
                double tmp = x;
                x = placedW - y;
                y = tmp;
            }
            pline.AddVertexAt(i, new Point2d(x, y), 0, 0, 0);
        }
        pline.Closed = true;
        pline.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
        return pline;
    }

    private static void DrawIrregularOutline(Transaction tr, BlockTableRecord modelSpace,
        Point3d origin, List<Pt2d> localOutline, List<List<Pt2d>> localInnerOutlines, double placedW, double placedH,
        bool isRotated, short colorIndex)
    {
        if (localOutline == null || localOutline.Count < 3) return;

        Polyline pline = new Polyline();
        for (int i = 0; i < localOutline.Count; i++)
        {
            double x = localOutline[i].X;
            double y = localOutline[i].Y;

            // 旋转90°：必须与 NestingService.RotateMask 一致
            // RotateMask: (x,y) → (H-y, x)，其中H=原始轮廓高度=placedW(旋转后宽=原始高)
            if (isRotated)
            {
                double tmp = x;
                x = placedW - y;
                y = tmp;
            }

            pline.AddVertexAt(i, new Point2d(origin.X + x, origin.Y + y), 0, 0, 0);
        }
        pline.Closed = true;
        pline.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
        modelSpace.AppendEntity(pline);
        tr.AddNewlyCreatedDBObject(pline, true);

        foreach (var inner in localInnerOutlines ?? Enumerable.Empty<List<Pt2d>>())
        {
            if (inner == null || inner.Count < 3) continue;

            Polyline hole = new Polyline();
            for (int i = 0; i < inner.Count; i++)
            {
                double x = inner[i].X;
                double y = inner[i].Y;
                if (isRotated)
                {
                    double tmp = x;
                    x = placedW - y;
                    y = tmp;
                }
                hole.AddVertexAt(i, new Point2d(origin.X + x, origin.Y + y), 0, 0, 0);
            }
            hole.Closed = true;
            hole.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
            modelSpace.AppendEntity(hole);
            tr.AddNewlyCreatedDBObject(hole, true);
        }
    }

    private static string BuildPanelLabel(NestingService.NestingItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Name)) parts.Add($"名称:{item.Name}");
        if (!string.IsNullOrWhiteSpace(item.CabinetId)) parts.Add($"柜号:{item.CabinetId}");
        if (!string.IsNullOrWhiteSpace(item.PanelNumber)) parts.Add($"订单号:{item.PanelNumber}");
        if (!string.IsNullOrWhiteSpace(item.EntityHandle)) parts.Add($"板件ID:{item.EntityHandle}");

        double cl = item.CuttingLength > 0 ? item.CuttingLength : item.Length;
        double cw = item.CuttingWidth > 0 ? item.CuttingWidth : item.Width;
        double ct = item.CuttingThickness > 0 ? item.CuttingThickness : item.Thickness;
        parts.Add($"裁切尺寸:{cl:F0}×{cw:F0}×{ct:F0}");

        return string.Join("\\P", parts);
    }

    #endregion

    #region 报表

    private static void DrawReportTable(Transaction tr, BlockTableRecord modelSpace,
        Point3d insertPoint, NestingService.NestingResult result, ExtractionResult extraction)
    {
        double rowHeight = 32;
        string[] headers = { "序号", "材质", "大板规格", "厚度(mm)", "大板张数", "板件数量", "总面积(m²)", "已用面积(m²)", "利用率" };

        double[] colWidths = CalculateAdaptiveColumnWidths(result, headers);
        double totalWidth = colWidths.Sum();
        double y = insertPoint.Y;
        double x = insertPoint.X + 20;

        AddText(tr, modelSpace, "排版报表", new Point3d(x + totalWidth / 2, y, 0), 28, AttachmentPoint.MiddleCenter);
        y -= rowHeight + 15;

        double headerX = x;
        for (int i = 0; i < headers.Length; i++)
        {
            DrawTableCell(tr, modelSpace, headerX, y, colWidths[i], rowHeight, headers[i], bgColorIndex: 8);
            headerX += colWidths[i];
        }
        y -= rowHeight;

        int seq = 1;
        foreach (var summary in result.MaterialSummaries ?? Enumerable.Empty<NestingService.MaterialSummary>())
        {
            if (summary == null) continue;
            DrawSummaryRow(tr, modelSpace, x, ref y, colWidths, rowHeight, seq++, summary);
        }

        y -= 8;

        int totalSheets = result.Sheets.Count + result.IrregularSheets.Count;
        int totalPlaced = PlacedCount(result);
        double totalAreaAll = result.Sheets.Sum(s => s.TotalArea) + result.IrregularSheets.Sum(s => s.TotalArea);
        double usedAreaAll = result.Sheets.Sum(s => s.UsedArea) + result.IrregularSheets.Sum(s => s.UsedArea);

        string[] totals = {
            "", "合计", "", "",
            totalSheets.ToString(), totalPlaced.ToString(),
            (totalAreaAll / 1_000_000).ToString("F3"),
            (usedAreaAll / 1_000_000).ToString("F3"),
            $"{(totalAreaAll > 0 ? usedAreaAll / totalAreaAll * 100 : 0):F1}%"
        };
        DrawTotalRowString(tr, modelSpace, x, y, colWidths, rowHeight, totals);

        if (result.IrregularSheets.Count > 0 || extraction.IrregularParts.Count > 0)
        {
            y -= rowHeight + 8;
            AddText(tr, modelSpace,
                $"异形件: 排版 {result.IrregularSheets.Sum(s => s.PlacedItems?.Count ?? 0)} 件 / 总共 {extraction.IrregularParts.Count} 件",
                new Point3d(x, y + rowHeight / 2, 0), 18, AttachmentPoint.MiddleLeft);
        }
    }

    private static double[] CalculateAdaptiveColumnWidths(NestingService.NestingResult result, string[] headers)
    {
        double[] baseWidths = { 40, 70, 90, 55, 70, 65, 90, 90, 60 };
        double charWidth = 11.0;

        var rows = new List<string[]>();
        foreach (var summary in result.MaterialSummaries ?? Enumerable.Empty<NestingService.MaterialSummary>())
        {
            rows.Add(new[] {
                "", summary.Material ?? "", summary.SheetSpec ?? "",
                summary.Thickness > 0 ? summary.Thickness.ToString("F0") : "",
                summary.SheetCount.ToString(), summary.PanelCount.ToString(),
                (summary.TotalArea / 1_000_000).ToString("F3"),
                (summary.UsedArea / 1_000_000).ToString("F3"),
                $"{summary.Utilization * 100:F1}%"
            });
        }

        double[] resultWidths = new double[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            double headerW = headers[i].Length * charWidth + 16;
            double dataW = headerW;
            foreach (var row in rows)
            {
                string cell = i < row.Length ? row[i] : "";
                double w = (cell?.Length ?? 0) * charWidth + 16;
                if (w > dataW) dataW = w;
            }
            resultWidths[i] = Math.Max(baseWidths[i], Math.Max(headerW, dataW));
        }

        return resultWidths;
    }

    private static void DrawSummaryRow(Transaction tr, BlockTableRecord modelSpace,
        double x, ref double y, double[] colWidths, double rowHeight, int seq,
        NestingService.MaterialSummary summary)
    {
        string[] values = {
            seq.ToString(), summary.Material ?? "", summary.SheetSpec ?? "",
            summary.Thickness > 0 ? summary.Thickness.ToString("F0") : "",
            summary.SheetCount.ToString(), summary.PanelCount.ToString(),
            (summary.TotalArea / 1_000_000).ToString("F3"),
            (summary.UsedArea / 1_000_000).ToString("F3"),
            $"{summary.Utilization * 100:F1}%"
        };

        double cellX = x;
        for (int i = 0; i < values.Length; i++)
        {
            DrawTableCell(tr, modelSpace, cellX, y, colWidths[i], rowHeight, values[i]);
            cellX += colWidths[i];
        }
        y -= rowHeight;
    }

    private static void DrawTotalRowString(Transaction tr, BlockTableRecord modelSpace,
        double x, double y, double[] colWidths, double rowHeight, string[] values)
    {
        double cellX = x;
        for (int i = 0; i < values.Length; i++)
        {
            DrawTableCell(tr, modelSpace, cellX, y, colWidths[i], rowHeight, values[i], bgColorIndex: 8);
            cellX += colWidths[i];
        }
    }

    private static void DrawTableCell(Transaction tr, BlockTableRecord modelSpace,
        double x, double y, double width, double height, string text,
        int bgColorIndex = -1, short textColorIndex = 7, bool drawBorder = true)
    {
        if (bgColorIndex > 0)
        {
            Polyline bg = new Polyline();
            bg.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
            bg.AddVertexAt(1, new Point2d(x + width, y), 0, 0, 0);
            bg.AddVertexAt(2, new Point2d(x + width, y - height), 0, 0, 0);
            bg.AddVertexAt(3, new Point2d(x, y - height), 0, 0, 0);
            bg.Closed = true;
            bg.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)bgColorIndex);
            bg.ConstantWidth = 1;
            modelSpace.AppendEntity(bg);
            tr.AddNewlyCreatedDBObject(bg, true);
        }

        if (drawBorder)
        {
            Polyline border = new Polyline();
            border.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
            border.AddVertexAt(1, new Point2d(x + width, y), 0, 0, 0);
            border.AddVertexAt(2, new Point2d(x + width, y - height), 0, 0, 0);
            border.AddVertexAt(3, new Point2d(x, y - height), 0, 0, 0);
            border.Closed = true;
            border.Color = Color.FromColorIndex(ColorMethod.ByAci, 252);
            modelSpace.AppendEntity(border);
            tr.AddNewlyCreatedDBObject(border, true);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            AddText(tr, modelSpace, text,
                new Point3d(x + width / 2, y - height / 2, 0),
                Math.Min(height * 0.4, 16),
                AttachmentPoint.MiddleCenter);
        }
    }

    #endregion

    #region 基础绘制工具

    private static void AddSheetOutline(Transaction tr, BlockTableRecord modelSpace,
        Point3d origin, double width, double height)
    {
        Polyline polyline = new Polyline();
        polyline.AddVertexAt(0, new Point2d(origin.X, origin.Y), 0, 0, 0);
        polyline.AddVertexAt(1, new Point2d(origin.X + width, origin.Y), 0, 0, 0);
        polyline.AddVertexAt(2, new Point2d(origin.X + width, origin.Y + height), 0, 0, 0);
        polyline.AddVertexAt(3, new Point2d(origin.X, origin.Y + height), 0, 0, 0);
        polyline.Closed = true;
        polyline.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
        modelSpace.AppendEntity(polyline);
        tr.AddNewlyCreatedDBObject(polyline, true);
    }

    private static void AddPanelRectangle(Transaction tr, BlockTableRecord modelSpace,
        Point3d origin, double width, double height, short colorIndex)
    {
        Polyline polyline = new Polyline();
        polyline.AddVertexAt(0, new Point2d(origin.X, origin.Y), 0, 0, 0);
        polyline.AddVertexAt(1, new Point2d(origin.X + width, origin.Y), 0, 0, 0);
        polyline.AddVertexAt(2, new Point2d(origin.X + width, origin.Y + height), 0, 0, 0);
        polyline.AddVertexAt(3, new Point2d(origin.X, origin.Y + height), 0, 0, 0);
        polyline.Closed = true;
        polyline.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
        modelSpace.AppendEntity(polyline);
        tr.AddNewlyCreatedDBObject(polyline, true);
    }

    private static void AddText(Transaction tr, BlockTableRecord modelSpace,
        string content, Point3d location, double textHeight,
        AttachmentPoint attachment = AttachmentPoint.BottomLeft, short colorIndex = 7)
    {
        MText text = new MText
        {
            Contents = content,
            TextHeight = textHeight,
            Location = location,
            Attachment = attachment,
            Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex)
        };
        modelSpace.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
    }

    private static short ResolveColorIndex(string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return 3;
        int hash = Math.Abs(material.GetHashCode());
        short[] palette = { 1, 2, 3, 4, 5, 6, 30, 40, 120, 140, 160, 180 };
        return palette[hash % palette.Length];
    }

    #endregion

    private static int PlacedCount(NestingService.NestingResult result)
    {
        int regular = result.Sheets.Sum(sheet => sheet.PlacedItems?.Count ?? 0);
        int irregular = result.IrregularSheets.Sum(sheet => sheet.PlacedItems?.Count ?? 0);
        return regular + irregular;
    }

    private static List<UnplaceableItem> BuildUnplaceableList(NestingService.NestingResult result)
    {
        var list = new List<UnplaceableItem>();
        foreach (var item in (result.UnplacedItems ?? Enumerable.Empty<NestingService.NestingItem>())
            .Concat(result.UnplacedIrregularItems ?? Enumerable.Empty<NestingService.NestingItem>()))
        {
            if (item == null) continue;
            list.Add(new UnplaceableItem
            {
                Handle = item.EntityHandle ?? "",
                OrderId = item.PanelNumber ?? "",
                CabinetId = item.CabinetId ?? "",
                Material = item.Material ?? "",
                Name = item.Name ?? "",
                Length = item.Length,
                Width = item.Width,
                Thickness = item.Thickness,
                    PartType = item.IsIrregular ? "异形" : "矩形",
                    Reason = "尺寸超出大板或张数限制",
                    Category = "排不进板件"
            });
        }
        return list;
    }

    private static void ComputeStatsOnlyPlaced(NestingService.NestingResult result)
    {
        double totalArea = result.Sheets.Sum(s => s.TotalArea) +
                           result.IrregularSheets.Sum(s => s.TotalArea);
        double usedArea = result.Sheets.Sum(s => s.UsedArea) +
                          result.IrregularSheets.Sum(s => s.UsedArea);
        result.TotalUtilization = totalArea > 0 ? usedArea / totalArea : 0;
    }

    #region CSV导出

    private static string ExportCsv(NestingService.NestingResult result, ExtractionResult extraction)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string filePath = Path.Combine(desktop, $"PAIBAN_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        StringBuilder csv = new StringBuilder();
        NormalizeResultReportData(result);
        csv.AppendLine("分类,大板序号,材质,大板规格,厚度,板件名称,柜号,板件编号,板件ID,裁切长,裁切宽,裁切厚,排版长,排版宽,X,Y,旋转,封边,备注");

        for (int i = 0; i < result.Sheets.Count; i++)
        {
            var sheet = result.Sheets[i];
            foreach (var placed in sheet.PlacedItems ?? Enumerable.Empty<NestingService.PlacedItem>())
            {
                if (placed?.Item == null) continue;
                csv.AppendLine(
                    $"矩形已排版,{i + 1},\"{sheet.Material}\",\"{sheet.SheetSpecLabel}\",{sheet.Thickness:F1}," +
                    $"\"{placed.Item.Name}\",\"{placed.Item.CabinetId}\",\"{placed.Item.PanelNumber}\",\"{placed.Item.EntityHandle}\"," +
                    $"{placed.Item.CuttingLength:F1},{placed.Item.CuttingWidth:F1},{placed.Item.CuttingThickness:F1}," +
                    $"{placed.Item.Length:F1},{placed.Item.Width:F1}," +
                    $"{placed.X:F1},{placed.Y:F1},{(placed.IsRotated ? "是" : "否")}," +
                    $"\"{placed.Item.EdgeInfo}\",");
            }
        }

        for (int i = 0; i < result.IrregularSheets.Count; i++)
        {
            var sheet = result.IrregularSheets[i];
            foreach (var placed in sheet.PlacedItems ?? Enumerable.Empty<NestingService.PlacedItem>())
            {
                if (placed?.Item == null) continue;
                csv.AppendLine(
                    $"异形已排版,{i + 1},\"{sheet.Material}\",\"{sheet.SheetSpecLabel}\",{sheet.Thickness:F1}," +
                    $"\"{placed.Item.Name}\",\"{placed.Item.CabinetId}\",\"{placed.Item.PanelNumber}\",\"{placed.Item.EntityHandle}\"," +
                    $"{placed.Item.CuttingLength:F1},{placed.Item.CuttingWidth:F1},{placed.Item.CuttingThickness:F1}," +
                    $"{placed.Item.Length:F1},{placed.Item.Width:F1}," +
                    $"{placed.X:F1},{placed.Y:F1},{(placed.IsRotated ? "是" : "否")}," +
                    $"\"{placed.Item.EdgeInfo}\",异形件");
            }
        }

        foreach (var item in (result.UnplacedItems ?? Enumerable.Empty<NestingService.NestingItem>())
            .Concat(result.UnplacedIrregularItems ?? Enumerable.Empty<NestingService.NestingItem>()))
        {
            if (item == null) continue;
            csv.AppendLine(
                $"未排入,,\"{item.Material}\",\"\",{item.Thickness:F1}," +
                $"\"{item.Name}\",\"{item.CabinetId}\",\"{item.PanelNumber}\",\"{item.EntityHandle}\"," +
                $"{item.CuttingLength:F1},{item.CuttingWidth:F1},{item.CuttingThickness:F1}," +
                $"{item.Length:F1},{item.Width:F1},,,,\"尺寸超出大板或张数限制\"");
        }

        foreach (var part in extraction.SkippedParts)
        {
            csv.AppendLine(
                $"跳过,,\"{part.Material}\",\"\",{part.Thickness:F1}," +
                $"\"{part.Name}\",\"{part.CabinetId}\",\"{part.PanelNumber}\",\"{part.HandleText}\"," +
                $"{part.CuttingLength:F1},{part.CuttingWidth:F1},{part.CuttingThickness:F1}," +
                $"{part.Length:F1},{part.Width:F1},,,," +
                $"\"{part.SkipReason}\"");
        }

        File.WriteAllText(filePath, csv.ToString(), new UTF8Encoding(true));
        return filePath;
    }

    #endregion
}
