using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    public partial class PanelInfoWindow : Window
    {
        private static readonly string EdgeBandConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FurniturePlugin",
            "edge_band_names.json");

        private static readonly string[] DefaultEdgeBandMaterials = new[]
        {
            "0.5*22 爱格W1000 ABS封边条",
            "1*22 爱格W1000 ABS封边条",
            "2*22 爱格W1000 ABS封边条",
            "0.5*22 同色PVC封边条",
            "1*22 同色PVC封边条",
            "2*22 同色PVC封边条",
            ""
        };

        private PanelInfo _panelData;
        private ObjectId _entityId;
        private bool _isReadOnly;
        private CabinetFrame _associatedFrame;
        private PanelSlot _associatedSlot;
        private ObservableCollection<PanelHardwareItem> _hardwareItems = new();
        private bool _isLoadingUi;
        private bool _isAutoUpdatingCutting;
        private bool _userModifiedCutting;

        // 板件形状类型列表（映射到PanelCalculationType）
        private static readonly string[] PanelShapeTypes = new[]
        {
            "常规矩形板(AABB—平行于XYZ轴)",
            "旋转过的矩形板(OBB—有向包围盒)",
            "异形板(OBB—有向包围盒)",
            "圆弧板件(弧形展开计算)",
            "样条实体板件(高精度—双轨中点折线展开)"
        };

        private static readonly string[] EntityKindTypes = new[]
        {
            "板件",
            "五金"
        };

        private static readonly string[] ArcLengthReferenceTypes = new[]
        {
            "外线",
            "中线",
            "内线"
        };

        private static PanelCalculationType GetCalcTypeFromShape(string shape)
        {
            if (shape == null) return PanelCalculationType.AABB;
            if (shape.StartsWith("常规矩形板")) return PanelCalculationType.AABB;
            if (shape.StartsWith("旋转过的矩形板")) return PanelCalculationType.OBB;
            if (shape.StartsWith("异形板")) return PanelCalculationType.Irregular;
            if (shape.StartsWith("圆弧板件")) return PanelCalculationType.ArcPanel;
            if (shape.StartsWith("样条实体")) return PanelCalculationType.SplinePanel;
            return PanelCalculationType.OBB;
        }

        private static string GetShapeFromCalcType(PanelCalculationType calcType)
        {
            return calcType switch
            {
                PanelCalculationType.AABB => PanelShapeTypes[0],
                PanelCalculationType.OBB => PanelShapeTypes[1],
                PanelCalculationType.Irregular => PanelShapeTypes[2],
                PanelCalculationType.ArcPanel => PanelShapeTypes[3],
                PanelCalculationType.SplinePanel => PanelShapeTypes[4],
                _ => PanelShapeTypes[1]
            };
        }

        private static EntityKind GetEntityKindFromDisplay(string display)
        {
            return string.Equals(display, "五金", StringComparison.OrdinalIgnoreCase)
                ? EntityKind.Hardware
                : EntityKind.Panel;
        }

        private static string GetDisplayFromEntityKind(EntityKind kind)
        {
            return kind == EntityKind.Hardware ? "五金" : "板件";
        }

        private static ArcLengthReferenceType GetArcLengthReferenceFromDisplay(string display)
        {
            if (string.Equals(display, "内线", StringComparison.OrdinalIgnoreCase))
                return ArcLengthReferenceType.Inner;
            if (string.Equals(display, "中线", StringComparison.OrdinalIgnoreCase))
                return ArcLengthReferenceType.Center;
            return ArcLengthReferenceType.Outer;
        }

        private static string GetDisplayFromArcLengthReference(ArcLengthReferenceType referenceType)
        {
            return referenceType switch
            {
                ArcLengthReferenceType.Inner => "内线",
                ArcLengthReferenceType.Center => "中线",
                _ => "外线"
            };
        }

        private enum EditableDimension
        {
            Length,
            Width,
            Height
        }

        private enum FrameDimension
        {
            Width,
            Depth,
            Height
        }

        public PanelInfo PanelData => _panelData;

        public PanelInfoWindow(PanelInfo existingInfo, ObjectId entityId, bool isReadOnly)
        {
            InitializeComponent();

            _panelData = existingInfo ?? new PanelInfo();
            DataContext = _panelData;
            _entityId = entityId;
            _isReadOnly = isReadOnly;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResize;
            WindowStyle = WindowStyle.SingleBorderWindow;

            InitComboBoxes();
            InitHardwareGrid();
            LoadBuildingBlockInfo();
            TryPopulateDimensionsFromAssociatedSlot();
            LoadDataToUI();
            UpdateDimensionEditorState();

            rbAlongLength.Checked += (s, ev) => OnTextureDirectionChanged();
            rbAlongWidth.Checked += (s, ev) => OnTextureDirectionChanged();

            // 裁切尺寸实时计算
            ExtraLengthTextBox.TextChanged += (s, ev) => UpdateExtraToolTips();
            ExtraWidthTextBox.TextChanged += (s, ev) => UpdateExtraToolTips();
            ExtraHeightTextBox.TextChanged += (s, ev) => UpdateExtraToolTips();
            ExtraLengthTextBox.TextChanged += CuttingTextChangedByUser;
            ExtraWidthTextBox.TextChanged += CuttingTextChangedByUser;
            ExtraHeightTextBox.TextChanged += CuttingTextChangedByUser;

            foreach (var cmb in new[] { cmbEdgeTop, cmbEdgeBottom, cmbEdgeLeft, cmbEdgeRight })
            {
                cmb.SelectionChanged += (_, __) => TryAutoUpdateCuttingFromEdge();
                cmb.LostFocus += (_, __) => TryAutoUpdateCuttingFromEdge();
            }

            if (_isReadOnly)
            {
                SetReadOnlyMode();
            }
        }

        #region 初始化

        private void InitComboBoxes()
        {
            var edgeBandMaterials = LoadEdgeBandCatalog();
            foreach (var cmb in new[] { cmbEdgeTop, cmbEdgeBottom, cmbEdgeLeft, cmbEdgeRight })
            {
                cmb.ItemsSource = edgeBandMaterials.ToList();
            }

            PanelShapeComboBox.ItemsSource = PanelShapeTypes.ToList();
            PanelShapeComboBox.IsEditable = false; // 板件形状不可自由输入
            EntityKindComboBox.ItemsSource = EntityKindTypes.ToList();
            EntityKindComboBox.IsEditable = false;
            ArcLengthReferenceComboBox.ItemsSource = ArcLengthReferenceTypes.ToList();
            ArcLengthReferenceComboBox.IsEditable = false;
        }

        private void SetShapeComboFromCalculationType(PanelCalculationType calcType)
        {
            bool oldLoading = _isLoadingUi;
            _isLoadingUi = true;
            try
            {
                PanelShapeComboBox.SelectedItem = GetShapeFromCalcType(calcType);
            }
            finally
            {
                _isLoadingUi = oldLoading;
            }
        }

        private void InitHardwareGrid()
        {
            _hardwareItems = new ObservableCollection<PanelHardwareItem>();
            dgPanelHardware.ItemsSource = _hardwareItems;
        }

        private void SetEntityKindComboFromValue(EntityKind kind)
        {
            bool oldLoading = _isLoadingUi;
            _isLoadingUi = true;
            try
            {
                EntityKindComboBox.SelectedItem = GetDisplayFromEntityKind(kind);
            }
            finally
            {
                _isLoadingUi = oldLoading;
            }
        }

        #endregion

        #region 数据加载

        private void LoadDataToUI()
        {
            _isLoadingUi = true;
            OrderIdTextBox.Text = _panelData.OrderId;
            CabinetIdTextBox.Text = _panelData.CabinetId;
            RoomIdTextBox.Text = _panelData.RoomId;
            PanelNameTextBox.Text = _panelData.PanelName;
            MaterialTextBox.Text = _panelData.Material;
            SetShapeComboFromCalculationType(_panelData.CalculationType);
            SetEntityKindComboFromValue(_panelData.EntityKind);
            ExcludeFromPaiBanCheckBox.IsChecked = _panelData.ExcludeFromPaiBan;

            LengthTextBox.Text = _panelData.Length.ToString("F1");
            WidthTextBox.Text = _panelData.Width.ToString("F1");
			HeightTextBox.Text = _panelData.Height.ToString("F1");
			ExtraLengthTextBox.Text = !string.IsNullOrEmpty(_panelData.ExtraLengthFormula) ? _panelData.ExtraLengthFormula : (_panelData.ExtraLength > 0 ? _panelData.ExtraLength.ToString("F1") : "");
			ExtraWidthTextBox.Text  = !string.IsNullOrEmpty(_panelData.ExtraWidthFormula)  ? _panelData.ExtraWidthFormula  : (_panelData.ExtraWidth > 0 ? _panelData.ExtraWidth.ToString("F1") : "");
			ExtraHeightTextBox.Text = !string.IsNullOrEmpty(_panelData.ExtraHeightFormula) ? _panelData.ExtraHeightFormula : (_panelData.ExtraHeight > 0 ? _panelData.ExtraHeight.ToString("F1") : "");
			UpdateExtraToolTips();
            cmbEdgeTop.Text = _panelData.EdgeTop ?? "";
            cmbEdgeBottom.Text = _panelData.EdgeBottom ?? "";
            cmbEdgeLeft.Text = _panelData.EdgeLeft ?? "";
            cmbEdgeRight.Text = _panelData.EdgeRight ?? "";

            // 纹路方向
            rbAlongLength.IsChecked = _panelData.TextureDirection == TextureDirection.AlongLength;
            rbAlongWidth.IsChecked = _panelData.TextureDirection == TextureDirection.AlongWidth;

            // 尺寸锁定
            DimensionLockCheckBox.IsChecked = _panelData.IsDimensionLocked;

            // 其他
            PaintTextBox.Text = _panelData.Paint;
            RemarksTextBox.Text = _panelData.Remarks;

            _hardwareItems.Clear();
            foreach (var item in (_panelData.HardwareItems ?? new List<PanelHardwareItem>()))
            {
                _hardwareItems.Add(item.Clone());
            }
            NormalizeHardwareIndexes();
            UpdateArcPanelVisibility();
            ApplyArcLengthReferenceToUi(updateCutting: false);
            _userModifiedCutting = false;
            _isLoadingUi = false;
            UpdateDimensionEditorState();
        }

        private void UpdateDimensionEditorState()
		{
			bool locked = DimensionLockCheckBox.IsChecked == true;
			bool canOverwriteDimensions = !locked && !_isReadOnly;
			LengthTextBox.IsReadOnly = true;
			WidthTextBox.IsReadOnly = true;
			HeightTextBox.IsReadOnly = true;
			ExtraLengthTextBox.IsReadOnly = locked;
			ExtraWidthTextBox.IsReadOnly = locked;
			ExtraHeightTextBox.IsReadOnly = locked;
            BtnCalcDimensions.IsEnabled = canOverwriteDimensions;
            BtnCalcCutting.IsEnabled = canOverwriteDimensions;
			LengthTextBox.ToolTip = "点击「自动计算尺寸」从CAD实体获取";
			WidthTextBox.ToolTip = "点击「自动计算尺寸」从CAD实体获取";
			HeightTextBox.ToolTip = "点击「自动计算尺寸」从CAD实体获取";
			ExtraLengthTextBox.ToolTip = "支持公式：L+50, W*0.5 等。空=自动扣封边";
			ExtraWidthTextBox.ToolTip = "支持公式。空=自动扣封边";
			ExtraHeightTextBox.ToolTip = "支持公式。空=自动扣封边";
            BtnCalcDimensions.ToolTip = canOverwriteDimensions
                ? "从 CAD 实体重新识别并回填显示尺寸、裁切尺寸。"
                : "尺寸已锁定，禁止自动计算覆盖当前尺寸。";
            BtnCalcCutting.ToolTip = canOverwriteDimensions
                ? "根据当前显示尺寸和封边重新计算裁切尺寸。"
                : "尺寸已锁定，禁止更新裁切尺寸覆盖当前数据。";
				UpdateExtraToolTips();
			}

        private bool CanEditDisplayDimension(EditableDimension dimension)
        {
            if (_isReadOnly || DimensionLockCheckBox.IsChecked == true)
                return false;

            if (_associatedSlot == null)
                return true;

            if (_associatedSlot.PlacementKind == PanelPlacementKind.AttachmentZone)
                return false;

            return TryMapSlotDimensionToFrameDimension(_associatedSlot, dimension, out _);
        }

        private string GetDisplayDimensionToolTip(EditableDimension dimension)
        {
            if (_associatedSlot == null)
                return "普通板件可直接编辑显示尺寸。";

            if (_associatedSlot.PlacementKind == PanelPlacementKind.AttachmentZone)
                return "附属搭积木板件请通过参数区修改，显示尺寸保持只读。";

            if (DimensionLockCheckBox.IsChecked == true)
                return "尺寸已锁定。";

            return TryMapSlotDimensionToFrameDimension(_associatedSlot, dimension, out var frameDimension)
                ? $"当前维度会回写整柜{GetFrameDimensionDisplayName(frameDimension)}，并触发整柜重算。"
                : "当前维度不映射整柜宽/深/高，保持只读。";
        }

        private static string GetFrameDimensionDisplayName(FrameDimension frameDimension)
        {
            return frameDimension switch
            {
                FrameDimension.Width => "宽",
                FrameDimension.Depth => "深",
                FrameDimension.Height => "高",
                _ => "尺寸"
            };
        }

        private static bool TryMapSlotDimensionToFrameDimension(PanelSlot slot, EditableDimension dimension, out FrameDimension frameDimension)
        {
            frameDimension = FrameDimension.Width;
            if (slot == null)
                return false;

            switch (slot.Orient)
            {
                case PanelOrient.Horizontal:
                    if (dimension == EditableDimension.Length)
                    {
                        frameDimension = FrameDimension.Width;
                        return true;
                    }

                    if (dimension == EditableDimension.Width)
                    {
                        frameDimension = FrameDimension.Depth;
                        return true;
                    }

                    return false;

                case PanelOrient.LeftSide:
                case PanelOrient.RightSide:
                case PanelOrient.VerticalDivider:
                    if (dimension == EditableDimension.Length)
                    {
                        frameDimension = FrameDimension.Height;
                        return true;
                    }

                    if (dimension == EditableDimension.Width)
                    {
                        frameDimension = FrameDimension.Depth;
                        return true;
                    }

                    return false;

                case PanelOrient.Back:
                    if (dimension == EditableDimension.Length)
                    {
                        frameDimension = FrameDimension.Width;
                        return true;
                    }

                    if (dimension == EditableDimension.Width)
                    {
                        frameDimension = FrameDimension.Height;
                        return true;
                    }

                    return false;

                case PanelOrient.Front:
                    if (dimension == EditableDimension.Length)
                    {
                        frameDimension = FrameDimension.Height;
                        return true;
                    }

                    if (dimension == EditableDimension.Width)
                    {
                        frameDimension = FrameDimension.Width;
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        private void LoadBuildingBlockInfo()
        {
            // 尝试查找关联的搭积木信息
            try
            {
                CabinetFrameService.LoadAll();
                string entityHandle = _entityId.Handle.Value.ToString();

                foreach (var frame in CabinetFrameService.GetAllFrames())
                {
                    var slot = frame.Panels.FirstOrDefault(p => p.EntityHandle == entityHandle);
                    if (slot != null)
                    {
                        _associatedFrame = frame;
                        _associatedSlot = slot;

                        txtFrameId.Text = frame.FrameId.Substring(0, 8) + "...";
                        txtSpaceId.Text = slot.SpaceId;
                        txtOrient.Text = slot.Orient.ToString();
                        txtBackPanelStyle.Text = frame.BackPanelStyle.ToString();
                        txtBuildingBlockInfo.Text = slot.PlacementKind == PanelPlacementKind.AttachmentZone
                            ? $"(附属搭积木板件: {slot.PanelType} | {slot.GetAttachmentParameterSummary()})"
                            : $"(搭积木板件: {slot.PanelType})";
                        SetAttachmentEditorVisibility(slot.PlacementKind == PanelPlacementKind.AttachmentZone);
                        if (slot.PlacementKind == PanelPlacementKind.AttachmentZone)
                            LoadAttachmentParameters(slot);

                        // 搭积木板件优先采用空间树里的精确尺寸，避免包围盒长宽厚判断错误。
                        LengthTextBox.Text = slot.ComputedLength.ToString("F1");
                        WidthTextBox.Text = slot.ComputedWidth.ToString("F1");
                        HeightTextBox.Text = slot.ComputedThickness.ToString("F1");
                        ExtraLengthTextBox.Text = slot.CuttingLength.ToString("F1");
                        ExtraWidthTextBox.Text = slot.CuttingWidth.ToString("F1");
                        _panelData.Length = slot.ComputedLength;
                        _panelData.Width = slot.ComputedWidth;
                        _panelData.Height = slot.ComputedThickness;
                        _panelData.ExtraLength = slot.CuttingLength;
                        _panelData.ExtraWidth = slot.CuttingWidth;
                        _panelData.ExtraHeight = slot.ComputedThickness;
                        return;
                    }
                }
            }
            catch { }

            // 非搭积木板件
            txtBuildingBlockInfo.Text = "(非搭积木板件)";
            gridBuildingBlock.Visibility = System.Windows.Visibility.Collapsed;
            SetAttachmentEditorVisibility(false);
        }

        #endregion

        #region 从UI收集数据

        private PanelInfo CollectDataFromUI()
        {
            var info = _panelData;

            info.OrderId = OrderIdTextBox.Text?.Trim() ?? "";
            info.CabinetId = CabinetIdTextBox.Text?.Trim() ?? "";
            info.RoomId = RoomIdTextBox.Text?.Trim() ?? "";
            info.PanelName = PanelNameTextBox.Text?.Trim() ?? "";
            info.Material = MaterialTextBox.Text?.Trim() ?? "";
            info.EntityKind = GetEntityKindFromDisplay(EntityKindComboBox.SelectedItem as string);
            info.ExcludeFromPaiBan = ExcludeFromPaiBanCheckBox.IsChecked == true;
            if (ArcLengthReferenceComboBox.SelectedItem is string arcLengthDisplay)
                info.ArcLengthReference = GetArcLengthReferenceFromDisplay(arcLengthDisplay);

            // 封边
            info.EdgeTop = cmbEdgeTop.Text?.Trim() ?? "";
            info.EdgeBottom = cmbEdgeBottom.Text?.Trim() ?? "";
            info.EdgeLeft = cmbEdgeLeft.Text?.Trim() ?? "";
            info.EdgeRight = cmbEdgeRight.Text?.Trim() ?? "";
            info.HardwareItems = _hardwareItems.Select(x => x.Clone()).ToList();

            // 纹路方向
            info.TextureDirection = rbAlongLength.IsChecked == true ? TextureDirection.AlongLength : TextureDirection.AlongWidth;

            // 裁切尺寸：支持公式运算（L=显示长, W=显示宽, T=显示厚）
            double L = info.Length, W = info.Width, T = info.Height;
            string txtL = ExtraLengthTextBox.Text?.Trim() ?? "";
            string txtW = ExtraWidthTextBox.Text?.Trim() ?? "";
            string txtH = ExtraHeightTextBox.Text?.Trim() ?? "";
            // 保存公式文本（如果有的话）
            info.ExtraLengthFormula = IsFormula(txtL) ? txtL : "";
            info.ExtraWidthFormula  = IsFormula(txtW) ? txtW : "";
            info.ExtraHeightFormula = IsFormula(txtH) ? txtH : "";
            // 计算结果
            info.ExtraLength = EvalDimensionFormula(txtL, L, W, T, info, "L");
            info.ExtraWidth  = EvalDimensionFormula(txtW, L, W, T, info, "W");
            info.ExtraHeight = EvalDimensionFormula(txtH, L, W, T, info, "H");
            // 校验：裁切尺寸不能为0或负数
            if (info.ExtraLength <= 0 || info.ExtraWidth <= 0 || info.ExtraHeight <= 0)
            {
                string errMsg = "裁切尺寸计算结果异常:\n";
                if (info.ExtraLength <= 0) errMsg += $"裁切长={info.ExtraLength:F1}mm (表达式: {(string.IsNullOrEmpty(txtL)?"自动扣封边":txtL)})\n";
                if (info.ExtraWidth <= 0)  errMsg += $"裁切宽={info.ExtraWidth:F1}mm (表达式: {(string.IsNullOrEmpty(txtW)?"自动扣封边":txtW)})\n";
                if (info.ExtraHeight <= 0) errMsg += $"裁切厚={info.ExtraHeight:F1}mm (表达式: {(string.IsNullOrEmpty(txtH)?"自动扣封边":txtH)})\n";
                errMsg += "\n请检查公式或封边扣除量。是否继续保存？";
                if (MessageBox.Show(errMsg, "裁切尺寸异常", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                {
                    _panelData = info;
                    throw new InvalidOperationException("用户取消了保存：裁切尺寸计算结果异常（0或负数）。");
                }
            }
            info.IsDimensionLocked = DimensionLockCheckBox.IsChecked == true;
            info.Paint = PaintTextBox.Text?.Trim() ?? "";
            info.Remarks = RemarksTextBox.Text?.Trim() ?? "";
            info.EntityId = _entityId.IsNull ? "" : _entityId.Handle.Value.ToString();

            // 板件形状 → CalculationType
            string shapeText = PanelShapeComboBox.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(shapeText))
                info.CalculationType = GetCalcTypeFromShape(shapeText);

            return info;
        }

        #endregion

        #region 按钮事件

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SavePanelInfoToCad();
                Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument
                    ?.Editor.WriteMessage("\n保存成功");
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"保存数据时出错: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void PanelShapeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_panelData == null || PanelShapeComboBox.SelectedItem == null) return;
            string shape = PanelShapeComboBox.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(shape))
            {
                _panelData.CalculationType = GetCalcTypeFromShape(shape);
                if (!_isLoadingUi)
                    _panelData.IsCalculationTypeManual = true;
                UpdateArcPanelVisibility();
            }
        }

        private void EntityKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_panelData == null || EntityKindComboBox.SelectedItem == null)
                return;

            var entityKind = GetEntityKindFromDisplay(EntityKindComboBox.SelectedItem as string);
            _panelData.EntityKind = entityKind;
            if (!_isLoadingUi && entityKind == EntityKind.Hardware && ExcludeFromPaiBanCheckBox.IsChecked != true)
            {
                ExcludeFromPaiBanCheckBox.IsChecked = true;
            }
        }

        private void ArcLengthReferenceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_panelData == null || ArcLengthReferenceComboBox.SelectedItem == null)
                return;

            _panelData.ArcLengthReference = GetArcLengthReferenceFromDisplay(ArcLengthReferenceComboBox.SelectedItem as string);
            ApplyArcLengthReferenceToUi(updateCutting: !_isLoadingUi);
        }

        private void UpdateArcPanelVisibility()
        {
            bool isArc = _panelData.CalculationType == PanelCalculationType.ArcPanel ||
                         _panelData.CalculationType == PanelCalculationType.SplinePanel;
            borderArcParams.Visibility = isArc ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            gridArcParams.Visibility = isArc ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            if (isArc)
            {
                ArcLengthReferenceComboBox.SelectedItem = GetDisplayFromArcLengthReference(_panelData.ArcLengthReference);
                ArcRadiusTextBox.Text = _panelData.ArcInnerRadius > 0 ? _panelData.ArcInnerRadius.ToString("F1") : "";
                ArcAngleTextBox.Text = _panelData.ArcAngleDegrees > 0 ? _panelData.ArcAngleDegrees.ToString("F1") : "";
                ArcStraight1TextBox.Text = _panelData.ArcStraightLength1 > 0 ? _panelData.ArcStraightLength1.ToString("F1") : "";
                ArcStraight2TextBox.Text = _panelData.ArcStraightLength2 > 0 ? _panelData.ArcStraightLength2.ToString("F1") : "";
                ArcOuterLengthTextBox.Text = _panelData.ArcOuterLength > 0 ? _panelData.ArcOuterLength.ToString("F1") : "";
                ArcCenterLengthTextBox.Text = _panelData.ArcCenterLength > 0 ? _panelData.ArcCenterLength.ToString("F1") : "";
                ArcInnerLengthTextBox.Text = _panelData.ArcInnerLengthValue > 0 ? _panelData.ArcInnerLengthValue.ToString("F1") : "";
                ArcUnfoldedTextBox.Text = _panelData.GetPreferredArcLength() > 0 ? _panelData.GetPreferredArcLength().ToString("F1") : "";
                ArcUnfoldedWidthTextBox.Text = _panelData.UnfoldedWidth > 0 ? _panelData.UnfoldedWidth.ToString("F1") : "";
            }
        }

        private void ApplyArcLengthReferenceToUi(bool updateCutting)
        {
            if (_panelData == null)
                return;

            bool isArc = _panelData.CalculationType == PanelCalculationType.ArcPanel ||
                         _panelData.CalculationType == PanelCalculationType.SplinePanel;
            if (!isArc)
                return;

            double preferredLength = _panelData.GetPreferredArcLength();
            if (preferredLength > 0)
            {
                _panelData.Length = Math.Round(preferredLength, 2);
                LengthTextBox.Text = _panelData.Length.ToString("F1");
                ArcUnfoldedTextBox.Text = _panelData.Length.ToString("F1");
            }

            if (_panelData.UnfoldedWidth > 0)
                WidthTextBox.Text = _panelData.UnfoldedWidth.ToString("F1");

            HeightTextBox.Text = _panelData.Height > 0 ? _panelData.Height.ToString("F1") : HeightTextBox.Text;

            if (updateCutting)
            {
                _userModifiedCutting = false;
                UpdateCuttingSize(true);
            }

            // #region debug-point E:seti-reference-apply
            FastDimensionService.DebugReport("E", "PanelInfoWindow.ApplyArcLengthReferenceToUi:ui-applied", new
            {
                reference = _panelData.ArcLengthReference.ToString(),
                outer = _panelData.ArcOuterLength,
                center = _panelData.ArcCenterLength,
                inner = _panelData.ArcInnerLengthValue,
                preferred = preferredLength,
                displayedLength = LengthTextBox.Text,
                displayedWidth = WidthTextBox.Text,
                displayedThickness = HeightTextBox.Text,
                displayedCuttingLength = ExtraLengthTextBox.Text,
                displayedCuttingWidth = ExtraWidthTextBox.Text,
                displayedCuttingThickness = ExtraHeightTextBox.Text,
                updateCutting
            });
            // #endregion
        }

        private void SelectPanelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                var editor = doc.Editor;

                var peo = new PromptEntityOptions("\n请选择要设置信息的3D实体板件: ");
                peo.SetRejectMessage("\n请选择一个有效的3D实体。");
                peo.AddAllowedClass(typeof(Solid3d), true);

                var per = editor.GetEntity(peo);
                if (per.Status == PromptStatus.OK)
                {
                    _entityId = per.ObjectId;
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var solid = tr.GetObject(_entityId, OpenMode.ForRead) as Solid3d;
                        if (solid != null)
                        {
                            var existingInfo = PanelInfoService.GetPanelInfo(solid, tr);
                            _panelData = existingInfo ?? new PanelInfo();
                            DataContext = _panelData;
                            LoadBuildingBlockInfo();
                            PanelGeometryAnalysisService.ApplyDetectedGeometry(
                                solid,
                                _panelData,
                                _associatedSlot,
                                overwriteDimensions: !_panelData.IsDimensionLocked,
                                overwriteCalculationType: _associatedSlot == null && !_panelData.IsCalculationTypeManual,
                                updateCuttingDimensions: !_panelData.IsDimensionLocked);
                            TryPopulateDimensionsFromAssociatedSlot();
                            LoadDataToUI();
                        }
                        tr.Commit();
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"选择板件时出错: {ex.Message}", "错误");
            }
            finally
            {
                this.Show();
            }
        }

        private void BtnCalcDimensions_Click(object sender, RoutedEventArgs e)
        {
            if (DimensionLockCheckBox.IsChecked == true)
            {
                MessageBox.Show("当前已锁定尺寸，不能执行“自动计算尺寸”覆盖现有数据。请先取消“锁定尺寸”。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var solid = tr.GetObject(_entityId, OpenMode.ForRead) as Solid3d;
                    if (solid != null)
                    {
                        LoadBuildingBlockInfo();
                        PanelGeometryAnalysisService.ApplyDetectedGeometry(
                            solid,
                            _panelData,
                            _associatedSlot,
                            overwriteDimensions: true,
                            overwriteCalculationType: _associatedSlot == null && !_panelData.IsCalculationTypeManual,
                            updateCuttingDimensions: true);

                        LengthTextBox.Text = _panelData.Length.ToString("F1");
                        WidthTextBox.Text = _panelData.Width.ToString("F1");
                        HeightTextBox.Text = _panelData.Height.ToString("F1");
                        ExtraLengthTextBox.Text = _panelData.ExtraLength.ToString("F1");
                        ExtraWidthTextBox.Text = _panelData.ExtraWidth.ToString("F1");
                        ExtraHeightTextBox.Text = _panelData.ExtraHeight.ToString("F1");
                        SetShapeComboFromCalculationType(_panelData.CalculationType);
                        UpdateArcPanelVisibility();
                        ApplyArcLengthReferenceToUi(updateCutting: true);
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"计算尺寸时出错: {ex.Message}", "错误");
            }
        }

        private void TryPopulateDimensionsFromAssociatedSlot()
        {
            if (_associatedSlot == null)
                return;

            bool hasAnyDimension = _panelData.Length > 0 || _panelData.Width > 0 || _panelData.Height > 0;
            if (hasAnyDimension)
                return;

            _panelData.Length = _associatedSlot.ComputedLength;
            _panelData.Width = _associatedSlot.ComputedWidth;
            _panelData.Height = _associatedSlot.ComputedThickness;
            _panelData.ExtraLength = _associatedSlot.CuttingLength;
            _panelData.ExtraWidth = _associatedSlot.CuttingWidth;
            _panelData.ExtraHeight = _associatedSlot.ComputedThickness;
        }

        private void SetAttachmentEditorVisibility(bool isVisible)
        {
            borderAttachmentParams.Visibility = isVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            gridAttachmentParams.Visibility = isVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        private void LoadAttachmentParameters(PanelSlot slot)
        {
            if (slot == null)
                return;

            txtAttachLeftInset.Text = slot.LeftInset.ToString("F1");
            txtAttachRightInset.Text = slot.RightInset.ToString("F1");
            txtAttachFrontInset.Text = slot.FrontInset.ToString("F1");
            txtAttachBackInset.Text = slot.BackInset.ToString("F1");
            txtAttachTopInset.Text = slot.TopInset.ToString("F1");
            txtAttachBottomInset.Text = slot.BottomInset.ToString("F1");
            txtAttachGroundClearance.Text = slot.GroundClearance.ToString("F1");
            txtAttachOuterOffset.Text = slot.OuterOffset.ToString("F1");
            txtAttachHeight.Text = slot.AttachmentHeight.ToString("F1");
            txtAttachReturnWidth.Text = slot.ReturnWidth.ToString("F1");
        }

        private AttachmentPanelOptions CollectAttachmentOptionsFromUi()
        {
            return new AttachmentPanelOptions
            {
                LeftInset = ParseAttachmentValue(txtAttachLeftInset, "左缩进"),
                RightInset = ParseAttachmentValue(txtAttachRightInset, "右缩进"),
                FrontInset = ParseAttachmentValue(txtAttachFrontInset, "前缩进"),
                BackInset = ParseAttachmentValue(txtAttachBackInset, "后缩进"),
                TopInset = ParseAttachmentValue(txtAttachTopInset, "上缩进"),
                BottomInset = ParseAttachmentValue(txtAttachBottomInset, "下缩进"),
                GroundClearance = ParseAttachmentValue(txtAttachGroundClearance, "离地"),
                OuterOffset = ParseAttachmentValue(txtAttachOuterOffset, "外偏移", true),
                PanelHeight = ParseAttachmentValue(txtAttachHeight, "板高"),
                ReturnWidth = ParseAttachmentValue(txtAttachReturnWidth, "回口宽")
            };
        }

        private static double ParseAttachmentValue(TextBox textBox, string name, bool allowNegative = false)
        {
            string text = textBox?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            if (!double.TryParse(text, out double value))
                throw new InvalidOperationException($"{name} 不是有效数字。");

            if (!allowNegative && value < 0)
                throw new InvalidOperationException($"{name} 不能为负数。");

            return value;
        }

        private void BtnCalcCutting_Click(object sender, RoutedEventArgs e)
        {
            if (DimensionLockCheckBox.IsChecked == true)
            {
                MessageBox.Show("当前已锁定尺寸，不能执行“更新裁切尺寸”覆盖现有数据。请先取消“锁定尺寸”。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _userModifiedCutting = false;
            UpdateCuttingSize(true);
        }

        private void BtnEditEdgeCatalog_Click(object sender, RoutedEventArgs e)
        {
            var editorWindow = new EdgeBandCatalogWindow(LoadEdgeBandCatalog());
            if (editorWindow.ShowDialog() == true)
            {
                SaveEdgeBandCatalog(editorWindow.CatalogItems);
                RebindEdgeCombos(editorWindow.CatalogItems);
            }
        }

        private void BtnClearEdge_Click(object sender, RoutedEventArgs e)
        {
            cmbEdgeTop.Text = "";
            cmbEdgeBottom.Text = "";
            cmbEdgeLeft.Text = "";
            cmbEdgeRight.Text = "";
            TryAutoUpdateCuttingFromEdge();
        }

        private void BtnAllEdge_Click(object sender, RoutedEventArgs e)
        {
            cmbEdgeTop.Text = "0.5*22 爱格W1000 ABS封边条";
            cmbEdgeBottom.Text = "0.5*22 爱格W1000 ABS封边条";
            cmbEdgeLeft.Text = "0.5*22 爱格W1000 ABS封边条";
            cmbEdgeRight.Text = "0.5*22 爱格W1000 ABS封边条";
            TryAutoUpdateCuttingFromEdge();
        }

        private void BtnAddHardwareRow_Click(object sender, RoutedEventArgs e)
        {
            _hardwareItems.Add(new PanelHardwareItem
            {
                Index = _hardwareItems.Count + 1,
                Name = "新五金",
                Quantity = 1,
                Unit = "件"
            });
            NormalizeHardwareIndexes();
        }

        private void BtnDeleteHardwareRow_Click(object sender, RoutedEventArgs e)
        {
            if (dgPanelHardware.SelectedItem is PanelHardwareItem item)
            {
                _hardwareItems.Remove(item);
                NormalizeHardwareIndexes();
            }
        }

        private void BtnToggleTexture_Click(object sender, RoutedEventArgs e)
        {
            if (rbAlongLength.IsChecked == true)
            {
                rbAlongWidth.IsChecked = true;
                rbAlongLength.IsChecked = false;
            }
            else
            {
                rbAlongLength.IsChecked = true;
                rbAlongWidth.IsChecked = false;
            }

            // 纹路切换时交换长宽
            var tmpLen = LengthTextBox.Text;
            var tmpWid = WidthTextBox.Text;
            LengthTextBox.Text = tmpWid;
            WidthTextBox.Text = tmpLen;

            // 也交换裁切尺寸
            var tmpExtraLen = ExtraLengthTextBox.Text;
            var tmpExtraWid = ExtraWidthTextBox.Text;
            ExtraLengthTextBox.Text = tmpExtraWid;
            ExtraWidthTextBox.Text = tmpExtraLen;
        }

        private void BtnShowTexture_Click(object sender, RoutedEventArgs e)
        {
            SetTextureVisibility(true);
        }

        private void BtnHideTexture_Click(object sender, RoutedEventArgs e)
        {
            SetTextureVisibility(false);
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 更新裁切尺寸（优先从封边文本首位提取厚度；用户手改/有运算符时不自动覆盖）
        /// </summary>
        private void UpdateCuttingSize(bool force = false)
        {
            if (!double.TryParse(LengthTextBox.Text, out double length)) return;
            if (!double.TryParse(WidthTextBox.Text, out double width)) return;
            if (!force && HasManualCuttingOverride()) return;

            double top = ParseEdgeThickness(cmbEdgeTop.Text);
            double bottom = ParseEdgeThickness(cmbEdgeBottom.Text);
            double left = ParseEdgeThickness(cmbEdgeLeft.Text);
            double right = ParseEdgeThickness(cmbEdgeRight.Text);

            double cuttingLength = Math.Round(length - top - bottom, 1);
            double cuttingWidth = Math.Round(width - left - right, 1);
            if (cuttingLength <= 0 || cuttingWidth <= 0) return;

            _isAutoUpdatingCutting = true;
            ExtraLengthTextBox.Text = cuttingLength.ToString("F1");
            ExtraWidthTextBox.Text = cuttingWidth.ToString("F1");
            if (double.TryParse(HeightTextBox.Text, out var thick))
            {
                ExtraHeightTextBox.Text = thick.ToString("F1");
            }
            _isAutoUpdatingCutting = false;
        }

        private void TryAutoUpdateCuttingFromEdge()
        {
            UpdateCuttingSize(false);
        }

        private void CuttingTextChangedByUser(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingUi || _isAutoUpdatingCutting) return;
            _userModifiedCutting = true;
        }

        private bool HasManualCuttingOverride()
        {
            if (_userModifiedCutting) return true;
            return HasOperatorsOrVariables(ExtraLengthTextBox.Text)
                || HasOperatorsOrVariables(ExtraWidthTextBox.Text)
                || HasOperatorsOrVariables(ExtraHeightTextBox.Text);
        }

        private static bool HasOperatorsOrVariables(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (double.TryParse(text, out _)) return false;
            return Regex.IsMatch(text, @"[\+\-\*/\(\)LWTlwt]");
        }

        private static double ParseEdgeThickness(string edgeText)
        {
            if (string.IsNullOrWhiteSpace(edgeText))
            {
                return 0;
            }
            var m = Regex.Match(edgeText.Trim(), @"^(\d+(?:\.\d+)?)");
            if (m.Success && double.TryParse(m.Groups[1].Value, out var t))
            {
                return t;
            }
            return 0;
        }

        private void NormalizeHardwareIndexes()
        {
            for (int i = 0; i < _hardwareItems.Count; i++)
            {
                _hardwareItems[i].Index = i + 1;
            }
            dgPanelHardware.Items.Refresh();
        }

        private static List<string> LoadEdgeBandCatalog()
        {
            try
            {
                if (File.Exists(EdgeBandConfigPath))
                {
                    var json = File.ReadAllText(EdgeBandConfigPath);
                    var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                    if (!list.Contains(""))
                    {
                        list.Add("");
                    }
                    return list;
                }
            }
            catch { }

            return DefaultEdgeBandMaterials.ToList();
        }

        private static void SaveEdgeBandCatalog(List<string> items)
        {
            try
            {
                var list = (items ?? new List<string>())
                    .Select(x => x?.Trim() ?? "")
                    .Distinct()
                    .ToList();
                if (!list.Contains(""))
                {
                    list.Add("");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(EdgeBandConfigPath)!);
                File.WriteAllText(EdgeBandConfigPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void RebindEdgeCombos(List<string> materials)
        {
            string top = cmbEdgeTop.Text;
            string bottom = cmbEdgeBottom.Text;
            string left = cmbEdgeLeft.Text;
            string right = cmbEdgeRight.Text;
            var source = materials?.ToList() ?? new List<string>();
            foreach (var cmb in new[] { cmbEdgeTop, cmbEdgeBottom, cmbEdgeLeft, cmbEdgeRight })
            {
                cmb.ItemsSource = source;
            }
            cmbEdgeTop.Text = top;
            cmbEdgeBottom.Text = bottom;
            cmbEdgeLeft.Text = left;
            cmbEdgeRight.Text = right;
        }

        private void SavePanelInfoToCad()
        {
            var info = CollectDataFromUI();
            ValidatePanelInfo(info);

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("没有活动的 AutoCAD 文档。");

            if (_associatedSlot != null)
            {
                SavePanelInfoWithFrame(info, doc);
                return;
            }

            SaveStandalonePanelInfo(info, doc);
        }

		private void SaveStandalonePanelInfo(PanelInfo info, Document doc)
		{
			var db = doc.Database;
			var entityIdCopy = _entityId;
			using (doc.LockDocument())
			{
				using var tr = db.TransactionManager.StartTransaction();
				try
				{
					var entity = tr.GetObject(entityIdCopy, OpenMode.ForWrite, false) as Entity;
					if (entity == null) throw new InvalidOperationException("无法获取当前板件实体。");
					info.EntityId = entityIdCopy.Handle.Value.ToString();
					PanelInfoService.SavePanelInfo(entity, info, tr);
					tr.Commit();
				}
				catch { tr.Abort(); throw; }
			}
		}

        private void OnTextureDirectionChanged()
        {
            _panelData.IsTextureDirectionManual = true;
            _panelData.TextureDirection = rbAlongLength.IsChecked == true
                ? TextureDirection.AlongLength
                : TextureDirection.AlongWidth;

            if (_panelData.IsTextureVisible)
            {
                SetTextureVisibility(true);
            }
        }

        private void SetTextureVisibility(bool visible)
        {
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null || _entityId.IsNull) return;

                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var solid = tr.GetObject(_entityId, OpenMode.ForRead, false) as Solid3d;
                    var entity = tr.GetObject(_entityId, OpenMode.ForWrite, false) as Entity;
                    if (solid == null || entity == null)
                        return;

                    _panelData.TextureDirection = rbAlongLength.IsChecked == true
                        ? TextureDirection.AlongLength
                        : TextureDirection.AlongWidth;
                    _panelData.IsTextureVisible = visible;

                    if (visible)
                    {
                        TextureDirectionService.RefreshTextureLinesForPanel(
                            doc.Database, tr, solid, _panelData, _entityId.Handle.Value.ToString());
                    }
                    else
                    {
                        TextureDirectionService.RemoveTextureLinesForPanel(
                            doc.Database, tr, _entityId.Handle.Value.ToString());
                    }

                    PanelInfoService.SavePanelInfo(entity, _panelData, tr);
                    tr.Commit();
                }

                doc.Editor.Regen();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"切换纹路显示状态失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ValidatePanelInfo(PanelInfo info)
        {
            if (string.IsNullOrWhiteSpace(info.PanelName))
                info.PanelName = "未命名板件";
        }

        private void SavePanelInfoWithFrame(PanelInfo info, Document doc)
        {
            if (_associatedSlot == null || _associatedFrame == null) return;
            using var tr = doc.Database.TransactionManager.StartTransaction();
            try
            {
                _associatedSlot.PanelType = DeterminePanelType(info.PanelName);
                CabinetFrameService.SaveFrame(_associatedFrame);

                // ★ 同时保存PanelInfo到实体扩展字典（之前只保存了框架，丢失所有板件数据）
                if (!_entityId.IsNull)
                {
                    var entity = tr.GetObject(_entityId, OpenMode.ForWrite, false) as Entity;
                    if (entity != null)
                    {
                        info.EntityId = _entityId.Handle.Value.ToString();
                        PanelInfoService.SavePanelInfo(entity, info, tr);
                    }
                }
                tr.Commit();
            }
            catch { tr.Abort(); throw; }
        }

        private void SetReadOnlyMode()
        {
            OrderIdTextBox.IsReadOnly = true;
            CabinetIdTextBox.IsReadOnly = true;
            RoomIdTextBox.IsReadOnly = true;
            PanelNameTextBox.IsReadOnly = true;
            MaterialTextBox.IsReadOnly = true;
        }

        private static string DeterminePanelType(string panelName)
        {
            if (string.IsNullOrWhiteSpace(panelName)) return "其他";
            var type = PanelNumberingService.GetPanelTypeShortName(panelName);
            if (type != "未知" && type != "其他") return type;
            if (panelName.Contains("收口板")) return "收口板";
            if (panelName.Contains("辅助板")) return "辅助板";
            return "其他";
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
                Close();
        }

        private void DimensionLockCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            UpdateDimensionEditorState();
        }

        private void DimensionLockCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateDimensionEditorState();
        }

        /// <summary>格式化裁切尺寸显示（值=0时显示空，让用户输入公式）</summary>
		private static bool IsFormula(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return false;
			if (double.TryParse(text, out _)) return false;
			string upper = text.ToUpper();
			return upper.Contains("L") || upper.Contains("W") || upper.Contains("T") ||
			       upper.Contains("+") || upper.Contains("-") || upper.Contains("*") || upper.Contains("/");
		}

		private void UpdateExtraToolTips()
		{
			double L = _panelData.Length, W = _panelData.Width, T = _panelData.Height;
			string txtL = ExtraLengthTextBox.Text?.Trim() ?? "";
			string txtW = ExtraWidthTextBox.Text?.Trim() ?? "";
			string txtH = ExtraHeightTextBox.Text?.Trim() ?? "";
			double el = EvalDimensionFormula(txtL, L, W, T, _panelData, "L");
			double ew = EvalDimensionFormula(txtW, L, W, T, _panelData, "W");
			double eh = EvalDimensionFormula(txtH, L, W, T, _panelData, "H");
			ExtraLengthTextBox.ToolTip = $"计算公式: {(string.IsNullOrEmpty(txtL) ? "自动扣封边" : txtL)} -> 结果: {el:F1} mm";
			ExtraWidthTextBox.ToolTip  = $"计算公式: {(string.IsNullOrEmpty(txtW) ? "自动扣封边" : txtW)} -> 结果: {ew:F1} mm";
			ExtraHeightTextBox.ToolTip = $"计算公式: {(string.IsNullOrEmpty(txtH) ? "自动扣封边" : txtH)} -> 结果: {eh:F1} mm";
		}

		private static string FormatDimensionFormula(double val, double L, double W, double T)
		{
			return val > 0 ? val.ToString("F1") : "";
		}

		/// <summary>
		/// 评估裁切尺寸公式
		/// 支持变量：L=显示长, W=显示宽, T=显示厚
		/// 支持运算：+ - * / ( )
		/// 空值 → 自动扣封边
		/// </summary>
		private static double EvalDimensionFormula(string formula, double L, double W, double T, PanelInfo info, string dimLabel)
		{
			// 空值 → 从封边自动计算
			if (string.IsNullOrWhiteSpace(formula))
				return CalcDefaultExtra(info, dimLabel, L, W, T);

			// 纯数字
			if (double.TryParse(formula, out double num))
				return Math.Round(num, 2);

			// 公式求值
			try
			{
				string expr = formula.ToUpper()
					.Replace("L", L.ToString("F6"))
					.Replace("W", W.ToString("F6"))
					.Replace("T", T.ToString("F6"));
				double result = EvaluateSimpleExpression(expr);
				return Math.Round(result, 2);
			}
			catch
			{
				return CalcDefaultExtra(info, dimLabel, L, W, T);
			}
		}

		/// <summary>根据封边自动计算裁切尺寸（从封边文本提取实际厚度）</summary>
		private static double CalcDefaultExtra(PanelInfo info, string dimLabel, double L, double W, double T)
		{
			double GetEdgeDeduction(string edgeText)
			{
				if (string.IsNullOrEmpty(edgeText)) return 0;
				var m = System.Text.RegularExpressions.Regex.Match(edgeText.Trim(), @"^(\d+\.?\d*)");
				if (m.Success && double.TryParse(m.Groups[1].Value, out double v)) return v;
				return 0;
			}
			if (dimLabel == "L")
			{
				double ded = GetEdgeDeduction(info.EdgeTop) + GetEdgeDeduction(info.EdgeBottom);
				return Math.Round(L - ded, 2);
			}
			if (dimLabel == "W")
			{
				double ded = GetEdgeDeduction(info.EdgeLeft) + GetEdgeDeduction(info.EdgeRight);
				return Math.Round(W - ded, 2);
			}
			return Math.Round(T, 2);
		}


        #endregion

        private static double EvaluateSimpleExpression(string expr)
        {
            expr = expr.Replace(" ", "");
            if (string.IsNullOrEmpty(expr)) return 0;
            return ParseAddSubtract(ref expr);
        }
        private static double ParseAddSubtract(ref string expr)
        {
            double left = ParseMultiplyDivide(ref expr);
            while (expr.Length > 0 && (expr[0] == '+' || (expr[0] == '-' && expr.Length > 1)))
            {
                char op = expr[0]; expr = expr.Substring(1);
                double right = ParseMultiplyDivide(ref expr);
                left = op == '+' ? left + right : left - right;
            }
            return left;
        }
        private static double ParseMultiplyDivide(ref string expr)
        {
            double left = ParseUnary(ref expr);
            while (expr.Length > 0 && (expr[0] == '*' || expr[0] == '/'))
            {
                char op = expr[0]; expr = expr.Substring(1);
                double right = ParseUnary(ref expr);
                if (op == '/' && Math.Abs(right) < 1e-12) return 0;
                left = op == '*' ? left * right : left / right;
            }
            return left;
        }
        private static double ParseUnary(ref string expr)
        {
            if (expr.Length > 0 && expr[0] == '-')
            {
                expr = expr.Substring(1);
                return -ParsePrimary(ref expr);
            }
            return ParsePrimary(ref expr);
        }
        private static double ParsePrimary(ref string expr)
        {
            if (expr.Length == 0) return 0;
            if (expr[0] == '(')
            {
                expr = expr.Substring(1);
                double val = ParseAddSubtract(ref expr);
                if (expr.Length > 0 && expr[0] == ')')
                    expr = expr.Substring(1);
                return val;
            }
            int i = 0;
            while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.'))
                i++;
            if (i == 0) return 0;
            double num = double.Parse(expr.Substring(0, i));
            expr = expr.Substring(i);
            return num;
        }
    }

    internal class EdgeBandCatalogWindow : Window
    {
        private readonly ObservableCollection<string> _items;
        private readonly ListBox _list;
        private readonly TextBox _editor;
        public List<string> CatalogItems => _items.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();

        public EdgeBandCatalogWindow(List<string> items)
        {
            Title = "自定义封边名称";
            Width = 520;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            Background = System.Windows.Media.Brushes.White;

            _items = new ObservableCollection<string>((items ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)));
            _list = new ListBox { Margin = new Thickness(0, 0, 0, 8), ItemsSource = _items };
            _editor = new TextBox { Margin = new Thickness(0, 0, 0, 8), MinHeight = 28 };
            _list.SelectionChanged += (_, __) => _editor.Text = _list.SelectedItem?.ToString() ?? "";

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(new TextBlock
            {
                Text = "封边命名建议格式：厚度*宽度 名称（示例：0.5*22 爱格W1000 ABS封边条）",
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });

            var center = new Grid();
            center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(center, 1);
            center.Children.Add(_list);
            Grid.SetRow(_editor, 1);
            center.Children.Add(_editor);
            root.Children.Add(center);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            void AddButton(string text, RoutedEventHandler click)
            {
                row.Children.Add(new Button
                {
                    Content = text,
                    Margin = new Thickness(4, 0, 0, 0),
                    Padding = new Thickness(12, 6, 12, 6),
                    MinWidth = 78
                });
                ((Button)row.Children[row.Children.Count - 1]).Click += click;
            }

            AddButton("新增", (_, __) =>
            {
                var value = _editor.Text?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value) && !_items.Contains(value)) _items.Add(value);
            });
            AddButton("修改", (_, __) =>
            {
                if (_list.SelectedIndex < 0) return;
                var value = _editor.Text?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value)) _items[_list.SelectedIndex] = value;
            });
            AddButton("删除", (_, __) =>
            {
                if (_list.SelectedIndex < 0) return;
                _items.RemoveAt(_list.SelectedIndex);
            });
            AddButton("保存", (_, __) => { DialogResult = true; Close(); });
            AddButton("取消", (_, __) => { DialogResult = false; Close(); });

            Grid.SetRow(row, 2);
            root.Children.Add(row);
            Content = root;
        }
    }
}
