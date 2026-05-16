using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using FurniturePlugin.Optimization;

namespace FurniturePlugin
{
    public partial class PaiBanWindow : Window
    {
        // ── 基本参数 ──
        public double SheetWidth { get; private set; } = 2440;
        public double SheetHeight { get; private set; } = 1220;
        public double PartSpacing { get; private set; } = 5;
        public double EdgeMargin { get; private set; } = 5;
        public bool AllowRotate { get; private set; } = true;
        public bool GroupByMaterial { get; private set; } = true;
        public bool ExportCsv { get; private set; } = true;
        public NestingSolveMode SolveMode { get; private set; } = NestingSolveMode.HeuristicFast;
        public int GlobalOptTimeLimitSeconds { get; private set; } = 120;
        public bool ShowOptimizationRunWindow { get; private set; } = true;
        public bool EnableIrregularExactPoc { get; private set; } = true;
        public int IrregularExactPocThreshold { get; private set; } = 20;

        /// <summary>全局大板规格（兼容旧版）</summary>
        public List<SheetSpecItem> SheetSpecs { get; private set; } = new();

        /// <summary>允许360°旋转的材质</summary>
        public HashSet<string> Allow360Materials { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>按材质分别的大板规格配置（key=材质名）</summary>
        public Dictionary<string, List<SheetSpecItem>> MaterialSheetSpecConfigs { get; private set; }
            = new(StringComparer.OrdinalIgnoreCase);

        // ── 内部数据 ──
        private ObservableCollection<MaterialConfigItem> _materialConfigs = new();
        private ObservableCollection<MaterialSelectionItem> _materialRotateItems = new();
        private HashSet<string> _allMaterials;

        public PaiBanWindow(HashSet<string> allMaterials = null, PaiBanConfigSnapshot snapshot = null)
        {
            InitializeComponent();
            _allMaterials = allMaterials ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            MaterialConfigsList.ItemsSource = _materialConfigs;
            MaterialRotationList.ItemsSource = _materialRotateItems;

            if (snapshot != null)
                ApplySnapshot(snapshot);
            else
            {
                PopulateMaterialConfigs();
                PopulateRotationList();
            }
        }

        public PaiBanConfigSnapshot CreateSnapshot()
        {
            var snapshot = new PaiBanConfigSnapshot
            {
                PartSpacingText = PartSpacingTextBox.Text,
                EdgeMarginText = EdgeMarginTextBox.Text,
                AllowRotate = AllowRotateCheckBox.IsChecked == true,
                GroupByMaterial = GroupByMaterialCheckBox.IsChecked == true,
                ExportCsv = ExportCsvCheckBox.IsChecked == true,
                SolveModeIndex = SolveModeComboBox.SelectedIndex,
                GlobalOptTimeLimitText = GlobalOptTimeLimitTextBox.Text,
                ShowOptRunWindow = ShowOptRunWindowCheckBox.IsChecked == true,
                EnableIrregularPoc = EnableIrregularPocCheckBox.IsChecked == true,
                IrregularPocThresholdText = IrregularPocThresholdTextBox.Text
            };

            foreach (var config in _materialConfigs)
            {
                var mc = new MaterialConfigSnapshot
                {
                    MaterialName = config.MaterialName,
                    Use2440x1220 = config.Use2440x1220,
                    Use2745x1220 = config.Use2745x1220,
                    Use2800x1220 = config.Use2800x1220,
                    Use2800x2080 = config.Use2800x2080,
                    Use3050x1220 = config.Use3050x1220
                };
                foreach (var cs in config.CustomSpecs)
                    mc.CustomSpecs.Add((cs.Width, cs.Height));
                snapshot.MaterialConfigs.Add(mc);
            }

            snapshot.Allow360Materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _materialRotateItems)
            {
                if (item.IsSelected && !string.IsNullOrWhiteSpace(item.Material))
                    snapshot.Allow360Materials.Add(item.Material);
            }

            return snapshot;
        }

        private void ApplySnapshot(PaiBanConfigSnapshot snapshot)
        {
            PartSpacingTextBox.Text = snapshot.PartSpacingText;
            EdgeMarginTextBox.Text = snapshot.EdgeMarginText;
            AllowRotateCheckBox.IsChecked = snapshot.AllowRotate;
            GroupByMaterialCheckBox.IsChecked = snapshot.GroupByMaterial;
            ExportCsvCheckBox.IsChecked = snapshot.ExportCsv;
            SolveModeComboBox.SelectedIndex = snapshot.SolveModeIndex;
            GlobalOptTimeLimitTextBox.Text = snapshot.GlobalOptTimeLimitText;
            ShowOptRunWindowCheckBox.IsChecked = snapshot.ShowOptRunWindow;
            EnableIrregularPocCheckBox.IsChecked = snapshot.EnableIrregularPoc;
            IrregularPocThresholdTextBox.Text = snapshot.IrregularPocThresholdText;

            _materialConfigs.Clear();
            if (snapshot.MaterialConfigs.Count > 0)
            {
                NoMaterialHint2.Visibility = Visibility.Collapsed;
                foreach (var mc in snapshot.MaterialConfigs)
                {
                    var item = new MaterialConfigItem
                    {
                        MaterialName = mc.MaterialName,
                        Use2440x1220 = mc.Use2440x1220,
                        Use2745x1220 = mc.Use2745x1220,
                        Use2800x1220 = mc.Use2800x1220,
                        Use2800x2080 = mc.Use2800x2080,
                        Use3050x1220 = mc.Use3050x1220
                    };
                    foreach (var cs in mc.CustomSpecs)
                        item.CustomSpecs.Add(new SheetSpecItem { Width = cs.Width, Height = cs.Height });
                    _materialConfigs.Add(item);
                }
            }
            else
            {
                PopulateMaterialConfigs();
            }

            _materialRotateItems.Clear();
            if (_allMaterials.Count > 0)
            {
                NoRotateHint.Visibility = Visibility.Collapsed;
                foreach (var mat in _allMaterials.OrderBy(m => m))
                {
                    if (string.IsNullOrWhiteSpace(mat)) continue;
                    _materialRotateItems.Add(new MaterialSelectionItem
                    {
                        Material = mat,
                        IsSelected = snapshot.Allow360Materials.Contains(mat)
                    });
                }
            }
            else
            {
                NoRotateHint.Visibility = Visibility.Visible;
            }
        }

        // ── 材质配置初始化 ──

        private void PopulateMaterialConfigs()
        {
            _materialConfigs.Clear();
            if (_allMaterials == null || _allMaterials.Count == 0)
            {
                NoMaterialHint2.Visibility = Visibility.Visible;
                return;
            }
            NoMaterialHint2.Visibility = Visibility.Collapsed;

            foreach (var mat in _allMaterials.OrderBy(m => m))
            {
                if (string.IsNullOrWhiteSpace(mat)) continue;
                _materialConfigs.Add(new MaterialConfigItem
                {
                    MaterialName = mat,
                    Use2440x1220 = true,  // 默认都勾选最常用规格
                    Use2745x1220 = true,
                    Use2800x1220 = false,
                    Use2800x2080 = false,
                    Use3050x1220 = false
                });
            }
        }

        private void PopulateRotationList()
        {
            _materialRotateItems.Clear();
            if (_allMaterials == null || _allMaterials.Count == 0)
            {
                NoRotateHint.Visibility = Visibility.Visible;
                return;
            }
            NoRotateHint.Visibility = Visibility.Collapsed;

            foreach (var mat in _allMaterials.OrderBy(m => m))
            {
                if (string.IsNullOrWhiteSpace(mat)) continue;
                _materialRotateItems.Add(new MaterialSelectionItem
                {
                    Material = mat,
                    IsSelected = false
                });
            }
        }

        // ── 自定义大板规格操作 ──

        private void AddCustomSpecForMaterial_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not MaterialConfigItem config) return;

            if (!double.TryParse(config.CustomWidth, out double w) ||
                !double.TryParse(config.CustomHeight, out double h) ||
                w < 100 || h < 100)
            {
                MessageBox.Show("请输入有效的大板尺寸（宽和高需大于100mm）", "输入错误");
                return;
            }

            config.CustomSpecs.Add(new SheetSpecItem { Width = w, Height = h });
            config.CustomWidth = "";
            config.CustomHeight = "";
        }

        private void RemoveCustomSpecForMaterial_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SheetSpecItem spec) return;

            // 找到所属的MaterialConfigItem
            foreach (var config in _materialConfigs)
            {
                if (config.CustomSpecs.Contains(spec))
                {
                    config.CustomSpecs.Remove(spec);
                    break;
                }
            }
        }

        // ── 确定按钮 ──

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PartSpacing = double.Parse(PartSpacingTextBox.Text);
                EdgeMargin = double.Parse(EdgeMarginTextBox.Text);
                AllowRotate = AllowRotateCheckBox.IsChecked == true;
                GroupByMaterial = GroupByMaterialCheckBox.IsChecked == true;
                ExportCsv = ExportCsvCheckBox.IsChecked == true;
                SolveMode = SolveModeComboBox.SelectedIndex == 1 ? NestingSolveMode.GlobalCpSat : NestingSolveMode.HeuristicFast;
                if (!int.TryParse(GlobalOptTimeLimitTextBox.Text, out var timeLimit) || timeLimit < 1 || timeLimit > 3600)
                {
                    MessageBox.Show("全局优化时间上限请输入 1~3600 秒。", "提示");
                    return;
                }
                GlobalOptTimeLimitSeconds = timeLimit;
                ShowOptimizationRunWindow = ShowOptRunWindowCheckBox.IsChecked == true;
                EnableIrregularExactPoc = EnableIrregularPocCheckBox.IsChecked == true;
                if (!int.TryParse(IrregularPocThresholdTextBox.Text, out var pocThreshold) || pocThreshold < 1 || pocThreshold > 500)
                {
                    MessageBox.Show("异形PoC阈值请输入 1~500。", "提示");
                    return;
                }
                IrregularExactPocThreshold = pocThreshold;

                // 构建按材质的大板规格字典
                MaterialSheetSpecConfigs = new Dictionary<string, List<SheetSpecItem>>(StringComparer.OrdinalIgnoreCase);

                bool anyChecked = false;
                foreach (var config in _materialConfigs)
                {
                    var specs = new List<SheetSpecItem>();

                    if (config.Use2440x1220) specs.Add(new SheetSpecItem { Width = 2440, Height = 1220 });
                    if (config.Use2745x1220) specs.Add(new SheetSpecItem { Width = 2745, Height = 1220 });
                    if (config.Use2800x1220) specs.Add(new SheetSpecItem { Width = 2800, Height = 1220 });
                    if (config.Use2800x2080) specs.Add(new SheetSpecItem { Width = 2800, Height = 2080 });
                    if (config.Use3050x1220) specs.Add(new SheetSpecItem { Width = 3050, Height = 1220 });

                    foreach (var cs in config.CustomSpecs)
                    {
                        if (cs.Width > 0 && cs.Height > 0)
                            specs.Add(new SheetSpecItem { Width = cs.Width, Height = cs.Height });
                    }

                    if (specs.Count > 0)
                    {
                        anyChecked = true;
                        MaterialSheetSpecConfigs[config.MaterialName] = specs;
                    }
                }

                if (!anyChecked)
                {
                    MessageBox.Show("请至少为一个材质选择一个可用大板规格。", "提示");
                    return;
                }

                // 全局兼容列表（所有材质规格的合集）
                SheetSpecs = MaterialSheetSpecConfigs.Values
                    .SelectMany(s => s)
                    .DistinctBy(s => $"{s.Width:F0}x{s.Height:F0}")
                    .ToList();

                var first = SheetSpecs[0];
                SheetWidth = first.Width;
                SheetHeight = first.Height;

                // 360°旋转材质
                Allow360Materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _materialRotateItems)
                {
                    if (item.IsSelected && !string.IsNullOrWhiteSpace(item.Material))
                        Allow360Materials.Add(item.Material);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"参数错误: {ex.Message}", "错误");
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    // ═══════════════════════════════════════════════
    // 数据类
    // ═══════════════════════════════════════════════

    /// <summary>每个材质的大板规格配置</summary>
    public class MaterialConfigItem : INotifyPropertyChanged
    {
        public string MaterialName { get; set; } = "";

        private bool _use2440x1220;
        public bool Use2440x1220 { get => _use2440x1220; set { _use2440x1220 = value; OnChanged(); } }

        private bool _use2745x1220;
        public bool Use2745x1220 { get => _use2745x1220; set { _use2745x1220 = value; OnChanged(); } }

        private bool _use2800x1220;
        public bool Use2800x1220 { get => _use2800x1220; set { _use2800x1220 = value; OnChanged(); } }

        private bool _use2800x2080;
        public bool Use2800x2080 { get => _use2800x2080; set { _use2800x2080 = value; OnChanged(); } }

        private bool _use3050x1220;
        public bool Use3050x1220 { get => _use3050x1220; set { _use3050x1220 = value; OnChanged(); } }

        private string _customWidth = "";
        public string CustomWidth { get => _customWidth; set { _customWidth = value; OnChanged(); } }

        private string _customHeight = "";
        public string CustomHeight { get => _customHeight; set { _customHeight = value; OnChanged(); } }

        public ObservableCollection<SheetSpecItem> CustomSpecs { get; set; } = new();

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>大板规格数据项</summary>
    public class SheetSpecItem
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public string Label => $"{Width:F0} × {Height:F0}";
    }

    /// <summary>材质旋转选择项</summary>
    public class MaterialSelectionItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string Material { get; set; } = "";
        public string Label => Material;

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class PaiBanConfigSnapshot
    {
        public string PartSpacingText { get; set; } = "5";
        public string EdgeMarginText { get; set; } = "5";
        public bool AllowRotate { get; set; } = true;
        public bool GroupByMaterial { get; set; } = true;
        public bool ExportCsv { get; set; } = true;
    public int SolveModeIndex { get; set; } = 0;
    public string GlobalOptTimeLimitText { get; set; } = "120";
    public bool ShowOptRunWindow { get; set; } = true;
        public bool EnableIrregularPoc { get; set; } = true;
        public string IrregularPocThresholdText { get; set; } = "20";
        public List<MaterialConfigSnapshot> MaterialConfigs { get; set; } = new();
        public HashSet<string> Allow360Materials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class MaterialConfigSnapshot
    {
        public string MaterialName { get; set; } = "";
        public bool Use2440x1220 { get; set; }
        public bool Use2745x1220 { get; set; }
        public bool Use2800x1220 { get; set; }
        public bool Use2800x2080 { get; set; }
        public bool Use3050x1220 { get; set; }
        public List<(double Width, double Height)> CustomSpecs { get; set; } = new();
    }
}
