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
    /// 层板/竖隔板参数化建模窗口
    /// </summary>
    public partial class ShelfPartitionWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        private ShelfPartitionData _panelData = null!;

        public ShelfPartitionData PanelData
        {
            get { return _panelData; }
            set
            {
                _panelData = value;
                OnPropertyChanged(nameof(PanelData));
            }
        }

        public ShelfPartitionWindow(Autodesk.AutoCAD.DatabaseServices.Extents3d? frameExtents = null)
        {
            InitializeComponent();
            this.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CreateButton_Click(s, e); e.Handled = true; } };

            // 加载保存的配置数据或使用默认值
            PanelData = DataPersistenceService.LoadShelfPartitionData() ?? new ShelfPartitionData();
            DataContext = this;

            // 如果提供了框架尺寸，则使用它们
            if (frameExtents.HasValue)
            {
                var extents = frameExtents.Value;
                PanelData.CabinetWidth = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
                PanelData.CabinetDepth = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
                PanelData.CabinetHeight = Math.Abs(extents.MaxPoint.Z - extents.MinPoint.Z);
            }

            // 设置UI值
            CabinetWidthTextBox.Text = PanelData.CabinetWidth.ToString("F0");
            CabinetDepthTextBox.Text = PanelData.CabinetDepth.ToString("F0");
            CabinetHeightTextBox.Text = PanelData.CabinetHeight.ToString("F0");
            PanelThicknessTextBox.Text = PanelData.PanelThickness.ToString();
            SidePanelThicknessTextBox.Text = PanelData.SidePanelThickness.ToString();
            ShelfCountTextBox.Text = PanelData.ShelfCount.ToString();
            PartitionCountTextBox.Text = PanelData.PartitionCount.ToString();
            ShelfSpacingTextBox.Text = PanelData.ShelfSpacing.ToString();
            PartitionSpacingTextBox.Text = PanelData.PartitionSpacing.ToString();
            EdgeBandingThicknessTextBox.Text = PanelData.EdgeBandingThickness.ToString();
            ClearanceTextBox.Text = PanelData.Clearance.ToString();

            // 设置材质选择
            ShelfMaterialComboBox.Text = PanelData.ShelfMaterial;
            PartitionMaterialComboBox.Text = PanelData.PartitionMaterial;

            // 设置创建选项
            CreateShelvesCheckBox.IsChecked = PanelData.CreateShelves;
            CreatePartitionsCheckBox.IsChecked = PanelData.CreatePartitions;
            AutoCalculateSpacingCheckBox.IsChecked = PanelData.AutoCalculateSpacing;
            IncludeEdgeBandingCheckBox.IsChecked = PanelData.IncludeEdgeBanding;

            // 设置ComboBox选择
            ShelfTypeComboBox.Text = PanelData.ShelfType;
            PartitionTypeComboBox.Text = PanelData.PartitionType;

            // 初始计算
            CalculateDimensions();
        }

        private void InitializeData()
        {
            PanelData = new ShelfPartitionData
            {
                CabinetWidth = 800,
                CabinetDepth = 600,
                CabinetHeight = 2400,
                PanelThickness = 18,
                SidePanelThickness = 18,
                CreateShelves = true,
                ShelfCount = 3,
                ShelfType = "固定层板",
                ShelfSpacing = 400,
                ShelfMaterial = "三聚氰胺板",
                CreatePartitions = false,
                PartitionCount = 1,
                PartitionType = "通长竖隔板",
                PartitionSpacing = 400,
                PartitionMaterial = "三聚氰胺板",
                AutoCalculateSpacing = true,
                IncludeEdgeBanding = true,
                EdgeBandingThickness = 0.5,
                Clearance = 2
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
                            CabinetHeightTextBox.Text = height.ToString("F0");
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
                DataPersistenceService.SaveShelfPartitionData(PanelData);
                
                // 创建板件规格列表
                var panelSpecs = new List<PanelSpecification>();
                
                // 添加层板
                for (int i = 0; i < PanelData.ShelfCount; i++)
                {
                    panelSpecs.Add(new PanelSpecification(
                        PanelData.ShelfLength,
                        PanelData.ShelfWidth,
                        PanelData.ShelfHeight,
                        $"层板{i + 1}")
                    {
                        Material = PanelData.ShelfMaterial,
                        OrderId = "AUTO",
                        CabinetId = "CABINET_01",
                        Remarks = $"层板 - 间距: {PanelData.ShelfSpacing}mm"
                    });
                }
                
                // 添加竖隔板
                for (int i = 0; i < PanelData.PartitionCount; i++)
                {
                    panelSpecs.Add(new PanelSpecification(
                        PanelData.PartitionLength,
                        PanelData.PartitionWidth,
                        PanelData.PartitionHeight,
                        $"竖隔板{i + 1}")
                    {
                        Material = PanelData.PartitionMaterial,
                        OrderId = "AUTO",
                        CabinetId = "CABINET_01",
                        Remarks = $"竖隔板 - 间距: {PanelData.PartitionSpacing}mm"
                    });
                }
                
                if (panelSpecs.Count == 0)
                {
                    MessageBox.Show("请至少设置一个层板或竖隔板", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                // 先设置DialogResult为true，然后关闭窗口
                this.DialogResult = true;
                
                // 在窗口关闭后执行创建操作
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    if (doc == null) return;

                    var editor = doc.Editor;
                    Point3d insertionPoint;

                    // 检查是否通过选择集启动
                    var selectionSet = editor.GetSelection().Value;
                    if (selectionSet != null && selectionSet.Count > 0)
                    {
                        var combinedExtents = new Extents3d();
                        using var tr = doc.Database.TransactionManager.StartTransaction();
                        foreach (SelectedObject selObj in selectionSet)
                        {
                            if (selObj != null)
                            {
                                var ent = tr.GetObject(selObj.ObjectId, OpenMode.ForRead) as Entity;
                                if (ent != null && ent.Bounds.HasValue)
                                {
                                    combinedExtents.AddExtents(ent.GeometricExtents);
                                }
                            }
                        }
                        tr.Commit();

                        // 计算中心点作为插入点
                        insertionPoint = combinedExtents.MinPoint + (combinedExtents.MaxPoint - combinedExtents.MinPoint) / 2.0;
                        editor.WriteMessage("\n已自动识别框架并计算中心插入点。");
                    }
                    else
                    {
                        // 提示用户选择插入点
                        var pointOptions = new PromptPointOptions("\n请选择层板/竖隔板插入点: ");
                        var pointResult = editor.GetPoint(pointOptions);

                        if (pointResult.Status != PromptStatus.OK)
                        {
                            editor.WriteMessage("\n操作已取消。");
                            return;
                        }
                        insertionPoint = pointResult.Value;
                    }

                    // 创建板件
                    var createdPanels = PanelCreationService.CreatePanels(panelSpecs, insertionPoint, 100);

                    editor.WriteMessage($"\n成功创建 {createdPanels.Count} 个板件");
                    if (PanelData.CreateShelves)
                    {
                        editor.WriteMessage($"\n层板数量: {PanelData.ShelfCount}, 尺寸: {PanelData.ShelfLength}×{PanelData.ShelfWidth}×{PanelData.ShelfHeight}mm");
                    }
                    if (PanelData.CreatePartitions)
                    {
                        editor.WriteMessage($"\n竖隔板数量: {PanelData.PartitionCount}, 尺寸: {PanelData.PartitionLength}×{PanelData.PartitionWidth}×{PanelData.PartitionHeight}mm");
                    }

                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"创建板件时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 保存当前输入的数据（即使取消也保存，方便下次使用）
            try
            {
                UpdateDataFromUI();
                DataPersistenceService.SaveShelfPartitionData(PanelData);
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
            if (double.TryParse(CabinetWidthTextBox.Text, out double width))
                PanelData.CabinetWidth = width;
            
            if (double.TryParse(CabinetDepthTextBox.Text, out double depth))
                PanelData.CabinetDepth = depth;
            
            if (double.TryParse(CabinetHeightTextBox.Text, out double height))
                PanelData.CabinetHeight = height;
            
            if (double.TryParse(PanelThicknessTextBox.Text, out double panelThickness))
                PanelData.PanelThickness = panelThickness;
            
            if (double.TryParse(SidePanelThicknessTextBox.Text, out double sideThickness))
                PanelData.SidePanelThickness = sideThickness;
            
            if (int.TryParse(ShelfCountTextBox.Text, out int shelfCount))
                PanelData.ShelfCount = shelfCount;
            
            if (double.TryParse(ShelfSpacingTextBox.Text, out double shelfSpacing))
                PanelData.ShelfSpacing = shelfSpacing;
            
            if (int.TryParse(PartitionCountTextBox.Text, out int partitionCount))
                PanelData.PartitionCount = partitionCount;
            
            if (double.TryParse(PartitionSpacingTextBox.Text, out double partitionSpacing))
                PanelData.PartitionSpacing = partitionSpacing;
            
            if (double.TryParse(EdgeBandingThicknessTextBox.Text, out double edgeBandingThickness))
                PanelData.EdgeBandingThickness = edgeBandingThickness;
            
            if (double.TryParse(ClearanceTextBox.Text, out double clearance))
                PanelData.Clearance = clearance;

            PanelData.CreateShelves = CreateShelvesCheckBox.IsChecked == true;
            PanelData.CreatePartitions = CreatePartitionsCheckBox.IsChecked == true;
            PanelData.AutoCalculateSpacing = AutoCalculateSpacingCheckBox.IsChecked == true;
            PanelData.IncludeEdgeBanding = IncludeEdgeBandingCheckBox.IsChecked == true;
            
            PanelData.ShelfType = (ShelfTypeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "固定层板";
            PanelData.ShelfMaterial = (ShelfMaterialComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "三聚氰胺板";
            PanelData.PartitionType = (PartitionTypeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "通长竖隔板";
            PanelData.PartitionMaterial = (PartitionMaterialComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "三聚氰胺板";
        }

        private void CalculateDimensions()
        {
            // 计算层板尺寸
            if (PanelData.CreateShelves)
            {
                // 层板长度 = 柜体宽度 - 2 * 侧板厚度 - 预留间隙
                PanelData.ShelfLength = PanelData.CabinetWidth - 2 * PanelData.SidePanelThickness - PanelData.Clearance;
                PanelData.ShelfWidth = PanelData.CabinetDepth;
                PanelData.ShelfHeight = PanelData.PanelThickness;
                
                // 如果自动计算间距
                if (PanelData.AutoCalculateSpacing && PanelData.ShelfCount > 0)
                {
                    // 可用高度 = 柜体高度 - 层板总厚度
                    double availableHeight = PanelData.CabinetHeight - PanelData.ShelfCount * PanelData.PanelThickness;
                    PanelData.ShelfSpacing = availableHeight / (PanelData.ShelfCount + 1);
                }
            }
            else
            {
                PanelData.ShelfLength = 0;
                PanelData.ShelfWidth = 0;
                PanelData.ShelfHeight = 0;
            }

            // 计算竖隔板尺寸
            if (PanelData.CreatePartitions)
            {
                PanelData.PartitionLength = PanelData.CabinetDepth;
                
                if (PanelData.PartitionType == "通长竖隔板")
                {
                    // 通长竖隔板高度 = 柜体高度
                    PanelData.PartitionHeight = PanelData.CabinetHeight;
                }
                else
                {
                    // 分段竖隔板高度 = 层板间距 - 板件厚度
                    PanelData.PartitionHeight = PanelData.ShelfSpacing - PanelData.PanelThickness;
                }
                
                PanelData.PartitionWidth = PanelData.PanelThickness;
                
                // 如果自动计算间距
                if (PanelData.AutoCalculateSpacing && PanelData.PartitionCount > 0)
                {
                    // 可用宽度 = 柜体宽度 - 2 * 侧板厚度 - 竖隔板总厚度
                    double availableWidth = PanelData.CabinetWidth - 2 * PanelData.SidePanelThickness - PanelData.PartitionCount * PanelData.PanelThickness;
                    PanelData.PartitionSpacing = availableWidth / (PanelData.PartitionCount + 1);
                }
            }
            else
            {
                PanelData.PartitionLength = 0;
                PanelData.PartitionWidth = 0;
                PanelData.PartitionHeight = 0;
            }

            // 更新UI显示
            UpdateDisplayText();
        }

        private void UpdateDisplayText()
        {
            // 更新尺寸显示
            ShelfSizeTextBlock.Text = PanelData.CreateShelves ? 
                $"{PanelData.ShelfLength:F0}×{PanelData.ShelfWidth:F0}×{PanelData.ShelfHeight:F0}" : "未创建";
            
            PartitionSizeTextBlock.Text = PanelData.CreatePartitions ? 
                $"{PanelData.PartitionLength:F0}×{PanelData.PartitionWidth:F0}×{PanelData.PartitionHeight:F0}" : "未创建";
            
            // 更新数量显示
            ShelfCountResultTextBlock.Text = PanelData.CreateShelves ? PanelData.ShelfCount.ToString() : "0";
            PartitionCountResultTextBlock.Text = PanelData.CreatePartitions ? PanelData.PartitionCount.ToString() : "0";
            
            // 计算面积（平方米）
            double shelfTotalArea = 0;
            double partitionTotalArea = 0;
            
            if (PanelData.CreateShelves)
            {
                shelfTotalArea = (PanelData.ShelfLength * PanelData.ShelfWidth * PanelData.ShelfCount) / 1000000;
            }
            
            if (PanelData.CreatePartitions)
            {
                partitionTotalArea = (PanelData.PartitionLength * PanelData.PartitionHeight * PanelData.PartitionCount) / 1000000;
            }
            
            double totalArea = shelfTotalArea + partitionTotalArea;
            
            ShelfTotalAreaTextBlock.Text = $"{shelfTotalArea:F2} m²";
            PartitionTotalAreaTextBlock.Text = $"{partitionTotalArea:F2} m²";
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
            
            if (PanelData.CabinetHeight <= 0)
            {
                MessageBox.Show("柜体高度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (PanelData.PanelThickness <= 0)
            {
                MessageBox.Show("板件厚度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (!PanelData.CreateShelves && !PanelData.CreatePartitions)
            {
                MessageBox.Show("至少需要创建层板或竖隔板", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (PanelData.CreateShelves && PanelData.ShelfCount <= 0)
            {
                MessageBox.Show("层板数量必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (PanelData.CreatePartitions && PanelData.PartitionCount <= 0)
            {
                MessageBox.Show("竖隔板数量必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 层板/竖隔板数据模型
    /// </summary>
    public class ShelfPartitionData
    {
        public double CabinetWidth { get; set; }
        public double CabinetDepth { get; set; }
        public double CabinetHeight { get; set; }
        public double PanelThickness { get; set; }
        public double SidePanelThickness { get; set; }
        
        // 层板配置
        public bool CreateShelves { get; set; }
        public int ShelfCount { get; set; }
        public string ShelfType { get; set; } = "";
        public double ShelfSpacing { get; set; }
        public string ShelfMaterial { get; set; } = "";
        
        // 竖隔板配置
        public bool CreatePartitions { get; set; }
        public int PartitionCount { get; set; }
        public string PartitionType { get; set; } = "";
        public double PartitionSpacing { get; set; }
        public string PartitionMaterial { get; set; } = "";
        
        // 高级选项
        public bool AutoCalculateSpacing { get; set; }
        public bool IncludeEdgeBanding { get; set; }
        public double EdgeBandingThickness { get; set; }
        public double Clearance { get; set; }
        
        // 计算结果 - 层板
        public double ShelfLength { get; set; }
        public double ShelfWidth { get; set; }
        public double ShelfHeight { get; set; }
        
        // 计算结果 - 竖隔板
        public double PartitionLength { get; set; }
        public double PartitionWidth { get; set; }
        public double PartitionHeight { get; set; }
    }
}