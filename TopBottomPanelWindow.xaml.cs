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
    /// 顶底板参数化建模窗口
    /// </summary>
    public partial class TopBottomPanelWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        private TopBottomPanelData _panelData = null!;

        public TopBottomPanelData PanelData
        {
            get { return _panelData; }
            set
            {
                _panelData = value;
                OnPropertyChanged(nameof(PanelData));
            }
        }

        public TopBottomPanelWindow(Autodesk.AutoCAD.DatabaseServices.Extents3d? frameExtents = null)
        {
            InitializeComponent();
            this.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CreateButton_Click(s, e); e.Handled = true; } };

            // 加载保存的配置数据或使用默认值
            PanelData = DataPersistenceService.LoadTopBottomPanelData() ?? new TopBottomPanelData();
            DataContext = this;

            // 如果提供了框架尺寸，则使用它们
            if (frameExtents.HasValue)
            {
                var extents = frameExtents.Value;
                PanelData.CabinetWidth = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
                PanelData.CabinetDepth = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            }

            // 设置UI值
            CabinetWidthTextBox.Text = PanelData.CabinetWidth.ToString("F0");
            CabinetDepthTextBox.Text = PanelData.CabinetDepth.ToString("F0");
            TopBottomThicknessTextBox.Text = PanelData.TopBottomThickness.ToString();
            SidePanelThicknessTextBox.Text = PanelData.SidePanelThickness.ToString();
            KickbackHeightTextBox.Text = PanelData.KickbackHeight.ToString();

            // 设置盖法选择
            if (PanelData.IsTopBottomCoverSide)
                TopBottomCoverSideRadio.IsChecked = true;
            else
                SideCoverTopBottomRadio.IsChecked = true;

            // 设置创建选项
            CreateTopPanelCheckBox.IsChecked = PanelData.CreateTopPanel;
            CreateBottomPanelCheckBox.IsChecked = PanelData.CreateBottomPanel;
            HasTopKickbackCheckBox.IsChecked = PanelData.HasTopKickback;
            HasBottomKickbackCheckBox.IsChecked = PanelData.HasBottomKickback;

            // 初始计算
            CalculateDimensions();
        }

        private void InitializeData()
        {
            PanelData = new TopBottomPanelData
            {
                CabinetWidth = 800,
                CabinetDepth = 600,
                TopBottomThickness = 18,
                SidePanelThickness = 18,
                IsTopBottomCoverSide = true,
                CreateTopPanel = true,
                CreateBottomPanel = true,
                TopPanelMaterial = "三聚氰胺板",
                BottomPanelMaterial = "三聚氰胺板",
                HasTopKickback = false,
                HasBottomKickback = false,
                KickbackHeight = 100
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
                MessageBox.Show($"计算时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                            CabinetWidthTextBox.Text = width.ToString("F0");
                            CabinetDepthTextBox.Text = depth.ToString("F0");
                            UpdateDataFromUI();
                            CalculateDimensions();
                            MessageBox.Show($"识别成功！\n宽度: {width:F0}mm\n高度: {height:F0}mm\n深度: {depth:F0}mm",
                                "识别结果", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                    }
                    else
                    {
                        this.Dispatcher.Invoke(() =>
                            MessageBox.Show("无法从选择的对象识别尺寸信息，请选择3D实体或封闭的多段线。",
                                "识别失败", MessageBoxButton.OK, MessageBoxImage.Warning));
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
                UpdateDataFromUI();
                CalculateDimensions();
                
                // 验证数据
                if (!ValidateData())
                {
                    return;
                }

                // 保存当前配置
                DataPersistenceService.SaveTopBottomPanelData(PanelData);
                
                // 创建板件规格列表
                var panelSpecs = new List<PanelSpecification>();
                
                if (PanelData.CreateTopPanel)
                {
                    panelSpecs.Add(new PanelSpecification(
                        PanelData.TopPanelLength,
                        PanelData.TopPanelWidth,
                        PanelData.TopPanelHeight,
                        "顶板")
                    {
                        Material = PanelData.TopPanelMaterial,
                        OrderId = "AUTO",
                        CabinetId = "CABINET_01",
                        Remarks = $"盖法: {(PanelData.IsTopBottomCoverSide ? "顶底板盖侧板" : "侧板盖顶底板")}"
                    });
                }
                
                if (PanelData.CreateBottomPanel)
                {
                    panelSpecs.Add(new PanelSpecification(
                        PanelData.BottomPanelLength,
                        PanelData.BottomPanelWidth,
                        PanelData.BottomPanelHeight,
                        "底板")
                    {
                        Material = PanelData.BottomPanelMaterial,
                        OrderId = "AUTO",
                        CabinetId = "CABINET_01",
                        Remarks = $"盖法: {(PanelData.IsTopBottomCoverSide ? "顶底板盖侧板" : "侧板盖顶底板")}"
                    });
                }
                
                if (panelSpecs.Count == 0)
                {
                    MessageBox.Show("请至少选择创建一个顶底板", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                                center.X,
                                center.Y,
                                combinedExtents.MinPoint.Z
                            );

                            tr.Commit();
                        }
                        else
                        {
                            // 如果没有选择实体，提示用户选择插入点
                            var pointOptions = new Autodesk.AutoCAD.EditorInput.PromptPointOptions("\n请选择顶底板插入点: ");
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

                        editor.WriteMessage($"\n成功创建 {createdPanels.Count} 个顶底板");
                        editor.WriteMessage($"\n顶板尺寸: {PanelData.TopPanelLength}×{PanelData.TopPanelWidth}×{PanelData.TopPanelHeight}mm");
                        editor.WriteMessage($"\n底板尺寸: {PanelData.BottomPanelLength}×{PanelData.BottomPanelWidth}×{PanelData.BottomPanelHeight}mm");
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
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
                DataPersistenceService.SaveTopBottomPanelData(PanelData);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存数据失败: {ex.Message}");
            }
            
            DialogResult = false;
            Close();
        }

        private void UpdateDataFromUI()
        {
            if (double.TryParse(CabinetWidthTextBox.Text, out double width))
                PanelData.CabinetWidth = width;
            
            if (double.TryParse(CabinetDepthTextBox.Text, out double depth))
                PanelData.CabinetDepth = depth;
            
            if (double.TryParse(TopBottomThicknessTextBox.Text, out double topBottomThickness))
                PanelData.TopBottomThickness = topBottomThickness;
            
            if (double.TryParse(SidePanelThicknessTextBox.Text, out double sideThickness))
                PanelData.SidePanelThickness = sideThickness;
            
            if (double.TryParse(KickbackHeightTextBox.Text, out double kickbackHeight))
                PanelData.KickbackHeight = kickbackHeight;

            PanelData.IsTopBottomCoverSide = TopBottomCoverSideRadio.IsChecked == true;
            PanelData.CreateTopPanel = CreateTopPanelCheckBox.IsChecked == true;
            PanelData.CreateBottomPanel = CreateBottomPanelCheckBox.IsChecked == true;
            PanelData.HasTopKickback = HasTopKickbackCheckBox.IsChecked == true;
            PanelData.HasBottomKickback = HasBottomKickbackCheckBox.IsChecked == true;
            
            PanelData.TopPanelMaterial = (TopPanelMaterialComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "三聚氰胺板";
            PanelData.BottomPanelMaterial = (BottomPanelMaterialComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "三聚氰胺板";
        }

        private void CalculateDimensions()
        {
            // 根据盖法计算顶底板尺寸
            double topBottomLength, topBottomWidth, topBottomHeight;
            
            if (PanelData.IsTopBottomCoverSide)
            {
                // 顶底板盖左右侧板
                topBottomLength = PanelData.CabinetWidth;
                topBottomWidth = PanelData.CabinetDepth;
                topBottomHeight = PanelData.TopBottomThickness;
            }
            else
            {
                // 左右侧板盖顶底板
                topBottomLength = PanelData.CabinetWidth - 2 * PanelData.SidePanelThickness;
                topBottomWidth = PanelData.CabinetDepth;
                topBottomHeight = PanelData.TopBottomThickness;
            }

            // 考虑踢脚线的影响
            if (PanelData.HasTopKickback || PanelData.HasBottomKickback)
            {
                // 如果有踢脚线，深度需要减去踢脚线高度，但要确保最小尺寸
                topBottomWidth = Math.Max(topBottomWidth - PanelData.KickbackHeight, 10.0); // 最小保持10mm
            }

            PanelData.TopPanelLength = topBottomLength;
            PanelData.TopPanelWidth = topBottomWidth;
            PanelData.TopPanelHeight = topBottomHeight;
            
            PanelData.BottomPanelLength = topBottomLength;
            PanelData.BottomPanelWidth = topBottomWidth;
            PanelData.BottomPanelHeight = topBottomHeight;

            // 更新UI显示
            UpdateDisplayText();
        }

        private void UpdateDisplayText()
        {
            TopPanelSizeTextBlock.Text = $"{PanelData.TopPanelLength:F0}×{PanelData.TopPanelWidth:F0}×{PanelData.TopPanelHeight:F0}";
            BottomPanelSizeTextBlock.Text = $"{PanelData.BottomPanelLength:F0}×{PanelData.BottomPanelWidth:F0}×{PanelData.BottomPanelHeight:F0}";
            
            // 计算总面积（平方米）
            double totalArea = 0;
            if (PanelData.CreateTopPanel)
            {
                totalArea += (PanelData.TopPanelLength * PanelData.TopPanelWidth) / 1000000;
            }
            if (PanelData.CreateBottomPanel)
            {
                totalArea += (PanelData.BottomPanelLength * PanelData.BottomPanelWidth) / 1000000;
            }
            
            TotalAreaTextBlock.Text = $"{totalArea:F2} m²";
        }

        private bool ValidateData()
        {
            if (PanelData.CabinetWidth <= 0)
            {
                MessageBox.Show("柜体宽度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (PanelData.CabinetDepth <= 0)
            {
                MessageBox.Show("柜体深度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (PanelData.TopBottomThickness <= 0)
            {
                MessageBox.Show("顶底板厚度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (PanelData.SidePanelThickness <= 0)
            {
                MessageBox.Show("侧板厚度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (!PanelData.CreateTopPanel && !PanelData.CreateBottomPanel)
            {
                MessageBox.Show("至少需要创建一个顶底板", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            // 验证计算后的板件尺寸
            const double minSize = 10.0; // 最小尺寸10mm
            
            if (PanelData.CreateTopPanel)
            {
                if (PanelData.TopPanelLength < minSize || PanelData.TopPanelWidth < minSize || PanelData.TopPanelHeight < minSize)
                {
                    MessageBox.Show($"顶板尺寸过小，所有尺寸必须大于{minSize}mm\n当前尺寸: {PanelData.TopPanelLength:F1}×{PanelData.TopPanelWidth:F1}×{PanelData.TopPanelHeight:F1}mm\n请检查柜体尺寸和踢脚线设置", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            
            if (PanelData.CreateBottomPanel)
            {
                if (PanelData.BottomPanelLength < minSize || PanelData.BottomPanelWidth < minSize || PanelData.BottomPanelHeight < minSize)
                {
                    MessageBox.Show($"底板尺寸过小，所有尺寸必须大于{minSize}mm\n当前尺寸: {PanelData.BottomPanelLength:F1}×{PanelData.BottomPanelWidth:F1}×{PanelData.BottomPanelHeight:F1}mm\n请检查柜体尺寸和踢脚线设置", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    /// 顶底板数据模型
    /// </summary>
    public class TopBottomPanelData
    {
        public double CabinetWidth { get; set; }
        public double CabinetDepth { get; set; }
        public double TopBottomThickness { get; set; }
        public double SidePanelThickness { get; set; }
        public bool IsTopBottomCoverSide { get; set; }
        public bool CreateTopPanel { get; set; }
        public bool CreateBottomPanel { get; set; }
        public string TopPanelMaterial { get; set; } = "";
        public string BottomPanelMaterial { get; set; } = "";
        public bool HasTopKickback { get; set; }
        public bool HasBottomKickback { get; set; }
        public double KickbackHeight { get; set; }
        
        // 计算结果
        public double TopPanelLength { get; set; }
        public double TopPanelWidth { get; set; }
        public double TopPanelHeight { get; set; }
        public double BottomPanelLength { get; set; }
        public double BottomPanelWidth { get; set; }
        public double BottomPanelHeight { get; set; }
    }
}