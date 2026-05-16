using System;
using System.Collections.Generic;

namespace FurniturePlugin;

/// <summary>
/// 异形板件「截面 + 圆弧中心」尺寸算法的纯数学层。
///
/// 核心思路（与 AutoCAD 无关，便于做高速回归）：
///   1. 由 AutoCAD 端把实体在厚度方向的中截面拿到（<c>Solid3d.GetSection</c>），
///      炸成边曲线，把边曲线上的关键点（直线端点、圆弧端/中点+采样、样条采样、多段线顶点+弯弧采样）
///      收集为一个 2D 点集，连同截面所在平面的法向量一起传进来。
///   2. 当截面里**存在主圆弧**时（典型：1/2 半圆门板、1/4 圆角板、扇形板）：
///      - 用主圆弧的「圆心 → 圆弧中点」方向作为「width 轴」（径向）
///      - 与 width 轴垂直、与平面法向量正交的方向作为「length 轴」（弦向）
///      - 这样得到的是「跟着圆弧自然语义对齐的轴」，比通用 OBB 更接近行业习惯。
///   3. 当截面里没有主圆弧时（纯样条板 / 多边形板）：用截面平面内的 2D PCA
///      （协方差矩阵特征向量）取主方向。
///   4. 把所有点投影到 length / width 两个轴 → 取 min/max → 得到长 × 宽。
///   5. 厚度由 AutoCAD 端直接用 BBox 最小维度给出。
///
/// 所有方法都是纯数学（接受 (X,Y,Z) 三元组而不是 AutoCAD 类型），可以脱机做单元测试。
/// </summary>
public static class FastDimensionMath
{
    public readonly struct Vec3
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Normalize()
        {
            double l = Length;
            return l < 1e-12 ? new Vec3(1, 0, 0) : new Vec3(X / l, Y / l, Z / l);
        }
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new(
            Y * b.Z - Z * b.Y,
            Z * b.X - X * b.Z,
            X * b.Y - Y * b.X);
    }

    /// <summary>
    /// 把一组 3D 点投影到平面上的两个正交轴 (uAxis, vAxis)，返回 4 元组 [uMin, uMax, vMin, vMax]。
    /// 投影方式 = (point - origin) · axis。
    /// </summary>
    public static (double UMin, double UMax, double VMin, double VMax) ProjectExtentsInPlane(
        IReadOnlyList<Vec3> points, Vec3 origin, Vec3 uAxis, Vec3 vAxis)
    {
        if (points == null || points.Count == 0)
            return (0, 0, 0, 0);
        double uMn = double.PositiveInfinity, uMx = double.NegativeInfinity;
        double vMn = double.PositiveInfinity, vMx = double.NegativeInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            var d = points[i] - origin;
            double u = d.Dot(uAxis);
            double v = d.Dot(vAxis);
            if (u < uMn) uMn = u; if (u > uMx) uMx = u;
            if (v < vMn) vMn = v; if (v > vMx) vMx = v;
        }
        return (uMn, uMx, vMn, vMx);
    }

    /// <summary>
    /// 找一个与 <paramref name="normal"/> 不平行的"种子向量"，用 Gram-Schmidt 投到平面里，
    /// 得到平面上的标准正交基 (e1, e2)。任何把 3D 点降到 2D 用的东西都基于这个。
    /// </summary>
    public static (Vec3 e1, Vec3 e2) BuildInPlaneBasis(Vec3 normal)
    {
        var n = normal.Normalize();
        Vec3 seed = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        // e1 = seed - (seed·n) n
        double dot = seed.Dot(n);
        var e1 = (seed - n * dot).Normalize();
        var e2 = n.Cross(e1).Normalize();
        return (e1, e2);
    }

    /// <summary>
    /// 在截面平面内做 2D PCA，返回与"主方向"（最大方差）对齐的 length 轴 + 与之正交的 width 轴。
    /// 二者均位于由 <paramref name="normal"/> 定义的平面内，且互相正交。
    /// 输入点不必先去均值；本方法内部自动减去 <paramref name="origin"/>。
    /// </summary>
    public static (Vec3 lengthAxis, Vec3 widthAxis, double majorVar, double minorVar) ComputePca2D(
        IReadOnlyList<Vec3> points, Vec3 origin, Vec3 normal)
    {
        var (e1, e2) = BuildInPlaneBasis(normal);
        if (points == null || points.Count < 2)
            return (e1, e2, 0, 0);

        // 协方差矩阵
        double sxx = 0, syy = 0, sxy = 0;
        int n = points.Count;
        for (int i = 0; i < n; i++)
        {
            var d = points[i] - origin;
            double x = d.Dot(e1);
            double y = d.Dot(e2);
            sxx += x * x;
            syy += y * y;
            sxy += x * y;
        }
        sxx /= n; syy /= n; sxy /= n;

        // 2×2 实对称矩阵特征值
        double trace = sxx + syy;
        double det = sxx * syy - sxy * sxy;
        double disc = Math.Max(0.0, trace * trace * 0.25 - det);
        double l1 = trace * 0.5 + Math.Sqrt(disc);   // 大特征值（length 轴方差）
        double l2 = trace * 0.5 - Math.Sqrt(disc);   // 小特征值（width 轴方差）

        // 大特征值对应的特征向量（在 (e1,e2) 下）
        double evx, evy;
        if (Math.Abs(sxy) > 1e-12)
        {
            evx = sxy;
            evy = l1 - sxx;
        }
        else
        {
            // 对角矩阵：哪个对角元更大就取哪个方向
            if (sxx >= syy) { evx = 1; evy = 0; }
            else { evx = 0; evy = 1; }
        }
        double evLen = Math.Sqrt(evx * evx + evy * evy);
        if (evLen < 1e-12) { evx = 1; evy = 0; evLen = 1; }
        evx /= evLen; evy /= evLen;

        var lengthAxis = (e1 * evx + e2 * evy).Normalize();
        var widthAxis = normal.Cross(lengthAxis).Normalize();
        return (lengthAxis, widthAxis, l1, l2);
    }

    /// <summary>
    /// 给定截面平面内的一段「主圆弧」，用其圆心 → 弧中点的方向定义自然主轴：
    ///   - widthAxis = (arcMid - arcCenter) 的单位向量 → 半径方向（典型：半圆板的"高"）
    ///   - lengthAxis = normal × widthAxis → 弦向（典型：半圆板的"直径方向"）
    ///
    /// 失败（半径方向几乎与平面法向量平行 / 数值不稳）返回 null，由调用方退化到 PCA。
    /// </summary>
    public static (Vec3 lengthAxis, Vec3 widthAxis)? BuildAxesFromArc(
        Vec3 arcCenter, Vec3 arcMidPoint, Vec3 normal)
    {
        var radial = arcMidPoint - arcCenter;
        if (radial.Length < 1e-9) return null;

        var n = normal.Normalize();
        // 把 radial 投到平面内（去掉法向分量），保证 widthAxis 真的躺在截面平面里
        var inPlane = radial - n * radial.Dot(n);
        if (inPlane.Length < 1e-6) return null;

        var widthAxis = inPlane.Normalize();
        var lengthAxis = n.Cross(widthAxis);
        if (lengthAxis.Length < 1e-9) return null;
        lengthAxis = lengthAxis.Normalize();
        return (lengthAxis, widthAxis);
    }

    /// <summary>
    /// 在一组「候选圆弧」里挑出主圆弧（半径最大且半径在合理量级内）。
    /// 这一步对"双圆弧门板"等场景重要：取最具代表性的弧定方向。
    /// </summary>
    public static int PickDominantArc(IReadOnlyList<double> radii, IReadOnlyList<double> sweepRadians)
    {
        if (radii == null || radii.Count == 0) return -1;
        int bestIdx = -1;
        double bestScore = -1;
        for (int i = 0; i < radii.Count; i++)
        {
            double r = radii[i];
            double s = sweepRadians == null || sweepRadians.Count <= i ? Math.PI : Math.Abs(sweepRadians[i]);
            // score = r × sweep（弧长加权）
            double score = r * Math.Max(s, 0.1);
            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    // ────────── 轨对识别 + 中线长度（圆弧/样条拉伸板件专用） ──────────
    //
    // 圆弧/样条拉伸板件的核心几何特征：在厚度方向中部切一刀后，得到的 2D 截面
    // 边线由「外导轨 + 内导轨 + 0~2 段端帽」组成，两条导轨基本平行（恒定法向偏移
    // = 板件宽度），且总长度占周长的绝大部分。
    //
    // 一旦识别出轨对，就能精确得到：
    //   · 板件 length = 两条导轨长度的均值（= 几何中线长度）
    //   · 板件 width  = 两条导轨之间的法向距离
    //   · 板件类型   = ArcPanel（与简单矩形/异形板区分开）

    public readonly struct RailPairResult
    {
        /// <summary>是否成功识别为轨对（两条几乎等长的长曲线 + 短端帽）。</summary>
        public bool IsRailPair { get; }
        /// <summary>第一条导轨在排序后的索引（0 起）。</summary>
        public int Rail1Index { get; }
        /// <summary>第二条导轨在排序后的索引（0 起）。</summary>
        public int Rail2Index { get; }
        /// <summary>两条导轨长度均值（= 中线长度估算 = 板件 length）。</summary>
        public double CenterlineLength { get; }
        /// <summary>诊断信息：拒绝原因 / 通过特征。</summary>
        public string Reason { get; }

        public RailPairResult(bool ok, int i1, int i2, double cl, string reason)
        {
            IsRailPair = ok;
            Rail1Index = i1;
            Rail2Index = i2;
            CenterlineLength = cl;
            Reason = reason ?? "";
        }
        public static RailPairResult Reject(string reason) => new(false, -1, -1, 0, reason);
    }

    /// <summary>
    /// 在一组截面边曲线长度里识别「轨对」。
    ///
    /// 判定规则（保守，避免把普通矩形板误判为弧板）：
    ///   1. 至少 3 条边（否则可能是退化截面）；
    ///   2. 最长两条 (rail1, rail2) 长度比 ≥ <paramref name="similarityThreshold"/>
    ///      —— 即两条导轨长度差异不超过 1 / similarityThreshold（默认要求 ≥ 0.5
    ///      = 较短的不少于较长的一半，能容纳「斜端帽 + 弯导轨」的不对称情形）；
    ///   3. (rail1 + rail2) / totalLength ≥ <paramref name="dominantThreshold"/>
    ///      —— 两条导轨总长度占周长 ≥ 60%，意味着剩余必为短端帽；
    ///   4. rail2 长度 &gt; <paramref name="minRailLength"/>（默认 5mm）—— 排除
    ///      退化短边引发的误识别。
    ///
    /// 通过时返回中线长度 = (rail1Len + rail2Len) / 2，外层据此回写 PanelInfo.Length。
    /// </summary>
    public static RailPairResult PickRailPair(
        IReadOnlyList<double> curveLengths,
        double dominantThreshold = 0.6,
        double similarityThreshold = 0.5,
        double minRailLength = 5.0)
    {
        if (curveLengths == null || curveLengths.Count < 3)
            return RailPairResult.Reject("曲线数 < 3");

        // 找最长两条
        int i1 = 0, i2 = -1;
        double l1 = curveLengths[0], l2 = -1;
        for (int i = 1; i < curveLengths.Count; i++)
        {
            double l = curveLengths[i];
            if (l > l1) { l2 = l1; i2 = i1; l1 = l; i1 = i; }
            else if (l > l2) { l2 = l; i2 = i; }
        }
        if (i2 < 0 || l2 <= 0)
            return RailPairResult.Reject("无第 2 条候选");
        if (l2 < minRailLength)
            return RailPairResult.Reject($"次长边 {l2:F2} < {minRailLength}");
        double similarity = l2 / l1;
        if (similarity < similarityThreshold)
            return RailPairResult.Reject($"长度差异过大: {similarity:F2} < {similarityThreshold}");

        double total = 0;
        for (int i = 0; i < curveLengths.Count; i++) total += curveLengths[i];
        if (total < 1e-6)
            return RailPairResult.Reject("总长度退化");
        double railRatio = (l1 + l2) / total;
        if (railRatio < dominantThreshold)
            return RailPairResult.Reject($"轨对周长占比 {railRatio:F2} < {dominantThreshold}");

        double centerline = (l1 + l2) * 0.5;
        return new RailPairResult(true, i1, i2, centerline,
            $"rail1={l1:F2}, rail2={l2:F2}, ratio={similarity:F2}, perimeter={railRatio:F2}");
    }

    /// <summary>
    /// 给定两条同心圆弧的半径与扫角（弧度），返回中线弧长 = ((r1+r2)/2) × meanSweep。
    /// 这是同心圆弧轨对（如标准 1/4 / 1/2 圆门板）的精确中线长度公式 ——
    /// 比 PickRailPair 的均值估算更精确（因为均值估算把"扫角差异"也算进去）。
    /// </summary>
    public static double ArcCenterlineLength(double r1, double sweep1, double r2, double sweep2)
    {
        if (r1 < 0 || r2 < 0) return 0;
        double rMean = (r1 + r2) * 0.5;
        double swMean = (Math.Abs(sweep1) + Math.Abs(sweep2)) * 0.5;
        return rMean * swMean;
    }

    /// <summary>
    /// 由轨对识别得到 (centerlineLength, railDistance) + 截面所在轴的 AABB 跨度，
    /// 推出板件最终的 (Length, Width, Thickness)。
    ///
    /// 规则（关键修复）：
    ///   · thickness = min(railDistance, aabbAlongSectionAxis)
    ///   · 中长 = max(railDistance, aabbAlongSectionAxis)
    ///   · length = max(centerlineLength, 中长)
    ///   · width  = min(centerlineLength, 中长)
    ///
    /// 物理意义：
    ///   - 曲线拉伸板件：截面 perp 拉伸轴 → railDist=18mm（真厚度）≪
    ///     aabbAlongAxis=1300mm（板件高度=展开宽度），中线弧长=1569mm（长度）。
    ///     → thickness=18, width=1300, length=1569 ✓
    ///   - 普通矩形板（1000×500×18）截面 perp 18 轴 → railDist=500（短边），
    ///     aabbAlongAxis=18（厚度方向 AABB），centerlineLength=1000（长边）。
    ///     → thickness=18, width=500, length=1000 ✓
    ///   - 旧版 bug：把 aabbAlongSectionAxis 硬当 thickness，对曲线板就把
    ///     "波幅 / 半径" 错算成厚度，railDist=18 反倒变成 width。
    /// </summary>
    public static (double Length, double Width, double Thickness) AssignDimensions(
        double centerlineLength, double railDistance, double aabbAlongSectionAxis)
    {
        double thickness = Math.Min(Math.Abs(railDistance), Math.Abs(aabbAlongSectionAxis));
        double midLong = Math.Max(Math.Abs(railDistance), Math.Abs(aabbAlongSectionAxis));
        double cl = Math.Abs(centerlineLength);
        double length = Math.Max(cl, midLong);
        double width = Math.Min(cl, midLong);
        return (length, width, thickness);
    }

    /// <summary>
    /// 样条/圆弧截面里，轨距采样可能被曲线最近点放大或缩小；如果能从两端端帽线拿到厚度，
    /// 端帽通常更接近真实板厚。两者接近时取较小值；差异过大时信任端帽，避开最近点跨弧误差。
    /// </summary>
    public static double ResolveRailDistance(double sampledRailDistance, double capThickness)
    {
        double sampled = Math.Abs(sampledRailDistance);
        double cap = Math.Abs(capThickness);

        if (sampled < 1e-6) return cap;
        if (cap < 1e-6) return sampled;

        double lo = Math.Min(sampled, cap);
        double hi = Math.Max(sampled, cap);
        return hi / lo <= 1.6 ? lo : cap;
    }
}
