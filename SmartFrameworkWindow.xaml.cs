using System;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;

namespace FurniturePlugin
{
    /// <summary>
    /// 智能外框架创建窗口
    /// </summary>
    public partial class SmartFrameworkWindow : Window
    {
        private FrameworkSpecification _frameworkSpec = null!;

        public SmartFrameworkWindow()
        {
            InitializeComponent();
            this.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CreateFrameworkButton_Click(s, e); e.Handled = true; } };
            InitializeData();
        }

        private void InitializeData()
        {
            _frameworkSpec = new FrameworkSpecification
            {
                Width = 800,
                Height = 2400,
                Depth = 600,
                PanelThickness = 18,
                IsSideCoverTopBottom = true,
                Material = "三聚氰胺板",
                CreateLeftPanel = true,
                CreateRightPanel = true,
                CreateTopPanel = true,
                CreateBottomPanel = true
            };
        }

        private void SelectPanelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 隐藏窗口以便用户在CAD中进行选择
                this.Hide();

                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    var editor = doc.Editor;

                    // 提示用户选择板件
                    var entityOptions = new PromptEntityOptions("\n请选择一个板件（3D实体）进行尺寸识别: ");
                    entityOptions.SetRejectMessage("\n请选择有效的3D实体。");
                    var entityResult = editor.GetEntity(entityOptions);

                    if (entityResult.Status == PromptStatus.OK)
                    {
                        // 识别板件尺寸
                        var dimensions = SmartFrameworkService.IdentifyPanelDimensions(entityResult.ObjectId);

                        this.Dispatcher.Invoke(() =>
                        {
                            // 智能推算柜体尺寸
                            InferCabinetDimensions(dimensions);
                            UpdateUIFromSpec();

                            IdentificationResultText.Text = $"识别成功！柜体尺寸：{_frameworkSpec.Width}×{_frameworkSpec.Depth}×{_frameworkSpec.Height}mm";
                            IdentificationResultText.Foreground = System.Windows.Media.Brushes.Green;
                        });
                    }
                    else
                    {
                        this.Dispatcher.Invoke(() =>
                        {
                            IdentificationResultText.Text = "识别操作已取消";
                            IdentificationResultText.Foreground = System.Windows.Media.Brushes.Gray;
                        });
                    }
                }

                // 重新显示窗口
                this.Show();
            }
            catch (Exception ex)
            {
                this.Show();
                MessageBox.Show($"板件识别失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                IdentificationResultText.Text = $"识别失败: {ex.Message}";
                IdentificationResultText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void SelectRegionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 隐藏窗口以便用户在CAD中进行选择
                this.Hide();

                // 框选区域识别
                var frameworkDimensions = SmartFrameworkService.AnalyzeSelectedRegion();

                this.Dispatcher.Invoke(() =>
                {
                    // 更新框架规格
                    _frameworkSpec.Width = frameworkDimensions.Width;
                    _frameworkSpec.Height = frameworkDimensions.Height;
                    _frameworkSpec.Depth = frameworkDimensions.Depth;
                    _frameworkSpec.PanelThickness = frameworkDimensions.SidePanelThickness; // 使用侧板厚度作为统一板厚
                    _frameworkSpec.IsSideCoverTopBottom = frameworkDimensions.IsSideCoverTopBottom;

                    UpdateUIFromSpec();

                    IdentificationResultText.Text = $"区域识别成功！柜体尺寸：{_frameworkSpec.Width:F0}×{_frameworkSpec.Depth:F0}×{_frameworkSpec.Height:F0}mm";
                    IdentificationResultText.Foreground = System.Windows.Media.Brushes.Green;
                });

                // 重新显示窗口
                this.Show();
            }
            catch (Exception ex)
            {
                this.Show();
                MessageBox.Show($"区域识别失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                IdentificationResultText.Text = $"识别失败: {ex.Message}";
                IdentificationResultText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void ManualInputButton_Click(object sender, RoutedEventArgs e)
        {
            IdentificationResultText.Text = "手动输入模式，请输入柜体尺寸参数";
            IdentificationResultText.Foreground = System.Windows.Media.Brushes.Blue;
        }

        private void CreateFrameworkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 验证输入
                if (!ValidateInput())
                {
                    return;
                }

                // 更新规格
                UpdateSpecFromUI();

                // 隐藏窗口
                this.Hide();

                // 获取AutoCAD文档和编辑器
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    MessageBox.Show("没有活动的AutoCAD文档", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    this.Show();
                    return;
                }

                var editor = doc.Editor;

                // 提示用户选择插入点
                var pointOptions = new PromptPointOptions("\n请选择框架插入点: ");
                var pointResult = editor.GetPoint(pointOptions);

                if (pointResult.Status == PromptStatus.OK)
                {
                    try
                    {
                        // 创建框架
                        var frameworkInfo = SmartFrameworkService.CreateSmartFramework(_frameworkSpec, pointResult.Value);
                        
                        // 显示创建结果
                        var createdCount = frameworkInfo.CreatedPanels.Count;
                        var message = $"框架创建成功！\n" +
                                    $"创建了 {createdCount} 个板件\n" +
                                    $"框架尺寸: {_frameworkSpec.Width}×{_frameworkSpec.Depth}×{_frameworkSpec.Height}mm\n" +
                                    $"板厚: {_frameworkSpec.PanelThickness}mm";
                        
                        MessageBox.Show(message, "创建成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        
                        // 隐藏窗口而不是关闭，避免CAD退出
                        this.Hide();
                    }
                    catch (System.Exception ex)
                    {
                        // 显示错误信息但不退出CAD
                        var errorMessage = $"创建框架时发生错误:\n{ex.Message}";
                        if (ex.InnerException != null)
                        {
                            errorMessage += $"\n详细信息: {ex.InnerException.Message}";
                        }
                        
                        MessageBox.Show(errorMessage, "创建失败", MessageBoxButton.OK, MessageBoxImage.Error);
                        this.Show();
                    }
                }
                else
                {
                    // 用户取消了选择
                    this.Show();
                }
            }
            catch (System.Exception ex)
            {
                // 最外层异常处理，确保不会导致CAD退出
                var errorMessage = $"操作过程中发生未预期的错误:\n{ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMessage += $"\n详细信息: {ex.InnerException.Message}";
                }
                
                MessageBox.Show(errorMessage, "系统错误", MessageBoxButton.OK, MessageBoxImage.Error);
                
                // 确保窗口可见
                try
                {
                    this.Show();
                }
                catch
                {
                    // 如果显示窗口也失败，至少不要让CAD崩溃
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 安全关闭窗口，不设置DialogResult
            this.Hide();
        }

        /// <summary>
        /// 窗口关闭事件处理，防止CAD退出
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                // 取消关闭事件，改为隐藏窗口
                e.Cancel = true;
                this.Hide();
            }
            catch (Exception ex)
            {
                // 即使在关闭事件中出错，也不要让CAD崩溃
                System.Diagnostics.Debug.WriteLine($"窗口关闭事件错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据单个板件推算柜体尺寸
        /// </summary>
        private void InferCabinetDimensions(PanelDimensions panelDimensions)
        {
            // 根据板件特征推算柜体尺寸
            if (IsLikelySidePanel(panelDimensions))
            {
                // 如果是侧板，推算柜体尺寸
                _frameworkSpec.PanelThickness = panelDimensions.Length;
                _frameworkSpec.Depth = panelDimensions.Width;
                _frameworkSpec.Height = panelDimensions.Height;
                
                // 推算宽度（假设标准柜体宽度）
                _frameworkSpec.Width = 800; // 默认值，可以根据经验调整
            }
            else if (IsLikelyTopBottomPanel(panelDimensions))
            {
                // 如果是顶底板，推算柜体尺寸
                _frameworkSpec.Width = panelDimensions.Length;
                _frameworkSpec.Depth = panelDimensions.Width;
                _frameworkSpec.PanelThickness = panelDimensions.Height;
                
                // 推算高度（假设标准柜体高度）
                _frameworkSpec.Height = 2400; // 默认值
            }
            else
            {
                // 无法确定板件类型，使用板件尺寸作为参考
                _frameworkSpec.Width = Math.Max(panelDimensions.Length, panelDimensions.Width);
                _frameworkSpec.Depth = Math.Min(panelDimensions.Length, panelDimensions.Width);
                _frameworkSpec.Height = Math.Max(panelDimensions.Height, 2400);
                
                // 统一板厚
                double thickness = Math.Min(panelDimensions.Length, Math.Min(panelDimensions.Width, panelDimensions.Height));
                _frameworkSpec.PanelThickness = thickness;
            }

            // 设置材质
            if (!string.IsNullOrEmpty(panelDimensions.Material))
            {
                _frameworkSpec.Material = panelDimensions.Material;
            }
        }

        private static bool IsLikelySidePanel(PanelDimensions panel)
        {
            double thickness = Math.Min(panel.Length, Math.Min(panel.Width, panel.Height));
            return Math.Abs(panel.Length - thickness) < 1.0;
        }

        private static bool IsLikelyTopBottomPanel(PanelDimensions panel)
        {
            double thickness = Math.Min(panel.Length, Math.Min(panel.Width, panel.Height));
            return Math.Abs(panel.Width - thickness) < 1.0;
        }

        private void UpdateUIFromSpec()
        {
            WidthTextBox.Text = _frameworkSpec.Width.ToString("F0");
            HeightTextBox.Text = _frameworkSpec.Height.ToString("F0");
            DepthTextBox.Text = _frameworkSpec.Depth.ToString("F0");
            ThicknessTextBox.Text = _frameworkSpec.PanelThickness.ToString("F0");

            SideCoverRadio.IsChecked = _frameworkSpec.IsSideCoverTopBottom;
            TopBottomCoverRadio.IsChecked = !_frameworkSpec.IsSideCoverTopBottom;

            // 设置材质
            foreach (ComboBoxItem item in MaterialComboBox.Items)
            {
                if (item.Content.ToString() == _frameworkSpec.Material)
                {
                    MaterialComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void UpdateSpecFromUI()
        {
            if (double.TryParse(WidthTextBox.Text, out double width))
                _frameworkSpec.Width = width;

            if (double.TryParse(HeightTextBox.Text, out double height))
                _frameworkSpec.Height = height;

            if (double.TryParse(DepthTextBox.Text, out double depth))
                _frameworkSpec.Depth = depth;

            if (double.TryParse(ThicknessTextBox.Text, out double thickness))
            {
                _frameworkSpec.PanelThickness = thickness;
            }

            _frameworkSpec.IsSideCoverTopBottom = SideCoverRadio.IsChecked == true;
            
            // 默认创建所有板件
            _frameworkSpec.CreateLeftPanel = true;
            _frameworkSpec.CreateRightPanel = true;
            _frameworkSpec.CreateTopPanel = true;
            _frameworkSpec.CreateBottomPanel = true;

            _frameworkSpec.Material = (MaterialComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "三聚氰胺板";
        }

        private bool ValidateInput()
        {
            UpdateSpecFromUI();

            if (_frameworkSpec.Width <= 0)
            {
                MessageBox.Show("柜体宽度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                WidthTextBox.Focus();
                return false;
            }

            if (_frameworkSpec.Height <= 0)
            {
                MessageBox.Show("柜体高度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                HeightTextBox.Focus();
                return false;
            }

            if (_frameworkSpec.Depth <= 0)
            {
                MessageBox.Show("柜体深度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                DepthTextBox.Focus();
                return false;
            }

            if (_frameworkSpec.PanelThickness <= 0)
            {
                MessageBox.Show("板厚必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                ThicknessTextBox.Focus();
                return false;
            }

            return true;
        }
    }
}