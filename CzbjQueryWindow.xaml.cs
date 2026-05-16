using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Microsoft.Win32;
using Exception = System.Exception;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace FurniturePlugin
{
    public class CzbjDisplayItem
    {
        public ObjectId EntityObjectId { get; set; }
        public string EntityId { get; set; }
        public string PanelName { get; set; }
        public string OrderId { get; set; }
        public string CabinetId { get; set; }
        public string RoomId { get; set; }
        public string Material { get; set; }
        public string CalculationTypeName { get; set; }
        public double Length { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Remarks { get; set; }
        public PanelInfo SourceInfo { get; set; }
    }

    public partial class CzbjQueryWindow : Window
    {
        private readonly List<CzbjDisplayItem> _allItems = new List<CzbjDisplayItem>();
        private List<CzbjDisplayItem> _filteredItems = new List<CzbjDisplayItem>();
        private int _currentPage = 1;
        private int _pageSize = 50;
        private int _totalPages = 1;
        private System.Windows.Threading.DispatcherTimer _debounceTimer;
        private bool _isFilterPanelExpanded = true;
        private CzbjDisplayItem _lastSelectedItem;

        public CzbjQueryWindow()
        {
            InitializeComponent();
            InitDebounce();
            Loaded += OnLoaded;
        }

        private Editor Ed => Application.DocumentManager.MdiActiveDocument?.Editor;

        private void InitDebounce()
        {
            _debounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                ApplyFilterInternal();
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadAllPanels();
            UpdateActiveFilterCount();
            IdSearchBox.Focus();
        }

        private void LoadAllPanels()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;

            _allItems.Clear();

            using var tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (ent is not Solid3d) continue;
                if (!PanelInfoService.HasPanelInfo(ent, tr)) continue;

                var info = PanelInfoService.GetPanelInfo(ent, tr);
                if (info == null) continue;

                var item = new CzbjDisplayItem
                {
                    EntityObjectId = id,
                    EntityId = info.EntityId ?? id.Handle.Value.ToString(),
                    PanelName = info.PanelName ?? "",
                    OrderId = info.OrderId ?? "",
                    CabinetId = info.CabinetId ?? "",
                    RoomId = info.RoomId ?? "",
                    Material = info.Material ?? "",
                    CalculationTypeName = info.CalculationType.ToString(),
                    Length = info.Length,
                    Width = info.Width,
                    Height = info.Height,
                    Remarks = info.Remarks ?? "",
                    SourceInfo = info
                };
                _allItems.Add(item);
            }

            tr.Commit();
            _filteredItems = _allItems.ToList();
            _currentPage = 1;
            RenderPage();
        }

        private void IdSearchBtn_Click(object sender, RoutedEventArgs e)
        {
            PerformIdSearch();
        }

        private void ClearIdBtn_Click(object sender, RoutedEventArgs e)
        {
            IdSearchBox.Text = "";
            IdSearchBox.Focus();
        }

        private void PerformIdSearch()
        {
            var input = IdSearchBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(input)) return;

            if (!TryParseHandleInput(input, out var handle))
            {
                System.Windows.MessageBox.Show(
                    "ID格式无效，请输入有效的十进制、十六进制(如0x1a2b)或带括号(如(12345))格式。",
                    "格式错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            if (!db.TryGetObjectId(handle, out var objId))
            {
                System.Windows.MessageBox.Show("未找到该实体。", "查询结果",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var ent = tr.GetObject(objId, OpenMode.ForRead, false) as Entity;
            if (ent == null || ent.IsErased)
            {
                System.Windows.MessageBox.Show("实体不存在或已删除。", "查询结果",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var info = PanelInfoService.GetPanelInfo(ent, tr);

            ed.SetImpliedSelection(new[] { objId });
            ZoomToSelection(db, ed, new[] { objId });

            if (info == null)
            {
                ed.WriteMessage("\n找到实体，但没有板件扩展字典信息。");
                ed.WriteMessage("\n已选中并定位到该实体。");
                tr.Commit();
                return;
            }

            ed.WriteMessage($"\n找到板件: {SafeText(info.PanelName)}");
            ed.WriteMessage($"\n订单号: {SafeText(info.OrderId)}");
            ed.WriteMessage($"\n柜号: {SafeText(info.CabinetId)}");
            ed.WriteMessage($"\n房间: {SafeText(info.RoomId)}");
            ed.WriteMessage($"\n材质: {SafeText(info.Material)}");
            ed.WriteMessage($"\n尺寸: {info.Length:F1}×{info.Width:F1}×{info.Height:F1}mm");
            ed.WriteMessage($"\n备注: {SafeText(info.Remarks)}");
            ed.WriteMessage("\n已选中并定位到该实体。");
            tr.Commit();
        }

        private void FilterValueBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void FilterValueBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _debounceTimer.Stop();
                ApplyFilterInternal();
                e.Handled = true;
            }
        }

        private void DoFilterBtn_Click(object sender, RoutedEventArgs e)
        {
            _debounceTimer.Stop();
            ApplyFilterInternal();
        }

        private void QuickFilterTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                if (tag.Length == 1)
                {
                    FieldComboBox.SelectedIndex = Array.FindIndex(new[]
                    {
                        "A", "O", "C", "R", "N", "M", "B", "E"
                    }, f => f == tag);
                }
                else if (tag.Contains(":"))
                {
                    var parts = tag.Split(':');
                    FieldComboBox.SelectedIndex = Array.FindIndex(new[]
                    {
                        "A", "O", "C", "R", "N", "M", "B", "E"
                    }, f => f == parts[0]);
                    FilterValueBox.Text = parts[1];
                }
                ApplyFilterInternal();
            }
        }

        private void ClearAllFiltersBtn_Click(object sender, RoutedEventArgs e)
        {
            FieldComboBox.SelectedIndex = 7;
            MatchModeComboBox.SelectedIndex = 0;
            FilterValueBox.Text = "";
            ApplyFilterInternal();
        }

        private void ToggleFilterBtn_Click(object sender, RoutedEventArgs e)
        {
            _isFilterPanelExpanded = !_isFilterPanelExpanded;
            FilterPanelContent.Visibility = _isFilterPanelExpanded
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            ToggleFilterBtn.Content = _isFilterPanelExpanded ? "收起 ▲" : "展开 ▼";
        }

        private void ApplyFilterInternal()
        {
            var fieldItem = FieldComboBox.SelectedItem as ComboBoxItem;
            var field = fieldItem?.Tag?.ToString() ?? "A";
            var matchItem = MatchModeComboBox.SelectedItem as ComboBoxItem;
            var matchMode = matchItem?.Tag?.ToString() ?? "P";
            var keyword = FilterValueBox.Text?.Trim() ?? "";
            bool exact = matchMode == "X";

            if (field == "A" && string.IsNullOrEmpty(keyword))
            {
                _filteredItems = _allItems.ToList();
            }
            else
            {
                _filteredItems = _allItems
                    .Where(item => IsMatchByField(item, field, keyword, exact))
                    .ToList();
            }

            _currentPage = 1;
            UpdateActiveFilterCount();
            RenderPage();
        }

        private bool IsMatchByField(CzbjDisplayItem item, string field, string keyword, bool exact)
        {
            if (string.IsNullOrEmpty(keyword)) return true;
            bool Hit(string s) => exact
                ? string.Equals((s ?? string.Empty).Trim(), keyword, StringComparison.OrdinalIgnoreCase)
                : (s ?? string.Empty).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

            return field switch
            {
                "O" => Hit(item.OrderId),
                "C" => Hit(item.CabinetId),
                "R" => Hit(item.RoomId),
                "N" => Hit(item.PanelName),
                "M" => Hit(item.Material),
                "B" => Hit(item.Remarks),
                "E" => Hit(item.EntityId),
                _ => Hit(item.OrderId) || Hit(item.CabinetId) || Hit(item.RoomId) ||
                     Hit(item.PanelName) || Hit(item.Material) || Hit(item.Remarks) || Hit(item.EntityId)
            };
        }

        private void UpdateActiveFilterCount()
        {
            var fieldItem = FieldComboBox.SelectedItem as ComboBoxItem;
            var field = fieldItem?.Tag?.ToString() ?? "A";
            var keyword = FilterValueBox.Text?.Trim() ?? "";

            int active = 0;
            if (!string.IsNullOrEmpty(keyword)) active++;
            if (field != "A") active++;

            ActiveFilterCountText.Text = active > 0 ? $"已选 {active} 个筛选条件" : "";
        }

        private void RenderPage()
        {
            if (_pageSize <= 0) _pageSize = 50;
            _totalPages = Math.Max(1, (int)Math.Ceiling((double)_filteredItems.Count / _pageSize));
            if (_currentPage > _totalPages) _currentPage = _totalPages;
            if (_currentPage < 1) _currentPage = 1;

            var pageItems = _filteredItems
                .Skip((_currentPage - 1) * _pageSize)
                .Take(_pageSize)
                .ToList();

            ResultGrid.ItemsSource = pageItems;
            PageInfoText.Text = $"第 {_currentPage} / {_totalPages} 页";
            ResultCountText.Text = $"共找到 {_filteredItems.Count} 个板件";

            EmptyStatePanel.Visibility = _filteredItems.Count == 0
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            ResultGrid.Visibility = _filteredItems.Count == 0
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;

            PrevPageBtn.IsEnabled = _currentPage > 1;
            NextPageBtn.IsEnabled = _currentPage < _totalPages;
        }

        private void PrevPageBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1) { _currentPage--; RenderPage(); }
        }

        private void NextPageBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages) { _currentPage++; RenderPage(); }
        }

        private void PageSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageSizeComboBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                if (int.TryParse(item.Tag.ToString(), out var newSize) && newSize > 0)
                {
                    _pageSize = newSize;
                    _currentPage = 1;
                    RenderPage();
                }
            }
        }

        private void ResultGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultGrid.SelectedItem is CzbjDisplayItem item)
            {
                LocateItem(item);
                e.Handled = true;
            }
        }

        private void ResultGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ResultGrid.SelectedItem is CzbjDisplayItem item)
            {
                LocateItem(item);
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ResultGrid.SelectAll();
                e.Handled = true;
            }
        }

        private void ResultGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _lastSelectedItem = ResultGrid.SelectedItem as CzbjDisplayItem;
        }

        private void LocateBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_lastSelectedItem != null)
                LocateItem(_lastSelectedItem);
            else
                System.Windows.MessageBox.Show("请先在列表中选择一个板件。", "提示",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        private void LocateItem(CzbjDisplayItem item)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            ed.SetImpliedSelection(new[] { item.EntityObjectId });
            ZoomToSelection(db, ed, new[] { item.EntityObjectId });
        }

        private void EditBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_lastSelectedItem != null)
                OpenEditWindow(_lastSelectedItem);
            else
                System.Windows.MessageBox.Show("请先在列表中选择一个板件。", "提示",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        private void OpenEditWindow(CzbjDisplayItem item)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;

            using var tr = db.TransactionManager.StartTransaction();
            var ent = tr.GetObject(item.EntityObjectId, OpenMode.ForRead, false) as Entity;
            if (ent == null || ent.IsErased)
            {
                System.Windows.MessageBox.Show("板件实体不存在或已删除。", "错误",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }
            tr.Commit();

            var win = new PanelInfoWindow(item.SourceInfo, item.EntityObjectId, false);
            win.ShowInTaskbar = false;
            win.Owner = this;
            win.Show();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            LoadAllPanels();
            ApplyFilterInternal();
        }

        private void ExportBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_filteredItems.Count == 0)
            {
                System.Windows.MessageBox.Show("没有可导出的数据。", "提示",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV文件(*.csv)|*.csv|所有文件(*.*)|*.*",
                DefaultExt = "csv",
                FileName = $"板件查询结果_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var lines = new List<string>
                    {
                        "模型ID,板件名称,订单号,柜号,房间,材质,计算类型,长(mm),宽(mm),厚(mm),备注"
                    };

                    foreach (var item in _filteredItems)
                    {
                        lines.Add($"\"{item.EntityId}\",\"{item.PanelName}\",\"{item.OrderId}\",\"{item.CabinetId}\"," +
                                  $"\"{item.RoomId}\",\"{item.Material}\",\"{item.CalculationTypeName}\"," +
                                  $"{item.Length:F1},{item.Width:F1},{item.Height:F1},\"{item.Remarks}\"");
                    }

                    System.IO.File.WriteAllLines(dialog.FileName, lines,
                        new System.Text.UTF8Encoding(true));
                    System.Windows.MessageBox.Show(
                        $"导出成功，共 {_filteredItems.Count} 条记录。\n保存位置: {dialog.FileName}",
                        "导出完成", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"导出失败: {ex.Message}", "错误",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private bool TryParseHandleInput(string input, out Handle handle)
        {
            handle = default;
            if (string.IsNullOrWhiteSpace(input)) return false;

            string s = input.Trim();
            if (s.StartsWith("(") && s.EndsWith(")") && s.Length > 2)
                s = s.Substring(1, s.Length - 2).Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);

            if (long.TryParse(s, out long dec))
            {
                handle = new Handle(dec);
                return true;
            }
            try
            {
                long hex = Convert.ToInt64(s, 16);
                handle = new Handle(hex);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ZoomToSelection(Database db, Editor ed, IEnumerable<ObjectId> ids)
        {
            if (db == null || ed == null || ids == null) return;

            bool has = false;
            Extents3d combined = default;
            using var tr = db.TransactionManager.StartTransaction();
            foreach (var id in ids)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || ent.IsErased) continue;
                try
                {
                    var ge = ent.GeometricExtents;
                    if (!has) { combined = ge; has = true; }
                    else combined.AddExtents(ge);
                }
                catch { }
            }
            tr.Commit();
            if (!has) return;

            try
            {
                var view = ed.GetCurrentView();
                try
                {
                    var wcs2dcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                    wcs2dcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcs2dcs;
                    wcs2dcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcs2dcs;
                    wcs2dcs = wcs2dcs.Inverse();
                    var ext = combined;
                    ext.TransformBy(wcs2dcs);

                    double width = Math.Abs(ext.MaxPoint.X - ext.MinPoint.X);
                    double height = Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y);
                    if (width < 1e-6) width = 1.0;
                    if (height < 1e-6) height = 1.0;
                    double marginFactor = 1.2;

                    view.CenterPoint = new Point2d(
                        (ext.MinPoint.X + ext.MaxPoint.X) * 0.5,
                        (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5);
                    view.Width = width * marginFactor;
                    view.Height = height * marginFactor;
                    ed.SetCurrentView(view);
                }
                finally
                {
                    view.Dispose();
                }
            }
            catch { }
        }

        private static string SafeText(string s) => s ?? "(空)";
    }
}
