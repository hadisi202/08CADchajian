using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    public static class CadEntityHelper
    {
        /// <summary>
        /// 从选中的 Solid3d 或 Polyline 中识别尺寸
        /// </summary>
        public static (double width, double height, double depth, bool identified)? IdentifyDimensionsFromEntity(ObjectId entityId)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return null;

            using var tr = doc.Database.TransactionManager.StartTransaction();
            try
            {
                var entity = tr.GetObject(entityId, OpenMode.ForRead);
                double width = 0, height = 0, depth = 0;
                bool identified = false;

                if (entity is Solid3d solid)
                {
                    var extents = solid.GeometricExtents;
                    width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
                    height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
                    depth = Math.Abs(extents.MaxPoint.Z - extents.MinPoint.Z);
                    identified = true;
                }
                else if (entity is Polyline pline && pline.Closed)
                {
                    var extents = pline.GeometricExtents;
                    width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
                    height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
                    depth = 600;
                    identified = true;
                }

                tr.Commit();
                return identified ? (width, height, depth, identified) : (0, 0, 0, false);
            }
            catch (Exception ex)
            {
                PluginLogger.Error("识别实体尺寸失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 安全的 Hide + CAD选择 + Show 模式
        /// </summary>
        public static T SafeCadSelection<T>(System.Windows.Window window, Func<Editor, T> selectionFunc)
        {
            window?.Hide();
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return default;
                return selectionFunc(doc.Editor);
            }
            finally
            {
                try { window?.Show(); } catch { }
            }
        }

        /// <summary>
        /// 从选择集中获取组合边界框并计算中心
        /// </summary>
        public static Point3d? GetCombinedCenterPoint(SelectionSet selection, Database db)
        {
            using var tr = db.TransactionManager.StartTransaction();
            try
            {
                var combinedExtents = new Extents3d();
                bool first = true;

                foreach (var id in selection.GetObjectIds())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null) continue;
                    if (first)
                    {
                        combinedExtents = entity.GeometricExtents;
                        first = false;
                    }
                    else
                    {
                        combinedExtents.AddExtents(entity.GeometricExtents);
                    }
                }

                if (first) return null;

                var center = new Point3d(
                    (combinedExtents.MinPoint.X + combinedExtents.MaxPoint.X) / 2,
                    (combinedExtents.MinPoint.Y + combinedExtents.MaxPoint.Y) / 2,
                    combinedExtents.MinPoint.Z);

                tr.Commit();
                return center;
            }
            catch (Exception ex)
            {
                PluginLogger.Error("计算选择集中心失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 通过选择集获取组合边界框并计算底部左下角
        /// </summary>
        public static Point3d? GetCombinedBottomLeftPoint(SelectionSet selection, Database db)
        {
            using var tr = db.TransactionManager.StartTransaction();
            try
            {
                var combinedExtents = new Extents3d();
                bool first = true;

                foreach (var id in selection.GetObjectIds())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null) continue;
                    if (first)
                    {
                        combinedExtents = entity.GeometricExtents;
                        first = false;
                    }
                    else
                    {
                        combinedExtents.AddExtents(entity.GeometricExtents);
                    }
                }

                if (first) return null;

                var center = new Point3d(
                    (combinedExtents.MinPoint.X + combinedExtents.MaxPoint.X) / 2,
                    (combinedExtents.MinPoint.Y + combinedExtents.MaxPoint.Y) / 2,
                    combinedExtents.MinPoint.Z);
                var bottomLeft = new Point3d(combinedExtents.MinPoint.X, center.Y, center.Z);

                tr.Commit();
                return bottomLeft;
            }
            catch (Exception ex)
            {
                PluginLogger.Error("计算选择集底部点失败", ex);
                return null;
            }
        }
    }
}
