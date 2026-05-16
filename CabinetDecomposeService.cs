using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FurniturePlugin
{
    /// <summary>
    /// 柜体拆单计算服务
    /// 根据柜体参数配置，自动计算所有板件的开料尺寸和位置
    /// 
    /// 柜体结构约定：
    /// - 坐标系：原点在柜体左下后角，X=宽(右)，Y=深(前)，Z=高(上)
    /// - 侧板：高度=柜高，深度=柜深（或柜深-背板厚度），厚度=板厚
    /// - 顶底板：长度=柜宽-2*板厚，深度=柜深-背板厚度，厚度=板厚
    /// - 层板：长度=柜宽-2*板厚，深度=柜深-背板厚度，厚度=层板厚
    /// - 背板：长度=柜宽，高度=柜高（嵌入时减2*板厚），厚度=背板厚
    /// - 门板：宽度=(柜宽-门缝)/门数，高度=柜高-门缝，厚度=门板厚
    /// </summary>
    public static class CabinetDecomposeService
    {
        /// <summary>
        /// 执行柜体拆单计算
        /// </summary>
        public static List<DecomposedPanel> Decompose(CabinetDecomposeConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var panels = new List<DecomposedPanel>();

            var W = config.CabinetWidth;
            var D = config.CabinetDepth;
            var H = config.CabinetHeight;
            var T = config.PanelThickness;     // 主板厚度
            var BT = config.BackPanelThickness; // 背板厚度

            // 1. 左侧板
            panels.Add(CreateSidePanel(
                "左侧板", "左侧板",
                length: H,
                width: D - (config.HasBackPanel && !config.BackPanelInsert ? BT : 0),
                thickness: T,
                material: config.Material,
                posX: 0, posY: 0, posZ: 0,
                config: config, isLeft: true));

            // 2. 右侧板
            panels.Add(CreateSidePanel(
                "右侧板", "右侧板",
                length: H,
                width: D - (config.HasBackPanel && !config.BackPanelInsert ? BT : 0),
                thickness: T,
                material: config.Material,
                posX: W - T, posY: 0, posZ: 0,
                config: config, isLeft: false));

            // 3. 顶板
            panels.Add(CreateTopBottomPanel(
                "顶板", "顶板",
                length: W - 2 * T,
                width: D - (config.HasBackPanel && !config.BackPanelInsert ? BT : 0) - (config.HasBackPanel && config.BackPanelInsert ? BT : 0),
                thickness: T,
                material: config.Material,
                posX: T, posY: 0, posZ: H - T,
                config: config, isTop: true));

            // 4. 底板
            double bottomZ = config.HasKickPlate ? config.BottomKickHeight : 0;
            panels.Add(CreateTopBottomPanel(
                "底板", "底板",
                length: W - 2 * T,
                width: D - (config.HasBackPanel && !config.BackPanelInsert ? BT : 0) - (config.HasBackPanel && config.BackPanelInsert ? BT : 0),
                thickness: T,
                material: config.Material,
                posX: T, posY: 0, posZ: bottomZ,
                config: config, isTop: false));

            // 5. 踢脚板
            if (config.HasKickPlate)
            {
                var kickPlate = new DecomposedPanel
                {
                    PanelType = "踢脚板",
                    PanelName = "踢脚板",
                    Length = W - 2 * T,
                    Width = D - (config.HasBackPanel ? BT : 0),
                    Thickness = T,
                    Quantity = 1,
                    Material = config.Material,
                    TextureDirection = TextureDirection.AlongLength,
                    PositionX = T,
                    PositionY = 0,
                    PositionZ = 0,
                    NeedsConnectorHoles = true,
                    // 踢脚板：前面封边
                    EdgeTop = "",
                    EdgeBottom = "",
                    EdgeLeft = "",
                    EdgeRight = config.SidePanelEdgeMaterial
                };
                kickPlate.EdgeInfo = BuildEdgeInfoString(kickPlate);
                panels.Add(kickPlate);
            }

            // 6. 层板
            if (config.ShelfCount > 0)
            {
                double innerHeight = H - T - bottomZ - T; // 内部可用高度
                if (config.HasDoor) innerHeight -= config.DoorGap;

                for (int i = 0; i < config.ShelfCount; i++)
                {
                    double shelfZ = bottomZ + T + (innerHeight / (config.ShelfCount + 1)) * (i + 1);
                    var shelf = new DecomposedPanel
                    {
                        PanelType = config.ShelfFixed ? "固定层板" : "活动层板",
                        PanelName = config.ShelfFixed ? $"层板{i + 1}" : $"活动层板{i + 1}",
                        Length = W - 2 * T,
                        Width = D - (config.HasBackPanel && !config.BackPanelInsert ? BT : 0) - (config.HasBackPanel && config.BackPanelInsert ? BT : 0),
                        Thickness = config.ShelfThickness,
                        Quantity = 1,
                        Material = config.Material,
                        TextureDirection = TextureDirection.AlongLength,
                        PositionX = T,
                        PositionY = config.HasBackPanel && config.BackPanelInsert ? BT : 0,
                        PositionZ = shelfZ,
                        NeedsConnectorHoles = config.ShelfFixed,
                        NeedsShelfPinHoles = !config.ShelfFixed,
                        // 层板封边：前边封，后边可选
                        EdgeTop = "",
                        EdgeBottom = "",
                        EdgeLeft = config.ShelfEdgeFront ? config.ShelfEdgeMaterial : "",
                        EdgeRight = config.ShelfEdgeBack ? config.ShelfEdgeMaterial : ""
                    };
                    shelf.EdgeInfo = BuildEdgeInfoString(shelf);
                    panels.Add(shelf);
                }
            }

            // 7. 背板
            if (config.HasBackPanel)
            {
                double bpLength, bpWidth, bpX, bpY;
                if (config.BackPanelInsert)
                {
                    bpLength = W - 2 * T;
                    bpWidth = H - 2 * T;
                    bpX = T;
                    bpY = D - BT;
                }
                else
                {
                    bpLength = W;
                    bpWidth = H;
                    bpX = 0;
                    bpY = D - BT;
                }

                var backPanel = new DecomposedPanel
                {
                    PanelType = "背板",
                    PanelName = "背板",
                    Length = bpLength,
                    Width = bpWidth,
                    Thickness = BT,
                    Quantity = 1,
                    Material = config.BackPanelMaterial,
                    TextureDirection = TextureDirection.AlongWidth,
                    PositionX = bpX,
                    PositionY = bpY,
                    PositionZ = 0,
                    NeedsConnectorHoles = false,
                    // 背板不封边
                    EdgeTop = "",
                    EdgeBottom = "",
                    EdgeLeft = "",
                    EdgeRight = ""
                };
                backPanel.EdgeInfo = "无封边";
                panels.Add(backPanel);
            }

            // 8. 门板
            if (config.HasDoor && config.DoorCount > 0)
            {
                double doorWidth = (W - config.DoorGap * (config.DoorCount + 1)) / config.DoorCount;
                double doorHeight = H - (config.HasKickPlate ? config.BottomKickHeight : 0) - config.DoorGap;

                for (int i = 0; i < config.DoorCount; i++)
                {
                    double doorX = config.DoorGap + i * (doorWidth + config.DoorGap);
                    double doorZ = (config.HasKickPlate ? config.BottomKickHeight : 0) + config.DoorGap / 2;

                    var doorPanel = new DecomposedPanel
                    {
                        PanelType = "门板",
                        PanelName = config.DoorCount > 1 ? $"门板{i + 1}" : "门板",
                        Length = doorHeight,
                        Width = doorWidth,
                        Thickness = config.DoorThickness,
                        Quantity = 1,
                        Material = config.Material,
                        TextureDirection = TextureDirection.AlongLength,
                        PositionX = doorX,
                        PositionY = D + 1,
                        PositionZ = doorZ,
                        NeedsConnectorHoles = false,
                        // 门板四边封边
                        EdgeTop = config.SidePanelEdgeMaterial,
                        EdgeBottom = config.SidePanelEdgeMaterial,
                        EdgeLeft = config.SidePanelEdgeMaterial,
                        EdgeRight = config.SidePanelEdgeMaterial
                    };
                    doorPanel.EdgeInfo = BuildEdgeInfoString(doorPanel);
                    panels.Add(doorPanel);
                }
            }

            // 9. 抽屉
            if (config.HasDrawer && config.DrawerCount > 0)
            {
                for (int i = 0; i < config.DrawerCount; i++)
                {
                    double drawerZ = bottomZ + T + i * (config.DrawerHeight + config.DoorGap);

                    // 抽屉面板
                    var drawerFront = new DecomposedPanel
                    {
                        PanelType = "抽屉面板",
                        PanelName = $"抽屉面板{i + 1}",
                        Length = config.DrawerHeight,
                        Width = W - 2 * T - 2,
                        Thickness = config.DrawerThickness,
                        Quantity = 1,
                        Material = config.Material,
                        TextureDirection = TextureDirection.AlongLength,
                        PositionX = T + 1,
                        PositionY = D + 1,
                        PositionZ = drawerZ,
                        NeedsConnectorHoles = false,
                        // 抽屉面板四边封边
                        EdgeTop = config.SidePanelEdgeMaterial,
                        EdgeBottom = config.SidePanelEdgeMaterial,
                        EdgeLeft = config.SidePanelEdgeMaterial,
                        EdgeRight = config.SidePanelEdgeMaterial
                    };
                    drawerFront.EdgeInfo = BuildEdgeInfoString(drawerFront);
                    panels.Add(drawerFront);

                    // 抽屉盒左右侧板
                    for (int side = 0; side < 2; side++)
                    {
                        panels.Add(new DecomposedPanel
                        {
                            PanelType = "抽屉盒侧板",
                            PanelName = $"抽屉{i + 1}_{(side == 0 ? "左" : "右")}侧板",
                            Length = config.DrawerBoxHeight,
                            Width = D - T - 30,
                            Thickness = 12,
                            Quantity = 1,
                            Material = "12mm多层板",
                            TextureDirection = TextureDirection.AlongLength,
                            PositionX = side == 0 ? T + 1 : W - T - 13,
                            PositionY = 0,
                            PositionZ = drawerZ + 2,
                            NeedsConnectorHoles = false,
                            EdgeTop = "", EdgeBottom = "", EdgeLeft = "", EdgeRight = ""
                        });
                        panels[panels.Count - 1].EdgeInfo = "无封边";
                    }

                    // 抽屉盒背板
                    panels.Add(new DecomposedPanel
                    {
                        PanelType = "抽屉盒背板",
                        PanelName = $"抽屉{i + 1}_背板",
                        Length = config.DrawerBoxHeight,
                        Width = W - 2 * T - 26,
                        Thickness = 12,
                        Quantity = 1,
                        Material = "12mm多层板",
                        TextureDirection = TextureDirection.AlongWidth,
                        PositionX = T + 13,
                        PositionY = 10,
                        PositionZ = drawerZ + 2,
                        NeedsConnectorHoles = false,
                        EdgeTop = "", EdgeBottom = "", EdgeLeft = "", EdgeRight = ""
                    });
                    panels[panels.Count - 1].EdgeInfo = "无封边";

                    // 抽屉盒底板
                    panels.Add(new DecomposedPanel
                    {
                        PanelType = "抽屉盒底板",
                        PanelName = $"抽屉{i + 1}_底板",
                        Length = W - 2 * T - 26,
                        Width = D - T - 32,
                        Thickness = 9,
                        Quantity = 1,
                        Material = "9mm密度板",
                        TextureDirection = TextureDirection.AlongLength,
                        PositionX = T + 13,
                        PositionY = 0,
                        PositionZ = drawerZ + 2,
                        NeedsConnectorHoles = false,
                        EdgeTop = "", EdgeBottom = "", EdgeLeft = "", EdgeRight = ""
                    });
                    panels[panels.Count - 1].EdgeInfo = "无封边";
                }
            }

            // 设置业务信息 + 生成五金件
            foreach (var panel in panels)
            {
                // 面板名称加上柜号前缀
                if (!string.IsNullOrEmpty(config.CabinetId))
                    panel.PanelName = $"{config.CabinetId}-{panel.PanelName}";
            }

            // 自动生成五金件并关联到对应板件
            GenerateAndAssociateHardware(config, panels);

            return panels;
        }

        /// <summary>
        /// 生成五金件清单并关联到对应板件
        /// </summary>
        private static void GenerateAndAssociateHardware(CabinetDecomposeConfig config, List<DecomposedPanel> panels)
        {
            var allHardware = GenerateHardwareList(config, panels);

            // 将五金件关联到对应的板件
            foreach (var hw in allHardware)
            {
                // 根据安装位置找对应板件
                var targetPanels = hw.InstallPosition switch
                {
                    InstallPosition.LeftPanel => panels.Where(p => p.PanelType == "左侧板").ToList(),
                    InstallPosition.RightPanel => panels.Where(p => p.PanelType == "右侧板").ToList(),
                    InstallPosition.TopPanel => panels.Where(p => p.PanelType == "顶板").ToList(),
                    InstallPosition.BottomPanel => panels.Where(p => p.PanelType == "底板").ToList(),
                    InstallPosition.Shelf => panels.Where(p => p.PanelType.Contains("层板")).ToList(),
                    InstallPosition.DoorPanel => panels.Where(p => p.PanelType == "门板" || p.PanelType == "抽屉面板").ToList(),
                    InstallPosition.DrawerFront => panels.Where(p => p.PanelType == "抽屉面板").ToList(),
                    InstallPosition.Interior => panels.Where(p => p.PanelType == "左侧板" || p.PanelType == "右侧板").ToList(),
                    _ => new List<DecomposedPanel>()
                };

                foreach (var tp in targetPanels)
                {
                    tp.AssociatedHardware.Add(hw);
                }
            }
        }

        /// <summary>
        /// 生成拆单板件的五金件清单
        /// </summary>
        public static List<HardwareInfo> GenerateHardwareList(CabinetDecomposeConfig config, List<DecomposedPanel> panels)
        {
            var hardwareList = new List<HardwareInfo>();

            // 三合一连接件
            int connectorPerJoint = 3;
            hardwareList.Add(new HardwareInfo
            {
                Name = "三合一连接件",
                Type = HardwareType.Connector,
                Model = "三合一+木榫",
                Quantity = connectorPerJoint * 4,
                Unit = "套",
                UnitPrice = 0.5,
                InstallPosition = InstallPosition.LeftPanel,
                OrderId = config.OrderId,
                CabinetId = config.CabinetId,
                RoomId = config.RoomId
            });

            // 铰链
            if (config.HasDoor && config.DoorCount > 0)
            {
                int hingePerDoor = config.CabinetHeight > 1800 ? 3 : 2;
                hardwareList.Add(new HardwareInfo
                {
                    Name = "全盖铰链",
                    Type = HardwareType.Hinge,
                    Model = "DTC 35mm全盖阻尼铰链",
                    Brand = "DTC",
                    Quantity = hingePerDoor * config.DoorCount,
                    Unit = "个",
                    UnitPrice = 4.5,
                    InstallPosition = InstallPosition.DoorPanel,
                    OrderId = config.OrderId,
                    CabinetId = config.CabinetId,
                    RoomId = config.RoomId
                });
            }

            // 滑轨
            if (config.HasDrawer && config.DrawerCount > 0)
            {
                hardwareList.Add(new HardwareInfo
                {
                    Name = "三节滑轨",
                    Type = HardwareType.SlideRail,
                    Model = "DTC 三节钢珠滑轨",
                    Brand = "DTC",
                    Quantity = config.DrawerCount * 2,
                    Unit = "付",
                    UnitPrice = 18.0,
                    InstallPosition = InstallPosition.Interior,
                    OrderId = config.OrderId,
                    CabinetId = config.CabinetId,
                    RoomId = config.RoomId
                });
            }

            // 层板托
            int shelfPinCount = panels.Count(p => p.NeedsShelfPinHoles) * 4;
            if (shelfPinCount > 0)
            {
                hardwareList.Add(new HardwareInfo
                {
                    Name = "层板托",
                    Type = HardwareType.ShelfSupport,
                    Model = "钢制层板托",
                    Quantity = shelfPinCount,
                    Unit = "个",
                    UnitPrice = 0.5,
                    InstallPosition = InstallPosition.Shelf,
                    OrderId = config.OrderId,
                    CabinetId = config.CabinetId,
                    RoomId = config.RoomId
                });
            }

            // 拉手
            if (config.HasDoor && config.DoorCount > 0)
            {
                hardwareList.Add(new HardwareInfo
                {
                    Name = "铝合金拉手",
                    Type = HardwareType.Handle,
                    Model = "128mm孔距",
                    Material = "铝合金",
                    Quantity = config.DoorCount + (config.HasDrawer ? config.DrawerCount : 0),
                    Unit = "个",
                    UnitPrice = 6.0,
                    InstallPosition = InstallPosition.DoorPanel,
                    OrderId = config.OrderId,
                    CabinetId = config.CabinetId,
                    RoomId = config.RoomId
                });
            }

            // 衣通
            if (config.CabinetWidth >= 600 && config.CabinetHeight >= 1500)
            {
                hardwareList.Add(new HardwareInfo
                {
                    Name = "衣通",
                    Type = HardwareType.ClothesRail,
                    Model = "铝合金衣通+支座",
                    Material = "铝合金",
                    Quantity = 1,
                    Unit = "根",
                    UnitPrice = 25.0,
                    InstallPosition = InstallPosition.Interior,
                    OrderId = config.OrderId,
                    CabinetId = config.CabinetId,
                    RoomId = config.RoomId
                });
            }

            return hardwareList;
        }

        #region 辅助方法

        private static DecomposedPanel CreateSidePanel(
            string type, string name, double length, double width, double thickness,
            string material, double posX, double posY, double posZ,
            CabinetDecomposeConfig config, bool isLeft)
        {
            var panel = new DecomposedPanel
            {
                PanelType = type,
                PanelName = name,
                Length = length,
                Width = width,
                Thickness = thickness,
                Quantity = 1,
                Material = material,
                TextureDirection = TextureDirection.AlongLength,
                PositionX = posX,
                PositionY = posY,
                PositionZ = posZ,
                NeedsConnectorHoles = true,
                // 侧板封边：前边封、后边可选、上边可选
                EdgeTop = config.SidePanelEdgeTop ? config.SidePanelEdgeMaterial : "",
                EdgeBottom = config.SidePanelEdgeBottom ? config.SidePanelEdgeMaterial : "",
                EdgeLeft = config.SidePanelEdgeFront ? config.SidePanelEdgeMaterial : "",
                EdgeRight = config.SidePanelEdgeBack ? config.SidePanelEdgeMaterial : ""
            };
            panel.EdgeInfo = BuildEdgeInfoString(panel);
            return panel;
        }

        private static DecomposedPanel CreateTopBottomPanel(
            string type, string name, double length, double width, double thickness,
            string material, double posX, double posY, double posZ,
            CabinetDecomposeConfig config, bool isTop)
        {
            var panel = new DecomposedPanel
            {
                PanelType = type,
                PanelName = name,
                Length = length,
                Width = width,
                Thickness = thickness,
                Quantity = 1,
                Material = material,
                TextureDirection = TextureDirection.AlongLength,
                PositionX = posX,
                PositionY = posY,
                PositionZ = posZ,
                NeedsConnectorHoles = true,
                // 顶底板封边：前边封、后边可选
                EdgeTop = "",
                EdgeBottom = "",
                EdgeLeft = config.TopBottomEdgeFront ? config.TopBottomEdgeMaterial : "",
                EdgeRight = config.TopBottomEdgeBack ? config.TopBottomEdgeMaterial : ""
            };
            panel.EdgeInfo = BuildEdgeInfoString(panel);
            return panel;
        }

        /// <summary>
        /// 从四边封边字段构建封边信息字符串
        /// </summary>
        private static string BuildEdgeInfoString(DecomposedPanel panel)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(panel.EdgeTop)) parts.Add($"上-{panel.EdgeTop}");
            if (!string.IsNullOrEmpty(panel.EdgeBottom)) parts.Add($"下-{panel.EdgeBottom}");
            if (!string.IsNullOrEmpty(panel.EdgeLeft)) parts.Add($"左-{panel.EdgeLeft}");
            if (!string.IsNullOrEmpty(panel.EdgeRight)) parts.Add($"右-{panel.EdgeRight}");
            return parts.Count > 0 ? string.Join(",", parts) : "无封边";
        }

        #endregion
    }
}
