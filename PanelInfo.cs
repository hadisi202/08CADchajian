using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace FurniturePlugin
{
    /// <summary>
    /// 板件计算类型枚举
    /// </summary>
    public enum PanelCalculationType
    {
        /// <summary>
        /// AABB计算（轴对齐包围盒）—— 常规矩形板，三轴边平行 XYZ
        /// </summary>
        AABB,
        /// <summary>
        /// OBB计算（有向包围盒）—— 旋转过的矩形板
        /// </summary>
        OBB,
        /// <summary>
        /// 圆弧板件计算（弧形展开）
        /// </summary>
        ArcPanel,
        /// <summary>
        /// 异形板（外轮廓非矩形：含半圆/扇形/C 型缺口/多孔/样条等）。
        /// 尺寸采用 OBB 的最小有向包围盒，并以 FLATSHOT 真实轮廓兜底。
        /// </summary>
        Irregular,
        /// <summary>
        /// 样条导轨拉伸板件：截面两条轨均为 AutoCAD <c>Spline</c> 时，
        /// 长度按「归一化弧长对齐 + 采样点连线中点的折线累加」（SETI FastDimension）。
        /// </summary>
        SplinePanel
    }

    /// <summary>
    /// 纹理方向枚举
    /// </summary>
    public enum TextureDirection
    {
        /// <summary>
        /// 沿长边
        /// </summary>
        AlongLength,
        /// <summary>
        /// 沿宽边
        /// </summary>
        AlongWidth
    }

    /// <summary>
    /// 实体用途类型
    /// </summary>
    public enum EntityKind
    {
        Panel,
        Hardware
    }

/// <summary>
/// 圆弧板件边长口径
/// </summary>
public enum ArcLengthReferenceType
{
    Outer,
    Center,
    Inner
}

    /// <summary>
    /// 用于存储板件信息的自定义数据结构。
    /// </summary>
    [Serializable] // 标记为可序列化，以便可以保存到扩展字典中
    public class PanelInfo : INotifyPropertyChanged
    {
        private string _orderId = "";
        private string _cabinetId = "";
        private string _roomId = ""; // 新增房间字段
        private string _panelName = "";
        private string _material = "";
        private double _length;
        private double _width;
        private double _height;
        private double _extraLength;
        private double _extraWidth;
        private double _extraHeight;
        private double _syncCalculation = 0; // 新增同步计算字段
        private string _edgeBanding = "";
        private string _paint = "";
        private string _remarks = "";
        private TextureDirection _textureDirection = TextureDirection.AlongLength; // 纹理方向
        private PanelCalculationType _calculationType = PanelCalculationType.AABB; // 板件计算类型
        private string _entityId = ""; // 实体ID
        private bool _isDimensionLocked = false; // 尺寸锁定状态
        private bool _isTextureVisible = true; // 纹路显示状态
        private bool _isCalculationTypeManual = false; // 板件形状是否由用户手动指定
        private bool _isTextureDirectionManual = false; // 纹理方向是否由用户手动指定（为true时WLXS不会自动纠正）
        private EntityKind _entityKind = EntityKind.Panel; // 实体用途：板件/五金
        private bool _excludeFromPaiBan = false; // 是否不参与排版
        private List<PanelHardwareItem> _hardwareItems = new(); // 板件五金配置表

        // 圆弧板件参数
        private double _arcInnerRadius;
        private double _arcAngleDegrees;
        private double _arcStraightLength1;
        private double _arcStraightLength2;
        private double _unfoldedLength;
        private double _unfoldedWidth;
        private double _arcOuterLength;
        private double _arcCenterLength;
        private double _arcInnerLengthValue;
        private ArcLengthReferenceType _arcLengthReference = ArcLengthReferenceType.Outer;

        // 裁切尺寸公式（用户输入的表达式，如 L+50）
        private string _extraLengthFormula = "";
        private string _extraWidthFormula = "";
        private string _extraHeightFormula = "";

        // 四边独立封边字段
        private string _edgeTop = "";       // 上边封边（沿长度方向的边）
        private string _edgeBottom = "";    // 下边封边
        private string _edgeLeft = "";      // 左边封边（沿宽度方向的边）
        private string _edgeRight = "";     // 右边封边

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
                    _length = value;
                    OnPropertyChanged();
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
                    _width = value;
                    OnPropertyChanged();
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
                    _extraLength = value;
                    OnPropertyChanged();
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
                    _extraWidth = value;
                    OnPropertyChanged();
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
                    _extraHeight = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>裁切长公式（用户输入表达式如L+50）</summary>
        public string ExtraLengthFormula
        {
            get => _extraLengthFormula;
            set { if (_extraLengthFormula != value) { _extraLengthFormula = value ?? ""; OnPropertyChanged(); } }
        }

        /// <summary>裁切宽公式</summary>
        public string ExtraWidthFormula
        {
            get => _extraWidthFormula;
            set { if (_extraWidthFormula != value) { _extraWidthFormula = value ?? ""; OnPropertyChanged(); } }
        }

        /// <summary>裁切厚公式</summary>
        public string ExtraHeightFormula
        {
            get => _extraHeightFormula;
            set { if (_extraHeightFormula != value) { _extraHeightFormula = value ?? ""; OnPropertyChanged(); } }
        }

        public double SyncCalculation
        {
            get => _syncCalculation;
            set
            {
                if (Math.Abs(_syncCalculation - value) > 1e-9)
                {
                    _syncCalculation = value;
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

        /// <summary>
        /// 纹理方向
        /// </summary>
        public TextureDirection TextureDirection
        {
            get => _textureDirection;
            set
            {
                if (_textureDirection != value)
                {
                    _textureDirection = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 板件计算类型
        /// </summary>
        public PanelCalculationType CalculationType
        {
            get => _calculationType;
            set
            {
                if (_calculationType != value)
                {
                    _calculationType = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 板件形状是否由用户手动指定；为 true 时，自动计算尺寸不应覆盖 CalculationType。
        /// </summary>
        public bool IsCalculationTypeManual
        {
            get => _isCalculationTypeManual;
            set
            {
                if (_isCalculationTypeManual != value)
                {
                    _isCalculationTypeManual = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 纹理方向是否由用户手动指定；为 true 时，WLXS 不会对该板件自动纠正纹理方向。
        /// </summary>
        public bool IsTextureDirectionManual
        {
            get => _isTextureDirectionManual;
            set
            {
                if (_isTextureDirectionManual != value)
                {
                    _isTextureDirectionManual = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 实体用途：板件或五金。
        /// </summary>
        public EntityKind EntityKind
        {
            get => _entityKind;
            set
            {
                if (_entityKind != value)
                {
                    _entityKind = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 是否从 PAIBAN 排版中排除。
        /// </summary>
        public bool ExcludeFromPaiBan
        {
            get => _excludeFromPaiBan;
            set
            {
                if (_excludeFromPaiBan != value)
                {
                    _excludeFromPaiBan = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 是否为圆弧板件（向后兼容属性）
        /// </summary>
        public bool IsArcPanel
        {
            get => _calculationType == PanelCalculationType.ArcPanel;
            set
            {
                if (value)
                {
                    if (_calculationType != PanelCalculationType.ArcPanel)
                    {
                        _calculationType = PanelCalculationType.ArcPanel;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(CalculationType));
                    }
                }
                else if (_calculationType == PanelCalculationType.ArcPanel)
                {
                    _calculationType = PanelCalculationType.AABB;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CalculationType));
                }
            }
        }

        /// <summary>
        /// 实体ID
        /// </summary>
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

        /// <summary>
        /// 尺寸锁定状态
        /// </summary>
        public bool IsDimensionLocked
        {
            get => _isDimensionLocked;
            set
            {
                if (_isDimensionLocked != value)
                {
                    _isDimensionLocked = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 纹路显示状态（true=显示，false=隐藏）
        /// </summary>
        public bool IsTextureVisible
        {
            get => _isTextureVisible;
            set
            {
                if (_isTextureVisible != value)
                {
                    _isTextureVisible = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 板件关联五金配置表
        /// </summary>
        public List<PanelHardwareItem> HardwareItems
        {
            get => _hardwareItems;
            set
            {
                _hardwareItems = value ?? new List<PanelHardwareItem>();
                OnPropertyChanged();
            }
        }

        /// <summary>圆弧板件内径</summary>
        public double ArcInnerRadius
        {
            get => _arcInnerRadius;
            set { if (Math.Abs(_arcInnerRadius - value) > 1e-9) { _arcInnerRadius = value; OnPropertyChanged(); } }
        }

        /// <summary>圆弧板件弧度角（度）</summary>
        public double ArcAngleDegrees
        {
            get => _arcAngleDegrees;
            set { if (Math.Abs(_arcAngleDegrees - value) > 1e-9) { _arcAngleDegrees = value; OnPropertyChanged(); } }
        }

        /// <summary>圆弧板件直段1长度</summary>
        public double ArcStraightLength1
        {
            get => _arcStraightLength1;
            set { if (Math.Abs(_arcStraightLength1 - value) > 1e-9) { _arcStraightLength1 = value; OnPropertyChanged(); } }
        }

        /// <summary>圆弧板件直段2长度</summary>
        public double ArcStraightLength2
        {
            get => _arcStraightLength2;
            set { if (Math.Abs(_arcStraightLength2 - value) > 1e-9) { _arcStraightLength2 = value; OnPropertyChanged(); } }
        }

        /// <summary>圆弧板件展开长度（只读计算值）</summary>
        public double UnfoldedLength
        {
            get => _unfoldedLength;
            set { if (Math.Abs(_unfoldedLength - value) > 1e-9) { _unfoldedLength = value; OnPropertyChanged(); } }
        }

        /// <summary>圆弧/样条板件展开宽度（只读计算值）</summary>
        public double UnfoldedWidth
        {
            get => _unfoldedWidth;
            set { if (Math.Abs(_unfoldedWidth - value) > 1e-9) { _unfoldedWidth = value; OnPropertyChanged(); } }
        }

        /// <summary>圆弧板件外线总边长。</summary>
        public double ArcOuterLength
        {
            get => _arcOuterLength;
            set { if (Math.Abs(_arcOuterLength - value) > 1e-9) { _arcOuterLength = value; OnPropertyChanged(); } }
        }

        /// <summary>圆弧板件中线总边长。</summary>
        public double ArcCenterLength
        {
            get => _arcCenterLength;
            set { if (Math.Abs(_arcCenterLength - value) > 1e-9) { _arcCenterLength = value; OnPropertyChanged(); } }
        }

        /// <summary>圆弧板件内线总边长。</summary>
        public double ArcInnerLengthValue
        {
            get => _arcInnerLengthValue;
            set { if (Math.Abs(_arcInnerLengthValue - value) > 1e-9) { _arcInnerLengthValue = value; OnPropertyChanged(); } }
        }

        /// <summary>SETI/保存数据时使用的圆弧边长口径。默认外线。</summary>
        public ArcLengthReferenceType ArcLengthReference
        {
            get => _arcLengthReference;
            set
            {
                if (_arcLengthReference != value)
                {
                    _arcLengthReference = value;
                    OnPropertyChanged();
                }
            }
        }

        public double GetPreferredArcLength()
        {
            double fallback = UnfoldedLength > 0 ? UnfoldedLength : Length;
            return ArcLengthReference switch
            {
                ArcLengthReferenceType.Inner => ArcInnerLengthValue > 0 ? ArcInnerLengthValue : (ArcCenterLength > 0 ? ArcCenterLength : fallback),
                ArcLengthReferenceType.Center => ArcCenterLength > 0 ? ArcCenterLength : (ArcOuterLength > 0 ? ArcOuterLength : fallback),
                _ => ArcOuterLength > 0 ? ArcOuterLength : (ArcCenterLength > 0 ? ArcCenterLength : fallback)
            };
        }

        /// <summary>
        /// 上边封边（沿长度方向）
        /// </summary>
        public string EdgeTop
        {
            get => _edgeTop;
            set
            {
                if (_edgeTop != value)
                {
                    _edgeTop = value;
                    OnPropertyChanged();
                    UpdateEdgeBandingFromSides();
                }
            }
        }

        /// <summary>
        /// 下边封边（沿长度方向）
        /// </summary>
        public string EdgeBottom
        {
            get => _edgeBottom;
            set
            {
                if (_edgeBottom != value)
                {
                    _edgeBottom = value;
                    OnPropertyChanged();
                    UpdateEdgeBandingFromSides();
                }
            }
        }

        /// <summary>
        /// 左边封边（沿宽度方向）
        /// </summary>
        public string EdgeLeft
        {
            get => _edgeLeft;
            set
            {
                if (_edgeLeft != value)
                {
                    _edgeLeft = value;
                    OnPropertyChanged();
                    UpdateEdgeBandingFromSides();
                }
            }
        }

        /// <summary>
        /// 右边封边（沿宽度方向）
        /// </summary>
        public string EdgeRight
        {
            get => _edgeRight;
            set
            {
                if (_edgeRight != value)
                {
                    _edgeRight = value;
                    OnPropertyChanged();
                    UpdateEdgeBandingFromSides();
                }
            }
        }

        /// <summary>
        /// 是否有任何封边
        /// </summary>
        public bool HasAnyEdgeBand => !string.IsNullOrEmpty(_edgeTop) || !string.IsNullOrEmpty(_edgeBottom) ||
                                      !string.IsNullOrEmpty(_edgeLeft) || !string.IsNullOrEmpty(_edgeRight);

        /// <summary>
        /// 封边边数
        /// </summary>
        public int EdgeBandCount => (_edgeTop != "" ? 1 : 0) + (_edgeBottom != "" ? 1 : 0) +
                                    (_edgeLeft != "" ? 1 : 0) + (_edgeRight != "" ? 1 : 0);

        /// <summary>
        /// 从四边封边字段同步更新 EdgeBanding 兼容字段
        /// </summary>
        private void UpdateEdgeBandingFromSides()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(_edgeTop)) parts.Add($"上-{_edgeTop}");
            if (!string.IsNullOrEmpty(_edgeBottom)) parts.Add($"下-{_edgeBottom}");
            if (!string.IsNullOrEmpty(_edgeLeft)) parts.Add($"左-{_edgeLeft}");
            if (!string.IsNullOrEmpty(_edgeRight)) parts.Add($"右-{_edgeRight}");
            _edgeBanding = parts.Count > 0 ? string.Join(",", parts) : "无封边";
            OnPropertyChanged(nameof(EdgeBanding));
            OnPropertyChanged(nameof(HasAnyEdgeBand));
            OnPropertyChanged(nameof(EdgeBandCount));
        }

        /// <summary>
        /// 从 EdgeBanding 字符串解析四边封边（用于兼容旧数据）
        /// </summary>
        public void ParseEdgeBandingToSides()
        {
            if (string.IsNullOrEmpty(_edgeBanding) || _edgeBanding == "无封边")
            {
                _edgeTop = _edgeBottom = _edgeLeft = _edgeRight = "";
                return;
            }

            // 尝试解析 "上-1mm封边,下-1mm封边,左-2mm封边,右-2mm封边" 格式
            foreach (var part in _edgeBanding.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("上-")) _edgeTop = trimmed.Substring(2);
                else if (trimmed.StartsWith("下-")) _edgeBottom = trimmed.Substring(2);
                else if (trimmed.StartsWith("左-")) _edgeLeft = trimmed.Substring(2);
                else if (trimmed.StartsWith("右-")) _edgeRight = trimmed.Substring(2);
                else if (!string.IsNullOrEmpty(trimmed))
                {
                    // 旧格式：无法区分四边，统一设置到所有可见边
                    _edgeTop = _edgeBottom = _edgeLeft = _edgeRight = trimmed;
                }
            }
        }

        /// <summary>
        /// 同步计算暂时只保留字段，不参与任何尺寸联动。
        /// 后续如需恢复逻辑，再在这里接入。
        /// </summary>
        private void UpdateExtraDimensionsFromSync()
        {
            // Intentionally left blank.
        }

        /// <summary>
        /// 手动触发同步计算更新（当长度或宽度改变时调用）
        /// </summary>
        public void TriggerSyncCalculationUpdate()
        {
            UpdateExtraDimensionsFromSync();
        }

        /// <summary>
        /// 字段分隔符
        /// </summary>
        private const string FieldSeparator = "|";
        
        /// <summary>
        /// 转义后的分隔符标记（用于字段值中包含 | 的情况）
        /// </summary>
        private const string EscapedPipe = "&#124;";
        
        public override string ToString()
        {
            var hardwareJson = System.Text.Json.JsonSerializer.Serialize(HardwareItems ?? new List<PanelHardwareItem>());
            return $"{Escape(OrderId)}|{Escape(CabinetId)}|{Escape(RoomId)}|{Escape(PanelName)}|{Escape(Material)}|{Length}|{Width}|{Height}|{ExtraLength}|{ExtraWidth}|{ExtraHeight}|{SyncCalculation}|{Escape(EdgeBanding)}|{Escape(Paint)}|{Escape(Remarks)}|{TextureDirection}|{CalculationType}|{IsArcPanel}|{Escape(EntityId)}|{IsDimensionLocked}|{IsTextureVisible}|{Escape(EdgeTop)}|{Escape(EdgeBottom)}|{Escape(EdgeLeft)}|{Escape(EdgeRight)}|{Escape(ExtraLengthFormula)}|{Escape(ExtraWidthFormula)}|{Escape(ExtraHeightFormula)}|{Escape(hardwareJson)}|{ArcInnerRadius}|{ArcAngleDegrees}|{ArcStraightLength1}|{ArcStraightLength2}|{UnfoldedLength}|{UnfoldedWidth}|{IsCalculationTypeManual}|{IsTextureDirectionManual}|{EntityKind}|{ExcludeFromPaiBan}|{ArcOuterLength}|{ArcCenterLength}|{ArcInnerLengthValue}|{ArcLengthReference}";
        }
        
        /// <summary>
        /// 转义字段值中的管道分隔符
        /// </summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("|", EscapedPipe);
        }
        
        /// <summary>
        /// 反转义字段值
        /// </summary>
        private static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace(EscapedPipe, "|");
        }
        
        /// <summary>
        /// 获取用于显示的格式化字符串
        /// </summary>
        public string ToDisplayString()
        {
            return $"订单号: {OrderId}, 柜号: {CabinetId}, 房间: {RoomId}, 板件名称: {PanelName}, 材质: {Material}, " +
                   $"尺寸: {Length:F2}x{Width:F2}x{Height:F2}, 加工尺寸: {ExtraLength:F2}x{ExtraWidth:F2}x{ExtraHeight:F2}, " +
                   $"封边: {EdgeBanding}, 油漆: {Paint}, 备注: {Remarks}";
        }
        
        /// <summary>
        /// 检查是否为空的板件信息（用户输入部分）
        /// </summary>
        public bool IsEmpty()
        {
            return string.IsNullOrWhiteSpace(OrderId) && 
                   string.IsNullOrWhiteSpace(CabinetId) && 
                   string.IsNullOrWhiteSpace(RoomId) && 
                   string.IsNullOrWhiteSpace(PanelName) && 
                   string.IsNullOrWhiteSpace(Material) && 
                   ExtraLength == 0 && ExtraWidth == 0 && ExtraHeight == 0 && 
                   SyncCalculation == 0 && 
                   string.IsNullOrWhiteSpace(EdgeBanding) && 
                   string.IsNullOrWhiteSpace(Paint) && 
                   string.IsNullOrWhiteSpace(Remarks);
        }
        
        /// <summary>
        /// 从另一个PanelInfo复制用户输入的信息（不包括自动计算的尺寸）
        /// </summary>
        public void CopyUserInputFrom(PanelInfo source)
        {
            if (source == null) return;
            
            OrderId = source.OrderId;
            CabinetId = source.CabinetId;
            RoomId = source.RoomId;
            PanelName = source.PanelName;
            Material = source.Material;
            ExtraLength = source.ExtraLength;
            ExtraWidth = source.ExtraWidth;
            ExtraHeight = source.ExtraHeight;
            ExtraLengthFormula = source.ExtraLengthFormula;
            ExtraWidthFormula = source.ExtraWidthFormula;
            ExtraHeightFormula = source.ExtraHeightFormula;
            SyncCalculation = source.SyncCalculation;
            EdgeBanding = source.EdgeBanding;
            Paint = source.Paint;
            Remarks = source.Remarks;
            TextureDirection = source.TextureDirection;
            IsTextureDirectionManual = source.IsTextureDirectionManual;
            CalculationType = source.CalculationType;
            IsCalculationTypeManual = source.IsCalculationTypeManual;
            EntityKind = source.EntityKind;
            ExcludeFromPaiBan = source.ExcludeFromPaiBan;
            IsDimensionLocked = source.IsDimensionLocked;
            IsTextureVisible = source.IsTextureVisible;
            HardwareItems = source.HardwareItems?.Select(h => h.Clone()).ToList() ?? new List<PanelHardwareItem>();
            EdgeTop = source.EdgeTop;
            EdgeBottom = source.EdgeBottom;
            EdgeLeft = source.EdgeLeft;
            EdgeRight = source.EdgeRight;
            ArcInnerRadius = source.ArcInnerRadius;
            ArcAngleDegrees = source.ArcAngleDegrees;
            ArcStraightLength1 = source.ArcStraightLength1;
            ArcStraightLength2 = source.ArcStraightLength2;
            UnfoldedLength = source.UnfoldedLength;
            UnfoldedWidth = source.UnfoldedWidth;
            ArcOuterLength = source.ArcOuterLength;
            ArcCenterLength = source.ArcCenterLength;
            ArcInnerLengthValue = source.ArcInnerLengthValue;
            ArcLengthReference = source.ArcLengthReference;
        }
        

        
        /// <summary>
        /// 从字符串反序列化PanelInfo
        /// </summary>
        public static PanelInfo FromString(string data)
        {
            if (string.IsNullOrEmpty(data)) return new PanelInfo();
            
            var parts = data.Split('|');
            var panelInfo = new PanelInfo();
            
            if (parts.Length >= 17)
            {
                panelInfo.OrderId = Unescape(parts[0]) ?? "";
                panelInfo.CabinetId = Unescape(parts[1]) ?? "";
                panelInfo.RoomId = Unescape(parts[2]) ?? "";
                panelInfo.PanelName = Unescape(parts[3]) ?? "";
                panelInfo.Material = Unescape(parts[4]) ?? "";
                double.TryParse(parts[5], out panelInfo._length);
                double.TryParse(parts[6], out panelInfo._width);
                double.TryParse(parts[7], out panelInfo._height);
                double.TryParse(parts[8], out panelInfo._extraLength);
                double.TryParse(parts[9], out panelInfo._extraWidth);
                double.TryParse(parts[10], out panelInfo._extraHeight);
                double.TryParse(parts[11], out panelInfo._syncCalculation);
                panelInfo.EdgeBanding = Unescape(parts[12]) ?? "";
                panelInfo.Paint = Unescape(parts[13]) ?? "";
                panelInfo.Remarks = Unescape(parts[14]) ?? "";
                if (Enum.TryParse<TextureDirection>(parts[15], out var texDir))
                {
                    panelInfo.TextureDirection = texDir;
                }
                
                // 处理CalculationType（向后兼容）
                if (Enum.TryParse<PanelCalculationType>(parts[16], out var calcType))
                {
                    panelInfo.CalculationType = calcType;
                }
                else if (bool.TryParse(parts[16], out bool isArcPanel))
                {
                    panelInfo.CalculationType = isArcPanel ? PanelCalculationType.ArcPanel : PanelCalculationType.AABB;
                }
                
                // 处理新增的IsArcPanel字段（如果存在）
                if (parts.Length >= 18 && bool.TryParse(parts[17], out bool isArcPanelNew) && isArcPanelNew)
                {
                    panelInfo.IsArcPanel = true;
                }
                
                // 处理EntityId字段（如果存在）
                if (parts.Length >= 19)
                {
                    panelInfo.EntityId = Unescape(parts[18]) ?? "";
                }
                
                // 处理IsDimensionLocked字段（如果存在）
                if (parts.Length >= 20 && bool.TryParse(parts[19], out bool isDimensionLocked))
                {
                    panelInfo.IsDimensionLocked = isDimensionLocked;
                }
                
                int indexShift = 0;
                if (parts.Length >= 21 && bool.TryParse(parts[20], out bool isTextureVisible))
                {
                    panelInfo.IsTextureVisible = isTextureVisible;
                    indexShift = 1;
                }
                else
                {
                    panelInfo.IsTextureVisible = true;
                }

                // 处理四边封边字段（兼容新增 IsTextureVisible 前后的版本）
                if (parts.Length >= 21 + indexShift) panelInfo._edgeTop = Unescape(parts[20 + indexShift]) ?? "";
                if (parts.Length >= 22 + indexShift) panelInfo._edgeBottom = Unescape(parts[21 + indexShift]) ?? "";
                if (parts.Length >= 23 + indexShift) panelInfo._edgeLeft = Unescape(parts[22 + indexShift]) ?? "";
                if (parts.Length >= 24 + indexShift) panelInfo._edgeRight = Unescape(parts[23 + indexShift]) ?? "";
                
                // 裁切公式字段（兼容新增 IsTextureVisible 前后的版本）
                if (parts.Length >= 25 + indexShift) panelInfo._extraLengthFormula = Unescape(parts[24 + indexShift]) ?? "";
                if (parts.Length >= 26 + indexShift) panelInfo._extraWidthFormula = Unescape(parts[25 + indexShift]) ?? "";
                if (parts.Length >= 27 + indexShift) panelInfo._extraHeightFormula = Unescape(parts[26 + indexShift]) ?? "";
                if (parts.Length >= 28 + indexShift)
                {
                    try
                    {
                        var hardwareJson = Unescape(parts[27 + indexShift]) ?? "[]";
                        panelInfo.HardwareItems = System.Text.Json.JsonSerializer.Deserialize<List<PanelHardwareItem>>(hardwareJson)
                                                  ?? new List<PanelHardwareItem>();
                    }
                    catch
                    {
                        panelInfo.HardwareItems = new List<PanelHardwareItem>();
                    }
                }

                // 圆弧板件参数（新增字段，向后兼容）
                int arcBase = 28 + indexShift;
                if (parts.Length >= arcBase + 1) double.TryParse(parts[arcBase], out panelInfo._arcInnerRadius);
                if (parts.Length >= arcBase + 2) double.TryParse(parts[arcBase + 1], out panelInfo._arcAngleDegrees);
                if (parts.Length >= arcBase + 3) double.TryParse(parts[arcBase + 2], out panelInfo._arcStraightLength1);
                if (parts.Length >= arcBase + 4) double.TryParse(parts[arcBase + 3], out panelInfo._arcStraightLength2);
                if (parts.Length >= arcBase + 5) double.TryParse(parts[arcBase + 4], out panelInfo._unfoldedLength);
                if (parts.Length >= arcBase + 6) double.TryParse(parts[arcBase + 5], out panelInfo._unfoldedWidth);
                if (parts.Length >= arcBase + 7 && bool.TryParse(parts[arcBase + 6], out bool isCalculationTypeManual))
                    panelInfo._isCalculationTypeManual = isCalculationTypeManual;
                if (parts.Length >= arcBase + 8 && bool.TryParse(parts[arcBase + 7], out bool isTextureDirectionManual))
                    panelInfo._isTextureDirectionManual = isTextureDirectionManual;
                if (parts.Length >= arcBase + 9 && Enum.TryParse<EntityKind>(parts[arcBase + 8], out var entityKind))
                    panelInfo._entityKind = entityKind;
                if (parts.Length >= arcBase + 10 && bool.TryParse(parts[arcBase + 9], out bool excludeFromPaiBan))
                    panelInfo._excludeFromPaiBan = excludeFromPaiBan;
                if (parts.Length >= arcBase + 11) double.TryParse(parts[arcBase + 10], out panelInfo._arcOuterLength);
                if (parts.Length >= arcBase + 12) double.TryParse(parts[arcBase + 11], out panelInfo._arcCenterLength);
                if (parts.Length >= arcBase + 13) double.TryParse(parts[arcBase + 12], out panelInfo._arcInnerLengthValue);
                if (parts.Length >= arcBase + 14 &&
                    Enum.TryParse<ArcLengthReferenceType>(parts[arcBase + 13], out var arcLengthReference))
                {
                    panelInfo._arcLengthReference = arcLengthReference;
                }

                // 如果没有四边封边数据，尝试从 EdgeBanding 字段解析
                if (parts.Length < 21 + indexShift && !string.IsNullOrEmpty(panelInfo.EdgeBanding) && panelInfo.EdgeBanding != "无封边")
                {
                    panelInfo.ParseEdgeBandingToSides();
                }
            }
            
            return panelInfo;
        }
    }

    [Serializable]
    public class PanelHardwareItem
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Spec { get; set; } = "";
        public double Quantity { get; set; } = 1;
        public string Unit { get; set; } = "件";
        public string Color { get; set; } = "";
        public string Remarks { get; set; } = "";

        public PanelHardwareItem Clone()
        {
            return new PanelHardwareItem
            {
                Index = Index,
                Name = Name,
                Spec = Spec,
                Quantity = Quantity,
                Unit = Unit,
                Color = Color,
                Remarks = Remarks
            };
        }
    }
}
