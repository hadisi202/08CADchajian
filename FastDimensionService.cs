using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin;

/// <summary>
/// SETI 命令使用的「截面 + 轨对中线」快速精确尺寸计算服务。
///
/// ★ 关键修复历史：
///   - v1：用 OBB / 全实体 Explode → 样条板上卡死；
///   - v2：改用 Solid3d.GetSection(中部) 切一刀 + Region.Explode（2D，便宜），
///         在截面里挑主圆弧定主轴或做 2D PCA → 圆弧板的「长度」会得到弦长（直径）
///         而不是弧长，且把 AABB 最小轴硬当成 thickness → 厚度也错；
///   - v3（当前）：在 X/Y/Z 三个 AABB 轴上「全部各试一次截面」，专门寻找
///         「曲线导轨对」（截面里两条几乎平行的长曲线）。
///         · 找得到 → 用 <see cref="FastDimensionMath.AssignDimensions"/>：
///           thickness=min(轨距,AABB 在该轴跨度)，再在「中线长」与「max(轨距,跨度)」之间分配 length/width；
///           双样条轨对 → <see cref="PanelCalculationType.SplinePanel"/>（归一化弧长对齐 + 中点折线）；
///           其他曲线导轨（含圆弧）→ <see cref="PanelCalculationType.ArcPanel"/>。
///         · 找不到 → 退化到最小 AABB 轴的旧路径（适配普通矩形板 + 异形多边形板）。
///
/// ★ 不卡死的保证：
///   - 不调用 <see cref="Entity.Explode"/>（实体级 explode 是历史卡死根源）；
///   - 只用 <see cref="Solid3d.GetSection(Plane)"/>（ASM 原生 ~10ms）+
///     <see cref="Region.Explode"/>（2D，对样条/弧也只是十几个曲线对象）；
///   - 任何曲线异常都吞掉，最差情况退化到 AABB。
/// </summary>
public static class FastDimensionService
{
    private static readonly object DebugFileSync = new();

    public sealed class Result
    {
        public double Length { get; set; }
        public double Width { get; set; }
        public double Thickness { get; set; }
        /// <summary>用于诊断：哪条路径产出了该尺寸。</summary>
        public string Method { get; set; } = "";
        public bool Success { get; set; }
        public string Reason { get; set; } = "";

        /// <summary>本算法识别出的板件几何类型（AABB / OBB / ArcPanel / SplinePanel / Irregular）。</summary>
        public PanelCalculationType DetectedType { get; set; } = PanelCalculationType.AABB;

        /// <summary>识别为 ArcPanel 时的内圈半径（mm）。0 = 未识别成圆弧（可能是样条）。</summary>
        public double ArcInnerRadius { get; set; }
        /// <summary>识别为 ArcPanel 时的扫角（度）。0 = 未识别成圆弧。</summary>
        public double ArcAngleDegrees { get; set; }
        /// <summary>识别为 ArcPanel 时，展开路径上的第 1 段直边长度。</summary>
        public double ArcStraightLength1 { get; set; }
        /// <summary>识别为 ArcPanel 时，展开路径上的第 2 段直边长度。</summary>
        public double ArcStraightLength2 { get; set; }
        /// <summary>识别为 ArcPanel 时，按直段 + 曲线段拆解得到的展开长。</summary>
        public double UnfoldedLength { get; set; }
        /// <summary>识别为 ArcPanel/SplinePanel 时，展开后的直向宽度。</summary>
        public double UnfoldedWidth { get; set; }
        /// <summary>圆弧/样条板件按外线累计得到的总边长。</summary>
        public double ArcOuterLength { get; set; }
        /// <summary>圆弧/样条板件按中线累计得到的总边长。</summary>
        public double ArcCenterLength { get; set; }
        /// <summary>圆弧/样条板件按内线累计得到的总边长。</summary>
        public double ArcInnerLength { get; set; }
    }

    /// <summary>截面里识别到的一对曲线导轨（圆弧解析或双样条高精度）。</summary>
    private readonly record struct CurvedRailPick(
        int Idx1,
        int Idx2,
        double CenterlineLength,
        double OuterLength,
        double InnerLength,
        double RailDistance,
        double InnerRadius,
        double AngleDegrees,
        bool AnalyticArc,
        bool DualSpline);

    /// <summary>
    /// 计算单个 Solid3d 的精确长 × 宽 × 厚 + 几何类型。任何失败都退化到 AABB —— 永远不会抛。
    /// </summary>
    public static Result Compute(Solid3d solid)
    {
        if (solid == null || solid.IsErased)
            return new Result { Reason = "实体无效" };

        Extents3d ext;
        try { ext = solid.GeometricExtents; }
        catch (System.Exception ex)
        {
            return new Result { Reason = "GeometricExtents 失败: " + ex.Message };
        }

        double[] aabb = {
            ext.MaxPoint.X - ext.MinPoint.X,
            ext.MaxPoint.Y - ext.MinPoint.Y,
            ext.MaxPoint.Z - ext.MinPoint.Z
        };

        // ── 第 1 段：在 X/Y/Z 三个轴上各试一次截面，专找「曲线导轨对」 ──
        // 这一步正确处理样条/圆弧拉伸板件：哪个轴的截面里有曲线导轨对，就把它当
        // 拉伸方向，AABB 在该轴的跨度 = 宽度，轨距 = 真厚度，中线弧长 = 长度。
        Result bestCurved = null;
        var axisCandidates = new List<object>(3);
        for (int axis = 0; axis < 3; axis++)
        {
            var cand = TrySectionForAxis(solid, ext, aabb, axis, requireCurvedRails: true);
            axisCandidates.Add(new
            {
                axis,
                hasCandidate = cand != null,
                method = cand?.Method ?? "",
                detectedType = cand?.DetectedType.ToString() ?? "",
                length = cand?.Length ?? 0,
                width = cand?.Width ?? 0,
                thickness = cand?.Thickness ?? 0,
                outer = cand?.ArcOuterLength ?? 0,
                center = cand?.ArcCenterLength ?? 0,
                inner = cand?.ArcInnerLength ?? 0,
                unfolded = cand?.UnfoldedLength ?? 0
            });
            if (cand != null && IsBetterCurvedCandidate(cand, bestCurved))
                bestCurved = cand;
        }
        // #region debug-point F:axis-candidates
        DebugReport("F", "FastDimensionService.Compute:curved-axis-candidates", new
        {
            aabb,
            chosenMethod = bestCurved?.Method ?? "",
            chosenType = bestCurved?.DetectedType.ToString() ?? "",
            chosenLength = bestCurved?.Length ?? 0,
            chosenWidth = bestCurved?.Width ?? 0,
            chosenThickness = bestCurved?.Thickness ?? 0,
            axisCandidates = axisCandidates.ToArray()
        });
        // #endregion
        if (bestCurved != null)
        {
            FinalizeRoundAndCompose(bestCurved, aabb);
            return bestCurved;
        }

        // ── 第 2 段：退化到最小 AABB 轴上的旧路径（普通矩形板 + 异形多边形板） ──
        int thickAxis = SmallestAxisIndex(aabb);
        var rect = TrySectionForAxis(solid, ext, aabb, thickAxis, requireCurvedRails: false);
        if (rect != null)
        {
            FinalizeRoundAndCompose(rect, aabb);
            return rect;
        }

        // ── 第 3 段：AABB 终极兜底 ──
        var sorted = (double[])aabb.Clone();
        Array.Sort(sorted);
        return new Result
        {
            Thickness = Math.Round(sorted[0], 2),
            Width = Math.Round(sorted[1], 2),
            Length = Math.Round(sorted[2], 2),
            Method = "aabb",
            DetectedType = PanelCalculationType.AABB,
            Success = true,
            Reason = $"AABB 兜底: L={Math.Round(sorted[2], 2)} W={Math.Round(sorted[1], 2)} T={Math.Round(sorted[0], 2)}"
        };
    }

    // ─────────── 单轴截面尝试 ───────────

    /// <summary>
    /// 在指定轴上做一次截面，尝试 (1) 轨对识别 → 中线弧长，(2) 主弧/PCA 投影 BBox。
    /// <paramref name="requireCurvedRails"/>=true 时只接受「轨对里有 ≥1 条曲线导轨」的候选；
    /// false 时接受任意 rail-pair / PCA 结果（适配普通矩形 / 异形多边形）。
    /// </summary>
    private static Result TrySectionForAxis(
        Solid3d solid, Extents3d ext, double[] aabb, int axis, bool requireCurvedRails)
    {
        Vector3d normal = axis switch
        {
            0 => Vector3d.XAxis,
            1 => Vector3d.YAxis,
            _ => Vector3d.ZAxis,
        };
        Point3d sectionOrigin = new Point3d(
            (ext.MinPoint.X + ext.MaxPoint.X) * 0.5,
            (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5,
            (ext.MinPoint.Z + ext.MaxPoint.Z) * 0.5);

        Region region;
        try
        {
            using var plane = new Plane(sectionOrigin, normal);
            region = solid.GetSection(plane);
        }
        catch (System.Exception ex)
        {
            PluginLogger.Debug($"[FastDimension] GetSection 失败 axis={axis}: {ex.Message}");
            return null;
        }
        if (region == null) return null;

        var curves = new List<Curve>();
        var coll = new DBObjectCollection();
        try
        {
            try { region.Explode(coll); }
            catch
            {
                PluginLogger.Debug($"[FastDimension] Region.Explode 失败 axis={axis}");
                try { region.Dispose(); } catch { }
                return null;
            }
            foreach (DBObject obj in coll)
                if (obj is Curve cv) curves.Add(cv);
        }
        finally { try { region.Dispose(); } catch { } }

        try
        {
            if (curves.Count == 0) return null;

            var nVec = new FastDimensionMath.Vec3(normal.X, normal.Y, normal.Z);
            var origin = new FastDimensionMath.Vec3(sectionOrigin.X, sectionOrigin.Y, sectionOrigin.Z);
            double aabbAlongAxis = aabb[axis];

            // (1) 轨对识别
            var railResult = TryRailPair(curves, aabbAlongAxis, requireCurvedRails);
            if (railResult != null) return railResult;

            // (2) 主弧/PCA 投影 —— 仅当不强求曲线导轨时启用（即第 2 段路径）
            if (!requireCurvedRails)
            {
                var pcaResult = TryAxisProjected(curves, nVec, origin, aabbAlongAxis);
                if (pcaResult != null) return pcaResult;
            }

            return null;
        }
        finally
        {
            foreach (var cv in curves) { try { cv.Dispose(); } catch { } }
        }
    }

    /// <summary>
    /// 「轨对中线」算法核心：识别截面里两条最长的曲线作为导轨，校验它们形成
    /// 一对几乎平行的「长导轨 + 短端帽」结构。
    ///
    /// 返回的 <see cref="Result"/> 中尺寸由 <see cref="FastDimensionMath.AssignDimensions"/> 合成：
    ///   thickness = min(轨距, AABB 沿截面法向轴跨度)，再在「中线长」与 max(轨距,跨度) 之间取 length/width。
    /// </summary>
    private static Result TryRailPair(
        List<Curve> curves, double aabbAlongAxis, bool requireCurvedRails)
    {
        var lengths = new double[curves.Count];
        for (int i = 0; i < curves.Count; i++) lengths[i] = SafeCurveLength(curves[i]);

        // 先处理“直边 + 圆弧/样条 + 直边”的组合轨道。Region.Explode 会把一条
        // 真实导轨拆成多段曲线，单纯取最长两条会漏掉直段或弧段。
        var composite = TryCompositeRailPath(curves, lengths, aabbAlongAxis, requireCurvedRails);
        if (composite != null) return composite;

        var rp = FastDimensionMath.PickRailPair(lengths);
        if (!rp.IsRailPair) return null;

        var rail1 = curves[rp.Rail1Index];
        var rail2 = curves[rp.Rail2Index];

        if (IsClosedCurvedLoop(rail1) || IsClosedCurvedLoop(rail2))
            return null;

        bool isCurved = !IsCurveStraight(rail1) || !IsCurveStraight(rail2);
        if (requireCurvedRails && !isCurved) return null;

        // 中线长度 —— 同心圆弧情形用解析公式，否则用两条导轨长度均值
        double centerlineLength = rp.CenterlineLength;
        double outerLength = Math.Max(lengths[rp.Rail1Index], lengths[rp.Rail2Index]);
        double innerLength = Math.Min(lengths[rp.Rail1Index], lengths[rp.Rail2Index]);
        double arcInnerRadius = 0;
        double arcAngleDegrees = 0;
        string method;
        if (rail1 is Arc a1 && rail2 is Arc a2)
        {
            double sw1 = Math.Abs(a1.EndAngle - a1.StartAngle);
            double sw2 = Math.Abs(a2.EndAngle - a2.StartAngle);
            centerlineLength = FastDimensionMath.ArcCenterlineLength(a1.Radius, sw1, a2.Radius, sw2);
            arcInnerRadius = Math.Min(a1.Radius, a2.Radius);
            arcAngleDegrees = (sw1 + sw2) * 0.5 * 180.0 / Math.PI;
            method = "section-rail-arc";
        }
        else if (isCurved)
        {
            method = "section-rail-spline";
        }
        else
        {
            method = "section-rail-rect";
        }

        // 轨距：圆弧情形用半径差，否则采样取中位数；若截面含两端端帽线，端帽长度优先校准真实厚度。
        double railDist;
        if (rail1 is Arc aa1 && rail2 is Arc aa2)
            railDist = Math.Abs(aa1.Radius - aa2.Radius);
        else
            railDist = SampleRailDistance(rail1, rail2);

        var railUsed = new HashSet<int> { rp.Rail1Index, rp.Rail2Index };
        double capThickness = DetectRailCapThickness(rail1, rail2, curves, railUsed, railDist);
        railDist = FastDimensionMath.ResolveRailDistance(railDist, capThickness);

        var spRail1 = rail1 as Spline;
        var spRail2 = rail2 as Spline;
        bool splineDualRails = isCurved && spRail1 != null && spRail2 != null;
        if (splineDualRails)
        {
            double refinedMid = ComputeDualSplineMidlineLength(spRail1!, spRail2!);
            if (refinedMid > 1.0)
            {
                centerlineLength = refinedMid;
                method = "section-rail-spline-mid";
            }
        }

        if (centerlineLength < 1.0 || railDist < 0.1) return null;

        var (length, width, thickness) =
            FastDimensionMath.AssignDimensions(centerlineLength, railDist, aabbAlongAxis);
        double unfoldedWidth = Math.Max(Math.Abs(railDist), Math.Abs(aabbAlongAxis));

        // 防呆：如果 width 与 length 之间差距 < 0.5mm 且都 < aabbAlongAxis 显著值，
        // 多半是同一边被识别成两条 → 退回 null 让上层 fallback。
        if (length < 1.0 || thickness < 0.1) return null;

        PanelCalculationType panelKind =
            !isCurved ? PanelCalculationType.AABB
            : splineDualRails ? PanelCalculationType.SplinePanel
            : PanelCalculationType.ArcPanel;

        return new Result
        {
            Length = length,
            Width = width,
            Thickness = thickness,
            Method = method,
            DetectedType = panelKind,
            ArcInnerRadius = arcInnerRadius,
            ArcAngleDegrees = arcAngleDegrees,
            ArcOuterLength = isCurved ? outerLength : 0,
            ArcCenterLength = isCurved ? centerlineLength : 0,
            ArcInnerLength = isCurved ? innerLength : 0,
            UnfoldedLength = isCurved ? centerlineLength : 0,
            UnfoldedWidth = isCurved ? unfoldedWidth : 0,
            Success = true
        };
    }

    /// <summary>
    /// 识别组合导轨：一条展开中线可能由多组平行直线 + 一组圆弧/样条导轨组成。
    /// 返回的 length 使用“各段中线长度求和”，而不是只取最长两条曲线。
    /// </summary>
    private static Result TryCompositeRailPath(
        List<Curve> curves, double[] lengths, double aabbAlongAxis, bool requireCurvedRails)
    {
        if (curves.Count < 4) return null;

        var curvePairOpt = PickCurvedRailPair(curves, lengths);
        // #region debug-point A:primary-curved-pair
        DebugReport("A", "FastDimensionService.TryCompositeRailPath:primary", new
        {
            curveCount = curves.Count,
            curves = curves.Select((cv, idx) => new
            {
                idx,
                type = cv?.GetType().Name ?? "null",
                length = idx >= 0 && idx < lengths.Length ? lengths[idx] : 0,
                straight = cv != null && IsCurveStraight(cv),
                closedCurved = cv != null && IsClosedCurvedLoop(cv),
                arcRadius = cv is Arc arc ? arc.Radius : 0,
                arcCenterX = cv is Arc arcX ? arcX.Center.X : 0,
                arcCenterY = cv is Arc arcY ? arcY.Center.Y : 0
            }).ToArray(),
            hasPrimary = curvePairOpt != null,
            primaryCenter = curvePairOpt?.CenterlineLength ?? 0,
            primaryOuter = curvePairOpt?.OuterLength ?? 0,
            primaryInner = curvePairOpt?.InnerLength ?? 0,
            primaryRailDistance = curvePairOpt?.RailDistance ?? 0,
            primaryAngle = curvePairOpt?.AngleDegrees ?? 0,
            primaryAnalytic = curvePairOpt?.AnalyticArc ?? false,
            primaryDualSpline = curvePairOpt?.DualSpline ?? false
        });
        // #endregion
        if (curvePairOpt == null)
            return null;

        var pick = curvePairOpt.Value;
        if (requireCurvedRails && pick.CenterlineLength <= 0) return null;
        if (pick.RailDistance < 0.1) return null;

        var straightSegments = new List<double>();
        var used = new HashSet<int>();
        var curvedPairs = new List<CurvedRailPick>();
        curvedPairs.Add(pick);
        used.Add(pick.Idx1);
        used.Add(pick.Idx2);
        double capThickness = DetectRailCapThickness(curves[pick.Idx1], curves[pick.Idx2], curves, used, pick.RailDistance);
        double railDistance = FastDimensionMath.ResolveRailDistance(pick.RailDistance, capThickness);

        foreach (var extraCurved in PickAdditionalCurvedRailPairs(curves, lengths, railDistance, used))
        {
            curvedPairs.Add(extraCurved);
            used.Add(extraCurved.Idx1);
            used.Add(extraCurved.Idx2);
        }
        // #region debug-point B:extra-curved-pairs
        DebugReport("B", "FastDimensionService.TryCompositeRailPath:extras", new
        {
            targetRailDistance = railDistance,
            curvedPairCount = curvedPairs.Count,
            curvedPairs = curvedPairs.Select(cp => new
            {
                cp.Idx1,
                cp.Idx2,
                cp.CenterlineLength,
                cp.OuterLength,
                cp.InnerLength,
                cp.RailDistance,
                cp.InnerRadius,
                cp.AngleDegrees,
                cp.AnalyticArc,
                cp.DualSpline
            }).ToArray()
        });
        // #endregion

        double curvedCenterTotal = 0;
        double curvedOuterTotal = 0;
        double curvedInnerTotal = 0;
        double minInnerRadius = 0;
        double totalAngleDegrees = 0;
        foreach (var curved in curvedPairs)
        {
            curvedCenterTotal += curved.CenterlineLength;
            curvedOuterTotal += curved.OuterLength;
            curvedInnerTotal += curved.InnerLength;
            if (curved.InnerRadius > 0 && (minInnerRadius <= 0 || curved.InnerRadius < minInnerRadius))
                minInnerRadius = curved.InnerRadius;
            if (curved.AngleDegrees > 0)
                totalAngleDegrees += curved.AngleDegrees;
        }

        double straightOuterTotal = 0;
        double straightInnerTotal = 0;
        var straightPairs = PickStraightRailPairs(curves, lengths, railDistance, used);
        foreach (var pair in straightPairs)
        {
            straightSegments.Add(pair.CenterlineLength);
            straightOuterTotal += pair.OuterLength;
            straightInnerTotal += pair.InnerLength;
            used.Add(pair.Index1);
            used.Add(pair.Index2);
        }

        straightSegments.Sort((a, b) => b.CompareTo(a));
        double straightTotal = 0;
        foreach (var value in straightSegments)
            straightTotal += value;

        double unfolded = straightTotal + curvedCenterTotal;
        double outerTotal = straightOuterTotal + curvedOuterTotal;
        double innerTotal = straightInnerTotal + curvedInnerTotal;
        if (TryComputeRailTotals(curves, lengths, pick, curvedPairs, straightPairs, railDistance, out double rail0Total, out double rail1Total))
        {
            unfolded = (rail0Total + rail1Total) * 0.5;
            outerTotal = Math.Max(rail0Total, rail1Total);
            innerTotal = Math.Min(rail0Total, rail1Total);
        }
        if (unfolded < 1.0) return null;

        var (length, width, thickness) =
            FastDimensionMath.AssignDimensions(unfolded, railDistance, aabbAlongAxis);
        double unfoldedWidth = Math.Max(Math.Abs(railDistance), Math.Abs(aabbAlongAxis));
        if (length < 1.0 || thickness < 0.1) return null;

        string compositeMethod = pick.AnalyticArc
            ? "section-rail-composite-arc"
            : (pick.DualSpline ? "section-rail-composite-spline-mid" : "section-rail-composite-spline");

        return new Result
        {
            Length = length,
            Width = width,
            Thickness = thickness,
            Method = compositeMethod,
            DetectedType = pick.DualSpline ? PanelCalculationType.SplinePanel : PanelCalculationType.ArcPanel,
            ArcInnerRadius = minInnerRadius,
            ArcAngleDegrees = totalAngleDegrees,
            ArcStraightLength1 = straightSegments.Count > 0 ? straightSegments[0] : 0,
            ArcStraightLength2 = straightSegments.Count > 1 ? straightSegments[1] : 0,
            ArcOuterLength = outerTotal,
            ArcCenterLength = unfolded,
            ArcInnerLength = innerTotal,
            UnfoldedLength = unfolded,
            UnfoldedWidth = unfoldedWidth,
            Success = true
        };
    }

    private static CurvedRailPick? PickCurvedRailPair(List<Curve> curves, double[] lengths)
    {
        CurvedRailPick? best = null;
        double bestScore = -1;

        for (int i = 0; i < curves.Count; i++)
        {
            if (IsCurveStraight(curves[i])) continue;
            if (IsClosedCurvedLoop(curves[i])) continue;
            for (int j = i + 1; j < curves.Count; j++)
            {
                if (IsCurveStraight(curves[j])) continue;
                if (IsClosedCurvedLoop(curves[j])) continue;

                double len1 = lengths[i];
                double len2 = lengths[j];
                if (len1 < 5 || len2 < 5) continue;

                double ratio = Math.Min(len1, len2) / Math.Max(len1, len2);
                if (ratio < 0.45) continue;

                if (!TryBuildCurvedRailPick(i, j, curves[i], curves[j], len1, len2, out var candidate))
                    continue;

                double score = candidate.CenterlineLength * ratio;
                if (candidate.AnalyticArc) score *= 1.25;
                if (candidate.DualSpline) score *= 1.08;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return best;
    }

    private readonly struct StraightRailPair
    {
        public int Index1 { get; }
        public int Index2 { get; }
        public double CenterlineLength { get; }
        public double OuterLength { get; }
        public double InnerLength { get; }
        public double Score { get; }

        public StraightRailPair(int index1, int index2, double centerlineLength, double outerLength, double innerLength, double score)
        {
            Index1 = index1;
            Index2 = index2;
            CenterlineLength = centerlineLength;
            OuterLength = outerLength;
            InnerLength = innerLength;
            Score = score;
        }
    }

    private static List<StraightRailPair> PickStraightRailPairs(
        List<Curve> curves, double[] lengths, double railDist, HashSet<int> excluded)
    {
        var candidates = new List<StraightRailPair>();
        for (int i = 0; i < curves.Count; i++)
        {
            if (excluded.Contains(i)) continue;
            if (!TryGetStraightSegment(curves[i], out var s1, out var e1)) continue;

            var d1 = e1 - s1;
            double len1 = d1.Length;
            if (len1 < 5) continue;

            for (int j = i + 1; j < curves.Count; j++)
            {
                if (excluded.Contains(j)) continue;
                if (!TryGetStraightSegment(curves[j], out var s2, out var e2)) continue;

                var d2 = e2 - s2;
                double len2 = d2.Length;
                if (len2 < 5) continue;

                double ratio = Math.Min(len1, len2) / Math.Max(len1, len2);
                if (ratio < 0.55) continue;

                double dot = Math.Abs(d1.GetNormal().DotProduct(d2.GetNormal()));
                if (dot < 0.96) continue;

                var m1 = MidPoint(s1, e1);
                var m2 = MidPoint(s2, e2);
                double midDistance = m1.DistanceTo(m2);
                if (midDistance < railDist * 0.35 || midDistance > Math.Max(railDist * 3.5, railDist + 80.0))
                    continue;

                double centerline = (len1 + len2) * 0.5;
                double distScore = 1.0 / (1.0 + Math.Abs(midDistance - railDist));
                candidates.Add(new StraightRailPair(i, j, centerline, Math.Max(len1, len2), Math.Min(len1, len2), centerline * ratio * distScore));
            }
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var selected = new List<StraightRailPair>();
        var used = new HashSet<int>();
        foreach (var c in candidates)
        {
            if (used.Contains(c.Index1) || used.Contains(c.Index2)) continue;
            selected.Add(c);
            used.Add(c.Index1);
            used.Add(c.Index2);
        }

        return selected;
    }

    private static bool TryComputeRailTotals(
        List<Curve> curves,
        double[] lengths,
        CurvedRailPick primary,
        IReadOnlyList<CurvedRailPick> curvedPairs,
        IReadOnlyList<StraightRailPair> straightPairs,
        double railDistance,
        out double rail0Total,
        out double rail1Total)
    {
        rail0Total = 0;
        rail1Total = 0;

        var selected = new HashSet<int> { primary.Idx1, primary.Idx2 };
        foreach (var curved in curvedPairs)
        {
            selected.Add(curved.Idx1);
            selected.Add(curved.Idx2);
        }

        foreach (var straight in straightPairs)
        {
            selected.Add(straight.Index1);
            selected.Add(straight.Index2);
        }

        if (selected.Count < 2)
            return false;

        var railSide = new Dictionary<int, int>
        {
            [primary.Idx1] = 0,
            [primary.Idx2] = 1
        };

        double tolerance = Math.Max(2.0, Math.Abs(railDistance) * 0.4);
        int passLimit = selected.Count * 3;
        for (int pass = 0; pass < passLimit; pass++)
        {
            bool changed = false;
            foreach (var curved in curvedPairs)
            {
                if (TryAssignRailSide(curves, curved.Idx1, curved.Idx2, railSide, tolerance))
                    changed = true;
            }

            foreach (var straight in straightPairs)
            {
                if (TryAssignRailSide(curves, straight.Index1, straight.Index2, railSide, tolerance))
                    changed = true;
            }

            if (!changed)
                break;
        }

        if (selected.Any(idx => !railSide.ContainsKey(idx)))
            return false;

        foreach (int idx in selected)
        {
            if (railSide[idx] == 0)
                rail0Total += lengths[idx];
            else
                rail1Total += lengths[idx];
        }

        return rail0Total > 0.1 && rail1Total > 0.1;
    }

    private static bool TryAssignRailSide(
        List<Curve> curves,
        int indexA,
        int indexB,
        Dictionary<int, int> railSide,
        double tolerance)
    {
        bool hasA = railSide.TryGetValue(indexA, out int sideA);
        bool hasB = railSide.TryGetValue(indexB, out int sideB);
        if (hasA && hasB)
            return false;

        int? connectedA = ResolveConnectedRailSide(curves, indexA, indexB, railSide, tolerance);
        int? connectedB = ResolveConnectedRailSide(curves, indexB, indexA, railSide, tolerance);

        bool changed = false;
        if (!hasA && connectedA.HasValue)
        {
            sideA = connectedA.Value;
            railSide[indexA] = sideA;
            hasA = true;
            changed = true;
        }

        if (!hasB && connectedB.HasValue)
        {
            sideB = connectedB.Value;
            railSide[indexB] = sideB;
            hasB = true;
            changed = true;
        }

        if (hasA && !hasB)
        {
            railSide[indexB] = 1 - sideA;
            changed = true;
        }
        else if (hasB && !hasA)
        {
            railSide[indexA] = 1 - sideB;
            changed = true;
        }

        return changed;
    }

    private static int? ResolveConnectedRailSide(
        List<Curve> curves,
        int index,
        int counterpartIndex,
        Dictionary<int, int> railSide,
        double tolerance)
    {
        foreach (var entry in railSide)
        {
            if (entry.Key == counterpartIndex)
                continue;

            if (CurvesTouch(curves[index], curves[entry.Key], tolerance))
                return entry.Value;
        }

        return null;
    }

    private static bool CurvesTouch(Curve first, Curve second, double tolerance)
    {
        if (first == null || second == null)
            return false;

        try
        {
            var endpointsA = new[] { first.StartPoint, first.EndPoint };
            var endpointsB = new[] { second.StartPoint, second.EndPoint };
            foreach (var pointA in endpointsA)
            {
                foreach (var pointB in endpointsB)
                {
                    if (pointA.DistanceTo(pointB) <= tolerance)
                        return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    internal static void DebugReport(string hypothesisId, string message, object data)
    {
        try
        {
            string debugDir = ResolveDebugDirectory();
            string envPath = ResolveDebugEnvPath(debugDir);
            string url = "http://127.0.0.1:7777/event";
            string sessionId = System.IO.Path.GetFileNameWithoutExtension(envPath);
            if (!string.IsNullOrWhiteSpace(envPath) && System.IO.File.Exists(envPath))
            {
                string env = System.IO.File.ReadAllText(envPath);
                foreach (var line in env.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("DEBUG_SERVER_URL=", StringComparison.OrdinalIgnoreCase))
                        url = line.Substring("DEBUG_SERVER_URL=".Length).Trim();
                    else if (line.StartsWith("DEBUG_SESSION_ID=", StringComparison.OrdinalIgnoreCase))
                        sessionId = line.Substring("DEBUG_SESSION_ID=".Length).Trim();
                }
            }

            string payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                sessionId,
                runId = "pre-fix",
                hypothesisId,
                location = "FastDimensionService",
                msg = "[DEBUG] " + message,
                data,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            AppendDebugPayload(debugDir, sessionId, payload);
            TryPostDebugPayload(url, payload);
        }
        catch
        {
        }
    }

    private static string ResolveDebugEnvPath(string debugDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(debugDir) || !System.IO.Directory.Exists(debugDir))
                return System.IO.Path.Combine(debugDir ?? ".dbg", "s-shape-arc.env");

            var envFiles = System.IO.Directory.GetFiles(debugDir, "*.env");
            if (envFiles.Length == 0)
                return System.IO.Path.Combine(debugDir, "s-shape-arc.env");

            return envFiles
                .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                .First();
        }
        catch
        {
            return System.IO.Path.Combine(debugDir ?? ".dbg", "s-shape-arc.env");
        }
    }

    private static bool IsBetterCurvedCandidate(Result candidate, Result currentBest)
    {
        if (candidate == null)
            return false;
        if (currentBest == null)
            return true;

        if (candidate.Length > currentBest.Length + 0.5)
            return true;
        if (currentBest.Length > candidate.Length + 0.5)
            return false;

        if (candidate.Thickness + 0.5 < currentBest.Thickness)
            return true;
        if (currentBest.Thickness + 0.5 < candidate.Thickness)
            return false;

        return candidate.ArcCenterLength > currentBest.ArcCenterLength + 0.5;
    }

    private static string ResolveDebugDirectory()
    {
        try
        {
            string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string assemblyDir = System.IO.Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrWhiteSpace(assemblyDir))
            {
                string debugDir = System.IO.Path.Combine(assemblyDir, ".dbg");
                System.IO.Directory.CreateDirectory(debugDir);
                return debugDir;
            }
        }
        catch
        {
        }

        try
        {
            string debugDir = System.IO.Path.Combine(Environment.CurrentDirectory, ".dbg");
            System.IO.Directory.CreateDirectory(debugDir);
            return debugDir;
        }
        catch
        {
            return ".dbg";
        }
    }

    private static void AppendDebugPayload(string debugDir, string sessionId, string payload)
    {
        try
        {
            string logPath = System.IO.Path.Combine(debugDir, $"trae-debug-log-{sessionId}.ndjson");
            lock (DebugFileSync)
            {
                System.IO.File.AppendAllText(logPath, payload + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    private static void TryPostDebugPayload(string url, string payload)
    {
        try
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    using var client = new System.Net.Http.HttpClient();
                    client.Timeout = TimeSpan.FromMilliseconds(150);
                    using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                    await client.PostAsync(url, content).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
    }

    private static List<CurvedRailPick> PickAdditionalCurvedRailPairs(
        List<Curve> curves, double[] lengths, double targetRailDistance, HashSet<int> used)
    {
        var candidates = new List<(CurvedRailPick Pick, double Score)>();
        for (int i = 0; i < curves.Count; i++)
        {
            if (used.Contains(i) || IsCurveStraight(curves[i]) || IsClosedCurvedLoop(curves[i]))
                continue;

            for (int j = i + 1; j < curves.Count; j++)
            {
                if (used.Contains(j) || IsCurveStraight(curves[j]) || IsClosedCurvedLoop(curves[j]))
                    continue;

                double len1 = lengths[i];
                double len2 = lengths[j];
                if (len1 < 5 || len2 < 5)
                    continue;

                double ratio = Math.Min(len1, len2) / Math.Max(len1, len2);
                if (ratio < 0.45)
                    continue;

                if (!TryBuildCurvedRailPick(i, j, curves[i], curves[j], len1, len2, out var pick))
                    continue;

                if (pick.RailDistance < 0.1)
                    continue;

                double railDistDelta = Math.Abs(pick.RailDistance - targetRailDistance);
                double tolerance = Math.Max(6.0, targetRailDistance * 0.45);
                if (railDistDelta > tolerance)
                    continue;

                double score = pick.CenterlineLength * ratio / (1.0 + railDistDelta);
                candidates.Add((pick, score));
            }
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var selected = new List<CurvedRailPick>();
        foreach (var candidate in candidates)
        {
            if (used.Contains(candidate.Pick.Idx1) || used.Contains(candidate.Pick.Idx2))
                continue;
            selected.Add(candidate.Pick);
            used.Add(candidate.Pick.Idx1);
            used.Add(candidate.Pick.Idx2);
        }

        return selected;
    }

    private static bool TryBuildCurvedRailPick(int idx1, int idx2, Curve curve1, Curve curve2, double len1, double len2, out CurvedRailPick pick)
    {
        pick = default;
        double centerline;
        double railDist;
        double innerRadius = 0;
        double angleDegrees = 0;
        bool analyticArc = false;
        bool dualSpline = false;

        if (curve1 is Arc a1 && curve2 is Arc a2)
        {
            double centerDistance = a1.Center.DistanceTo(a2.Center);
            double radiusDelta = Math.Abs(a1.Radius - a2.Radius);
            if (radiusDelta < 0.1 || centerDistance > Math.Max(3.0, radiusDelta * 0.25))
                return false;

            double sw1 = ResolveArcSweepRadians(a1, len1);
            double sw2 = ResolveArcSweepRadians(a2, len2);
            if (Math.Min(sw1, sw2) / Math.Max(sw1, sw2) < 0.75)
                return false;

            centerline = FastDimensionMath.ArcCenterlineLength(a1.Radius, sw1, a2.Radius, sw2);
            railDist = radiusDelta;
            innerRadius = Math.Min(a1.Radius, a2.Radius);
            angleDegrees = (sw1 + sw2) * 0.5 * 180.0 / Math.PI;
            analyticArc = true;
        }
        else if (curve1 is Spline s1 && curve2 is Spline s2)
        {
            railDist = SampleRailDistance(s1, s2);
            centerline = ComputeDualSplineMidlineLength(s1, s2);
            if (centerline < 1.0)
                centerline = (len1 + len2) * 0.5;
            dualSpline = true;
        }
        else
        {
            centerline = (len1 + len2) * 0.5;
            railDist = SampleRailDistance(curve1, curve2);
        }

        if (centerline < 1.0 || railDist < 0.1)
            return false;

        pick = new CurvedRailPick(
            idx1,
            idx2,
            centerline,
            Math.Max(len1, len2),
            Math.Min(len1, len2),
            railDist,
            innerRadius,
            angleDegrees,
            analyticArc,
            dualSpline);
        return true;
    }

    /// <summary>
    /// 旧的「主弧定轴 + 投影 BBox」+「2D PCA 兜底」算法。
    /// 仅在 <see cref="TryRailPair"/> 失败时启用（普通异形多边形板）。
    /// 返回时把 <paramref name="aabbAlongAxis"/> 当 thickness。
    /// </summary>
    private static Result TryAxisProjected(
        List<Curve> curves, FastDimensionMath.Vec3 normal, FastDimensionMath.Vec3 origin,
        double aabbAlongAxis)
    {
        var pts = new List<FastDimensionMath.Vec3>(64);
        var arcCenters = new List<FastDimensionMath.Vec3>();
        var arcMidPts = new List<FastDimensionMath.Vec3>();
        var arcRadii = new List<double>();
        var arcSweeps = new List<double>();
        double straightTotal = 0;
        double curvedTotal = 0;
        bool hasClosedCurvedLoop = false;
        foreach (var cv in curves)
        {
            SampleCurve(cv, pts, arcCenters, arcMidPts, arcRadii, arcSweeps);
            double len = SafeCurveLength(cv);
            if (IsCurveStraight(cv))
            {
                straightTotal += len;
            }
            else
            {
                curvedTotal += len;
                if (IsClosedCurvedLoop(cv))
                    hasClosedCurvedLoop = true;
            }
        }
        if (pts.Count < 3) return null;

        FastDimensionMath.Vec3 lengthAxis, widthAxis;
        string method;
        int dom = FastDimensionMath.PickDominantArc(arcRadii, arcSweeps);
        bool allowArcProjection = ShouldUseArcProjection(dom, arcSweeps, straightTotal, curvedTotal, hasClosedCurvedLoop);
        if (allowArcProjection)
        {
            var ax = FastDimensionMath.BuildAxesFromArc(arcCenters[dom], arcMidPts[dom], normal);
            if (ax.HasValue)
            {
                lengthAxis = ax.Value.lengthAxis;
                widthAxis = ax.Value.widthAxis;
                method = "section-arc";
            }
            else
            {
                var pca = FastDimensionMath.ComputePca2D(pts, origin, normal);
                lengthAxis = pca.lengthAxis;
                widthAxis = pca.widthAxis;
                method = "section-pca";
            }
        }
        else
        {
            var pca = FastDimensionMath.ComputePca2D(pts, origin, normal);
            lengthAxis = pca.lengthAxis;
            widthAxis = pca.widthAxis;
            method = "section-pca";
        }

        var ext2 = FastDimensionMath.ProjectExtentsInPlane(pts, origin, lengthAxis, widthAxis);
        double e1 = ext2.UMax - ext2.UMin;
        double e2 = ext2.VMax - ext2.VMin;
        if (e1 < 0.5 && e2 < 0.5) return null;

        double inSectionLong = Math.Max(e1, e2);
        double inSectionShort = Math.Min(e1, e2);
        // 异形板退化：在截面平面内 PCA 给的就是 length / width；aabbAlongAxis = thickness
        return new Result
        {
            Length = inSectionLong,
            Width = inSectionShort,
            Thickness = aabbAlongAxis,
            Method = method,
            DetectedType = method == "section-arc"
                ? PanelCalculationType.ArcPanel
                : PanelCalculationType.Irregular,
            Success = true
        };
    }

    private static bool ShouldUseArcProjection(
        int dominantArcIndex,
        IReadOnlyList<double> arcSweeps,
        double straightTotal,
        double curvedTotal,
        bool hasClosedCurvedLoop)
    {
        if (dominantArcIndex < 0)
            return false;

        double dominantSweep = arcSweeps != null && dominantArcIndex < arcSweeps.Count
            ? Math.Abs(arcSweeps[dominantArcIndex])
            : 0;

        bool fullLoopLike = dominantSweep >= Math.PI * 1.85;
        if (fullLoopLike && straightTotal > 20.0)
            return false;

        // 孔洞/盲孔的截面常表现为“闭合曲线 + 外部长直边”；
        // 这类内部特征不应把整块矩形板升级成 ArcPanel。
        if (hasClosedCurvedLoop && straightTotal > 1.0)
            return false;

        // 只有当曲线在截面轮廓中占明显主导时，才用主圆弧决定整板类型。
        if (curvedTotal < Math.Max(40.0, straightTotal * 0.6))
            return false;

        return true;
    }

    private static void FinalizeRoundAndCompose(Result r, double[] aabb)
    {
        r.Length = Math.Round(r.Length, 2);
        r.Width = Math.Round(r.Width, 2);
        r.Thickness = Math.Round(r.Thickness, 2);
        r.ArcInnerRadius = Math.Round(r.ArcInnerRadius, 2);
        r.ArcAngleDegrees = Math.Round(r.ArcAngleDegrees, 2);
        r.ArcStraightLength1 = Math.Round(r.ArcStraightLength1, 2);
        r.ArcStraightLength2 = Math.Round(r.ArcStraightLength2, 2);
        r.UnfoldedLength = Math.Round(r.UnfoldedLength, 2);
        r.UnfoldedWidth = Math.Round(r.UnfoldedWidth, 2);
        r.ArcOuterLength = Math.Round(r.ArcOuterLength, 2);
        r.ArcCenterLength = Math.Round(r.ArcCenterLength, 2);
        r.ArcInnerLength = Math.Round(r.ArcInnerLength, 2);
        r.Reason = $"截面算法 {r.Method}: L={r.Length} W={r.Width} T={r.Thickness}, 类型={r.DetectedType}"
                 + (r.UnfoldedLength > 0 ? $", 展开长={r.UnfoldedLength}" : "")
                 + (r.UnfoldedWidth > 0 ? $", 展开宽={r.UnfoldedWidth}" : "")
                 + (r.ArcOuterLength > 0 ? $", 外线={r.ArcOuterLength}" : "")
                 + (r.ArcCenterLength > 0 ? $", 中线={r.ArcCenterLength}" : "")
                 + (r.ArcInnerLength > 0 ? $", 内线={r.ArcInnerLength}" : "")
                 + (r.ArcInnerRadius > 0 ? $", 内径={r.ArcInnerRadius}, 角度={r.ArcAngleDegrees}°" : "");
    }

    private static int SmallestAxisIndex(double[] aabb)
    {
        int idx = 0;
        if (aabb[1] < aabb[idx]) idx = 1;
        if (aabb[2] < aabb[idx]) idx = 2;
        return idx;
    }

    // ─────────── 工具：曲线长度 / 直曲判断 / 轨距采样 / 通用采样 ───────────

    private static double SafeCurveLength(Curve cv)
    {
        try
        {
            return cv switch
            {
                Polyline pl => pl.Length,
                _ => Math.Abs(cv.GetDistanceAtParameter(cv.EndParam)
                            - cv.GetDistanceAtParameter(cv.StartParam))
            };
        }
        catch
        {
            try { return cv.StartPoint.DistanceTo(cv.EndPoint); }
            catch { return 0; }
        }
    }

    /// <summary>
    /// 判断曲线在几何意义上是否为「直线」。Line 与无 bulge 的 Polyline 算直线；
    /// Arc / Spline / 有 bulge 的 Polyline / Circle / Ellipse 全部算曲线。
    /// </summary>
    private static bool IsCurveStraight(Curve cv)
    {
        if (cv == null) return true;
        if (cv is Line) return true;
        if (cv is Polyline pl)
        {
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                try { if (Math.Abs(pl.GetBulgeAt(i)) > 1e-6) return false; } catch { }
            }
            return true;
        }
        return false; // Arc, Spline, Circle, Ellipse 等
    }

    private static bool IsClosedCurvedLoop(Curve cv)
    {
        if (cv == null || IsCurveStraight(cv))
            return false;

        try { return cv.Closed; }
        catch { return false; }
    }

    private static bool TryGetStraightSegment(Curve cv, out Point3d start, out Point3d end)
    {
        start = Point3d.Origin;
        end = Point3d.Origin;

        try
        {
            if (cv is Line line)
            {
                start = line.StartPoint;
                end = line.EndPoint;
                return start.DistanceTo(end) > 1e-6;
            }

            if (cv is Polyline pl && IsCurveStraight(pl) && pl.NumberOfVertices >= 2)
            {
                start = pl.GetPoint3dAt(0);
                end = pl.GetPoint3dAt(pl.NumberOfVertices - 1);
                return start.DistanceTo(end) > 1e-6;
            }
        }
        catch { }

        return false;
    }

    private static double ResolveArcSweepRadians(Arc arc, double fallbackLength)
    {
        if (arc == null)
            return 0;

        double radius = Math.Abs(arc.Radius);
        if (radius > 1e-6 && fallbackLength > 1e-6)
        {
            double byLength = Math.Abs(fallbackLength) / radius;
            if (byLength > 1e-6 && byLength <= Math.PI * 2.0 + 1e-3)
                return byLength;
        }

        double byAngles = Math.Abs(arc.EndAngle - arc.StartAngle);
        while (byAngles > Math.PI * 2.0)
            byAngles -= Math.PI * 2.0;
        if (byAngles > Math.PI)
            byAngles = Math.PI * 2.0 - byAngles;
        return Math.Abs(byAngles);
    }

    private static Point3d MidPoint(Point3d a, Point3d b)
    {
        return new Point3d(
            (a.X + b.X) * 0.5,
            (a.Y + b.Y) * 0.5,
            (a.Z + b.Z) * 0.5);
    }

    private static double DetectRailCapThickness(
        Curve rail1, Curve rail2, List<Curve> curves, HashSet<int> excluded, double approximateRailDistance)
    {
        if (rail1 == null || rail2 == null || curves == null)
            return 0;

        var candidates = new List<double>();
        double tolerance = Math.Max(1.0, Math.Min(10.0, Math.Abs(approximateRailDistance) * 0.35));

        for (int i = 0; i < curves.Count; i++)
        {
            if (excluded != null && excluded.Contains(i)) continue;
            if (!TryGetStraightSegment(curves[i], out var start, out var end)) continue;

            if (ConnectsRailEnds(start, end, rail1, rail2, tolerance))
                candidates.Add(start.DistanceTo(end));
        }

        if (candidates.Count == 0)
            return 0;

        candidates.Sort();
        return candidates[candidates.Count / 2];
    }

    private static bool ConnectsRailEnds(Point3d start, Point3d end, Curve rail1, Curve rail2, double tolerance)
    {
        try
        {
            var a = new[] { rail1.StartPoint, rail1.EndPoint };
            var b = new[] { rail2.StartPoint, rail2.EndPoint };

            foreach (var pa in a)
            foreach (var pb in b)
            {
                if (start.DistanceTo(pa) <= tolerance && end.DistanceTo(pb) <= tolerance)
                    return true;
                if (start.DistanceTo(pb) <= tolerance && end.DistanceTo(pa) <= tolerance)
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// 双样条导轨：按归一化弧长 u∈[0,1] 在两条曲线上取对应点，连线中点构成折线路径，累加分段长度。
    /// 同时尝试第二条曲线参数正向与反向各一次，取较短者以对齐可能反向的轨对。
    /// </summary>
    private static double ComputeDualSplineMidlineLength(Spline spline1, Spline spline2)
    {
        if (spline1 == null || spline2 == null) return 0;
        try
        {
            double len1 = SafeCurveLength(spline1);
            double len2 = SafeCurveLength(spline2);
            if (len1 < 1e-3 || len2 < 1e-3) return 0;

            double forward = AccumulateMidlineBetweenCurves(spline1, spline2, flipSecondNormalized: false);
            double flipped = AccumulateMidlineBetweenCurves(spline1, spline2, flipSecondNormalized: true);
            double selected = ChooseMostPlausibleMidline(len1, len2, forward, flipped);
            // #region debug-point G:spline-midline
            DebugReport("G", "FastDimensionService.ComputeDualSplineMidlineLength:paired-midline", new
            {
                len1,
                len2,
                forward,
                flipped,
                selected
            });
            // #endregion
            return selected;
        }
        catch
        {
            return 0;
        }
    }

    private static double ChooseMostPlausibleMidline(double len1, double len2, double forward, double flipped)
    {
        bool okF = forward > 1e-3;
        bool okR = flipped > 1e-3;
        if (!okF && !okR) return 0;
        if (!okF) return flipped;
        if (!okR) return forward;

        double reference = (Math.Abs(len1) + Math.Abs(len2)) * 0.5;
        double scoreF = Math.Abs(forward - reference);
        double scoreR = Math.Abs(flipped - reference);
        if (Math.Abs(scoreF - scoreR) <= 1e-3)
            return Math.Max(forward, flipped);

        return scoreF <= scoreR ? forward : flipped;
    }

    private static double AccumulateMidlineBetweenCurves(Curve curveA, Curve curveB, bool flipSecondNormalized)
    {
        double lenA = SafeCurveLength(curveA);
        double lenB = SafeCurveLength(curveB);
        if (lenA < 1e-6 || lenB < 1e-6) return 0;

        int segments = ComputeMidlineSegmentsCount(Math.Max(lenA, lenB));
        double sum = 0;
        Point3d? prevMid = null;

        for (int k = 0; k <= segments; k++)
        {
            double u = k / (double)segments;
            if (!TryPointAtNormalizedArclength(curveA, u, out Point3d pa)) continue;
            double v = flipSecondNormalized ? (1.0 - u) : u;
            if (!TryPointAtNormalizedArclength(curveB, v, out Point3d pb)) continue;

            var mid = MidPoint(pa, pb);
            if (prevMid.HasValue)
                sum += prevMid.Value.DistanceTo(mid);
            prevMid = mid;
        }

        return sum;
    }

    private static int ComputeMidlineSegmentsCount(double maxCurveLength)
    {
        return Math.Clamp((int)Math.Ceiling(maxCurveLength / 12.0), 48, 320);
    }

    /// <summary>参数 u 为归一化弧长比 [0,1]，沿曲线度量位置（优先 <see cref="Curve.GetDistanceAtParameter"/> + <see cref="Curve.GetPointAtDist"/>）。</summary>
    private static bool TryPointAtNormalizedArclength(Curve curve, double u, out Point3d point)
    {
        point = Point3d.Origin;
        if (curve == null) return false;
        try
        {
            if (u <= 1e-9)
            {
                point = curve.StartPoint;
                return true;
            }

            if (u >= 1 - 1e-9)
            {
                point = curve.EndPoint;
                return true;
            }

            double d0 = curve.GetDistanceAtParameter(curve.StartParam);
            double d1 = curve.GetDistanceAtParameter(curve.EndParam);
            double dt = d0 + u * (d1 - d0);
            point = curve.GetPointAtDist(dt);
            return true;
        }
        catch
        {
            try
            {
                double tp = curve.StartParam + u * (curve.EndParam - curve.StartParam);
                point = curve.GetPointAtParameter(tp);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 用 5 等分采样估算两条导轨之间的法向距离（取中位数，避免端点畸变）。
    /// </summary>
    private static double SampleRailDistance(Curve rail1, Curve rail2)
    {
        int sampleCount = 9;
        try
        {
            double l = SafeCurveLength(rail1);
            if (l > 1e-6)
                sampleCount = Math.Clamp((int)Math.Ceiling(l / 200.0), 7, 17);
        }
        catch { }

        var distances = new List<double>(sampleCount);
        try
        {
            double sp = rail1.StartParam, ep = rail1.EndParam;
            for (int i = 1; i <= sampleCount; i++)
            {
                double t = i / (double)(sampleCount + 1);
                try
                {
                    var p = rail1.GetPointAtParameter(sp + (ep - sp) * t);
                    Point3d closest;
                    try { closest = rail2.GetClosestPointTo(p, false); }
                    catch { continue; }
                    distances.Add(p.DistanceTo(closest));
                }
                catch { }
            }
        }
        catch { }

        if (distances.Count == 0)
        {
            try
            {
                double d1 = rail1.StartPoint.DistanceTo(rail2.GetClosestPointTo(rail1.StartPoint, false));
                double d2 = rail1.EndPoint.DistanceTo(rail2.GetClosestPointTo(rail1.EndPoint, false));
                return Math.Max(d1, d2);
            }
            catch { return 0; }
        }
        distances.Sort();
        double median = distances[distances.Count / 2];
        if (rail1 is Spline || rail2 is Spline)
        {
            // #region debug-point H:spline-rail-distance
            DebugReport("H", "FastDimensionService.SampleRailDistance:spline-distance", new
            {
                rail1Type = rail1?.GetType().Name ?? "",
                rail2Type = rail2?.GetType().Name ?? "",
                sampleCount,
                distances = distances.Take(8).ToArray(),
                median
            });
            // #endregion
        }
        return median;
    }

    private static void SampleCurve(
        Curve cv,
        List<FastDimensionMath.Vec3> pts,
        List<FastDimensionMath.Vec3> arcCenters,
        List<FastDimensionMath.Vec3> arcMidPts,
        List<double> arcRadii,
        List<double> arcSweeps)
    {
        try
        {
            switch (cv)
            {
                case Line line:
                    AddPt(pts, line.StartPoint);
                    AddPt(pts, line.EndPoint);
                    break;

                case Arc arc:
                {
                    AddPt(pts, arc.StartPoint);
                    AddPt(pts, arc.EndPoint);
                    double sp = arc.StartParam;
                    double ep = arc.EndParam;
                    double sweep = Math.Abs(ep - sp);
                    Point3d mid = SafePtAt(arc, sp + sweep * 0.5);
                    AddPt(pts, mid);
                    for (int i = 1; i < 8; i++)
                        AddPt(pts, SafePtAt(arc, sp + sweep * i / 8.0));
                    arcCenters.Add(new FastDimensionMath.Vec3(arc.Center.X, arc.Center.Y, arc.Center.Z));
                    arcMidPts.Add(new FastDimensionMath.Vec3(mid.X, mid.Y, mid.Z));
                    arcRadii.Add(arc.Radius);
                    arcSweeps.Add(sweep);
                    break;
                }

                case Circle circ:
                {
                    var c = circ.Center;
                    double r = circ.Radius;
                    var n = circ.Normal.GetNormal();
                    Vector3d e1 = Vector3d.XAxis;
                    if (Math.Abs(n.X) > 0.9) e1 = Vector3d.YAxis;
                    Vector3d ax1 = (e1 - n * e1.DotProduct(n)).GetNormal();
                    Vector3d ax2 = n.CrossProduct(ax1).GetNormal();
                    Point3d? mid = null;
                    for (int i = 0; i < 16; i++)
                    {
                        double t = 2 * Math.PI * i / 16.0;
                        var p = c + ax1 * (Math.Cos(t) * r) + ax2 * (Math.Sin(t) * r);
                        if (i == 0) mid = p;
                        AddPt(pts, p);
                    }
                    arcCenters.Add(new FastDimensionMath.Vec3(c.X, c.Y, c.Z));
                    if (mid.HasValue)
                        arcMidPts.Add(new FastDimensionMath.Vec3(mid.Value.X, mid.Value.Y, mid.Value.Z));
                    else
                        arcMidPts.Add(new FastDimensionMath.Vec3(c.X + r, c.Y, c.Z));
                    arcRadii.Add(r);
                    arcSweeps.Add(2 * Math.PI);
                    break;
                }

                case Polyline pl:
                {
                    int n = pl.NumberOfVertices;
                    for (int i = 0; i < n; i++)
                        try { AddPt(pts, pl.GetPoint3dAt(i)); } catch { }
                    if (pl.HasBulges)
                    {
                        try
                        {
                            double total = pl.Length;
                            int samples = Math.Max(16, n * 4);
                            for (int i = 1; i < samples; i++)
                                try { AddPt(pts, pl.GetPointAtDist(total * i / samples)); } catch { }
                            for (int i = 0; i < n; i++)
                            {
                                double bulge;
                                try { bulge = pl.GetBulgeAt(i); } catch { continue; }
                                if (Math.Abs(bulge) < 1e-6) continue;
                                try
                                {
                                    var cseg = pl.GetArcSegmentAt(i);
                                    var center = cseg.Center;
                                    double r = cseg.Radius;
                                    double sweep = Math.Abs(cseg.EndAngle - cseg.StartAngle);
                                    double midAngle = (cseg.StartAngle + cseg.EndAngle) * 0.5;
                                    Point3d midOnArc;
                                    try { midOnArc = cseg.EvaluatePoint(midAngle); }
                                    catch { midOnArc = center; }
                                    arcCenters.Add(new FastDimensionMath.Vec3(center.X, center.Y, center.Z));
                                    arcMidPts.Add(new FastDimensionMath.Vec3(midOnArc.X, midOnArc.Y, midOnArc.Z));
                                    arcRadii.Add(r);
                                    arcSweeps.Add(sweep);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                    break;
                }

                case Spline spl:
                {
                    try
                    {
                        double sp = spl.StartParam, ep = spl.EndParam;
                        int samples = Math.Clamp((int)Math.Ceiling(SafeCurveLength(spl) / 20.0), 32, 192);
                        for (int i = 0; i <= samples; i++)
                            try { AddPt(pts, spl.GetPointAtParameter(sp + (ep - sp) * i / (double)samples)); } catch { }
                    }
                    catch { AddPt(pts, cv.StartPoint); AddPt(pts, cv.EndPoint); }
                    break;
                }

                default:
                {
                    try { AddPt(pts, cv.StartPoint); AddPt(pts, cv.EndPoint); } catch { }
                    break;
                }
            }
        }
        catch { /* 任何曲线异常都吞掉 */ }
    }

    private static Point3d SafePtAt(Curve cv, double param)
    {
        try { return cv.GetPointAtParameter(param); }
        catch
        {
            try { return cv.StartPoint; } catch { return Point3d.Origin; }
        }
    }

    private static void AddPt(List<FastDimensionMath.Vec3> pts, Point3d p)
    {
        pts.Add(new FastDimensionMath.Vec3(p.X, p.Y, p.Z));
    }
}
