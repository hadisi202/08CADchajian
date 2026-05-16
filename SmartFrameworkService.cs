using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    /// <summary>
    /// 盖法枚举
    /// </summary>
    public enum CoverMethod
    {
        /// <summary>
        /// 侧板盖顶底板
        /// </summary>
        SideCoverTopBottom,
        
        /// <summary>
        /// 顶底板盖侧板
        /// </summary>
        TopBottomCoverSide
    }

    /// <summary>
    /// 智能外框架服务类
    /// </summary>
    public class SmartFrameworkService
    {
        /// <summary>
        /// 创建参数化外框架（左右侧板+顶底板）
        /// </summary>
        public static FrameworkInfo CreateFramework(FrameworkSpecification spec, Point3d insertPoint)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有活动的AutoCAD文档");

            var createdPanels = new List<ObjectId>();
            var frameworkInfo = new FrameworkInfo
            {
                Width = spec.Width,
                Height = spec.Height,
                Depth = spec.Depth,
                SidePanelThickness = spec.SidePanelThickness,
                TopBottomThickness = spec.TopBottomThickness,
                IsSideCoverTopBottom = spec.IsSideCoverTopBottom,
                InsertPoint = insertPoint,
                CreatedPanels = createdPanels
            };

            try
            {
                // 计算各板件尺寸
                var panelSpecs = CalculateFrameworkPanels(spec);
                
                // 一次性创建所有板件
                // 创建左侧板
                if (spec.CreateLeftPanel)
                {
                    var leftPanelPoint = CalculateLeftPanelPosition(insertPoint, spec);
                    var leftPanelId = PanelCreationService.CreatePanel(panelSpecs.LeftPanel, leftPanelPoint);
                    createdPanels.Add(leftPanelId);
                    frameworkInfo.LeftPanelId = leftPanelId;
                }

                // 创建右侧板
                if (spec.CreateRightPanel)
                {
                    var rightPanelPoint = CalculateRightPanelPosition(insertPoint, spec);
                    var rightPanelId = PanelCreationService.CreatePanel(panelSpecs.RightPanel, rightPanelPoint);
                    createdPanels.Add(rightPanelId);
                    frameworkInfo.RightPanelId = rightPanelId;
                }

                // 创建顶板
                if (spec.CreateTopPanel)
                {
                    var topPanelPoint = CalculateTopPanelPosition(insertPoint, spec);
                    var topPanelId = PanelCreationService.CreatePanel(panelSpecs.TopPanel, topPanelPoint);
                    createdPanels.Add(topPanelId);
                    frameworkInfo.TopPanelId = topPanelId;
                }

                // 创建底板
                if (spec.CreateBottomPanel)
                {
                    var bottomPanelPoint = CalculateBottomPanelPosition(insertPoint, spec);
                    var bottomPanelId = PanelCreationService.CreatePanel(panelSpecs.BottomPanel, bottomPanelPoint);
                    createdPanels.Add(bottomPanelId);
                    frameworkInfo.BottomPanelId = bottomPanelId;
                }

                // 保存框架信息到数据库
                SaveFrameworkInfo(frameworkInfo);

                return frameworkInfo;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"创建外框架失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 创建智能外框架 - 直接创建4个BOX板件
        /// </summary>
        public static FrameworkInfo CreateSmartFramework(FrameworkSpecification spec, Point3d insertPoint)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有活动的AutoCAD文档");

            var editor = doc.Editor;
            var frameworkInfo = new FrameworkInfo
            {
                Width = spec.Width,
                Height = spec.Height,
                Depth = spec.Depth,
                SidePanelThickness = spec.PanelThickness,
                TopBottomThickness = spec.PanelThickness,
                IsSideCoverTopBottom = spec.IsSideCoverTopBottom,
                InsertPoint = insertPoint
            };

            try
            {
                // 添加调试信息
                editor.WriteMessage($"\n=== 开始创建框架 (Y轴为高度) ===");
                editor.WriteMessage($"\n插入点: ({insertPoint.X:F1}, {insertPoint.Y:F1}, {insertPoint.Z:F1})");
                editor.WriteMessage($"\n框架规格: 宽度(X)={spec.Width:F1}, 高度(Y)={spec.Height:F1}, 深度(Z)={spec.Depth:F1}, 板厚={spec.PanelThickness:F1}");
                editor.WriteMessage($"\n盖法: {(spec.IsSideCoverTopBottom ? "侧板盖顶底板" : "顶底板盖侧板")}");

                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // 创建左侧板
                    if (spec.CreateLeftPanel)
                    {
                        var leftBox = new Solid3d();
                        leftBox.CreateBox(spec.PanelThickness, spec.Height, spec.Depth);
                        var center = new Point3d(insertPoint.X + spec.PanelThickness / 2, insertPoint.Y + spec.Height / 2, insertPoint.Z + spec.Depth / 2);
                        leftBox.TransformBy(Matrix3d.Displacement(center.GetAsVector()));
                        btr.AppendEntity(leftBox);
                        tr.AddNewlyCreatedDBObject(leftBox, true);
                        frameworkInfo.CreatedPanels.Add(leftBox.ObjectId);
                        editor.WriteMessage($"\n左侧板创建完成 - 中心点: ({center.X:F1}, {center.Y:F1}, {center.Z:F1})");
                    }

                    // 创建右侧板
                    if (spec.CreateRightPanel)
                    {
                        var rightBox = new Solid3d();
                        rightBox.CreateBox(spec.PanelThickness, spec.Height, spec.Depth);
                        var center = new Point3d(insertPoint.X + spec.Width - spec.PanelThickness / 2, insertPoint.Y + spec.Height / 2, insertPoint.Z + spec.Depth / 2);
                        rightBox.TransformBy(Matrix3d.Displacement(center.GetAsVector()));
                        btr.AppendEntity(rightBox);
                        tr.AddNewlyCreatedDBObject(rightBox, true);
                        frameworkInfo.CreatedPanels.Add(rightBox.ObjectId);
                        editor.WriteMessage($"\n右侧板创建完成 - 中心点: ({center.X:F1}, {center.Y:F1}, {center.Z:F1})");
                    }

                    // 创建顶板
                    if (spec.CreateTopPanel)
                    {
                        var topBox = new Solid3d();
                        double topWidth = spec.IsSideCoverTopBottom ? spec.Width - 2 * spec.PanelThickness : spec.Width;
                        topBox.CreateBox(topWidth, spec.PanelThickness, spec.Depth);
                        var center = new Point3d(insertPoint.X + spec.Width / 2, insertPoint.Y + spec.Height - spec.PanelThickness / 2, insertPoint.Z + spec.Depth / 2);
                        topBox.TransformBy(Matrix3d.Displacement(center.GetAsVector()));
                        btr.AppendEntity(topBox);
                        tr.AddNewlyCreatedDBObject(topBox, true);
                        frameworkInfo.CreatedPanels.Add(topBox.ObjectId);
                        editor.WriteMessage($"\n顶板创建完成 - 中心点: ({center.X:F1}, {center.Y:F1}, {center.Z:F1})");
                    }

                    // 创建底板
                    if (spec.CreateBottomPanel)
                    {
                        var bottomBox = new Solid3d();
                        double bottomWidth = spec.IsSideCoverTopBottom ? spec.Width - 2 * spec.PanelThickness : spec.Width;
                        bottomBox.CreateBox(bottomWidth, spec.PanelThickness, spec.Depth);
                        var center = new Point3d(insertPoint.X + spec.Width / 2, insertPoint.Y + spec.PanelThickness / 2, insertPoint.Z + spec.Depth / 2);
                        bottomBox.TransformBy(Matrix3d.Displacement(center.GetAsVector()));
                        btr.AppendEntity(bottomBox);
                        tr.AddNewlyCreatedDBObject(bottomBox, true);
                        frameworkInfo.CreatedPanels.Add(bottomBox.ObjectId);
                        editor.WriteMessage($"\n底板创建完成 - 中心点: ({center.X:F1}, {center.Y:F1}, {center.Z:F1})");
                    }

                    tr.Commit();
                }

                editor.WriteMessage($"\n外框架创建完成！");
                editor.WriteMessage($"\n框架尺寸: 宽度(X)={spec.Width} 高度(Y)={spec.Height} 深度(Z)={spec.Depth}");
                editor.WriteMessage($"\n板厚: {spec.PanelThickness}mm");
                editor.WriteMessage($"\n盖法: {(spec.IsSideCoverTopBottom ? "侧板盖顶底板" : "顶底板盖侧板")}");

                return frameworkInfo;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"创建外框架失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 智能识别选中板件的尺寸信息
        /// </summary>
        public static PanelDimensions IdentifyPanelDimensions(ObjectId entityId)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有活动的AutoCAD文档");

            using (var tr = doc.TransactionManager.StartTransaction())
            {
                var entity = tr.GetObject(entityId, OpenMode.ForRead);
                
                if (entity is Solid3d solid)
                {
                    // 获取实体的边界框
                    var extents = solid.GeometricExtents;
                    var dimensions = new PanelDimensions
                    {
                        Length = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X),
                        Width = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y),
                        Height = Math.Abs(extents.MaxPoint.Z - extents.MinPoint.Z),
                        InsertPoint = extents.MinPoint
                    };

                    // 尝试从扩展字典获取板件信息
                    var panelInfo = GetPanelInfoFromEntity(solid, tr);
                    if (panelInfo != null)
                    {
                        dimensions.Material = panelInfo.Material;
                        dimensions.PanelName = panelInfo.PanelName;
                        dimensions.EdgeBanding = panelInfo.EdgeBanding;
                    }

                    tr.Commit();
                    return dimensions;
                }
                
                tr.Commit();
                throw new ArgumentException("选择的对象不是有效的3D实体");
            }
        }

        /// <summary>
        /// 框选区域智能识别 - 分析框选范围内的所有板件
        /// </summary>
        public static FrameworkDimensions AnalyzeSelectedRegion()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有活动的AutoCAD文档");

            var editor = doc.Editor;
            
            // 提示用户框选区域
            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\n请框选要分析的区域（包含板件）: "
            };
            
            var selectionResult = editor.GetSelection(selectionOptions);
            if (selectionResult.Status != PromptStatus.OK)
            {
                throw new OperationCanceledException("用户取消了选择操作");
            }

            var panelDimensions = new List<PanelDimensions>();
            
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selObj in selectionResult.Value)
                {
                    var entity = tr.GetObject(selObj.ObjectId, OpenMode.ForRead);
                    if (entity is Solid3d solid)
                    {
                        try
                        {
                            var dimensions = IdentifyPanelDimensions(selObj.ObjectId);
                            panelDimensions.Add(dimensions);
                        }
                        catch
                        {
                            // 忽略无法识别的实体
                        }
                    }
                }
                tr.Commit();
            }

            if (panelDimensions.Count == 0)
            {
                throw new InvalidOperationException("框选区域内没有找到有效的板件");
            }

            // 分析整体框架尺寸
            return AnalyzeFrameworkFromPanels(panelDimensions);
        }

        /// <summary>
        /// 根据已有板件推算柜体整体尺寸
        /// </summary>
        private static FrameworkDimensions AnalyzeFrameworkFromPanels(List<PanelDimensions> panels)
        {
            // 计算所有板件的边界框
            var minX = panels.Min(p => p.InsertPoint.X);
            var maxX = panels.Max(p => p.InsertPoint.X + p.Length);
            var minY = panels.Min(p => p.InsertPoint.Y);
            var maxY = panels.Max(p => p.InsertPoint.Y + p.Width);
            var minZ = panels.Min(p => p.InsertPoint.Z);
            var maxZ = panels.Max(p => p.InsertPoint.Z + p.Height);

            // 智能识别板件类型和厚度
            var sidePanels = panels.Where(p => IsLikelySidePanel(p, panels)).ToList();
            var topBottomPanels = panels.Where(p => IsLikelyTopBottomPanel(p, panels)).ToList();

            var framework = new FrameworkDimensions
            {
                Width = maxX - minX,
                Height = maxZ - minZ,
                Depth = maxY - minY,
                InsertPoint = new Point3d(minX, minY, minZ)
            };

            // 推算板厚
            if (sidePanels.Any())
            {
                framework.SidePanelThickness = sidePanels.First().Length; // 侧板通常长度就是厚度
            }
            
            if (topBottomPanels.Any())
            {
                framework.TopBottomThickness = topBottomPanels.First().Height; // 顶底板通常高度就是厚度
            }

            // 推算盖法
            framework.IsSideCoverTopBottom = DetermineCoverMethod(sidePanels, topBottomPanels, framework);

            return framework;
        }

        /// <summary>
        /// 判断是否为侧板
        /// </summary>
        private static bool IsLikelySidePanel(PanelDimensions panel, List<PanelDimensions> allPanels)
        {
            // 侧板通常：长度（X方向）是厚度，且小于宽度和高度
            // 或者在创建时，我们定义 Box(Thickness, Height, Depth) -> X=Thickness, Y=Height, Z=Depth
            // 但 IdentifyPanelDimensions 返回的是 Length=X, Width=Y, Height=Z
            // 所以侧板特征：Length 是最小的（厚度）
            
            double thickness = Math.Min(panel.Length, Math.Min(panel.Width, panel.Height));
            return Math.Abs(panel.Length - thickness) < 1.0; // X轴是厚度
        }

        /// <summary>
        /// 判断是否为顶底板
        /// </summary>
        private static bool IsLikelyTopBottomPanel(PanelDimensions panel, List<PanelDimensions> allPanels)
        {
            // 顶底板通常：高度（Y方向）是厚度
            // 创建时 Box(Width, Thickness, Depth) -> X=Width, Y=Thickness, Z=Depth
            // 所以顶底板特征：Width(Y) 是最小的（厚度）
            
            double thickness = Math.Min(panel.Length, Math.Min(panel.Width, panel.Height));
            return Math.Abs(panel.Width - thickness) < 1.0; // Y轴是厚度
        }

        /// <summary>
        /// 推算盖法
        /// </summary>
        private static bool DetermineCoverMethod(List<PanelDimensions> sidePanels, List<PanelDimensions> topBottomPanels, FrameworkDimensions framework)
        {
            if (!sidePanels.Any() || !topBottomPanels.Any())
                return true; // 默认侧板盖顶底板

            var sidePanel = sidePanels.First();
            var topBottomPanel = topBottomPanels.First();

            // 如果侧板高度等于框架高度，说明是侧板盖顶底板
            // 如果侧板高度小于框架高度，说明是顶底板盖侧板
            return Math.Abs(sidePanel.Height - framework.Height) < Math.Abs(sidePanel.Height - (framework.Height - 2 * topBottomPanel.Height));
        }

        /// <summary>
        /// 计算框架各板件规格
        /// </summary>
        private static FrameworkPanelSpecs CalculateFrameworkPanels(FrameworkSpecification spec)
        {
            var specs = new FrameworkPanelSpecs();

            if (spec.IsSideCoverTopBottom)
            {
                // 侧板盖顶底板
                specs.LeftPanel = new PanelSpecification(
                    spec.PanelThickness, spec.Depth, spec.Height, "左侧板")
                { 
                    Material = spec.Material,
                    OrderId = spec.OrderId,
                    CabinetId = spec.CabinetId,
                    RoomId = spec.RoomId,
                    EdgeBanding = spec.EdgeBanding,
                    Paint = spec.Paint,
                    Remarks = spec.Remarks
                };

                specs.RightPanel = new PanelSpecification(
                    spec.PanelThickness, spec.Depth, spec.Height, "右侧板")
                { 
                    Material = spec.Material,
                    OrderId = spec.OrderId,
                    CabinetId = spec.CabinetId,
                    RoomId = spec.RoomId,
                    EdgeBanding = spec.EdgeBanding,
                    Paint = spec.Paint,
                    Remarks = spec.Remarks
                };

                specs.TopPanel = new PanelSpecification(
                    spec.Width - 2 * spec.PanelThickness, spec.Depth, spec.PanelThickness, "顶板")
                { 
                    Material = spec.Material,
                    OrderId = spec.OrderId,
                    CabinetId = spec.CabinetId,
                    RoomId = spec.RoomId,
                    EdgeBanding = spec.EdgeBanding,
                    Paint = spec.Paint,
                    Remarks = spec.Remarks
                };

                specs.BottomPanel = new PanelSpecification(
                    spec.Width - 2 * spec.PanelThickness, spec.Depth, spec.PanelThickness, "底板")
                { 
                    Material = spec.Material,
                    OrderId = spec.OrderId,
                    CabinetId = spec.CabinetId,
                    RoomId = spec.RoomId,
                    EdgeBanding = spec.EdgeBanding,
                    Paint = spec.Paint,
                    Remarks = spec.Remarks
                };
            }
            else
            {
                // 顶底板盖侧板
                specs.LeftPanel = new PanelSpecification(
                    spec.PanelThickness, spec.Depth, spec.Height - 2 * spec.PanelThickness, "左侧板")
                { 
                    Material = spec.Material,
                    OrderId = spec.OrderId,
                    CabinetId = spec.CabinetId,
                    RoomId = spec.RoomId,
                    EdgeBanding = spec.EdgeBanding,
                    Paint = spec.Paint,
                    Remarks = spec.Remarks
                };

                specs.RightPanel = new PanelSpecification(
                    spec.PanelThickness, spec.Depth, spec.Height - 2 * spec.PanelThickness, "右侧板")
                { 
                    Material = spec.Material,
                    OrderId = spec.OrderId,
                    CabinetId = spec.CabinetId,
                    RoomId = spec.RoomId,
                    EdgeBanding = spec.EdgeBanding,
                    Paint = spec.Paint,
                    Remarks = spec.Remarks
                };

                specs.TopPanel = new PanelSpecification(
                    spec.Width, spec.Depth, spec.PanelThickness, "顶板")
                { 
                    Material = spec.Material,
                    OrderId = spec.OrderId,
                    CabinetId = spec.CabinetId,
                    RoomId = spec.RoomId,
                    EdgeBanding = spec.EdgeBanding,
                    Paint = spec.Paint,
                    Remarks = spec.Remarks
                };

                specs.BottomPanel = new PanelSpecification(
                    spec.Width, spec.Depth, spec.PanelThickness, "底板")
                { 
                    Material = spec.Material,
                    OrderId = spec.OrderId,
                    CabinetId = spec.CabinetId,
                    RoomId = spec.RoomId,
                    EdgeBanding = spec.EdgeBanding,
                    Paint = spec.Paint,
                    Remarks = spec.Remarks
                };
            }

            return specs;
        }

        /// <summary>
        /// 计算左侧板位置 - 基点就是左侧板的左下角
        /// </summary>
        private static Point3d CalculateLeftPanelPosition(Point3d basePoint, FrameworkSpecification spec)
        {
            // 左侧板的左下角就是基点
            return basePoint;
        }

        /// <summary>
        /// 计算右侧板位置 - 在框架右侧
        /// </summary>
        private static Point3d CalculateRightPanelPosition(Point3d basePoint, FrameworkSpecification spec)
        {
            // 右侧板的左下角位置
            return new Point3d(
                basePoint.X + spec.Width - spec.PanelThickness,
                basePoint.Y,
                basePoint.Z
            );
        }

        /// <summary>
        /// 计算顶板位置
        /// </summary>
        private static Point3d CalculateTopPanelPosition(Point3d basePoint, FrameworkSpecification spec)
        {
            if (spec.IsSideCoverTopBottom)
            {
                // 侧板盖顶底板：顶底板在两侧板之间
                // 顶底板起始位置 = 基点X + 左侧板厚度
                return new Point3d(
                    basePoint.X + spec.PanelThickness,
                    basePoint.Y,
                    basePoint.Z + spec.Height - spec.PanelThickness
                );
            }
            else
            {
                // 顶底板盖侧板：顶底板覆盖整个宽度
                return new Point3d(
                    basePoint.X,
                    basePoint.Y,
                    basePoint.Z + spec.Height - spec.PanelThickness
                );
            }
        }

        /// <summary>
        /// 计算底板位置
        /// </summary>
        private static Point3d CalculateBottomPanelPosition(Point3d basePoint, FrameworkSpecification spec)
        {
            if (spec.IsSideCoverTopBottom)
            {
                // 侧板盖顶底板：顶底板在两侧板之间
                // 顶底板起始位置 = 基点X + 左侧板厚度
                return new Point3d(
                    basePoint.X + spec.PanelThickness,
                    basePoint.Y,
                    basePoint.Z
                );
            }
            else
            {
                // 顶底板盖侧板：顶底板覆盖整个宽度
                return new Point3d(
                    basePoint.X,
                    basePoint.Y,
                    basePoint.Z
                );
            }
        }

        /// <summary>
        /// 从实体获取板件信息
        /// </summary>
        private static PanelInfo GetPanelInfoFromEntity(DBObject entity, Transaction tr)
        {
            // 实现从扩展字典获取板件信息的逻辑
            // 这里简化处理，实际应该从扩展字典读取
            return null;
        }

        /// <summary>
        /// 保存框架信息
        /// </summary>
        private static void SaveFrameworkInfo(FrameworkInfo frameworkInfo)
        {
            // 实现框架信息保存逻辑
            // 可以保存到文件或数据库
        }
    }

    /// <summary>
    /// 框架规格定义
    /// </summary>
    public class FrameworkSpecification
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
        public double SidePanelThickness { get; set; } = 18;
        public double TopBottomThickness { get; set; } = 18;
        public double PanelThickness { get; set; } = 18; // 统一板厚
        public bool IsSideCoverTopBottom { get; set; } = true;
        public string Material { get; set; } = "三聚氰胺板";
        public string OrderId { get; set; } = "";
        public string CabinetId { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string EdgeBanding { get; set; } = "";
        public string Paint { get; set; } = "";
        public string Remarks { get; set; } = "";
        public bool CreateLeftPanel { get; set; } = true;
        public bool CreateRightPanel { get; set; } = true;
        public bool CreateTopPanel { get; set; } = true;
        public bool CreateBottomPanel { get; set; } = true;
    }

    /// <summary>
    /// 框架信息
    /// </summary>
    public class FrameworkInfo
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
        public double SidePanelThickness { get; set; }
        public double TopBottomThickness { get; set; }
        public bool IsSideCoverTopBottom { get; set; }
        public Point3d InsertPoint { get; set; }
        public List<ObjectId> CreatedPanels { get; set; } = new List<ObjectId>();
        public ObjectId LeftPanelId { get; set; }
        public ObjectId RightPanelId { get; set; }
        public ObjectId TopPanelId { get; set; }
        public ObjectId BottomPanelId { get; set; }
    }

    /// <summary>
    /// 板件尺寸信息
    /// </summary>
    public class PanelDimensions
    {
        public double Length { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public Point3d InsertPoint { get; set; }
        public string Material { get; set; } = "";
        public string PanelName { get; set; } = "";
        public string EdgeBanding { get; set; } = "";
    }

    /// <summary>
    /// 框架尺寸信息
    /// </summary>
    public class FrameworkDimensions
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
        public double SidePanelThickness { get; set; } = 18;
        public double TopBottomThickness { get; set; } = 18;
        public bool IsSideCoverTopBottom { get; set; } = true;
        public Point3d InsertPoint { get; set; }
    }

    /// <summary>
    /// 框架板件规格
    /// </summary>
    public class FrameworkPanelSpecs
    {
        public PanelSpecification LeftPanel { get; set; } = null!;
        public PanelSpecification RightPanel { get; set; } = null!;
        public PanelSpecification TopPanel { get; set; } = null!;
        public PanelSpecification BottomPanel { get; set; } = null!;
    }
}