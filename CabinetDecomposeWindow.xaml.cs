using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace FurniturePlugin
{
    public partial class CabinetDecomposeWindow : Window
    {
        private List<DecomposedPanel> _result = new();
        private CabinetDecomposeConfig _config = new();

        public List<DecomposedPanel> DecomposedPanels => _result;
        public CabinetDecomposeConfig Config => _config;

        public CabinetDecomposeWindow()
        {
            InitializeComponent();
        }

        private CabinetDecomposeConfig ReadConfigFromUI()
        {
            return new CabinetDecomposeConfig
            {
                CabinetWidth = ParseDouble(txtWidth.Text, 800),
                CabinetDepth = ParseDouble(txtDepth.Text, 600),
                CabinetHeight = ParseDouble(txtHeight.Text, 2400),
                PanelThickness = ParseDouble(txtThickness.Text, 18),
                BackPanelThickness = ParseDouble(txtBackThickness.Text, 9),
                BottomKickHeight = ParseDouble(txtKickHeight.Text, 80),
                OrderId = txtOrderId.Text?.Trim() ?? "",
                CabinetId = txtCabinetId.Text?.Trim() ?? "",
                RoomId = txtRoomId.Text?.Trim() ?? "",
                Material = txtMaterial.Text?.Trim() ?? "18mm多层实木板",
                BackPanelMaterial = txtBackMaterial.Text?.Trim() ?? "9mm密度板",
                HasBackPanel = chkHasBackPanel.IsChecked == true,
                BackPanelInsert = chkBackPanelInsert.IsChecked == true,
                HasDoor = chkHasDoor.IsChecked == true,
                HasDrawer = chkHasDrawer.IsChecked == true,
                HasKickPlate = chkHasKickPlate.IsChecked == true,
                ShelfFixed = chkShelfFixed.IsChecked == true,
                ShelfCount = (int)ParseDouble(txtShelfCount.Text, 2),
                ShelfThickness = ParseDouble(txtShelfThickness.Text, 18),
                DoorCount = (int)ParseDouble(txtDoorCount.Text, 2),
                DoorThickness = ParseDouble(txtDoorThickness.Text, 18),
                DrawerCount = (int)ParseDouble(txtDrawerCount.Text, 0),
                DrawerHeight = ParseDouble(txtDrawerHeight.Text, 150),
                SidePanelEdgeMaterial = txtSideEdgeMaterial.Text?.Trim() ?? "1mm同色封边",
                SidePanelEdgeFront = chkSideEdgeFront.IsChecked == true,
                SidePanelEdgeBack = chkSideEdgeBack.IsChecked == true,
                TopBottomEdgeMaterial = txtTopBottomEdgeMaterial.Text?.Trim() ?? "1mm同色封边",
                TopBottomEdgeFront = chkTopBottomEdgeFront.IsChecked == true,
                TopBottomEdgeBack = chkTopBottomEdgeBack.IsChecked == true,
                ShelfEdgeMaterial = txtShelfEdgeMaterial.Text?.Trim() ?? "1mm同色封边",
                ShelfEdgeFront = chkShelfEdgeFront.IsChecked == true,
                ShelfEdgeBack = chkShelfEdgeBack.IsChecked == true,
            };
        }

        private static double ParseDouble(string text, double defaultVal)
        {
            return double.TryParse(text, out double val) ? val : defaultVal;
        }

        private void BtnCalculate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _config = ReadConfigFromUI();
                _result = CabinetDecomposeService.Decompose(_config);
                dgResult.ItemsSource = _result;

                var boardCount = _result.Sum(p => p.Quantity);
                var totalArea = _result.Sum(p => p.Length * p.Width * p.Quantity / 1000000.0);
                MessageBox.Show(
                    $"拆单完成！\n共 {boardCount} 块板件\n总面积: {totalArea:F2} m²",
                    "拆单结果", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("拆单计算失败", ex);
                MessageBox.Show($"拆单计算失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCreateCad_Click(object sender, RoutedEventArgs e)
        {
            if (_result.Count == 0)
            {
                MessageBox.Show("请先执行拆单计算。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            this.DialogResult = true;
            this.Close();
        }

        private void BtnAddHardware_Click(object sender, RoutedEventArgs e)
        {
            if (_result.Count == 0)
            {
                MessageBox.Show("请先执行拆单计算。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var hardwareList = CabinetDecomposeService.GenerateHardwareList(_config, _result);
                int count = HardwareService.AddHardwareRange(hardwareList);
                MessageBox.Show(
                    $"已添加 {count} 项五金件到五金件管理。\n可在五金件管理窗口中查看和管理。",
                    "五金件添加完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("添加五金件失败", ex);
                MessageBox.Show($"添加五金件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
