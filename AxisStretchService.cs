using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace FurniturePlugin
{
    /// <summary>
    /// AXS命令：沿指定轴向拉伸板件/柜体
    /// 算法：
    ///   1. 计算所有选中实体沿拉伸轴的总范围 (totalExtent)
    ///   2. 全局缩放因子 sf = (totalExtent + distance) / totalExtent
    ///   3. 对每个实体：
    ///      - 厚度方向 = 拉伸轴 → 比例位移（保持尺寸不变）
    ///      - 非厚度方向 → 非均匀缩放（拉伸变长）
    ///   基准点为缩放锚点，基准点侧固定，远端移动
    /// </summary>
    public static class AxisStretchService
    {
        public static void Run()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            try
            {
                // 1. 选择实体
                var pso = new PromptSelectionOptions { MessageForAdding = "\n请选择要拉伸的板件或柜体: " };
                var selRes = ed.GetSelection(pso);
                if (selRes.Status != PromptStatus.OK) { ed.WriteMessage("\n操作已取消。"); return; }
                var ids = selRes.Value.GetObjectIds();
                if (ids.Length == 0) { ed.WriteMessage("\n没有选择任何实体。"); return; }

                // 2. 选择拉伸轴
                var pko = new PromptKeywordOptions("\n选择拉伸轴方向 [X/Y/Z]: ");
                pko.Keywords.Add("X"); pko.Keywords.Add("Y"); pko.Keywords.Add("Z");
                pko.Keywords.Default = "X"; pko.AllowNone = false;
                var kRes = ed.GetKeywords(pko);
                if (kRes.Status != PromptStatus.OK) { ed.WriteMessage("\n操作已取消。"); return; }
                string axisKey = kRes.StringResult;
                int axisIdx = axisKey == "X" ? 0 : axisKey == "Y" ? 1 : 2;
                Vector3d stretchAxis = axisIdx == 0 ? Vector3d.XAxis : axisIdx == 1 ? Vector3d.YAxis : Vector3d.ZAxis;

                // 3. 选择基准点（固定侧的参考点）
                var ppo = new PromptPointOptions("\n指定拉伸基准点 (此侧固定不动): ");
                ppo.AllowNone = false;
                var ptRes = ed.GetPoint(ppo);
                if (ptRes.Status != PromptStatus.OK) { ed.WriteMessage("\n操作已取消。"); return; }
                Point3d basePoint = ptRes.Value;
                double baseVal = AxisVal(basePoint, axisIdx);

                // 4. 输入拉伸距离（正=正方向拉伸，负=负方向拉伸）
                var pdo = new PromptDoubleOptions("\n输入拉伸距离 (正数=正方向, 负数=负方向): ");
                pdo.AllowNegative = true; pdo.AllowZero = false;
                pdo.DefaultValue = 100; pdo.UseDefaultValue = true;
                var dRes = ed.GetDouble(pdo);
                if (dRes.Status != PromptStatus.OK) { ed.WriteMessage("\n操作已取消。"); return; }
                double distance = dRes.Value;

                // 5. 分析全局范围
                using var tr = db.TransactionManager.StartTransaction();
                double globalMin = double.MaxValue, globalMax = double.MinValue;

                var entities = new List<(ObjectId id, Extents3d ext)>();
                foreach (var id in ids)
                {
                    try
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        var ext = ent.GeometricExtents;
                        entities.Add((id, ext));
                        double eMin = AxisVal(ext.MinPoint, axisIdx);
                        double eMax = AxisVal(ext.MaxPoint, axisIdx);
                        if (eMin < globalMin) globalMin = eMin;
                        if (eMax > globalMax) globalMax = eMax;
                    }
                    catch { }
                }

                if (entities.Count == 0) { ed.WriteMessage("\n没有有效实体。"); tr.Commit(); return; }

                // 以基准点为锚点的有效拉伸范围
                double stretchSideMax = distance > 0 ? globalMax : globalMin;
                double totalExtent = Math.Abs(stretchSideMax - baseVal);
                if (totalExtent < 0.1) { ed.WriteMessage("\n实体不跨越基准点，无法拉伸。"); tr.Commit(); return; }
                double sf = (totalExtent + Math.Abs(distance)) / totalExtent;

                // 6. 执行
                int stretched = 0, moved = 0, skipped = 0;
                foreach (var (id, ext) in entities)
                {
                    try
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent == null) { skipped++; continue; }

                        double eMin = AxisVal(ext.MinPoint, axisIdx);
                        double eMax = AxisVal(ext.MaxPoint, axisIdx);
                        double eCenter = (eMin + eMax) / 2.0;
                        double eAxisSize = eMax - eMin;

                        double[] dims = {
                            ext.MaxPoint.X - ext.MinPoint.X,
                            ext.MaxPoint.Y - ext.MinPoint.Y,
                            ext.MaxPoint.Z - ext.MinPoint.Z
                        };
                        double minDim = dims.Min();

                        // 判断：拉伸轴方向是否是厚度方向
                        bool isThicknessAxis = Math.Abs(dims[axisIdx] - minDim) < 1.0
                                            && minDim / dims.Max() < 0.35;

                        // 判断：实体是否在拉伸方向的"活动侧"
                        bool onStretchSide = distance > 0
                            ? eCenter > baseVal + 1
                            : eCenter < baseVal - 1;

                        if (isThicknessAxis)
                        {
                            // 厚度方向板件：只移动，不拉伸（保持厚度不变）
                            if (onStretchSide)
                            {
                                double moveD = (eCenter - baseVal) / totalExtent * Math.Abs(distance);
                                if (distance < 0) moveD = -moveD;
                                ent.TransformBy(Matrix3d.Displacement(stretchAxis * moveD));
                                moved++;
                            }
                            else skipped++;
                        }
                        else
                        {
                            // 非厚度方向：非均匀缩放拉伸
                            if (onStretchSide || SpansBase(eMin, eMax, baseVal))
                            {
                                bool ok = TryNonUniformScale(ent, axisIdx, sf, baseVal);
                                if (ok) stretched++;
                                else
                                {
                                    // 回退：按比例位移
                                    double moveD = (eCenter - baseVal) / totalExtent * Math.Abs(distance);
                                    if (distance < 0) moveD = -moveD;
                                    ent.TransformBy(Matrix3d.Displacement(stretchAxis * moveD));
                                    moved++;
                                }
                            }
                            else skipped++;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage($"\n处理实体出错: {ex.Message}");
                        skipped++;
                    }
                }

                tr.Commit();
                ed.WriteMessage($"\n拉伸完成 - 轴:{axisKey} 距离:{distance:F1}mm 缩放:{sf:F4}");
                ed.WriteMessage($"\n  拉伸:{stretched}个  移动:{moved}个  跳过:{skipped}个");
            }
            catch (System.Exception ex) { ed.WriteMessage($"\nAXS命令失败: {ex.Message}"); }
        }

        private static bool SpansBase(double eMin, double eMax, double baseVal) =>
            eMin <= baseVal + 5 && eMax >= baseVal - 5;

        /// <summary>
        /// 沿单一轴向非均匀缩放，从baseVal锚定
        /// Matrix: x' = sf * x + baseVal * (1 - sf) ← 仅拉伸轴方向
        /// </summary>
        private static bool TryNonUniformScale(Entity ent, int axisIdx, double sf, double baseVal)
        {
            try
            {
                double ox = axisIdx == 0 ? baseVal * (1 - sf) : 0;
                double oy = axisIdx == 1 ? baseVal * (1 - sf) : 0;
                double oz = axisIdx == 2 ? baseVal * (1 - sf) : 0;

                var mat = new Matrix3d(new double[] {
                    axisIdx == 0 ? sf : 1, 0, 0, ox,
                    0, axisIdx == 1 ? sf : 1, 0, oy,
                    0, 0, axisIdx == 2 ? sf : 1, oz,
                    0, 0, 0, 1
                });

                var oldExt = ent.GeometricExtents;
                ent.TransformBy(mat);
                var newExt = ent.GeometricExtents;

                double oldSize = AxisVal(oldExt.MaxPoint, axisIdx) - AxisVal(oldExt.MinPoint, axisIdx);
                double newSize = AxisVal(newExt.MaxPoint, axisIdx) - AxisVal(newExt.MinPoint, axisIdx);
                return Math.Abs(newSize - oldSize * sf) < oldSize * 0.5;
            }
            catch { return false; }
        }

        private static double AxisVal(Point3d pt, int idx) =>
            idx == 0 ? pt.X : idx == 1 ? pt.Y : pt.Z;
    }
}
