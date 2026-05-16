using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace FurniturePlugin;

/// <summary>
/// 统一板件几何识别入口：SETI、CKBJ 和批量自动取尺寸都从这里拿板件形状与尺寸。
/// </summary>
public static class PanelGeometryAnalysisService
{
    public sealed class AnalysisResult
    {
        public bool Success { get; set; }
        public string Reason { get; set; } = "";
        public bool FromSlot { get; set; }
        public PanelCalculationType CalculationType { get; set; } = PanelCalculationType.AABB;
        public double Length { get; set; }
        public double Width { get; set; }
        public double Thickness { get; set; }
        public double ArcInnerRadius { get; set; }
        public double ArcAngleDegrees { get; set; }
        public double ArcStraightLength1 { get; set; }
        public double ArcStraightLength2 { get; set; }
        public double UnfoldedLength { get; set; }
        public double UnfoldedWidth { get; set; }
        public double ArcOuterLength { get; set; }
        public double ArcCenterLength { get; set; }
        public double ArcInnerLength { get; set; }
    }

    public static AnalysisResult Analyze(Solid3d solid, PanelSlot associatedSlot = null)
    {
        if (associatedSlot != null)
        {
            return new AnalysisResult
            {
                Success = true,
                FromSlot = true,
                CalculationType = PanelCalculationType.AABB,
                Length = associatedSlot.ComputedLength,
                Width = associatedSlot.ComputedWidth,
                Thickness = associatedSlot.ComputedThickness,
                Reason = "搭积木 PanelSlot 尺寸"
            };
        }

        if (solid == null || solid.IsErased)
            return new AnalysisResult { Reason = "实体无效" };

        FastDimensionService.Result fast = null;
        try
        {
            fast = FastDimensionService.Compute(solid);
        }
        catch (Exception ex)
        {
            PluginLogger.Warning("[PanelGeometry] FastDimension 失败: " + ex.Message);
        }

        PanelTypeDetector.DetectionResult grip = null;
        try
        {
            grip = PanelTypeDetector.Detect(solid);
        }
        catch (Exception ex)
        {
            PluginLogger.Warning("[PanelGeometry] PanelTypeDetector 失败: " + ex.Message);
        }

        bool isSweepPanel = fast != null &&
                            fast.Success &&
                            (fast.DetectedType == PanelCalculationType.ArcPanel ||
                             fast.DetectedType == PanelCalculationType.SplinePanel);
        if (isSweepPanel)
        {
            var reviewed = ReviewCurvedCandidateOnAlignedClone(solid, fast, grip);
            if (reviewed != null)
                return reviewed;

            if (ShouldDowngradeCurvedDetectionByOutline(solid))
                isSweepPanel = false;
        }

        var result = new AnalysisResult
        {
            Success = true,
            CalculationType = isSweepPanel
                ? fast.DetectedType
                : (grip?.DetectedType ?? fast?.DetectedType ?? PanelCalculationType.AABB),
            Reason = ComposeReason(fast, grip)
        };

        if (isSweepPanel)
        {
            FillFromFast(result, fast);
            return result;
        }

        PopulateNonSweepDimensions(result, solid, fast);

        if (result.Length <= 0 || result.Width <= 0 || result.Thickness <= 0)
        {
            FillFromAabb(result, solid);
            if (result.CalculationType != PanelCalculationType.Irregular)
                result.CalculationType = PanelCalculationType.AABB;
        }

        return result;
    }

    public static bool ApplyDetectedGeometry(
        Solid3d solid,
        PanelInfo info,
        PanelSlot associatedSlot = null,
        bool overwriteDimensions = true,
        bool overwriteCalculationType = true,
        bool updateCuttingDimensions = true)
    {
        if (info == null)
            return false;

        if (overwriteDimensions &&
            associatedSlot == null &&
            info.IsCalculationTypeManual)
        {
            ApplyGeometryByManualCalculationType(solid, info, updateCuttingDimensions);
            return true;
        }

        var result = Analyze(solid, associatedSlot);
        if (!result.Success)
            return false;

        bool allowOverwriteCalculationType = overwriteCalculationType && !info.IsCalculationTypeManual;
        if (allowOverwriteCalculationType && !result.FromSlot)
            info.CalculationType = result.CalculationType;

        ApplyCurveParameters(info, result);

        if (overwriteDimensions)
        {
            bool isCurve = result.CalculationType == PanelCalculationType.ArcPanel ||
                           result.CalculationType == PanelCalculationType.SplinePanel;
            info.Length = Math.Round(isCurve ? ResolveArcLengthByReference(result, info.ArcLengthReference) : result.Length, 2);
            info.Width = Math.Round(result.Width, 2);
            info.Height = Math.Round(result.Thickness, 2);
            // #region debug-point C:apply-curve-length
            FastDimensionService.DebugReport("C", "PanelGeometryAnalysisService.ApplyDetectedGeometry:length-applied", new
            {
                calcType = result.CalculationType.ToString(),
                reference = info.ArcLengthReference.ToString(),
                resultLength = result.Length,
                resultWidth = result.Width,
                resultThickness = result.Thickness,
                arcOuter = result.ArcOuterLength,
                arcCenter = result.ArcCenterLength,
                arcInner = result.ArcInnerLength,
                appliedLength = info.Length,
                appliedWidth = info.Width,
                appliedHeight = info.Height
            });
            // #endregion
        }

        if (updateCuttingDimensions && overwriteDimensions)
            ApplyDefaultCuttingDimensions(info, associatedSlot);

        return true;
    }

    private static void ApplyGeometryByManualCalculationType(
        Solid3d solid,
        PanelInfo info,
        bool updateCuttingDimensions)
    {
        if (solid == null || info == null)
            return;

        double oldExtraLength = info.ExtraLength;
        double oldExtraWidth = info.ExtraWidth;
        double oldExtraHeight = info.ExtraHeight;

        MyPlugin.CalculateAndSetDimensionsByType(solid, info);
        if (info.CalculationType != PanelCalculationType.ArcPanel &&
            info.CalculationType != PanelCalculationType.SplinePanel)
        {
            ResetCurveParameters(info);
        }

        if (!updateCuttingDimensions)
        {
            info.ExtraLength = oldExtraLength;
            info.ExtraWidth = oldExtraWidth;
            info.ExtraHeight = oldExtraHeight;
        }
    }

    public static void ApplyDefaultCuttingDimensions(PanelInfo info, PanelSlot associatedSlot = null)
    {
        if (info == null)
            return;

        if (associatedSlot != null)
        {
            info.ExtraLength = associatedSlot.CuttingLength;
            info.ExtraWidth = associatedSlot.CuttingWidth;
            info.ExtraHeight = associatedSlot.ComputedThickness;
            return;
        }

        if (string.IsNullOrWhiteSpace(info.ExtraLengthFormula))
        {
            info.ExtraLength = Math.Round(info.Length - ParseEdgeThickness(info.EdgeTop) - ParseEdgeThickness(info.EdgeBottom), 2);
        }

        if (string.IsNullOrWhiteSpace(info.ExtraWidthFormula))
        {
            info.ExtraWidth = Math.Round(info.Width - ParseEdgeThickness(info.EdgeLeft) - ParseEdgeThickness(info.EdgeRight), 2);
        }

        if (string.IsNullOrWhiteSpace(info.ExtraHeightFormula))
        {
            info.ExtraHeight = Math.Round(info.Height, 2);
        }
    }

    private static void FillFromFast(AnalysisResult result, FastDimensionService.Result fast)
    {
        result.Length = fast.Length;
        result.Width = fast.Width;
        result.Thickness = fast.Thickness;
        result.ArcInnerRadius = fast.ArcInnerRadius;
        result.ArcAngleDegrees = fast.ArcAngleDegrees;
        result.ArcStraightLength1 = fast.ArcStraightLength1;
        result.ArcStraightLength2 = fast.ArcStraightLength2;
        result.UnfoldedLength = fast.UnfoldedLength;
        result.UnfoldedWidth = fast.UnfoldedWidth > 0 ? fast.UnfoldedWidth : fast.Width;
        result.ArcOuterLength = fast.ArcOuterLength;
        result.ArcCenterLength = fast.ArcCenterLength > 0 ? fast.ArcCenterLength : fast.UnfoldedLength;
        result.ArcInnerLength = fast.ArcInnerLength;
    }

    private static void PopulateNonSweepDimensions(
        AnalysisResult result,
        Solid3d solid,
        FastDimensionService.Result fast)
    {
        switch (result.CalculationType)
        {
            case PanelCalculationType.OBB:
                MyPlugin.CalcOBBByMassProperties(solid, out var obbLength, out var obbWidth, out var obbThickness);
                result.Length = obbLength;
                result.Width = obbWidth;
                result.Thickness = obbThickness;
                break;

            case PanelCalculationType.Irregular:
                MyPlugin.CalcIrregularDimensions(solid, out var irrLength, out var irrWidth, out var irrThickness);
                result.Length = irrLength;
                result.Width = irrWidth;
                result.Thickness = irrThickness;
                result.CalculationType = PanelCalculationType.Irregular;
                break;

            case PanelCalculationType.AABB:
            default:
                FillFromAabb(result, solid);
                result.CalculationType = PanelCalculationType.AABB;
                break;
        }
    }

    private static void FillFromAabb(AnalysisResult result, Solid3d solid)
    {
        try
        {
            var ext = solid.GeometricExtents;
            var dims = new[]
            {
                Math.Abs(ext.MaxPoint.X - ext.MinPoint.X),
                Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y),
                Math.Abs(ext.MaxPoint.Z - ext.MinPoint.Z)
            };
            Array.Sort(dims);
            result.Thickness = Math.Round(dims[0], 2);
            result.Width = Math.Round(dims[1], 2);
            result.Length = Math.Round(dims[2], 2);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Reason = "AABB 失败: " + ex.Message;
        }
    }

    private static void ApplyCurveParameters(PanelInfo info, AnalysisResult result)
    {
        bool isCurve = result.CalculationType == PanelCalculationType.ArcPanel ||
                       result.CalculationType == PanelCalculationType.SplinePanel;

        if (!isCurve)
        {
            ResetCurveParameters(info);
            return;
        }

        info.ArcInnerRadius = Math.Round(result.ArcInnerRadius, 2);
        info.ArcAngleDegrees = Math.Round(result.ArcAngleDegrees, 2);
        info.ArcStraightLength1 = Math.Round(result.ArcStraightLength1, 2);
        info.ArcStraightLength2 = Math.Round(result.ArcStraightLength2, 2);
        info.UnfoldedLength = Math.Round(result.UnfoldedLength > 0 ? result.UnfoldedLength : result.Length, 2);
        info.UnfoldedWidth = Math.Round(result.UnfoldedWidth > 0 ? result.UnfoldedWidth : result.Width, 2);
        info.ArcOuterLength = Math.Round(result.ArcOuterLength > 0 ? result.ArcOuterLength : info.UnfoldedLength, 2);
        info.ArcCenterLength = Math.Round(result.ArcCenterLength > 0 ? result.ArcCenterLength : info.UnfoldedLength, 2);
        info.ArcInnerLengthValue = Math.Round(result.ArcInnerLength > 0 ? result.ArcInnerLength : info.UnfoldedLength, 2);
        if (!Enum.IsDefined(typeof(ArcLengthReferenceType), info.ArcLengthReference))
            info.ArcLengthReference = ArcLengthReferenceType.Outer;
        // #region debug-point D:curve-parameters
        FastDimensionService.DebugReport("D", "PanelGeometryAnalysisService.ApplyCurveParameters:curve-summary", new
        {
            calcType = result.CalculationType.ToString(),
            outer = info.ArcOuterLength,
            center = info.ArcCenterLength,
            inner = info.ArcInnerLengthValue,
            unfolded = info.UnfoldedLength,
            width = info.UnfoldedWidth,
            radius = info.ArcInnerRadius,
            angle = info.ArcAngleDegrees,
            straight1 = info.ArcStraightLength1,
            straight2 = info.ArcStraightLength2,
            reference = info.ArcLengthReference.ToString()
        });
        // #endregion
    }

    private static void ResetCurveParameters(PanelInfo info)
    {
        info.ArcInnerRadius = 0;
        info.ArcAngleDegrees = 0;
        info.ArcStraightLength1 = 0;
        info.ArcStraightLength2 = 0;
        info.UnfoldedLength = 0;
        info.UnfoldedWidth = 0;
        info.ArcOuterLength = 0;
        info.ArcCenterLength = 0;
        info.ArcInnerLengthValue = 0;
        info.ArcLengthReference = ArcLengthReferenceType.Outer;
    }

    private static double ResolveArcLengthByReference(AnalysisResult result, ArcLengthReferenceType referenceType)
    {
        double outer = result.ArcOuterLength > 0 ? result.ArcOuterLength : (result.UnfoldedLength > 0 ? result.UnfoldedLength : result.Length);
        double center = result.ArcCenterLength > 0 ? result.ArcCenterLength : (result.UnfoldedLength > 0 ? result.UnfoldedLength : outer);
        double inner = result.ArcInnerLength > 0 ? result.ArcInnerLength : center;

        return referenceType switch
        {
            ArcLengthReferenceType.Inner => inner,
            ArcLengthReferenceType.Center => center,
            _ => outer
        };
    }

    private static double ParseEdgeThickness(string edgeText)
    {
        if (string.IsNullOrWhiteSpace(edgeText))
            return 0;

        var m = System.Text.RegularExpressions.Regex.Match(edgeText.Trim(), @"^(\d+(?:\.\d+)?)");
        if (m.Success && double.TryParse(m.Groups[1].Value, out var value))
            return value;

        return 0;
    }

    private static string ComposeReason(FastDimensionService.Result fast, PanelTypeDetector.DetectionResult grip)
    {
        string fastReason = fast?.Reason ?? "";
        string gripReason = grip?.Reason ?? "";
        if (!string.IsNullOrWhiteSpace(fastReason) && !string.IsNullOrWhiteSpace(gripReason))
            return fastReason + "；" + gripReason;
        return fastReason + gripReason;
    }

    private static AnalysisResult ReviewCurvedCandidateOnAlignedClone(
        Solid3d solid,
        FastDimensionService.Result originalFast,
        PanelTypeDetector.DetectionResult originalGrip)
    {
        if (solid == null || solid.IsErased)
            return null;

        Solid3d clone = null;
        try
        {
            clone = solid.Clone() as Solid3d;
            if (clone == null)
                return null;

            var align = BzbjAlignmentService.AlignToWorldXY(clone, normalToleranceDeg: 0.001);
            FastDimensionService.Result alignedFast = null;
            PanelTypeDetector.DetectionResult alignedGrip = null;

            try { alignedFast = FastDimensionService.Compute(clone); }
            catch (Exception ex) { PluginLogger.Warning("[PanelGeometry] 对齐后 FastDimension 失败: " + ex.Message); }

            try { alignedGrip = PanelTypeDetector.Detect(clone); }
            catch (Exception ex) { PluginLogger.Warning("[PanelGeometry] 对齐后 PanelTypeDetector 失败: " + ex.Message); }

            bool alignedSweep = alignedFast != null &&
                                alignedFast.Success &&
                                (alignedFast.DetectedType == PanelCalculationType.ArcPanel ||
                                 alignedFast.DetectedType == PanelCalculationType.SplinePanel);
            bool downgradeByOutline = ShouldDowngradeCurvedDetectionByOutline(clone);

            if (alignedSweep && !downgradeByOutline)
            {
                var curved = new AnalysisResult
                {
                    Success = true,
                    CalculationType = alignedFast.DetectedType,
                    Reason = ComposeReason(alignedFast, alignedGrip) + AppendReviewReason("虚拟摆正复核仍为曲面板", align?.Reason)
                };
                FillFromFast(curved, alignedFast);
                return curved;
            }

            var downgraded = new AnalysisResult
            {
                Success = true,
                CalculationType = PickFallbackType(alignedGrip, alignedFast, originalGrip),
                Reason = ComposeReason(alignedFast ?? originalFast, alignedGrip ?? originalGrip) +
                         AppendReviewReason("虚拟摆正复核判定为非曲面板", align?.Reason)
            };

            if (downgraded.CalculationType == PanelCalculationType.ArcPanel ||
                downgraded.CalculationType == PanelCalculationType.SplinePanel)
            {
                downgraded.CalculationType = PanelCalculationType.Irregular;
            }

            PopulateNonSweepDimensions(downgraded, solid, alignedFast ?? originalFast);
            return downgraded;
        }
        catch (Exception ex)
        {
            PluginLogger.Warning("[PanelGeometry] 曲面候选复核失败: " + ex.Message);
            return null;
        }
        finally
        {
            try { clone?.Dispose(); } catch { }
        }
    }

    private static PanelCalculationType PickFallbackType(
        PanelTypeDetector.DetectionResult alignedGrip,
        FastDimensionService.Result alignedFast,
        PanelTypeDetector.DetectionResult originalGrip)
    {
        if (alignedGrip != null)
        {
            if (alignedGrip.DetectedType == PanelCalculationType.ArcPanel ||
                alignedGrip.DetectedType == PanelCalculationType.SplinePanel)
                return PanelCalculationType.Irregular;
            return alignedGrip.DetectedType;
        }

        if (alignedFast != null && alignedFast.Success)
        {
            if (alignedFast.DetectedType == PanelCalculationType.ArcPanel ||
                alignedFast.DetectedType == PanelCalculationType.SplinePanel)
                return PanelCalculationType.Irregular;
            return alignedFast.DetectedType;
        }

        if (originalGrip != null)
            return originalGrip.DetectedType;

        return PanelCalculationType.Irregular;
    }

    private static string AppendReviewReason(string reviewReason, string alignReason)
    {
        if (string.IsNullOrWhiteSpace(reviewReason) && string.IsNullOrWhiteSpace(alignReason))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(alignReason))
            return "；" + reviewReason;
        if (string.IsNullOrWhiteSpace(reviewReason))
            return "；" + alignReason;
        return $"；{reviewReason}（{alignReason}）";
    }

    private static bool ShouldDowngradeCurvedDetectionByOutline(Solid3d solid)
    {
        if (solid == null || solid.IsErased)
            return false;

        try
        {
            var ext = solid.GeometricExtents;
            var dims = new[]
            {
                Math.Abs(ext.MaxPoint.X - ext.MinPoint.X),
                Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y),
                Math.Abs(ext.MaxPoint.Z - ext.MinPoint.Z)
            };
            Array.Sort(dims);
            double boxLen = dims[2];
            double boxWid = dims[1];

            var profile = OutlineService.ExtractOutlineProfile(solid, boxLen, boxWid);
            if (profile == null || profile.Inners == null || profile.Inners.Count == 0)
                return false;

            return IsOuterLoopRectangularish(profile.Outer);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOuterLoopRectangularish(System.Collections.Generic.List<Pt2d> outer)
    {
        if (outer == null || outer.Count < 4)
            return false;

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        for (int i = 0; i < outer.Count; i++)
        {
            var p = outer[i];
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        double width = Math.Max(0, maxX - minX);
        double height = Math.Max(0, maxY - minY);
        if (width < 1.0 || height < 1.0)
            return false;

        double area = Math.Abs(PolygonArea(outer));
        double bboxArea = width * height;
        if (bboxArea < 1.0)
            return false;

        double fillRatio = area / bboxArea;
        if (fillRatio < 0.96)
            return false;

        double edgeTol = Math.Max(1.0, Math.Min(width, height) * 0.015);
        int edgeAligned = 0;
        for (int i = 0; i < outer.Count; i++)
        {
            var p = outer[i];
            double dist = Math.Min(
                Math.Min(Math.Abs(p.X - minX), Math.Abs(p.X - maxX)),
                Math.Min(Math.Abs(p.Y - minY), Math.Abs(p.Y - maxY)));
            if (dist <= edgeTol)
                edgeAligned++;
        }

        return edgeAligned >= Math.Ceiling(outer.Count * 0.85);
    }

    private static double PolygonArea(System.Collections.Generic.List<Pt2d> pts)
    {
        if (pts == null || pts.Count < 3)
            return 0;

        double sum = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return sum * 0.5;
    }
}
