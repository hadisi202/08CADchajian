using System;

namespace FurniturePlugin;

/// <summary>
/// AXS 命令的纯数学层（不依赖 AutoCAD 程序集，便于做高速回归）。
///
/// 提供两类核心算法：
///   1. <see cref="ComputeIntervalStretch"/>：经典 STRETCH 单轴区间位移逻辑
///      （= 旧 AXSORTHO 的核心，保留向后兼容，已有 4 个回归用例）。
///   2. 非等比例缩放 (Non-uniform Affine Scale)：
///      新 AXS 命令使用 <see cref="BuildNonUniformScaleMatrix"/> +
///      <see cref="ApplyMatrix4x4"/> 构造任意 3 个正交方向、3 个独立缩放系数、
///      任意基准点 (anchor) 的 4×4 仿射矩阵，并安全应用于任意 3D 点。
///
/// 数学约定：矩阵以行主序 (row-major) 存储为 4×4 = 16 个 double，
/// 即 [m00, m01, m02, m03,  m10, ..., m33]。这与 AutoCAD Matrix3d 的
/// 行主序约定一致，便于外层零损失桥接到 Matrix3d。
/// </summary>
public static class AxsStretchMath
{
    public enum Axis3D { X, Y, Z }
    public enum StretchAction { None, Move, Stretch }

    /// <summary>新版 AXS 全局组拉伸的"哪一侧不动"语义。</summary>
    public enum FixedSide { Min, Max, Center }

    public readonly struct IntervalResult
    {
        public double NewMin { get; }
        public double NewMax { get; }
        public StretchAction Action { get; }
        public IntervalResult(double newMin, double newMax, StretchAction action)
        {
            NewMin = newMin;
            NewMax = newMax;
            Action = action;
        }
    }

    /// <summary>
    /// 单实体在「整组拉伸」中应执行的动作。
    /// </summary>
    public readonly struct GroupStretchClassification
    {
        public StretchAction Action { get; }
        /// <summary>当 Action=Move 时：沿轴的位移（单位 mm）；否则未使用。</summary>
        public double Displacement { get; }
        /// <summary>当 Action=Stretch 时：实体沿轴的新 Min；否则未使用。</summary>
        public double NewMin { get; }
        public double NewMax { get; }

        public GroupStretchClassification(StretchAction action, double disp, double newMin, double newMax)
        {
            Action = action;
            Displacement = disp;
            NewMin = newMin;
            NewMax = newMax;
        }

        public static GroupStretchClassification None() =>
            new(StretchAction.None, 0, 0, 0);
        public static GroupStretchClassification Move(double disp) =>
            new(StretchAction.Move, disp, 0, 0);
        public static GroupStretchClassification Stretch(double newMin, double newMax) =>
            new(StretchAction.Stretch, 0, newMin, newMax);
    }

    /// <summary>
    /// 「整组拉伸」核心分类算法（纯数学，无 AutoCAD 依赖）。
    ///
    /// 输入：
    ///   - <paramref name="entMin"/>/<paramref name="entMax"/>: 单实体在选定轴上的当前 BBox；
    ///   - <paramref name="globalMin"/>/<paramref name="globalMax"/>: 整组实体在该轴上的全局 BBox；
    ///   - <paramref name="delta"/>: 用户输入的拉伸距离（正=变长，负=变短）；
    ///   - <paramref name="fixedSide"/>: 哪一侧固定（Min/Max/Center）；
    ///   - <paramref name="thinRatio"/>: 厚/薄判别比例（默认 0.5：实体在轴上的伸展 ≤ 全局范围的 50% 视为"薄"）。
    ///
    /// 决策矩阵（以 Fix=Min 为例）：
    ///   实体类型               实体中心位置             动作
    ///   ───────────────────────────────────────────────────
    ///   薄（&lt;= 50% 范围）     在全局中线下方            None（不动；典型：底板）
    ///   薄                     在全局中线上方            Move +delta（典型：顶板/上层搁板）
    ///   长（&gt; 50% 范围）       —                       Stretch：Max 侧外推 +delta（典型：立板）
    ///
    /// Fix=Max 与 Fix=Min 对称；Fix=Center 时 delta 各分一半给两端。
    ///
    /// 这就是经典 AutoCAD STRETCH 在「无法手选 crossing window」语境下的最佳近似：
    /// 顶板/底板这类「薄面」按所处一侧整体平移；立板这类「长面」按所处端拉伸。
    /// </summary>
    public static GroupStretchClassification ClassifyForGroupStretch(
        double entMin, double entMax,
        double globalMin, double globalMax,
        double delta, FixedSide fixedSide,
        double thinRatio = 0.5)
    {
        if (entMax < entMin) (entMin, entMax) = (entMax, entMin);
        if (globalMax < globalMin) (globalMin, globalMax) = (globalMax, globalMin);

        double globalRange = globalMax - globalMin;
        if (globalRange < 1e-9)
            return GroupStretchClassification.None();
        if (Math.Abs(delta) < 1e-9)
            return GroupStretchClassification.None();

        double entExtent = entMax - entMin;
        double entCenter = (entMin + entMax) * 0.5;
        double globalMid = (globalMin + globalMax) * 0.5;
        bool isThin = entExtent <= globalRange * thinRatio;

        switch (fixedSide)
        {
            case FixedSide.Min:
                if (isThin)
                {
                    return entCenter > globalMid
                        ? GroupStretchClassification.Move(delta)
                        : GroupStretchClassification.None();
                }
                return GroupStretchClassification.Stretch(entMin, entMax + delta);

            case FixedSide.Max:
                if (isThin)
                {
                    return entCenter < globalMid
                        ? GroupStretchClassification.Move(-delta)
                        : GroupStretchClassification.None();
                }
                return GroupStretchClassification.Stretch(entMin - delta, entMax);

            default: // Center
                if (isThin)
                {
                    double half = delta * 0.5;
                    return entCenter < globalMid
                        ? GroupStretchClassification.Move(-half)
                        : GroupStretchClassification.Move(half);
                }
                return GroupStretchClassification.Stretch(entMin - delta * 0.5, entMax + delta * 0.5);
        }
    }

    /// <summary>
    /// 缩放系数验证结果。
    /// </summary>
    public readonly struct ScaleValidationResult
    {
        public bool IsValid { get; }
        public bool HasMirror { get; }
        public bool HasDegenerateAxis { get; }
        public string Reason { get; }
        public double SafeSx { get; }
        public double SafeSy { get; }
        public double SafeSz { get; }

        public ScaleValidationResult(bool valid, bool mirror, bool degenerate,
            string reason, double sx, double sy, double sz)
        {
            IsValid = valid; HasMirror = mirror; HasDegenerateAxis = degenerate;
            Reason = reason ?? "";
            SafeSx = sx; SafeSy = sy; SafeSz = sz;
        }
    }

    /// <summary>
    /// 校验三个轴向缩放系数：
    ///   - 任一 |s|&lt;<paramref name="minMagnitude"/> → 退化（拓扑会塌成面/线/点），
    ///     若 <paramref name="clampDegenerate"/>=true 则将其夹到 ±minMagnitude；
    ///   - 任一 s&lt;0 → 镜像；若 <paramref name="allowMirror"/>=false 则视为非法。
    /// </summary>
    public static ScaleValidationResult ValidateScales(
        double sx, double sy, double sz,
        bool allowMirror = false, bool clampDegenerate = true,
        double minMagnitude = 1e-3)
    {
        bool mirror = sx < 0 || sy < 0 || sz < 0;
        bool degenerate = Math.Abs(sx) < minMagnitude
                       || Math.Abs(sy) < minMagnitude
                       || Math.Abs(sz) < minMagnitude;

        if (mirror && !allowMirror)
            return new ScaleValidationResult(false, true, degenerate,
                "缩放系数为负将产生镜像，但当前未开启允许镜像", sx, sy, sz);

        if (!degenerate)
            return new ScaleValidationResult(true, mirror, false, "OK", sx, sy, sz);

        if (!clampDegenerate)
            return new ScaleValidationResult(false, mirror, true,
                "缩放系数过小，会导致面/边塌陷为退化几何", sx, sy, sz);

        // clamp：保留原符号，幅值不小于 minMagnitude
        double safeSx = Math.Abs(sx) < minMagnitude
            ? (sx < 0 ? -minMagnitude : minMagnitude) : sx;
        double safeSy = Math.Abs(sy) < minMagnitude
            ? (sy < 0 ? -minMagnitude : minMagnitude) : sy;
        double safeSz = Math.Abs(sz) < minMagnitude
            ? (sz < 0 ? -minMagnitude : minMagnitude) : sz;
        return new ScaleValidationResult(true, mirror, true,
            $"已将退化轴夹到 ±{minMagnitude}", safeSx, safeSy, safeSz);
    }

    /// <summary>
    /// 把单位向量 v 单位化；零向量返回 (1,0,0) 兜底。
    /// </summary>
    public static (double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        double l = Math.Sqrt(x * x + y * y + z * z);
        if (l < 1e-12) return (1, 0, 0);
        return (x / l, y / l, z / l);
    }

    /// <summary>
    /// 用 Gram-Schmidt 构造一组以 <paramref name="ux"/> 为第 1 主向量的右手正交基。
    /// 当三个轴非正交（如用户给了一个倾斜向量），会补出严格正交的第 2/3 轴。
    /// </summary>
    public static void BuildOrthonormalFrame(
        double[] ux, double[] uy, double[] uz)
    {
        if (ux == null || ux.Length != 3) throw new ArgumentException(nameof(ux));
        if (uy == null || uy.Length != 3) throw new ArgumentException(nameof(uy));
        if (uz == null || uz.Length != 3) throw new ArgumentException(nameof(uz));

        // u1 = normalize(ux)
        var u1 = Normalize(ux[0], ux[1], ux[2]);
        ux[0] = u1.X; ux[1] = u1.Y; ux[2] = u1.Z;

        // u2 = normalize(uy - (uy·u1) u1)
        double d12 = uy[0] * ux[0] + uy[1] * ux[1] + uy[2] * ux[2];
        double y0 = uy[0] - d12 * ux[0], y1 = uy[1] - d12 * ux[1], y2 = uy[2] - d12 * ux[2];
        // 若 uy 与 ux 共线，则取与 ux 不平行的世界轴做第 2 轴
        double yLen = Math.Sqrt(y0 * y0 + y1 * y1 + y2 * y2);
        if (yLen < 1e-9)
        {
            (double X, double Y, double Z) cand = Math.Abs(ux[0]) < 0.9 ? (1, 0, 0) : (0, 1, 0);
            d12 = cand.X * ux[0] + cand.Y * ux[1] + cand.Z * ux[2];
            y0 = cand.X - d12 * ux[0]; y1 = cand.Y - d12 * ux[1]; y2 = cand.Z - d12 * ux[2];
            yLen = Math.Sqrt(y0 * y0 + y1 * y1 + y2 * y2);
        }
        uy[0] = y0 / yLen; uy[1] = y1 / yLen; uy[2] = y2 / yLen;

        // u3 = u1 × u2 → 强制右手系
        uz[0] = ux[1] * uy[2] - ux[2] * uy[1];
        uz[1] = ux[2] * uy[0] - ux[0] * uy[2];
        uz[2] = ux[0] * uy[1] - ux[1] * uy[0];
        var nz = Normalize(uz[0], uz[1], uz[2]);
        uz[0] = nz.X; uz[1] = nz.Y; uz[2] = nz.Z;
    }

    /// <summary>
    /// 构造 4×4 仿射矩阵 M = T(anchor) · R · S(sx,sy,sz) · R⁻¹ · T(-anchor)。
    /// 含义：先把世界 → 局部基（R⁻¹），围绕局部 X/Y/Z 各自缩放，再回到世界。
    /// 等价物理意义：把 <paramref name="anchor"/> 作为不动点，沿 (ux,uy,uz)
    /// 三个正交方向分别按 (sx,sy,sz) 缩放整个空间。
    ///
    /// 返回行主序 16 个 double。
    /// </summary>
    public static double[] BuildNonUniformScaleMatrix(
        (double X, double Y, double Z) anchor,
        (double X, double Y, double Z) ux,
        (double X, double Y, double Z) uy,
        (double X, double Y, double Z) uz,
        double sx, double sy, double sz)
    {
        // R 把"世界 X" 行带到 ux：列 = ux, uy, uz，所以 R 的列向量是 ux/uy/uz
        // 等价：R^T 把 P_world - anchor 的世界坐标投到 (ux,uy,uz) 局部坐标
        // 局部缩放矩阵 S = diag(sx,sy,sz)
        // 整体 (作用在世界向量 v) ：R · S · R^T · v
        // R = [ux | uy | uz]  (列向量)
        // R^T = [ux; uy; uz] (行向量)

        // M3 = R · S · R^T 的 3×3 部分
        double[,] M3 = new double[3, 3];
        // R = columns ux, uy, uz
        // S · R^T 的第 i 行 = (s_i) * (R^T 的第 i 行) = (s_i) * (i 轴向量)
        // 然后 R · 上面 = sum_i column_i * (s_i * row_i)
        // m_{rc} = ux_r * sx * ux_c + uy_r * sy * uy_c + uz_r * sz * uz_c
        double[] U1 = { ux.X, ux.Y, ux.Z };
        double[] U2 = { uy.X, uy.Y, uy.Z };
        double[] U3 = { uz.X, uz.Y, uz.Z };
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                M3[r, c] = U1[r] * sx * U1[c] + U2[r] * sy * U2[c] + U3[r] * sz * U3[c];

        // 平移：t = anchor - M3 · anchor
        double tx = anchor.X - (M3[0, 0] * anchor.X + M3[0, 1] * anchor.Y + M3[0, 2] * anchor.Z);
        double ty = anchor.Y - (M3[1, 0] * anchor.X + M3[1, 1] * anchor.Y + M3[1, 2] * anchor.Z);
        double tz = anchor.Z - (M3[2, 0] * anchor.X + M3[2, 1] * anchor.Y + M3[2, 2] * anchor.Z);

        return new double[]
        {
            M3[0,0], M3[0,1], M3[0,2], tx,
            M3[1,0], M3[1,1], M3[1,2], ty,
            M3[2,0], M3[2,1], M3[2,2], tz,
            0,       0,       0,       1
        };
    }

    /// <summary>
    /// 用主轴对齐世界 X/Y/Z 的快捷构造（最常见用法）。
    /// </summary>
    public static double[] BuildAxisAlignedScaleMatrix(
        (double X, double Y, double Z) anchor, double sx, double sy, double sz)
    {
        return BuildNonUniformScaleMatrix(
            anchor,
            (1, 0, 0), (0, 1, 0), (0, 0, 1),
            sx, sy, sz);
    }

    /// <summary>
    /// 把行主序 4×4 矩阵作用到点 (px,py,pz)。
    /// </summary>
    public static (double X, double Y, double Z) ApplyMatrix4x4(double[] m, double px, double py, double pz)
    {
        if (m == null || m.Length != 16) throw new ArgumentException("矩阵必须是 16 元素的行主序数组", nameof(m));
        double x = m[0] * px + m[1] * py + m[2] * pz + m[3];
        double y = m[4] * px + m[5] * py + m[6] * pz + m[7];
        double z = m[8] * px + m[9] * py + m[10] * pz + m[11];
        // 仿射矩阵 w 行恒为 [0 0 0 1] —— 不做齐次除法
        return (x, y, z);
    }

    /// <summary>
    /// 行主序 4×4 矩阵的行列式（不含 w 行的 3×3 块）。
    /// |det|≈|sx·sy·sz|；symbol 显示是否翻转手性（<0 → 镜像）。
    /// </summary>
    public static double Determinant3x3OfAffine(double[] m)
    {
        if (m == null || m.Length != 16) throw new ArgumentException(nameof(m));
        double a = m[0], b = m[1], c = m[2];
        double d = m[4], e = m[5], f = m[6];
        double g = m[8], h = m[9], i = m[10];
        return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    }

    /// <summary>
    /// 用矩阵把一个 AABB（轴对齐包围盒）的 8 个角变换后取新 BBox。
    /// 注意：非等比例缩放后的 BBox 一般≠等比缩放结果，必须遍历 8 角。
    /// </summary>
    public static (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        TransformAABB(double[] m, double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        double mnX = double.PositiveInfinity, mnY = double.PositiveInfinity, mnZ = double.PositiveInfinity;
        double mxX = double.NegativeInfinity, mxY = double.NegativeInfinity, mxZ = double.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            double x = (i & 1) == 0 ? minX : maxX;
            double y = (i & 2) == 0 ? minY : maxY;
            double z = (i & 4) == 0 ? minZ : maxZ;
            var p = ApplyMatrix4x4(m, x, y, z);
            if (p.X < mnX) mnX = p.X; if (p.X > mxX) mxX = p.X;
            if (p.Y < mnY) mnY = p.Y; if (p.Y > mxY) mxY = p.Y;
            if (p.Z < mnZ) mnZ = p.Z; if (p.Z > mxZ) mxZ = p.Z;
        }
        return (mnX, mnY, mnZ, mxX, mxY, mxZ);
    }

    public static IntervalResult ComputeIntervalStretch(double min, double max, double baseCoord, double delta)
    {
        if (max < min) (min, max) = (max, min);
        if (Math.Abs(delta) < 1e-12) return new IntervalResult(min, max, StretchAction.None);

        if (delta > 0)
        {
            if (max <= baseCoord + 1e-9) return new IntervalResult(min, max, StretchAction.None);
            if (min >= baseCoord - 1e-9) return new IntervalResult(min + delta, max + delta, StretchAction.Move);
            return new IntervalResult(min, max + delta, StretchAction.Stretch);
        }
        else
        {
            if (min >= baseCoord - 1e-9) return new IntervalResult(min, max, StretchAction.None);
            if (max <= baseCoord + 1e-9) return new IntervalResult(min + delta, max + delta, StretchAction.Move);
            return new IntervalResult(min + delta, max, StretchAction.Stretch);
        }
    }

    /// <summary>
    /// 经典 AutoCAD STRETCH 在「轴对齐长方体」上的语义（核心算法）。
    /// 给定实体在选定轴上的当前范围 [<paramref name="entMin"/>, <paramref name="entMax"/>]、
    /// 交叉窗口在该轴上的范围 [<paramref name="winMin"/>, <paramref name="winMax"/>]、
    /// 用户位移 <paramref name="delta"/>，按"窗口内顶点跟随、窗口外顶点不动"判定行为：
    ///
    ///   entMin 在窗口内？ | entMax 在窗口内？ | 行为
    ///   ────────────────────────────────────────────────────────────
    ///   是                 | 是                 | <see cref="StretchAction.Move"/>（整体平移 +Δ；典型：中间隔板/抽屉面板）
    ///   是                 | 否                 | <see cref="StretchAction.Stretch"/>：newMin = entMin+Δ, newMax 不变
    ///   否                 | 是                 | <see cref="StretchAction.Stretch"/>：newMin 不变, newMax = entMax+Δ（典型：立板顶面在框内）
    ///   否                 | 否                 | <see cref="StretchAction.None"/>（顶点都不在窗口内 → 不动；典型：立板两端在框外、框只跨过中段）
    ///
    /// 退化处理：若 newMin &gt; newMax（拉伸量过大把实体压扁/反向），返回 <see cref="StretchAction.None"/> 并保持原值，
    /// 由调用方决定如何处理（建议向用户报"拉伸量超出实体厚度"）。
    /// </summary>
    public static IntervalResult ComputeClassicStretch(
        double entMin, double entMax,
        double winMin, double winMax,
        double delta,
        double tol = 1e-6)
    {
        if (entMax < entMin) (entMin, entMax) = (entMax, entMin);
        if (winMax < winMin) (winMin, winMax) = (winMax, winMin);
        if (Math.Abs(delta) < 1e-12)
            return new IntervalResult(entMin, entMax, StretchAction.None);

        bool inMin = entMin >= winMin - tol && entMin <= winMax + tol;
        bool inMax = entMax >= winMin - tol && entMax <= winMax + tol;

        if (inMin && inMax)
            return new IntervalResult(entMin + delta, entMax + delta, StretchAction.Move);

        if (inMin)
        {
            double newMin = entMin + delta;
            if (newMin > entMax - 1e-9)
                return new IntervalResult(entMin, entMax, StretchAction.None);
            return new IntervalResult(newMin, entMax, StretchAction.Stretch);
        }

        if (inMax)
        {
            double newMax = entMax + delta;
            if (newMax < entMin + 1e-9)
                return new IntervalResult(entMin, entMax, StretchAction.None);
            return new IntervalResult(entMin, newMax, StretchAction.Stretch);
        }

        return new IntervalResult(entMin, entMax, StretchAction.None);
    }

    public static bool IsMinimalRotation(double deg)
    {
        return Math.Abs(deg) <= 90.0 + 1e-9;
    }

    public static double ExtractAxisDelta(Axis3D axis, (double X, double Y, double Z) p1, (double X, double Y, double Z) p2)
    {
        return axis switch
        {
            Axis3D.X => p2.X - p1.X,
            Axis3D.Y => p2.Y - p1.Y,
            _ => p2.Z - p1.Z
        };
    }
}
