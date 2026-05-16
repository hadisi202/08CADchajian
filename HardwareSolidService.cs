using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;

namespace FurniturePlugin
{
    /// <summary>
    /// 五金件3D实体生成和布尔运算服务
    /// 负责根据五金件参数生成3D实体，并与板件进行布尔减运算（打孔）
    /// 
    /// AutoCAD .NET API说明：
    /// - Solid3d.CreateFrustum() 创建圆柱/圆台
    /// - Solid3d.CreateBox() 创建长方体
    /// - Solid3d.BooleanOperation(BooleanType, Solid3d) 布尔运算
    /// </summary>
    public static class HardwareSolidService
    {
        /// <summary>
        /// 在模型空间创建五金件3D实体（可视化）
        /// </summary>
        public static ObjectId CreateHardwareSolid(HardwareSolidParams @params, Point3d position, Vector3d direction, Transaction tr)
        {
            if (@params == null) return ObjectId.Null;

            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            Solid3d solid = null;

            switch (@params.ShapeType)
            {
                case "Cylinder":
                    solid = CreateCylinderSolid(@params);
                    break;
                case "Box":
                    solid = CreateBoxSolid(@params);
                    break;
                case "TShape":
                    solid = CreateTShapeSolid(@params);
                    break;
                default:
                    solid = CreateCylinderSolid(@params);
                    break;
            }

            if (solid == null) return ObjectId.Null;

            // 将五金件从原点移动到目标位置
            var displacement = position.GetAsVector();
            solid.TransformBy(Matrix3d.Displacement(displacement));

            // 旋转使Z轴对齐direction方向
            if (!direction.IsParallelTo(Vector3d.ZAxis))
            {
                var rotAxis = Vector3d.ZAxis.CrossProduct(direction.GetNormal());
                var rotAngle = Vector3d.ZAxis.GetAngleTo(direction.GetNormal());
                if (rotAxis.Length > 1e-9)
                {
                    solid.TransformBy(Matrix3d.Rotation(rotAngle, rotAxis.GetNormal(), position));
                }
            }

            // 设置五金件图层和颜色
            SetHardwareLayer(solid, tr, db);

            modelSpace.AppendEntity(solid);
            tr.AddNewlyCreatedDBObject(solid, true);

            return solid.ObjectId;
        }

        #region 五金件3D形状创建 — 使用AutoCAD .NET API

        /// <summary>
        /// 创建圆柱体（使用CreateFrustum）
        /// </summary>
        private static Solid3d CreateFrustumSolid(double radius, double topRadius, double height)
        {
            try
            {
                var solid = new Solid3d();
                solid.SetDatabaseDefaults();
                solid.CreateFrustum(height, radius, radius, topRadius);
                // CreateFrustum创建的圆柱底面中心在原点，顶面中心在(0,0,height)
                // 移动使中心在原点
                solid.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -height / 2)));
                return solid;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 创建长方体
        /// </summary>
        private static Solid3d CreateBoxSolidParams(double width, double depth, double height)
        {
            try
            {
                var solid = new Solid3d();
                solid.SetDatabaseDefaults();
                solid.CreateBox(width, depth, height);
                // CreateBox创建的长方体中心在原点
                // 移动使底面中心在原点
                solid.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, height / 2)));
                return solid;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 布尔并运算
        /// </summary>
        private static bool UnionSolids(Solid3d target, Solid3d tool)
        {
            try
            {
                target.BooleanOperation(BooleanOperationType.BoolUnite, tool);
                if (!IsSolidHealthy(target))
                {
                    PluginLogger.Warning("[Hardware] BoolUnite 后实体健康检查失败");
                    return false;
                }
                return true;
            }
            catch (System.Exception ex)
            {
                PluginLogger.Warning("[Hardware] BoolUnite 失败: " + ex.Message);
                return false;
            }
        }

        private static bool IsSolidHealthy(Solid3d solid)
        {
            if (solid == null || solid.IsErased) return false;
            try
            {
                var vol = solid.MassProperties.Volume;
                if (double.IsNaN(vol) || double.IsInfinity(vol) || vol <= 1e-6) return false;
                var ext = solid.GeometricExtents;
                double dx = ext.MaxPoint.X - ext.MinPoint.X;
                double dy = ext.MaxPoint.Y - ext.MinPoint.Y;
                double dz = ext.MaxPoint.Z - ext.MinPoint.Z;
                return dx > 1e-6 && dy > 1e-6 && dz > 1e-6;
            }
            catch
            {
                return false;
            }
        }

        private static Solid3d CreateCylinderSolid(HardwareSolidParams @params)
        {
            // 主体圆柱
            var solid = CreateFrustumSolid(@params.BodyDiameter / 2, @params.BodyDiameter / 2, @params.BodyDepth);
            if (solid == null) return null;

            // 如果有帽，添加帽圆柱
            if (@params.CapDiameter > 0 && @params.CapThickness > 0)
            {
                try
                {
                    var cap = CreateFrustumSolid(@params.CapDiameter / 2, @params.CapDiameter / 2, @params.CapThickness);
                    if (cap != null)
                    {
                        // 帽在主体上方
                        cap.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, @params.BodyDepth / 2 + @params.CapThickness / 2)));
                        UnionSolids(solid, cap);
                    }
                }
                catch { }
            }

            // 如果有螺纹/销，添加销圆柱
            if (@params.ScrewDiameter > 0 && @params.ScrewLength > 0)
            {
                try
                {
                    var screw = CreateFrustumSolid(@params.ScrewDiameter / 2, @params.ScrewDiameter / 2, @params.ScrewLength);
                    if (screw != null)
                    {
                        // 销从主体底部延伸
                        screw.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -(@params.BodyDepth / 2 + @params.ScrewLength / 2))));
                        UnionSolids(solid, screw);
                    }
                }
                catch { }
            }

            return solid;
        }

        private static Solid3d CreateBoxSolid(HardwareSolidParams @params)
        {
            double w = @params.BoxWidth > 0 ? @params.BoxWidth : @params.BodyDiameter;
            double h = @params.BoxHeight > 0 ? @params.BoxHeight : @params.BodyDepth;
            double d = @params.BoxDepth > 0 ? @params.BoxDepth : @params.BodyDiameter;

            return CreateBoxSolidParams(w, d, h);
        }

        private static Solid3d CreateTShapeSolid(HardwareSolidParams @params)
        {
            // T形：主体圆柱 + 横向销
            var solid = CreateFrustumSolid(@params.BodyDiameter / 2, @params.BodyDiameter / 2, @params.BodyDepth);
            if (solid == null) return null;

            if (@params.ScrewDiameter > 0 && @params.ScrewLength > 0)
            {
                try
                {
                    var crossPin = CreateFrustumSolid(@params.ScrewDiameter / 2, @params.ScrewDiameter / 2, @params.ScrewLength);
                    if (crossPin != null)
                    {
                        // 横向销，旋转90度（绕X轴旋转，使其沿Y方向）
                        crossPin.TransformBy(Matrix3d.Rotation(Math.PI / 2, Vector3d.XAxis, Point3d.Origin));
                        crossPin.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, 0)));
                        UnionSolids(solid, crossPin);
                    }
                }
                catch { }
            }

            return solid;
        }

        #endregion

        #region 图层和显示

        private static void SetHardwareLayer(Solid3d solid, Transaction tr, Database db)
        {
            const string layerName = "五金件";

            try
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(layerName))
                {
                    var ltWrite = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                    var layer = new LayerTableRecord { Name = layerName };
                    layer.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 6);
                    ltWrite.Add(layer);
                    tr.AddNewlyCreatedDBObject(layer, true);
                }

                solid.LayerId = lt[layerName];
                solid.ColorIndex = 6;
            }
            catch
            {
                solid.ColorIndex = 6;
            }
        }

        #endregion
    }
}
