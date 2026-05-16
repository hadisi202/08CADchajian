using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System.Text.Json;

namespace FurniturePlugin
{
    /// <summary>
    /// 板件创建服务类，提供统一的板件创建接口
    /// </summary>
    public static class PanelCreationService
    {
        /// <summary>
        /// 创建单个板件
        /// </summary>
        /// <param name="panelSpec">板件规格</param>
        /// <param name="insertPoint">插入点</param>
        /// <returns>创建的板件实体ID</returns>
        public static ObjectId CreatePanel(PanelSpecification panelSpec, Point3d insertPoint)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有活动的AutoCAD文档");

            var db = doc.Database;

            try
            {
                // 验证输入参数
                if (panelSpec == null)
                    throw new ArgumentNullException(nameof(panelSpec), "板件规格不能为空");
                
                // 验证尺寸参数
                if (panelSpec.Length <= 0)
                    throw new ArgumentException($"板件长度必须大于0，当前值: {panelSpec.Length}mm", nameof(panelSpec.Length));
                
                if (panelSpec.Width <= 0)
                    throw new ArgumentException($"板件宽度必须大于0，当前值: {panelSpec.Width}mm", nameof(panelSpec.Width));
                
                if (panelSpec.Height <= 0)
                    throw new ArgumentException($"板件厚度必须大于0，当前值: {panelSpec.Height}mm", nameof(panelSpec.Height));
                
                // 检查尺寸是否过小（小于1mm可能导致创建失败）
                const double minSize = 1.0;
                if (panelSpec.Length < minSize)
                    throw new ArgumentException($"板件长度过小，最小值为{minSize}mm，当前值: {panelSpec.Length}mm");
                
                if (panelSpec.Width < minSize)
                    throw new ArgumentException($"板件宽度过小，最小值为{minSize}mm，当前值: {panelSpec.Width}mm");
                
                if (panelSpec.Height < minSize)
                    throw new ArgumentException($"板件厚度过小，最小值为{minSize}mm，当前值: {panelSpec.Height}mm");

                // 计算实际尺寸
                double actualLength = panelSpec.Length;
                double actualWidth = panelSpec.Width;
                double actualHeight = panelSpec.Height;

                ObjectId entityId;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var blockTable = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var modelSpace = tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                    // 创建3D实体（BOX）
                    var solid = new Solid3d();
                    
                    // 定义BOX的角点
                    Point3d corner1 = insertPoint;
                    Point3d corner2 = new Point3d(
                        insertPoint.X + actualLength,
                        insertPoint.Y + actualWidth,
                        insertPoint.Z + actualHeight
                    );

                    // 创建BOX实体
                    solid.CreateBox(actualLength, actualWidth, actualHeight);
                    
                    // 移动到正确位置
                    Vector3d moveVector = new Vector3d(insertPoint.X, insertPoint.Y, insertPoint.Z);
                    Matrix3d moveMatrix = Matrix3d.Displacement(moveVector);
                    solid.TransformBy(moveMatrix);

                    // 设置实体的显示属性
                    solid.ColorIndex = 256;
                    solid.LinetypeId = db.Celtype;

                    // 添加到模型空间
                    entityId = modelSpace.AppendEntity(solid);
                    tr.AddNewlyCreatedDBObject(solid, true);

                    // 应用旋转变换
                    if (Math.Abs(panelSpec.HorizontalRotation) > 1e-6)
                    {
                        Point3d centerPoint = new Point3d(
                            insertPoint.X + actualLength / 2,
                            insertPoint.Y + actualWidth / 2,
                            insertPoint.Z + actualHeight / 2
                        );
                        
                        Vector3d zAxis = new Vector3d(0, 0, 1);
                        double rotationAngle = panelSpec.HorizontalRotation * Math.PI / 180.0;
                        Matrix3d rotationMatrix = Matrix3d.Rotation(rotationAngle, zAxis, centerPoint);
                        solid.TransformBy(rotationMatrix);
                    }

                    if (Math.Abs(panelSpec.VerticalRotation) > 1e-6)
                    {
                        Point3d centerPoint = new Point3d(
                            insertPoint.X + actualLength / 2,
                            insertPoint.Y + actualWidth / 2,
                            insertPoint.Z + actualHeight / 2
                        );
                        
                        Vector3d xAxis = new Vector3d(1, 0, 0);
                        double rotationAngle = panelSpec.VerticalRotation * Math.PI / 180.0;
                        Matrix3d rotationMatrix = Matrix3d.Rotation(rotationAngle, xAxis, centerPoint);
                        solid.TransformBy(rotationMatrix);
                    }

                    // 创建板件信息
                    var panelInfo = new PanelInfo
                    {
                        Length = panelSpec.Length,
                        Width = panelSpec.Width,
                        Height = panelSpec.Thickness,
                        PanelName = panelSpec.PanelName,
                        Material = panelSpec.Material,
                        OrderId = panelSpec.OrderId,
                        CabinetId = panelSpec.CabinetId,
                        RoomId = panelSpec.RoomId,
                        EdgeBanding = panelSpec.EdgeBanding,
                        Paint = panelSpec.Paint,
                        Remarks = panelSpec.Remarks,
                        EntityId = solid.ObjectId.ToString()
                    };

                    // 保存板件信息到实体
                    SetPanelInfoData(solid, panelInfo, tr);
                    
                    tr.Commit();
                }

                return entityId;
            }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException($"创建板件失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 批量创建板件
        /// </summary>
        /// <param name="panelSpecs">板件规格列表</param>
        /// <param name="baseInsertPoint">基础插入点</param>
        /// <param name="spacing">板件间距</param>
        /// <returns>创建的板件实体ID列表</returns>
        public static List<ObjectId> CreatePanels(List<PanelSpecification> panelSpecs, Point3d baseInsertPoint, double spacing = 50)
        {
            var createdPanels = new List<ObjectId>();
            Point3d currentPoint = baseInsertPoint;

            foreach (var spec in panelSpecs)
            {
                try
                {
                    var panelId = CreatePanel(spec, currentPoint);
                    createdPanels.Add(panelId);

                    // 计算下一个板件的插入点
                    currentPoint = new Point3d(
                        currentPoint.X + spec.Length + spacing,
                        currentPoint.Y,
                        currentPoint.Z
                    );
                }
                catch (System.Exception ex)
                {
                    var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
                    editor?.WriteMessage($"\n创建板件 {spec.PanelName} 失败: {ex.Message}");
                }
            }

            return createdPanels;
        }



        /// <summary>
        /// 保存板件信息到实体的扩展字典
        /// </summary>
        private static void SetPanelInfoData(DBObject obj, PanelInfo data, Transaction tr)
        {
            var extDict = GetExtensionDictionary(obj, tr);
            Xrecord xrec = new Xrecord();
            string dataString = data.ToString();
            xrec.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, dataString));
            extDict.SetAt("FurniturePlugin", xrec);
            tr.AddNewlyCreatedDBObject(xrec, true);
        }

        /// <summary>
        /// 获取或创建扩展字典
        /// </summary>
        private static DBDictionary GetExtensionDictionary(DBObject obj, Transaction tr)
        {
            if (obj.ExtensionDictionary == ObjectId.Null)
            {
                obj.CreateExtensionDictionary();
            }
            return (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForWrite);
        }
    }

    /// <summary>
    /// 板件规格类
    /// </summary>
    public class PanelSpecification
    {
        public double Length { get; set; }
        public double Width { get; set; }
        /// <summary>
        /// 厚度
        /// </summary>
        public double Thickness { get; set; }
        
        /// <summary>
        /// 高度（兼容性属性，等同于厚度）
        /// </summary>
        public double Height 
        { 
            get => Thickness; 
            set => Thickness = value; 
        }
        public string PanelName { get; set; } = "";
        public string Material { get; set; } = "三聚氰胺板";
        public string OrderId { get; set; } = "";
        public string CabinetId { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string EdgeBanding { get; set; } = "";
        public string Paint { get; set; } = "";
        public string Remarks { get; set; } = "";
        public double HorizontalRotation { get; set; } = 0;
        public double VerticalRotation { get; set; } = 0;

        public PanelSpecification() { }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="length">长度</param>
        /// <param name="width">宽度</param>
        /// <param name="thickness">厚度</param>
        /// <param name="panelName">板件名称</param>
        public PanelSpecification(double length, double width, double thickness, string panelName)
        {
            Length = length;
            Width = width;
            Thickness = thickness;
            Height = thickness; // For compatibility
            PanelName = panelName;
        }
    }
}