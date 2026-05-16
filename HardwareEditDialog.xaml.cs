using System;
using System.Collections.Generic;
using System.Windows;

namespace FurniturePlugin
{
    public partial class HardwareEditDialog : Window
    {
        public HardwareInfo Hardware { get; private set; }

        public HardwareEditDialog() : this(new HardwareInfo()) { }

        public HardwareEditDialog(HardwareInfo existing)
        {
            InitializeComponent();
            Hardware = existing ?? new HardwareInfo();
            InitComboBoxes();
            LoadData();
        }

        private void InitComboBoxes()
        {
            cmbType.ItemsSource = HardwareInfo.AllTypes;
            cmbPosition.ItemsSource = HardwareInfo.AllPositions;
        }

        private void LoadData()
        {
            txtName.Text = Hardware.Name;
            cmbType.SelectedValue = Hardware.Type;
            txtModel.Text = Hardware.Model;
            txtBrand.Text = Hardware.Brand;
            txtMaterial.Text = Hardware.Material;
            txtQuantity.Text = Hardware.Quantity.ToString();
            txtUnit.Text = Hardware.Unit;
            cmbPosition.SelectedValue = Hardware.InstallPosition;
            txtInstallDesc.Text = Hardware.InstallDescription;
            txtUnitPrice.Text = Hardware.UnitPrice.ToString();
            txtSupplier.Text = Hardware.Supplier;
            txtOrderId.Text = Hardware.OrderId;
            txtCabinetId.Text = Hardware.CabinetId;
            txtRoomId.Text = Hardware.RoomId;
            txtRemarks.Text = Hardware.Remarks;
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("请输入五金件名称。", "提示");
                return;
            }

            if (!double.TryParse(txtQuantity.Text, out double qty) || qty <= 0)
            {
                MessageBox.Show("请输入有效的数量。", "提示");
                return;
            }

            double price = 0;
            double.TryParse(txtUnitPrice.Text, out price);

            Hardware.Name = txtName.Text.Trim();
            Hardware.Type = cmbType.SelectedValue is HardwareType t ? t : HardwareType.Other;
            Hardware.Model = txtModel.Text?.Trim() ?? "";
            Hardware.Brand = txtBrand.Text?.Trim() ?? "";
            Hardware.Material = txtMaterial.Text?.Trim() ?? "";
            Hardware.Quantity = qty;
            Hardware.Unit = txtUnit.Text?.Trim() ?? "个";
            Hardware.InstallPosition = cmbPosition.SelectedValue is InstallPosition p ? p : InstallPosition.Other;
            Hardware.InstallDescription = txtInstallDesc.Text?.Trim() ?? "";
            Hardware.UnitPrice = price;
            Hardware.Supplier = txtSupplier.Text?.Trim() ?? "";
            Hardware.OrderId = txtOrderId.Text?.Trim() ?? "";
            Hardware.CabinetId = txtCabinetId.Text?.Trim() ?? "";
            Hardware.RoomId = txtRoomId.Text?.Trim() ?? "";
            Hardware.Remarks = txtRemarks.Text?.Trim() ?? "";

            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
