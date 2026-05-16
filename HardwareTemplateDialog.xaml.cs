using System;
using System.Collections.Generic;
using System.Windows;

namespace FurniturePlugin
{
    public partial class HardwareTemplateDialog : Window
    {
        public HardwareInfo? SelectedHardware { get; private set; }
        public double SelectedQuantity { get; private set; } = 1;
        public string OrderId { get; private set; } = "";
        public string CabinetId { get; private set; } = "";
        public string RoomId { get; private set; } = "";

        public HardwareTemplateDialog(List<HardwareInfo> templates)
        {
            InitializeComponent();
            dgTemplates.ItemsSource = templates;
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            if (dgTemplates.SelectedItem is not HardwareInfo selected)
            {
                MessageBox.Show("请选择一个五金件模板。", "提示");
                return;
            }

            if (!double.TryParse(txtQuantity.Text, out double qty) || qty <= 0)
            {
                MessageBox.Show("请输入有效的数量。", "提示");
                return;
            }

            SelectedHardware = selected;
            SelectedQuantity = qty;
            OrderId = txtOrderId.Text?.Trim() ?? "";
            CabinetId = txtCabinetId.Text?.Trim() ?? "";

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
