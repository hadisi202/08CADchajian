using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin;

/// <summary>
/// 把任意 3D 实体板件摆正到与 WCS 的 XY 平面绝对平行（板件法向量与 Z 轴夹角 &lt; 0.001°）。
/// 既是 [CommandMethod("BZBJ")] 的算法核心，也供 PAIBAN 流程在 FLATSHOT 投影前调用。
///
/// 算法：
///   1. 顶点采集（GripPoints + 递归Explode 端点 + BBox 兜底）
///   2. PCA 协方差矩阵特征分解 → 厚/中/长三轴
///   3. 旋转最小特征向量到 +Z（保证投影基准面为 XY）
///   4. 在 XY 平面内做绕 Z 轴的“最长边对齐”最小旋转（≤90°）
///   5. 验证：旋转后再次取顶点，校验厚轴与 Z 轴夹角
/// </summary>
public static class BzbjAlignmentService
{
    public sealed class AlignmentResult
    {
        public bool Success { get; set; }
        public double NormalAngleToZDeg { get; set; }
        public double InPlaneRotationDeg { get; set; }
        public double InPlaneErrorDeg { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// 将 solid 旋转到与 XY 平面平行（已 ForWrite 打开的实体）。
    /// </summary>
    /// <param name="solid">已经 ForWrite 打开的 Solid3d</param>
    /// <param name="normalToleranceDeg">合格阈值，默认 0.001°</param>
    public static AlignmentResult AlignToWorldXY(Solid3d solid, double normalToleranceDeg = 0.001)
    {
        var result = new AlignmentResult();
        if (solid == null || solid.IsErased)
        {
            result.Reason = "实体无效";
            return result;
        }

        try
        {
            // ─── 阶段1: BBox 快速识别（仅适用于轴对齐板件） ────────────
            if (TryAlignByExtents(solid, out var extentsCenter, out var extentsThickAxis))
            {
                RotateAxisToWorldZ(solid, extentsCenter, extentsThickAxis, 1e-9);
                AlignInPlaneAndVerify(solid, result, normalToleranceDeg);
                if (result.Success) return result;
            }

            // ─── 阶段2: 迭代精对齐 ──────────────────────────────────
            // 不论倾斜板还是 BBox 阶段未达标的板，都进入迭代:
            //   每次迭代依次尝试 MassProperties（最准）→ 稠密 PCA，
            //   把残余倾角拉到 0；最多 5 轮，达标即返回。
            for (int iter = 0; iter < 5; iter++)
            {
                bool anyApplied = false;
                if (TryAlignByMassProperties(solid)) anyApplied = true;
                else if (TryAlignByDensePCA(solid)) anyApplied = true;

                AlignInPlaneAndVerify(solid, result, normalToleranceDeg);
                if (result.Success) return result;
                if (!anyApplied) break;
                // 若残余倾角已经很小，无需再迭代（避免数值抖动）
                if (result.NormalAngleToZDeg < normalToleranceDeg * 5) break;
            }

            if (string.IsNullOrEmpty(result.Reason))
                result.Reason = $"残余法向夹角 {result.NormalAngleToZDeg:F6}° > 阈值 {normalToleranceDeg:F4}°";
            return result;
        }
        catch (System.Exception ex)
        {
            result.Reason = "异常: " + ex.Message;
            return result;
        }
    }

    /// <summary>
    /// 用稠密采样点 PCA 找薄板法向并旋转。
    /// 调用前 CollectVertices 已经沿曲线密集采样，足够支撑 PCA 在曲面板件上不退化。
    /// </summary>
    public static bool TryAlignByDensePCA(Solid3d solid)
    {
        try
        {
            var verts = CollectVertices(solid);
            if (verts.Count < 8) return false;
            var centroid = Centroid(verts);
            if (!ComputeAxesByPCA(verts, centroid, out _, out _, out var thickAxis)) return false;
            return RotateAxisToWorldZ(solid, centroid, thickAxis, 1e-9);
        }
        catch { return false; }
    }

    /// <summary>
    /// 在已对齐厚轴的 solid 上，做 XY 平面内的最长边对齐（绕 Z 旋转），随后验证法向夹角。
    /// 验证失败时不抛异常，把诊断写入 result。
    /// </summary>
    private static void AlignInPlaneAndVerify(Solid3d solid, AlignmentResult result, double normalToleranceDeg)
    {
        try
        {
            var verts2 = CollectVertices(solid);
            if (verts2.Count >= 2)
            {
                var c2 = Centroid(verts2);
                var pts2 = verts2.Select(v => new BzbjAlignmentMath.Pt2(v.X, v.Y)).ToList();
                if (BzbjAlignmentMath.TryGetLongestEdgeDirection(pts2, out var dir, out _))
                {
                    double rotDeg = BzbjAlignmentMath.ComputeMinimalRotationToXAxisDeg(dir);
                    double rad = rotDeg * Math.PI / 180.0;
                    if (Math.Abs(rad) > 1e-12)
                        solid.TransformBy(Matrix3d.Rotation(rad, Vector3d.ZAxis, c2));
                    result.InPlaneRotationDeg = rotDeg;
                }
            }
        }
        catch { }

        // 用 MassProperties 校验最准（内核精度），失败则用 PCA
        double normalDeg = MeasureNormalAngleByMassProperties(solid);
        if (normalDeg < 0)
        {
            var verts3 = CollectVertices(solid);
            if (verts3.Count >= 4 &&
                ComputeAxesByPCA(verts3, Centroid(verts3),
                    out var longF, out _, out var thickF))
            {
                normalDeg = AngleBetweenDeg(thickF, Vector3d.ZAxis);
                result.InPlaneErrorDeg = InPlaneAxisErrorDeg(longF);
            }
        }
        if (normalDeg >= 0) result.NormalAngleToZDeg = normalDeg;
        result.Success = result.NormalAngleToZDeg <= normalToleranceDeg;
        if (!result.Success && string.IsNullOrEmpty(result.Reason))
            result.Reason = $"法向夹角 {result.NormalAngleToZDeg:F6}° > 阈值 {normalToleranceDeg:F4}°";
    }

    /// <summary>
    /// 直接基于实体几何包围盒识别厚轴：取 X/Y/Z 三向尺寸里最小的，必须显著小于另外两个
    /// （厚 ≤ 长/宽的 30% 才算"明显是厚度方向"）。这一手段对曲面板/样条板/带洞板都有效，
    /// 因为不依赖顶点采样质量。
    /// 仅当板件本身已经轴对齐（即三向之一明显是厚度）才返回 true。
    /// </summary>
    public static bool TryAlignByExtents(Solid3d solid, out Point3d centerOut, out Vector3d thickAxisOut)
    {
        centerOut = Point3d.Origin;
        thickAxisOut = Vector3d.ZAxis;
        try
        {
            var ext = ((Entity)solid).GeometricExtents;
            double dx = ext.MaxPoint.X - ext.MinPoint.X;
            double dy = ext.MaxPoint.Y - ext.MinPoint.Y;
            double dz = ext.MaxPoint.Z - ext.MinPoint.Z;
            if (dx <= 0 || dy <= 0 || dz <= 0) return false;

            double minD = Math.Min(dx, Math.Min(dy, dz));
            double maxD = Math.Max(dx, Math.Max(dy, dz));

            // 厚度必须明显小于长宽（薄板特征）
            if (minD > maxD * 0.3) return false;

            centerOut = new Point3d(
                (ext.MinPoint.X + ext.MaxPoint.X) * 0.5,
                (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5,
                (ext.MinPoint.Z + ext.MaxPoint.Z) * 0.5);

            if (Math.Abs(dz - minD) < 1e-9) thickAxisOut = Vector3d.ZAxis;
            else if (Math.Abs(dx - minD) < 1e-9) thickAxisOut = Vector3d.XAxis;
            else thickAxisOut = Vector3d.YAxis;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 用 AutoCAD 内核的 MassProperties.PrincipalAxes/PrincipalMoments 找薄板的法向。
    /// 对于薄板，绕厚度轴旋转的转动惯量最大（质量分布最远）；
    /// 等价地，PrincipalMoments 最大对应的 PrincipalAxis 即法向。
    /// 内核基于真实几何积分计算，曲面/样条/任意倾斜都精确。
    /// </summary>
    public static bool TryAlignByMassProperties(Solid3d solid)
    {
        if (!TryReadMassPrincipalAxes(solid, out var axes, out var moments, out var center)) return false;

        int normalIdx = 0;
        if (moments[1] > moments[normalIdx]) normalIdx = 1;
        if (moments[2] > moments[normalIdx]) normalIdx = 2;

        var thickAxis = axes[normalIdx].GetNormal();
        return RotateAxisToWorldZ(solid, center, thickAxis, 1e-9);
    }

    /// <summary>
    /// 读取 Solid3d.MassProperties 的主轴/主矩。Solid3dMassProperties 在不同 ACAD 版本
    /// 既可能是 Properties 也可能是 Fields，此处两种都尝试。
    /// </summary>
    private static bool TryReadMassPrincipalAxes(Solid3d solid,
        out Vector3d[] axes, out double[] moments, out Point3d center)
    {
        axes = null;
        moments = null;
        center = Point3d.Origin;
        try
        {
            object mp = solid.MassProperties;
            if (mp == null) return false;
            // Volume 检查（先尝试属性，再字段）
            double volume = ReadMember<double>(mp, "Volume");
            if (volume <= 1e-6) return false;

            object paObj = ReadMember<object>(mp, "PrincipalAxes");
            object pmObj = ReadMember<object>(mp, "PrincipalMoments");
            if (paObj == null || pmObj == null) return false;

            axes = ExtractVector3dArray(paObj);
            moments = ExtractVector3dValues(pmObj);
            if (axes == null || moments == null || axes.Length < 3 || moments.Length < 3) return false;

            try { center = ReadMember<Point3d>(mp, "Centroid"); }
            catch { center = Point3d.Origin; }
            return true;
        }
        catch { return false; }
    }

    private static T ReadMember<T>(object obj, string name)
    {
        var t = obj.GetType();
        var p = t.GetProperty(name);
        if (p != null)
        {
            try
            {
                var v = p.GetValue(obj);
                if (v is T tv) return tv;
                if (v != null && typeof(T) == typeof(object)) return (T)v;
            }
            catch { }
        }
        var f = t.GetField(name);
        if (f != null)
        {
            try
            {
                var v = f.GetValue(obj);
                if (v is T tv) return tv;
                if (v != null && typeof(T) == typeof(object)) return (T)v;
            }
            catch { }
        }
        return default;
    }

    private static Vector3d[] ExtractVector3dArray(object obj)
    {
        if (obj is Vector3d[] arr) return arr;
        if (obj is Matrix3d m)
            return new[] {
                new Vector3d(m[0,0], m[1,0], m[2,0]),
                new Vector3d(m[0,1], m[1,1], m[2,1]),
                new Vector3d(m[0,2], m[1,2], m[2,2])
            };
        // 元组类型 (Vector3d, Vector3d, Vector3d)
        try
        {
            var t = obj.GetType();
            var i1 = t.GetField("Item1") ?? t.GetField("item1");
            var i2 = t.GetField("Item2") ?? t.GetField("item2");
            var i3 = t.GetField("Item3") ?? t.GetField("item3");
            if (i1 != null && i2 != null && i3 != null)
            {
                if (i1.GetValue(obj) is Vector3d v1 &&
                    i2.GetValue(obj) is Vector3d v2 &&
                    i3.GetValue(obj) is Vector3d v3)
                    return new[] { v1, v2, v3 };
            }
        }
        catch { }
        return null;
    }

    private static double[] ExtractVector3dValues(object obj)
    {
        if (obj is Vector3d v) return new[] { v.X, v.Y, v.Z };
        if (obj is double[] d && d.Length >= 3) return d;
        return null;
    }

    /// <summary>仅做测量（不旋转）：用 MassProperties 测当前 solid 法向与 +Z 的夹角。失败返回 -1。</summary>
    public static double MeasureNormalAngleByMassProperties(Solid3d solid)
    {
        if (!TryReadMassPrincipalAxes(solid, out var axes, out var moments, out _)) return -1;
        int idx = 0;
        if (moments[1] > moments[idx]) idx = 1;
        if (moments[2] > moments[idx]) idx = 2;
        return AngleBetweenDeg(axes[idx], Vector3d.ZAxis);
    }

    #region 顶点采集

    public static List<Point3d> CollectVertices(Solid3d solid)
    {
        var result = new List<Point3d>();
        var seen = new HashSet<long>();

        try
        {
            var grips = new Point3dCollection();
            var sm = new IntegerCollection();
            var gi = new IntegerCollection();
            solid.GetGripPoints(grips, sm, gi);
            foreach (Point3d p in grips)
            {
                long k = HashKey(p);
                if (seen.Add(k)) result.Add(p);
            }
        }
        catch { }

        try
        {
            var sub = new DBObjectCollection();
            ((Entity)solid).Explode(sub);
            foreach (DBObject obj in sub)
            {
                ExtractCurveVerts(obj, result, seen);
                try { (obj as IDisposable)?.Dispose(); } catch { }
            }
            sub.Dispose();
        }
        catch { }

        if (result.Count < 4)
        {
            try
            {
                var e = ((Entity)solid).GeometricExtents;
                var corners = new[]
                {
                    e.MinPoint, e.MaxPoint,
                    new Point3d(e.MinPoint.X, e.MaxPoint.Y, e.MinPoint.Z),
                    new Point3d(e.MaxPoint.X, e.MinPoint.Y, e.MinPoint.Z),
                    new Point3d(e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.Z),
                    new Point3d(e.MaxPoint.X, e.MaxPoint.Y, e.MinPoint.Z),
                    new Point3d(e.MinPoint.X, e.MaxPoint.Y, e.MaxPoint.Z),
                    new Point3d(e.MaxPoint.X, e.MinPoint.Y, e.MaxPoint.Z)
                };
                foreach (var c in corners)
                    if (seen.Add(HashKey(c))) result.Add(c);
            }
            catch { }
        }
        return result;
    }

    private static void ExtractCurveVerts(DBObject obj, List<Point3d> result, HashSet<long> seen)
    {
        if (obj is Region region)
        {
            try
            {
                var sub = new DBObjectCollection();
                ((Entity)region).Explode(sub);
                foreach (DBObject c in sub)
                {
                    ExtractCurveVerts(c, result, seen);
                    try { (c as IDisposable)?.Dispose(); } catch { }
                }
                sub.Dispose();
            }
            catch { }
            return;
        }

        if (obj is Polyline pl)
        {
            for (int i = 0; i < pl.NumberOfVertices; i++)
                AddPt(pl.GetPoint3dAt(i), result, seen);
            // 沿弧段稠密采样
            if (pl.Length > 0)
            {
                int dense = Math.Max(8, Math.Min(64, (int)(pl.Length / 20.0)));
                SampleAlongCurve(pl, dense, result, seen);
            }
            return;
        }

        if (obj is Curve cv)
        {
            try { AddPt(cv.StartPoint, result, seen); } catch { }
            try { AddPt(cv.EndPoint, result, seen); } catch { }
            // 沿曲线稠密采样：直线给少量，弧/样条给很多
            int samples = obj switch
            {
                Line _ => 4,
                Arc _ => 32,
                Circle _ => 32,
                Ellipse _ => 32,
                Spline _ => 48,
                _ => 12
            };
            SampleAlongCurve(cv, samples, result, seen);
        }
    }

    /// <summary>
    /// 沿曲线均匀采样指定数量的点，加入候选集合。这是为了让 PCA 在曲线类板件上不退化：
    /// 半圆/扇形板若只有几个端点，PCA 会找错主轴（顶点几乎共线）。
    /// </summary>
    private static void SampleAlongCurve(Curve cv, int samples, List<Point3d> result, HashSet<long> seen)
    {
        try
        {
            double startParam = cv.StartParam;
            double endParam = cv.EndParam;
            for (int i = 1; i < samples; i++)
            {
                double t = startParam + (endParam - startParam) * i / samples;
                try
                {
                    var p = cv.GetPointAtParameter(t);
                    AddPt(p, result, seen);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void AddPt(Point3d p, List<Point3d> result, HashSet<long> seen)
    {
        long k = HashKey(p);
        if (seen.Add(k)) result.Add(p);
    }

    private static long HashKey(Point3d p) =>
        ((long)Math.Round(p.X * 100)) * 100000000000L
        + ((long)Math.Round(p.Y * 100)) * 100000L
        + (long)Math.Round(p.Z * 100);

    private static Point3d Centroid(List<Point3d> verts)
    {
        double cx = 0, cy = 0, cz = 0;
        foreach (var v in verts) { cx += v.X; cy += v.Y; cz += v.Z; }
        return new Point3d(cx / verts.Count, cy / verts.Count, cz / verts.Count);
    }

    #endregion

    #region PCA + 旋转

    public static bool ComputeAxesByPCA(
        List<Point3d> verts, Point3d centroid,
        out Vector3d lenAxis, out Vector3d midAxis, out Vector3d thickAxis)
    {
        lenAxis = Vector3d.XAxis;
        midAxis = Vector3d.YAxis;
        thickAxis = Vector3d.ZAxis;
        if (verts == null || verts.Count < 4) return false;

        double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
        foreach (var p in verts)
        {
            double dx = p.X - centroid.X, dy = p.Y - centroid.Y, dz = p.Z - centroid.Z;
            cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
            cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
        }
        int n = verts.Count;
        var cov = new double[,]
        {
            { cxx / n, cxy / n, cxz / n },
            { cxy / n, cyy / n, cyz / n },
            { cxz / n, cyz / n, czz / n }
        };

        Eigen3x3(cov, out var evals, out var evecs);
        int iMin = 0, iMax = 0;
        for (int i = 1; i < 3; i++)
        {
            if (evals[i] < evals[iMin]) iMin = i;
            if (evals[i] > evals[iMax]) iMax = i;
        }
        if (iMin == iMax) return false;
        int iMid = 3 - iMin - iMax;

        thickAxis = new Vector3d(evecs[0, iMin], evecs[1, iMin], evecs[2, iMin]).GetNormal();
        lenAxis = new Vector3d(evecs[0, iMax], evecs[1, iMax], evecs[2, iMax]).GetNormal();
        midAxis = new Vector3d(evecs[0, iMid], evecs[1, iMid], evecs[2, iMid]).GetNormal();
        if (lenAxis.CrossProduct(midAxis).DotProduct(thickAxis) < 0)
            midAxis = midAxis.Negate();
        return true;
    }

    public static bool RotateAxisToWorldZ(Entity ent, Point3d center, Vector3d axis, double tol)
    {
        var n = axis.GetNormal();
        double dot = Math.Max(-1.0, Math.Min(1.0, n.DotProduct(Vector3d.ZAxis)));
        if (Math.Abs(dot - 1.0) < tol) return true;
        if (Math.Abs(dot + 1.0) < tol)
        {
            ent.TransformBy(Matrix3d.Rotation(Math.PI, Vector3d.XAxis, center));
            return true;
        }
        var rotAxis = n.CrossProduct(Vector3d.ZAxis);
        if (rotAxis.Length < tol) return false;
        double ang = n.GetAngleTo(Vector3d.ZAxis, rotAxis);
        ent.TransformBy(Matrix3d.Rotation(ang, rotAxis.GetNormal(), center));
        return true;
    }

    public static double AngleBetweenDeg(Vector3d a, Vector3d b)
    {
        var an = a.GetNormal();
        var bn = b.GetNormal();
        double dot = Math.Max(-1.0, Math.Min(1.0, Math.Abs(an.DotProduct(bn))));
        return Math.Acos(dot) * 180.0 / Math.PI;
    }

    private static double InPlaneAxisErrorDeg(Vector3d axis)
    {
        var v = new Vector3d(axis.X, axis.Y, 0);
        if (v.Length < 1e-9) return 90.0;
        v = v.GetNormal();
        double ax = Math.Abs(v.DotProduct(Vector3d.XAxis));
        double ay = Math.Abs(v.DotProduct(Vector3d.YAxis));
        double best = Math.Max(ax, ay);
        best = Math.Max(-1.0, Math.Min(1.0, best));
        return Math.Acos(best) * 180.0 / Math.PI;
    }

    private static void Eigen3x3(double[,] m, out double[] evals, out double[,] evecs)
    {
        evals = new double[3];
        evecs = new double[3, 3];
        double a00 = m[0, 0], a01 = m[0, 1], a02 = m[0, 2];
        double a11 = m[1, 1], a12 = m[1, 2], a22 = m[2, 2];
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (int iter = 0; iter < 100; iter++)
        {
            double maxOff = Math.Abs(a01);
            int pp = 0, qq = 1;
            if (Math.Abs(a02) > maxOff) { maxOff = Math.Abs(a02); pp = 0; qq = 2; }
            if (Math.Abs(a12) > maxOff) { maxOff = Math.Abs(a12); pp = 1; qq = 2; }
            if (maxOff < 1e-12) break;

            double app = pp == 0 ? a00 : pp == 1 ? a11 : a22;
            double aqq = qq == 0 ? a00 : qq == 1 ? a11 : a22;
            double apq = (pp == 0 && qq == 1) ? a01 : (pp == 0 && qq == 2) ? a02 : a12;

            double theta = 0.5 * Math.Atan2(2 * apq, aqq - app);
            double c = Math.Cos(theta), s = Math.Sin(theta);

            double newApp = c * c * app - 2 * s * c * apq + s * s * aqq;
            double newAqq = s * s * app + 2 * s * c * apq + c * c * aqq;

            if (pp == 0 && qq == 1) { a00 = newApp; a11 = newAqq; a01 = 0; }
            else if (pp == 0 && qq == 2) { a00 = newApp; a22 = newAqq; a02 = 0; }
            else { a11 = newApp; a22 = newAqq; a12 = 0; }

            // 旋转其它元素
            if (pp == 0 && qq == 1)
            {
                double na02 = c * a02 - s * a12;
                double na12 = s * a02 + c * a12;
                a02 = na02; a12 = na12;
            }
            else if (pp == 0 && qq == 2)
            {
                double na01 = c * a01 - s * a12;
                double na12 = s * a01 + c * a12;
                a01 = na01; a12 = na12;
            }
            else
            {
                double na01 = c * a01 - s * a02;
                double na02 = s * a01 + c * a02;
                a01 = na01; a02 = na02;
            }

            for (int i = 0; i < 3; i++)
            {
                double vip = v[i, pp], viq = v[i, qq];
                v[i, pp] = c * vip - s * viq;
                v[i, qq] = s * vip + c * viq;
            }
        }

        evals[0] = a00; evals[1] = a11; evals[2] = a22;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                evecs[i, j] = v[i, j];
    }

    #endregion
}
