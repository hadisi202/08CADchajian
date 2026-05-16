using System;
using System.Collections.Generic;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    public partial class CabinetCreatorWindow : Window
    {
        // 计算结果
        private CabinetPanelSet _calculatedPanels = null!;

        public CabinetCreatorWindow()
        {
            InitializeComponent();
        }

        private void ChkKickboard_Changed(object sender, RoutedEventArgs e)
        {
            if (KickboardPanel != null)
                KickboardPanel.IsEnabled = ChkKickboard.IsChecked == true;
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            CalculatePanels();
            DisplayPreview();
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            CalculatePanels();
            
            if (_calculatedPanels == null)
            {
                MessageBox.Show("请先计算板件尺寸！", "提示");
                return;
            }

            // 关闭窗口并在AutoCAD中创建
            this.DialogResult = true;
            this.Close();

            // 提示用户选择插入点
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var editor = doc.Editor;

            var opts = new PromptPointOptions("\n请指定柜体左下角插入点: ");
            var result = editor.GetPoint(opts);

            if (result.Status == PromptStatus.OK)
            {
                CreateAllPanels(result.Value);
                editor.WriteMessage("\n柜体板件创建完成！");
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void CalculatePanels()
        {
            try
            {
                // 读取输入
                double width = double.Parse(TxtWidth.Text);
                double height = double.Parse(TxtHeight.Text);
                double depth = double.Parse(TxtDepth.Text);
                double thickness = double.Parse(TxtThickness.Text);
                double backThickness = double.Parse(TxtBackThickness.Text);
                double grooveDepth = double.Parse(TxtGrooveDepth.Text);

                bool sideCoverTopBottom = RbSideCoverTopBottom.IsChecked == true;
                bool backExternal = RbBackExternal.IsChecked == true;

                bool hasKickboard = ChkKickboard.IsChecked == true;
                double kickboardHeight = hasKickboard ? double.Parse(TxtKickboardHeight.Text) : 0;
                double kickboardInset = hasKickboard ? double.Parse(TxtKickboardInset.Text) : 0;

                _calculatedPanels = new CabinetPanelSet();

                // ========== 计算侧板 ==========
                if (sideCoverTopBottom)
                {
                    // 侧盖顶底：侧板全高
                    _calculatedPanels.LeftSide = new PanelDimension
                    {
                        Name = "左侧板",
                        Length = thickness,
                        Width = depth,
                        Height = height
                    };
                    _calculatedPanels.RightSide = new PanelDimension
                    {
                        Name = "右侧板",
                        Length = thickness,
                        Width = depth,
                        Height = height
                    };

                    // 顶底板宽度 = 柜宽 - 2*侧板厚度
                    _calculatedPanels.TopPanel = new PanelDimension
                    {
                        Name = "顶板",
                        Length = width - 2 * thickness,
                        Width = depth,
                        Height = thickness
                    };
                    _calculatedPanels.BottomPanel = new PanelDimension
                    {
                        Name = "底板",
                        Length = width - 2 * thickness,
                        Width = depth - (hasKickboard ? kickboardInset : 0),
                        Height = thickness
                    };
                }
                else
                {
                    // 顶底盖侧：顶底板全宽
                    _calculatedPanels.TopPanel = new PanelDimension
                    {
                        Name = "顶板",
                        Length = width,
                        Width = depth,
                        Height = thickness
                    };
                    _calculatedPanels.BottomPanel = new PanelDimension
                    {
                        Name = "底板",
                        Length = width,
                        Width = depth - (hasKickboard ? kickboardInset : 0),
                        Height = thickness
                    };

                    // 侧板高度 = 柜高 - 2*顶底板厚度
                    _calculatedPanels.LeftSide = new PanelDimension
                    {
                        Name = "左侧板",
                        Length = thickness,
                        Width = depth,
                        Height = height - 2 * thickness
                    };
                    _calculatedPanels.RightSide = new PanelDimension
                    {
                        Name = "右侧板",
                        Length = thickness,
                        Width = depth,
                        Height = height - 2 * thickness
                    };
                }

                // ========== 计算背板 ==========
                if (backExternal)
                {
                    // 外盖式：背板 = 柜体全宽 x 全高
                    _calculatedPanels.BackPanel = new PanelDimension
                    {
                        Name = "背板(外盖)",
                        Length = width,
                        Width = backThickness,
                        Height = height
                    };
                }
                else
                {
                    // 内嵌式：背板 = (柜宽 - 2*侧板 + 2*槽深) x (柜高 - 2*顶底 + 2*槽深)
                    double backWidth = width - 2 * thickness + 2 * grooveDepth;
                    double backHeight = height - 2 * thickness + 2 * grooveDepth;

                    _calculatedPanels.BackPanel = new PanelDimension
                    {
                        Name = "背板(内嵌)",
                        Length = backWidth,
                        Width = backThickness,
                        Height = backHeight
                    };
                }

                // 保存配置供创建时使用
                _calculatedPanels.Config = new CabinetConfig
                {
                    Width = width,
                    Height = height,
                    Depth = depth,
                    Thickness = thickness,
                    BackThickness = backThickness,
                    GrooveDepth = grooveDepth,
                    SideCoverTopBottom = sideCoverTopBottom,
                    BackExternal = backExternal,
                    HasKickboard = hasKickboard,
                    KickboardHeight = kickboardHeight,
                    KickboardInset = kickboardInset
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"计算错误: {ex.Message}", "错误");
                _calculatedPanels = null;
            }
        }

        private void DisplayPreview()
        {
            if (_calculatedPanels == null)
            {
                TxtPreview.Text = "计算失败";
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 板件尺寸计算结果 ===");
            sb.AppendLine();

            void AddPanel(PanelDimension p)
            {
                if (p != null)
                    sb.AppendLine($"{p.Name}: {p.Length:F0} x {p.Width:F0} x {p.Height:F0} mm");
            }

            AddPanel(_calculatedPanels.LeftSide);
            AddPanel(_calculatedPanels.RightSide);
            AddPanel(_calculatedPanels.TopPanel);
            AddPanel(_calculatedPanels.BottomPanel);
            AddPanel(_calculatedPanels.BackPanel);

            sb.AppendLine();
            sb.AppendLine($"盖法: {(_calculatedPanels.Config.SideCoverTopBottom ? "侧盖顶底" : "顶底盖侧")}");
            sb.AppendLine($"背板: {(_calculatedPanels.Config.BackExternal ? "外盖式" : "内嵌式")}");

            if (_calculatedPanels.Config.HasKickboard)
                sb.AppendLine($"踢脚: 高{_calculatedPanels.Config.KickboardHeight}mm 内缩{_calculatedPanels.Config.KickboardInset}mm");

            TxtPreview.Text = sb.ToString();
        }

        private void CreateAllPanels(Point3d insertPoint)
        {
            var config = _calculatedPanels.Config;

            // 左侧板
            var leftSpec = CreateSpec(_calculatedPanels.LeftSide);
            PanelCreationService.CreatePanel(leftSpec, insertPoint);

            // 右侧板
            var rightSpec = CreateSpec(_calculatedPanels.RightSide);
            var rightInsert = new Point3d(
                insertPoint.X + config.Width - config.Thickness,
                insertPoint.Y,
                insertPoint.Z
            );
            PanelCreationService.CreatePanel(rightSpec, rightInsert);

            // 底板
            var bottomSpec = CreateSpec(_calculatedPanels.BottomPanel);
            Point3d bottomInsert;
            if (config.SideCoverTopBottom)
            {
                bottomInsert = new Point3d(
                    insertPoint.X + config.Thickness,
                    insertPoint.Y,
                    insertPoint.Z + (config.HasKickboard ? config.KickboardHeight : 0)
                );
            }
            else
            {
                bottomInsert = new Point3d(
                    insertPoint.X,
                    insertPoint.Y,
                    insertPoint.Z + (config.HasKickboard ? config.KickboardHeight : 0)
                );
            }
            PanelCreationService.CreatePanel(bottomSpec, bottomInsert);

            // 顶板
            var topSpec = CreateSpec(_calculatedPanels.TopPanel);
            Point3d topInsert;
            if (config.SideCoverTopBottom)
            {
                topInsert = new Point3d(
                    insertPoint.X + config.Thickness,
                    insertPoint.Y,
                    insertPoint.Z + config.Height - config.Thickness
                );
            }
            else
            {
                topInsert = new Point3d(
                    insertPoint.X,
                    insertPoint.Y,
                    insertPoint.Z + config.Height - config.Thickness
                );
            }
            PanelCreationService.CreatePanel(topSpec, topInsert);

            // 背板
            var backSpec = CreateSpec(_calculatedPanels.BackPanel);
            Point3d backInsert;
            if (config.BackExternal)
            {
                backInsert = new Point3d(
                    insertPoint.X,
                    insertPoint.Y + config.Depth,
                    insertPoint.Z
                );
            }
            else
            {
                // 内嵌式
                backInsert = new Point3d(
                    insertPoint.X + config.Thickness - config.GrooveDepth,
                    insertPoint.Y + config.Depth - config.BackThickness,
                    insertPoint.Z + config.Thickness - config.GrooveDepth
                );
            }
            PanelCreationService.CreatePanel(backSpec, backInsert);
        }

        private static PanelSpecification CreateSpec(PanelDimension dim)
        {
            return new PanelSpecification
            {
                Length = dim.Length,
                Width = dim.Width,
                Height = dim.Height,
                PanelName = dim.Name,
                Material = "三聚氰胺板"
            };
        }
    }

    // 辅助类
    public class CabinetPanelSet
    {
        public PanelDimension LeftSide { get; set; }
        public PanelDimension RightSide { get; set; }
        public PanelDimension TopPanel { get; set; }
        public PanelDimension BottomPanel { get; set; }
        public PanelDimension BackPanel { get; set; }
        public CabinetConfig Config { get; set; }
    }

    public class PanelDimension
    {
        public string Name { get; set; }
        public double Length { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public class CabinetConfig
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
        public double Thickness { get; set; }
        public double BackThickness { get; set; }
        public double GrooveDepth { get; set; }
        public bool SideCoverTopBottom { get; set; }
        public bool BackExternal { get; set; }
        public bool HasKickboard { get; set; }
        public double KickboardHeight { get; set; }
        public double KickboardInset { get; set; }
    }
}
