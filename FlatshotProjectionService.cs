using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin;

/// <summary>
/// FLATSHOT 风格的正交投影服务：
/// 假设输入的 3D 实体已通过 BZBJ 摆正到 XY 平面，
/// 该服务沿 -Z 方向把可见外轮廓与内孔轮廓提取为零厚度闭合 LWPolyline。
///
/// 输出契约：
///   1) 多段线 Closed=true、Elevation=0、ConstantWidth=0；
///   2) 外轮廓绕向 CCW、内孔绕向 CW（与 AutoCAD HATCH 边界约定一致）；
///   3) 每个板件的轮廓集合归一到原点 (0,0,0) 1:1 比例，方便排版算法直接放置。
/// </summary>
public static class FlatshotProjectionService
{
    public sealed class ProjectionResult
    {
        public bool Success { get; set; }
        public string Reason { get; set; } = "";
        /// <summary>外轮廓（局部坐标，已平移到 minX=0, minY=0）</summary>
        public List<Pt2d> OuterLoop { get; set; } = new();
        /// <summary>内孔轮廓（同坐标系）</summary>
        public List<List<Pt2d>> InnerLoops { get; set; } = new();
        /// <summary>归一化后的宽 (X方向)</summary>
        public double Width { get; set; }
        /// <summary>归一化后的高 (Y方向)</summary>
        public double Height { get; set; }
        /// <summary>提取过程中链接成功的边段比例（用于成功率统计）</summary>
        public double EdgeChainRate { get; set; }
        /// <summary>验证：外轮廓自交</summary>
        public bool OuterSelfIntersected { get; set; }
        /// <summary>验证：法向夹角（通过 BZBJ 后应 < 0.001°）</summary>
        public double NormalAngleToZDeg { get; set; }
    }

    private const int ArcSamples = 128;
    private const int SplineSamples = 192;
    private const int MaxExplodeDepth = 6;
    /// <summary>
    /// 链接容差档位。最大严格控制在 1mm 以内：再大就会"桥接"两段物理上不相连的边
    /// （典型场景：薄板内孔距外边缘只有 2~3mm 时，大容差会把内孔顶边并到外边缘上）
    /// </summary>
    private static readonly double[] ChainTolerances = { 0.02, 0.1, 0.3, 1.0 };

    /// <summary>
    /// 对实体执行 FLATSHOT 投影。要求实体的厚轴已与 +Z 平行（建议先调用 BzbjAlignmentService）。
    ///
    /// 策略链（每一级失败都自动降级）：
    ///   1) 截面切片：solid.GetSection(z=zMid) → Region → 边界曲线 → 链接闭环
    ///      用 ACAD 几何内核直接出线，最稳；天然支持样条 / 圆弧 / 倾斜板件
    ///   2) 顶/底面 Explode + Best-of-N 链接：跨多容差挑最优结果
    ///   3) 全部边段 Explode + Best-of-N：把侧面边也丢进来一起链接（兜底）
    /// 输出再做 BBox 校验：与实体几何包围盒比，宽高偏差 ≤10~15%
    /// </summary>
    public static ProjectionResult Project(Solid3d solid)
    {
        if (solid == null || solid.IsErased)
            return new ProjectionResult { Reason = "实体无效" };

        Solid3d clone = null;
        try
        {
            clone = solid.Clone() as Solid3d;
            if (clone == null)
                return new ProjectionResult { Reason = "实体无法克隆" };

            return ProjectCore(clone, disposeWork: false);
        }
        finally
        {
            try { clone?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 对已摆正到 XY 的克隆体直接投影，避免 OutlineService 再次克隆同一实体。
    /// </summary>
    public static ProjectionResult ProjectAligned(Solid3d alignedSolid)
    {
        if (alignedSolid == null || alignedSolid.IsErased)
            return new ProjectionResult { Reason = "实体无效" };

        return ProjectCore(alignedSolid, disposeWork: false);
    }

    private static ProjectionResult ProjectCore(Solid3d work, bool disposeWork)
    {
        var result = new ProjectionResult();
        var explodedToDispose = new List<DBObject>();
        try
        {
            PopulateAlignmentDiagnostic(work, result);

            var ext = ((Entity)work).GeometricExtents;
            double zMin = ext.MinPoint.Z, zMax = ext.MaxPoint.Z;
            double extW = ext.MaxPoint.X - ext.MinPoint.X;
            double extH = ext.MaxPoint.Y - ext.MinPoint.Y;
            double thick = Math.Max(0.0001, zMax - zMin);

            FlatProfile sectionBest = null;
            double sectionBestArea = -1;
            foreach (double frac in new[] { 0.5, 0.25, 0.75, 0.1, 0.9 })
            {
                double zCut = zMin + thick * frac;
                var sec = TryExtractBySection(work, zCut);
                if (sec == null) continue;
                double area = sec.Width * sec.Height;
                if (area > sectionBestArea) { sectionBestArea = area; sectionBest = sec; }
            }
            if (sectionBest != null && IsBBoxConsistent(sectionBest, extW, extH, tolPercent: 0.15))
            {
                Finalize(result, sectionBest);
                result.Reason = "截面切片";
                return result;
            }

            var allCurves = RecursiveExplodeAll(work);
            explodedToDispose.AddRange(allCurves);
            if (allCurves.Count == 0)
            {
                result.Reason = "Explode 无可用曲线";
                return result;
            }

            double zTol = Math.Max(0.5, thick * 0.05);
            var topCurves = FilterCurvesAtZ(allCurves, zMax, zTol);
            var botCurves = FilterCurvesAtZ(allCurves, zMin, zTol);
            var faceCurves = topCurves.Count >= botCurves.Count ? topCurves : botCurves;

            var edges = FlattenCurvesToEdges(faceCurves);
            if (edges.Count < 3)
            {
                double widerZTol = Math.Max(2.0, thick * 0.3);
                var top2 = FilterCurvesAtZ(allCurves, zMax, widerZTol);
                var bot2 = FilterCurvesAtZ(allCurves, zMin, widerZTol);
                var face2 = top2.Count >= bot2.Count ? top2 : bot2;
                edges = FlattenCurvesToEdges(face2);
            }
            if (edges.Count < 3)
            {
                var fallback = DedupEdges2d(FlattenCurvesToEdges(allCurves));
                if (fallback.Count >= 3) edges = fallback;
            }
            if (edges.Count < 3)
            {
                result.Reason = $"顶/底面边段不足 ({edges.Count})";
                return result;
            }

            ProfileExtractionMath.ExtractionReport bestReport = null;
            double bestScore = -1;
            double bboxRef = Math.Max(1.0, extW * extH);
            double maxAllowedSegment = Math.Max(50.0, Math.Max(extW, extH) * 0.6);
            foreach (double tol in ChainTolerances)
            {
                var rep = ProfileExtractionMath.ChainEdgesToProfile(edges, tol);
                if (rep.OuterLoop == null || rep.OuterLoop.Count < 3) continue;
                if (HasLongJump(rep.OuterLoop, maxAllowedSegment)) continue;
                double area = Math.Abs(ProfileExtractionMath.SignedArea(rep.OuterLoop));
                double areaRatio = area / bboxRef;
                if (areaRatio < 0.3) continue;
                int innerCount = rep.InnerLoops?.Count ?? 0;
                double score = (1.0 / Math.Max(0.01, tol)) * (1 + innerCount * 0.5) * rep.SuccessRate;
                if (score > bestScore) { bestScore = score; bestReport = rep; }
                if (tol <= 0.3 && rep.SuccessRate >= 0.8 && areaRatio >= 0.5)
                    break;
            }

            if (bestReport == null)
            {
                var allEdges2d = DedupEdges2d(FlattenCurvesToEdges(allCurves));
                if (allEdges2d.Count >= 3)
                {
                    foreach (double tol in ChainTolerances)
                    {
                        var rep = ProfileExtractionMath.ChainEdgesToProfile(allEdges2d, tol);
                        if (rep.OuterLoop == null || rep.OuterLoop.Count < 3) continue;
                        if (HasLongJump(rep.OuterLoop, maxAllowedSegment)) continue;
                        double area = Math.Abs(ProfileExtractionMath.SignedArea(rep.OuterLoop));
                        if (area / bboxRef < 0.3) continue;
                        bestReport = rep;
                        if (tol <= 0.5 && rep.SuccessRate >= 0.8) break;
                    }
                }
            }

            if (bestReport == null)
            {
                if (sectionBest != null)
                {
                    Finalize(result, sectionBest);
                    result.Reason = "截面切片(终极回退)";
                    return result;
                }
                result.Reason = "链接失败";
                return result;
            }
            result.EdgeChainRate = bestReport.SuccessRate;
            result.OuterSelfIntersected = !bestReport.OuterIsSimple;

            var chainProfile = ConvertToFlatProfile(bestReport);
            if (chainProfile == null)
            {
                result.Reason = bestReport.Reason ?? "未提取到外轮廓";
                return result;
            }
            if (!IsBBoxConsistent(chainProfile, extW, extH, tolPercent: 0.15))
            {
                if (sectionBest != null)
                {
                    Finalize(result, sectionBest);
                    result.Reason = $"截面切片(回退)；链接 BBox {chainProfile.Width:F1}×{chainProfile.Height:F1} 与实体 {extW:F1}×{extH:F1} 偏差>15%";
                    return result;
                }
                result.Reason = $"链接结果 BBox {chainProfile.Width:F1}×{chainProfile.Height:F1} 与实体 {extW:F1}×{extH:F1} 偏差>15%";
                Finalize(result, chainProfile);
                result.Success = false;
                return result;
            }
            Finalize(result, chainProfile);
            result.Reason = "Explode 链接 (Best-of-N)";
            return result;
        }
        catch (System.Exception ex)
        {
            result.Reason = "投影异常: " + ex.Message;
            return result;
        }
        finally
        {
            if (disposeWork)
            {
                try { work?.Dispose(); } catch { }
            }
            foreach (var o in explodedToDispose)
            {
                try { (o as IDisposable)?.Dispose(); } catch { }
            }
        }
    }

    private static void PopulateAlignmentDiagnostic(Solid3d solid, ProjectionResult result)
    {
        double mpDeg = BzbjAlignmentService.MeasureNormalAngleByMassProperties(solid);
        if (mpDeg >= 0)
        {
            result.NormalAngleToZDeg = mpDeg;
            return;
        }

        var verts = BzbjAlignmentService.CollectVertices(solid);
        if (verts.Count >= 4 &&
            BzbjAlignmentService.ComputeAxesByPCA(verts, Centroid(verts), out _, out _, out var nAxis))
        {
            result.NormalAngleToZDeg = BzbjAlignmentService.AngleBetweenDeg(nAxis, Vector3d.ZAxis);
        }
    }

    private sealed class FlatProfile
    {
        public List<Pt2d> Outer = new();
        public List<List<Pt2d>> Inners = new();
        public double Width;
        public double Height;
        public bool SelfIntersected;
        public double EdgeChainRate;
    }

    private static void Finalize(ProjectionResult result, FlatProfile p)
    {
        result.OuterLoop = p.Outer;
        result.InnerLoops = p.Inners;
        result.Width = p.Width;
        result.Height = p.Height;
        result.OuterSelfIntersected = p.SelfIntersected;
        if (p.EdgeChainRate > 0) result.EdgeChainRate = p.EdgeChainRate;
        result.Success = p.Outer.Count >= 3 && !p.SelfIntersected && p.Width > 0.5 && p.Height > 0.5;
    }

    private static FlatProfile ConvertToFlatProfile(ProfileExtractionMath.ExtractionReport rep)
    {
        if (rep == null || rep.OuterLoop == null || rep.OuterLoop.Count < 3) return null;
        double mnX = rep.OuterLoop.Min(p => p.X);
        double mnY = rep.OuterLoop.Min(p => p.Y);
        double mxX = rep.OuterLoop.Max(p => p.X);
        double mxY = rep.OuterLoop.Max(p => p.Y);
        var fp = new FlatProfile
        {
            Outer = rep.OuterLoop.Select(p => new Pt2d(p.X - mnX, p.Y - mnY)).ToList(),
            Width = mxX - mnX,
            Height = mxY - mnY,
            SelfIntersected = !rep.OuterIsSimple,
            EdgeChainRate = rep.SuccessRate
        };
        foreach (var inner in rep.InnerLoops)
            fp.Inners.Add(inner.Select(p => new Pt2d(p.X - mnX, p.Y - mnY)).ToList());
        return fp;
    }

    private static bool IsBBoxConsistent(FlatProfile p, double expectedW, double expectedH, double tolPercent)
    {
        if (p == null || p.Width < 0.5 || p.Height < 0.5) return false;
        if (expectedW <= 0 || expectedH <= 0) return true;
        double dw = Math.Abs(p.Width - expectedW) / expectedW;
        double dh = Math.Abs(p.Height - expectedH) / expectedH;
        return dw <= tolPercent && dh <= tolPercent;
    }

    /// <summary>
    /// 把边段按 2D 坐标去重：投影到 XY 平面后，顶面和底面的同一条边会重叠，
    /// 二者并存会让链接器误把它们都用上而无法闭合。
    /// </summary>
    private static List<ProfileExtractionMath.Edge> DedupEdges2d(List<ProfileExtractionMath.Edge> edges)
    {
        if (edges == null || edges.Count == 0) return edges;
        var seen = new HashSet<long>();
        var result = new List<ProfileExtractionMath.Edge>(edges.Count);
        foreach (var e in edges)
        {
            long k1 = HashEdge2d(e.Start, e.End);
            long k2 = HashEdge2d(e.End, e.Start);
            if (seen.Contains(k1) || seen.Contains(k2)) continue;
            seen.Add(k1);
            result.Add(e);
        }
        return result;
    }

    private static long HashEdge2d(ProfileExtractionMath.PtXY a, ProfileExtractionMath.PtXY b)
    {
        long ax = (long)Math.Round(a.X * 10);
        long ay = (long)Math.Round(a.Y * 10);
        long bx = (long)Math.Round(b.X * 10);
        long by = (long)Math.Round(b.Y * 10);
        return (ax * 73856093L) ^ (ay * 19349663L) ^ (bx * 83492791L) ^ (by * 51571L);
    }

    /// <summary>
    /// 检测多段线中是否存在"超长跳变段"——若任一相邻顶点距离 &gt; maxAllowed，
    /// 说明链接器把两段不相邻的边强行连接了（桥接），结果不可信。
    /// </summary>
    private static bool HasLongJump(List<ProfileExtractionMath.PtXY> loop, double maxAllowed)
    {
        if (loop == null || loop.Count < 2) return false;
        for (int i = 0; i < loop.Count; i++)
        {
            int j = (i + 1) % loop.Count;
            double dx = loop[j].X - loop[i].X;
            double dy = loop[j].Y - loop[i].Y;
            if (dx * dx + dy * dy > maxAllowed * maxAllowed) return true;
        }
        return false;
    }

    /// <summary>
    /// 把投影结果写入 ModelSpace，作为 (0,0,0) 1:1 的零厚度闭合多段线 BLOCK。
    /// 返回 BlockReference 的 ObjectId（已加入交易），方便后续选择/移动。
    /// </summary>
    public static ObjectId InsertProjectionBlock(
        Transaction tr, BlockTableRecord modelSpace,
        ProjectionResult projection,
        string blockName,
        Point3d insertPoint)
    {
        if (projection == null || !projection.Success) return ObjectId.Null;

        var db = modelSpace.Database;
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);

        string finalName = EnsureUniqueBlockName(bt, blockName);
        var def = new BlockTableRecord
        {
            Name = finalName,
            Origin = Point3d.Origin
        };
        bt.Add(def);
        tr.AddNewlyCreatedDBObject(def, true);

        var outer = BuildClosedLwPolyline(projection.OuterLoop, ccw: true);
        if (outer != null)
        {
            def.AppendEntity(outer);
            tr.AddNewlyCreatedDBObject(outer, true);
        }
        foreach (var inner in projection.InnerLoops)
        {
            var hole = BuildClosedLwPolyline(inner, ccw: false);
            if (hole == null) continue;
            def.AppendEntity(hole);
            tr.AddNewlyCreatedDBObject(hole, true);
        }

        var bref = new BlockReference(insertPoint, def.ObjectId)
        {
            ScaleFactors = new Scale3d(1.0, 1.0, 1.0),
            Rotation = 0.0
        };
        modelSpace.AppendEntity(bref);
        tr.AddNewlyCreatedDBObject(bref, true);
        return bref.ObjectId;
    }

    private static string EnsureUniqueBlockName(BlockTable bt, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "PAIBAN_OUTLINE";
        if (!bt.Has(baseName)) return baseName;
        for (int i = 1; i < 100000; i++)
        {
            string n = baseName + "_" + i;
            if (!bt.Has(n)) return n;
        }
        return baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
    }

    private static Polyline BuildClosedLwPolyline(List<Pt2d> loop, bool ccw)
    {
        if (loop == null || loop.Count < 3) return null;
        var ordered = new List<Pt2d>(loop);
        double area = SignedAreaPt2d(ordered);
        if (ccw && area < 0) ordered.Reverse();
        if (!ccw && area > 0) ordered.Reverse();

        var pl = new Polyline
        {
            Closed = true,
            Elevation = 0,
            ConstantWidth = 0
        };
        for (int i = 0; i < ordered.Count; i++)
            pl.AddVertexAt(i, new Point2d(ordered[i].X, ordered[i].Y), 0, 0, 0);
        return pl;
    }

    private static double SignedAreaPt2d(IReadOnlyList<Pt2d> poly)
    {
        if (poly == null || poly.Count < 3) return 0;
        double a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            a += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
        return a * 0.5;
    }

    #region 截面切片（首选，依赖 ACAD 几何内核）

    /// <summary>
    /// 在 z=zMid 平面上切一刀，返回的 Region 即为截面轮廓。
    /// 这一手段绕过了 BREP 所需的曲面采样问题，对任何能切片的实体都有效。
    /// </summary>
    private static FlatProfile TryExtractBySection(Solid3d aligned, double zMid)
    {
        DBObject section = null;
        var toDispose = new List<DBObject>();
        try
        {
            var plane = new Plane(new Point3d(0, 0, zMid), Vector3d.ZAxis);
            // GetSection 在不同 ACAD 版本签名略有差异；统一用反射调用兜底
            section = TryGetSection(aligned, plane);
            if (section == null) return null;

            var allCurves = new List<DBObject>();
            CollectCurvesFromSection(section, allCurves);
            toDispose.AddRange(allCurves);
            if (allCurves.Count < 3) return null;

            var edges = FlattenCurvesToEdges(allCurves);
            if (edges.Count < 3) return null;

            ProfileExtractionMath.ExtractionReport best = null;
            double bestArea = -1;
            foreach (double tol in ChainTolerances)
            {
                var rep = ProfileExtractionMath.ChainEdgesToProfile(edges, tol);
                if (rep.OuterLoop == null) continue;
                double a = Math.Abs(ProfileExtractionMath.SignedArea(rep.OuterLoop));
                if (a > bestArea) { bestArea = a; best = rep; }
            }
            if (best == null) return null;
            return ConvertToFlatProfile(best);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { section?.Dispose(); } catch { }
            foreach (var o in toDispose)
            {
                try { (o as IDisposable)?.Dispose(); } catch { }
            }
        }
    }

    private static DBObject TryGetSection(Solid3d solid, Plane plane)
    {
        try
        {
            var mi = typeof(Solid3d).GetMethod("GetSection", new[] { typeof(Plane) });
            if (mi != null)
                return mi.Invoke(solid, new object[] { plane }) as DBObject;
        }
        catch { }
        return null;
    }

    private static void CollectCurvesFromSection(DBObject section, List<DBObject> result)
    {
        if (section is Region region)
        {
            try
            {
                var sub = new DBObjectCollection();
                ((Entity)region).Explode(sub);
                foreach (DBObject obj in sub)
                {
                    if (obj is Region r2)
                    {
                        CollectCurvesFromSection(r2, result);
                        try { r2.Dispose(); } catch { }
                    }
                    else
                    {
                        result.Add(obj);
                    }
                }
                sub.Dispose();
            }
            catch { }
            return;
        }

        if (section is Curve)
        {
            result.Add(section);
            return;
        }

        if (section is Entity ent)
        {
            try
            {
                var sub = new DBObjectCollection();
                ent.Explode(sub);
                foreach (DBObject obj in sub) result.Add(obj);
                sub.Dispose();
            }
            catch { }
        }
    }

    #endregion

    #region 边段抽取

    private static List<DBObject> RecursiveExplodeAll(DBObject entity)
    {
        var result = new List<DBObject>();
        DoExplode(entity, result, 0);
        return result;
    }

    private static void DoExplode(DBObject entity, List<DBObject> result, int depth)
    {
        if (depth > MaxExplodeDepth) return;
        if (entity is Line || entity is Arc || entity is Circle || entity is Spline || entity is Ellipse)
        { result.Add(entity); return; }

        if (entity is Polyline pl)
        {
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                try
                {
                    if (pl.GetSegmentType(i) == SegmentType.Line)
                    {
                        var seg = pl.GetLineSegment2dAt(i);
                        result.Add(new Line(
                            new Point3d(seg.StartPoint.X, seg.StartPoint.Y, pl.Elevation),
                            new Point3d(seg.EndPoint.X, seg.EndPoint.Y, pl.Elevation)));
                    }
                    else if (pl.GetSegmentType(i) == SegmentType.Arc)
                    {
                        var seg = pl.GetArcSegment2dAt(i);
                        double sa = (seg.StartPoint - seg.Center).Angle;
                        double ea = (seg.EndPoint - seg.Center).Angle;
                        result.Add(new Arc(
                            new Point3d(seg.Center.X, seg.Center.Y, pl.Elevation),
                            seg.Radius, sa, ea));
                    }
                }
                catch { }
            }
            return;
        }

        if (entity is Entity ent)
        {
            try
            {
                var sub = new DBObjectCollection();
                ent.Explode(sub);
                foreach (DBObject c in sub) DoExplode(c, result, depth + 1);
                sub.Dispose();
            }
            catch { }
        }
    }

    private static List<DBObject> FilterCurvesAtZ(List<DBObject> curves, double targetZ, double tol)
    {
        var result = new List<DBObject>();
        foreach (var obj in curves)
        {
            if (obj is not Entity ent) continue;
            try
            {
                var e = ent.GeometricExtents;
                double zRange = Math.Abs(e.MaxPoint.Z - e.MinPoint.Z);
                double zMid = (e.MaxPoint.Z + e.MinPoint.Z) * 0.5;
                if (zRange < tol * 3 && Math.Abs(zMid - targetZ) < tol * 2)
                    result.Add(obj);
            }
            catch { }
        }
        return result;
    }

    /// <summary>
    /// 把 Line/Arc/Circle/Ellipse/Spline 拆分为 2D 直线段（用于链接成闭合环）。
    /// </summary>
    private static List<ProfileExtractionMath.Edge> FlattenCurvesToEdges(IEnumerable<DBObject> curves)
    {
        var edges = new List<ProfileExtractionMath.Edge>();
        foreach (var obj in curves)
        {
            try
            {
                if (obj is Line ln)
                {
                    edges.Add(new ProfileExtractionMath.Edge
                    {
                        Start = new ProfileExtractionMath.PtXY(ln.StartPoint.X, ln.StartPoint.Y),
                        End = new ProfileExtractionMath.PtXY(ln.EndPoint.X, ln.EndPoint.Y)
                    });
                }
                else if (obj is Arc arc)
                {
                    double sa = arc.StartAngle, ea = arc.EndAngle;
                    if (ea < sa) ea += 2 * Math.PI;
                    int samples = GetAdaptiveSampleCount(arc.Length, 12, ArcSamples, 35.0);
                    var pts = new List<ProfileExtractionMath.PtXY>();
                    for (int i = 0; i <= samples; i++)
                    {
                        double a = sa + (ea - sa) * i / samples;
                        pts.Add(new ProfileExtractionMath.PtXY(
                            arc.Center.X + arc.Radius * Math.Cos(a),
                            arc.Center.Y + arc.Radius * Math.Sin(a)));
                    }
                    AddPolyline(edges, pts);
                }
                else if (obj is Circle c)
                {
                    double circumference = 2 * Math.PI * c.Radius;
                    int samples = GetAdaptiveSampleCount(circumference, 16, ArcSamples, 40.0);
                    var pts = new List<ProfileExtractionMath.PtXY>();
                    for (int i = 0; i <= samples; i++)
                    {
                        double a = 2 * Math.PI * i / samples;
                        pts.Add(new ProfileExtractionMath.PtXY(
                            c.Center.X + c.Radius * Math.Cos(a),
                            c.Center.Y + c.Radius * Math.Sin(a)));
                    }
                    AddPolyline(edges, pts);
                }
                else if (obj is Ellipse e)
                {
                    double ellipseRefLength = 2 * Math.PI * Math.Max(e.MajorRadius, e.MinorRadius);
                    int samples = GetAdaptiveSampleCount(ellipseRefLength, 16, ArcSamples, 40.0);
                    var pts = new List<ProfileExtractionMath.PtXY>();
                    for (int i = 0; i <= samples; i++)
                    {
                        double t = 2 * Math.PI * i / samples;
                        var p = e.Center + Math.Cos(t) * e.MajorAxis + Math.Sin(t) * e.MinorAxis;
                        pts.Add(new ProfileExtractionMath.PtXY(p.X, p.Y));
                    }
                    AddPolyline(edges, pts);
                }
                else if (obj is Spline sp)
                {
                    try
                    {
                        double s = sp.StartParam, ep = sp.EndParam;
                        int samples = GetAdaptiveSampleCount(GetSplineLength(sp), 18, SplineSamples, 30.0);
                        var pts = new List<ProfileExtractionMath.PtXY>();
                        for (int i = 0; i <= samples; i++)
                        {
                            var p = sp.GetPointAtParameter(s + (ep - s) * i / samples);
                            pts.Add(new ProfileExtractionMath.PtXY(p.X, p.Y));
                        }
                        AddPolyline(edges, pts);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return edges;
    }

    private static int GetAdaptiveSampleCount(double curveLength, int minSamples, int maxSamples, double targetStep)
    {
        if (curveLength <= 0 || double.IsNaN(curveLength) || double.IsInfinity(curveLength))
            return minSamples;

        int samples = (int)Math.Ceiling(curveLength / Math.Max(1.0, targetStep));
        if (samples < minSamples) samples = minSamples;
        if (samples > maxSamples) samples = maxSamples;
        return samples;
    }

    private static double GetSplineLength(Spline spline)
    {
        try
        {
            return spline.GetDistanceAtParameter(spline.EndParam) - spline.GetDistanceAtParameter(spline.StartParam);
        }
        catch
        {
            return 0;
        }
    }

    private static void AddPolyline(List<ProfileExtractionMath.Edge> edges,
        List<ProfileExtractionMath.PtXY> pts)
    {
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9) continue;
            edges.Add(new ProfileExtractionMath.Edge { Start = a, End = b });
        }
    }

    #endregion

    private static Point3d Centroid(List<Point3d> pts)
    {
        double cx = 0, cy = 0, cz = 0;
        foreach (var p in pts) { cx += p.X; cy += p.Y; cz += p.Z; }
        return new Point3d(cx / pts.Count, cy / pts.Count, cz / pts.Count);
    }

    private static double Distance2d(Pt2d a, Pt2d b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
