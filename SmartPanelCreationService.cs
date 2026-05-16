using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    /// <summary>
    /// 快速板件创建服务 - 使用可配置的默认参数
    /// </summary>
    public static class SmartPanelCreationService
    {
        #region 可配置的默认参数

        // 柜体尺寸
        public static double CabinetWidth { get; set; } = 800;   // 柜体宽度
        public static double CabinetHeight { get; set; } = 2400; // 柜体高度
        public static double CabinetDepth { get; set; } = 600;   // 柜体深度

        // 板件厚度
        public static double SideThickness { get; set; } = 18;      // 侧板厚度
        public static double TopBottomThickness { get; set; } = 18; // 顶底板厚度
        public static double ShelfThickness { get; set; } = 18;     // 层板厚度
        public static double BackThickness { get; set; } = 5;       // 背板厚度

        // 层板配置
        public static int ShelfCount { get; set; } = 3;  // 层板数量

        // 材质
        public static string DefaultMaterial { get; set; } = "三聚氰胺板";
        public static string BackPanelMaterial { get; set; } = "密度板";

        #endregion

        /// <summary>
        /// 显示当前默认参数
        /// </summary>
        public static void ShowCurrentSettings()
        {
            var editor = Application.DocumentManager.MdiActiveDocument.Editor;
            editor.WriteMessage("\n=== 当前默认参数 ===");
            editor.WriteMessage($"\n柜体宽度: {CabinetWidth}mm");
            editor.WriteMessage($"\n柜体高度: {CabinetHeight}mm");
            editor.WriteMessage($"\n柜体深度: {CabinetDepth}mm");
            editor.WriteMessage($"\n侧板厚度: {SideThickness}mm");
            editor.WriteMessage($"\n顶底板厚度: {TopBottomThickness}mm");
            editor.WriteMessage($"\n层板厚度: {ShelfThickness}mm");
            editor.WriteMessage($"\n背板厚度: {BackThickness}mm");
            editor.WriteMessage($"\n层板数量: {ShelfCount}");
        }

        /// <summary>
        /// 创建左右侧板（在指定插入点）
        /// </summary>
        public static void CreateSidePanelsAtPoint(Point3d insertPoint)
        {
            var editor = Application.DocumentManager.MdiActiveDocument.Editor;

            // 左侧板
            var leftPanel = new PanelSpecification
            {
                Length = SideThickness,
                Width = CabinetDepth,
                Height = CabinetHeight,
                PanelName = "左侧板",
                Material = DefaultMaterial
            };

            // 右侧板
            var rightPanel = new PanelSpecification
            {
                Length = SideThickness,
                Width = CabinetDepth,
                Height = CabinetHeight,
                PanelName = "右侧板",
                Material = DefaultMaterial
            };

            // 左侧板插入点 = 用户点击的点
            Point3d leftInsert = insertPoint;

            // 右侧板插入点 = X + 柜宽 - 侧板厚度
            Point3d rightInsert = new Point3d(
                insertPoint.X + CabinetWidth - SideThickness,
                insertPoint.Y,
                insertPoint.Z
            );

            PanelCreationService.CreatePanel(leftPanel, leftInsert);
            PanelCreationService.CreatePanel(rightPanel, rightInsert);

            editor.WriteMessage($"\n已创建左右侧板。尺寸: {SideThickness}x{CabinetDepth}x{CabinetHeight}mm");
        }

        /// <summary>
        /// 创建顶底板（在指定插入点）
        /// </summary>
        public static void CreateTopBottomPanelsAtPoint(Point3d insertPoint)
        {
            var editor = Application.DocumentManager.MdiActiveDocument.Editor;

            // 顶底板宽度 = 柜宽 - 2*侧板厚度 (侧盖顶底)
            double panelWidth = CabinetWidth - 2 * SideThickness;

            var topPanel = new PanelSpecification
            {
                Length = panelWidth,
                Width = CabinetDepth,
                Height = TopBottomThickness,
                PanelName = "顶板",
                Material = DefaultMaterial
            };

            var bottomPanel = new PanelSpecification
            {
                Length = panelWidth,
                Width = CabinetDepth,
                Height = TopBottomThickness,
                PanelName = "底板",
                Material = DefaultMaterial
            };

            // 底板插入点 = X + 侧板厚度
            Point3d bottomInsert = new Point3d(
                insertPoint.X + SideThickness,
                insertPoint.Y,
                insertPoint.Z
            );

            // 顶板插入点 = Z + 柜高 - 顶板厚度
            Point3d topInsert = new Point3d(
                insertPoint.X + SideThickness,
                insertPoint.Y,
                insertPoint.Z + CabinetHeight - TopBottomThickness
            );

            PanelCreationService.CreatePanel(bottomPanel, bottomInsert);
            PanelCreationService.CreatePanel(topPanel, topInsert);

            editor.WriteMessage($"\n已创建顶底板。尺寸: {panelWidth}x{CabinetDepth}x{TopBottomThickness}mm");
        }

        /// <summary>
        /// 创建层板（在指定插入点）
        /// </summary>
        public static void CreateShelvesAtPoint(Point3d insertPoint)
        {
            var editor = Application.DocumentManager.MdiActiveDocument.Editor;

            // 层板宽度 = 柜宽 - 2*侧板厚度
            double shelfWidth = CabinetWidth - 2 * SideThickness;
            // 层板深度 = 柜深 - 20 (内缩)
            double shelfDepth = CabinetDepth - 20;

            // 可用高度 = 柜高 - 2*顶底板厚度
            double availableHeight = CabinetHeight - 2 * TopBottomThickness;
            double spacing = availableHeight / (ShelfCount + 1);

            var shelfSpec = new PanelSpecification
            {
                Length = shelfWidth,
                Width = shelfDepth,
                Height = ShelfThickness,
                PanelName = "层板",
                Material = DefaultMaterial
            };

            double startZ = insertPoint.Z + TopBottomThickness;

            for (int i = 1; i <= ShelfCount; i++)
            {
                double z = startZ + i * spacing - (ShelfThickness / 2);

                Point3d insert = new Point3d(
                    insertPoint.X + SideThickness,
                    insertPoint.Y + 10, // 前缩10mm
                    z
                );

                PanelCreationService.CreatePanel(shelfSpec, insert);
            }

            editor.WriteMessage($"\n已创建 {ShelfCount} 个层板。尺寸: {shelfWidth}x{shelfDepth}x{ShelfThickness}mm");
        }

        /// <summary>
        /// 创建背板（在指定插入点）
        /// </summary>
        public static void CreateBackPanelAtPoint(Point3d insertPoint)
        {
            var editor = Application.DocumentManager.MdiActiveDocument.Editor;

            // 背板宽度 = 柜宽 - 22 (内嵌式，槽深7mm)
            double backWidth = CabinetWidth - 22;
            // 背板高度 = 柜高 - 22
            double backHeight = CabinetHeight - 22;

            var backPanel = new PanelSpecification
            {
                Length = backWidth,
                Width = BackThickness,
                Height = backHeight,
                PanelName = "背板",
                Material = BackPanelMaterial
            };

            // 插入点：X + 11, Y + 柜深 - 背板厚度 - 2, Z + 11
            Point3d insert = new Point3d(
                insertPoint.X + 11,
                insertPoint.Y + CabinetDepth - BackThickness - 2,
                insertPoint.Z + 11
            );

            PanelCreationService.CreatePanel(backPanel, insert);

            editor.WriteMessage($"\n已创建背板。尺寸: {backWidth}x{BackThickness}x{backHeight}mm");
        }
    }
}
