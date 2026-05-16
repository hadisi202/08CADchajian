using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

namespace FurniturePlugin
{
    /// <summary>
    /// 背板参数化建模窗口
    /// </summary>
    public partial class BackPanelWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        private BackPanelData _panelData = null!;

        public BackPanelData PanelData
        {
            get { return _panelData; }
            set
            {
                _panelData = value;
                OnPropertyChanged(nameof(PanelData));
            }
        }

        public BackPanelWindow(Extents3d? frameExtents = null)
        {
            InitializeComponent();
            this.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CreateButton_Click(s, e); e.Handled = true; } };

            // 加载保存的配置数据或使用默认值
            PanelData = DataPersistenceService.LoadBackPanelData() ?? new BackPanelData();
            DataContext = this;

            // 如果提供了框架尺寸，则使用它们
            if (frameExtents.HasValue)
            {
                var extents = frameExtents.Value;
                PanelData.CabinetWidth = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
                PanelData.CabinetHeight = Math.Abs(extents.MaxPoint.Z - extents.MinPoint.Z);
                PanelData.BackPanelDepth = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            }

            // 设置UI值
            CabinetWidthTextBox.Text = PanelData.CabinetWidth.ToString("F0");
            CabinetHeightTextBox.Text = PanelData.CabinetHeight.ToString("F0");
            BackPanelThicknessTextBox.Text = PanelData.BackPanelThickness.ToString();
            SidePanelThicknessTextBox.Text = PanelData.SidePanelThickness.ToString();
            TopBottomThicknessTextBox.Text = PanelData.TopBottomThickness.ToString();
            BackPanelDepthTextBox.Text = PanelData.BackPanelDepth.ToString("F0");

            // 设置背板类型选择
            if (PanelData.BackPanelType == "整块背板")
                FullBackPanelRadio.IsChecked = true;
            else if (PanelData.BackPanelType == "分块背板")
                SplitBackPanelRadio.IsChecked = true;
            else if (PanelData.BackPanelType == "无背板")
                NoBackPanelRadio.IsChecked = true;

            // 设置安装方式选择
            if (PanelData.InstallationType == "内嵌式")
                EmbeddedRadio.IsChecked = true;
            else if (PanelData.InstallationType == "外盖式")
                ExternalRadio.IsChecked = true;

            // 设置材质选择
            BackPanelMaterialComboBox.Text = PanelData.BackPanelMaterial;

            // 设置分块配置
            SplitCountTextBox.Text = PanelData.SplitCount.ToString();
            SplitDirectionComboBox.Text = PanelData.SplitDirection;

            // 设置高级选项
            IncludeSlotCheckBox.IsChecked = PanelData.IncludeSlot;
            SlotDepthTextBox.Text = PanelData.SlotDepth.ToString();
            ClearanceTextBox.Text = PanelData.Clearance.ToString();
            SplitGapTextBox.Text = PanelData.SplitGap.ToString();

            SetupEventHandlers();
            CalculateDimensions();
        }

        private void InitializeData()
        {
            PanelData = new BackPanelData
            {
                CabinetWidth = 800,
                CabinetHeight = 2400,
                BackPanelThickness = 5,
                SidePanelThickness = 18,
                TopBottomThickness = 18,
                BackPanelDepth = 600,
                BackPanelType = "整块背板",
                InstallationType = "内嵌式",
                BackPanelMaterial = "密度板",
                SplitCount = 2,
                SplitDirection = "水平分块",
                IncludeSlot = true,
                SlotDepth = 10,
                Clearance = 2,
                SplitGap = 5
            };

            DataContext = this;
        }

        private void SetupEventHandlers()
        {
            // 背板类型变化事件
            FullBackPanelRadio.Checked += BackPanelType_Changed;
            SplitBackPanelRadio.Checked += BackPanelType_Changed;
            NoBackPanelRadio.Checked += BackPanelType_Changed;
            
            // 安装方式变化事件
            EmbeddedRadio.Checked += InstallationType_Changed;
            ExternalRadio.Checked += InstallationType_Changed;
        }

        private void BackPanelType_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radio && radio.IsChecked == true)
            {
                PanelData.BackPanelType = radio.Content.ToString();
                
                // 根据背板类型启用/禁用相关控件
                bool isSplitPanel = PanelData.BackPanelType == "分块背板";
                bool hasBackPanel = PanelData.BackPanelType != "无背板";
                
                SplitCountTextBox.IsEnabled = isSplitPanel;
                SplitDirectionComboBox.IsEnabled = isSplitPanel;
                SplitGapTextBox.IsEnabled = isSplitPanel;
                
                BackPanelMaterialComboBox.IsEnabled = hasBackPanel;
                IncludeSlotCheckBox.IsEnabled = hasBackPanel;
                SlotDepthTextBox.IsEnabled = hasBackPanel && IncludeSlotCheckBox.IsChecked == true;
                
                CalculateDimensions();
            }
        }

        private void InstallationType_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radio && radio.IsChecked == true)
            {
                PanelData.InstallationType = radio.Content.ToString();
                CalculateDimensions();
            }
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
                            BackPanelDepthTextBox.Text = depth.ToString("F0");
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
                DataPersistenceService.SaveBackPanelData(PanelData);
                
                // 创建板件规格列表
                var panelSpecs = new List<PanelSpecification>();
                
                if (PanelData.BackPanelType != "无背板")
                {
                    if (PanelData.BackPanelType == "整块背板")
                    {
                        panelSpecs.Add(new PanelSpecification(
                            PanelData.BackPanelWidth,
                            PanelData.BackPanelHeight,
                            PanelData.BackPanelThickness,
                            "背板")
                        {
                            Material = PanelData.BackPanelMaterial,
                            OrderId = "AUTO",
                            CabinetId = "CABINET_01",
                            Remarks = $"安装方式: {PanelData.InstallationType}, 背板槽: {(PanelData.IncludeSlot ? "有" : "无")}"
                        });
                    }
                    else if (PanelData.BackPanelType == "分块背板")
                    {
                        for (int i = 0; i < PanelData.PanelCount; i++)
                        {
                            panelSpecs.Add(new PanelSpecification(
                                PanelData.SinglePanelWidth,
                                PanelData.SinglePanelHeight,
                                PanelData.BackPanelThickness,
                                $"背板{i + 1}")
                            {
                                Material = PanelData.BackPanelMaterial,
                                OrderId = "AUTO",
                                CabinetId = "CABINET_01",
                                Remarks = $"分块方向: {PanelData.SplitDirection}, 安装方式: {PanelData.InstallationType}"
                            });
                        }
                    }
                }
                
                if (panelSpecs.Count == 0)
                {
                    MessageBox.Show("当前配置无需创建背板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
                        var pointOptions = new PromptPointOptions("\n请选择背板插入点: ");
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

                    editor.WriteMessage($"\n成功创建 {createdPanels.Count} 个背板");
                    editor.WriteMessage($"\n背板类型: {PanelData.BackPanelType}");
                    editor.WriteMessage($"\n背板尺寸: {PanelData.BackPanelWidth}×{PanelData.BackPanelHeight}×{PanelData.BackPanelThickness}mm");
                    if (PanelData.BackPanelType == "分块背板")
                    {
                        editor.WriteMessage($"\n单块尺寸: {PanelData.SinglePanelWidth}×{PanelData.SinglePanelHeight}mm");
                        editor.WriteMessage($"\n分块数量: {PanelData.PanelCount}");
                    }

                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (System.Exception ex)
            {
                this.Show();
                MessageBox.Show($"创建背板时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 保存当前输入的数据（即使取消也保存，方便下次使用）
            try
            {
                UpdateDataFromUI();
                DataPersistenceService.SaveBackPanelData(PanelData);
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
            
            if (double.TryParse(CabinetHeightTextBox.Text, out double height))
                PanelData.CabinetHeight = height;
            
            if (double.TryParse(BackPanelThicknessTextBox.Text, out double backThickness))
                PanelData.BackPanelThickness = backThickness;
            
            if (double.TryParse(SidePanelThicknessTextBox.Text, out double sideThickness))
                PanelData.SidePanelThickness = sideThickness;
            
            if (double.TryParse(TopBottomThicknessTextBox.Text, out double topBottomThickness))
                PanelData.TopBottomThickness = topBottomThickness;
            
            if (double.TryParse(BackPanelDepthTextBox.Text, out double depth))
                PanelData.BackPanelDepth = depth;
            
            if (int.TryParse(SplitCountTextBox.Text, out int splitCount))
                PanelData.SplitCount = splitCount;
            
            if (double.TryParse(SlotDepthTextBox.Text, out double slotDepth))
                PanelData.SlotDepth = slotDepth;
            
            if (double.TryParse(ClearanceTextBox.Text, out double clearance))
                PanelData.Clearance = clearance;
            
            if (double.TryParse(SplitGapTextBox.Text, out double splitGap))
                PanelData.SplitGap = splitGap;

            PanelData.BackPanelMaterial = (BackPanelMaterialComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "密度板";
            PanelData.SplitDirection = (SplitDirectionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "水平分块";
            PanelData.IncludeSlot = IncludeSlotCheckBox.IsChecked == true;
        }

        private void CalculateDimensions()
        {
            if (PanelData.BackPanelType == "无背板")
            {
                // 无背板情况
                PanelData.BackPanelWidth = 0;
                PanelData.BackPanelHeight = 0;
                PanelData.SinglePanelWidth = 0;
                PanelData.SinglePanelHeight = 0;
                PanelData.PanelCount = 0;
            }
            else
            {
                // 根据安装方式计算背板尺寸
                if (PanelData.InstallationType == "内嵌式")
                {
                    // 内嵌式：背板尺寸需要减去侧板和顶底板厚度
                    PanelData.BackPanelWidth = PanelData.CabinetWidth - 2 * PanelData.SidePanelThickness - PanelData.Clearance;
                    PanelData.BackPanelHeight = PanelData.CabinetHeight - 2 * PanelData.TopBottomThickness - PanelData.Clearance;
                }
                else
                {
                    // 外盖式：背板尺寸等于柜体尺寸
                    PanelData.BackPanelWidth = PanelData.CabinetWidth;
                    PanelData.BackPanelHeight = PanelData.CabinetHeight;
                }

                // 根据背板类型计算单块尺寸和数量
                if (PanelData.BackPanelType == "整块背板")
                {
                    PanelData.SinglePanelWidth = PanelData.BackPanelWidth;
                    PanelData.SinglePanelHeight = PanelData.BackPanelHeight;
                    PanelData.PanelCount = 1;
                }
                else if (PanelData.BackPanelType == "分块背板")
                {
                    PanelData.PanelCount = PanelData.SplitCount;
                    
                    if (PanelData.SplitDirection == "水平分块")
                    {
                        // 水平分块：宽度不变，高度平分
                        PanelData.SinglePanelWidth = PanelData.BackPanelWidth;
                        PanelData.SinglePanelHeight = (PanelData.BackPanelHeight - (PanelData.SplitCount - 1) * PanelData.SplitGap) / PanelData.SplitCount;
                    }
                    else
                    {
                        // 垂直分块：高度不变，宽度平分
                        PanelData.SinglePanelWidth = (PanelData.BackPanelWidth - (PanelData.SplitCount - 1) * PanelData.SplitGap) / PanelData.SplitCount;
                        PanelData.SinglePanelHeight = PanelData.BackPanelHeight;
                    }
                }
            }

            // 更新UI显示
            UpdateDisplayText();
        }

        private void UpdateDisplayText()
        {
            if (PanelData.BackPanelType == "无背板")
            {
                BackPanelSizeTextBlock.Text = "无背板";
                BackPanelCountTextBlock.Text = "0";
                SinglePanelSizeTextBlock.Text = "无";
                TotalAreaTextBlock.Text = "0.00 m²";
                InstallationTypeTextBlock.Text = "无";
                SlotInfoTextBlock.Text = "无";
            }
            else
            {
                // 更新尺寸显示
                BackPanelSizeTextBlock.Text = $"{PanelData.BackPanelWidth:F0}×{PanelData.BackPanelHeight:F0}×{PanelData.BackPanelThickness:F0}";
                BackPanelCountTextBlock.Text = PanelData.PanelCount.ToString();
                SinglePanelSizeTextBlock.Text = $"{PanelData.SinglePanelWidth:F0}×{PanelData.SinglePanelHeight:F0}×{PanelData.BackPanelThickness:F0}";
                
                // 计算总面积（平方米）
                double totalArea = (PanelData.SinglePanelWidth * PanelData.SinglePanelHeight * PanelData.PanelCount) / 1000000;
                TotalAreaTextBlock.Text = $"{totalArea:F2} m²";
                
                // 更新安装方式和背板槽信息
                InstallationTypeTextBlock.Text = PanelData.InstallationType;
                
                if (PanelData.IncludeSlot)
                {
                    SlotInfoTextBlock.Text = $"深度{PanelData.SlotDepth:F0}mm";
                }
                else
                {
                    SlotInfoTextBlock.Text = "无背板槽";
                }
            }
        }

        private bool ValidateData()
        {
            if (PanelData.BackPanelType != "无背板")
            {
                if (PanelData.CabinetWidth <= 0)
                {
                    MessageBox.Show("柜体宽度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                
                if (PanelData.CabinetHeight <= 0)
                {
                    MessageBox.Show("柜体高度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                
                if (PanelData.BackPanelThickness <= 0)
                {
                    MessageBox.Show("背板厚度必须大于0", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                
                // 验证计算后的背板尺寸
                if (PanelData.BackPanelWidth < 10.0 || PanelData.BackPanelHeight < 10.0 || PanelData.BackPanelThickness < 1.0)
                {
                    MessageBox.Show($"背板尺寸过小（{PanelData.BackPanelWidth:F1}×{PanelData.BackPanelHeight:F1}×{PanelData.BackPanelThickness:F1}mm），所有尺寸必须≥10mm（厚度≥1mm）", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                
                if (PanelData.BackPanelType == "分块背板")
                {
                    if (PanelData.SplitCount <= 1)
                    {
                        MessageBox.Show("分块数量必须大于1", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                    
                    if (PanelData.SinglePanelWidth < 10.0 || PanelData.SinglePanelHeight < 10.0)
                    {
                        MessageBox.Show($"分块后的单块尺寸过小（{PanelData.SinglePanelWidth:F1}×{PanelData.SinglePanelHeight:F1}mm），长度和宽度必须≥10mm，请调整分块数量或间隙", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }
                
                if (PanelData.InstallationType == "内嵌式")
                {
                    double minWidth = 2 * PanelData.SidePanelThickness + PanelData.Clearance + 50; // 最小50mm背板宽度
                    double minHeight = 2 * PanelData.TopBottomThickness + PanelData.Clearance + 50; // 最小50mm背板高度
                    
                    if (PanelData.CabinetWidth < minWidth)
                    {
                        MessageBox.Show($"柜体宽度太小，内嵌式背板至少需要{minWidth:F0}mm", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                    
                    if (PanelData.CabinetHeight < minHeight)
                    {
                        MessageBox.Show($"柜体高度太小，内嵌式背板至少需要{minHeight:F0}mm", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
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
    /// 背板数据模型
    /// </summary>
    public class BackPanelData
    {
        public double CabinetWidth { get; set; }
        public double CabinetHeight { get; set; }
        public double BackPanelThickness { get; set; }
        public double SidePanelThickness { get; set; }
        public double TopBottomThickness { get; set; }
        public double BackPanelDepth { get; set; }
        
        public string BackPanelType { get; set; } = "";
        public string InstallationType { get; set; } = "";
        public string BackPanelMaterial { get; set; } = "";
        
        public int SplitCount { get; set; }
        public string SplitDirection { get; set; } = "";
        public bool IncludeSlot { get; set; }
        public double SlotDepth { get; set; }
        public double Clearance { get; set; }
        public double SplitGap { get; set; }
        
        // 计算结果
        public double BackPanelWidth { get; set; }
        public double BackPanelHeight { get; set; }
        public double SinglePanelWidth { get; set; }
        public double SinglePanelHeight { get; set; }
        public int PanelCount { get; set; }
    }
}