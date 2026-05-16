using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace FurniturePlugin
{
    public partial class HardwareManageWindow : Window
    {
        private List<HardwareInfo> _currentData = new();

        public HardwareManageWindow()
        {
            InitializeComponent();
            InitFilterComboBox();
            LoadData();
        }

        private void InitFilterComboBox()
        {
            cmbFilterType.Items.Add(new KeyValuePair<HardwareType?, string>(null, "全部"));
            foreach (var kvp in HardwareInfo.AllTypes)
                cmbFilterType.Items.Add(new KeyValuePair<HardwareType?, string>(kvp.Key, kvp.Value));
            cmbFilterType.DisplayMemberPath = "Value";
            cmbFilterType.SelectedValuePath = "Key";
            cmbFilterType.SelectedIndex = 0;
        }

        private void LoadData()
        {
            try
            {
                _currentData = HardwareService.GetAllHardware();
                ApplyFilter();
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("加载五金件数据失败", ex);
                MessageBox.Show($"加载数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyFilter()
        {
            var filtered = _currentData.AsEnumerable();

            var orderId = txtFilterOrder.Text?.Trim();
            if (!string.IsNullOrEmpty(orderId))
                filtered = filtered.Where(h => h.OrderId.Contains(orderId));

            var cabinetId = txtFilterCabinet.Text?.Trim();
            if (!string.IsNullOrEmpty(cabinetId))
                filtered = filtered.Where(h => h.CabinetId.Contains(cabinetId));

            if (cmbFilterType.SelectedValue is HardwareType type)
                filtered = filtered.Where(h => h.Type == type);

            dgHardware.ItemsSource = filtered.ToList();
            UpdateStatistics();
        }

        private void UpdateStatistics()
        {
            var items = dgHardware.ItemsSource as List<HardwareInfo> ?? new List<HardwareInfo>();
            txtTotalCount.Text = items.Sum(h => h.Quantity).ToString("F0");
            txtTotalTypes.Text = items.Select(h => h.Name).Distinct().Count().ToString();
            txtTotalPrice.Text = $"¥{items.Sum(h => h.TotalPrice):F2}";
            txtAssociatedCount.Text = items.Count(h => !string.IsNullOrEmpty(h.AssociatedPanelId)).ToString();
        }

        #region 按钮事件

        private void BtnFilter_Click(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new HardwareEditDialog();
            if (dlg.ShowDialog() == true)
            {
                if (HardwareService.AddHardware(dlg.Hardware))
                {
                    LoadData();
                }
            }
        }

        private void BtnAddTemplate_Click(object sender, RoutedEventArgs e)
        {
            var templates = HardwareService.GetCommonTemplates();
            var dlg = new HardwareTemplateDialog(templates);
            if (dlg.ShowDialog() == true && dlg.SelectedHardware != null)
            {
                // 复制模板数据
                var hw = new HardwareInfo
                {
                    Name = dlg.SelectedHardware.Name,
                    Type = dlg.SelectedHardware.Type,
                    Model = dlg.SelectedHardware.Model,
                    Brand = dlg.SelectedHardware.Brand,
                    Material = dlg.SelectedHardware.Material,
                    Unit = dlg.SelectedHardware.Unit,
                    UnitPrice = dlg.SelectedHardware.UnitPrice,
                    Quantity = dlg.SelectedQuantity,
                    OrderId = dlg.OrderId,
                    CabinetId = dlg.CabinetId,
                    RoomId = dlg.RoomId,
                };
                if (HardwareService.AddHardware(hw))
                {
                    LoadData();
                }
            }
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (dgHardware.SelectedItem is not HardwareInfo selected)
            {
                MessageBox.Show("请先选择要编辑的五金件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new HardwareEditDialog(selected);
            if (dlg.ShowDialog() == true)
            {
                if (HardwareService.UpdateHardware(dlg.Hardware))
                {
                    LoadData();
                }
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = dgHardware.SelectedItems.Cast<HardwareInfo>().ToList();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("请先选择要删除的五金件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"确定要删除 {selectedItems.Count} 个五金件吗？",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            int deleted = 0;
            foreach (var item in selectedItems)
            {
                if (HardwareService.DeleteHardware(item.OrderId, item.Id))
                    deleted++;
            }

            MessageBox.Show($"已删除 {deleted} 个五金件。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadData();
        }

        private void BtnAssociatePanel_Click(object sender, RoutedEventArgs e)
        {
            if (dgHardware.SelectedItem is not HardwareInfo selected)
            {
                MessageBox.Show("请先选择要关联的五金件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 提示用户在CAD中选择板件实体
            this.Hide();
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    MessageBox.Show("没有活动的AutoCAD文档。", "错误");
                    return;
                }

                var editor = doc.Editor;
                var peo = new Autodesk.AutoCAD.EditorInput.PromptEntityOptions("\n请选择要关联的板件实体: ");
                peo.SetRejectMessage("\n只能选择3D实体。");
                peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Solid3d), true);
                var per = editor.GetEntity(peo);

                if (per.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
                {
                    selected.AssociatedPanelId = per.ObjectId.Handle.Value.ToString();
                    HardwareService.UpdateHardware(selected);
                    MessageBox.Show($"已关联到实体 {selected.AssociatedPanelId}", "成功");
                }
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("关联板件失败", ex);
                MessageBox.Show($"关联失败: {ex.Message}", "错误");
            }
            finally
            {
                this.Show();
                LoadData();
            }
        }

        private void BtnUnassociate_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = dgHardware.SelectedItems.Cast<HardwareInfo>()
                .Where(h => !string.IsNullOrEmpty(h.AssociatedPanelId))
                .ToList();

            if (selectedItems.Count == 0)
            {
                MessageBox.Show("请先选择已关联板件的五金件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var item in selectedItems)
            {
                item.AssociatedPanelId = "";
                HardwareService.UpdateHardware(item);
            }

            LoadData();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var items = dgHardware.ItemsSource as List<HardwareInfo> ?? new List<HardwareInfo>();
            if (items.Count == 0)
            {
                MessageBox.Show("没有数据可导出。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV文件|*.csv",
                    DefaultExt = ".csv",
                    FileName = $"五金件清单_{DateTime.Now:yyyyMMdd}"
                };

                if (dlg.ShowDialog() != true) return;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("名称,类型,型号,品牌,材质,数量,单位,安装位置,单价,总价,供应商,订单号,柜号,房间,备注");

                foreach (var item in items)
                {
                    sb.AppendLine($"\"{item.Name}\",\"{item.TypeDisplayName}\",\"{item.Model}\",\"{item.Brand}\"," +
                        $"\"{item.Material}\",{item.Quantity},\"{item.Unit}\",\"{item.PositionDisplayName}\"," +
                        $"{item.UnitPrice:F2},{item.TotalPrice:F2},\"{item.Supplier}\"," +
                        $"\"{item.OrderId}\",\"{item.CabinetId}\",\"{item.RoomId}\",\"{item.Remarks}\"");
                }

                File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                MessageBox.Show($"已导出到: {dlg.FileName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _currentData = HardwareService.GetAllHardware();
            ApplyFilter();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        #endregion
    }
}
