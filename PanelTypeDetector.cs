using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin;

/// <summary>
/// 自动判别板件类型（SETI 命令的"板件形状"自动识别）。
///
/// ★ 性能与稳定性优先：
///   - 不再 Clone Solid3d（克隆对样条/圆弧实体可能耗时）；
///   - 不再调用 BZBJ AlignToWorldXY（迭代 PCA 在样条/圆弧实体上会卡数秒）；
///   - 不再递归 Explode（样条板炸开会触发 ASM 大量内部计算 → 卡死）；
///   - 仅使用 GeometricExtents + GetGripPoints 做形状判断，O(grip 数)。
///
/// 判别策略（保守）：
///   1. 取 BBox 尺寸 [thickness, width, length]（升序）；
///      若退化 → OBB 默认。
///   2. 取 grip 点；正立方/规则板件总是 8 个角点。
///      若 grip == 8 且 8 个点的 X/Y/Z 各只有 2 个唯一值 → AABB（轴对齐）；
///      否则 → OBB（旋转矩形）。
///   3. grip != 8（圆弧/样条/缺口/孔洞板件） → Irregular（异形板）。
///
/// 任何异常均退化到 OBB —— 绝不抛出，不卡 SETI。
/// </summary>
public static class PanelTypeDetector
{
    public sealed class DetectionResult
    {
        public PanelCalculationType DetectedType { get; set; } = PanelCalculationType.OBB;
        public string Reason { get; set; } = "";
        public int GripCount { get; set; }
        public bool AxisAligned { get; set; }
    }

    /// <summary>
    /// 自动判别板件几何类型（不修改实体，只读）。
    /// </summary>
    public static DetectionResult Detect(Solid3d solid)
    {
        var result = new DetectionResult();
        if (solid == null || solid.IsErased)
        {
            result.Reason = "实体无效";
            return result;
        }

        try
        {
            // 1) BBox 退化检查
            Extents3d ext;
            try { ext = solid.GeometricExtents; }
            catch
            {
                result.Reason = "无法取得几何范围";
                return result;
            }

            double dx = ext.MaxPoint.X - ext.MinPoint.X;
            double dy = ext.MaxPoint.Y - ext.MinPoint.Y;
            double dz = ext.MaxPoint.Z - ext.MinPoint.Z;
            if (dx < 1e-3 || dy < 1e-3 || dz < 1e-3)
            {
                result.Reason = "BBox 退化";
                return result;
            }

            // 2) 取 grip 点
            var grips = new Point3dCollection();
            try
            {
                var sm = new IntegerCollection();
                var gi = new IntegerCollection();
                solid.GetGripPoints(grips, sm, gi);
            }
            catch
            {
                // 取不到 grip → 保守为 OBB
                result.Reason = "无法取得 grip 点";
                return result;
            }

            int gripCount = grips.Count;
            result.GripCount = gripCount;

            // 3) 8 个 grip：典型的矩形板（盒子）
            if (gripCount == 8)
            {
                var pts = new List<Point3d>(8);
                foreach (Point3d p in grips) pts.Add(p);

                // 把每个轴的坐标量化到 0.1mm 后看唯一值数量；
                // 真正的 AABB 立方体每轴应当只有 2 个不同值
                int ux = pts.Select(p => Math.Round(p.X, 1)).Distinct().Count();
                int uy = pts.Select(p => Math.Round(p.Y, 1)).Distinct().Count();
                int uz = pts.Select(p => Math.Round(p.Z, 1)).Distinct().Count();
                bool axisAligned = ux == 2 && uy == 2 && uz == 2;
                result.AxisAligned = axisAligned;
                result.DetectedType = axisAligned
                    ? PanelCalculationType.AABB
                    : PanelCalculationType.OBB;
                result.Reason = axisAligned
                    ? "8 grip 且轴对齐 → AABB"
                    : "8 grip 但旋转 → OBB";
                return result;
            }

            // 4) grip 数量异常 → 异形板（含弧/样条/缺口/孔洞）
            //    交给 OutlineService FLATSHOT 流程做精确投影，不在这里硬算
            result.DetectedType = PanelCalculationType.Irregular;
            result.Reason = $"{gripCount} grip → 异形板";
            return result;
        }
        catch (System.Exception ex)
        {
            // 任何异常都不能传出去 —— SETI 卡死的根源就是这里抛/卡
            result.DetectedType = PanelCalculationType.OBB;
            result.Reason = "判别异常: " + ex.Message;
            return result;
        }
    }
}
