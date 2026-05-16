using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin;

/// <summary>
/// AXS 命令的执行层（AutoCAD 桥接）—— 经典 AutoCAD STRETCH 在 3D 长方体上的语义。
///
/// ★ 工作流（命令交互见 <c>Commands/AxsCommand.cs</c>）：
///   1) 选定要拉伸的轴 X/Y/Z；
///   2) 用「交叉窗口」拾两个对角点 → 得到该轴上的窗口范围 [winMin, winMax]
///      并自动收集所有 BBox 与窗口相交的实体；
///   3) 拾基点 + 第二点（或输入距离）→ 得到沿选定轴的有向位移 delta。
///
/// ★ 决策矩阵（见 <see cref="AxsStretchMath.ComputeClassicStretch"/>）：
///   把每件 3D 实体当作一个轴对齐的 BBox，看它在选定轴上的两个面顶点是否在窗口内：
///     · entMin∈[winMin,winMax] 且 entMax∈[winMin,winMax] → Move（整体平移 +Δ；
///       典型：中间隔板/抽屉面板 —— 两个面都被框中）；
///     · 仅 entMax∈[winMin,winMax] → Stretch：entMax 移动 +Δ，entMin 不变
///       （典型：立板顶面被框中，底面在框外 → 立板拉长/缩短）；
///     · 仅 entMin∈[winMin,winMax] → 镜像情况；
///     · 都不在 → None（典型：立板两端都在框外、框只跨过中段 → 立板不动）。
///
/// ★ 这是 AutoCAD STRETCH 在长方体上的精确移植 —— 一举解决两类历史 BUG：
///     · 旧 baseCoord 单边参考面：用户框中段时立板"两端都在窗口外" → 算法判断
///       为活动/非活动侧 → 立板"莫名其妙"动起来；
///     · 旧"中线启发式"：薄/长按位置切分，框选无关 → 用户无法精确控制。
///
/// ★ 与早期非等比例缩放设计的关键区别（修复 e0434352h CLR/SEH 闪退）：
///   - 不再对 <see cref="Solid3d"/> 调用非等比例 <c>TransformBy</c>。
///   - Stretch 路径用 <see cref="Solid3d.CreateBox"/> 重建，Move 路径用安全的
///     <see cref="Matrix3d.Displacement(Vector3d)"/>（纯平移，ASM 永远接受）。
///
/// ★ 全程要求外层已开启事务；本类内部不开/提交事务。
/// </summary>
public static class AxsStretchEngine
{
    public enum StretchAxis { X, Y, Z }

    public sealed class StretchRequest
    {
        public StretchAxis Axis { get; init; } = StretchAxis.X;
        /// <summary>交叉窗口在选定轴上的下界（单位 mm，WCS）。</summary>
        public double WinMin { get; init; }
        /// <summary>交叉窗口在选定轴上的上界（单位 mm，WCS）。WinMax ≥ WinMin。</summary>
        public double WinMax { get; init; }
        /// <summary>有向位移：第二点在该轴上的坐标 − 基点在该轴上的坐标，单位 mm。</summary>
        public double Delta { get; init; }
        public bool UpdatePanelInfo { get; init; } = true;
        /// <summary>若 Stretch 后该实体在任意维度小于此阈值（mm），则放弃；避免拓扑塌陷。</summary>
        public double MinLengthAfter { get; init; } = 0.5;
        /// <summary>面顶点是否落在窗口内的判定容差（mm，默认 0.001）。</summary>
        public double FaceInTol { get; init; } = 1e-3;
    }

    public sealed class StretchResult
    {
        public bool Success { get; set; }
        public string Reason { get; set; } = "";
        public int Moved { get; set; }
        public int Stretched { get; set; }
        public int Untouched { get; set; }
        public int SkippedNonSolid { get; set; }
        public int Failed { get; set; }
        public int RejectedTooSmall { get; set; }
    }

    /// <summary>
    /// 经典 STRETCH：按基点参考面 + 有向位移作用到选中实体。
    /// 要求外层已经在事务内、entities 已 ForWrite 打开。
    /// </summary>
    public static StretchResult Apply(Transaction tr, IEnumerable<Entity> entities, StretchRequest req)
    {
        var result = new StretchResult();
        if (tr == null) { result.Reason = "缺少事务"; return result; }
        if (req == null) { result.Reason = "缺少请求"; return result; }
        if (Math.Abs(req.Delta) < 1e-6)
        {
            result.Success = true;
            result.Reason = "拉伸距离为 0";
            return result;
        }

        Vector3d axisVec = AxisVector(req.Axis);

        foreach (var ent in entities)
        {
            if (ent == null || ent.IsErased) continue;

            double entMin, entMax;
            try
            {
                var ext = ent.GeometricExtents;
                entMin = AxisCoord(ext.MinPoint, req.Axis);
                entMax = AxisCoord(ext.MaxPoint, req.Axis);
            }
            catch
            {
                result.Failed++;
                continue;
            }

            var iv = AxsStretchMath.ComputeClassicStretch(
                entMin, entMax, req.WinMin, req.WinMax, req.Delta, req.FaceInTol);

            switch (iv.Action)
            {
                case AxsStretchMath.StretchAction.None:
                    result.Untouched++;
                    break;

                case AxsStretchMath.StretchAction.Move:
                    if (TryMove(ent, axisVec, req.Delta))
                        result.Moved++;
                    else
                        result.Failed++;
                    break;

                case AxsStretchMath.StretchAction.Stretch:
                    if (ent is Solid3d solid)
                    {
                        if (TryStretchSolid(tr, solid, req.Axis, iv.NewMin, iv.NewMax,
                                req.UpdatePanelInfo, req.MinLengthAfter))
                            result.Stretched++;
                        else
                            result.RejectedTooSmall++;
                    }
                    else
                    {
                        // 非 Solid3d 一律跳过（避免折线/块的非等比例缩放风险）
                        result.SkippedNonSolid++;
                    }
                    break;
            }
        }

        result.Success = result.Moved + result.Stretched > 0 || result.Untouched > 0;
        result.Reason = $"成功 拉伸 {result.Stretched} 件 / 平移 {result.Moved} 件 / 不动 {result.Untouched} 件" +
                        (result.RejectedTooSmall > 0 ? $", 拒绝 {result.RejectedTooSmall} 件（过小）" : "") +
                        (result.SkippedNonSolid > 0 ? $", 跳过 {result.SkippedNonSolid} 个非 3D 实体" : "") +
                        (result.Failed > 0 ? $", 失败 {result.Failed} 件" : "");
        return result;
    }

    // ─────────── 内部 ───────────

    private static double AxisCoord(Point3d p, StretchAxis axis) => axis switch
    {
        StretchAxis.X => p.X,
        StretchAxis.Y => p.Y,
        _ => p.Z,
    };

    private static Vector3d AxisVector(StretchAxis axis) => axis switch
    {
        StretchAxis.X => Vector3d.XAxis,
        StretchAxis.Y => Vector3d.YAxis,
        _ => Vector3d.ZAxis,
    };

    private static bool TryMove(Entity ent, Vector3d axisVec, double displacement)
    {
        if (Math.Abs(displacement) < 1e-9) return true;
        try
        {
            ent.TransformBy(Matrix3d.Displacement(axisVec * displacement));
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return false; }
        catch (System.Exception) { return false; }
    }

    /// <summary>
    /// "Stretch" 分支专用：按新 BBox 边界重建 Solid3d 盒体，删除旧实体。
    /// 不调用任何非等比例 TransformBy（避免 ASM 拒绝引发原生 AV）。
    /// </summary>
    private static bool TryStretchSolid(
        Transaction tr, Solid3d solid, StretchAxis axis,
        double newMin, double newMax, bool updatePanelInfo, double minLengthAfter)
    {
        try
        {
            Extents3d ext = solid.GeometricExtents;
            double mnX = ext.MinPoint.X, mnY = ext.MinPoint.Y, mnZ = ext.MinPoint.Z;
            double mxX = ext.MaxPoint.X, mxY = ext.MaxPoint.Y, mxZ = ext.MaxPoint.Z;

            switch (axis)
            {
                case StretchAxis.X: mnX = newMin; mxX = newMax; break;
                case StretchAxis.Y: mnY = newMin; mxY = newMax; break;
                default: mnZ = newMin; mxZ = newMax; break;
            }

            double sx = mxX - mnX, sy = mxY - mnY, sz = mxZ - mnZ;
            if (sx <= minLengthAfter || sy <= minLengthAfter || sz <= minLengthAfter)
                return false;

            var ns = new Solid3d();
            try { ns.CreateBox(sx, sy, sz); }
            catch { try { ns.Dispose(); } catch { } return false; }

            ns.TransformBy(Matrix3d.Displacement(
                new Point3d((mnX + mxX) * 0.5, (mnY + mxY) * 0.5, (mnZ + mxZ) * 0.5) - Point3d.Origin));

            try { ns.SetPropertiesFrom(solid); } catch { }

            var btr = (BlockTableRecord)tr.GetObject(solid.BlockId, OpenMode.ForWrite);
            btr.AppendEntity(ns);
            tr.AddNewlyCreatedDBObject(ns, true);

            if (updatePanelInfo)
            {
                try
                {
                    var oldInfo = PanelInfoService.GetPanelInfo(solid, tr);
                    if (oldInfo != null)
                    {
                        var dims = new[] { sx, sy, sz };
                        Array.Sort(dims);
                        oldInfo.Height = Math.Round(dims[0], 2);
                        oldInfo.Width = Math.Round(dims[1], 2);
                        oldInfo.Length = Math.Round(dims[2], 2);
                        oldInfo.EntityId = ns.Handle.Value.ToString();
                        PanelInfoService.SavePanelInfo(ns, oldInfo, tr);
                    }
                }
                catch { /* ignore */ }
            }

            solid.Erase();
            return true;
        }
        catch { return false; }
    }
}
