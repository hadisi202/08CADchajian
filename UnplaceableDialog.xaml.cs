using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;

namespace FurniturePlugin
{
    public partial class UnplaceableDialog : Window
    {
        public bool SkipAndContinue { get; private set; }
        public bool GoBack { get; private set; }

        private readonly ObservableCollection<UnplaceableItem> _precheckItems;
        private readonly ObservableCollection<UnplaceableItem> _unplacedItems;
        private readonly ICollectionView _precheckView;
        private readonly ICollectionView _unplacedView;
        private readonly bool _isPrecheckOnly;
        private HashSet<ObjectId> _highlightedEntityIds = new();

        public UnplaceableDialog(List<UnplaceableItem> items)
            : this(items, null, true)
        {
        }

        public UnplaceableDialog(List<UnplaceableItem> precheckItems, List<UnplaceableItem> unplacedItems, bool defaultToPrecheck = false)
        {
            InitializeComponent();

            _precheckItems = new ObservableCollection<UnplaceableItem>(
                PrepareItems(precheckItems, "数据预检", "超出当前大板规格"));
            _unplacedItems = new ObservableCollection<UnplaceableItem>(
                PrepareItems(unplacedItems, "排不进板件", "尺寸超出大板或张数限制"));

            _isPrecheckOnly = _precheckItems.Count > 0 && _unplacedItems.Count == 0;

            _precheckView = CollectionViewSource.GetDefaultView(_precheckItems);
            _unplacedView = CollectionViewSource.GetDefaultView(_unplacedItems);
            _precheckView.Filter = FilterItem;
            _unplacedView.Filter = FilterItem;

            PrecheckGrid.ItemsSource = _precheckView;
            UnplacedGrid.ItemsSource = _unplacedView;

            InitializeFilters();
            ConfigureLayout(defaultToPrecheck);
            RefreshUiState();

            Closed += OnWindowClosed;
        }

        private static List<UnplaceableItem> PrepareItems(IEnumerable<UnplaceableItem> items, string category, string defaultReason)
        {
            var source = items ?? Enumerable.Empty<UnplaceableItem>();
            var list = source.Select(item => new UnplaceableItem
            {
                Handle = item?.Handle ?? "",
                OrderId = item?.OrderId ?? "",
                CabinetId = item?.CabinetId ?? "",
                Material = item?.Material ?? "",
                Name = item?.Name ?? "",
                Length = item?.Length ?? 0,
                Width = item?.Width ?? 0,
                Thickness = item?.Thickness ?? 0,
                PartType = item?.PartType ?? "",
                Reason = string.IsNullOrWhiteSpace(item?.Reason) ? defaultReason : item.Reason,
                Category = string.IsNullOrWhiteSpace(item?.Category) ? category : item.Category
            }).ToList();

            for (int i = 0; i < list.Count; i++)
                list[i].Index = i + 1;

            return list;
        }

        private void ConfigureLayout(bool defaultToPrecheck)
        {
            bool hasPrecheck = _precheckItems.Count > 0;
            bool hasUnplaced = _unplacedItems.Count > 0;

            PrecheckTab.Visibility = hasPrecheck ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            UnplacedTab.Visibility = hasUnplaced ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            if (hasPrecheck && (defaultToPrecheck || !hasUnplaced))
                ResultsTabControl.SelectedItem = PrecheckTab;
            else if (hasUnplaced)
                ResultsTabControl.SelectedItem = UnplacedTab;

            if (_precheckItems.Count == 0 && _unplacedItems.Count == 0)
            {
                SkipButton.Visibility = System.Windows.Visibility.Collapsed;
                GoBackButton.Visibility = System.Windows.Visibility.Collapsed;
                CancelButton.Content = "关闭";
                DialogIntroText.Text = "当前没有需要处理的数据预检结果或排不进板件，所有板件均可继续流转。";
                ActionHintText.Text = "你可以直接关闭窗口，或返回排版流程继续操作。";
            }
            else if (_isPrecheckOnly)
            {
                Title = "排版数据预检";
                DialogIntroText.Text = "以下板件在当前大板规格下预检不可排入。你可以返回上一步调整大板规格，也可以忽略这些板件继续排版。";
                ActionHintText.Text = "建议先检查大板规格、板件尺寸与材质配置；如确认这些板件本次不参与排版，可直接忽略继续。";
                GoBackButton.Content = "返回调整大板";
                SkipButton.Content = "忽略预检项并继续排版";
                CancelButton.Content = "取消排版";
            }
            else
            {
                Title = "排版结果检查";
                DialogIntroText.Text = "窗口已整合数据预检结果与最终排不进板件，方便统一筛选、定位和决策。";
                ActionHintText.Text = "可先查看数据预检结果，再切换到排不进板件页核对最终未排入项；选中表格行可在 CAD 中联动定位。";
                GoBackButton.Content = "返回上一步";
                SkipButton.Content = "忽略这些板件并继续";
                CancelButton.Content = "取消排版，返回修改数据";
            }
        }

        private void InitializeFilters()
        {
            KeywordTextBox.Text = "";
            PopulateCombo(MaterialFilterComboBox, "全部材质",
                _precheckItems.Concat(_unplacedItems)
                    .Select(i => i.Material)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            PopulateCombo(PartTypeFilterComboBox, "全部类型",
                _precheckItems.Concat(_unplacedItems)
                    .Select(i => i.PartType)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            PopulateCombo(ReasonFilterComboBox, "全部原因",
                _precheckItems.Concat(_unplacedItems)
                    .Select(i => i.Reason)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        }

        private static void PopulateCombo(ComboBox comboBox, string defaultLabel, IEnumerable<string> values)
        {
            if (comboBox == null) return;
            var items = new List<string> { defaultLabel };
            items.AddRange(values ?? Enumerable.Empty<string>());
            comboBox.ItemsSource = items;
            comboBox.SelectedIndex = 0;
        }

        private bool FilterItem(object obj)
        {
            if (obj is not UnplaceableItem item)
                return false;

            string keyword = KeywordTextBox?.Text?.Trim() ?? "";
            string material = MaterialFilterComboBox?.SelectedItem as string ?? "";
            string partType = PartTypeFilterComboBox?.SelectedItem as string ?? "";
            string reason = ReasonFilterComboBox?.SelectedItem as string ?? "";

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string haystack = string.Join(" ", new[]
                {
                    item.Handle, item.OrderId, item.CabinetId, item.Material, item.Name, item.PartType, item.Reason, item.SizeText
                });
                if (haystack.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(material) && !material.StartsWith("全部", StringComparison.Ordinal) &&
                !string.Equals(item.Material, material, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(partType) && !partType.StartsWith("全部", StringComparison.Ordinal) &&
                !string.Equals(item.PartType, partType, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(reason) && !reason.StartsWith("全部", StringComparison.Ordinal) &&
                !string.Equals(item.Reason, reason, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private void RefreshUiState()
        {
            _precheckView.Refresh();
            _unplacedView.Refresh();

            int precheckVisible = CountVisible(_precheckView);
            int unplacedVisible = CountVisible(_unplacedView);
            int totalVisible = precheckVisible + unplacedVisible;

            TotalCountText.Text = (_precheckItems.Count + _unplacedItems.Count).ToString(CultureInfo.InvariantCulture);
            PrecheckCountText.Text = _precheckItems.Count.ToString(CultureInfo.InvariantCulture);
            UnplacedCountText.Text = _unplacedItems.Count.ToString(CultureInfo.InvariantCulture);

            PrecheckTab.Header = $"数据预检 ({precheckVisible}/{_precheckItems.Count})";
            UnplacedTab.Header = $"排不进板件 ({unplacedVisible}/{_unplacedItems.Count})";

            PrecheckEmptyHint.Visibility = precheckVisible == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            UnplacedEmptyHint.Visibility = unplacedVisible == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            ActiveSectionText.Text = GetActiveSectionTitle();
            FilterSummaryText.Text = $"当前筛选后共 {totalVisible} 项，数据预检 {precheckVisible} 项，排不进板件 {unplacedVisible} 项。";

            UpdateCadHighlightsFromUiSelection();
        }

        private static int CountVisible(ICollectionView view)
        {
            if (view == null) return 0;
            int count = 0;
            foreach (var _ in view) count++;
            return count;
        }

        private string GetActiveSectionTitle()
        {
            if (ResultsTabControl?.SelectedItem == PrecheckTab)
                return "数据预检";
            if (ResultsTabControl?.SelectedItem == UnplacedTab)
                return "排不进板件";
            return "结果列表";
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            ClearCadHighlights();
        }

        private void GoBackButton_Click(object sender, RoutedEventArgs e)
        {
            ClearCadHighlights();
            GoBack = true;
            SkipAndContinue = false;
            DialogResult = true;
            Close();
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            ClearCadHighlights();
            SkipAndContinue = true;
            GoBack = false;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            ClearCadHighlights();
            SkipAndContinue = false;
            GoBack = false;
            DialogResult = false;
            Close();
        }

        private void FilterControl_Changed(object sender, RoutedEventArgs e)
        {
            RefreshUiState();
        }

        private void ResetFilterButton_Click(object sender, RoutedEventArgs e)
        {
            KeywordTextBox.Text = "";
            MaterialFilterComboBox.SelectedIndex = 0;
            PartTypeFilterComboBox.SelectedIndex = 0;
            ReasonFilterComboBox.SelectedIndex = 0;
            RefreshUiState();
        }

        private void ResultsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, ResultsTabControl))
                return;
            if (!IsLoaded) return;
            ActiveSectionText.Text = GetActiveSectionTitle();
            UpdateCadHighlightsFromUiSelection();
        }

        private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCadHighlightsFromUiSelection();
        }

        private DataGrid GetActiveGrid()
        {
            if (ResultsTabControl?.SelectedItem == PrecheckTab && PrecheckTab.Visibility == System.Windows.Visibility.Visible)
                return PrecheckGrid;
            if (ResultsTabControl?.SelectedItem == UnplacedTab && UnplacedTab.Visibility == System.Windows.Visibility.Visible)
                return UnplacedGrid;
            return PrecheckTab.Visibility == System.Windows.Visibility.Visible ? PrecheckGrid : UnplacedGrid;
        }

        private void UpdateCadHighlightsFromUiSelection()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var desired = new HashSet<ObjectId>();

            var grid = GetActiveGrid();
            if (grid?.SelectedItems != null)
            {
                foreach (var obj in grid.SelectedItems)
                {
                    if (obj is UnplaceableItem item &&
                        TryResolveObjectIdFromHandle(db, item.Handle, out var id))
                    {
                        desired.Add(id);
                    }
                }
            }

            ApplyCadSelectionAndHighlight(doc, db, desired);
        }

        private void ApplyCadSelectionAndHighlight(AcadDocument doc, Database db, HashSet<ObjectId> desired)
        {
            if (desired == null) return;
            if (_highlightedEntityIds.SetEquals(desired)) return;

            _highlightedEntityIds = new HashSet<ObjectId>(desired);

            try
            {
                if (desired.Count > 0)
                {
                    doc.Editor.SetImpliedSelection(desired.ToArray());
                    if (HighlightOnlyCheckBox?.IsChecked != true)
                        ZoomToEntities(doc, db, desired);
                }
                else
                {
                    doc.Editor.SetImpliedSelection(Array.Empty<ObjectId>());
                }
                doc.Editor.Regen();
            }
            catch
            {
            }
        }

        private void ZoomToEntities(AcadDocument doc, Database db, HashSet<ObjectId> ids)
        {
            try
            {
                Extents3d? combined = null;
                using var tr = db.TransactionManager.StartTransaction();
                foreach (var id in ids)
                {
                    try
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (ent == null) continue;
                        var ext = ent.GeometricExtents;
                        if (combined == null)
                        {
                            combined = ext;
                        }
                        else
                        {
                            combined = new Extents3d(
                                new Point3d(
                                    Math.Min(combined.Value.MinPoint.X, ext.MinPoint.X),
                                    Math.Min(combined.Value.MinPoint.Y, ext.MinPoint.Y),
                                    Math.Min(combined.Value.MinPoint.Z, ext.MinPoint.Z)),
                                new Point3d(
                                    Math.Max(combined.Value.MaxPoint.X, ext.MaxPoint.X),
                                    Math.Max(combined.Value.MaxPoint.Y, ext.MaxPoint.Y),
                                    Math.Max(combined.Value.MaxPoint.Z, ext.MaxPoint.Z)));
                        }
                    }
                    catch
                    {
                    }
                }
                tr.Commit();

                if (!combined.HasValue)
                    return;

                var view = doc.Editor.GetCurrentView();
                var extents = combined.Value;
                var wcs2dcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                wcs2dcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcs2dcs;
                wcs2dcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcs2dcs;
                wcs2dcs = wcs2dcs.Inverse();
                extents.TransformBy(wcs2dcs);

                double width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
                double height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
                if (width < 1e-6) width = 1.0;
                if (height < 1e-6) height = 1.0;

                view.CenterPoint = new Point2d(
                    (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);
                view.Width = width * 1.2;
                view.Height = height * 1.2;
                doc.Editor.SetCurrentView(view);
            }
            catch
            {
            }
        }

        private void ClearCadHighlights()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            _highlightedEntityIds.Clear();
            try { doc.Editor.SetImpliedSelection(Array.Empty<ObjectId>()); } catch { }
        }

        private static bool TryResolveObjectIdFromHandle(Database db, string handleText, out ObjectId entityId)
        {
            entityId = ObjectId.Null;
            if (db == null || string.IsNullOrWhiteSpace(handleText))
                return false;

            if (long.TryParse(handleText, out var decimalValue) &&
                db.TryGetObjectId(new Handle(decimalValue), out entityId))
            {
                return true;
            }

            string text = handleText.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
                return false;

            return db.TryGetObjectId(new Handle(hexValue), out entityId);
        }
    }

    public class UnplaceableItem
    {
        public int Index { get; set; }
        public string Handle { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string CabinetId { get; set; } = "";
        public string Material { get; set; } = "";
        public string Name { get; set; } = "";
        public double Length { get; set; }
        public double Width { get; set; }
        public double Thickness { get; set; }
        public string PartType { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Category { get; set; } = "";
        public string SizeText => $"{Length:F1}×{Width:F1}×{Thickness:F1}";
    }
}
