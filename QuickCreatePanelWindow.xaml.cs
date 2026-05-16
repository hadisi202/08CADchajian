using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FurniturePlugin
{
    public partial class QuickCreatePanelWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };
        
        // 数据持久化路径
        private static readonly string _dataFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FurniturePlugin", "QuickCreateData.json");
        
        // 静态字段保存最后输入的数据
        private static QuickCreateData _lastInputData = new QuickCreateData();
        
        public QuickCreateData PanelData { get; set; }
        
        // 搭积木相关
        private List<CabinetFrame> _availableFrames = new();
        private CabinetFrame _selectedFrame;
        
        // 静态构造函数，加载保存的数据
        static QuickCreatePanelWindow()
        {
            LoadLastInputData();
        }
        
        public QuickCreatePanelWindow()
        {
            InitializeComponent();
            
            // 使用保存的数据初始化界面
            PanelData = new QuickCreateData();
            PanelData.CopyFrom(_lastInputData);
            
            LoadDataToUI();
            LoadAvailableFrames();
            
            // 设置焦点到长度输入框
            Loaded += (s, e) => LengthTextBox.Focus();
        }
        
        private void LoadDataToUI()
        {
            LengthTextBox.Text = PanelData.Length.ToString();
            WidthTextBox.Text = PanelData.Width.ToString();
            ThicknessTextBox.Text = PanelData.Thickness.ToString();
            HorizontalRotationTextBox.Text = PanelData.HorizontalRotation.ToString();
            VerticalRotationTextBox.Text = PanelData.VerticalRotation.ToString();
            
            switch (PanelData.Direction)
            {
                case PanelDirection.X:
                    XDirectionRadio.IsChecked = true;
                    break;
                case PanelDirection.Y:
                    YDirectionRadio.IsChecked = true;
                    break;
                case PanelDirection.Z:
                    ZDirectionRadio.IsChecked = true;
                    break;
            }
        }
        
        /// <summary>
        /// 加载可用的搭积木外框
        /// </summary>
        private void LoadAvailableFrames()
        {
            try
            {
                CabinetFrameService.LoadAll();
                _availableFrames = CabinetFrameService.GetAllFrames().ToList();
                
                cmbFrame.Items.Clear();
                cmbFrame.Items.Add("(无 - 独立板件)");
                foreach (var frame in _availableFrames)
                {
                    cmbFrame.Items.Add($"柜{frame.CabinetId} [{frame.Width:F0}x{frame.Depth:F0}x{frame.Height:F0}]");
                }
                cmbFrame.SelectedIndex = 0;
            }
            catch
            {
                _availableFrames = new List<CabinetFrame>();
                cmbFrame.Items.Clear();
                cmbFrame.Items.Add("(无 - 独立板件)");
                cmbFrame.SelectedIndex = 0;
            }
        }
        
        private void cmbFrame_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbFrame.SelectedIndex <= 0)
            {
                _selectedFrame = null;
                txtFrameInfo.Text = "未选择外框 - 将创建独立板件";
                txtSpaceInfo.Text = "";
                return;
            }
            
            int idx = cmbFrame.SelectedIndex - 1;
            if (idx < _availableFrames.Count)
            {
                _selectedFrame = _availableFrames[idx];
                var frame = _selectedFrame;
                txtFrameInfo.Text = $"外框: {frame.Width:F0}(X) x {frame.Depth:F0}(Y) x {frame.Height:F0}(Z)mm | 板件: {frame.Panels.Count}个";
                
                // 显示可用空间信息
                var leafSpaces = new List<SpaceNode>();
                CollectLeafSpaces(frame.RootSpace, leafSpaces);
                if (leafSpaces.Count > 0)
                {
                    string spaceInfo = "可用空间:\n";
                    foreach (var space in leafSpaces.Take(5))
                    {
                        double w = space.MaxX - space.MinX;
                        double d = space.MaxY - space.MinY;
                        double h = space.MaxZ - space.MinZ;
                        spaceInfo += $"  {space.Id}: {w:F0}x{d:F0}x{h:F0}mm\n";
                    }
                    if (leafSpaces.Count > 5)
                        spaceInfo += $"  ... 共{leafSpaces.Count}个空间";
                    txtSpaceInfo.Text = spaceInfo;
                }
                else
                {
                    txtSpaceInfo.Text = "无可用空间（已全部占用）";
                }
            }
        }
        
        private void CollectLeafSpaces(SpaceNode node, List<SpaceNode> leaves)
        {
            if (node == null) return;
            if (node.IsLeaf)
            {
                // 过滤掉太小的空间
                if (node.Width > 10 && node.Depth > 10 && node.Height > 10)
                    leaves.Add(node);
                return;
            }
            CollectLeafSpaces(node.Left, leaves);
            CollectLeafSpaces(node.Right, leaves);
        }
        
        private void BtnRefreshFrames_Click(object sender, RoutedEventArgs e)
        {
            LoadAvailableFrames();
        }
        
        private void SaveUIToData()
        {
            if (double.TryParse(LengthTextBox.Text, out double length))
                PanelData.Length = length;
            if (double.TryParse(WidthTextBox.Text, out double width))
                PanelData.Width = width;
            if (double.TryParse(ThicknessTextBox.Text, out double thickness))
                PanelData.Thickness = thickness;
            if (double.TryParse(HorizontalRotationTextBox.Text, out double horizontalRotation))
                PanelData.HorizontalRotation = horizontalRotation;
            if (double.TryParse(VerticalRotationTextBox.Text, out double verticalRotation))
                PanelData.VerticalRotation = verticalRotation;
            
            if (XDirectionRadio.IsChecked == true)
                PanelData.Direction = PanelDirection.X;
            else if (YDirectionRadio.IsChecked == true)
                PanelData.Direction = PanelDirection.Y;
            else if (ZDirectionRadio.IsChecked == true)
                PanelData.Direction = PanelDirection.Z;
        }
        
        private void PresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string preset)
            {
                var values = preset.Split(',');
                if (values.Length == 3)
                {
                    LengthTextBox.Text = values[0];
                    WidthTextBox.Text = values[1];
                    ThicknessTextBox.Text = values[2];
                }
            }
        }
        
        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (ValidateInput())
            {
                SaveUIToData();
                
                // 如果勾选了自动排序尺寸，则对尺寸进行排序
                if (AutoSortDimensionsCheckBox.IsChecked == true)
                {
                    SortDimensions();
                }
                
                // 保存数据到静态字段和文件
                _lastInputData.CopyFrom(PanelData);
                SaveLastInputData();
                
                DialogResult = true;
                Close();
            }
        }
        
        // 自动排序尺寸：长度>=宽度>=厚度
        private void SortDimensions()
        {
            var dimensions = new[] { PanelData.Length, PanelData.Width, PanelData.Thickness };
            Array.Sort(dimensions, (a, b) => b.CompareTo(a)); // 降序排序
            
            PanelData.Length = dimensions[0];   // 最大值作为长度
            PanelData.Width = dimensions[1];    // 中间值作为宽度
            PanelData.Thickness = dimensions[2]; // 最小值作为厚度
            
            // 更新界面显示
            LengthTextBox.Text = PanelData.Length.ToString();
            WidthTextBox.Text = PanelData.Width.ToString();
            ThicknessTextBox.Text = PanelData.Thickness.ToString();
        }
        
        /// <summary>
        /// 获取选中的搭积木外框
        /// </summary>
        public CabinetFrame SelectedFrame => _selectedFrame;
        
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OkButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelButton_Click(sender, e);
                e.Handled = true;
            }
        }
        
        private bool ValidateInput()
        {
            if (!double.TryParse(LengthTextBox.Text, out double length) || length <= 0)
            {
                MessageBox.Show("请输入有效的长度值（大于0）。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                LengthTextBox.Focus();
                return false;
            }
            
            if (!double.TryParse(WidthTextBox.Text, out double width) || width <= 0)
            {
                MessageBox.Show("请输入有效的宽度值（大于0）。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                WidthTextBox.Focus();
                return false;
            }
            
            if (!double.TryParse(ThicknessTextBox.Text, out double thickness) || thickness <= 0)
            {
                MessageBox.Show("请输入有效的厚度值（大于0）。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                ThicknessTextBox.Focus();
                return false;
            }
            
            if (!double.TryParse(HorizontalRotationTextBox.Text, out double horizontalRotation))
            {
                MessageBox.Show("请输入有效的水平旋转角度。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                HorizontalRotationTextBox.Focus();
                return false;
            }
            
            if (!double.TryParse(VerticalRotationTextBox.Text, out double verticalRotation))
            {
                MessageBox.Show("请输入有效的垂直旋转角度。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                VerticalRotationTextBox.Focus();
                return false;
            }
            
            return true;
        }
        
        // 保存最后输入的数据到文件
        private static void SaveLastInputData()
        {
            try
            {
                var directory = Path.GetDirectoryName(_dataFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                var json = JsonSerializer.Serialize(_lastInputData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_dataFilePath, json);
            }
            catch (System.Exception ex)
            {
                // 静默处理保存错误，不影响用户操作
                System.Diagnostics.Debug.WriteLine($"保存快速创建数据失败: {ex.Message}");
            }
        }
        
        // 从文件加载最后输入的数据
        private static void LoadLastInputData()
        {
            try
            {
                if (File.Exists(_dataFilePath))
                {
                    var json = File.ReadAllText(_dataFilePath);
                    var data = JsonSerializer.Deserialize<QuickCreateData>(json);
                    if (data != null)
                    {
                        _lastInputData = data;
                    }
                }
            }
            catch (System.Exception ex)
            {
                // 静默处理加载错误，使用默认值
                _lastInputData = new QuickCreateData();
                System.Diagnostics.Debug.WriteLine($"加载快速创建数据失败: {ex.Message}");
            }
        }
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    
    // 板件方向枚举
    public enum PanelDirection
    {
        X, // 顶底板
        Y, // 左右侧板
        Z  // 背板
    }
    
    // 快速创建数据类
    public class QuickCreateData
    {
        public double Length { get; set; } = 400;
        public double Width { get; set; } = 300;
        public double Thickness { get; set; } = 18;
        public double HorizontalRotation { get; set; } = 0;
        public double VerticalRotation { get; set; } = 0;
        public PanelDirection Direction { get; set; } = PanelDirection.X;
        
        public void CopyFrom(QuickCreateData source)
        {
            Length = source.Length;
            Width = source.Width;
            Thickness = source.Thickness;
            HorizontalRotation = source.HorizontalRotation;
            VerticalRotation = source.VerticalRotation;
            Direction = source.Direction;
        }
    }
}
