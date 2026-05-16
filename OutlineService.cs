using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    public static class OutlineService
    {
        public sealed class OutlineProfile
        {
            public List<Pt2d> Outer { get; set; } = new();
            public List<List<Pt2d>> Inners { get; set; } = new();
            /// <summary>FLATSHOT 投影提取是否成功（仅 Solid3d 有意义）</summary>
            public bool FlatshotSuccess { get; set; }
            /// <summary>BZBJ 摆正后法向与 Z 轴夹角(度)；&lt;0.001° 视为合格</summary>
            public double NormalAngleToZDeg { get; set; }
            /// <summary>边段链接成功率(0~1)</summary>
            public double EdgeChainRate { get; set; }
            /// <summary>外轮廓是否自交</summary>
            public bool OuterSelfIntersected { get; set; }
            /// <summary>外轮廓是否封闭（FLATSHOT 输出契约要求）</summary>
            public bool OuterClosed { get; set; }
            /// <summary>FLATSHOT 实测宽（X 方向 mm）。当 &gt;0 时应优先用作板件长度</summary>
            public double TrueWidth { get; set; }
            /// <summary>FLATSHOT 实测高（Y 方向 mm）</summary>
            public double TrueHeight { get; set; }
            /// <summary>失败/降级原因</summary>
            public string DiagnosticMessage { get; set; } = "";
        }

        private const double ZTol = 1.5;
        private const int ArcSamples = 32;
        private const int SplineSamples = 48;
        private const double DedupTol = 2.0;
        private const int MaxExplodeDepth = 6;

        #region 公开API

        public static List<Pt2d> ExtractOutline(Entity entity, double boxLen, double boxWid)
        {
            return ExtractOutlineProfile(entity, boxLen, boxWid).Outer;
        }

        public static OutlineProfile ExtractOutlineProfile(Entity entity, double boxLen, double boxWid)
        {
            var profile = new OutlineProfile();
            if (entity is Solid3d solid)
                return ExtractSolidOutlineProfile(solid, boxLen, boxWid);
            if (entity is Polyline pline)
            {
                profile.Outer = ExtractPolylineOutline(pline, boxLen, boxWid);
                return profile;
            }
            if (entity is Circle || entity is Ellipse || entity is Spline)
            {
                profile.Outer = ExtractCurveEntityOutline(entity, boxLen, boxWid);
                return profile;
            }

            profile.Outer = RectOutline(boxLen, boxWid);
            return profile;
        }

        /// <summary>圆弧/样条板件展开尺寸计算（保留原逻辑）</summary>
        public static bool TryCalcArcUnfold(Solid3d solid,
            out double unfoldedLength, out double panelWidth, out double thickness,
            out double innerRadius, out double arcAngleDeg,
            out double straight1, out double straight2)
        {
            unfoldedLength = panelWidth = thickness = 0;
            innerRadius = arcAngleDeg = straight1 = straight2 = 0;
            if (solid == null) return false;
            try
            {
                double volume = solid.MassProperties.Volume;
                Extents3d ext = solid.GeometricExtents;
                double[] dims = SortedBboxDims(ext);
                var allCurves = RecursiveExplodeAll(solid);
                var arcs = ExtractUniqueArcs(allCurves);
                var lines = ExtractUniqueLines(allCurves);
                var arcPairs = PairInnerOuterArcs(arcs);
                if (arcPairs.Count > 0)
                {
                    thickness = arcPairs.Average(p => Math.Abs(p.OuterR - p.InnerR));
                    innerRadius = arcPairs.Min(p => p.InnerR);
                    arcAngleDeg = Math.Round(arcPairs.Sum(p => p.Sweep) * 180.0 / Math.PI, 1);
                }
                if (thickness <= 0 || thickness > 60) thickness = DetectThicknessFromCurves(allCurves);
                if (thickness <= 0) thickness = dims[0];
                if (thickness > 60) thickness = dims[0];
                panelWidth = (arcPairs.Count > 0) ? DetectWidthFromArcs(solid, arcs) : 0;
                if (panelWidth <= 0)
                    panelWidth = (Math.Abs(dims[0] - thickness) < 2) ? dims[1] : dims[0];
                if (thickness > panelWidth) (thickness, panelWidth) = (panelWidth, thickness);
                if (thickness > 0 && panelWidth > 0)
                    unfoldedLength = Math.Round(volume / (panelWidth * thickness), 2);
                if (lines.Count > 0)
                {
                    var segs = FilterStraightSegments(lines, thickness);
                    straight1 = segs.Count > 0 ? segs[0] : 0;
                    straight2 = segs.Count > 1 ? segs[1] : 0;
                }
                DisposeCurves(allCurves);
                thickness = Math.Round(thickness, 2);
                panelWidth = Math.Round(panelWidth, 2);
                return unfoldedLength > 0;
            }
            catch
            {
                return CalcByVolumeFallback(solid, out unfoldedLength, out panelWidth, out thickness);
            }
        }

        #endregion

        #region Solid3d轮廓提取 —— PCA平面检测 + 多级回退

        private struct NormalizeMapper
        {
            public double Angle;
            public double MinX;
            public double MinY;
            public double ActW;
            public double ActH;
            public bool Swapped;
            public double ScaleX;
            public double ScaleY;
        }

        private static OutlineProfile ExtractSolidOutlineProfile(Solid3d solid, double boxLen, double boxWid)
        {
            var profile = new OutlineProfile();
            try
            {
                // ★ 主策略：BZBJ 摆正 → FLATSHOT 正交投影 → 闭合多段线
                //   仅在主策略失败时才回退到 PCA + 顶面采样的历史算法。
                var flatshot = TryFlatshotProjection(solid);
                if (flatshot != null)
                {
                    profile.NormalAngleToZDeg = flatshot.NormalAngleToZDeg;
                    profile.EdgeChainRate = flatshot.EdgeChainRate;
                    profile.OuterSelfIntersected = flatshot.OuterSelfIntersected;
                    profile.OuterClosed = flatshot.Success && flatshot.OuterLoop.Count >= 3;
                    profile.DiagnosticMessage = flatshot.Reason ?? "";
                }
                // FLATSHOT 链接彻底失败时：先用顶面/底面点的 2D 凸包兜底，
                // 这样像半圆/扇形等"凸形板件"至少能拿到正确的轮廓而不是矩形 BBox。
                if ((flatshot == null || !flatshot.Success || flatshot.OuterLoop.Count < 3)
                    && TryConvexHullFallback(solid, out var hull) && hull.Count >= 3)
                {
                    profile.Outer = hull;
                    profile.TrueWidth = hull.Max(p => p.X) - hull.Min(p => p.X);
                    profile.TrueHeight = hull.Max(p => p.Y) - hull.Min(p => p.Y);
                    profile.OuterClosed = true;
                    profile.FlatshotSuccess = true;  // 仍标记成功 — 凸包是可用的轮廓
                    profile.DiagnosticMessage = (flatshot?.Reason ?? "") + " → 凸包兜底";
                    return profile;
                }

                if (flatshot != null && flatshot.Success && flatshot.OuterLoop.Count >= 3)
                {
                    // 关键：信任 FLATSHOT 的实际尺寸，不强行缩放到 boxLen×boxWid。
                    // 倾斜板件 (非 XY 平行) 的 entity.GeometricExtents 包围盒比真实板大很多，
                    // 若仍按它来缩放 FLATSHOT 输出，会把正确的轮廓扭曲掉。
                    double sx = 1.0, sy = 1.0;
                    var outerLoop = flatshot.OuterLoop;
                    double srcW = flatshot.Width, srcH = flatshot.Height;

                    // 保持长边沿 X 的习惯
                    bool swapNeeded = (srcW < srcH) != (boxLen < boxWid);
                    // 但仅当 boxLen/boxWid 看起来有意义时才考虑换轴
                    if (swapNeeded && boxLen > 0.5 && boxWid > 0.5)
                    {
                        outerLoop = outerLoop.Select(p => new Pt2d(p.Y, p.X)).ToList();
                        (srcW, srcH) = (srcH, srcW);
                    }

                    // 仅在 FLATSHOT 实测尺寸与 box 接近(±10%)时做微调缩放，远超就保留实测
                    if (boxLen > 0.5 && boxWid > 0.5)
                    {
                        double tW = Math.Max(boxLen, boxWid);
                        double tH = Math.Min(boxLen, boxWid);
                        double rx = tW / Math.Max(1e-3, srcW);
                        double ry = tH / Math.Max(1e-3, srcH);
                        if (rx > 0.9 && rx < 1.1 && ry > 0.9 && ry < 1.1)
                        {
                            sx = rx; sy = ry;
                        }
                    }

                    profile.Outer = outerLoop.Select(p => new Pt2d(p.X * sx, p.Y * sy)).ToList();
                    foreach (var inner in flatshot.InnerLoops)
                    {
                        if (inner == null || inner.Count < 3) continue;
                        var src = inner;
                        if (swapNeeded && boxLen > 0.5 && boxWid > 0.5)
                            src = inner.Select(p => new Pt2d(p.Y, p.X)).ToList();
                        var scaled = src.Select(p => new Pt2d(p.X * sx, p.Y * sy)).ToList();
                        if (Math.Abs(PolygonAreaPt(scaled)) < 1.0) continue;
                        profile.Inners.Add(scaled);
                    }
                    profile.TrueWidth = srcW * sx;
                    profile.TrueHeight = srcH * sy;
                    profile.FlatshotSuccess = true;
                    return profile;
                }

                if (!GetPCATransform(solid, out Matrix3d toLocal))
                {
                    profile.Outer = RectOutline(boxLen, boxWid);
                    return profile;
                }

                List<Point2d> outerRaw = null;
                List<List<Point2d>> innerRawLoops = null;
                bool projectedOk = TryExtractProjectedLoopsFromAlignedSolid(solid, toLocal, out outerRaw, out innerRawLoops);

                if (!projectedOk || outerRaw == null || outerRaw.Count < 3 || !IsValidOutline(outerRaw))
                {
                    outerRaw = ExtractTopFaceByExplode(solid, toLocal);
                    innerRawLoops = null;
                }
                if (outerRaw == null || outerRaw.Count < 3 || !IsValidOutline(outerRaw))
                {
                    outerRaw = ExtractTopFaceByGripPoints(solid, toLocal);
                    innerRawLoops = null;
                }
                if (outerRaw == null || outerRaw.Count < 3 || !IsValidOutline(outerRaw))
                {
                    outerRaw = ExtractByVertexCollection(solid, toLocal);
                    innerRawLoops = null;
                }

                if (outerRaw == null || outerRaw.Count < 3 || !IsValidOutline(outerRaw))
                {
                    profile.Outer = RectOutline(boxLen, boxWid);
                    return profile;
                }

                profile.Outer = Normalize(outerRaw, boxLen, boxWid, out var mapper);

                innerRawLoops ??= ExtractInnerLoopsRaw(solid, toLocal, outerRaw);
                foreach (var loop in innerRawLoops)
                {
                    var mapped = MapByNormalizer(loop, mapper);
                    if (mapped.Count < 3) continue;
                    double area = Math.Abs(PolygonAreaPt(mapped));
                    if (area < 1.0) continue;
                    profile.Inners.Add(mapped);
                }

                return profile;
            }
            catch
            {
                profile.Outer = RectOutline(boxLen, boxWid);
                return profile;
            }
        }

        /// <summary>
        /// FLATSHOT 流程：摆正(克隆体) → 投影 → 闭合环。失败时返回 null，由调用方回退。
        /// 注意：不会修改原始 solid，所有变换都作用在克隆体上。
        /// </summary>
        private static FlatshotProjectionService.ProjectionResult TryFlatshotProjection(Solid3d solid)
        {
            Solid3d clone = null;
            try
            {
                clone = solid.Clone() as Solid3d;
                if (clone == null) return null;

                // BZBJ 摆正（在克隆体上操作，保证原始板件不动）
                var align = BzbjAlignmentService.AlignToWorldXY(clone, normalToleranceDeg: 0.001);
                // 这里已经在克隆体上完成摆正，直接走已摆正入口，避免重复克隆同一实体。
                var proj = FlatshotProjectionService.ProjectAligned(clone);
                if (proj == null) return null;
                proj.NormalAngleToZDeg = Math.Min(proj.NormalAngleToZDeg, align.NormalAngleToZDeg);
                return proj;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { clone?.Dispose(); } catch { }
            }
        }

        /// <summary>把 FLATSHOT 局部坐标的轮廓适配到目标 boxLen × boxWid（保留长宽比，必要时拉伸）</summary>
        private static List<Pt2d> AdaptToBox(List<Pt2d> loop, double srcW, double srcH,
            double boxLen, double boxWid, out double scaleX, out double scaleY)
        {
            scaleX = 1.0; scaleY = 1.0;
            if (loop == null || loop.Count < 3 || srcW < 1e-6 || srcH < 1e-6)
                return loop ?? new List<Pt2d>();
            if (boxLen <= 0 || boxWid <= 0) return loop;

            bool swap = (srcW < srcH) != (boxLen < boxWid);
            if (swap)
            {
                loop = loop.Select(p => new Pt2d(p.Y, p.X)).ToList();
                (srcW, srcH) = (srcH, srcW);
            }

            double targetW = Math.Max(boxLen, boxWid);
            double targetH = Math.Min(boxLen, boxWid);
            scaleX = targetW / srcW;
            scaleY = targetH / srcH;

            var r = new List<Pt2d>(loop.Count);
            foreach (var p in loop) r.Add(new Pt2d(p.X * scaleX, p.Y * scaleY));
            return r;
        }

        private static bool TryExtractProjectedLoopsFromAlignedSolid(
            Solid3d solid, Matrix3d toLocal, out List<Point2d> outerLoop, out List<List<Point2d>> innerLoops)
        {
            outerLoop = null;
            innerLoops = new List<List<Point2d>>();
            Solid3d cloned = null;
            try
            {
                cloned = solid.Clone() as Solid3d;
                if (cloned == null) return false;
                cloned.TransformBy(toLocal);

                Extents3d le = cloned.GeometricExtents;
                double zMax = le.MaxPoint.Z, zMin = le.MinPoint.Z;
                double thick = zMax - zMin;
                double tol = Math.Max(ZTol, thick * 0.2);

                var allCurves = RecursiveExplodeAll(cloned);
                var topC = FilterCurvesByZ(allCurves, zMax, tol);
                var botC = FilterCurvesByZ(allCurves, zMin, tol);
                var candidate = topC.Count >= botC.Count ? topC : botC;

                var loops = new List<List<Point2d>>();
                foreach (var obj in candidate)
                {
                    var loop = TryBuildClosedLoopFromCurve(obj);
                    if (loop == null || loop.Count < 3) continue;
                    loop = Dedup2d(loop);
                    if (loop.Count < 3) continue;
                    if (!IsValidOutline(loop)) continue;
                    loops.Add(loop);
                }

                DisposeCurves(allCurves);

                if (loops.Count == 0) return false;

                var ordered = loops
                    .Select(l => (Loop: l, Area: Math.Abs(PolygonArea(l))))
                    .Where(x => x.Area > 1.0)
                    .OrderByDescending(x => x.Area)
                    .ToList();
                if (ordered.Count == 0) return false;

                outerLoop = ordered[0].Loop;
                var outerCentroidSafe = CalcCentroid2d(outerLoop);
                foreach (var item in ordered.Skip(1))
                {
                    var c = CalcCentroid2d(item.Loop);
                    if (PointInPolygon(c, outerLoop) || PointInPolygon(outerCentroidSafe, item.Loop) == false)
                        innerLoops.Add(item.Loop);
                }

                return outerLoop != null && outerLoop.Count >= 3;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { cloned?.Dispose(); } catch { }
            }
        }

        #endregion

        #region 内轮廓提取

        private static List<List<Point2d>> ExtractInnerLoopsRaw(Solid3d solid, Matrix3d toLocal, List<Point2d> outerRaw)
        {
            var result = new List<List<Point2d>>();
            Solid3d cloned = null;
            try
            {
                cloned = solid.Clone() as Solid3d;
                if (cloned == null) return result;
                cloned.TransformBy(toLocal);

                Extents3d le = cloned.GeometricExtents;
                double zMax = le.MaxPoint.Z, zMin = le.MinPoint.Z;
                double thick = zMax - zMin;
                double tol = Math.Max(ZTol, thick * 0.25);

                var allCurves = RecursiveExplodeAll(cloned);
                var topC = FilterCurvesByZ(allCurves, zMax, tol);
                var botC = FilterCurvesByZ(allCurves, zMin, tol);
                var curves = topC.Count >= botC.Count ? topC : botC;

                double outerArea = Math.Abs(PolygonArea(outerRaw));
                if (outerArea < 1.0)
                {
                    DisposeCurves(allCurves);
                    return result;
                }

                var dedup = new HashSet<string>(StringComparer.Ordinal);
                foreach (var obj in curves)
                {
                    var loop = TryBuildClosedLoopFromCurve(obj);
                    if (loop == null || loop.Count < 3) continue;
                    loop = Dedup2d(loop);
                    if (loop.Count < 3) continue;

                    double area = Math.Abs(PolygonArea(loop));
                    if (area < 1.0 || area >= outerArea * 0.95) continue;

                    var c = CalcCentroid2d(loop);
                    if (!PointInPolygon(c, outerRaw)) continue;

                    string key = $"{Math.Round(c.X, 1)}_{Math.Round(c.Y, 1)}_{Math.Round(area, 1)}";
                    if (!dedup.Add(key)) continue;
                    result.Add(loop);
                }

                DisposeCurves(allCurves);
                return result;
            }
            catch
            {
                return result;
            }
            finally
            {
                try { cloned?.Dispose(); } catch { }
            }
        }

        private static List<Point2d> TryBuildClosedLoopFromCurve(DBObject obj)
        {
            if (obj is Circle c)
            {
                var pts = new List<Point2d>();
                for (int i = 0; i < ArcSamples; i++)
                {
                    double a = 2 * Math.PI * i / ArcSamples;
                    pts.Add(new Point2d(c.Center.X + c.Radius * Math.Cos(a), c.Center.Y + c.Radius * Math.Sin(a)));
                }
                return pts;
            }

            if (obj is Ellipse e)
            {
                var pts = new List<Point2d>();
                for (int i = 0; i < ArcSamples; i++)
                {
                    double a = 2 * Math.PI * i / ArcSamples;
                    pts.Add(new Point2d(e.Center.X + e.MajorRadius * Math.Cos(a), e.Center.Y + e.MinorRadius * Math.Sin(a)));
                }
                return pts;
            }

            if (obj is Polyline p && p.Closed)
            {
                var pts = new List<Point2d>();
                for (int i = 0; i < p.NumberOfVertices; i++)
                {
                    double bulge = p.GetBulgeAt(i);
                    var pt = p.GetPoint2dAt(i);
                    if (Math.Abs(bulge) > 1e-9)
                    {
                        var nxt = p.GetPoint2dAt((i + 1) % p.NumberOfVertices);
                        var arcPts = SampleBulgeArc(pt, nxt, bulge, ArcSamples);
                        for (int j = (pts.Count > 0 ? 1 : 0); j < arcPts.Count; j++) pts.Add(arcPts[j]);
                    }
                    else
                    {
                        pts.Add(pt);
                    }
                }
                return pts;
            }

            if (obj is Spline sp)
            {
                try
                {
                    if (!sp.Closed) return null;
                    var pts = new List<Point2d>();
                    double s = sp.StartParam, ep = sp.EndParam;
                    for (int i = 0; i <= SplineSamples; i++)
                    {
                        var pp = sp.GetPointAtParameter(s + (ep - s) * i / SplineSamples);
                        pts.Add(new Point2d(pp.X, pp.Y));
                    }
                    return pts;
                }
                catch { return null; }
            }

            return null;
        }

        private static Point2d CalcCentroid2d(List<Point2d> pts)
        {
            if (pts == null || pts.Count == 0) return Point2d.Origin;
            double sx = 0, sy = 0;
            foreach (var p in pts) { sx += p.X; sy += p.Y; }
            return new Point2d(sx / pts.Count, sy / pts.Count);
        }

        private static bool PointInPolygon(Point2d p, List<Point2d> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                bool intersect = ((poly[i].Y > p.Y) != (poly[j].Y > p.Y)) &&
                    (p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y + 1e-12) + poly[i].X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        #endregion

        #region PCA平面检测（核心改进）

        /// <summary>
        /// PCA（主成分分析）找板件真实坐标系。
        /// 用实体顶点的协方差矩阵特征向量确定厚度/长度/宽度方向。
        /// 对任意倾斜/旋转的板件都能正确检测平面方向。
        /// </summary>
        private static bool GetPCATransform(Solid3d solid, out Matrix3d toLocal)
        {
            toLocal = Matrix3d.Identity;
            try
            {
                var pts = GetAllVertices3d(solid);
                if (pts.Count < 4) return false;

                // 1. 计算质心
                double cx = 0, cy = 0, cz = 0;
                foreach (var p in pts) { cx += p.X; cy += p.Y; cz += p.Z; }
                cx /= pts.Count; cy /= pts.Count; cz /= pts.Count;
                var centroid = new Point3d(cx, cy, cz);

                // 2. 计算协方差矩阵
                double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
                foreach (var p in pts)
                {
                    double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
                    cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                    cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
                }
                int n = pts.Count;
                var cov = new double[,] {
                    { cxx / n, cxy / n, cxz / n },
                    { cxy / n, cyy / n, cyz / n },
                    { cxz / n, cyz / n, czz / n }
                };

                // 3. 求特征向量（Jacobi迭代法）
                Eigen3x3(cov, out var evals, out var evecs);

                // 4. 最小特征值→厚度方向，最大→长度方向
                int iMin = 0, iMax = 0;
                for (int i = 1; i < 3; i++)
                {
                    if (evals[i] < evals[iMin]) iMin = i;
                    if (evals[i] > evals[iMax]) iMax = i;
                }
                int iMid = 3 - iMin - iMax;

                Vector3d thickAxis = new Vector3d(evecs[0, iMin], evecs[1, iMin], evecs[2, iMin]).GetNormal();
                Vector3d lenAxis = new Vector3d(evecs[0, iMax], evecs[1, iMax], evecs[2, iMax]).GetNormal();
                Vector3d widAxis = thickAxis.CrossProduct(lenAxis).GetNormal();

                // 确保右手系
                if (lenAxis.CrossProduct(widAxis).DotProduct(thickAxis) < 0)
                    widAxis = widAxis.Negate();

                toLocal = Matrix3d.AlignCoordinateSystem(
                    centroid, lenAxis, widAxis, thickAxis,
                    Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis);
                return true;
            }
            catch { return false; }
        }

        /// <summary>获取Solid3d的所有顶点（GripPoints + Explode端点）</summary>
        private static List<Point3d> GetAllVertices3d(Solid3d solid)
        {
            var pts = new HashSet<long>();
            var result = new List<Point3d>();

            // GripPoints
            try
            {
                var grips = new Point3dCollection();
                var sm = new IntegerCollection();
                var gi = new IntegerCollection();
                solid.GetGripPoints(grips, sm, gi);
                foreach (Point3d p in grips)
                {
                    long key = HashPt(p);
                    if (pts.Add(key)) result.Add(p);
                }
            }
            catch { }

            // 递归Explode端点
            try
            {
                var curves = RecursiveExplodeAll(solid);
                foreach (var obj in curves)
                {
                    if (obj is Line ln)
                    {
                        if (pts.Add(HashPt(ln.StartPoint))) result.Add(ln.StartPoint);
                        if (pts.Add(HashPt(ln.EndPoint))) result.Add(ln.EndPoint);
                    }
                    else if (obj is Arc arc)
                    {
                        if (pts.Add(HashPt(arc.StartPoint))) result.Add(arc.StartPoint);
                        if (pts.Add(HashPt(arc.EndPoint))) result.Add(arc.EndPoint);
                        if (pts.Add(HashPt(arc.Center))) result.Add(arc.Center);
                    }
                }
                DisposeCurves(curves);
            }
            catch { }

            // BBox角点作保底
            if (result.Count < 4)
            {
                try
                {
                    var e = solid.GeometricExtents;
                    var corners = new[] {
                        e.MinPoint, e.MaxPoint,
                        new Point3d(e.MinPoint.X, e.MaxPoint.Y, e.MinPoint.Z),
                        new Point3d(e.MaxPoint.X, e.MinPoint.Y, e.MinPoint.Z),
                        new Point3d(e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.Z),
                        new Point3d(e.MaxPoint.X, e.MaxPoint.Y, e.MinPoint.Z),
                        new Point3d(e.MinPoint.X, e.MaxPoint.Y, e.MaxPoint.Z),
                        new Point3d(e.MaxPoint.X, e.MinPoint.Y, e.MaxPoint.Z)
                    };
                    foreach (var c in corners) if (pts.Add(HashPt(c))) result.Add(c);
                }
                catch { }
            }
            return result;
        }

        private static long HashPt(Point3d p) =>
            ((long)(p.X * 10)) * 1000000000L + ((long)(p.Y * 10)) * 1000000L + (long)(p.Z * 10);

        /// <summary>
        /// 3×3对称矩阵特征值分解（Jacobi迭代法）
        /// 输出：evals[3]=特征值(降序), evecs[3,3]=对应特征向量(列向量)
        /// </summary>
        private static void Eigen3x3(double[,] a, out double[] evals, out double[,] evecs)
        {
            evals = new double[3];
            evecs = new double[3, 3];
            // 复制
            double a00 = a[0, 0], a01 = a[0, 1], a02 = a[0, 2];
            double a11 = a[1, 1], a12 = a[1, 2], a22 = a[2, 2];
            // 初始化特征向量为单位矩阵
            double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

            for (int iter = 0; iter < 100; iter++)
            {
                // 找最大非对角元素
                double maxOff = Math.Abs(a01);
                int p = 0, q = 1;
                if (Math.Abs(a02) > maxOff) { maxOff = Math.Abs(a02); p = 0; q = 2; }
                if (Math.Abs(a12) > maxOff) { maxOff = Math.Abs(a12); p = 1; q = 2; }
                if (maxOff < 1e-12) break;

                double app = p == 0 ? a00 : p == 1 ? a11 : a22;
                double aqq = q == 0 ? a00 : q == 1 ? a11 : a22;
                double apq = p == 0 && q == 1 ? a01 : p == 0 && q == 2 ? a02 : a12;

                double theta = 0.5 * Math.Atan2(2 * apq, aqq - app);
                double c = Math.Cos(theta), s = Math.Sin(theta);

                // Givens旋转
                double[] row = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    double aip = Get(i, p); double aiq = Get(i, q);
                    Set(i, p, c * aip - s * aiq);
                    Set(i, q, s * aip + c * aiq);
                }
                for (int i = 0; i < 3; i++)
                {
                    double api = Get(p, i); double aqi = Get(q, i);
                    Set(p, i, c * api - s * aqi);
                    Set(q, i, s * api + c * aqi);
                }
                // 更新特征向量
                for (int i = 0; i < 3; i++)
                {
                    double vip = v[i, p], viq = v[i, q];
                    v[i, p] = c * vip - s * viq;
                    v[i, q] = s * vip + c * viq;
                }

                double Get(int r, int cc) => r == 0 && cc == 0 ? a00 : r == 0 && cc == 1 ? a01 : r == 0 && cc == 2 ? a02
                    : r == 1 && cc == 0 ? a01 : r == 1 && cc == 1 ? a11 : r == 1 && cc == 2 ? a12
                    : r == 2 && cc == 0 ? a02 : r == 2 && cc == 1 ? a12 : a22;
                void Set(int r, int cc, double val)
                {
                    if (r > cc) (r, cc) = (cc, r);
                    if (r == 0 && cc == 0) a00 = val;
                    else if (r == 0 && cc == 1) a01 = val;
                    else if (r == 0 && cc == 2) a02 = val;
                    else if (r == 1 && cc == 1) a11 = val;
                    else if (r == 1 && cc == 2) a12 = val;
                    else if (r == 2 && cc == 2) a22 = val;
                }
            }

            evals[0] = a00; evals[1] = a11; evals[2] = a22;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    evecs[i, j] = v[i, j];
        }

        #endregion

        #region 轮廓验证 + 凸包回退

        /// <summary>验证2D轮廓是否有效（面积>0, 无极端自交）</summary>
        private static bool IsValidOutline(List<Point2d> pts)
        {
            if (pts == null || pts.Count < 3) return false;
            double area = PolygonArea(pts);
            if (Math.Abs(area) < 1.0) return false;

            // 检查范围是否合理
            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            double w = maxX - minX, h = maxY - minY;
            if (w < 1 || h < 1) return false;

            // 面积 vs 包围盒面积 — 如果面积太小相比包围盒，说明轮廓可能自交严重
            double bboxArea = w * h;
            if (Math.Abs(area) < bboxArea * 0.05) return false;

            return true;
        }

        private static double PolygonArea(List<Point2d> pts)
        {
            double area = 0;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                area += (pts[j].X - pts[i].X) * (pts[j].Y + pts[i].Y);
            return area / 2.0;
        }

        /// <summary>
        /// 2D凸包（Graham扫描法）—— 用于乱点的安全轮廓
        /// </summary>
        private static List<Point2d> ConvexHull2d(List<Point2d> points)
        {
            if (points.Count < 3) return points;
            var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            var hull = new List<Point2d>();

            // 下凸包
            foreach (var p in sorted)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            // 上凸包
            int lower = hull.Count + 1;
            for (int i = sorted.Count - 2; i >= 0; i--)
            {
                while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], sorted[i]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(sorted[i]);
            }
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        private static double Cross(Point2d o, Point2d a, Point2d b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        #endregion

        #region 顶面提取策略

        private static List<Point2d> ExtractTopFaceByExplode(Solid3d solid, Matrix3d toLocal)
        {
            Solid3d cloned = null;
            try
            {
                cloned = solid.Clone() as Solid3d;
                if (cloned == null) return null;
                cloned.TransformBy(toLocal);

                Extents3d le = cloned.GeometricExtents;
                double zMax = le.MaxPoint.Z, zMin = le.MinPoint.Z;
                double thick = zMax - zMin;
                double tol = Math.Max(ZTol, thick * 0.25);

                var allCurves = RecursiveExplodeAll(cloned);
                if (allCurves.Count < 3) { DisposeCurves(allCurves); return null; }

                var topC = FilterCurvesByZ(allCurves, zMax, tol);
                if (topC.Count >= 3) { var r = ChainAndSample(topC); if (IsValidOutline(r)) { DisposeCurves(allCurves); return r; } }

                var botC = FilterCurvesByZ(allCurves, zMin, tol);
                if (botC.Count >= 3) { var r = ChainAndSample(botC); if (IsValidOutline(r)) { DisposeCurves(allCurves); return r; } }

                // 凸包回退：收集所有顶面点
                var topPts = new List<Point2d>();
                foreach (var obj in allCurves)
                {
                    if (obj is not Entity ent) continue;
                    try
                    {
                        var e = ent.GeometricExtents;
                        double zm = (e.MaxPoint.Z + e.MinPoint.Z) / 2;
                        if (Math.Abs(zm - zMax) < tol * 3)
                        {
                            var sp = SampleCurve(obj);
                            topPts.AddRange(sp);
                        }
                    }
                    catch { }
                }
                DisposeCurves(allCurves);
                topPts = Dedup2d(topPts);
                if (topPts.Count >= 3) return ConvexHull2d(topPts);
                return null;
            }
            catch { return null; }
            finally { try { cloned?.Dispose(); } catch { } }
        }

        private static List<Point2d> ExtractTopFaceByGripPoints(Solid3d solid, Matrix3d toLocal)
        {
            try
            {
                var grips = new Point3dCollection();
                var sm = new IntegerCollection(); var gi = new IntegerCollection();
                solid.GetGripPoints(grips, sm, gi);
                if (grips.Count < 3) return null;

                var localPts = new List<Point3d>();
                foreach (Point3d p in grips) localPts.Add(p.TransformBy(toLocal));

                double zMax = localPts.Max(p => p.Z), zMin = localPts.Min(p => p.Z);
                double thick = zMax - zMin;
                double tol = Math.Max(2.0, thick * 0.3);

                var topVerts = localPts.Where(p => Math.Abs(p.Z - zMax) < tol).ToList();
                if (topVerts.Count < 3)
                    topVerts = localPts.Where(p => Math.Abs(p.Z - zMin) < tol).ToList();
                if (topVerts.Count < 3) return null;

                var pts2d = topVerts.Select(p => new Point2d(p.X, p.Y)).ToList();
                pts2d = Dedup2d(pts2d);
                return pts2d.Count >= 3 ? ConvexHull2d(pts2d) : null;
            }
            catch { return null; }
        }

        private static List<Point2d> ExtractByVertexCollection(Solid3d solid, Matrix3d toLocal)
        {
            try
            {
                var pts3d = GetAllVertices3d(solid);
                var localPts = pts3d.Select(p => p.TransformBy(toLocal)).ToList();

                double zMax = localPts.Max(p => p.Z), zMin = localPts.Min(p => p.Z);
                double thick = zMax - zMin;
                double tol = Math.Max(2.0, thick * 0.35);

                var topPts = localPts
                    .Where(p => Math.Abs(p.Z - zMax) < tol)
                    .Select(p => new Point2d(p.X, p.Y))
                    .ToList();
                topPts = Dedup2d(topPts);
                return topPts.Count >= 3 ? ConvexHull2d(topPts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// FLATSHOT 链接彻底失败时的兜底：把 solid 摆正后取顶/底面顶点的 2D 凸包，
        /// 这样像 1/2 圆/扇形/凸多边形这种"凸"形板件至少能拿到正确轮廓而不是矩形包围盒。
        /// 仅对单连通凸轮廓有效，但远好于"链接失败 → 退化成 BBox 矩形"。
        /// </summary>
        private static bool TryConvexHullFallback(Solid3d solid, out List<Pt2d> hull)
        {
            hull = new List<Pt2d>();
            Solid3d clone = null;
            try
            {
                clone = solid?.Clone() as Solid3d;
                if (clone == null) return false;

                // 用 BZBJ 把克隆体摆正后再取顶面，避免倾斜板件的 BBox 失真
                try { BzbjAlignmentService.AlignToWorldXY(clone); } catch { /* 失败也继续 */ }

                var pts3d = GetAllVertices3d(clone);
                if (pts3d == null || pts3d.Count < 3) return false;

                double zMax = pts3d.Max(p => p.Z);
                double zMin = pts3d.Min(p => p.Z);
                double thick = zMax - zMin;
                double tol = Math.Max(2.0, thick * 0.35);

                var topPts = pts3d
                    .Where(p => Math.Abs(p.Z - zMax) < tol)
                    .Select(p => new Point2d(p.X, p.Y))
                    .ToList();
                if (topPts.Count < 3)
                {
                    topPts = pts3d
                        .Where(p => Math.Abs(p.Z - zMin) < tol)
                        .Select(p => new Point2d(p.X, p.Y))
                        .ToList();
                }
                if (topPts.Count < 3) return false;

                topPts = Dedup2d(topPts);
                if (topPts.Count < 3) return false;

                var ch = ConvexHull2d(topPts);
                if (ch == null || ch.Count < 3) return false;

                // 平移到 minX=minY=0 并转 Pt2d
                double mnX = ch.Min(p => p.X), mnY = ch.Min(p => p.Y);
                hull = ch.Select(p => new Pt2d(p.X - mnX, p.Y - mnY)).ToList();
                return true;
            }
            catch { return false; }
            finally { try { clone?.Dispose(); } catch { } }
        }

        #endregion

        #region S形壁厚检测（保留）

        private static double DetectThicknessFromCurves(List<DBObject> allCurves)
        {
            var candidates = new List<(DBObject curve, double length)>();
            foreach (var obj in allCurves)
            {
                double len = 0;
                if (obj is Spline sp) len = CalcSplineLength(sp);
                else if (obj is Arc arc) len = arc.Length;
                else if (obj is Line ln) len = ln.Length;
                if (len > 20) candidates.Add((obj, len));
            }
            if (candidates.Count < 2) return 0;
            candidates.Sort((a, b) => b.length.CompareTo(a.length));

            for (int i = 0; i < Math.Min(candidates.Count, 6); i++)
            {
                for (int j = i + 1; j < Math.Min(candidates.Count, 8); j++)
                {
                    double ratio = candidates[j].length / candidates[i].length;
                    if (ratio < 0.65) continue;
                    double dist = MeasureCurveDistance3d(candidates[i].curve, candidates[j].curve);
                    if (dist > 2 && dist < 60) return Math.Round(dist, 2);
                }
            }
            return 0;
        }

        private static double MeasureCurveDistance3d(DBObject c1, DBObject c2)
        {
            var pts1 = SampleCurve3d(c1); var pts2 = SampleCurve3d(c2);
            if (pts1.Count < 3 || pts2.Count < 3) return 0;
            double total = 0; int count = 0;
            foreach (var p1 in pts1)
            {
                double minD = double.MaxValue;
                foreach (var p2 in pts2) { double d = p1.DistanceTo(p2); if (d < minD) minD = d; }
                if (minD < 200) { total += minD; count++; }
            }
            if (count < pts1.Count / 3) return 0;
            double avg = total / count;
            int consistent = 0;
            foreach (var p1 in pts1)
            {
                double minD = double.MaxValue;
                foreach (var p2 in pts2) { double d = p1.DistanceTo(p2); if (d < minD) minD = d; }
                if (Math.Abs(minD - avg) < avg * 0.35) consistent++;
            }
            return consistent >= pts1.Count / 2 ? avg : 0;
        }

        private static List<Point3d> SampleCurve3d(DBObject obj)
        {
            var pts = new List<Point3d>(); int n = 20;
            if (obj is Spline sp)
            { try { double s = sp.StartParam, e = sp.EndParam; for (int i = 0; i <= n; i++) pts.Add(sp.GetPointAtParameter(s + (e - s) * i / n)); } catch { } }
            else if (obj is Arc arc)
            {
                double sa = arc.StartAngle, ea = arc.EndAngle; if (ea < sa) ea += 2 * Math.PI;
                for (int i = 0; i <= n; i++) { double a = sa + (ea - sa) * i / n; pts.Add(new Point3d(arc.Center.X + arc.Radius * Math.Cos(a), arc.Center.Y + arc.Radius * Math.Sin(a), arc.Center.Z)); }
            }
            else if (obj is Line ln)
            { for (int i = 0; i <= n; i++) { double t = (double)i / n; pts.Add(new Point3d(ln.StartPoint.X + t * (ln.EndPoint.X - ln.StartPoint.X), ln.StartPoint.Y + t * (ln.EndPoint.Y - ln.StartPoint.Y), ln.StartPoint.Z + t * (ln.EndPoint.Z - ln.StartPoint.Z))); } }
            return pts;
        }

        #endregion

        #region 递归Explode + 曲线采样

        private static List<DBObject> RecursiveExplodeAll(DBObject entity)
        {
            var result = new List<DBObject>(); DoExplode(entity, result, 0); return result;
        }

        private static void DoExplode(DBObject entity, List<DBObject> result, int depth)
        {
            if (depth > MaxExplodeDepth) return;
            if (entity is Line || entity is Arc || entity is Circle || entity is Spline || entity is Ellipse)
            { result.Add(entity); return; }
            if (entity is Polyline pline)
            {
                for (int i = 0; i < pline.NumberOfVertices; i++)
                {
                    try
                    {
                        if (pline.GetSegmentType(i) == SegmentType.Line) { var s = pline.GetLineSegment2dAt(i); result.Add(new Line(To3d(s.StartPoint), To3d(s.EndPoint))); }
                        else if (pline.GetSegmentType(i) == SegmentType.Arc) { var s = pline.GetArcSegment2dAt(i); double sa = (s.StartPoint - s.Center).Angle, ea = (s.EndPoint - s.Center).Angle; result.Add(new Arc(To3d(s.Center), s.Radius, sa, ea)); }
                    }
                    catch { }
                }
                return;
            }
            if (entity is Entity ent) { try { var sub = new DBObjectCollection(); ent.Explode(sub); foreach (DBObject c in sub) DoExplode(c, result, depth + 1); sub.Dispose(); } catch { } }
        }

        private static List<DBObject> FilterCurvesByZ(List<DBObject> curves, double targetZ, double tol)
        {
            var result = new List<DBObject>();
            foreach (var obj in curves)
            {
                if (obj is not Entity ent) continue;
                try { var e = ent.GeometricExtents; double zR = Math.Abs(e.MaxPoint.Z - e.MinPoint.Z), zm = (e.MaxPoint.Z + e.MinPoint.Z) / 2; if (zR < tol * 3 && Math.Abs(zm - targetZ) < tol * 2.5) result.Add(obj); }
                catch { }
            }
            return result;
        }

        private static List<Point2d> SampleCurve(DBObject obj)
        {
            var pts = new List<Point2d>();
            if (obj is Line l) { pts.Add(new Point2d(l.StartPoint.X, l.StartPoint.Y)); pts.Add(new Point2d(l.EndPoint.X, l.EndPoint.Y)); }
            else if (obj is Arc a) { double sa = a.StartAngle, ea = a.EndAngle; if (ea < sa) ea += 2 * Math.PI; for (int i = 0; i <= ArcSamples; i++) { double ang = sa + (ea - sa) * i / ArcSamples; pts.Add(new Point2d(a.Center.X + a.Radius * Math.Cos(ang), a.Center.Y + a.Radius * Math.Sin(ang))); } }
            else if (obj is Circle c) { for (int i = 0; i < ArcSamples; i++) { double ang = 2 * Math.PI * i / ArcSamples; pts.Add(new Point2d(c.Center.X + c.Radius * Math.Cos(ang), c.Center.Y + c.Radius * Math.Sin(ang))); } }
            else if (obj is Spline sp) { try { double s = sp.StartParam, e = sp.EndParam; for (int i = 0; i <= SplineSamples; i++) { var p = sp.GetPointAtParameter(s + (e - s) * i / SplineSamples); pts.Add(new Point2d(p.X, p.Y)); } } catch { } }
            return pts;
        }

        private static List<Point2d> ChainAndSample(List<DBObject> curves)
        {
            // ★ 凹边保留：把每条曲线展开为 2D 线段，再用 ProfileExtractionMath 链接闭环。
            //   只有在所有容差都失败时，才退回凸包（最差情况下也比丢失轮廓好）。
            var edges = new List<ProfileExtractionMath.Edge>();
            foreach (var obj in curves)
            {
                var pts = SampleCurve(obj);
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    edges.Add(new ProfileExtractionMath.Edge
                    {
                        Start = new ProfileExtractionMath.PtXY(pts[i].X, pts[i].Y),
                        End = new ProfileExtractionMath.PtXY(pts[i + 1].X, pts[i + 1].Y)
                    });
                }
            }

            // 多容差重试（5 档：0.05 / 0.5 / 1 / 5 / 20 mm）
            double[] tols = { 0.05, 0.5, 1.0, 5.0, 20.0 };
            foreach (double tol in tols)
            {
                var rep = ProfileExtractionMath.ChainEdgesToProfile(edges, tol);
                if (rep.Success && rep.OuterLoop != null && rep.OuterLoop.Count >= 3)
                    return rep.OuterLoop.Select(p => new Point2d(p.X, p.Y)).ToList();
            }

            // 兜底：凸包（损失凹特征，但保证返回有效形状）
            var allPts = new List<Point2d>();
            foreach (var obj in curves) allPts.AddRange(SampleCurve(obj));
            allPts = Dedup2d(allPts);
            if (allPts.Count < 3) return null;
            return ConvexHull2d(allPts);
        }

        #endregion

        #region 弧段检测（去重）

        private class ArcInfo { public double CenterX, CenterY, Radius, Sweep; public Vector3d Normal; }
        private class LineInfo { public double X1, Y1, Z1, X2, Y2, Z2, Length; }
        private class ArcPairInfo { public double InnerR, OuterR, Sweep; }

        private static List<ArcInfo> ExtractUniqueArcs(List<DBObject> curves)
        {
            var raw = new List<ArcInfo>();
            foreach (var obj in curves)
                if (obj is Arc arc) { double sw = arc.EndAngle - arc.StartAngle; if (sw < 0) sw += 2 * Math.PI; raw.Add(new ArcInfo { CenterX = arc.Center.X, CenterY = arc.Center.Y, Radius = arc.Radius, Sweep = sw, Normal = arc.Normal }); }
            var unique = new List<ArcInfo>();
            foreach (var a in raw) { bool dup = unique.Any(u => Math.Sqrt(Sq(a.CenterX - u.CenterX) + Sq(a.CenterY - u.CenterY)) < DedupTol && Math.Abs(a.Radius - u.Radius) < DedupTol && Math.Abs(a.Sweep - u.Sweep) < 0.05); if (!dup) unique.Add(a); }
            return unique;
        }

        private static List<LineInfo> ExtractUniqueLines(List<DBObject> curves)
        {
            var raw = new List<LineInfo>();
            foreach (var obj in curves)
                if (obj is Line l) { double len = l.StartPoint.DistanceTo(l.EndPoint); if (len > 0.1) raw.Add(new LineInfo { X1 = l.StartPoint.X, Y1 = l.StartPoint.Y, Z1 = l.StartPoint.Z, X2 = l.EndPoint.X, Y2 = l.EndPoint.Y, Z2 = l.EndPoint.Z, Length = len }); }
            var unique = new List<LineInfo>();
            foreach (var a in raw) { bool dup = unique.Any(u => (Math.Sqrt(Sq(a.X1 - u.X1) + Sq(a.Y1 - u.Y1)) < DedupTol && Math.Sqrt(Sq(a.X2 - u.X2) + Sq(a.Y2 - u.Y2)) < DedupTol) || (Math.Sqrt(Sq(a.X1 - u.X2) + Sq(a.Y1 - u.Y2)) < DedupTol && Math.Sqrt(Sq(a.X2 - u.X1) + Sq(a.Y2 - u.Y1)) < DedupTol)); if (!dup) unique.Add(a); }
            return unique;
        }

        private static List<ArcPairInfo> PairInnerOuterArcs(List<ArcInfo> arcs)
        {
            var pairs = new List<ArcPairInfo>(); var used = new bool[arcs.Count];
            for (int i = 0; i < arcs.Count; i++) { if (used[i]) continue; for (int j = i + 1; j < arcs.Count; j++) { if (used[j]) continue; double dC = Math.Sqrt(Sq(arcs[i].CenterX - arcs[j].CenterX) + Sq(arcs[i].CenterY - arcs[j].CenterY)); if (dC < 3 && Math.Abs(arcs[i].Sweep - arcs[j].Sweep) < 0.15 && Math.Abs(arcs[i].Radius - arcs[j].Radius) > 0.5) { pairs.Add(new ArcPairInfo { InnerR = Math.Min(arcs[i].Radius, arcs[j].Radius), OuterR = Math.Max(arcs[i].Radius, arcs[j].Radius), Sweep = (arcs[i].Sweep + arcs[j].Sweep) / 2 }); used[i] = used[j] = true; break; } } }
            return pairs;
        }

        private static List<double> FilterStraightSegments(List<LineInfo> lines, double thickness) =>
            lines.Where(l => Math.Abs(l.Length - thickness) > 5 && l.Length > thickness * 1.3).Select(l => Math.Round(l.Length, 1)).Distinct().OrderByDescending(l => l).Take(4).ToList();

        private static double DetectWidthFromArcs(Solid3d solid, List<ArcInfo> arcs)
        {
            if (arcs.Count == 0) return 0;
            var n = arcs[0].Normal; Extents3d ext = solid.GeometricExtents;
            double px = Math.Abs(n.X) * (ext.MaxPoint.X - ext.MinPoint.X), py = Math.Abs(n.Y) * (ext.MaxPoint.Y - ext.MinPoint.Y), pz = Math.Abs(n.Z) * (ext.MaxPoint.Z - ext.MinPoint.Z);
            double max = Math.Max(px, Math.Max(py, pz));
            if (max == pz) return ext.MaxPoint.Z - ext.MinPoint.Z;
            if (max == py) return ext.MaxPoint.Y - ext.MinPoint.Y;
            return ext.MaxPoint.X - ext.MinPoint.X;
        }

        private static double CalcSplineLength(Spline sp)
        { try { double s = sp.StartParam, e = sp.EndParam, len = 0; Point3d prev = sp.GetPointAtParameter(s); for (int i = 1; i <= SplineSamples; i++) { var cur = sp.GetPointAtParameter(s + (e - s) * i / SplineSamples); len += prev.DistanceTo(cur); prev = cur; } return len; } catch { return 0; } }

        private static bool CalcByVolumeFallback(Solid3d solid, out double ul, out double w, out double t)
        { ul = w = t = 0; try { double vol = solid.MassProperties.Volume; double[] dims = SortedBboxDims(solid.GeometricExtents); t = dims[0]; w = dims[1]; if (t > 0 && w > 0) { ul = Math.Round(vol / (w * t), 2); t = Math.Round(t, 2); w = Math.Round(w, 2); return ul > 0; } return false; } catch { return false; } }

        #endregion

        #region Polyline/曲线实体轮廓

        private static List<Pt2d> ExtractPolylineOutline(Polyline pline, double boxLen, double boxWid)
        {
            var pts = new List<Point2d>();
            for (int i = 0; i < pline.NumberOfVertices; i++)
            {
                double bulge = pline.GetBulgeAt(i);
                var pt = pline.GetPoint2dAt(i);
                if (Math.Abs(bulge) > 1e-9 && i < pline.NumberOfVertices - 1)
                { var nxt = pline.GetPoint2dAt((i + 1) % pline.NumberOfVertices); var arcPts = SampleBulgeArc(pt, nxt, bulge, ArcSamples); for (int j = (pts.Count > 0 ? 1 : 0); j < arcPts.Count; j++) pts.Add(arcPts[j]); }
                else pts.Add(pt);
            }
            return pts.Count >= 3 ? Normalize(pts, boxLen, boxWid) : RectOutline(boxLen, boxWid);
        }

        private static List<Point2d> SampleBulgeArc(Point2d start, Point2d end, double bulge, int samples)
        {
            var r = new List<Point2d>(); double dx = end.X - start.X, dy = end.Y - start.Y, chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-9) { r.Add(start); return r; }
            double sag = Math.Abs(bulge) * chord / 2, rad = (chord * chord / 4 + sag * sag) / (2 * sag);
            double mx = (start.X + end.X) / 2, my = (start.Y + end.Y) / 2, nx = -dy / chord, ny = dx / chord, d = rad - sag;
            double sign = bulge > 0 ? 1 : -1, ccx = mx + sign * d * nx, ccy = my + sign * d * ny;
            double sa = Math.Atan2(start.Y - ccy, start.X - ccx), ea = Math.Atan2(end.Y - ccy, end.X - ccx);
            if (bulge > 0) { if (ea < sa) ea += 2 * Math.PI; } else { if (ea > sa) ea -= 2 * Math.PI; }
            for (int i = 0; i <= samples; i++) { double t = (double)i / samples, a = sa + t * (ea - sa); r.Add(new Point2d(ccx + rad * Math.Cos(a), ccy + rad * Math.Sin(a))); }
            return r;
        }

        private static List<Pt2d> ExtractCurveEntityOutline(Entity entity, double bL, double bW)
        {
            var pts = new List<Point2d>();
            if (entity is Circle c) { for (int i = 0; i < ArcSamples; i++) { double a = 2 * Math.PI * i / ArcSamples; pts.Add(new Point2d(c.Center.X + c.Radius * Math.Cos(a), c.Center.Y + c.Radius * Math.Sin(a))); } }
            else if (entity is Ellipse e) { for (int i = 0; i < ArcSamples; i++) { double a = 2 * Math.PI * i / ArcSamples; pts.Add(new Point2d(e.Center.X + e.MajorRadius * Math.Cos(a), e.Center.Y + e.MinorRadius * Math.Sin(a))); } }
            else if (entity is Spline s) { try { double sp = s.StartParam, ep = s.EndParam; for (int i = 0; i <= SplineSamples; i++) { var p = s.GetPointAtParameter(sp + (ep - sp) * i / SplineSamples); pts.Add(new Point2d(p.X, p.Y)); } } catch { } }
            return pts.Count >= 3 ? Normalize(pts, bL, bW) : RectOutline(bL, bW);
        }

        #endregion

        #region 标准化

        /// <summary>OBB对齐 → 平移至原点 → 缩放匹配目标尺寸</summary>
        private static List<Pt2d> Normalize(List<Point2d> pts, double bL, double bW)
        {
            return Normalize(pts, bL, bW, out _);
        }

        private static List<Pt2d> Normalize(List<Point2d> pts, double bL, double bW, out NormalizeMapper mapper)
        {
            mapper = default;
            if (pts.Count < 3) return RectOutline(bL, bW);
            pts = Dedup2d(pts);
            if (pts.Count < 3) return RectOutline(bL, bW);

            // OBB对齐
            double angle = FindMinBBoxAngle(pts);
            if (Math.Abs(angle) > 0.01)
            {
                double cs = Math.Cos(angle), sn = Math.Sin(angle);
                pts = pts.Select(p => new Point2d(p.X * cs + p.Y * sn, -p.X * sn + p.Y * cs)).ToList();
            }

            double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
            double maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
            double actW = maxX - minX, actH = maxY - minY;
            var r = pts.Select(p => new Pt2d(p.X - minX, p.Y - minY)).ToList();

            bool swapped = false;
            if (actW < actH) { r = r.Select(p => new Pt2d(actH - p.Y, p.X)).ToList(); (actW, actH) = (actH, actW); swapped = true; }

            double scX = 1.0, scY = 1.0;
            if (bL > 0.1 && bW > 0.1 && actW > 0.1 && actH > 0.1)
            {
                scX = bL / actW; scY = bW / actH;
                r = r.Select(p => new Pt2d(p.X * scX, p.Y * scY)).ToList();
            }

            mapper = new NormalizeMapper
            {
                Angle = angle,
                MinX = minX,
                MinY = minY,
                ActW = actW,
                ActH = actH,
                Swapped = swapped,
                ScaleX = scX,
                ScaleY = scY
            };
            return r;
        }

        private static List<Pt2d> MapByNormalizer(List<Point2d> raw, NormalizeMapper mapper)
        {
            var r = new List<Pt2d>();
            if (raw == null || raw.Count < 3) return r;

            double cs = Math.Cos(mapper.Angle), sn = Math.Sin(mapper.Angle);
            foreach (var p in raw)
            {
                double x = p.X * cs + p.Y * sn;
                double y = -p.X * sn + p.Y * cs;
                x -= mapper.MinX;
                y -= mapper.MinY;
                if (mapper.Swapped)
                {
                    double tx = mapper.ActH - y;
                    y = x;
                    x = tx;
                }

                x *= mapper.ScaleX;
                y *= mapper.ScaleY;
                r.Add(new Pt2d(x, y));
            }
            return r;
        }

        private static double FindMinBBoxAngle(List<Point2d> pts)
        {
            double bestAngle = 0, bestArea = double.MaxValue;
            for (int deg = 0; deg < 180; deg += 2)
            {
                double rad = deg * Math.PI / 180.0, cs = Math.Cos(rad), sn = Math.Sin(rad);
                double mnX = double.MaxValue, mxX = double.MinValue, mnY = double.MaxValue, mxY = double.MinValue;
                foreach (var p in pts) { double rx = p.X * cs + p.Y * sn, ry = -p.X * sn + p.Y * cs; if (rx < mnX) mnX = rx; if (rx > mxX) mxX = rx; if (ry < mnY) mnY = ry; if (ry > mxY) mxY = ry; }
                double area = (mxX - mnX) * (mxY - mnY);
                if (area < bestArea) { bestArea = area; bestAngle = rad; }
            }
            return bestAngle;
        }

        #endregion

        #region 辅助

        private static List<Pt2d> RectOutline(double l, double w) => new() { new(0, 0), new(l, 0), new(l, w), new(0, w) };
        private static double PolygonAreaPt(List<Pt2d> pts)
        {
            if (pts == null || pts.Count < 3) return 0;
            double area = 0;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                area += (pts[j].X - pts[i].X) * (pts[j].Y + pts[i].Y);
            return area / 2.0;
        }
        private static double Sq(double v) => v * v;
        private static double[] SortedBboxDims(Extents3d e) { double[] d = { e.MaxPoint.X - e.MinPoint.X, e.MaxPoint.Y - e.MinPoint.Y, e.MaxPoint.Z - e.MinPoint.Z }; Array.Sort(d); return d; }
        private static Point3d To3d(Point2d p) => new(p.X, p.Y, 0);
        private static List<Point2d> Dedup2d(List<Point2d> pts)
        {
            if (pts.Count <= 1) return pts;
            var r = new List<Point2d> { pts[0] };
            for (int i = 1; i < pts.Count; i++) { double dx = pts[i].X - r[r.Count - 1].X, dy = pts[i].Y - r[r.Count - 1].Y; if (dx * dx + dy * dy > 0.01) r.Add(pts[i]); }
            return r;
        }
        private static void DisposeCurves(List<DBObject> c) { foreach (var o in c) try { if (o is Entity e && !e.IsDisposed) e.Dispose(); } catch { } }

        #endregion
    }
}
