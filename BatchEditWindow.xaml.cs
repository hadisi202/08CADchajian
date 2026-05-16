using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace FurniturePlugin
{
    public partial class BatchEditWindow : Window
    {
        private ObservableCollection<BatchEditItem> _items;
        private Dictionary<string, string> _originalValues = new();
        private Dictionary<string, bool?> _originalCheckStates = new();
        private int _originalPanelShapeIndex;

        public BatchEditWindow(List<BatchEditItem> items)
        {
            InitializeComponent();
            _items = new ObservableCollection<BatchEditItem>(items ?? new List<BatchEditItem>());
            SaveOriginalValues();
            WireDirtyTracking();
            UpdateSelectedCount();
        }

        private void SaveOriginalValues()
        {
            _originalValues["OrderId"] = OrderIdTextBox.Text;
            _originalValues["CabinetId"] = CabinetIdTextBox.Text;
            _originalValues["RoomId"] = RoomIdTextBox.Text;
            _originalValues["PanelName"] = PanelNameTextBox.Text;
            _originalValues["Material"] = MaterialTextBox.Text;
            _originalValues["ExtraLength"] = ExtraLengthTextBox.Text;
            _originalValues["ExtraWidth"] = ExtraWidthTextBox.Text;
            _originalValues["ExtraHeight"] = ExtraHeightTextBox.Text;
            _originalValues["EdgeTop"] = EdgeTopTextBox.Text;
            _originalValues["EdgeBottom"] = EdgeBottomTextBox.Text;
            _originalValues["EdgeLeft"] = EdgeLeftTextBox.Text;
            _originalValues["EdgeRight"] = EdgeRightTextBox.Text;
            _originalValues["Paint"] = PaintTextBox.Text;
            _originalValues["Remarks"] = RemarksTextBox.Text;
            _originalPanelShapeIndex = PanelShapeComboBox.SelectedIndex;

            foreach (var checkBox in GetOptionCheckBoxes())
            {
                _originalCheckStates[checkBox.Name] = checkBox.IsChecked;
            }
        }

        private IEnumerable<CheckBox> GetOptionCheckBoxes()
        {
            return new[]
            {
                OrderIdCheckBox, CabinetIdCheckBox, RoomIdCheckBox, PanelNameCheckBox, MaterialCheckBox,
                ExtraLengthCheckBox, ExtraWidthCheckBox, ExtraHeightCheckBox, EdgeTopCheckBox, EdgeBottomCheckBox,
                EdgeLeftCheckBox, EdgeRightCheckBox, PanelShapeCheckBox, PaintCheckBox, RemarksCheckBox
            };
        }

        private void WireDirtyTracking()
        {
            foreach (var textBox in new[] { OrderIdTextBox, CabinetIdTextBox, RoomIdTextBox, PanelNameTextBox, MaterialTextBox,
                ExtraLengthTextBox, ExtraWidthTextBox, ExtraHeightTextBox, EdgeTopTextBox, EdgeBottomTextBox, EdgeLeftTextBox,
                EdgeRightTextBox, PaintTextBox, RemarksTextBox })
            {
                textBox.TextChanged += (_, __) => UndoButton.IsEnabled = true;
            }

            foreach (var checkBox in GetOptionCheckBoxes())
            {
                checkBox.Checked += (_, __) => UndoButton.IsEnabled = true;
                checkBox.Unchecked += (_, __) => UndoButton.IsEnabled = true;
            }

            PanelShapeComboBox.SelectionChanged += (_, __) => UndoButton.IsEnabled = true;
        }

        private void UpdateSelectedCount()
        {
            int count = _items?.Count(i => i.IsSelected) ?? 0;
            SelectedCountTextBlock.Text = $"{count} / {_items?.Count ?? 0}";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int success = ApplyChanges();
                MessageBox.Show($"批量修改完成！成功: {success} / {_items?.Count ?? 0}", "PLXG");
                DialogResult = true;
                Close();
            }
            catch (Exception ex) { MessageBox.Show($"修改出错: {ex.Message}", "错误"); }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var checkBox in GetOptionCheckBoxes())
            {
                checkBox.IsChecked = true;
            }
            UndoButton.IsEnabled = true;
        }

        private void UnselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var checkBox in GetOptionCheckBoxes())
            {
                checkBox.IsChecked = false;
            }
            UndoButton.IsEnabled = true;
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            OrderIdTextBox.Text = _originalValues["OrderId"];
            CabinetIdTextBox.Text = _originalValues["CabinetId"];
            RoomIdTextBox.Text = _originalValues["RoomId"];
            PanelNameTextBox.Text = _originalValues["PanelName"];
            MaterialTextBox.Text = _originalValues["Material"];
            ExtraLengthTextBox.Text = _originalValues["ExtraLength"];
            ExtraWidthTextBox.Text = _originalValues["ExtraWidth"];
            ExtraHeightTextBox.Text = _originalValues["ExtraHeight"];
            EdgeTopTextBox.Text = _originalValues["EdgeTop"];
            EdgeBottomTextBox.Text = _originalValues["EdgeBottom"];
            EdgeLeftTextBox.Text = _originalValues["EdgeLeft"];
            EdgeRightTextBox.Text = _originalValues["EdgeRight"];
            PaintTextBox.Text = _originalValues["Paint"];
            RemarksTextBox.Text = _originalValues["Remarks"];
            PanelShapeComboBox.SelectedIndex = _originalPanelShapeIndex;
            foreach (var checkBox in GetOptionCheckBoxes())
            {
                if (_originalCheckStates.TryGetValue(checkBox.Name, out var state))
                {
                    checkBox.IsChecked = state;
                }
            }
            UndoButton.IsEnabled = false;
        }

        private void GetPanelDataButton_Click(object sender, RoutedEventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var peo = new PromptEntityOptions("\n选择要拾取信息的板件: ");
            peo.SetRejectMessage("请选择3D实体。");
            peo.AddAllowedClass(typeof(Solid3d), true);
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;
            try
            {
                using var tr = doc.Database.TransactionManager.StartTransaction();
                var ent = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Entity;
                if (ent == null) return;
                var info = PanelInfoService.GetPanelInfo(ent, tr);
                if (info != null)
                {
                    OrderIdTextBox.Text = info.OrderId;
                    CabinetIdTextBox.Text = info.CabinetId;
                    RoomIdTextBox.Text = info.RoomId;
                    PanelNameTextBox.Text = info.PanelName;
                    MaterialTextBox.Text = info.Material;
                    ExtraLengthTextBox.Text = info.ExtraLength > 0 ? info.ExtraLength.ToString("F1") : "";
                    ExtraWidthTextBox.Text = info.ExtraWidth > 0 ? info.ExtraWidth.ToString("F1") : "";
                    ExtraHeightTextBox.Text = info.ExtraHeight > 0 ? info.ExtraHeight.ToString("F1") : "";
                    EdgeTopTextBox.Text = info.EdgeTop;
                    EdgeBottomTextBox.Text = info.EdgeBottom;
                    EdgeLeftTextBox.Text = info.EdgeLeft;
                    EdgeRightTextBox.Text = info.EdgeRight;
                    PaintTextBox.Text = info.Paint;
                    RemarksTextBox.Text = info.Remarks;
                    PanelShapeComboBox.SelectedIndex = BatchEditRules.CalcTypeToBatchShapeIndex(info.CalculationType);
                    AutoCheckFilledOptionsFromForm();
                    UndoButton.IsEnabled = true;
                }
                tr.Commit();
            }
            catch (Exception ex) { MessageBox.Show($"拾取失败: {ex.Message}"); }
        }

        private void AutoCheckFilledOptionsFromForm()
        {
            var states = BatchEditRules.BuildAutoCheckStates(
                OrderIdTextBox.Text,
                CabinetIdTextBox.Text,
                RoomIdTextBox.Text,
                PanelNameTextBox.Text,
                MaterialTextBox.Text,
                ExtraLengthTextBox.Text,
                ExtraWidthTextBox.Text,
                ExtraHeightTextBox.Text,
                EdgeTopTextBox.Text,
                EdgeBottomTextBox.Text,
                EdgeLeftTextBox.Text,
                EdgeRightTextBox.Text,
                PaintTextBox.Text,
                RemarksTextBox.Text,
                PanelShapeComboBox.SelectedIndex);

            OrderIdCheckBox.IsChecked = states["OrderId"];
            CabinetIdCheckBox.IsChecked = states["CabinetId"];
            RoomIdCheckBox.IsChecked = states["RoomId"];
            PanelNameCheckBox.IsChecked = states["PanelName"];
            MaterialCheckBox.IsChecked = states["Material"];
            ExtraLengthCheckBox.IsChecked = states["ExtraLength"];
            ExtraWidthCheckBox.IsChecked = states["ExtraWidth"];
            ExtraHeightCheckBox.IsChecked = states["ExtraHeight"];
            EdgeTopCheckBox.IsChecked = states["EdgeTop"];
            EdgeBottomCheckBox.IsChecked = states["EdgeBottom"];
            EdgeLeftCheckBox.IsChecked = states["EdgeLeft"];
            EdgeRightCheckBox.IsChecked = states["EdgeRight"];
            PanelShapeCheckBox.IsChecked = states["PanelShape"];
            PaintCheckBox.IsChecked = states["Paint"];
            RemarksCheckBox.IsChecked = states["Remarks"];
        }

        private int ApplyChanges()
        {
            int success = 0;
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return 0;
            using var tr = doc.Database.TransactionManager.StartTransaction();
            foreach (var item in _items)
            {
                if (!item.IsSelected) continue;
                try
                {
                    if (string.IsNullOrEmpty(item.Handle)) continue;
                    if (!long.TryParse(item.Handle, out long hval)) continue;
                    var h = new Handle(hval);
                    if (!doc.Database.TryGetObjectId(h, out ObjectId objId)) continue;
                    var ent = tr.GetObject(objId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    var info = PanelInfoService.GetPanelInfo(ent, tr) ?? new PanelInfo();
                    info.EntityId = item.Handle;

                    if (OrderIdCheckBox.IsChecked == true) info.OrderId = OrderIdTextBox.Text.Trim();
                    if (CabinetIdCheckBox.IsChecked == true) info.CabinetId = CabinetIdTextBox.Text.Trim();
                    if (RoomIdCheckBox.IsChecked == true) info.RoomId = RoomIdTextBox.Text.Trim();
                    if (PanelNameCheckBox.IsChecked == true) info.PanelName = PanelNameTextBox.Text.Trim();
                    if (MaterialCheckBox.IsChecked == true) info.Material = MaterialTextBox.Text.Trim();
                    if (ExtraLengthCheckBox.IsChecked == true && double.TryParse(ExtraLengthTextBox.Text, out double el)) info.ExtraLength = el;
                    if (ExtraWidthCheckBox.IsChecked == true && double.TryParse(ExtraWidthTextBox.Text, out double ew)) info.ExtraWidth = ew;
                    if (ExtraHeightCheckBox.IsChecked == true && double.TryParse(ExtraHeightTextBox.Text, out double eh)) info.ExtraHeight = eh;
                    if (EdgeTopCheckBox.IsChecked == true) info.EdgeTop = EdgeTopTextBox.Text?.Trim() ?? "";
                    if (EdgeBottomCheckBox.IsChecked == true) info.EdgeBottom = EdgeBottomTextBox.Text?.Trim() ?? "";
                    if (EdgeLeftCheckBox.IsChecked == true) info.EdgeLeft = EdgeLeftTextBox.Text?.Trim() ?? "";
                    if (EdgeRightCheckBox.IsChecked == true) info.EdgeRight = EdgeRightTextBox.Text?.Trim() ?? "";
                    if (PanelShapeCheckBox.IsChecked == true && PanelShapeComboBox.SelectedIndex > 0)
                    {
                        var st = (PanelShapeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                        info.CalculationType = BatchEditRules.BatchShapeTextToCalcType(st);
                    }
                    if (PaintCheckBox.IsChecked == true) info.Paint = PaintTextBox.Text?.Trim() ?? "";
                    if (RemarksCheckBox.IsChecked == true) info.Remarks = RemarksTextBox.Text?.Trim() ?? "";

                    PanelInfoService.SavePanelInfo(ent, info, tr);
                    success++;
                }
                catch { }
            }
            tr.Commit();
            return success;
        }
    }

    public class BatchEditItem
    {
        public bool IsSelected { get; set; } = true;
        public string EntityId { get; set; } = "";
        public string Handle { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string CabinetId { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string Material { get; set; } = "";
        public string PanelName { get; set; } = "";
        public double Length { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double ExtraLength { get; set; }
        public double ExtraWidth { get; set; }
        public double ExtraHeight { get; set; }
        public string EdgeBanding { get; set; } = "";
        public string EdgeTop { get; set; } = "";
        public string EdgeBottom { get; set; } = "";
        public string EdgeLeft { get; set; } = "";
        public string EdgeRight { get; set; } = "";
        public string Paint { get; set; } = "";
        public string Remarks { get; set; } = "";
        public bool IsArcPanel { get; set; }
    }
}
