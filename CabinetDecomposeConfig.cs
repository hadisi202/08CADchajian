using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FurniturePlugin
{
    /// <summary>
    /// 柜体拆单参数配置
    /// 定义一个柜体的所有结构参数，用于自动生成拆单板件
    /// </summary>
    public class CabinetDecomposeConfig : INotifyPropertyChanged
    {
        #region 基本尺寸

        private double _cabinetWidth = 800;     // 柜体宽度(mm)
        private double _cabinetDepth = 600;     // 柜体深度(mm)
        private double _cabinetHeight = 2400;   // 柜体高度(mm)
        private double _panelThickness = 18;    // 板件厚度(mm)

        #endregion

        #region 结构参数

        private double _backPanelThickness = 9;  // 背板厚度(mm)
        private bool _hasBackPanel = true;        // 是否有背板
        private bool _backPanelInsert = true;     // 背板嵌入（true）或外盖（false）
        private double _backPanelInset = 0;       // 背板内嵌深度(mm)

        private int _shelfCount = 2;              // 层板数量（不含顶底板）
        private double _shelfThickness = 18;      // 层板厚度(mm)
        private bool _shelfFixed = true;          // 层板固定（否则为活动层板）

        private int _doorCount = 2;               // 门板数量
        private double _doorThickness = 18;       // 门板厚度(mm)
        private double _doorGap = 2;              // 门缝间距(mm)
        private bool _hasDoor = true;             // 是否有门板

        private int _drawerCount = 0;             // 抽屉数量
        private double _drawerHeight = 150;       // 抽屉面板高度(mm)
        private double _drawerThickness = 18;     // 抽屉面板厚度(mm)
        private double _drawerBoxHeight = 100;    // 抽屉盒高度(mm)
        private bool _hasDrawer = false;          // 是否有抽屉

        private double _topClearance = 0;         // 顶板与天花板的间距
        private double _bottomKickHeight = 80;    // 踢脚板高度(mm)
        private bool _hasKickPlate = true;        // 是否有踢脚板

        #endregion

        #region 封边配置

        private string _sidePanelEdgeMaterial = "1mm同色封边";     // 侧板封边材料
        private bool _sidePanelEdgeTop = true;      // 侧板上边封边
        private bool _sidePanelEdgeBottom = true;   // 侧板下边封边
        private bool _sidePanelEdgeFront = true;    // 侧板前边封边
        private bool _sidePanelEdgeBack = false;    // 侧板后边封边

        private string _topBottomEdgeMaterial = "1mm同色封边";    // 顶底板封边材料
        private bool _topBottomEdgeFront = true;    // 顶底板前边封边
        private bool _topBottomEdgeBack = false;    // 顶底板后边封边
        private bool _topBottomEdgeLeft = false;    // 顶底板左边封边
        private bool _topBottomEdgeRight = false;   // 顶底板右边封边

        private string _shelfEdgeMaterial = "1mm同色封边";        // 层板封边材料
        private bool _shelfEdgeFront = true;        // 层板前边封边
        private bool _shelfEdgeBack = false;        // 层板后边封边
        private bool _shelfEdgeLeft = false;        // 层板左边封边
        private bool _shelfEdgeRight = false;       // 层板右边封边

        #endregion

        #region 业务信息

        private string _orderId = "";
        private string _cabinetId = "";
        private string _roomId = "";
        private string _material = "18mm多层实木板";
        private string _backPanelMaterial = "9mm密度板";

        #endregion

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region 属性

        public double CabinetWidth { get => _cabinetWidth; set { if (_cabinetWidth != value) { _cabinetWidth = value; OnPropertyChanged(); } } }
        public double CabinetDepth { get => _cabinetDepth; set { if (_cabinetDepth != value) { _cabinetDepth = value; OnPropertyChanged(); } } }
        public double CabinetHeight { get => _cabinetHeight; set { if (_cabinetHeight != value) { _cabinetHeight = value; OnPropertyChanged(); } } }
        public double PanelThickness { get => _panelThickness; set { if (_panelThickness != value) { _panelThickness = value; OnPropertyChanged(); } } }
        public double BackPanelThickness { get => _backPanelThickness; set { if (_backPanelThickness != value) { _backPanelThickness = value; OnPropertyChanged(); } } }
        public bool HasBackPanel { get => _hasBackPanel; set { if (_hasBackPanel != value) { _hasBackPanel = value; OnPropertyChanged(); } } }
        public bool BackPanelInsert { get => _backPanelInsert; set { if (_backPanelInsert != value) { _backPanelInsert = value; OnPropertyChanged(); } } }
        public double BackPanelInset { get => _backPanelInset; set { if (_backPanelInset != value) { _backPanelInset = value; OnPropertyChanged(); } } }
        public int ShelfCount { get => _shelfCount; set { if (_shelfCount != value) { _shelfCount = value; OnPropertyChanged(); } } }
        public double ShelfThickness { get => _shelfThickness; set { if (_shelfThickness != value) { _shelfThickness = value; OnPropertyChanged(); } } }
        public bool ShelfFixed { get => _shelfFixed; set { if (_shelfFixed != value) { _shelfFixed = value; OnPropertyChanged(); } } }
        public int DoorCount { get => _doorCount; set { if (_doorCount != value) { _doorCount = value; OnPropertyChanged(); } } }
        public double DoorThickness { get => _doorThickness; set { if (_doorThickness != value) { _doorThickness = value; OnPropertyChanged(); } } }
        public double DoorGap { get => _doorGap; set { if (_doorGap != value) { _doorGap = value; OnPropertyChanged(); } } }
        public bool HasDoor { get => _hasDoor; set { if (_hasDoor != value) { _hasDoor = value; OnPropertyChanged(); } } }
        public int DrawerCount { get => _drawerCount; set { if (_drawerCount != value) { _drawerCount = value; OnPropertyChanged(); } } }
        public double DrawerHeight { get => _drawerHeight; set { if (_drawerHeight != value) { _drawerHeight = value; OnPropertyChanged(); } } }
        public double DrawerThickness { get => _drawerThickness; set { if (_drawerThickness != value) { _drawerThickness = value; OnPropertyChanged(); } } }
        public double DrawerBoxHeight { get => _drawerBoxHeight; set { if (_drawerBoxHeight != value) { _drawerBoxHeight = value; OnPropertyChanged(); } } }
        public bool HasDrawer { get => _hasDrawer; set { if (_hasDrawer != value) { _hasDrawer = value; OnPropertyChanged(); } } }
        public double TopClearance { get => _topClearance; set { if (_topClearance != value) { _topClearance = value; OnPropertyChanged(); } } }
        public double BottomKickHeight { get => _bottomKickHeight; set { if (_bottomKickHeight != value) { _bottomKickHeight = value; OnPropertyChanged(); } } }
        public bool HasKickPlate { get => _hasKickPlate; set { if (_hasKickPlate != value) { _hasKickPlate = value; OnPropertyChanged(); } } }
        public string SidePanelEdgeMaterial { get => _sidePanelEdgeMaterial; set { if (_sidePanelEdgeMaterial != value) { _sidePanelEdgeMaterial = value; OnPropertyChanged(); } } }
        public bool SidePanelEdgeTop { get => _sidePanelEdgeTop; set { if (_sidePanelEdgeTop != value) { _sidePanelEdgeTop = value; OnPropertyChanged(); } } }
        public bool SidePanelEdgeBottom { get => _sidePanelEdgeBottom; set { if (_sidePanelEdgeBottom != value) { _sidePanelEdgeBottom = value; OnPropertyChanged(); } } }
        public bool SidePanelEdgeFront { get => _sidePanelEdgeFront; set { if (_sidePanelEdgeFront != value) { _sidePanelEdgeFront = value; OnPropertyChanged(); } } }
        public bool SidePanelEdgeBack { get => _sidePanelEdgeBack; set { if (_sidePanelEdgeBack != value) { _sidePanelEdgeBack = value; OnPropertyChanged(); } } }
        public string TopBottomEdgeMaterial { get => _topBottomEdgeMaterial; set { if (_topBottomEdgeMaterial != value) { _topBottomEdgeMaterial = value; OnPropertyChanged(); } } }
        public bool TopBottomEdgeFront { get => _topBottomEdgeFront; set { if (_topBottomEdgeFront != value) { _topBottomEdgeFront = value; OnPropertyChanged(); } } }
        public bool TopBottomEdgeBack { get => _topBottomEdgeBack; set { if (_topBottomEdgeBack != value) { _topBottomEdgeBack = value; OnPropertyChanged(); } } }
        public bool TopBottomEdgeLeft { get => _topBottomEdgeLeft; set { if (_topBottomEdgeLeft != value) { _topBottomEdgeLeft = value; OnPropertyChanged(); } } }
        public bool TopBottomEdgeRight { get => _topBottomEdgeRight; set { if (_topBottomEdgeRight != value) { _topBottomEdgeRight = value; OnPropertyChanged(); } } }
        public string ShelfEdgeMaterial { get => _shelfEdgeMaterial; set { if (_shelfEdgeMaterial != value) { _shelfEdgeMaterial = value; OnPropertyChanged(); } } }
        public bool ShelfEdgeFront { get => _shelfEdgeFront; set { if (_shelfEdgeFront != value) { _shelfEdgeFront = value; OnPropertyChanged(); } } }
        public bool ShelfEdgeBack { get => _shelfEdgeBack; set { if (_shelfEdgeBack != value) { _shelfEdgeBack = value; OnPropertyChanged(); } } }
        public bool ShelfEdgeLeft { get => _shelfEdgeLeft; set { if (_shelfEdgeLeft != value) { _shelfEdgeLeft = value; OnPropertyChanged(); } } }
        public bool ShelfEdgeRight { get => _shelfEdgeRight; set { if (_shelfEdgeRight != value) { _shelfEdgeRight = value; OnPropertyChanged(); } } }
        public string OrderId { get => _orderId; set { if (_orderId != value) { _orderId = value; OnPropertyChanged(); } } }
        public string CabinetId { get => _cabinetId; set { if (_cabinetId != value) { _cabinetId = value; OnPropertyChanged(); } } }
        public string RoomId { get => _roomId; set { if (_roomId != value) { _roomId = value; OnPropertyChanged(); } } }
        public string Material { get => _material; set { if (_material != value) { _material = value; OnPropertyChanged(); } } }
        public string BackPanelMaterial { get => _backPanelMaterial; set { if (_backPanelMaterial != value) { _backPanelMaterial = value; OnPropertyChanged(); } } }

        #endregion
    }

    /// <summary>
    /// 拆单结果中的单块板件
    /// </summary>
    public class DecomposedPanel
    {
        /// <summary>板件类型</summary>
        public string PanelType { get; set; } = "";

        /// <summary>板件名称</summary>
        public string PanelName { get; set; } = "";

        /// <summary>长度(开料尺寸)</summary>
        public double Length { get; set; }

        /// <summary>宽度(开料尺寸)</summary>
        public double Width { get; set; }

        /// <summary>厚度</summary>
        public double Thickness { get; set; }

        /// <summary>数量</summary>
        public int Quantity { get; set; } = 1;

        /// <summary>材质</summary>
        public string Material { get; set; } = "";

        /// <summary>纹理方向</summary>
        public TextureDirection TextureDirection { get; set; } = TextureDirection.AlongLength;

        /// <summary>封边信息（兼容旧格式，由四边封边自动合成）</summary>
        public string EdgeInfo { get; set; } = "";

        /// <summary>上边封边（沿长度方向）</summary>
        public string EdgeTop { get; set; } = "";

        /// <summary>下边封边（沿长度方向）</summary>
        public string EdgeBottom { get; set; } = "";

        /// <summary>左边封边（沿宽度方向）</summary>
        public string EdgeLeft { get; set; } = "";

        /// <summary>右边封边（沿宽度方向）</summary>
        public string EdgeRight { get; set; } = "";

        public List<HardwareInfo> AssociatedHardware { get; set; } = new();

        /// <summary>在柜体中的X坐标</summary>
        public double PositionX { get; set; }

        /// <summary>在柜体中的Y坐标</summary>
        public double PositionY { get; set; }

        /// <summary>在柜体中的Z坐标</summary>
        public double PositionZ { get; set; }

        /// <summary>是否需要三合一孔</summary>
        public bool NeedsConnectorHoles { get; set; }

        /// <summary>是否需要层板托孔</summary>
        public bool NeedsShelfPinHoles { get; set; }
    }
}
