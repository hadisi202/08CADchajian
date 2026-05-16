using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FurniturePlugin
{
    /// <summary>
    /// 五金件类型枚举
    /// </summary>
    public enum HardwareType
    {
        /// <summary>铰链</summary>
        Hinge,
        /// <summary>滑轨</summary>
        SlideRail,
        /// <summary>拉手</summary>
        Handle,
        /// <summary>连接件</summary>
        Connector,
        /// <summary>层板托</summary>
        ShelfSupport,
        /// <summary>锁具</summary>
        Lock,
        /// <summary>衣通</summary>
        ClothesRail,
        /// <summary>裤架</summary>
        TrouserRack,
        /// <summary>领带架</summary>
        TieRack,
        /// <summary>穿衣镜</summary>
        Mirror,
        /// <summary>灯带</summary>
        LightStrip,
        /// <summary>其他</summary>
        Other
    }

    /// <summary>
    /// 五金件安装位置
    /// </summary>
    public enum InstallPosition
    {
        /// <summary>左侧板</summary>
        LeftPanel,
        /// <summary>右侧板</summary>
        RightPanel,
        /// <summary>顶板</summary>
        TopPanel,
        /// <summary>底板</summary>
        BottomPanel,
        /// <summary>层板</summary>
        Shelf,
        /// <summary>背板</summary>
        BackPanel,
        /// <summary>门板</summary>
        DoorPanel,
        /// <summary>抽屉面板</summary>
        DrawerFront,
        /// <summary>柜体内部</summary>
        Interior,
        /// <summary>其他</summary>
        Other
    }

    /// <summary>
    /// 五金件3D实体参数
    /// </summary>
    public class HardwareSolidParams
    {
        public string ShapeType { get; set; } = "Cylinder";
        public double BodyDiameter { get; set; } = 8;
        public double BodyDepth { get; set; } = 12;
        public double CapDiameter { get; set; }
        public double CapThickness { get; set; }
        public double ScrewDiameter { get; set; }
        public double ScrewLength { get; set; }
        public double BoxWidth { get; set; }
        public double BoxHeight { get; set; }
        public double BoxDepth { get; set; }
    }

    /// <summary>
    /// 五金件数据模型
    /// 每个五金件实例关联到一个具体的板件实体
    /// </summary>
    public class HardwareInfo : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _name = "";
        private string _model = "";       // 型号/规格
        private string _brand = "";       // 品牌
        private HardwareType _type = HardwareType.Other;
        private string _material = "";    // 材质（如不锈钢、铝合金等）
        private double _quantity = 1;
        private string _unit = "个";
        private string _associatedPanelId = "";  // 关联的板件EntityId
        private InstallPosition _installPosition = InstallPosition.Other;
        private string _installDescription = ""; // 安装说明
        private string _supplier = "";     // 供应商
        private double _unitPrice;
        private string _orderId = "";
        private string _cabinetId = "";
        private string _roomId = "";
        private string _remarks = "";

        // 3D实体参数 — 用于在CAD中生成五金件3D实体和布尔运算
        private HardwareSolidParams _solidParams;

        /// <summary>是否在CAD中显示3D实体</summary>
        private bool _showSolid = false;

        /// <summary>3D实体Handle（用于查找已创建的实体）</summary>
        private string _solidEntityHandle = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>唯一ID</summary>
        public string Id
        {
            get => _id;
            set { if (_id != value) { _id = value; OnPropertyChanged(); } }
        }

        /// <summary>五金件名称</summary>
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        /// <summary>型号/规格</summary>
        public string Model
        {
            get => _model;
            set { if (_model != value) { _model = value; OnPropertyChanged(); } }
        }

        /// <summary>品牌</summary>
        public string Brand
        {
            get => _brand;
            set { if (_brand != value) { _brand = value; OnPropertyChanged(); } }
        }

        /// <summary>五金件类型</summary>
        public HardwareType Type
        {
            get => _type;
            set { if (_type != value) { _type = value; OnPropertyChanged(); } }
        }

        /// <summary>材质</summary>
        public string Material
        {
            get => _material;
            set { if (_material != value) { _material = value; OnPropertyChanged(); } }
        }

        /// <summary>数量</summary>
        public double Quantity
        {
            get => _quantity;
            set { if (Math.Abs(_quantity - value) > 1e-9) { _quantity = value; OnPropertyChanged(); } }
        }

        /// <summary>单位</summary>
        public string Unit
        {
            get => _unit;
            set { if (_unit != value) { _unit = value; OnPropertyChanged(); } }
        }

        /// <summary>关联的板件EntityId（用于绑定到CAD实体）</summary>
        public string AssociatedPanelId
        {
            get => _associatedPanelId;
            set { if (_associatedPanelId != value) { _associatedPanelId = value; OnPropertyChanged(); } }
        }

        /// <summary>安装位置</summary>
        public InstallPosition InstallPosition
        {
            get => _installPosition;
            set { if (_installPosition != value) { _installPosition = value; OnPropertyChanged(); } }
        }

        /// <summary>安装说明</summary>
        public string InstallDescription
        {
            get => _installDescription;
            set { if (_installDescription != value) { _installDescription = value; OnPropertyChanged(); } }
        }

        /// <summary>供应商</summary>
        public string Supplier
        {
            get => _supplier;
            set { if (_supplier != value) { _supplier = value; OnPropertyChanged(); } }
        }

        /// <summary>单价</summary>
        public double UnitPrice
        {
            get => _unitPrice;
            set { if (Math.Abs(_unitPrice - value) > 1e-9) { _unitPrice = value; OnPropertyChanged(); } }
        }

        /// <summary>总价</summary>
        public double TotalPrice => _quantity * _unitPrice;

        /// <summary>订单号</summary>
        public string OrderId
        {
            get => _orderId;
            set { if (_orderId != value) { _orderId = value; OnPropertyChanged(); } }
        }

        /// <summary>柜号</summary>
        public string CabinetId
        {
            get => _cabinetId;
            set { if (_cabinetId != value) { _cabinetId = value; OnPropertyChanged(); } }
        }

        /// <summary>房间</summary>
        public string RoomId
        {
            get => _roomId;
            set { if (_roomId != value) { _roomId = value; OnPropertyChanged(); } }
        }

        /// <summary>备注</summary>
        public string Remarks
        {
            get => _remarks;
            set { if (_remarks != value) { _remarks = value; OnPropertyChanged(); } }
        }

        /// <summary>3D实体参数（用于生成五金件3D实体）</summary>
        public HardwareSolidParams SolidParams
        {
            get => _solidParams;
            set { if (_solidParams != value) { _solidParams = value; OnPropertyChanged(); } }
        }

        /// <summary>是否在CAD中显示3D实体</summary>
        public bool ShowSolid
        {
            get => _showSolid;
            set { if (_showSolid != value) { _showSolid = value; OnPropertyChanged(); } }
        }

        /// <summary>3D实体Handle</summary>
        public string SolidEntityHandle
        {
            get => _solidEntityHandle;
            set { if (_solidEntityHandle != value) { _solidEntityHandle = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// 获取五金件类型的中文显示名
        /// </summary>
        public string TypeDisplayName => GetTypeDisplayName(_type);

        /// <summary>
        /// 获取安装位置的中文显示名
        /// </summary>
        public string PositionDisplayName => GetPositionDisplayName(_installPosition);

        #region 静态辅助方法

        public static string GetTypeDisplayName(HardwareType type) => type switch
        {
            HardwareType.Hinge => "铰链",
            HardwareType.SlideRail => "滑轨",
            HardwareType.Handle => "拉手",
            HardwareType.Connector => "连接件",
            HardwareType.ShelfSupport => "层板托",
            HardwareType.Lock => "锁具",
            HardwareType.ClothesRail => "衣通",
            HardwareType.TrouserRack => "裤架",
            HardwareType.TieRack => "领带架",
            HardwareType.Mirror => "穿衣镜",
            HardwareType.LightStrip => "灯带",
            _ => "其他"
        };

        public static string GetPositionDisplayName(InstallPosition pos) => pos switch
        {
            InstallPosition.LeftPanel => "左侧板",
            InstallPosition.RightPanel => "右侧板",
            InstallPosition.TopPanel => "顶板",
            InstallPosition.BottomPanel => "底板",
            InstallPosition.Shelf => "层板",
            InstallPosition.BackPanel => "背板",
            InstallPosition.DoorPanel => "门板",
            InstallPosition.DrawerFront => "抽屉面板",
            InstallPosition.Interior => "柜体内部",
            _ => "其他"
        };

        /// <summary>
        /// 所有五金件类型列表（用于UI下拉）
        /// </summary>
        public static List<KeyValuePair<HardwareType, string>> AllTypes => new()
        {
            new(HardwareType.Hinge, "铰链"),
            new(HardwareType.SlideRail, "滑轨"),
            new(HardwareType.Handle, "拉手"),
            new(HardwareType.Connector, "连接件"),
            new(HardwareType.ShelfSupport, "层板托"),
            new(HardwareType.Lock, "锁具"),
            new(HardwareType.ClothesRail, "衣通"),
            new(HardwareType.TrouserRack, "裤架"),
            new(HardwareType.TieRack, "领带架"),
            new(HardwareType.Mirror, "穿衣镜"),
            new(HardwareType.LightStrip, "灯带"),
            new(HardwareType.Other, "其他"),
        };

        /// <summary>
        /// 所有安装位置列表（用于UI下拉）
        /// </summary>
        public static List<KeyValuePair<InstallPosition, string>> AllPositions => new()
        {
            new(InstallPosition.LeftPanel, "左侧板"),
            new(InstallPosition.RightPanel, "右侧板"),
            new(InstallPosition.TopPanel, "顶板"),
            new(InstallPosition.BottomPanel, "底板"),
            new(InstallPosition.Shelf, "层板"),
            new(InstallPosition.BackPanel, "背板"),
            new(InstallPosition.DoorPanel, "门板"),
            new(InstallPosition.DrawerFront, "抽屉面板"),
            new(InstallPosition.Interior, "柜体内部"),
            new(InstallPosition.Other, "其他"),
        };

        #endregion
    }
}
