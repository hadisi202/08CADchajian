using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Data;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    public partial class PanelInfoListWindow : Window
    {
        private const string BlankFilterDisplay = "（空白）";
        private ObservableCollection<PanelInfoListItem> _panelInfoList;
        private List<ObjectId> _selectedEntityIds;
        private HashSet<ObjectId> _highlightedEntityIds = new HashSet<ObjectId>();
        private readonly Dictionary<ObjectId, (short ColorIndex, Color Color)> _highlightedOriginals = new Dictionary<ObjectId, (short, Color)>();
        private readonly ObservableCollection<FilterOptionItem> _headerFilterOptions = new ObservableCollection<FilterOptionItem>();
        private readonly Dictionary<string, ColumnFilterState> _columnFilters = new Dictionary<string, ColumnFilterState>(StringComparer.OrdinalIgnoreCase);
        private ICollectionView _panelInfoView;
        private ICollectionView _headerFilterOptionsView;
        private string _activeFilterPropertyPath = "";
        private string _currentSortPropertyPath = "";
        private ListSortDirection? _currentSortDirection;
        
        public PanelInfoListWindow(List<ObjectId> entityIds)
        {
            InitializeComponent();
            ShowActivated = false;
            Topmost = false;
            _selectedEntityIds = entityIds ?? new List<ObjectId>();
            _panelInfoList = new ObservableCollection<PanelInfoListItem>();
            _panelInfoView = CollectionViewSource.GetDefaultView(_panelInfoList);
            _panelInfoView.Filter = PanelInfoListFilter;
            PanelInfoDataGrid.ItemsSource = _panelInfoView;
            _headerFilterOptionsView = CollectionViewSource.GetDefaultView(_headerFilterOptions);
            HeaderFilterItemsControl.ItemsSource = _headerFilterOptionsView;
            _panelInfoList.CollectionChanged += PanelInfoList_CollectionChanged;
            
            LoadPanelInfoList();
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                ClearCadHighlights();
            }
            catch
            {
            }
            base.OnClosed(e);
        }

        private void PanelInfoList_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var it in e.OldItems)
                {
                    if (it is PanelInfoListItem item)
                        item.PropertyChanged -= PanelInfoListItem_PropertyChanged;
                }
            }
            if (e.NewItems != null)
            {
                foreach (var it in e.NewItems)
                {
                    if (it is PanelInfoListItem item)
                        item.PropertyChanged += PanelInfoListItem_PropertyChanged;
                }
            }
        }

        private void PanelInfoListItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PanelInfoListItem.IsSelected))
            {
                Dispatcher.BeginInvoke(new Action(UpdateCadHighlightsFromUiSelection));
                return;
            }

            Dispatcher.BeginInvoke(new Action(ApplyAllFiltersAndSort));
        }

        private void PanelInfoDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCadHighlightsFromUiSelection();
        }

        private void UpdateCadHighlightsFromUiSelection()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;

            var desired = new HashSet<ObjectId>();
            foreach (var item in _panelInfoList)
            {
                if (!item.IsSelected) continue;
                if (TryResolveObjectIdFromEntityId(db, item.EntityId, out var id))
                    desired.Add(id);
            }

            if (PanelInfoDataGrid.SelectedItems != null)
            {
                foreach (var obj in PanelInfoDataGrid.SelectedItems)
                {
                    if (obj is PanelInfoListItem item)
                    {
                        if (TryResolveObjectIdFromEntityId(db, item.EntityId, out var id))
                            desired.Add(id);
                    }
                }
            }

            ApplyCadSelectionAndHighlight(doc, db, desired);
        }

        private void ApplyCadSelectionAndHighlight(Document doc, Database db, HashSet<ObjectId> desired)
        {
            if (desired == null) return;

            var toUnhighlight = _highlightedEntityIds.Where(id => !desired.Contains(id)).ToList();
            var toHighlight = desired.Where(id => !_highlightedEntityIds.Contains(id)).ToList();

            if (toUnhighlight.Count == 0 && toHighlight.Count == 0 && _highlightedEntityIds.SetEquals(desired))
                return;

            using var tr = db.TransactionManager.StartTransaction();

            for (int i = 0; i < toHighlight.Count; i++)
            {
                try
                {
                    var ent = tr.GetObject(toHighlight[i], OpenMode.ForRead, false) as Entity;
                    if (ent == null) continue;
                    // 只选中，不改变颜色
                }
                catch { }
            }

            tr.Commit();

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
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                }
                doc.Editor.Regen();
            }
            catch
            {
            }
        }

        private void ZoomToEntities(Document doc, Database db, HashSet<ObjectId> ids)
        {
            try
            {
                Extents3d? combined = null;
            using var tr2 = db.TransactionManager.StartTransaction();
            foreach (var id in ids)
            {
                try
                {
                    var ent = tr2.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null) continue;
                    var ext = ent.GeometricExtents;
                    if (combined == null)
                        combined = ext;
                    else
                        combined = new Extents3d(
                            new Point3d(
                                Math.Min(combined.Value.MinPoint.X, ext.MinPoint.X),
                                Math.Min(combined.Value.MinPoint.Y, ext.MinPoint.Y),
                                Math.Min(combined.Value.MinPoint.Z, ext.MinPoint.Z)),
                            new Point3d(
                                Math.Max(combined.Value.MaxPoint.X, ext.MaxPoint.X),
                                Math.Max(combined.Value.MaxPoint.Y, ext.MaxPoint.Y),
                                Math.Max(combined.Value.MaxPoint.Z, ext.MaxPoint.Z))
                        );
                }
                catch
                {
                }
            }
            tr2.Commit();

                if (combined.HasValue)
                {
                    var view = doc.Editor.GetCurrentView();
                    var ext = combined.Value;

                    // 将实体范围从WCS转换到当前视图DCS，避免视图方向/扭转导致缩放跑偏或失效。
                    var wcs2dcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                    wcs2dcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcs2dcs;
                    wcs2dcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcs2dcs;
                    wcs2dcs = wcs2dcs.Inverse();
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
                    doc.Editor.SetCurrentView(view);
                }
            }
            catch
            {
            }
        }

        private void ClearCadHighlights()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            if (_highlightedEntityIds == null || _highlightedEntityIds.Count == 0) return;

            using var tr = db.TransactionManager.StartTransaction();
            foreach (var id in _highlightedEntityIds)
            {
                try
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (ent == null) continue;
                    if (_highlightedOriginals.TryGetValue(id, out var original))
                    {
                        ent.ColorIndex = original.ColorIndex;
                        ent.Color = original.Color;
                    }
                }
                catch
                {
                }
            }
            tr.Commit();

            _highlightedEntityIds.Clear();
            _highlightedOriginals.Clear();
            try { doc.Editor.Regen(); } catch { }
        }

        private static bool TryResolveObjectIdFromEntityId(Database db, string entityIdText, out ObjectId entityId)
        {
            entityId = ObjectId.Null;
            if (db == null) return false;
            if (string.IsNullOrWhiteSpace(entityIdText)) return false;

            // 兼容十进制与十六进制句柄文本。
            if (long.TryParse(entityIdText, out var decimalValue))
            {
                if (db.TryGetObjectId(new Autodesk.AutoCAD.DatabaseServices.Handle(decimalValue), out entityId))
                    return true;
            }

            var text = entityIdText.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
                return false;
            return db.TryGetObjectId(new Autodesk.AutoCAD.DatabaseServices.Handle(hexValue), out entityId);
        }
        
        private void UpdateStatistics()
        {
            try
            {
                var visibleItems = GetVisibleItems();

                // 计算板件总数
                int totalPanelCount = visibleItems.Count;
                int validCount = visibleItems.Count(item => string.Equals(item.Status, "有信息", StringComparison.OrdinalIgnoreCase));
                int emptyCount = visibleItems.Count(item => !string.Equals(item.Status, "有信息", StringComparison.OrdinalIgnoreCase));
                
                // 计算总面积（平方米）
                double totalArea = visibleItems.Sum(item => item.Area);
                
                // 计算封边总长度（毫米）
                double totalEdgeLength = 0;
                foreach (var item in visibleItems)
                {
                    // 只计算有封边信息的板件
                    if (!string.IsNullOrWhiteSpace(item.EdgeBanding))
                    {
                        // 计算板件周长：2 * (长度 + 宽度)
                        double perimeter = 2 * (item.ExtraLength + item.ExtraWidth);
                        totalEdgeLength += perimeter;
                    }
                }
                
                // 更新UI显示
                TotalCountTextBlock.Text = totalPanelCount.ToString();
                ValidCountTextBlock.Text = validCount.ToString();
                EmptyCountTextBlock.Text = emptyCount.ToString();
                StatTotalCountTextBlock.Text = totalPanelCount.ToString();
                StatTotalAreaTextBlock.Text = $"{totalArea:F4}";
                // 将封边长度从毫米转换为米，显示小数点后2位
                StatEdgeLengthTextBlock.Text = $"{totalEdgeLength / 1000.0:F2}";
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新统计信息时发生错误: {ex.Message}");
            }
        }
        
        private void LoadPanelInfoList()
        {
            try
            {
                _panelInfoList.Clear();
                
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                using var tr = doc.TransactionManager.StartTransaction();
                int index = 1;
                int validCount = 0;
                int emptyCount = 0;
                var slotMap = BuildPanelSlotMap();
                
                foreach (var entityId in _selectedEntityIds)
                    {
                        try
                        {
                            var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                            if (entity != null)
                            {
                                var savedPanelInfo = GetPanelInfoFromEntity(entity);
                                bool hasSavedInfo = savedPanelInfo != null && !savedPanelInfo.IsEmpty();
                                var panelInfo = savedPanelInfo ?? new PanelInfo();
                                string handleStr = entityId.Handle.Value.ToString();
                                slotMap.TryGetValue(handleStr, out var associatedSlot);

                                if (entity is Solid3d solid)
                                {
                                    bool overwriteDimensions = associatedSlot != null ||
                                                               !panelInfo.IsDimensionLocked ||
                                                               panelInfo.Length <= 0 ||
                                                               panelInfo.Width <= 0 ||
                                                               panelInfo.Height <= 0;
                                    PanelGeometryAnalysisService.ApplyDetectedGeometry(
                                        solid,
                                        panelInfo,
                                        associatedSlot,
                                        overwriteDimensions: overwriteDimensions,
                                        overwriteCalculationType: associatedSlot == null && !panelInfo.IsCalculationTypeManual,
                                        updateCuttingDimensions: overwriteDimensions);
                                }

                                double length = panelInfo.Length;
                                double width = panelInfo.Width;
                                double height = panelInfo.Height;
                                
                                var listItem = new PanelInfoListItem
                                {
                                    Index = index++,
                                    EntityId = entityId.Handle.Value.ToString(),
                                    EntityKindDisplay = GetEntityKindDisplay(panelInfo),
                                    ExcludeFromPaiBanDisplay = GetExcludeFromPaiBanDisplay(panelInfo),
                                    OrderId = panelInfo.OrderId ?? "",
                                    CabinetId = panelInfo.CabinetId ?? "",
                                    RoomId = panelInfo.RoomId ?? "",
                                    PanelName = panelInfo.PanelName ?? "",
                                    Material = panelInfo.Material ?? "",
                                    Length = length,
                                    Width = width,
                                    Height = height,
                                    ExtraLength = panelInfo.ExtraLength,
                                    ExtraWidth = panelInfo.ExtraWidth,
                                    ExtraHeight = panelInfo.ExtraHeight,
                                    CuttingLength = panelInfo.ExtraLength,
                                    CuttingWidth = panelInfo.ExtraWidth,
                                    CuttingHeight = panelInfo.ExtraHeight,
                                    EdgeBanding = panelInfo.EdgeBanding ?? "",
                                    Paint = panelInfo.Paint ?? "",
                                    Remarks = panelInfo.Remarks ?? "",
                                    Quantity = 1,
                                    Area = panelInfo.ExtraLength * panelInfo.ExtraWidth / 1000000.0,
                                    IsLocked = panelInfo.IsDimensionLocked,
                                    // 四边封边
                                    EdgeTop = panelInfo.EdgeTop ?? "",
                                    EdgeBottom = panelInfo.EdgeBottom ?? "",
                                    EdgeLeft = panelInfo.EdgeLeft ?? "",
                                    EdgeRight = panelInfo.EdgeRight ?? "",
                                    // 纹路方向
                                    TextureDirection = panelInfo.TextureDirection.ToString(),
                                    // 形状类型
                                    ShapeType = CalcTypeToShapeName(panelInfo.CalculationType),
                                    HasHardware = panelInfo.HardwareItems?.Count > 0 ? "有" : "",
                                    ArcInnerRadius = panelInfo.ArcInnerRadius,
                                    ArcAngleDegrees = panelInfo.ArcAngleDegrees,
                                    ArcStraightLength1 = panelInfo.ArcStraightLength1,
                                    ArcStraightLength2 = panelInfo.ArcStraightLength2,
                                    UnfoldedLength = panelInfo.UnfoldedLength,
                                    UnfoldedWidth = panelInfo.UnfoldedWidth
                                };

                                if (associatedSlot != null)
                                {
                                    listItem.PanelType = associatedSlot.PanelType;
                                    listItem.CuttingLength = associatedSlot.CuttingLength;
                                    listItem.CuttingWidth = associatedSlot.CuttingWidth;
                                    if (!string.IsNullOrEmpty(associatedSlot.EdgeTop))
                                    {
                                        listItem.EdgeTop = associatedSlot.EdgeTop;
                                        listItem.EdgeBottom = associatedSlot.EdgeBottom;
                                        listItem.EdgeLeft = associatedSlot.EdgeLeft;
                                        listItem.EdgeRight = associatedSlot.EdgeRight;
                                    }
                                    listItem.TextureDirection = associatedSlot.TextureDirection.ToString();
                                }

                                // 如果没有搭积木裁切尺寸，则从PanelInfo的封边推算
                                if (listItem.CuttingLength == 0 && listItem.ExtraLength > 0)
                                {
                                    double edgeT = 0.5; // 默认封边厚度
                                    double cutL = listItem.ExtraLength;
                                    double cutW = listItem.ExtraWidth;
                                    if (!string.IsNullOrEmpty(listItem.EdgeTop)) cutL -= edgeT;
                                    if (!string.IsNullOrEmpty(listItem.EdgeBottom)) cutL -= edgeT;
                                    if (!string.IsNullOrEmpty(listItem.EdgeLeft)) cutW -= edgeT;
                                    if (!string.IsNullOrEmpty(listItem.EdgeRight)) cutW -= edgeT;
                                    listItem.CuttingLength = cutL;
                                    listItem.CuttingWidth = cutW;
                                }
                                
                                // 判断状态
                                if (!hasSavedInfo)
                                {
                                    listItem.Status = "无信息";
                                    emptyCount++;
                                }
                                else
                                {
                                    listItem.Status = "有信息";
                                    validCount++;
                                }
                                
                                _panelInfoList.Add(listItem);
                            }
                        }
                        catch (System.Exception ex)
                        {
                            // 如果某个实体处理失败，添加错误信息
                            var errorItem = new PanelInfoListItem
                            {
                                Index = index++,
                                EntityId = entityId.Handle.Value.ToString(),
                                Status = "错误",
                                Remarks = $"处理失败: {ex.Message}"
                            };
                            _panelInfoList.Add(errorItem);
                            emptyCount++;
                        }
                    }
                    
                    tr.Commit();
                    ApplyAllFiltersAndSort();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"加载板件信息列表时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadPanelInfoList();
        }
        
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                    DefaultExt = "csv",
                    FileName = $"板件信息列表_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    string F1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.InvariantCulture);

                    var csv = new StringBuilder();
                    csv.AppendLine("序号,实体ID,板件类型,实体用途,排版忽略,订单号,柜号,房间号,板件名称,材质,长度(mm),宽度(mm),厚度(mm),裁切长(mm),裁切宽(mm),裁切厚(mm),数量,面积(㎡),封边,纹路,五金,油漆,备注,状态");

                    foreach (var item in _panelInfoList)
                    {
                        csv.AppendLine($"{item.Index},{item.EntityId},{item.ShapeType},{item.EntityKindDisplay},{item.ExcludeFromPaiBanDisplay},{item.OrderId},{item.CabinetId},{item.RoomId},{item.PanelName},{item.Material},{F1(item.Length)},{F1(item.Width)},{F1(item.Height)},{F1(item.CuttingLength)},{F1(item.CuttingWidth)},{F1(item.CuttingHeight)},{F1(item.Quantity)},{F1(item.Area)},{item.EdgeBanding},{item.TextureDirection},{item.HasHardware},{item.Paint},{item.Remarks},{item.Status}");
                    }

                    File.WriteAllText(saveFileDialog.FileName, csv.ToString(), Encoding.UTF8);
                    MessageBox.Show($"导出成功！\n文件保存至: {saveFileDialog.FileName}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportHardwareButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedItems = _panelInfoList.Where(x => x.IsSelected).ToList();
                if (selectedItems.Count == 0)
                {
                    MessageBox.Show("请先勾选需要导出五金的板件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                    DefaultExt = "csv",
                    FileName = $"板件五金_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };
                if (saveFileDialog.ShowDialog() != true)
                {
                    return;
                }

                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    return;
                }

                var csv = new StringBuilder();
                csv.AppendLine("订单号,房间号,柜号,板件名称,实体ID,序号,五金名称,规格,数量,单位,颜色,备注");
                int rowCount = 0;

                using var tr = doc.TransactionManager.StartTransaction();
                foreach (var item in selectedItems)
                {
                    if (!long.TryParse(item.EntityId, out long handleValue))
                    {
                        continue;
                    }

                    var handle = new Handle(handleValue);
                    if (!doc.Database.TryGetObjectId(handle, out ObjectId objectId))
                    {
                        continue;
                    }

                    var entity = tr.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                    if (entity == null)
                    {
                        continue;
                    }

                    var panelInfo = PanelInfoService.GetPanelInfo(entity, tr) ?? new PanelInfo();
                    var hardwareList = panelInfo.HardwareItems ?? new List<PanelHardwareItem>();
                    bool isHardwareEntity = panelInfo.EntityKind == EntityKind.Hardware;

                    if (hardwareList.Count > 0)
                    {
                        foreach (var hw in hardwareList)
                        {
                            csv.AppendLine(string.Join(",",
                                CsvEscape(panelInfo.OrderId),
                                CsvEscape(panelInfo.RoomId),
                                CsvEscape(panelInfo.CabinetId),
                                CsvEscape(panelInfo.PanelName),
                                CsvEscape(item.EntityId),
                                hw.Index.ToString(CultureInfo.InvariantCulture),
                                CsvEscape(hw.Name),
                                CsvEscape(hw.Spec),
                                hw.Quantity.ToString(CultureInfo.InvariantCulture),
                                CsvEscape(hw.Unit),
                                CsvEscape(hw.Color),
                                CsvEscape(hw.Remarks)));
                            rowCount++;
                        }
                        continue;
                    }

                    if (!isHardwareEntity)
                    {
                        continue;
                    }

                    // 独立五金实体：如果没有显式五金明细，则实体本身导出为一条五金记录。
                    csv.AppendLine(string.Join(",",
                        CsvEscape(panelInfo.OrderId),
                        CsvEscape(panelInfo.RoomId),
                        CsvEscape(panelInfo.CabinetId),
                        CsvEscape(panelInfo.PanelName),
                        CsvEscape(item.EntityId),
                        "1",
                        CsvEscape(string.IsNullOrWhiteSpace(panelInfo.PanelName) ? "五金实体" : panelInfo.PanelName),
                        CsvEscape(panelInfo.Material),
                        "1",
                        "件",
                        "",
                        CsvEscape(panelInfo.Remarks)));
                    rowCount++;
                }
                tr.Commit();

                File.WriteAllText(saveFileDialog.FileName, csv.ToString(), Encoding.UTF8);
                MessageBox.Show($"导出完成，五金明细 {rowCount} 条。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"导出五金失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string CsvEscape(string input)
        {
            var value = input ?? "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return value;
        }
        
        // 从实体获取板件信息（委托给 PanelInfoService）
        private PanelInfo GetPanelInfoFromEntity(Entity entity)
        {
            return PanelInfoService.GetPanelInfo(entity);
        }

        private static Dictionary<string, PanelSlot> BuildPanelSlotMap()
        {
            var slotMap = new Dictionary<string, PanelSlot>(StringComparer.OrdinalIgnoreCase);
            try
            {
                CabinetFrameService.LoadAll();
                foreach (var frame in CabinetFrameService.GetAllFrames())
                {
                    foreach (var slot in frame.Panels)
                    {
                        if (!string.IsNullOrEmpty(slot.EntityHandle))
                            slotMap[slot.EntityHandle] = slot;
                    }
                }
            }
            catch
            {
            }

            return slotMap;
        }
        
        private void ExportToCsv(string filePath)
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            writer.WriteLine("序号,实体ID,订单号,柜号,板件名称,材质,长度(mm),宽度(mm),厚度(mm),裁切长(mm),裁切宽(mm),裁切厚(mm),数量,面积(㎡),封边,油漆,备注,状态");
            
            foreach (var item in _panelInfoList)
            {
                var line = $"{item.Index},\"{item.EntityId}\",\"{item.OrderId}\",\"{item.CabinetId}\",\"{item.PanelName}\",\"{item.Material}\",{item.Length:F2},{item.Width:F2},{item.Height:F2},{item.ExtraLength:F2},{item.ExtraWidth:F2},{item.ExtraHeight:F2},{item.Quantity},{item.Area:F4},\"{item.EdgeBanding}\",\"{item.Paint}\",\"{item.Remarks}\",\"{item.Status}\"";
                writer.WriteLine(line);
            }
        }
        
        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _panelInfoList)
            {
                item.IsSelected = true;
            }
            UpdateCadHighlightsFromUiSelection();
        }
        
        private void UnselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _panelInfoList)
            {
                item.IsSelected = false;
            }
            UpdateCadHighlightsFromUiSelection();
        }
        
        private void BatchEditButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = _panelInfoList.Where(item => item.IsSelected).ToList();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("请先选择要修改的板件！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            this.Hide();
            try
            {
                // 转换为BatchEditItem列表
                var batchItems = selectedItems.Select(si => new BatchEditItem
                {
                    IsSelected = true,
                    Handle = si.EntityId,
                    OrderId = si.OrderId,
                    CabinetId = si.CabinetId,
                    Material = si.Material,
                    PanelName = si.PanelName,
                    EdgeTop = si.EdgeTop,
                    EdgeBottom = si.EdgeBottom,
                    EdgeLeft = si.EdgeLeft,
                    EdgeRight = si.EdgeRight,
                    Paint = si.Paint,
                    IsArcPanel = false
                }).ToList();
                var batchEditWindow = new BatchEditWindow(batchItems);
                batchEditWindow.Owner = this;
                if (batchEditWindow.ShowDialog() == true)
                {
                    LoadPanelInfoList();
                }
            }
            finally
            {
                if (!this.IsVisible)
                {
                    this.Show();
                }
                this.Activate();
            }
        }
        
        private void AutoGetSizeButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = _panelInfoList.Where(item => item.IsSelected).ToList();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("请先选择要获取尺寸的板件！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) { MessageBox.Show("没有活动的AutoCAD文档。"); return; }
                var db = doc.Database;

                int successCount = 0;
                int errorCount = 0;
                int fromSlotCount = 0;
                
                var slotMap = BuildPanelSlotMap();
                
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var item in selectedItems)
                    {
                        try
                        {
                            // 检查是否锁定，锁定的板件跳过自动获取尺寸
                            if (item.IsLocked)
                            {
                                continue;
                            }
                            
                            if (long.TryParse(item.EntityId, out long handle))
                            {
                                var handleObj = new Autodesk.AutoCAD.DatabaseServices.Handle(handle);
                                if (db.TryGetObjectId(handleObj, out ObjectId entityId))
                                {
                                    var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                                    if (entity != null && !entity.IsErased)
                                    {
                                        string handleStr = handle.ToString();
                                        slotMap.TryGetValue(handleStr, out var slot);
                                        var panelInfo = GetPanelInfoFromEntity(entity) ?? new PanelInfo();

                                        if (entity is not Solid3d solid)
                                            continue;

                                        if (!string.IsNullOrEmpty(item.EdgeTop) && string.IsNullOrEmpty(panelInfo.EdgeTop))
                                        {
                                            panelInfo.EdgeTop = item.EdgeTop;
                                            panelInfo.EdgeBottom = item.EdgeBottom;
                                            panelInfo.EdgeLeft = item.EdgeLeft;
                                            panelInfo.EdgeRight = item.EdgeRight;
                                        }

                                        PanelGeometryAnalysisService.ApplyDetectedGeometry(
                                            solid,
                                            panelInfo,
                                            slot,
                                            overwriteDimensions: true,
                                            overwriteCalculationType: slot == null && !panelInfo.IsCalculationTypeManual,
                                            updateCuttingDimensions: true);

                                        item._length = panelInfo.Length;
                                        item._width = panelInfo.Width;
                                        item._height = panelInfo.Height;
                                        item._extraLength = panelInfo.ExtraLength;
                                        item._extraWidth = panelInfo.ExtraWidth;
                                        item._extraHeight = panelInfo.ExtraHeight;
                                        item.CuttingLength = panelInfo.ExtraLength;
                                        item.CuttingWidth = panelInfo.ExtraWidth;
                                        item.CuttingHeight = panelInfo.ExtraHeight;
                                        item.ShapeType = CalcTypeToShapeName(panelInfo.CalculationType);
                                        item.ArcInnerRadius = panelInfo.ArcInnerRadius;
                                        item.ArcAngleDegrees = panelInfo.ArcAngleDegrees;
                                        item.ArcStraightLength1 = panelInfo.ArcStraightLength1;
                                        item.ArcStraightLength2 = panelInfo.ArcStraightLength2;
                                        item.UnfoldedLength = panelInfo.UnfoldedLength;
                                        item.UnfoldedWidth = panelInfo.UnfoldedWidth;

                                        if (slot != null)
                                        {
                                            item.PanelType = slot.PanelType;
                                            item.EdgeTop = slot.EdgeTop;
                                            item.EdgeBottom = slot.EdgeBottom;
                                            item.EdgeLeft = slot.EdgeLeft;
                                            item.EdgeRight = slot.EdgeRight;
                                            item.TextureDirection = slot.TextureDirection.ToString();
                                            fromSlotCount++;
                                        }
                                        else if (string.IsNullOrEmpty(item.PanelType) && !string.IsNullOrEmpty(panelInfo.PanelName))
                                        {
                                            item.PanelType = DeterminePanelTypeFromName(panelInfo.PanelName);
                                        }
                                        
                                        // 重新计算面积
                                        item._area = (item._extraLength * item._extraWidth) / 1000000.0;
                                        
                                        // 手动触发属性更改通知
                                        item.OnPropertyChanged(nameof(item.Length));
                                        item.OnPropertyChanged(nameof(item.Width));
                                        item.OnPropertyChanged(nameof(item.Height));
                                        item.OnPropertyChanged(nameof(item.ExtraLength));
                                        item.OnPropertyChanged(nameof(item.ExtraWidth));
                                        item.OnPropertyChanged(nameof(item.ExtraHeight));
                                        item.OnPropertyChanged(nameof(item.Area));
                                        item.OnPropertyChanged(nameof(item.CuttingLength));
                                        item.OnPropertyChanged(nameof(item.CuttingWidth));
                                        item.OnPropertyChanged(nameof(item.CuttingHeight));
                                        item.OnPropertyChanged(nameof(item.PanelType));
                                        item.OnPropertyChanged(nameof(item.ShapeType));
                                        item.OnPropertyChanged(nameof(item.EdgeTop));
                                        item.OnPropertyChanged(nameof(item.EdgeBottom));
                                        item.OnPropertyChanged(nameof(item.EdgeLeft));
                                        item.OnPropertyChanged(nameof(item.EdgeRight));
                                        
                                        // 标记为已修改
                                        item.IsModified = true;
                                        
                                        successCount++;
                                    }
                                }
                            }
                        }
                        catch (System.Exception ex)
                        {
                            errorCount++;
                            System.Diagnostics.Debug.WriteLine($"获取实体 {item.EntityId} 尺寸时发生错误: {ex.Message}");
                        }
                    }
                    
                    tr.Commit();
                }
                
                // 刷新界面显示
                PanelInfoDataGrid.Items.Refresh();
                
                // 更新统计信息
                UpdateStatistics();
                
                string message = $"自动获取尺寸完成！\n成功: {successCount} 个\n来自搭积木: {fromSlotCount} 个\n失败: {errorCount} 个";
                MessageBox.Show(message, "获取结果", MessageBoxButton.OK, 
                    errorCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"获取尺寸失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// 从CalculationType获取形状名称
        /// </summary>
        private static string CalcTypeToShapeName(PanelCalculationType calcType)
        {
            return calcType switch
            {
                PanelCalculationType.AABB => "常规矩形板",
                PanelCalculationType.OBB => "旋转矩形板",
                PanelCalculationType.Irregular => "异形板",
                PanelCalculationType.ArcPanel => "圆弧板件",
                PanelCalculationType.SplinePanel => "样条实体板件",
                _ => "常规矩形板"
            };
        }

        private static PanelCalculationType ShapeNameToCalcType(string shapeType)
        {
            if (string.IsNullOrWhiteSpace(shapeType))
            {
                return PanelCalculationType.AABB;
            }

            if (shapeType.Contains("常规矩形板"))
            {
                return PanelCalculationType.AABB;
            }

            if (shapeType.Contains("样条实体"))
            {
                return PanelCalculationType.SplinePanel;
            }

            if (shapeType.Contains("圆弧板件") || shapeType.Contains("圆弧板"))
            {
                return PanelCalculationType.ArcPanel;
            }

            if (shapeType.Contains("异形板"))
            {
                return PanelCalculationType.Irregular;
            }

            if (shapeType.Contains("旋转矩形板") || shapeType.Contains("旋转过的矩形"))
            {
                return PanelCalculationType.OBB;
            }

            return PanelCalculationType.OBB;
        }

        /// <summary>
        /// 从板件名称推断板件类型
        /// </summary>
        private static string DeterminePanelTypeFromName(string panelName)
        {
            if (string.IsNullOrWhiteSpace(panelName)) return "";
            var type = PanelNumberingService.GetPanelTypeShortName(panelName);
            if (type != "未知" && type != "其他") return type;
            if (panelName.Contains("收口板")) return "收口板";
            if (panelName.Contains("辅助板")) return "辅助板";
            return panelName;
        }

        private static string GetEntityKindDisplay(PanelInfo panelInfo)
        {
            if (panelInfo == null)
                return "板件";

            return panelInfo.EntityKind == EntityKind.Hardware ? "五金" : "板件";
        }

        private static string GetExcludeFromPaiBanDisplay(PanelInfo panelInfo)
        {
            if (panelInfo == null)
                return "否";

            return panelInfo.ExcludeFromPaiBan ? "是" : "否";
        }

        private bool PanelInfoListFilter(object obj)
        {
            return obj is PanelInfoListItem item && PassesAllColumnFilters(item);
        }

        private bool PassesAllColumnFilters(PanelInfoListItem item, string excludedPropertyPath = null)
        {
            if (item == null)
                return false;

            foreach (var entry in _columnFilters)
            {
                if (!entry.Value.IsActive)
                    continue;

                if (!string.IsNullOrWhiteSpace(excludedPropertyPath) &&
                    string.Equals(entry.Key, excludedPropertyPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                string displayValue = GetFilterDisplayValue(item, entry.Key);
                if (!entry.Value.SelectedValues.Contains(displayValue))
                    return false;
            }

            return true;
        }

        private List<PanelInfoListItem> GetVisibleItems()
        {
            if (_panelInfoView == null)
                return _panelInfoList.ToList();

            return _panelInfoView.Cast<object>()
                .OfType<PanelInfoListItem>()
                .ToList();
        }

        private void ApplyAllFiltersAndSort()
        {
            if (_panelInfoView == null)
                return;

            using (_panelInfoView.DeferRefresh())
            {
                _panelInfoView.SortDescriptions.Clear();
                if (!string.IsNullOrWhiteSpace(_currentSortPropertyPath) && _currentSortDirection.HasValue)
                {
                    _panelInfoView.SortDescriptions.Add(new SortDescription(_currentSortPropertyPath, _currentSortDirection.Value));
                }
            }

            _panelInfoView.Refresh();
            UpdateStatistics();
        }

        private void FilterHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement trigger)
                return;

            var header = FindVisualParent<DataGridColumnHeader>(trigger);
            if (header?.Column == null)
                return;

            string propertyPath = header.Column.SortMemberPath;
            if (string.IsNullOrWhiteSpace(propertyPath))
                return;

            _activeFilterPropertyPath = propertyPath;
            HeaderFilterTitleTextBlock.Text = $"{header.Column.Header} - 筛选";
            BuildHeaderFilterOptions(propertyPath);
            HeaderFilterSearchTextBox.Text = "";
            HeaderFilterPopup.PlacementTarget = trigger;
            HeaderFilterPopup.IsOpen = true;
        }

        private void BuildHeaderFilterOptions(string propertyPath)
        {
            var baseItems = _panelInfoList
                .Where(item => PassesAllColumnFilters(item, propertyPath))
                .ToList();

            var distinctValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in baseItems)
            {
                distinctValues.Add(GetFilterDisplayValue(item, propertyPath));
            }

            if (_columnFilters.TryGetValue(propertyPath, out var existingState) && existingState.IsActive)
            {
                foreach (var selectedValue in existingState.SelectedValues)
                {
                    distinctValues.Add(selectedValue);
                }
            }

            var orderedValues = distinctValues
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _headerFilterOptions.Clear();
            foreach (var value in orderedValues)
            {
                bool isSelected = existingState == null || !existingState.IsActive || existingState.SelectedValues.Contains(value);
                _headerFilterOptions.Add(new FilterOptionItem
                {
                    Value = value,
                    DisplayText = value,
                    IsSelected = isSelected
                });
            }

            _headerFilterOptionsView?.Refresh();
        }

        private void HeaderFilterSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_headerFilterOptionsView == null)
                return;

            _headerFilterOptionsView.Filter = obj =>
            {
                if (obj is not FilterOptionItem option)
                    return false;

                string keyword = HeaderFilterSearchTextBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(keyword))
                    return true;

                return option.DisplayText.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0;
            };
            _headerFilterOptionsView.Refresh();
        }

        private void SortAscendingButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_activeFilterPropertyPath))
                return;

            _currentSortPropertyPath = _activeFilterPropertyPath;
            _currentSortDirection = ListSortDirection.Ascending;
            ApplyAllFiltersAndSort();
        }

        private void SortDescendingButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_activeFilterPropertyPath))
                return;

            _currentSortPropertyPath = _activeFilterPropertyPath;
            _currentSortDirection = ListSortDirection.Descending;
            ApplyAllFiltersAndSort();
        }

        private void ClearSortButton_Click(object sender, RoutedEventArgs e)
        {
            _currentSortPropertyPath = "";
            _currentSortDirection = null;
            ApplyAllFiltersAndSort();
        }

        private void SelectAllFilterValuesButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var option in _headerFilterOptions)
            {
                option.IsSelected = true;
            }
        }

        private void ClearAllFilterValuesButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var option in _headerFilterOptions)
            {
                option.IsSelected = false;
            }
        }

        private void ApplyHeaderFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_activeFilterPropertyPath))
                return;

            var allValues = _headerFilterOptions.Select(x => x.Value).ToList();
            var selectedValues = _headerFilterOptions
                .Where(x => x.IsSelected)
                .Select(x => x.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool selectsAll = selectedValues.Count == allValues.Count && allValues.Count > 0;
            if (selectsAll)
            {
                _columnFilters.Remove(_activeFilterPropertyPath);
            }
            else
            {
                _columnFilters[_activeFilterPropertyPath] = new ColumnFilterState
                {
                    PropertyPath = _activeFilterPropertyPath,
                    IsActive = true,
                    SelectedValues = selectedValues
                };
            }

            ApplyAllFiltersAndSort();
            HeaderFilterPopup.IsOpen = false;
        }

        private void ClearHeaderFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_activeFilterPropertyPath))
            {
                _columnFilters.Remove(_activeFilterPropertyPath);
            }

            ApplyAllFiltersAndSort();
            BuildHeaderFilterOptions(_activeFilterPropertyPath);
        }

        private void CancelHeaderFilterButton_Click(object sender, RoutedEventArgs e)
        {
            HeaderFilterPopup.IsOpen = false;
        }

        private string GetFilterDisplayValue(PanelInfoListItem item, string propertyPath)
        {
            if (item == null || string.IsNullOrWhiteSpace(propertyPath))
                return BlankFilterDisplay;

            string value = propertyPath switch
            {
                nameof(PanelInfoListItem.IsSelected) => item.IsSelected ? "是" : "否",
                nameof(PanelInfoListItem.Index) => item.Index.ToString(CultureInfo.InvariantCulture),
                nameof(PanelInfoListItem.EntityId) => item.EntityId,
                nameof(PanelInfoListItem.ShapeType) => item.ShapeType,
                nameof(PanelInfoListItem.EntityKindDisplay) => item.EntityKindDisplay,
                nameof(PanelInfoListItem.ExcludeFromPaiBanDisplay) => item.ExcludeFromPaiBanDisplay,
                nameof(PanelInfoListItem.OrderId) => item.OrderId,
                nameof(PanelInfoListItem.CabinetId) => item.CabinetId,
                nameof(PanelInfoListItem.RoomId) => item.RoomId,
                nameof(PanelInfoListItem.PanelName) => item.PanelName,
                nameof(PanelInfoListItem.Material) => item.Material,
                nameof(PanelInfoListItem.Length) => item.Length.ToString("F1", CultureInfo.InvariantCulture),
                nameof(PanelInfoListItem.Width) => item.Width.ToString("F1", CultureInfo.InvariantCulture),
                nameof(PanelInfoListItem.Height) => item.Height.ToString("F1", CultureInfo.InvariantCulture),
                nameof(PanelInfoListItem.IsLocked) => item.IsLocked ? "是" : "否",
                nameof(PanelInfoListItem.CuttingLength) => item.CuttingLength.ToString("F1", CultureInfo.InvariantCulture),
                nameof(PanelInfoListItem.CuttingWidth) => item.CuttingWidth.ToString("F1", CultureInfo.InvariantCulture),
                nameof(PanelInfoListItem.CuttingHeight) => item.CuttingHeight.ToString("F1", CultureInfo.InvariantCulture),
                nameof(PanelInfoListItem.Quantity) => item.Quantity.ToString(CultureInfo.InvariantCulture),
                nameof(PanelInfoListItem.Area) => item.Area.ToString("F4", CultureInfo.InvariantCulture),
                nameof(PanelInfoListItem.EdgeTop) => item.EdgeTop,
                nameof(PanelInfoListItem.EdgeBottom) => item.EdgeBottom,
                nameof(PanelInfoListItem.EdgeLeft) => item.EdgeLeft,
                nameof(PanelInfoListItem.EdgeRight) => item.EdgeRight,
                nameof(PanelInfoListItem.TextureDirection) => item.TextureDirection,
                nameof(PanelInfoListItem.HasHardware) => string.IsNullOrWhiteSpace(item.HasHardware) ? "否" : item.HasHardware,
                nameof(PanelInfoListItem.Paint) => item.Paint,
                nameof(PanelInfoListItem.Remarks) => item.Remarks,
                nameof(PanelInfoListItem.Status) => item.Status,
                _ => ""
            };

            return string.IsNullOrWhiteSpace(value) ? BlankFilterDisplay : value.Trim();
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var current = child;
            while (current != null)
            {
                if (current is T target)
                    return target;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
        
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseButton_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            // 键盘事件处理
        }
        

        
        private void PanelInfoDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // 单元格编辑结束时，标记该项需要保存
            if (e.Row.Item is PanelInfoListItem item)
            {
                item.IsModified = true;
                
                // 如果是锁定列的编辑，添加调试信息
                if (e.Column.Header.ToString() == "锁定")
                {
                    System.Diagnostics.Debug.WriteLine($"锁定状态已修改: {item.EntityId} -> {item.IsLocked}");
                }
            }
        }
        
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    int successCount = 0;
                    int errorCount = 0;
                    
                    foreach (var item in _panelInfoList.Where(x => x.IsModified))
                    {
                        try
                        {
                            if (long.TryParse(item.EntityId, out long handle))
                            {
                                var db = doc.Database;
                                if (db.TryGetObjectId(new Handle(handle), out ObjectId entityId))
                                {
                                    var entity = tr.GetObject(entityId, OpenMode.ForWrite) as Entity;
                                    if (entity != null)
                                    {
                                        // 先读取原有扩展数据，避免覆盖五金等未在表格编辑的字段
                        var panelInfo = PanelInfoService.GetPanelInfo(entity, tr) ?? new PanelInfo();
                        panelInfo.OrderId = item.OrderId;
                        panelInfo.CabinetId = item.CabinetId;
                        panelInfo.RoomId = item.RoomId;
                        panelInfo.PanelName = item.PanelName;
                        panelInfo.Material = item.Material;
                        panelInfo.Length = item.Length;
                        panelInfo.Width = item.Width;
                        panelInfo.Height = item.Height;
                        panelInfo.ExtraLength = item.ExtraLength;
                        panelInfo.ExtraWidth = item.ExtraWidth;
                        panelInfo.ExtraHeight = item.ExtraHeight;
                        panelInfo.EdgeBanding = item.EdgeBanding;
                        panelInfo.Paint = item.Paint;
                        panelInfo.Remarks = item.Remarks;
                        var newCalcType = ShapeNameToCalcType(item.ShapeType);
                        if (panelInfo.CalculationType != newCalcType)
                            panelInfo.IsCalculationTypeManual = true;
                        panelInfo.CalculationType = newCalcType;
                        panelInfo.IsDimensionLocked = item.IsLocked;
                        panelInfo.EdgeTop = item.EdgeTop;
                        panelInfo.EdgeBottom = item.EdgeBottom;
                        panelInfo.EdgeLeft = item.EdgeLeft;
                        panelInfo.EdgeRight = item.EdgeRight;
                        panelInfo.ArcInnerRadius = item.ArcInnerRadius;
                        panelInfo.ArcAngleDegrees = item.ArcAngleDegrees;
                        panelInfo.ArcStraightLength1 = item.ArcStraightLength1;
                        panelInfo.ArcStraightLength2 = item.ArcStraightLength2;
                        panelInfo.UnfoldedLength = item.UnfoldedLength;
                        panelInfo.UnfoldedWidth = item.UnfoldedWidth;
                                        
                                        // 保存到实体
                                        SavePanelInfoToEntity(entity, panelInfo);
                                        item.IsModified = false;
                                        successCount++;
                                    }
                                }
                            }
                        }
                        catch (System.Exception ex)
                        {
                            errorCount++;
                            System.Diagnostics.Debug.WriteLine($"保存实体 {item.EntityId} 时出错: {ex.Message}");
                        }
                    }
                    
                    tr.Commit();
                    
                    // 显示保存结果
                    string message = $"保存完成！\n成功: {successCount} 个\n失败: {errorCount} 个";
                    MessageBox.Show(message, "保存结果", MessageBoxButton.OK, 
                        errorCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                    
                    // 刷新列表
                    LoadPanelInfoList();
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"保存时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void SavePanelInfoToEntity(Entity entity, PanelInfo panelInfo)
        {
            try
            {
                var tr = entity.Database.TransactionManager.TopTransaction;
                PanelInfoService.SavePanelInfo(entity, panelInfo, tr);
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("保存板件信息失败", ex);
                throw;
            }
        }
    }
    
    /// <summary>
    /// 板件信息列表项
    /// </summary>
    public class PanelInfoListItem : INotifyPropertyChanged
    {
        private int _index;
        private string _entityId = "";
        private string _orderId = "";
        private string _cabinetId = "";
        private string _panelName = "";
        private string _material = "";
        internal double _length;
        internal double _width;
        internal double _height;
        internal double _extraLength;
        internal double _extraWidth;
        internal double _extraHeight;
        private string _edgeBanding = "";
        private string _paint = "";
        private string _remarks = "";
        private string _status = "";
        private bool _isSelected = false;
        private bool _isArcPanel = false;
        private string _roomId = "";
        private bool _isModified = false;
        private int _quantity = 1;
        internal double _area = 0;
        private bool _isLocked = false; // 锁定状态

        // 四边独立封边
        private string _edgeTop = "";
        private string _edgeBottom = "";
        private string _edgeLeft = "";
        private string _edgeRight = "";

        // 裁切尺寸（开料尺寸 - 封边扣除）
        private double _cuttingLength;
        private double _cuttingWidth;
        private double _cuttingHeight;

        // 板件类型（来自搭积木系统）
        private string _panelType = "";
        // 板件形状类型（来自PanelInfo.CalculationType）
        private string _shapeType = "";
        private string _textureDirection = "";
        private string _hasHardware = "";
        private string _entityKindDisplay = "";
        private string _excludeFromPaiBanDisplay = "";
        private double _arcInnerRadius;
        private double _arcAngleDegrees;
        private double _arcStraightLength1;
        private double _arcStraightLength2;
        private double _unfoldedLength;
        private double _unfoldedWidth;
        
        public event PropertyChangedEventHandler PropertyChanged = delegate { };
        
        public virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
        public int Index
        {
            get => _index;
            set
            {
                if (_index != value)
                {
                    _index = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string EntityId
        {
            get => _entityId;
            set
            {
                if (_entityId != value)
                {
                    _entityId = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public int Quantity
        {
            get => _quantity;
            set
            {
                if (_quantity != value)
                {
                    _quantity = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public double Area
        {
            get => _area;
            set
            {
                if (Math.Abs(_area - value) > 1e-9)
                {
                    _area = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string OrderId
        {
            get => _orderId;
            set
            {
                if (_orderId != value)
                {
                    _orderId = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string CabinetId
        {
            get => _cabinetId;
            set
            {
                if (_cabinetId != value)
                {
                    _cabinetId = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string PanelName
        {
            get => _panelName;
            set
            {
                if (_panelName != value)
                {
                    _panelName = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string Material
        {
            get => _material;
            set
            {
                if (_material != value)
                {
                    _material = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public double Length
        {
            get => _length;
            set
            {
                if (Math.Abs(_length - value) > 1e-9)
                {
                    // 检查是否锁定，如果锁定则不允许修改
                    if (IsLocked)
                    {
                        return; // 锁定状态下不允许修改
                    }
                    
                    _length = value;
                    OnPropertyChanged();
                    
                    // 重新计算面积
                    Area = (_extraLength * _extraWidth) / 1000000.0;
                }
            }
        }
        
        public double Width
        {
            get => _width;
            set
            {
                if (Math.Abs(_width - value) > 1e-9)
                {
                    // 检查是否锁定，如果锁定则不允许修改
                    if (IsLocked)
                    {
                        return; // 锁定状态下不允许修改
                    }
                    
                    _width = value;
                    OnPropertyChanged();
                    
                    // 重新计算面积
                    Area = (_extraLength * _extraWidth) / 1000000.0;
                }
            }
        }
        
        public double Height
        {
            get => _height;
            set
            {
                if (Math.Abs(_height - value) > 1e-9)
                {
                    _height = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public double ExtraLength
        {
            get => _extraLength;
            set
            {
                if (Math.Abs(_extraLength - value) > 1e-9)
                {
                    // 检查是否锁定，如果锁定则不允许修改
                    if (IsLocked)
                    {
                        return; // 锁定状态下不允许修改
                    }
                    
                    _extraLength = value;
                    OnPropertyChanged();
                    
                    // 重新计算面积
                    Area = (_extraLength * _extraWidth) / 1000000.0;
                }
            }
        }
        
        public double ExtraWidth
        {
            get => _extraWidth;
            set
            {
                if (Math.Abs(_extraWidth - value) > 1e-9)
                {
                    // 检查是否锁定，如果锁定则不允许修改
                    if (IsLocked)
                    {
                        return; // 锁定状态下不允许修改
                    }
                    
                    _extraWidth = value;
                    OnPropertyChanged();
                    
                    // 重新计算面积
                    Area = (_extraLength * _extraWidth) / 1000000.0;
                }
            }
        }
        
        public double ExtraHeight
        {
            get => _extraHeight;
            set
            {
                if (Math.Abs(_extraHeight - value) > 1e-9)
                {
                    // 检查是否锁定，如果锁定则不允许修改
                    if (IsLocked)
                    {
                        return; // 锁定状态下不允许修改
                    }
                    
                    _extraHeight = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string EdgeBanding
        {
            get => _edgeBanding;
            set
            {
                if (_edgeBanding != value)
                {
                    _edgeBanding = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string Paint
        {
            get => _paint;
            set
            {
                if (_paint != value)
                {
                    _paint = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string Remarks
        {
            get => _remarks;
            set
            {
                if (_remarks != value)
                {
                    _remarks = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public bool IsArcPanel
        {
            get => _isArcPanel;
            set
            {
                if (_isArcPanel != value)
                {
                    _isArcPanel = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string RoomId
        {
            get => _roomId;
            set
            {
                if (_roomId != value)
                {
                    _roomId = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public bool IsModified
        {
            get => _isModified;
            set
            {
                if (_isModified != value)
                {
                    _isModified = value;
                    OnPropertyChanged();
                }
            }
        }
        
        /// <summary>
        /// 尺寸锁定状态
        /// </summary>
        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                if (_isLocked != value)
                {
                    _isLocked = value;
                    OnPropertyChanged();
                    IsModified = true; // 标记为已修改，确保锁定状态的变更会被保存
                }
            }
        }

        /// <summary>上边封边</summary>
        public string EdgeTop
        {
            get => _edgeTop;
            set { if (_edgeTop != value) { _edgeTop = value; OnPropertyChanged(); } }
        }

        /// <summary>下边封边</summary>
        public string EdgeBottom
        {
            get => _edgeBottom;
            set { if (_edgeBottom != value) { _edgeBottom = value; OnPropertyChanged(); } }
        }

        /// <summary>左边封边</summary>
        public string EdgeLeft
        {
            get => _edgeLeft;
            set { if (_edgeLeft != value) { _edgeLeft = value; OnPropertyChanged(); } }
        }

        /// <summary>右边封边</summary>
        public string EdgeRight
        {
            get => _edgeRight;
            set { if (_edgeRight != value) { _edgeRight = value; OnPropertyChanged(); } }
        }

        /// <summary>裁切长度(mm)</summary>
        public double CuttingLength
        {
            get => _cuttingLength;
            set { if (Math.Abs(_cuttingLength - value) > 1e-9) { _cuttingLength = value; OnPropertyChanged(); } }
        }

        /// <summary>裁切宽度(mm)</summary>
        public double CuttingWidth
        {
            get => _cuttingWidth;
            set { if (Math.Abs(_cuttingWidth - value) > 1e-9) { _cuttingWidth = value; OnPropertyChanged(); } }
        }

        /// <summary>裁切厚度(mm)</summary>
        public double CuttingHeight
        {
            get => _cuttingHeight;
            set { if (Math.Abs(_cuttingHeight - value) > 1e-9) { _cuttingHeight = value; OnPropertyChanged(); } }
        }

        /// <summary>板件类型（来自搭积木系统）</summary>
        public string PanelType
        {
            get => _panelType;
            set { if (_panelType != value) { _panelType = value; OnPropertyChanged(); } }
        }

        /// <summary>板件形状类型（AABB/OBB/ArcPanel→显示文本）</summary>
        public string ShapeType
        {
            get => _shapeType;
            set { if (_shapeType != value) { _shapeType = value; OnPropertyChanged(); } }
        }

        /// <summary>纹路方向</summary>
        public string TextureDirection
        {
            get => _textureDirection;
            set { if (_textureDirection != value) { _textureDirection = value; OnPropertyChanged(); } }
        }

        /// <summary>是否含有五金配置</summary>
        public string HasHardware
        {
            get => _hasHardware;
            set { if (_hasHardware != value) { _hasHardware = value; OnPropertyChanged(); } }
        }

        /// <summary>实体用途（板件/五金）</summary>
        public string EntityKindDisplay
        {
            get => _entityKindDisplay;
            set { if (_entityKindDisplay != value) { _entityKindDisplay = value; OnPropertyChanged(); } }
        }

        /// <summary>是否排版忽略</summary>
        public string ExcludeFromPaiBanDisplay
        {
            get => _excludeFromPaiBanDisplay;
            set { if (_excludeFromPaiBanDisplay != value) { _excludeFromPaiBanDisplay = value; OnPropertyChanged(); } }
        }

        public double ArcInnerRadius
        {
            get => _arcInnerRadius;
            set { if (Math.Abs(_arcInnerRadius - value) > 1e-9) { _arcInnerRadius = value; OnPropertyChanged(); } }
        }

        public double ArcAngleDegrees
        {
            get => _arcAngleDegrees;
            set { if (Math.Abs(_arcAngleDegrees - value) > 1e-9) { _arcAngleDegrees = value; OnPropertyChanged(); } }
        }

        public double ArcStraightLength1
        {
            get => _arcStraightLength1;
            set { if (Math.Abs(_arcStraightLength1 - value) > 1e-9) { _arcStraightLength1 = value; OnPropertyChanged(); } }
        }

        public double ArcStraightLength2
        {
            get => _arcStraightLength2;
            set { if (Math.Abs(_arcStraightLength2 - value) > 1e-9) { _arcStraightLength2 = value; OnPropertyChanged(); } }
        }

        public double UnfoldedLength
        {
            get => _unfoldedLength;
            set { if (Math.Abs(_unfoldedLength - value) > 1e-9) { _unfoldedLength = value; OnPropertyChanged(); } }
        }

        public double UnfoldedWidth
        {
            get => _unfoldedWidth;
            set { if (Math.Abs(_unfoldedWidth - value) > 1e-9) { _unfoldedWidth = value; OnPropertyChanged(); } }
        }
    }

    public class FilterOptionItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private string _value = "";
        private string _displayText = "";

        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        public string Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    PropertyChanged(this, new PropertyChangedEventArgs(nameof(Value)));
                }
            }
        }

        public string DisplayText
        {
            get => _displayText;
            set
            {
                if (_displayText != value)
                {
                    _displayText = value;
                    PropertyChanged(this, new PropertyChangedEventArgs(nameof(DisplayText)));
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }
    }

    public class ColumnFilterState
    {
        public string PropertyPath { get; set; } = "";
        public bool IsActive { get; set; }
        public HashSet<string> SelectedValues { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
