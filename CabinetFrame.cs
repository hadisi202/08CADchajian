using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    /// <summary>
    /// 背板安装方式
    /// </summary>
    public enum BackPanelType
    {
        /// <summary>内嵌背板：背板安装在柜体内部后方</summary>
        Insert,
        /// <summary>外盖背板：背板覆盖在柜体后面外部</summary>
        Cover
    }

    /// <summary>
    /// 板件朝向 — 决定板件在3D空间中的摆放方向
    /// 对于一块板件，开料尺寸为 长×宽×厚，
    /// 朝向决定了长/宽/厚分别对应3D空间中的哪个轴
    /// </summary>
    public enum PanelOrient
    {
        /// <summary>水平板（层板/顶底板）：长沿X, 宽沿Y, 厚沿Z</summary>
        Horizontal,
        /// <summary>左侧板：厚沿X, 宽沿Y, 长沿Z</summary>
        LeftSide,
        /// <summary>右侧板：厚沿X, 宽沿Y, 长沿Z</summary>
        RightSide,
        /// <summary>背板：长沿X, 厚沿Y, 宽沿Z</summary>
        Back,
        /// <summary>门板/抽屉面板：长沿X, 厚沿Y, 宽沿Z</summary>
        Front,
        /// <summary>立板（竖向分隔）：厚沿X, 宽沿Y, 长沿Z</summary>
        VerticalDivider
    }

    /// <summary>
    /// 板件挂接类型
    /// </summary>
    public enum PanelPlacementKind
    {
        InternalSpace,
        AttachmentZone
    }

    /// <summary>
    /// 柜体外挂接区类型
    /// </summary>
    public enum AttachmentZoneType
    {
        None,
        BaseFront,
        LeftOuter,
        RightOuter,
        TopOuter,
        FrontOuter,
        BackOuter
    }

    /// <summary>
    /// 柜体外框 — 搭积木的"底盘"
    /// 定义柜体的外部边界，所有板件都在框内添加
    /// </summary>
    public class CabinetFrame
    {
        public string FrameId { get; set; } = Guid.NewGuid().ToString("N");
        public string OrderId { get; set; } = "";
        public string CabinetId { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string DefaultMaterial { get; set; } = "18mm多层实木板";
        public double DefaultThickness { get; set; } = 18;

        /// <summary>背板安装方式（外盖/内嵌）</summary>
        public BackPanelType BackPanelStyle { get; set; } = BackPanelType.Insert;

        /// <summary>背板厚度（固定18mm）</summary>
        public double BackPanelThickness { get; set; } = 18;

        /// <summary>封边材料名称</summary>
        public string EdgeBandMaterial { get; set; } = "0.5mm同色封边";

        /// <summary>封边厚度(mm)，用于裁切尺寸计算</summary>
        public double EdgeBandThickness { get; set; } = 0.5;

        /// <summary>外框宽度(X方向)</summary>
        public double Width { get; set; } = 800;
        /// <summary>外框深度(Y方向)</summary>
        public double Depth { get; set; } = 600;
        /// <summary>外框高度(Z方向)</summary>
        public double Height { get; set; } = 2400;

        /// <summary>踢脚板默认高度</summary>
        public double KickPanelHeight { get; set; } = 80;

        /// <summary>外框原点（左下后角）</summary>
        public Point3d Origin { get; set; } = Point3d.Origin;

        /// <summary>外框锚点实体Handle（用于检测外框是否仍存在）</summary>
        public string FrameAnchorHandle { get; set; } = "";

        /// <summary>框内所有板件槽位</summary>
        public List<PanelSlot> Panels { get; set; } = new();

        /// <summary>空间分割树的根节点</summary>
        public SpaceNode RootSpace { get; set; }

        /// <summary>关联的其他外框（用于拼接）</summary>
        public List<string> LinkedFrameIds { get; set; } = new();

        /// <summary>是否共享左侧板（与左邻框共享）</summary>
        public bool ShareLeftPanel { get; set; }
        /// <summary>是否共享右侧板（与右邻框共享）</summary>
        public bool ShareRightPanel { get; set; }

        public CabinetFrame()
        {
            // 初始化根空间 = 外框内部
            RootSpace = new SpaceNode
            {
                Id = "root",
                MinX = 0,
                MaxX = Width,
                MinY = 0,
                MaxY = Depth,
                MinZ = 0,
                MaxZ = Height
            };
        }

        /// <summary>
        /// 初始化空间树（设置尺寸后调用）
        /// </summary>
        public void InitSpaceTree()
        {
            RootSpace = new SpaceNode
            {
                Id = "root",
                MinX = 0,
                MaxX = Width,
                MinY = 0,
                MaxY = Depth,
                MinZ = 0,
                MaxZ = Height
            };
        }

        /// <summary>
        /// 查找包含指定3D点的叶子空间节点
        /// </summary>
        public SpaceNode FindSpace(Point3d point)
        {
            double rx = point.X - Origin.X;
            double ry = point.Y - Origin.Y;
            double rz = point.Z - Origin.Z;
            return FindBestLeafSpace(rx, ry, rz);
        }

        /// <summary>
        /// 根据点击位置判断要添加的板件类型
        /// </summary>
        public PanelAddHint GetPanelAddHint(Point3d clickPoint)
        {
            double rx = clickPoint.X - Origin.X;
            double ry = clickPoint.Y - Origin.Y;
            double rz = clickPoint.Z - Origin.Z;

            var space = FindSpace(clickPoint);
            if (space == null) return new PanelAddHint { PanelType = "无" };

            double spaceW = space.MaxX - space.MinX;
            double spaceH = space.MaxZ - space.MinZ;
            double spaceD = space.MaxY - space.MinY;

            if (spaceW <= 0 || spaceH <= 0 || spaceD <= 0)
                return new PanelAddHint { PanelType = "无" };

            var hint = new PanelAddHint { Space = space };

            double edgeThresholdX = GetEdgeThreshold(spaceW);
            double edgeThresholdY = GetEdgeThreshold(spaceD);
            double edgeThresholdZ = GetEdgeThreshold(spaceH);

            double distLeft = Math.Abs(rx - space.MinX);
            double distRight = Math.Abs(space.MaxX - rx);
            double distBack = Math.Abs(ry - space.MinY);
            double distFront = Math.Abs(space.MaxY - ry);
            double distBottom = Math.Abs(rz - space.MinZ);
            double distTop = Math.Abs(space.MaxZ - rz);

            bool hasAnyInternalPanels = Panels.Any(p => p.PlacementKind == PanelPlacementKind.InternalSpace);
            int GetPriority(string panelType)
            {
                if (hasAnyInternalPanels)
                    return 0;

                if (panelType == "左侧板" || panelType == "右侧板")
                    return 0;
                if (panelType == "顶板" || panelType == "底板")
                    return 1;
                return 2;
            }

            var edgeCandidates = new List<(double Score, int Priority, double Distance, string PanelType, PanelOrient Orient, string Description)>();

            if (distLeft <= edgeThresholdX && !Panels.Any(p => p.PanelType == "左侧板" && p.SpaceId == space.Id))
                edgeCandidates.Add((distLeft / Math.Max(edgeThresholdX, 1.0), GetPriority("左侧板"), distLeft, "左侧板", PanelOrient.LeftSide, "在空间左侧添加侧板"));

            if (distRight <= edgeThresholdX && !Panels.Any(p => p.PanelType == "右侧板" && p.SpaceId == space.Id))
                edgeCandidates.Add((distRight / Math.Max(edgeThresholdX, 1.0), GetPriority("右侧板"), distRight, "右侧板", PanelOrient.RightSide, "在空间右侧添加侧板"));

            if (distTop <= edgeThresholdZ && !Panels.Any(p => p.PanelType == "顶板" && p.SpaceId == space.Id))
                edgeCandidates.Add((distTop / Math.Max(edgeThresholdZ, 1.0), GetPriority("顶板"), distTop, "顶板", PanelOrient.Horizontal, "在空间顶部添加顶板"));

            if (distBottom <= edgeThresholdZ && !Panels.Any(p => p.PanelType == "底板" && p.SpaceId == space.Id))
                edgeCandidates.Add((distBottom / Math.Max(edgeThresholdZ, 1.0), GetPriority("底板"), distBottom, "底板", PanelOrient.Horizontal, "在空间底部添加底板"));

            if (distBack <= edgeThresholdY && !Panels.Any(p => p.PanelType == "背板" && p.SpaceId == space.Id))
                edgeCandidates.Add((distBack / Math.Max(edgeThresholdY, 1.0), GetPriority("背板"), distBack, "背板", PanelOrient.Back, "在空间后部添加背板"));

            if (distFront <= edgeThresholdY && !Panels.Any(p => p.PanelType == "门板" && p.SpaceId == space.Id))
                edgeCandidates.Add((distFront / Math.Max(edgeThresholdY, 1.0), GetPriority("门板"), distFront, "门板", PanelOrient.Front, "在空间前部添加门板"));

            if (edgeCandidates.Count > 0)
            {
                var bestEdge = edgeCandidates
                    .OrderBy(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Priority)
                    .ThenBy(candidate => candidate.Distance)
                    .First();

                hint.PanelType = bestEdge.PanelType;
                hint.Orient = bestEdge.Orient;
                hint.Description = bestEdge.Description;
                return hint;
            }

            // 内部区域：判断是加层板还是立板
            if (spaceW >= spaceH)
            {
                hint.PanelType = "层板";
                hint.Orient = PanelOrient.Horizontal;
                hint.Description = $"添加水平层板 (空间{spaceW:F0}×{spaceD:F0}×{spaceH:F0})";
            }
            else
            {
                hint.PanelType = "立板";
                hint.Orient = PanelOrient.VerticalDivider;
                hint.Description = $"添加竖向立板 (空间{spaceW:F0}×{spaceD:F0}×{spaceH:F0})";
            }

            return hint;
        }

        /// <summary>
        /// 根据柜体外部点击位置识别附属板件挂接区
        /// </summary>
        public AttachmentAddHint GetAttachmentAddHint(Point3d clickPoint)
        {
            double rx = clickPoint.X - Origin.X;
            double ry = clickPoint.Y - Origin.Y;
            double rz = clickPoint.Z - Origin.Z;

            var zones = BuildAttachmentZones();
            if (zones.Count == 0)
                return null;

            var best = zones
                .Select(zone => new
                {
                    Zone = zone,
                    IsInside = zone.Contains(rx, ry, rz),
                    Distance = zone.DistanceTo(rx, ry, rz)
                })
                .OrderBy(candidate => candidate.IsInside ? 0 : 1)
                .ThenBy(candidate => candidate.Distance)
                .FirstOrDefault();

            if (best == null)
                return null;

            double snapLimit = Math.Max(DefaultThickness * 6.0, 120.0);
            if (!best.IsInside && best.Distance > snapLimit)
                return null;

            string suggestedType = GetSuggestedAttachmentPanelType(best.Zone.ZoneType);
            GetAttachmentPanelGeometry(suggestedType, best.Zone, out double length, out double width, out double thickness);

            return new AttachmentAddHint
            {
                PanelType = suggestedType,
                Zone = best.Zone,
                Description = $"{best.Zone.DisplayName}，自动补齐 {suggestedType}",
                PreviewLength = length,
                PreviewWidth = width,
                PreviewThickness = thickness
            };
        }

        /// <summary>
        /// 添加附属板件，不参与内空分割
        /// </summary>
        public PanelSlot AddAttachmentPanel(string panelType, AttachmentZone zone, AttachmentPanelOptions options = null)
        {
            var slots = AddAttachmentPanels(panelType, zone, options);
            return slots.FirstOrDefault();
        }

        public List<PanelSlot> AddAttachmentPanels(string panelType, AttachmentZone zone, AttachmentPanelOptions options = null)
        {
            var slots = BuildAttachmentPanelSlots(panelType, zone, options);
            if (slots.Count == 0)
                return slots;

            Panels.AddRange(slots);
            return slots;
        }

        public List<PanelSlot> BuildAttachmentPreviewSlots(string panelType, AttachmentZone zone, AttachmentPanelOptions options = null)
        {
            return BuildAttachmentPanelSlots(panelType, zone, options);
        }

        public AttachmentZone FindAttachmentZone(string zoneId)
        {
            if (string.IsNullOrWhiteSpace(zoneId))
                return null;

            return BuildAttachmentZones()
                .FirstOrDefault(zone => string.Equals(zone.Id, zoneId, StringComparison.OrdinalIgnoreCase));
        }

        public List<PanelSlot> ReconfigureAttachmentGroup(PanelSlot anchorSlot)
        {
            if (anchorSlot == null || anchorSlot.PlacementKind != PanelPlacementKind.AttachmentZone)
                return new List<PanelSlot>();

            var zone = FindAttachmentZone(anchorSlot.HostZoneId);
            if (zone == null)
                return new List<PanelSlot>();

            string groupId = string.IsNullOrWhiteSpace(anchorSlot.AttachmentGroupId)
                ? anchorSlot.PanelId
                : anchorSlot.AttachmentGroupId;

            var groupSlots = Panels
                .Where(p => p.PlacementKind == PanelPlacementKind.AttachmentZone &&
                            string.Equals(
                                string.IsNullOrWhiteSpace(p.AttachmentGroupId) ? p.PanelId : p.AttachmentGroupId,
                                groupId,
                                StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.AttachmentRole == "Main" ? 0 : 1)
                .ToList();

            if (groupSlots.Count == 0)
                return new List<PanelSlot>();

            var mainSlot = groupSlots.FirstOrDefault(p => p.AttachmentRole == "Main") ?? groupSlots[0];
            foreach (var slot in groupSlots)
            {
                slot.ApplyAttachmentOptions(mainSlot.ToAttachmentOptions());
                ConfigureAttachmentSlot(slot, zone);
                SetDefaultEdgeBanding(slot);
            }

            return groupSlots;
        }

        public List<PanelSlot> RemoveAttachmentPanelsByZone(string zoneId)
        {
            if (string.IsNullOrWhiteSpace(zoneId))
                return new List<PanelSlot>();

            var removed = Panels
                .Where(p => p.PlacementKind == PanelPlacementKind.AttachmentZone &&
                            string.Equals(p.HostZoneId, zoneId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (removed.Count == 0)
                return removed;

            Panels.RemoveAll(p => removed.Any(r => r.PanelId == p.PanelId));
            return removed;
        }

        private List<PanelSlot> BuildAttachmentPanelSlots(string panelType, AttachmentZone zone, AttachmentPanelOptions options)
        {
            if (zone == null)
                return new List<PanelSlot>();

            string resolvedType = ResolveAttachmentPanelType(panelType, zone);
            AttachmentPanelOptions effectiveOptions = NormalizeAttachmentOptions(zone, options);
            string groupId = Guid.NewGuid().ToString("N");

            var slots = new List<PanelSlot>();
            var mainSlot = CreateAttachmentBaseSlot(resolvedType, zone, groupId, "Main");
            mainSlot.ApplyAttachmentOptions(effectiveOptions);
            ConfigureAttachmentSlot(mainSlot, zone);
            SetDefaultEdgeBanding(mainSlot);
            slots.Add(mainSlot);

            if (ShouldUseLShapeAttachment(resolvedType, zone))
            {
                var returnSlot = CreateAttachmentBaseSlot(resolvedType, zone, groupId, "Return");
                returnSlot.ApplyAttachmentOptions(effectiveOptions);
                ConfigureAttachmentSlot(returnSlot, zone);
                SetDefaultEdgeBanding(returnSlot);
                slots.Add(returnSlot);
            }

            return slots;
        }

        private PanelSlot CreateAttachmentBaseSlot(string resolvedType, AttachmentZone zone, string groupId, string role)
        {
            return new PanelSlot
            {
                PanelId = Guid.NewGuid().ToString("N"),
                PanelType = resolvedType,
                Orient = zone.DefaultOrient,
                SpaceId = zone.Id,
                SplitDirection = SplitDirection.None,
                Thickness = DefaultThickness,
                Material = DefaultMaterial,
                OrderId = OrderId,
                CabinetId = CabinetId,
                RoomId = RoomId,
                PlacementKind = PanelPlacementKind.AttachmentZone,
                HostZoneId = zone.Id,
                AttachmentGroupId = groupId,
                AttachmentRole = role
            };
        }

        private static bool ShouldUseLShapeAttachment(string panelType, AttachmentZone zone)
        {
            if (zone == null || string.IsNullOrWhiteSpace(panelType) || !panelType.Contains("收口板"))
                return false;

            return zone.ZoneType == AttachmentZoneType.LeftOuter ||
                   zone.ZoneType == AttachmentZoneType.RightOuter ||
                   zone.ZoneType == AttachmentZoneType.TopOuter ||
                   zone.ZoneType == AttachmentZoneType.BaseFront;
        }

        private List<AttachmentZone> BuildAttachmentZones()
        {
            double t = Math.Max(DefaultThickness, 1.0);
            double backT = Math.Max(BackPanelThickness, t);
            double kickH = Math.Max(KickPanelHeight, t);

            return new List<AttachmentZone>
            {
                new AttachmentZone
                {
                    Id = "attach-base-front",
                    ZoneType = AttachmentZoneType.BaseFront,
                    DisplayName = "前下踢脚区",
                    DefaultOrient = PanelOrient.Front,
                    MinX = 0,
                    MaxX = Width,
                    MinY = -t,
                    MaxY = 0,
                    MinZ = 0,
                    MaxZ = kickH
                },
                new AttachmentZone
                {
                    Id = "attach-left-outer",
                    ZoneType = AttachmentZoneType.LeftOuter,
                    DisplayName = "左外侧挂接区",
                    DefaultOrient = PanelOrient.LeftSide,
                    MinX = -t,
                    MaxX = 0,
                    MinY = 0,
                    MaxY = Depth,
                    MinZ = 0,
                    MaxZ = Height
                },
                new AttachmentZone
                {
                    Id = "attach-right-outer",
                    ZoneType = AttachmentZoneType.RightOuter,
                    DisplayName = "右外侧挂接区",
                    DefaultOrient = PanelOrient.RightSide,
                    MinX = Width,
                    MaxX = Width + t,
                    MinY = 0,
                    MaxY = Depth,
                    MinZ = 0,
                    MaxZ = Height
                },
                new AttachmentZone
                {
                    Id = "attach-top-outer",
                    ZoneType = AttachmentZoneType.TopOuter,
                    DisplayName = "顶部外挂区",
                    DefaultOrient = PanelOrient.Horizontal,
                    MinX = 0,
                    MaxX = Width,
                    MinY = 0,
                    MaxY = Depth,
                    MinZ = Height,
                    MaxZ = Height + t
                },
                new AttachmentZone
                {
                    Id = "attach-front-outer",
                    ZoneType = AttachmentZoneType.FrontOuter,
                    DisplayName = "前外挂区",
                    DefaultOrient = PanelOrient.Front,
                    MinX = 0,
                    MaxX = Width,
                    MinY = -t,
                    MaxY = 0,
                    MinZ = 0,
                    MaxZ = Height
                },
                new AttachmentZone
                {
                    Id = "attach-back-outer",
                    ZoneType = AttachmentZoneType.BackOuter,
                    DisplayName = "后外挂区",
                    DefaultOrient = PanelOrient.Back,
                    MinX = 0,
                    MaxX = Width,
                    MinY = Depth,
                    MaxY = Depth + backT,
                    MinZ = 0,
                    MaxZ = Height
                }
            };
        }

        private string GetSuggestedAttachmentPanelType(AttachmentZoneType zoneType)
        {
            switch (zoneType)
            {
                case AttachmentZoneType.BaseFront:
                    return "踢脚板";
                case AttachmentZoneType.LeftOuter:
                case AttachmentZoneType.RightOuter:
                    return "收口板";
                default:
                    return "辅助板";
            }
        }

        private string ResolveAttachmentPanelType(string panelType, AttachmentZone zone)
        {
            if (string.IsNullOrWhiteSpace(panelType))
                panelType = GetSuggestedAttachmentPanelType(zone.ZoneType);

            if (panelType == "收口板")
            {
                return zone.ZoneType switch
                {
                    AttachmentZoneType.LeftOuter => "左收口板",
                    AttachmentZoneType.RightOuter => "右收口板",
                    AttachmentZoneType.TopOuter => "顶收口板",
                    AttachmentZoneType.FrontOuter => "前收口板",
                    AttachmentZoneType.BackOuter => "后收口板",
                    _ => "收口板"
                };
            }

            if (panelType == "辅助板")
            {
                return zone.ZoneType switch
                {
                    AttachmentZoneType.LeftOuter => "左辅助板",
                    AttachmentZoneType.RightOuter => "右辅助板",
                    AttachmentZoneType.TopOuter => "顶辅助板",
                    AttachmentZoneType.FrontOuter => "前辅助板",
                    AttachmentZoneType.BackOuter => "后辅助板",
                    _ => "辅助板"
                };
            }

            return panelType;
        }

        private AttachmentPanelOptions NormalizeAttachmentOptions(AttachmentZone zone, AttachmentPanelOptions options)
        {
            var normalized = options?.Clone() ?? new AttachmentPanelOptions();
            normalized.PanelHeight = normalized.PanelHeight > 0
                ? normalized.PanelHeight
                : KickPanelHeight;

            normalized.LeftInset = Math.Max(0, normalized.LeftInset);
            normalized.RightInset = Math.Max(0, normalized.RightInset);
            normalized.FrontInset = Math.Max(0, normalized.FrontInset);
            normalized.BackInset = Math.Max(0, normalized.BackInset);
            normalized.TopInset = Math.Max(0, normalized.TopInset);
            normalized.BottomInset = Math.Max(0, normalized.BottomInset);
            normalized.GroundClearance = Math.Max(0, normalized.GroundClearance);
            normalized.ReturnWidth = normalized.ReturnWidth > 0
                ? normalized.ReturnWidth
                : Math.Max(DefaultThickness * 2.0, 60.0);
            return normalized;
        }

        private void ConfigureAttachmentSlot(PanelSlot slot, AttachmentZone zone)
        {
            GetAttachmentPanelGeometry(slot.PanelType, zone, out double length, out double width, out double thickness);
            slot.Thickness = thickness;

            double leftInset = Math.Max(0, slot.LeftInset);
            double rightInset = Math.Max(0, slot.RightInset);
            double frontInset = Math.Max(0, slot.FrontInset);
            double backInset = Math.Max(0, slot.BackInset);
            double topInset = Math.Max(0, slot.TopInset);
            double bottomInset = Math.Max(0, slot.BottomInset);
            double groundClearance = Math.Max(0, slot.GroundClearance);
            double outerOffset = slot.OuterOffset;
            double returnWidth = Math.Max(1.0, slot.ReturnWidth > 0 ? slot.ReturnWidth : Math.Max(DefaultThickness * 2.0, 60.0));

            if (slot.AttachmentRole == "Return")
            {
                switch (zone.ZoneType)
                {
                    case AttachmentZoneType.LeftOuter:
                        slot.Orient = PanelOrient.Front;
                        slot.ComputedLength = Math.Max(Height - topInset - bottomInset, 1.0);
                        slot.ComputedWidth = Math.Max(Math.Min(returnWidth, Width - leftInset - rightInset), 1.0);
                        slot.ComputedThickness = thickness;
                        slot.PosX = leftInset;
                        slot.PosY = -thickness - outerOffset;
                        slot.PosZ = bottomInset;
                        return;

                    case AttachmentZoneType.RightOuter:
                        slot.Orient = PanelOrient.Front;
                        slot.ComputedLength = Math.Max(Height - topInset - bottomInset, 1.0);
                        slot.ComputedWidth = Math.Max(Math.Min(returnWidth, Width - leftInset - rightInset), 1.0);
                        slot.ComputedThickness = thickness;
                        slot.PosX = Math.Max(leftInset, Width - rightInset - slot.ComputedWidth);
                        slot.PosY = -thickness - outerOffset;
                        slot.PosZ = bottomInset;
                        return;

                    case AttachmentZoneType.TopOuter:
                        slot.Orient = PanelOrient.Front;
                        slot.ComputedLength = Math.Max(Math.Min(returnWidth, Height - topInset - bottomInset), 1.0);
                        slot.ComputedWidth = Math.Max(Width - leftInset - rightInset, 1.0);
                        slot.ComputedThickness = thickness;
                        slot.PosX = leftInset;
                        slot.PosY = -thickness - outerOffset;
                        slot.PosZ = Math.Max(bottomInset, Height - topInset - slot.ComputedLength);
                        return;

                    case AttachmentZoneType.BaseFront:
                        slot.Orient = PanelOrient.Horizontal;
                        slot.ComputedLength = Math.Max(Width - leftInset - rightInset, 1.0);
                        slot.ComputedWidth = Math.Max(Math.Min(returnWidth, Depth - frontInset - backInset), 1.0);
                        slot.ComputedThickness = thickness;
                        slot.PosX = leftInset;
                        slot.PosY = -slot.ComputedWidth - outerOffset;
                        slot.PosZ = groundClearance;
                        return;
                }
            }

            switch (zone.ZoneType)
            {
                case AttachmentZoneType.BaseFront:
                    slot.Orient = PanelOrient.Front;
                    slot.ComputedLength = Math.Max(slot.AttachmentHeight > 0 ? slot.AttachmentHeight : length, 1.0);
                    slot.ComputedWidth = Math.Max(Width - leftInset - rightInset, 1.0);
                    slot.ComputedThickness = thickness;
                    slot.PosX = leftInset;
                    slot.PosY = -thickness - outerOffset;
                    slot.PosZ = groundClearance;
                    break;

                case AttachmentZoneType.LeftOuter:
                    slot.Orient = PanelOrient.LeftSide;
                    slot.ComputedLength = Math.Max(Height - topInset - bottomInset, 1.0);
                    slot.ComputedWidth = Math.Max(Depth - frontInset - backInset, 1.0);
                    slot.ComputedThickness = thickness;
                    slot.PosX = -thickness - outerOffset;
                    slot.PosY = backInset;
                    slot.PosZ = bottomInset;
                    break;

                case AttachmentZoneType.RightOuter:
                    slot.Orient = PanelOrient.RightSide;
                    slot.ComputedLength = Math.Max(Height - topInset - bottomInset, 1.0);
                    slot.ComputedWidth = Math.Max(Depth - frontInset - backInset, 1.0);
                    slot.ComputedThickness = thickness;
                    slot.PosX = Width + outerOffset;
                    slot.PosY = backInset;
                    slot.PosZ = bottomInset;
                    break;

                case AttachmentZoneType.TopOuter:
                    slot.Orient = PanelOrient.Horizontal;
                    slot.ComputedLength = Math.Max(Width - leftInset - rightInset, 1.0);
                    slot.ComputedWidth = Math.Max(Depth - frontInset - backInset, 1.0);
                    slot.ComputedThickness = thickness;
                    slot.PosX = leftInset;
                    slot.PosY = backInset;
                    slot.PosZ = Height + outerOffset;
                    break;

                case AttachmentZoneType.BackOuter:
                    slot.Orient = PanelOrient.Back;
                    slot.ComputedLength = Math.Max(Width - leftInset - rightInset, 1.0);
                    slot.ComputedWidth = Math.Max(Height - topInset - bottomInset, 1.0);
                    slot.ComputedThickness = thickness;
                    slot.PosX = leftInset;
                    slot.PosY = Depth + outerOffset;
                    slot.PosZ = bottomInset;
                    break;

                default:
                    slot.Orient = PanelOrient.Front;
                    slot.ComputedLength = Math.Max(Height - topInset - bottomInset, 1.0);
                    slot.ComputedWidth = Math.Max(Width - leftInset - rightInset, 1.0);
                    slot.ComputedThickness = thickness;
                    slot.PosX = leftInset;
                    slot.PosY = -thickness - outerOffset;
                    slot.PosZ = bottomInset;
                    break;
            }
        }

        private void GetAttachmentPanelGeometry(string panelType, AttachmentZone zone, out double length, out double width, out double thickness)
        {
            thickness = DefaultThickness;
            length = 0;
            width = 0;

            if (panelType.Contains("踢脚板"))
            {
                length = Math.Max(KickPanelHeight, thickness);
                width = Width;
                thickness = DefaultThickness;
                return;
            }

            switch (zone.ZoneType)
            {
                case AttachmentZoneType.LeftOuter:
                case AttachmentZoneType.RightOuter:
                    length = Height;
                    width = Depth;
                    break;

                case AttachmentZoneType.TopOuter:
                    length = Width;
                    width = Depth;
                    break;

                case AttachmentZoneType.BackOuter:
                    length = Width;
                    width = Height;
                    thickness = BackPanelThickness;
                    break;

                case AttachmentZoneType.FrontOuter:
                case AttachmentZoneType.BaseFront:
                default:
                    length = Height;
                    width = Width;
                    break;
            }
        }

        private SpaceNode FindBestLeafSpace(double x, double y, double z)
        {
            if (RootSpace == null)
                return null;

            var probePoints = BuildProbePoints(x, y, z);
            var hitCounts = new Dictionary<string, int>();
            var candidateMap = new Dictionary<string, SpaceNode>();

            foreach (var probe in probePoints)
            {
                foreach (var candidate in RootSpace.FindLeafCandidates(probe.X, probe.Y, probe.Z))
                {
                    if (candidate == null || candidate.Width <= 0 || candidate.Depth <= 0 || candidate.Height <= 0)
                        continue;

                    if (!candidateMap.ContainsKey(candidate.Id))
                        candidateMap[candidate.Id] = candidate;

                    hitCounts[candidate.Id] = hitCounts.TryGetValue(candidate.Id, out int count) ? count + 1 : 1;
                }
            }

            if (candidateMap.Count == 0)
                return null;

            return candidateMap.Values
                .OrderByDescending(space => space.ContainsStrict(x, y, z) ? 1 : 0)
                .ThenByDescending(space => hitCounts.GetValueOrDefault(space.Id))
                .ThenByDescending(space => space.GetBoundaryClearance(x, y, z))
                .ThenBy(space => space.Volume)
                .FirstOrDefault();
        }

        private List<Point3d> BuildProbePoints(double x, double y, double z)
        {
            double probe = Math.Max(DefaultThickness * 0.35, 2.0);
            return new List<Point3d>
            {
                new Point3d(x, y, z),
                new Point3d(x - probe, y, z),
                new Point3d(x + probe, y, z),
                new Point3d(x, y - probe, z),
                new Point3d(x, y + probe, z),
                new Point3d(x, y, z - probe),
                new Point3d(x, y, z + probe)
            };
        }

        private static double GetEdgeThreshold(double size)
        {
            return Math.Max(18.0, Math.Min(size * 0.18, 120.0));
        }

        /// <summary>
        /// 添加板件到框内，自动计算尺寸和位置，分割空间
        /// </summary>
        public PanelSlot AddPanel(string panelType, PanelOrient orient, SpaceNode targetSpace, double? position = null)
        {
            double T = DefaultThickness;
            var slot = new PanelSlot
            {
                PanelId = Guid.NewGuid().ToString("N"),
                PanelType = panelType,
                Orient = orient,
                SpaceId = targetSpace.Id,
                Thickness = T,
                Material = DefaultMaterial,
                OrderId = OrderId,
                CabinetId = CabinetId,
                RoomId = RoomId
            };

            double spaceW = targetSpace.MaxX - targetSpace.MinX;
            double spaceD = targetSpace.MaxY - targetSpace.MinY;
            double spaceH = targetSpace.MaxZ - targetSpace.MinZ;

            switch (orient)
            {
                case PanelOrient.Horizontal: // 层板/顶板/底板
                    slot.ComputedLength = spaceW;
                    slot.ComputedWidth = spaceD;
                    slot.ComputedThickness = T;
                    slot.PosX = targetSpace.MinX;
                    slot.PosY = targetSpace.MinY;
                    slot.PosZ = position ?? (targetSpace.MinZ + spaceH / 2);
                    // 分割空间：上下
                    slot.SplitDirection = SplitDirection.Horizontal;
                    break;

                case PanelOrient.LeftSide: // 左侧板
                    slot.ComputedLength = spaceH;
                    slot.ComputedWidth = spaceD;
                    slot.ComputedThickness = T;
                    slot.PosX = targetSpace.MinX;
                    slot.PosY = targetSpace.MinY;
                    slot.PosZ = targetSpace.MinZ;
                    slot.SplitDirection = SplitDirection.VerticalX;
                    break;

                case PanelOrient.RightSide: // 右侧板
                    slot.ComputedLength = spaceH;
                    slot.ComputedWidth = spaceD;
                    slot.ComputedThickness = T;
                    slot.PosX = targetSpace.MaxX - T;
                    slot.PosY = targetSpace.MinY;
                    slot.PosZ = targetSpace.MinZ;
                    slot.SplitDirection = SplitDirection.VerticalX;
                    break;

                case PanelOrient.Back: // 背板
                    double backT = BackPanelThickness; // 固定18mm
                    slot.ComputedThickness = backT;
                    slot.Thickness = backT;
                    slot.Material = DefaultMaterial; // 背板与柜体同材质

                    if (BackPanelStyle == BackPanelType.Insert)
                    {
                        slot.SplitDirection = SplitDirection.VerticalY;
                        // 内嵌：背板在柜体后方内部，尺寸=内空尺寸
                        slot.ComputedLength = spaceW;
                        slot.ComputedWidth = spaceH;
                        slot.PosX = targetSpace.MinX;
                        slot.PosY = targetSpace.MaxY - backT;
                        slot.PosZ = targetSpace.MinZ;
                    }
                    else
                    {
                        // 外盖：始终覆盖整柜后口，不受当前内空分割影响。
                        slot.SplitDirection = SplitDirection.None;
                        slot.ComputedLength = Width;
                        slot.ComputedWidth = Height;
                        slot.PosX = 0;
                        slot.PosY = Depth;
                        slot.PosZ = 0;
                    }
                    break;

                case PanelOrient.Front: // 门板
                    double doorT = 18;
                    slot.ComputedLength = spaceH;
                    slot.ComputedWidth = spaceW;
                    slot.ComputedThickness = doorT;
                    slot.PosX = targetSpace.MinX;
                    slot.PosY = targetSpace.MinY - doorT; // 门板在前面
                    slot.PosZ = targetSpace.MinZ;
                    slot.SplitDirection = SplitDirection.None;
                    break;

                case PanelOrient.VerticalDivider: // 立板
                    slot.ComputedLength = spaceH;
                    slot.ComputedWidth = spaceD;
                    slot.ComputedThickness = T;
                    slot.PosX = position ?? (targetSpace.MinX + spaceW / 2 - T / 2);
                    slot.PosY = targetSpace.MinY;
                    slot.PosZ = targetSpace.MinZ;
                    slot.SplitDirection = SplitDirection.VerticalX;
                    break;
            }

            // 自动设置封边
            SetDefaultEdgeBanding(slot);

            // 分割空间
            if (slot.SplitDirection != SplitDirection.None)
            {
                SplitSpace(targetSpace, slot);
            }

            Panels.Add(slot);
            return slot;
        }

        /// <summary>
        /// 根据板件类型自动设置四边封边
        /// WKK 搭建板件默认四边封边 0.5mm 同色封边
        /// </summary>
        public void SetDefaultEdgeBanding(PanelSlot slot)
        {
            string edgeMat = EdgeBandMaterial;
            slot.EdgeTop = edgeMat;
            slot.EdgeBottom = edgeMat;
            slot.EdgeLeft = edgeMat;
            slot.EdgeRight = edgeMat;
        }

        /// <summary>
        /// 分割空间节点
        /// </summary>
        private void SplitSpace(SpaceNode parent, PanelSlot panel)
        {
            switch (panel.SplitDirection)
            {
                case SplitDirection.Horizontal: // 水平分割（层板）
                    {
                        double splitZ = panel.PosZ;
                        parent.Left = new SpaceNode
                        {
                            Id = $"{parent.Id}_L",
                            MinX = parent.MinX, MaxX = parent.MaxX,
                            MinY = parent.MinY, MaxY = parent.MaxY,
                            MinZ = parent.MinZ, MaxZ = splitZ
                        };
                        parent.Right = new SpaceNode
                        {
                            Id = $"{parent.Id}_R",
                            MinX = parent.MinX, MaxX = parent.MaxX,
                            MinY = parent.MinY, MaxY = parent.MaxY,
                            MinZ = splitZ + panel.Thickness, MaxZ = parent.MaxZ
                        };
                    }
                    break;
                case SplitDirection.VerticalX: // X方向竖直分割（侧板/立板）
                    {
                        double splitX = panel.PosX;
                        // 如果是右侧板，分割点在 MaxX-Thickness
                        if (panel.PanelType == "右侧板") splitX = parent.MaxX - panel.Thickness;

                        parent.Left = new SpaceNode
                        {
                            Id = $"{parent.Id}_L",
                            MinX = parent.MinX, MaxX = splitX,
                            MinY = parent.MinY, MaxY = parent.MaxY,
                            MinZ = parent.MinZ, MaxZ = parent.MaxZ
                        };
                        parent.Right = new SpaceNode
                        {
                            Id = $"{parent.Id}_R",
                            MinX = splitX + panel.Thickness, MaxX = parent.MaxX,
                            MinY = parent.MinY, MaxY = parent.MaxY,
                            MinZ = parent.MinZ, MaxZ = parent.MaxZ
                        };
                    }
                    break;
                case SplitDirection.VerticalY: // Y方向分割（背板）
                    {
                        double splitY = panel.PosY;
                        parent.Left = new SpaceNode
                        {
                            Id = $"{parent.Id}_L",
                            MinX = parent.MinX, MaxX = parent.MaxX,
                            MinY = parent.MinY, MaxY = splitY,
                            MinZ = parent.MinZ, MaxZ = parent.MaxZ
                        };
                        parent.Right = new SpaceNode
                        {
                            Id = $"{parent.Id}_R",
                            MinX = parent.MinX, MaxX = parent.MaxX,
                            MinY = splitY + panel.Thickness, MaxY = parent.MaxY,
                            MinZ = parent.MinZ, MaxZ = parent.MaxZ
                        };
                    }
                    break;
            }
        }

        /// <summary>
        /// 移除板件并合并空间
        /// </summary>
        public bool RemovePanel(string panelId)
        {
            var panel = Panels.FirstOrDefault(p => p.PanelId == panelId);
            if (panel == null) return false;

            // 找到该板件分割产生的空间节点，合并回去
            MergeSpaceForPanel(panel);
            Panels.Remove(panel);
            return true;
        }

        public void ResizeFrame(double width, double depth, double height)
        {
            var layoutSnapshots = CaptureInternalPanelLayoutSnapshots();

            Width = Math.Max(width, 1.0);
            Depth = Math.Max(depth, 1.0);
            Height = Math.Max(height, 1.0);

            InitSpaceTree();

            foreach (var panel in Panels
                .Where(p => p.PlacementKind != PanelPlacementKind.AttachmentZone)
                .OrderBy(p => p.PanelId))
            {
                var targetSpace = FindSpaceNodeById(RootSpace, panel.SpaceId) ?? RootSpace;
                layoutSnapshots.TryGetValue(panel.PanelId, out var snapshot);
                RecomputeInternalPanelSlot(panel, targetSpace, snapshot);

                if (panel.SplitDirection != SplitDirection.None)
                {
                    SplitSpace(targetSpace, panel);
                }
            }

            foreach (var slot in Panels.Where(p => p.PlacementKind == PanelPlacementKind.AttachmentZone))
            {
                var zone = FindAttachmentZone(slot.HostZoneId);
                if (zone == null)
                    continue;

                ConfigureAttachmentSlot(slot, zone);
                SetDefaultEdgeBanding(slot);
            }
        }

        private void MergeSpaceForPanel(PanelSlot panel)
        {
            // 找到以该板件space为父节点的合并操作
            // 简化实现：重置空间树后重新从所有剩余板件构建
            RebuildSpaceTree();
        }

        /// <summary>
        /// 重建空间分割树（用于删除板件后）
        /// </summary>
        public void RebuildSpaceTree()
        {
            InitSpaceTree();
            foreach (var panel in Panels.OrderBy(p => p.PanelId))
            {
                var center = GetPanelCenterPoint(panel);
                var space = RootSpace.FindLeaf(center.X, center.Y, center.Z);
                if (space != null)
                {
                    SplitSpace(space, panel);
                }
            }
        }

        private Dictionary<string, PanelLayoutSnapshot> CaptureInternalPanelLayoutSnapshots()
        {
            var snapshots = new Dictionary<string, PanelLayoutSnapshot>(StringComparer.OrdinalIgnoreCase);

            foreach (var panel in Panels.Where(p => p.PlacementKind != PanelPlacementKind.AttachmentZone))
            {
                var targetSpace = FindSpaceNodeById(RootSpace, panel.SpaceId);
                if (targetSpace == null)
                    continue;

                snapshots[panel.PanelId] = new PanelLayoutSnapshot
                {
                    SpaceId = panel.SpaceId,
                    RelativeRatio = GetRelativeInsertRatio(panel, targetSpace)
                };
            }

            return snapshots;
        }

        private static double GetRelativeInsertRatio(PanelSlot panel, SpaceNode targetSpace)
        {
            if (panel == null || targetSpace == null)
                return 0.5;

            switch (panel.Orient)
            {
                case PanelOrient.Horizontal:
                    {
                        double availableHeight = Math.Max(targetSpace.Height - panel.Thickness, 0.0);
                        if (availableHeight <= 1e-9)
                            return 0.5;

                        return Clamp01((panel.PosZ - targetSpace.MinZ) / availableHeight);
                    }

                case PanelOrient.VerticalDivider:
                    {
                        double availableWidth = Math.Max(targetSpace.Width - panel.Thickness, 0.0);
                        if (availableWidth <= 1e-9)
                            return 0.5;

                        return Clamp01((panel.PosX - targetSpace.MinX) / availableWidth);
                    }

                default:
                    return 0.5;
            }
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static SpaceNode FindSpaceNodeById(SpaceNode node, string spaceId)
        {
            if (node == null || string.IsNullOrWhiteSpace(spaceId))
                return null;

            if (string.Equals(node.Id, spaceId, StringComparison.OrdinalIgnoreCase))
                return node;

            return FindSpaceNodeById(node.Left, spaceId) ?? FindSpaceNodeById(node.Right, spaceId);
        }

        private void RecomputeInternalPanelSlot(PanelSlot panel, SpaceNode targetSpace, PanelLayoutSnapshot snapshot)
        {
            if (panel == null || targetSpace == null)
                return;

            double thickness = panel.Thickness > 0 ? panel.Thickness : DefaultThickness;
            double ratio = snapshot?.RelativeRatio ?? 0.5;

            switch (panel.Orient)
            {
                case PanelOrient.Horizontal:
                    panel.ComputedLength = targetSpace.Width;
                    panel.ComputedWidth = targetSpace.Depth;
                    panel.ComputedThickness = thickness;
                    panel.PosX = targetSpace.MinX;
                    panel.PosY = targetSpace.MinY;
                    panel.SplitDirection = SplitDirection.Horizontal;

                    if (panel.PanelType == "顶板")
                    {
                        panel.PosZ = targetSpace.MaxZ - thickness;
                    }
                    else if (panel.PanelType == "底板")
                    {
                        panel.PosZ = targetSpace.MinZ;
                    }
                    else
                    {
                        double availableHeight = Math.Max(targetSpace.Height - thickness, 0.0);
                        panel.PosZ = targetSpace.MinZ + availableHeight * ratio;
                    }
                    break;

                case PanelOrient.LeftSide:
                    panel.ComputedLength = targetSpace.Height;
                    panel.ComputedWidth = targetSpace.Depth;
                    panel.ComputedThickness = thickness;
                    panel.PosX = targetSpace.MinX;
                    panel.PosY = targetSpace.MinY;
                    panel.PosZ = targetSpace.MinZ;
                    panel.SplitDirection = SplitDirection.VerticalX;
                    break;

                case PanelOrient.RightSide:
                    panel.ComputedLength = targetSpace.Height;
                    panel.ComputedWidth = targetSpace.Depth;
                    panel.ComputedThickness = thickness;
                    panel.PosX = targetSpace.MaxX - thickness;
                    panel.PosY = targetSpace.MinY;
                    panel.PosZ = targetSpace.MinZ;
                    panel.SplitDirection = SplitDirection.VerticalX;
                    break;

                case PanelOrient.Back:
                    panel.Thickness = Math.Max(BackPanelThickness, 1.0);
                    panel.ComputedThickness = panel.Thickness;
                    if (BackPanelStyle == BackPanelType.Insert)
                    {
                        panel.SplitDirection = SplitDirection.VerticalY;
                        panel.ComputedLength = targetSpace.Width;
                        panel.ComputedWidth = targetSpace.Height;
                        panel.PosX = targetSpace.MinX;
                        panel.PosY = targetSpace.MaxY - panel.Thickness;
                        panel.PosZ = targetSpace.MinZ;
                    }
                    else
                    {
                        panel.SplitDirection = SplitDirection.None;
                        panel.ComputedLength = Width;
                        panel.ComputedWidth = Height;
                        panel.PosX = 0;
                        panel.PosY = Depth;
                        panel.PosZ = 0;
                    }
                    break;

                case PanelOrient.Front:
                    panel.ComputedLength = targetSpace.Height;
                    panel.ComputedWidth = targetSpace.Width;
                    panel.ComputedThickness = thickness;
                    panel.PosX = targetSpace.MinX;
                    panel.PosY = targetSpace.MinY - thickness;
                    panel.PosZ = targetSpace.MinZ;
                    panel.SplitDirection = SplitDirection.None;
                    break;

                case PanelOrient.VerticalDivider:
                    panel.ComputedLength = targetSpace.Height;
                    panel.ComputedWidth = targetSpace.Depth;
                    panel.ComputedThickness = thickness;
                    panel.PosY = targetSpace.MinY;
                    panel.PosZ = targetSpace.MinZ;
                    panel.SplitDirection = SplitDirection.VerticalX;
                    panel.PosX = targetSpace.MinX + Math.Max(targetSpace.Width - thickness, 0.0) * ratio;
                    break;
            }
        }

        private sealed class PanelLayoutSnapshot
        {
            public string SpaceId { get; set; } = "";
            public double RelativeRatio { get; set; }
        }

        private Point3d GetPanelCenterPoint(PanelSlot panel)
        {
            if (panel == null)
                return Point3d.Origin;

            double centerX;
            double centerY;
            double centerZ;

            switch (panel.Orient)
            {
                case PanelOrient.Horizontal:
                    centerX = panel.PosX + panel.ComputedLength / 2.0;
                    centerY = panel.PosY + panel.ComputedWidth / 2.0;
                    centerZ = panel.PosZ + panel.ComputedThickness / 2.0;
                    break;

                case PanelOrient.LeftSide:
                case PanelOrient.RightSide:
                case PanelOrient.VerticalDivider:
                    centerX = panel.PosX + panel.ComputedThickness / 2.0;
                    centerY = panel.PosY + panel.ComputedWidth / 2.0;
                    centerZ = panel.PosZ + panel.ComputedLength / 2.0;
                    break;

                case PanelOrient.Back:
                    centerX = panel.PosX + panel.ComputedLength / 2.0;
                    centerY = panel.PosY + panel.ComputedThickness / 2.0;
                    centerZ = panel.PosZ + panel.ComputedWidth / 2.0;
                    break;

                case PanelOrient.Front:
                    centerX = panel.PosX + panel.ComputedWidth / 2.0;
                    centerY = panel.PosY + panel.ComputedThickness / 2.0;
                    centerZ = panel.PosZ + panel.ComputedLength / 2.0;
                    break;

                default:
                    centerX = panel.PosX + panel.ComputedLength / 2.0;
                    centerY = panel.PosY + panel.ComputedWidth / 2.0;
                    centerZ = panel.PosZ + panel.ComputedThickness / 2.0;
                    break;
            }

            return new Point3d(centerX, centerY, centerZ);
        }

        /// <summary>
        /// 获取最近的同柜号板件（用于信息继承）
        /// </summary>
        public PanelSlot FindNearestPanel(string panelType)
        {
            return Panels.FirstOrDefault(p => p.PanelType == panelType);
        }

        /// <summary>
        /// 序列化为JSON（用于持久化）
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        }

        public static CabinetFrame FromJson(string json)
        {
            return JsonSerializer.Deserialize<CabinetFrame>(json);
        }
    }

    /// <summary>
    /// 板件槽位 — 记录板件在框中的位置和约束关系
    /// </summary>
    public class PanelSlot
    {
        public string PanelId { get; set; } = "";
        public string PanelType { get; set; } = "";
        public PanelOrient Orient { get; set; }
        public PanelPlacementKind PlacementKind { get; set; } = PanelPlacementKind.InternalSpace;
        public string SpaceId { get; set; } = "";
        public string HostZoneId { get; set; } = "";
        public string AttachmentGroupId { get; set; } = "";
        public string AttachmentRole { get; set; } = "";
        public SplitDirection SplitDirection { get; set; }
        public double LeftInset { get; set; }
        public double RightInset { get; set; }
        public double FrontInset { get; set; }
        public double BackInset { get; set; }
        public double TopInset { get; set; }
        public double BottomInset { get; set; }
        public double GroundClearance { get; set; }
        public double OuterOffset { get; set; }
        public double AttachmentHeight { get; set; }
        public double ReturnWidth { get; set; }

        // 自动计算的尺寸（由空间约束决定）
        public double ComputedLength { get; set; }
        public double ComputedWidth { get; set; }
        public double ComputedThickness { get; set; }
        public double Thickness { get; set; } = 18;

        // 在框内的位置
        public double PosX { get; set; }
        public double PosY { get; set; }
        public double PosZ { get; set; }

        // 是否可拖动
        public bool IsFixed { get; set; }
        public bool IsDragging { get; set; }

        // 业务信息
        public string OrderId { get; set; } = "";
        public string CabinetId { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string Material { get; set; } = "18mm多层实木板";
        public TextureDirection TextureDirection { get; set; } = TextureDirection.AlongLength;

        // 四边封边
        public string EdgeTop { get; set; } = "";
        public string EdgeBottom { get; set; } = "";
        public string EdgeLeft { get; set; } = "";
        public string EdgeRight { get; set; } = "";

        /// <summary>封边厚度(mm)，默认0.5mm，用于裁切尺寸计算</summary>
        public double EdgeBandThickness { get; set; } = 0.5;

        /// <summary>
        /// 裁切长度 = 开料长度 - 有封边的边之和的封边厚度
        /// 上边(EdgeTop)和下边(EdgeBottom)对应长度方向
        /// </summary>
        public double CuttingLength
        {
            get
            {
                double deduct = 0;
                if (!string.IsNullOrEmpty(EdgeTop)) deduct += EdgeBandThickness;
                if (!string.IsNullOrEmpty(EdgeBottom)) deduct += EdgeBandThickness;
                return ComputedLength - deduct;
            }
        }

        /// <summary>
        /// 裁切宽度 = 开料宽度 - 有封边的边之和的封边厚度
        /// 左边(EdgeLeft)和右边(EdgeRight)对应宽度方向
        /// </summary>
        public double CuttingWidth
        {
            get
            {
                double deduct = 0;
                if (!string.IsNullOrEmpty(EdgeLeft)) deduct += EdgeBandThickness;
                if (!string.IsNullOrEmpty(EdgeRight)) deduct += EdgeBandThickness;
                return ComputedWidth - deduct;
            }
        }

        // CAD实体Handle
        public string EntityHandle { get; set; } = "";

        /// <summary>
        /// 获取板件开料名称
        /// </summary>
        public string GetPanelName(string cabinetPrefix)
        {
            string prefix = string.IsNullOrEmpty(cabinetPrefix) ? "" : $"{cabinetPrefix}-";
            return $"{prefix}{PanelType}";
        }

        /// <summary>
        /// 获取封边信息字符串
        /// </summary>
        public string GetEdgeInfoString()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(EdgeTop)) parts.Add($"上-{EdgeTop}");
            if (!string.IsNullOrEmpty(EdgeBottom)) parts.Add($"下-{EdgeBottom}");
            if (!string.IsNullOrEmpty(EdgeLeft)) parts.Add($"左-{EdgeLeft}");
            if (!string.IsNullOrEmpty(EdgeRight)) parts.Add($"右-{EdgeRight}");
            return parts.Count > 0 ? string.Join(",", parts) : "无封边";
        }

        public void ApplyAttachmentOptions(AttachmentPanelOptions options)
        {
            if (options == null)
                return;

            LeftInset = options.LeftInset;
            RightInset = options.RightInset;
            FrontInset = options.FrontInset;
            BackInset = options.BackInset;
            TopInset = options.TopInset;
            BottomInset = options.BottomInset;
            GroundClearance = options.GroundClearance;
            OuterOffset = options.OuterOffset;
            AttachmentHeight = options.PanelHeight;
            ReturnWidth = options.ReturnWidth;
        }

        public AttachmentPanelOptions ToAttachmentOptions()
        {
            return new AttachmentPanelOptions
            {
                LeftInset = LeftInset,
                RightInset = RightInset,
                FrontInset = FrontInset,
                BackInset = BackInset,
                TopInset = TopInset,
                BottomInset = BottomInset,
                GroundClearance = GroundClearance,
                OuterOffset = OuterOffset,
                PanelHeight = AttachmentHeight,
                ReturnWidth = ReturnWidth
            };
        }

        public string GetAttachmentParameterSummary()
        {
            if (PlacementKind != PanelPlacementKind.AttachmentZone)
                return "";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(HostZoneId))
                parts.Add($"挂接区={HostZoneId}");
            if (AttachmentHeight > 0)
                parts.Add($"高度={AttachmentHeight:F1}");
            if (!string.IsNullOrWhiteSpace(AttachmentRole) && AttachmentRole != "Main")
                parts.Add($"构件={AttachmentRole}");
            if (LeftInset > 0)
                parts.Add($"左缩进={LeftInset:F1}");
            if (RightInset > 0)
                parts.Add($"右缩进={RightInset:F1}");
            if (FrontInset > 0)
                parts.Add($"前缩进={FrontInset:F1}");
            if (BackInset > 0)
                parts.Add($"后缩进={BackInset:F1}");
            if (TopInset > 0)
                parts.Add($"上缩进={TopInset:F1}");
            if (BottomInset > 0)
                parts.Add($"下缩进={BottomInset:F1}");
            if (GroundClearance > 0)
                parts.Add($"离地={GroundClearance:F1}");
            if (Math.Abs(OuterOffset) > 0.001)
                parts.Add($"外偏移={OuterOffset:F1}");
            if (ReturnWidth > 0)
                parts.Add($"回口宽={ReturnWidth:F1}");
            return parts.Count > 0 ? string.Join("，", parts) : "附属板件";
        }
    }

    /// <summary>
    /// 空间分割方向
    /// </summary>
    public enum SplitDirection
    {
        None,
        Horizontal,     // 水平分割（层板 → 上下分割）
        VerticalX,      // X方向竖直分割（侧板/立板 → 左右分割）
        VerticalY       // Y方向分割（背板 → 前后分割）
    }

    /// <summary>
    /// 空间分割树节点
    /// 每加一块板就把一个空间分成两个子空间
    /// </summary>
    public class SpaceNode
    {
        public string Id { get; set; } = "";
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }

        public SpaceNode Left { get; set; }
        public SpaceNode Right { get; set; }

        public bool IsLeaf => Left == null && Right == null;

        public double Width => MaxX - MinX;
        public double Depth => MaxY - MinY;
        public double Height => MaxZ - MinZ;
        public double Volume => Math.Max(0, Width) * Math.Max(0, Depth) * Math.Max(0, Height);

        /// <summary>
        /// 查找包含指定点的叶子节点
        /// </summary>
        public SpaceNode FindLeaf(double x, double y, double z)
        {
            if (!Contains(x, y, z)) return null;
            if (IsLeaf) return this;

            var found = Left?.FindLeaf(x, y, z);
            if (found != null) return found;
            return Right?.FindLeaf(x, y, z);
        }

        public List<SpaceNode> FindLeafCandidates(double x, double y, double z)
        {
            var result = new List<SpaceNode>();
            CollectLeafCandidates(x, y, z, result);
            return result;
        }

        private void CollectLeafCandidates(double x, double y, double z, List<SpaceNode> result)
        {
            if (!Contains(x, y, z))
                return;

            if (IsLeaf)
            {
                result.Add(this);
                return;
            }

            Left?.CollectLeafCandidates(x, y, z, result);
            Right?.CollectLeafCandidates(x, y, z, result);
        }

        public bool Contains(double x, double y, double z)
        {
            return x >= MinX - 1 && x <= MaxX + 1 &&
                   y >= MinY - 1 && y <= MaxY + 1 &&
                   z >= MinZ - 1 && z <= MaxZ + 1;
        }

        public bool ContainsStrict(double x, double y, double z)
        {
            const double epsilon = 0.001;
            return x >= MinX + epsilon && x <= MaxX - epsilon &&
                   y >= MinY + epsilon && y <= MaxY - epsilon &&
                   z >= MinZ + epsilon && z <= MaxZ - epsilon;
        }

        public double GetBoundaryClearance(double x, double y, double z)
        {
            double dx = Math.Min(Math.Abs(x - MinX), Math.Abs(MaxX - x));
            double dy = Math.Min(Math.Abs(y - MinY), Math.Abs(MaxY - y));
            double dz = Math.Min(Math.Abs(z - MinZ), Math.Abs(MaxZ - z));
            return Math.Min(dx, Math.Min(dy, dz));
        }
    }

    /// <summary>
    /// 板件添加提示 — 用户点击位置后返回的判断结果
    /// </summary>
    public class PanelAddHint
    {
        public string PanelType { get; set; } = "无";
        public PanelOrient Orient { get; set; }
        public SpaceNode Space { get; set; }
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// 附属板件挂接区
    /// </summary>
    public class AttachmentZone
    {
        public string Id { get; set; } = "";
        public AttachmentZoneType ZoneType { get; set; }
        public string DisplayName { get; set; } = "";
        public PanelOrient DefaultOrient { get; set; }
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }

        public bool Contains(double x, double y, double z)
        {
            return x >= MinX && x <= MaxX &&
                   y >= MinY && y <= MaxY &&
                   z >= MinZ && z <= MaxZ;
        }

        public double DistanceTo(double x, double y, double z)
        {
            double dx = x < MinX ? MinX - x : (x > MaxX ? x - MaxX : 0);
            double dy = y < MinY ? MinY - y : (y > MaxY ? y - MaxY : 0);
            double dz = z < MinZ ? MinZ - z : (z > MaxZ ? z - MaxZ : 0);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    /// <summary>
    /// 附属板件添加提示
    /// </summary>
    public class AttachmentAddHint
    {
        public string PanelType { get; set; } = "辅助板";
        public AttachmentZone Zone { get; set; }
        public string Description { get; set; } = "";
        public double PreviewLength { get; set; }
        public double PreviewWidth { get; set; }
        public double PreviewThickness { get; set; }
    }

    public class AttachmentPanelOptions
    {
        public double LeftInset { get; set; }
        public double RightInset { get; set; }
        public double FrontInset { get; set; }
        public double BackInset { get; set; }
        public double TopInset { get; set; }
        public double BottomInset { get; set; }
        public double GroundClearance { get; set; }
        public double OuterOffset { get; set; }
        public double PanelHeight { get; set; }
        public double ReturnWidth { get; set; }

        public AttachmentPanelOptions Clone()
        {
            return new AttachmentPanelOptions
            {
                LeftInset = LeftInset,
                RightInset = RightInset,
                FrontInset = FrontInset,
                BackInset = BackInset,
                TopInset = TopInset,
                BottomInset = BottomInset,
                GroundClearance = GroundClearance,
                OuterOffset = OuterOffset,
                PanelHeight = PanelHeight,
                ReturnWidth = ReturnWidth
            };
        }
    }
}
