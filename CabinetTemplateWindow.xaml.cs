using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace FurniturePlugin
{
    public partial class CabinetTemplateWindow : Window
    {
        private List<CabinetTemplate> _allTemplates = new();

        public CabinetTemplate? SelectedTemplate { get; private set; }

        public CabinetTemplateWindow()
        {
            InitializeComponent();
            LoadTemplates();
        }

        private void LoadTemplates()
        {
            _allTemplates.Clear();

            // 加载预设模板
            var presets = CabinetTemplateService.GetPresetTemplates();
            foreach (var p in presets) p.IsPreset = true;
            _allTemplates.AddRange(presets);

            // 加载用户模板
            _allTemplates.AddRange(CabinetTemplateService.GetUserTemplates());

            // 填充分类
            cmbCategory.Items.Clear();
            cmbCategory.Items.Add("全部");
            foreach (var cat in _allTemplates.Select(t => t.Category).Distinct().OrderBy(c => c))
                cmbCategory.Items.Add(cat);
            cmbCategory.SelectedIndex = 0;

            lbTemplates.ItemsSource = _allTemplates;
        }

        private void CmbCategory_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbCategory.SelectedItem is string category)
            {
                lbTemplates.ItemsSource = category == "全部"
                    ? _allTemplates
                    : _allTemplates.Where(t => t.Category == category).ToList();
            }
        }

        private void BtnUse_Click(object sender, RoutedEventArgs e)
        {
            if (lbTemplates.SelectedItem is not CabinetTemplate selected)
            {
                MessageBox.Show("请先选择一个模板。", "提示");
                return;
            }

            SelectedTemplate = selected;
            this.DialogResult = true;
            this.Close();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 让用户输入模板名称
            var inputDialog = new Window
            {
                Title = "保存模板",
                Width = 350,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
            stack.Children.Add(new System.Windows.Controls.TextBlock { Text = "模板名称:" });
            var txtName = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 3, 0, 10) };
            stack.Children.Add(txtName);

            stack.Children.Add(new System.Windows.Controls.TextBlock { Text = "分类:" });
            var txtCategory = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 3, 0, 10), Text = "自定义" };
            stack.Children.Add(txtCategory);

            stack.Children.Add(new System.Windows.Controls.TextBlock { Text = "描述:" });
            var txtDesc = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 3, 0, 10) };
            stack.Children.Add(txtDesc);

            var btnOk = new System.Windows.Controls.Button { Content = "保存", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
            stack.Children.Add(btnOk);

            inputDialog.Content = stack;

            btnOk.Click += (s, args) =>
            {
                var name = txtName.Text?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show("请输入模板名称。", "提示");
                    return;
                }

                var template = new CabinetTemplate
                {
                    Name = name,
                    Category = txtCategory.Text?.Trim() ?? "自定义",
                    Description = txtDesc.Text?.Trim() ?? "",
                    Config = new CabinetDecomposeConfig() // 默认配置，可从当前拆单窗口获取
                };

                if (CabinetTemplateService.SaveTemplate(template))
                {
                    MessageBox.Show("模板保存成功！", "成功");
                    LoadTemplates();
                }
                inputDialog.Close();
            };

            inputDialog.ShowDialog();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (lbTemplates.SelectedItem is not CabinetTemplate selected)
            {
                MessageBox.Show("请先选择要删除的模板。", "提示");
                return;
            }

            if (selected.IsPreset)
            {
                MessageBox.Show("预设模板不可删除。", "提示");
                return;
            }

            if (MessageBox.Show($"确定要删除模板 \"{selected.Name}\" 吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                if (CabinetTemplateService.DeleteTemplate(selected.FilePath ?? ""))
                {
                    LoadTemplates();
                }
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadTemplates();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
