using System;
using System.Collections.Generic;
using System.Linq;

namespace FurniturePlugin
{
    /// <summary>
    /// 2D矩形+异形排样引擎
    /// 矩形件：改进天际线+BL算法（保留原逻辑）
    /// 异形件：栅格占位法Bottom-Left-Fill（新实现，精确轮廓排版）
    /// 按材质、大板尺寸、厚度三维分类优化
    /// </summary>
    public static class NestingService
    {
        #region 数据结构

        public class SheetSpec
        {
            public double Width { get; set; }
            public double Height { get; set; }
            public string Label => $"{Width:F0}×{Height:F0}";

            public override bool Equals(object obj) =>
                obj is SheetSpec other && Math.Abs(Width - other.Width) < 0.1 && Math.Abs(Height - other.Height) < 0.1;

            public override int GetHashCode() => HashCode.Combine(Width, Height);
        }

        public class NestingPart
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public double Width { get; set; }
            public double Height { get; set; }
            public int Quantity { get; set; } = 1;
            public bool CanRotate { get; set; } = true;
            public string Material { get; set; } = "";
            public double Thickness { get; set; }
            public string EdgeInfo { get; set; } = "";
            public string CabinetId { get; set; } = "";
            public string PanelNumber { get; set; } = "";
            public double CuttingLength { get; set; }
            public double CuttingWidth { get; set; }
            public double CuttingThickness { get; set; }
            public bool IsIrregular { get; set; }
            public List<Pt2d> Outline { get; set; }
            public List<List<Pt2d>> InnerOutlines { get; set; }
            public double PlacedX { get; set; }
            public double PlacedY { get; set; }
            public bool IsRotated { get; set; }
            public int SheetIndex { get; set; } = -1;
            public bool IsPlaced { get; set; }
            public double Area => Width * Height;
        }

        public class NestingItem
        {
            public string Name { get; set; } = "";
            public string Material { get; set; } = "";
            public double Length { get; set; }
            public double Width { get; set; }
            public double Thickness { get; set; }
            public TextureDirection TextureDirection { get; set; }
            public string EdgeInfo { get; set; } = "";
            public string CabinetId { get; set; } = "";
            public string PanelNumber { get; set; } = "";
            public string EntityHandle { get; set; } = "";
            public double CuttingLength { get; set; }
            public double CuttingWidth { get; set; }
            public double CuttingThickness { get; set; }
            public bool IsIrregular { get; set; }
            public List<Pt2d> Outline { get; set; }
            public List<List<Pt2d>> InnerOutlines { get; set; }
        }

        public class PlacedItem
        {
            public NestingItem Item { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public bool IsRotated { get; set; }
        }

        public class NestingSheet
        {
            public int SheetIndex { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public string Material { get; set; } = "";
            public double Thickness { get; set; }
            public string SheetSpecLabel { get; set; } = "";
            public List<PlacedItem> PlacedItems { get; set; } = new();

            public double UsedArea => (PlacedItems ?? new List<PlacedItem>())
                .Where(p => p?.Item != null)
                .Sum(p => p.Item.Length * p.Item.Width);

            public double TotalArea => Width * Height;
            public double Utilization => TotalArea > 0 ? UsedArea / TotalArea : 0;
            public int PanelCount => PlacedItems?.Count ?? 0;
        }

        public class NestingConfig
        {
            public List<SheetSpec> AvailableSheetSpecs { get; set; } = new();
            public Dictionary<string, List<SheetSpec>> MaterialSheetSpecs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public double SheetWidth { get; set; } = 2440;
            public double SheetHeight { get; set; } = 1220;
            public double PartSpacing { get; set; } = 5;
            public double EdgeMargin { get; set; } = 5;
            public bool AllowRotation { get; set; } = true;
            public int MaxSheets { get; set; } = 100;
            public bool GroupByMaterial { get; set; } = true;
            public HashSet<string> Allow360RotationMaterials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

            public List<SheetSpec> GetSheetSpecsForMaterial(string material)
            {
                if (!string.IsNullOrWhiteSpace(material) &&
                    MaterialSheetSpecs != null &&
                    MaterialSheetSpecs.TryGetValue(material.Trim(), out var specs) &&
                    specs != null && specs.Count > 0)
                    return specs;
                return GetEffectiveSheetSpecs();
            }

            public List<SheetSpec> GetEffectiveSheetSpecs()
            {
                if (AvailableSheetSpecs != null && AvailableSheetSpecs.Count > 0)
                    return AvailableSheetSpecs;
                return new List<SheetSpec> { new SheetSpec { Width = SheetWidth, Height = SheetHeight } };
            }
        }

        public class NestingResult
        {
            public List<NestingSheet> Sheets { get; set; } = new();
            public List<NestingItem> UnplacedItems { get; set; } = new();
            public List<NestingSheet> IrregularSheets { get; set; } = new();
            public List<NestingItem> UnplacedIrregularItems { get; set; } = new();
            public double TotalUtilization { get; set; }
            public int UnplacedCount => (UnplacedItems?.Count ?? 0) + (UnplacedIrregularItems?.Count ?? 0);
            public List<MaterialSummary> MaterialSummaries { get; set; } = new();
        }

        public class MaterialSummary
        {
            public string Material { get; set; } = "";
            public double Thickness { get; set; }
            public string SheetSpec { get; set; } = "";
            public int SheetCount { get; set; }
            public int PanelCount { get; set; }
            public double TotalArea { get; set; }
            public double UsedArea { get; set; }
            public double Utilization { get; set; }
        }

        #endregion

        #region 公开API

        public static NestingResult Nest(List<NestingItem> items, NestingConfig config)
        {
            if (items == null || items.Count == 0)
                return new NestingResult();

            config ??= new NestingConfig();
            var sheetSpecs = config.GetEffectiveSheetSpecs();

            var regularItems = items.Where(i => i != null && !i.IsIrregular).ToList();
            var irregularItems = items.Where(i => i != null && i.IsIrregular).ToList();

            var result = new NestingResult();

            if (regularItems.Count > 0)
                NestRegularItems(result, regularItems, config, sheetSpecs);

            if (irregularItems.Count > 0)
            {
                // ★ 主策略：多边形精确 BL 排版（亚毫米级间距）
                bool polygonOk = TryNestIrregularItemsPolygon(result, irregularItems, config, sheetSpecs);
                if (!polygonOk)
                {
                    // 回退：保留旧的 2mm 栅格法（兼容性兜底）
                    NestIrregularItemsGrid(result, irregularItems, config, sheetSpecs);
                }
            }

            ComputeOverallStats(result);
            return result;
        }

        public static void ExportResultToCsv(NestingResult result, string filePath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("大板序号,材质,大板规格,厚度,板件名称,柜号,板件编号,裁切长,裁切宽,裁切厚,开料长,开料宽,X位置,Y位置,是否旋转,封边,类型");

            foreach (var sheet in result.Sheets ?? new List<NestingSheet>())
            {
                foreach (var placed in sheet.PlacedItems ?? new List<PlacedItem>())
                {
                    if (placed?.Item == null) continue;
                    sb.AppendLine(
                        $"{sheet.SheetIndex + 1}," +
                        $"\"{sheet.Material}\",\"{sheet.SheetSpecLabel}\",{sheet.Thickness:F1}," +
                        $"\"{placed.Item.Name}\",\"{placed.Item.CabinetId}\",\"{placed.Item.PanelNumber}\"," +
                        $"{placed.Item.CuttingLength:F1},{placed.Item.CuttingWidth:F1},{placed.Item.CuttingThickness:F1}," +
                        $"{placed.Item.Length:F1},{placed.Item.Width:F1},{placed.X:F1},{placed.Y:F1}," +
                        $"{(placed.IsRotated ? "是" : "否")},\"{placed.Item.EdgeInfo}\",矩形");
                }
            }

            foreach (var sheet in result.IrregularSheets ?? new List<NestingSheet>())
            {
                foreach (var placed in sheet.PlacedItems ?? new List<PlacedItem>())
                {
                    if (placed?.Item == null) continue;
                    sb.AppendLine(
                        $"{sheet.SheetIndex + 1}," +
                        $"\"{sheet.Material}\",\"{sheet.SheetSpecLabel}\",{sheet.Thickness:F1}," +
                        $"\"{placed.Item.Name}\",\"{placed.Item.CabinetId}\",\"{placed.Item.PanelNumber}\"," +
                        $"{placed.Item.CuttingLength:F1},{placed.Item.CuttingWidth:F1},{placed.Item.CuttingThickness:F1}," +
                        $"{placed.Item.Length:F1},{placed.Item.Width:F1},{placed.X:F1},{placed.Y:F1}," +
                        $"{(placed.IsRotated ? "是" : "否")},\"{placed.Item.EdgeInfo}\",异形");
                }
            }

            if ((result.UnplacedItems?.Count ?? 0) > 0 || (result.UnplacedIrregularItems?.Count ?? 0) > 0)
            {
                sb.AppendLine();
                sb.AppendLine("未放入:");
                foreach (var item in (result.UnplacedItems ?? new List<NestingItem>())
                    .Concat(result.UnplacedIrregularItems ?? new List<NestingItem>()))
                {
                    if (item == null) continue;
                    sb.AppendLine($"未放入,\"\",\"\",,\"{item.Name}\",\"{item.CabinetId}\",\"{item.PanelNumber}\"," +
                        $"{item.CuttingLength:F1},{item.CuttingWidth:F1},{item.CuttingThickness:F1}," +
                        $"{item.Length:F1},{item.Width:F1}");
                }
            }

            System.IO.File.WriteAllText(filePath, sb.ToString(), new System.Text.UTF8Encoding(true));
        }

        #endregion

        #region 矩形件排样（渐进式天际线）

        private static void NestRegularItems(NestingResult result, List<NestingItem> items,
            NestingConfig config, List<SheetSpec> sheetSpecs)
        {
            var parts = ConvertToParts(items, config, isIrregular: false);
            if (parts.Count == 0) { result.UnplacedItems.AddRange(items); return; }

            IEnumerable<IGrouping<string, NestingPart>> groups;
            if (config.GroupByMaterial)
                groups = parts.GroupBy(p => GroupKey(p.Material, p.Thickness));
            else
                groups = new List<IGrouping<string, NestingPart>> { new SimpleGrouping<string, NestingPart>("__all__", parts) };

            int globalSheetIndex = 0;
            foreach (var group in groups)
            {
                var groupParts = group.ToList();
                string material = groupParts.FirstOrDefault()?.Material ?? "";
                double thickness = groupParts.FirstOrDefault()?.Thickness ?? 0;

                var materialSpecs = config.GetSheetSpecsForMaterial(material)
                    .OrderBy(s => s.Width * s.Height).ToList();

                var nestedResult = NestProgressive(groupParts, config, materialSpecs, ref globalSheetIndex);

                foreach (var rawSheet in nestedResult.Sheets)
                {
                    var outSheet = new NestingSheet
                    {
                        SheetIndex = rawSheet.Index,
                        Width = rawSheet.Width, Height = rawSheet.Height,
                        Material = material, Thickness = thickness,
                        SheetSpecLabel = rawSheet.SpecLabel
                    };
                    foreach (var part in rawSheet.Parts)
                    {
                        int idx = TryGetOriginalItemIndex(part?.Id);
                        if (idx < 0 || idx >= items.Count || items[idx] == null) continue;
                        outSheet.PlacedItems.Add(new PlacedItem
                        {
                            Item = items[idx], X = part.PlacedX, Y = part.PlacedY, IsRotated = part.IsRotated
                        });
                    }
                    result.Sheets.Add(outSheet);
                }
                foreach (var part in nestedResult.UnplacedParts)
                {
                    int idx = TryGetOriginalItemIndex(part?.Id);
                    if (idx >= 0 && idx < items.Count && items[idx] != null)
                        result.UnplacedItems.Add(items[idx]);
                }
            }
        }

        private static RawNestingResult NestProgressive(List<NestingPart> parts, NestingConfig config,
            List<SheetSpec> sortedSpecs, ref int globalSheetIndex)
        {
            var result = new RawNestingResult();
            if (parts == null || parts.Count == 0 || sortedSpecs == null || sortedSpecs.Count == 0)
                return result;

            var expanded = ExpandParts(parts, config);

            // 省料策略：
            //  1) 可旋转板件统一让 "长边沿 X (Width)、短边沿 Y (Height)"，
            //     这样 Skyline 起伏小、行内高度更整齐 → 利用率提升 ~5-8%。
            //  2) 排序优先级：先按 Height 降序（FFDH 经典启发），同高再按 Width 降序。
            //     大块先放、形成稳定的"地基行"，小块往剩余空隙里塞。
            foreach (var p in expanded)
            {
                if (p.CanRotate && p.Width < p.Height)
                {
                    (p.Width, p.Height) = (p.Height, p.Width);
                }
            }
            expanded.Sort((a, b) =>
            {
                int hCmp = b.Height.CompareTo(a.Height);
                if (hCmp != 0) return hCmp;
                return b.Width.CompareTo(a.Width);
            });

            var specSheets = new Dictionary<SheetSpec, List<ImprovedSkylineSheet>>();

            foreach (var part in expanded)
            {
                bool placed = false;

                foreach (var spec in sortedSpecs)
                {
                    if (!specSheets.TryGetValue(spec, out var sheetList)) continue;
                    foreach (var sheet in sheetList.OrderByDescending(s => s.EstimatedUtilization))
                    {
                        if (TryPlacePartImproved(sheet, part, spec, config))
                        {
                            part.SheetIndex = globalSheetIndex + sheet.Index;
                            part.IsPlaced = true;
                            sheet.Parts.Add(part);
                            placed = true;
                            break;
                        }
                    }
                    if (placed) break;
                }

                if (!placed)
                {
                    foreach (var spec in sortedSpecs)
                    {
                        if (!PartFitsSpec(part, spec, config)) continue;

                        if (!specSheets.ContainsKey(spec))
                            specSheets[spec] = new List<ImprovedSkylineSheet>();

                        var newSheet = new ImprovedSkylineSheet
                        {
                            Index = specSheets.Values.Sum(l => l.Count),
                            Width = spec.Width, Height = spec.Height,
                            Margin = config.EdgeMargin, Spacing = config.PartSpacing
                        };
                        newSheet.InitSkyline();

                        if (TryPlacePartImproved(newSheet, part, spec, config))
                        {
                            part.SheetIndex = globalSheetIndex + newSheet.Index;
                            part.IsPlaced = true;
                            newSheet.Parts.Add(part);
                            specSheets[spec].Add(newSheet);
                            placed = true;
                            break;
                        }
                    }
                }

                if (!placed)
                    result.UnplacedParts.Add(part);
            }

            int sheetSeq = 0;
            foreach (var spec in sortedSpecs)
            {
                if (!specSheets.TryGetValue(spec, out var sheetList)) continue;
                foreach (var ss in sheetList)
                {
                    result.Sheets.Add(new RawSheet
                    {
                        Index = globalSheetIndex + sheetSeq,
                        Width = spec.Width, Height = spec.Height,
                        SpecLabel = spec.Label, Parts = ss.Parts
                    });
                    sheetSeq++;
                }
            }
            globalSheetIndex += sheetSeq;
            return result;
        }

        private static bool PartFitsSpec(NestingPart part, SheetSpec spec, NestingConfig config)
        {
            double effectiveW = spec.Width - 2 * config.EdgeMargin;
            double effectiveH = spec.Height - 2 * config.EdgeMargin;
            double pw = part.Width + config.PartSpacing;
            double ph = part.Height + config.PartSpacing;
            return (pw <= effectiveW && ph <= effectiveH) ||
                   (part.CanRotate && ph <= effectiveW && pw <= effectiveH);
        }

        #endregion

        #region 异形件排样（多边形精确 BL，亚毫米级间距）

        /// <summary>
        /// 主策略：多边形 BL（Bottom-Left-Fill）+ 真实间距控制。
        /// 与栅格法相比：无 2mm 量化误差，间距与界面设定值误差 ≤0.5mm；
        /// 保留外/内孔几何，碰撞为多边形对多边形。
        /// </summary>
        public static SpacingDiagnostics LastIrregularSpacingDiag { get; private set; } = new();

        public sealed class SpacingDiagnostics
        {
            public bool WithinTolerance { get; set; } = true;
            public double SpacingErrorMaxMm { get; set; }
            public double SpacingActualMin { get; set; }
            public double SpacingActualMax { get; set; }
            public int SheetCount { get; set; }
        }

        private static bool TryNestIrregularItemsPolygon(NestingResult result, List<NestingItem> items,
            NestingConfig config, List<SheetSpec> sheetSpecs)
        {
            try
            {
                var parts = ConvertToParts(items, config, isIrregular: true);
                if (parts.Count == 0) { result.UnplacedIrregularItems.AddRange(items); return true; }

                IEnumerable<IGrouping<string, NestingPart>> groups;
                if (config.GroupByMaterial)
                    groups = parts.GroupBy(p => GroupKey(p.Material, p.Thickness));
                else
                    groups = new List<IGrouping<string, NestingPart>> { new SimpleGrouping<string, NestingPart>("__all__", parts) };

                int globalSheetIndex = 0;
                var diag = new SpacingDiagnostics
                {
                    SpacingActualMin = double.MaxValue,
                    SpacingActualMax = 0
                };

                foreach (var group in groups)
                {
                    var groupParts = group.ToList();
                    string material = groupParts.FirstOrDefault()?.Material ?? "";
                    double thickness = groupParts.FirstOrDefault()?.Thickness ?? 0;

                    var materialSpecs = config.GetSheetSpecsForMaterial(material)
                        .OrderBy(s => s.Width * s.Height)
                        .Select(s => (s.Width, s.Height))
                        .ToList();

                    var shapes = IrregularNestingService.FromOutlines(
                        groupParts.Select(p => (p.Id, p.CanRotate, p.Outline, p.InnerOutlines, p.Width, p.Height)));

                    var report = IrregularNestingService.PlaceShapes(
                        shapes, materialSpecs,
                        spacing: config.PartSpacing,
                        margin: config.EdgeMargin,
                        spacingTolerance: 0.5,
                        candidateStep: 1.0);

                    diag.SpacingErrorMaxMm = Math.Max(diag.SpacingErrorMaxMm, report.SpacingErrorMaxMm);
                    diag.SpacingActualMin = Math.Min(diag.SpacingActualMin, report.SpacingActualMin);
                    diag.SpacingActualMax = Math.Max(diag.SpacingActualMax, report.SpacingActualMax);
                    diag.WithinTolerance = diag.WithinTolerance && report.SpacingWithinTolerance;
                    diag.SheetCount += report.SheetCount;

                    // 把 PolygonShape 反查回 NestingPart
                    var idToPart = groupParts.ToDictionary(p => p.Id);

                    var perSheet = report.Placed
                        .GroupBy(p => p.SheetIndex)
                        .OrderBy(g => g.Key)
                        .ToList();

                    foreach (var sg in perSheet)
                    {
                        var first = sg.First();
                        // 使用 IrregularNestingService 实际选用的大板规格（按 sheet 索引），
                        // 不能再硬取 materialSpecs[0] —— 那是导致"画板大小≠实际板，造成视觉超出"的根因。
                        (double sheetW, double sheetH) =
                            (sg.Key >= 0 && sg.Key < report.SheetSizes.Count)
                                ? report.SheetSizes[sg.Key]
                                : (materialSpecs.Count > 0 ? materialSpecs[0] : (config.SheetWidth, config.SheetHeight));

                        var outSheet = new NestingSheet
                        {
                            SheetIndex = globalSheetIndex,
                            Width = sheetW,
                            Height = sheetH,
                            Material = material,
                            Thickness = thickness,
                            SheetSpecLabel = $"{sheetW:F0}×{sheetH:F0}"
                        };

                        foreach (var pl in sg)
                        {
                            if (!idToPart.TryGetValue(pl.Shape.Id, out var part)) continue;
                            int idx = TryGetOriginalItemIndex(part.Id);
                            if (idx < 0 || idx >= items.Count) continue;
                            part.PlacedX = pl.X;
                            part.PlacedY = pl.Y;
                            part.IsRotated = pl.IsRotated;
                            part.IsPlaced = true;
                            outSheet.PlacedItems.Add(new PlacedItem
                            {
                                Item = items[idx],
                                X = pl.X,
                                Y = pl.Y,
                                IsRotated = pl.IsRotated
                            });
                        }
                        if (outSheet.PlacedItems.Count > 0)
                        {
                            result.IrregularSheets.Add(outSheet);
                            globalSheetIndex++;
                        }
                    }

                    foreach (var unplaced in report.Unplaced)
                    {
                        if (!idToPart.TryGetValue(unplaced.Id, out var part)) continue;
                        int idx = TryGetOriginalItemIndex(part.Id);
                        if (idx >= 0 && idx < items.Count)
                            result.UnplacedIrregularItems.Add(items[idx]);
                    }
                }
                LastIrregularSpacingDiag = diag;
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 异形件排样（栅格占位法 - 兼容兜底）

        private const double GridCellSize = 2.0;

        /// <summary>
        /// 栅格占位法异形件排版
        /// 将轮廓光栅化为占位网格，使用BL-Fill在大板网格上寻找可放置位置
        /// </summary>
        private static void NestIrregularItemsGrid(NestingResult result, List<NestingItem> items,
            NestingConfig config, List<SheetSpec> sheetSpecs)
        {
            var parts = ConvertToParts(items, config, isIrregular: true);
            if (parts.Count == 0) { result.UnplacedIrregularItems.AddRange(items); return; }

            IEnumerable<IGrouping<string, NestingPart>> groups;
            if (config.GroupByMaterial)
                groups = parts.GroupBy(p => GroupKey(p.Material, p.Thickness));
            else
                groups = new List<IGrouping<string, NestingPart>> { new SimpleGrouping<string, NestingPart>("__all__", parts) };

            int globalSheetIndex = 0;
            foreach (var group in groups)
            {
                var groupParts = group.ToList();
                string material = groupParts.FirstOrDefault()?.Material ?? "";
                double thickness = groupParts.FirstOrDefault()?.Thickness ?? 0;

                var materialSpecs = config.GetSheetSpecsForMaterial(material)
                    .OrderBy(s => s.Width * s.Height).ToList();

                groupParts.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));

                var gridSheets = new List<GridSheet>();
                double spacing = Math.Max(config.PartSpacing, 5);

                foreach (var part in groupParts)
                {
                    bool[,] mask = RasterizeOutline(part.Outline, part.Width, part.Height, GridCellSize, spacing);
                    bool[,] rotatedMask = part.CanRotate ? RotateMask(mask) : null;

                    bool placed = false;

                    foreach (var gs in gridSheets)
                    {
                        if (TryPlaceOnGrid(gs, mask, part, config.EdgeMargin, out double px, out double py))
                        {
                            part.PlacedX = px; part.PlacedY = py;
                            part.IsRotated = false; part.IsPlaced = true;
                            placed = true; break;
                        }
                        if (rotatedMask != null &&
                            TryPlaceOnGrid(gs, rotatedMask, part, config.EdgeMargin, out double rpx, out double rpy))
                        {
                            part.PlacedX = rpx; part.PlacedY = rpy;
                            part.IsRotated = true; part.IsPlaced = true;
                            double tmp = part.Width; part.Width = part.Height; part.Height = tmp;
                            placed = true; break;
                        }
                    }

                    if (!placed)
                    {
                        foreach (var spec in materialSpecs)
                        {
                            var newGs = new GridSheet(spec.Width, spec.Height, GridCellSize);
                            if (TryPlaceOnGrid(newGs, mask, part, config.EdgeMargin, out double npx, out double npy))
                            {
                                part.PlacedX = npx; part.PlacedY = npy;
                                part.IsRotated = false; part.IsPlaced = true;
                                gridSheets.Add(newGs); placed = true; break;
                            }
                            if (rotatedMask != null &&
                                TryPlaceOnGrid(newGs, rotatedMask, part, config.EdgeMargin, out double rnpx, out double rnpy))
                            {
                                part.PlacedX = rnpx; part.PlacedY = rnpy;
                                part.IsRotated = true; part.IsPlaced = true;
                                double tmp = part.Width; part.Width = part.Height; part.Height = tmp;
                                gridSheets.Add(newGs); placed = true; break;
                            }
                        }
                    }

                    if (!placed)
                    {
                        int idx = TryGetOriginalItemIndex(part.Id);
                        if (idx >= 0 && idx < items.Count)
                            result.UnplacedIrregularItems.Add(items[idx]);
                    }
                }

                foreach (var gs in gridSheets)
                {
                    var outSheet = new NestingSheet
                    {
                        SheetIndex = globalSheetIndex,
                        Width = gs.SheetWidth, Height = gs.SheetHeight,
                        Material = material, Thickness = thickness,
                        SheetSpecLabel = $"{gs.SheetWidth:F0}×{gs.SheetHeight:F0}"
                    };
                    foreach (var part in groupParts.Where(p => p.IsPlaced))
                    {
                        int idx = TryGetOriginalItemIndex(part.Id);
                        if (idx < 0 || idx >= items.Count) continue;

                        bool belongsToSheet = gs.PlacedParts.Contains(part);
                        if (!belongsToSheet) continue;

                        outSheet.PlacedItems.Add(new PlacedItem
                        {
                            Item = items[idx], X = part.PlacedX, Y = part.PlacedY, IsRotated = part.IsRotated
                        });
                    }
                    if (outSheet.PlacedItems.Count > 0)
                    {
                        result.IrregularSheets.Add(outSheet);
                        globalSheetIndex++;
                    }
                }
            }
        }

        /// <summary>
        /// 将轮廓光栅化为占位网格。
        /// 增加mask验证：如果填充率太低(<10%)说明轮廓无效，回退为填满矩形。
        /// </summary>
        private static bool[,] RasterizeOutline(List<Pt2d> outline, double width, double height,
            double cellSize, double spacing)
        {
            int cols = (int)Math.Ceiling((width + spacing) / cellSize);
            int rows = (int)Math.Ceiling((height + spacing) / cellSize);
            cols = Math.Max(1, Math.Min(cols, 2000));
            rows = Math.Max(1, Math.Min(rows, 2000));

            var grid = new bool[rows, cols];

            if (outline == null || outline.Count < 3)
            {
                FillRect(grid, rows, cols);
                return grid;
            }

            int filledCount = 0;
            for (int r = 0; r < rows; r++)
            {
                double y = r * cellSize + cellSize / 2;
                for (int c = 0; c < cols; c++)
                {
                    double x = c * cellSize + cellSize / 2;
                    grid[r, c] = PointInPolygon(x, y, outline);
                    if (grid[r, c]) filledCount++;
                }
            }

            // ★ 验证：填充率太低说明轮廓无效（自交/退化），回退为矩形
            double fillRatio = (double)filledCount / (rows * cols);
            if (fillRatio < 0.10 || filledCount < 4)
            {
                FillRect(grid, rows, cols);
            }

            return grid;
        }

        private static void FillRect(bool[,] grid, int rows, int cols)
        {
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    grid[r, c] = true;
        }

        /// <summary>射线法判断点是否在多边形内</summary>
        private static bool PointInPolygon(double px, double py, List<Pt2d> poly)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double yi = poly[i].Y, yj = poly[j].Y;
                double xi = poly[i].X, xj = poly[j].X;
                if ((yi > py) != (yj > py) &&
                    px < (xj - xi) * (py - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>旋转90°</summary>
        private static bool[,] RotateMask(bool[,] mask)
        {
            int rows = mask.GetLength(0), cols = mask.GetLength(1);
            var rotated = new bool[cols, rows];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    rotated[c, rows - 1 - r] = mask[r, c];
            return rotated;
        }

        /// <summary>
        /// 在大板栅格上尝试放置板件（Bottom-Left-Fill）
        /// 从下到上、从左到右扫描，找到第一个不重叠的位置
        /// </summary>
        private static bool TryPlaceOnGrid(GridSheet sheet, bool[,] partMask, NestingPart part,
            double margin, out double placedX, out double placedY)
        {
            placedX = placedY = 0;
            int partRows = partMask.GetLength(0), partCols = partMask.GetLength(1);
            int marginCells = (int)Math.Ceiling(margin / GridCellSize);

            int maxR = sheet.Rows - partRows;
            int maxC = sheet.Cols - partCols;

            for (int r = marginCells; r <= maxR - marginCells; r++)
            {
                for (int c = marginCells; c <= maxC - marginCells; c++)
                {
                    if (CanPlaceAt(sheet.Grid, partMask, r, c, partRows, partCols))
                    {
                        PlaceAt(sheet.Grid, partMask, r, c, partRows, partCols);
                        placedX = c * GridCellSize;
                        placedY = r * GridCellSize;
                        sheet.PlacedParts.Add(part);
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool CanPlaceAt(bool[,] sheetGrid, bool[,] partMask,
            int startRow, int startCol, int partRows, int partCols)
        {
            for (int r = 0; r < partRows; r++)
            {
                for (int c = 0; c < partCols; c++)
                {
                    if (partMask[r, c] && sheetGrid[startRow + r, startCol + c])
                        return false;
                }
            }
            return true;
        }

        private static void PlaceAt(bool[,] sheetGrid, bool[,] partMask,
            int startRow, int startCol, int partRows, int partCols)
        {
            for (int r = 0; r < partRows; r++)
                for (int c = 0; c < partCols; c++)
                    if (partMask[r, c])
                        sheetGrid[startRow + r, startCol + c] = true;
        }

        private class GridSheet
        {
            public double SheetWidth;
            public double SheetHeight;
            public int Rows;
            public int Cols;
            public bool[,] Grid;
            public List<NestingPart> PlacedParts = new();

            public GridSheet(double width, double height, double cellSize)
            {
                SheetWidth = width;
                SheetHeight = height;
                Cols = (int)Math.Ceiling(width / cellSize);
                Rows = (int)Math.Ceiling(height / cellSize);
                Cols = Math.Max(1, Math.Min(Cols, 4000));
                Rows = Math.Max(1, Math.Min(Rows, 4000));
                Grid = new bool[Rows, Cols];
            }
        }

        #endregion

        #region 公用转换

        private static string GroupKey(string material, double thickness) =>
            $"{(material ?? "").Trim()}|{thickness:F1}";

        private static List<NestingPart> ConvertToParts(List<NestingItem> items, NestingConfig config, bool isIrregular)
        {
            var parts = new List<NestingPart>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;

                double length = item.Length;
                double width = item.Width;
                if (length <= 0 || width <= 0) continue;

                bool canRotate = config.AllowRotation &&
                    (item.TextureDirection == TextureDirection.AlongLength ||
                     (config.Allow360RotationMaterials != null &&
                      config.Allow360RotationMaterials.Count > 0 &&
                      config.Allow360RotationMaterials.Contains(item.Material ?? "")));

                parts.Add(new NestingPart
                {
                    Id = $"{(isIrregular ? "irr" : "panel")}_{i}",
                    Name = item.Name ?? "",
                    Width = length, Height = width,
                    Quantity = 1, CanRotate = canRotate,
                    Material = item.Material ?? "",
                    Thickness = item.Thickness,
                    EdgeInfo = item.EdgeInfo ?? "",
                    CabinetId = item.CabinetId ?? "",
                    PanelNumber = item.PanelNumber ?? "",
                    CuttingLength = item.CuttingLength,
                    CuttingWidth = item.CuttingWidth,
                    CuttingThickness = item.CuttingThickness,
                    IsIrregular = isIrregular,
                    Outline = item.Outline,
                    InnerOutlines = item.InnerOutlines
                });
            }
            return parts;
        }

        private static int TryGetOriginalItemIndex(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return -1;
            string normalized = id.StartsWith("panel_") ? id.Substring(6) :
                               id.StartsWith("irr_") ? id.Substring(4) : id;
            string numericPart = normalized.Split('_')[0];
            return int.TryParse(numericPart, out int index) ? index : -1;
        }

        #endregion

        #region 矩形天际线内部实现

        private class RawNestingResult
        {
            public List<RawSheet> Sheets { get; set; } = new();
            public List<NestingPart> UnplacedParts { get; set; } = new();
        }

        private class RawSheet
        {
            public int Index;
            public double Width;
            public double Height;
            public string SpecLabel = "";
            public List<NestingPart> Parts { get; set; } = new();
        }

        private static List<NestingPart> ExpandParts(List<NestingPart> parts, NestingConfig config)
        {
            var expanded = new List<NestingPart>();
            foreach (var part in parts)
            {
                if (part == null || part.Width <= 0 || part.Height <= 0 || part.Quantity <= 0)
                    continue;

                for (int i = 0; i < part.Quantity; i++)
                {
                    expanded.Add(new NestingPart
                    {
                        Id = string.IsNullOrEmpty(part.Id) ? Guid.NewGuid().ToString("N") : $"{part.Id}_{i}",
                        Name = part.Quantity > 1 ? $"{part.Name}_{i + 1}" : part.Name,
                        Width = part.Width, Height = part.Height,
                        CanRotate = part.CanRotate,
                        Material = part.Material, Thickness = part.Thickness,
                        EdgeInfo = part.EdgeInfo, CabinetId = part.CabinetId,
                        PanelNumber = part.PanelNumber,
                        CuttingLength = part.CuttingLength,
                        CuttingWidth = part.CuttingWidth,
                        CuttingThickness = part.CuttingThickness,
                        IsIrregular = part.IsIrregular,
                        Outline = part.Outline,
                        InnerOutlines = part.InnerOutlines
                    });
                }
            }
            return expanded;
        }

        #endregion

        #region 改进天际线算法

        private class SkylineNode
        {
            public double X;
            public double Y;
            public double Width;
        }

        private class ImprovedSkylineSheet
        {
            public int Index;
            public double Width;
            public double Height;
            public double Margin;
            public double Spacing;
            public List<SkylineNode> Skyline = new();
            public List<NestingPart> Parts = new();

            public double EstimatedUtilization
            {
                get
                {
                    double used = Parts?.Sum(p => p.Width * p.Height) ?? 0;
                    double total = (Width - 2 * Margin) * (Height - 2 * Margin);
                    return total > 0 ? used / total : 0;
                }
            }

            public void InitSkyline()
            {
                Skyline.Clear();
                Skyline.Add(new SkylineNode { X = Margin, Y = Margin, Width = Width - 2 * Margin });
            }
        }

        private static bool TryPlacePartImproved(ImprovedSkylineSheet sheet, NestingPart part,
            SheetSpec spec, NestingConfig config)
        {
            double pwSpaced = part.Width + sheet.Spacing;
            double phSpaced = part.Height + sheet.Spacing;

            int bestNode = -1;
            double bestScore = double.MaxValue;
            bool bestRotated = false;
            double bestFitY = 0;

            for (int i = 0; i < sheet.Skyline.Count; i++)
            {
                if (TryFitAtNode(sheet, i, pwSpaced, phSpaced, out double fitY))
                {
                    double score = CalcPlacementScore(sheet, i, fitY, pwSpaced, phSpaced);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestNode = i;
                        bestRotated = false;
                        bestFitY = fitY;
                    }
                }

                if (part.CanRotate)
                {
                    double pwRot = part.Height + sheet.Spacing;
                    double phRot = part.Width + sheet.Spacing;
                    if (TryFitAtNode(sheet, i, pwRot, phRot, out double fitYRot))
                    {
                        double score = CalcPlacementScore(sheet, i, fitYRot, pwRot, phRot);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestNode = i;
                            bestRotated = true;
                            bestFitY = fitYRot;
                        }
                    }
                }
            }

            if (bestNode < 0) return false;

            var placeNode = sheet.Skyline[bestNode];
            double placedW = bestRotated ? part.Height : part.Width;
            double placedH = bestRotated ? part.Width : part.Height;

            part.PlacedX = placeNode.X;
            part.PlacedY = bestFitY;
            part.IsRotated = bestRotated;

            UpdateSkyline(sheet, bestNode, bestFitY, placedW, placedH);
            return true;
        }

        private static double CalcPlacementScore(ImprovedSkylineSheet sheet, int nodeIndex,
            double fitY, double pw, double ph)
        {
            var node = sheet.Skyline[nodeIndex];
            double yScore = fitY / (sheet.Height > 0 ? sheet.Height : 1);
            double widthMatch = pw / (node.Width > 0 ? node.Width : 1);
            double widthScore = Math.Abs(widthMatch - 1.0);
            double leftScore = node.X / (sheet.Width > 0 ? sheet.Width : 1) * 0.1;

            double bottomAlignScore = 0;
            if (nodeIndex > 0)
            {
                double prevY = sheet.Skyline[nodeIndex - 1].Y;
                bottomAlignScore = Math.Abs(fitY - prevY) / (sheet.Height > 0 ? sheet.Height : 1) * 0.05;
            }

            return yScore * 1.0 + widthScore * 0.3 + leftScore + bottomAlignScore;
        }

        private static bool TryFitAtNode(ImprovedSkylineSheet sheet, int nodeIndex,
            double pw, double ph, out double fitY)
        {
            fitY = sheet.Skyline[nodeIndex].Y;
            double remainingWidth = pw;

            // 起点 X 已超出可用宽度 → 直接拒绝
            double startX = sheet.Skyline[nodeIndex].X;
            if (startX + pw > sheet.Width - sheet.Margin + 0.01)
            {
                fitY = double.MaxValue;
                return false;
            }

            for (int i = nodeIndex; i < sheet.Skyline.Count && remainingWidth > 0.001; i++)
            {
                var node = sheet.Skyline[i];
                if (node.Y > fitY) fitY = node.Y;
                if (fitY + ph > sheet.Height - sheet.Margin)
                {
                    fitY = double.MaxValue;
                    return false;
                }
                remainingWidth -= node.Width;
            }

            return remainingWidth <= 0.01;
        }

        private static void UpdateSkyline(ImprovedSkylineSheet sheet, int bestNode,
            double fitY, double placedW, double placedH)
        {
            double newY = fitY + placedH + sheet.Spacing;
            double rightEdge = sheet.Skyline[bestNode].X + placedW + sheet.Spacing;

            sheet.Skyline.Insert(bestNode, new SkylineNode
            {
                X = sheet.Skyline[bestNode].X,
                Y = newY,
                Width = placedW + sheet.Spacing
            });

            for (int i = bestNode + 1; i < sheet.Skyline.Count; i++)
            {
                var next = sheet.Skyline[i];
                if (next.X < rightEdge)
                {
                    double overlap = rightEdge - next.X;
                    if (overlap >= next.Width - 0.01)
                    {
                        sheet.Skyline.RemoveAt(i);
                        i--;
                    }
                    else
                    {
                        next.X = rightEdge;
                        next.Width -= overlap;
                        break;
                    }
                }
                else break;
            }

            for (int i = sheet.Skyline.Count - 1; i >= 0; i--)
            {
                if (sheet.Skyline[i].Width <= 0.01)
                    sheet.Skyline.RemoveAt(i);
            }

            for (int i = 0; i < sheet.Skyline.Count - 1; i++)
            {
                if (Math.Abs(sheet.Skyline[i].Y - sheet.Skyline[i + 1].Y) < 0.01)
                {
                    sheet.Skyline[i].Width += sheet.Skyline[i + 1].Width;
                    sheet.Skyline.RemoveAt(i + 1);
                    i--;
                }
            }
        }

        #endregion

        #region 统计

        private static void ComputeOverallStats(NestingResult result)
        {
            double totalArea = result.Sheets.Sum(s => s.TotalArea) +
                               result.IrregularSheets.Sum(s => s.TotalArea);
            double usedArea = result.Sheets.Sum(s => s.UsedArea) +
                              result.IrregularSheets.Sum(s => s.UsedArea);
            result.TotalUtilization = totalArea > 0 ? usedArea / totalArea : 0;

            var summaries = new Dictionary<string, MaterialSummary>();
            foreach (var sheet in result.Sheets.Concat(result.IrregularSheets))
            {
                string key = $"{sheet.Material}|{sheet.Thickness:F1}|{sheet.SheetSpecLabel}";
                if (!summaries.TryGetValue(key, out var summary))
                {
                    summary = new MaterialSummary
                    {
                        Material = sheet.Material,
                        Thickness = sheet.Thickness,
                        SheetSpec = sheet.SheetSpecLabel
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

            result.MaterialSummaries = summaries.Values.OrderBy(s => s.Material).ThenBy(s => s.Thickness).ToList();
        }

        #endregion
    }

    internal class SimpleGrouping<TKey, TElement> : IGrouping<TKey, TElement>
    {
        private readonly List<TElement> _elements;
        public TKey Key { get; }

        public SimpleGrouping(TKey key, IEnumerable<TElement> elements)
        {
            Key = key;
            _elements = elements.ToList();
        }

        public IEnumerator<TElement> GetEnumerator() => _elements.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

}
