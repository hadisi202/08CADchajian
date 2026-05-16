using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    /// <summary>
    /// 左右侧板参数化建模窗口
    /// </summary>
    public partial class SidePanelWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        private SidePanelData _panelData = null!;

        public SidePanelData PanelData
        {
            get { return _panelData; }
            set
            {
                _panelData = value;
                OnPropertyChanged(nameof(PanelData));
            }
        }

        public SidePanelWindow(Autodesk.AutoCAD.DatabaseServices.Extents3d? frameExtents = null)
        {
            InitializeComponent();
            this.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CreateButton_Click(s, e); e.Handled = true; } };

            // 加载保存的配置数据
            PanelData = DataPersistenceService.LoadSidePanelData();
            DataContext = this;

            if (frameExtents.HasValue)
            {
                var extents = frameExtents.Value;
                PanelData.CabinetDepth = Math.Abs(extents.MaxPoint.Z - extents.MinPoint.Z);
                PanelData.CabinetHeight = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
                // 宽度可以从X轴获得，但对于侧板来说，我们更关心深度和高度
            }

            // 设置UI值
            CabinetDepthTextBox.Text = PanelData.CabinetDepth.ToString("F0");
            CabinetHeightTextBox.Text = PanelData.CabinetHeight.ToString("F0");
            SidePanelThicknessTextBox.Text = PanelData.SidePanelThickness.ToString();
            TopBottomThicknessTextBox.Text = PanelData.TopBottomThickness.ToString();

            // 设置盖法选择
            if (PanelData.IsSideCoverTopBottom)
                SideCoverTopBottomRadio.IsChecked = true;
            else
                TopBottomCoverSideRadio.IsChecked = true;

            // 设置创建选项
            CreateLeftPanelCheckBox.IsChecked = PanelData.CreateLeftPanel;
            CreateRightPanelCheckBox.IsChecked = PanelData.CreateRightPanel;

            // 初始计算
            CalculateDimensions();
        }

        private void InitializeData()
        {
            PanelData = new SidePanelData
            {
                CabinetDepth = 600,
                CabinetHeight = 2400,
                SidePanelThickness = 18,
                TopBottomThickness = 18,
                IsSideCoverTopBottom = true,
                CreateLeftPanel = true,
                CreateRightPanel = true,
                LeftPanelMaterial = "三聚氰胺板",
                RightPanelMaterial = "三聚氰胺板"
            };

            DataContext = this;
        }

        private void CalculateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateDataFromUI();
                CalculateDimensions();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"保存数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void IdentifyButton_Click(object sender, RoutedEventArgs e)
        {
            CadEntityHelper.SafeCadSelection(this, editor =>
            {
                var entityOptions = new PromptEntityOptions("\n请选择一个矩形或立体对象作为参考: ");
                entityOptions.SetRejectMessage("\n请选择有效的对象。");
                var entityResult = editor.GetEntity(entityOptions);

                if (entityResult.Status == PromptStatus.OK)
                {
                    var result = CadEntityHelper.IdentifyDimensionsFromEntity(entityResult.ObjectId);
                    if (result.HasValue && result.Value.identified)
                    {
                        var (width, height, depth, _) = result.Value;
                        this.Dispatcher.Invoke(() =>
                        {
                            CabinetDepthTextBox.Text = depth.ToString("F0");
                            CabinetHeightTextBox.Text = height.ToString("F0");
                            UpdateDataFromUI();
                            CalculateDimensions();
                            MessageBox.Show($"识别成功！\n宽度: {width:F0}mm\n高度: {height:F0}mm\n深度: {depth:F0}mm",
                                "识别结果", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                    }
                    else
                    {
                        this.Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show("无法从选择的对象识别尺寸信息，请选择3D实体或封闭的多段线。",
                                "识别失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
                }
                else
                {
                    editor.WriteMessage("\n识别操作已取消。");
                }
                return true;
            });
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ValidateData())
                {
                    UpdateDataFromUI();
                    CalculateDimensions();
                    
                    // 保存当前配置
                    DataPersistenceService.SaveSidePanelData(PanelData);
                    
                    // 创建板件规格列表
                    var panelSpecs = new List<PanelSpecification>();
                    
                    if (PanelData.CreateLeftPanel)
                    {
                        panelSpecs.Add(new PanelSpecification(
                            PanelData.LeftPanelLength,
                            PanelData.LeftPanelWidth,
                            PanelData.LeftPanelHeight,
                            "左侧板")
                        {
                            Material = PanelData.LeftPanelMaterial,
                            OrderId = "AUTO",
                            CabinetId = "CABINET_01",
                            Remarks = $"盖法: {(PanelData.IsSideCoverTopBottom ? "侧板盖顶底板" : "顶底板盖侧板")}"
                        });
                    }
                    
                    if (PanelData.CreateRightPanel)
                    {
                        panelSpecs.Add(new PanelSpecification(
                            PanelData.RightPanelLength,
                            PanelData.RightPanelWidth,
                            PanelData.RightPanelHeight,
                            "右侧板")
                        {
                            Material = PanelData.RightPanelMaterial,
                            OrderId = "AUTO",
                            CabinetId = "CABINET_01",
                            Remarks = $"盖法: {(PanelData.IsSideCoverTopBottom ? "侧板盖顶底板" : "顶底板盖侧板")}"
                        });
                    }
                    
                    if (panelSpecs.Count == 0)
                    {
                        MessageBox.Show("请至少选择创建一个侧板", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    
                    // 先设置DialogResult为true，然后关闭窗口
                    this.DialogResult = true;
                    
                    // 在窗口关闭后执行创建操作
                    this.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                        if (doc != null)
                        {
                            var editor = doc.Editor;
                            var db = doc.Database;

                            // 获取当前选择的实体
                            var selection = editor.GetSelection();
                            Point3d insertPoint = Point3d.Origin;

                            if (selection.Status == PromptStatus.OK && selection.Value.Count > 0)
                            {
                                using var tr = db.TransactionManager.StartTransaction();
                                var combinedExtents = new Extents3d();
                                bool first = true;

                                foreach (var id in selection.Value.GetObjectIds())
                                {
                                    var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                                    if (entity != null)
                                    {
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
                                }

                                var center = new Point3d(
                                    (combinedExtents.MinPoint.X + combinedExtents.MaxPoint.X) / 2,
                                    (combinedExtents.MinPoint.Y + combinedExtents.MaxPoint.Y) / 2,
                                    combinedExtents.MinPoint.Z
                                );

                                insertPoint = new Point3d(
                                    combinedExtents.MinPoint.X,
                                    center.Y,
                                    center.Z
                                );

                                tr.Commit();
                            }
                            else
                            {
                                // 如果没有选择实体，提示用户选择插入点
                                var pointOptions = new Autodesk.AutoCAD.EditorInput.PromptPointOptions("\n请选择侧板插入点: ");
                                var pointResult = editor.GetPoint(pointOptions);
                                
                                if (pointResult.Status != PromptStatus.OK)
                                {
                                    editor.WriteMessage("\n操作已取消");
                                    return;
                                }
                                insertPoint = pointResult.Value;
                            }

                            // 创建板件
                            var createdPanels = PanelCreationService.CreatePanels(panelSpecs, insertPoint, 100);
                            
                            editor.WriteMessage($"\n成功创建 {createdPanels.Count} 个侧板");
                            editor.WriteMessage($"\n左侧板尺寸: {PanelData.LeftPanelLength}×{PanelData.LeftPanelWidth}×{PanelData.LeftPanelHeight}mm");
                            editor.WriteMessage($"\n右侧板尺寸: {PanelData.RightPanelLength}×{PanelData.RightPanelWidth}×{PanelData.RightPanelHeight}mm");
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (System.Exception ex)
            {
                this.Show();
                MessageBox.Show($"创建板件时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 保存当前输入的数据（即使取消也保存，方便下次使用）
            try
            {
                UpdateDataFromUI();
                DataPersistenceService.SaveSidePanelData(PanelData);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存数据失败: {ex.Message}");
            }
            
            this.DialogResult = false;
            this.Close();
        }

        private void UpdateDataFromUI()
        {
            if (double.TryParse(CabinetDepthTextBox.Text, out double depth))
                PanelData.CabinetDepth = depth;
            
            if (double.TryParse(CabinetHeightTextBox.Text, out double height))
                PanelData.CabinetHeight = height;
            
            if (double.TryParse(SidePanelThicknessTextBox.Text, out double sideThickness))
                PanelData.SidePanelThickness = sideThickness;
            
            if (double.TryParse(TopBottomThicknessTextBox.Text, out double topBottomThickness))
                PanelData.TopBottomThickness = topBottomThickness;

            PanelData.IsSideCoverTopBottom = SideCoverTopBottomRadio.IsChecked == true;
            PanelData.CreateLeftPanel = CreateLeftPanelCheckBox.IsChecked == true;
            PanelData.CreateRightPanel = CreateRightPanelCheckBox.IsChecked == true;
            
            PanelData.LeftPanelMaterial = (LeftPanelMaterialComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "三聚氰胺板";
            PanelData.RightPanelMaterial = (RightPanelMaterialComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "三聚氰胺板";
        }

        private void CalculateDimensions()
        {
            // 根据盖法计算侧板尺寸
            double sidePanelLength, sidePanelWidth, sidePanelHeight;
            
            if (PanelData.IsSideCoverTopBottom)
            {
                // 左右侧板盖顶底板
                sidePanelLength = PanelData.SidePanelThickness;
                sidePanelWidth = PanelData.CabinetDepth;
                sidePanelHeight = PanelData.CabinetHeight;
            }
            else
            {
                // 顶底板盖左右侧板
                sidePanelLength = PanelData.SidePanelThickness;
                sidePanelWidth = PanelData.CabinetDepth;
                sidePanelHeight = PanelData.CabinetHeight - 2 * PanelData.TopBottomThickness;
            }

            PanelData.LeftPanelLength = sidePanelLength;
            PanelData.LeftPanelWidth = sidePanelWidth;
            PanelData.LeftPanelHeight = sidePanelHeight;
            
            PanelData.RightPanelLength = sidePanelLength;
            PanelData.RightPanelWidth = sidePanelWidth;
            PanelData.RightPanelHeight = sidePanelHeight;

            // 更新UI显示
            UpdateDisplayText();
        }

        private void UpdateDisplayText()
        {
            LeftPanelSizeTextBlock.Text = $"{PanelData.LeftPanelLength:F0}×{PanelData.LeftPanelWidth:F0}×{PanelData.LeftPanelHeight:F0}";
            RightPanelSizeTextBlock.Text = $"{PanelData.RightPanelLength:F0}×{PanelData.RightPanelWidth:F0}×{PanelData.RightPanelHeight:F0}";
            
            // 计算总面积（平方米）
            double totalArea = 0;
            if (PanelData.CreateLeftPanel)
            {
                totalArea += (PanelData.LeftPanelWidth * PanelData.LeftPanelHeight) / 1000000;
            }
            if (PanelData.CreateRightPanel)
            {
                totalArea += (PanelData.RightPanelWidth * PanelData.RightPanelHeight) / 1000000;
            }
            
            TotalAreaTextBlock.Text = $"{totalArea:F2} m²";
        }

        private bool ValidateData()
        {
            if (PanelData.CabinetDepth <= 0)
            {
                MessageBox.Show("柜体深度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (PanelData.CabinetHeight <= 0)
            {
                MessageBox.Show("柜体高度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (PanelData.SidePanelThickness <= 0)
            {
                MessageBox.Show("侧板厚度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (PanelData.TopBottomThickness <= 0)
            {
                MessageBox.Show("顶底板厚度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (!PanelData.CreateLeftPanel && !PanelData.CreateRightPanel)
            {
                MessageBox.Show("至少需要创建一个侧板", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            // 验证计算后的板件尺寸
            const double minSize = 10.0; // 最小尺寸10mm
            
            if (PanelData.CreateLeftPanel)
            {
                if (PanelData.LeftPanelLength < minSize || PanelData.LeftPanelWidth < minSize || PanelData.LeftPanelHeight < minSize)
                {
                    MessageBox.Show($"左侧板尺寸过小，所有尺寸必须大于{minSize}mm\n当前尺寸: {PanelData.LeftPanelLength:F1}×{PanelData.LeftPanelWidth:F1}×{PanelData.LeftPanelHeight:F1}mm\n请检查柜体尺寸设置", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            
            if (PanelData.CreateRightPanel)
            {
                if (PanelData.RightPanelLength < minSize || PanelData.RightPanelWidth < minSize || PanelData.RightPanelHeight < minSize)
                {
                    MessageBox.Show($"右侧板尺寸过小，所有尺寸必须大于{minSize}mm\n当前尺寸: {PanelData.RightPanelLength:F1}×{PanelData.RightPanelWidth:F1}×{PanelData.RightPanelHeight:F1}mm\n请检查柜体尺寸设置", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            return true;
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 左右侧板数据模型
    /// </summary>
    public class SidePanelData
    {
        public double CabinetDepth { get; set; }
        public double CabinetHeight { get; set; }
        public double SidePanelThickness { get; set; }
        public double TopBottomThickness { get; set; }
        public bool IsSideCoverTopBottom { get; set; }
        public bool CreateLeftPanel { get; set; }
        public bool CreateRightPanel { get; set; }
        public string LeftPanelMaterial { get; set; } = "";
        public string RightPanelMaterial { get; set; } = "";
        
        // 计算结果
        public double LeftPanelLength { get; set; }
        public double LeftPanelWidth { get; set; }
        public double LeftPanelHeight { get; set; }
        public double RightPanelLength { get; set; }
        public double RightPanelWidth { get; set; }
        public double RightPanelHeight { get; set; }
    }
}