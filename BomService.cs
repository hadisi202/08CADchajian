using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FurniturePlugin
{
    /// <summary>
    /// BOM（物料清单）自动生成服务
    /// 从CAD图纸中提取所有板件和五金件信息，生成完整的BOM清单
    /// 支持CSV/Excel格式导出
    /// </summary>
    public static class BomService
    {
        /// <summary>
        /// BOM行项
        /// </summary>
        public class BomItem
        {
            public int Seq { get; set; }
            public string OrderId { get; set; } = "";
            public string RoomId { get; set; } = "";
            public string CabinetId { get; set; } = "";
            public string PanelName { get; set; } = "";
            public string PanelType { get; set; } = "";
            public string Material { get; set; } = "";
            public double Length { get; set; }
            public double Width { get; set; }
            public double Thickness { get; set; }
            public int Quantity { get; set; }
            public string EdgeTop { get; set; } = "";
            public string EdgeBottom { get; set; } = "";
            public string EdgeLeft { get; set; } = "";
            public string EdgeRight { get; set; } = "";
            public string EdgeInfo { get; set; } = "";
            public string TextureDirection { get; set; } = "";
            public string Remarks { get; set; } = "";
            public double Area { get; set; }  // m²
            public string Category { get; set; } = "板件"; // 板件/五金件
            public double UnitPrice { get; set; }
            public double TotalPrice { get; set; }
        }

        /// <summary>
        /// BOM导出结果
        /// </summary>
        public class BomResult
        {
            public List<BomItem> Items { get; set; } = new();
            public double TotalPanelArea { get; set; }
            public int TotalPanelCount { get; set; }
            public int TotalHardwareCount { get; set; }
            public double TotalPrice { get; set; }
            public string ExportPath { get; set; } = "";
        }

        /// <summary>
        /// 从PanelInfo列表生成BOM
        /// </summary>
        public static BomResult GenerateBom(List<PanelInfo> panels, string? orderId = null)
        {
            var result = new BomResult();
            int seq = 0;

            // 按订单号-房间-柜号排序
            var sorted = panels.OrderBy(p => p.OrderId).ThenBy(p => p.RoomId).ThenBy(p => p.CabinetId).ToList();

            foreach (var panel in sorted)
            {
                if (!string.IsNullOrEmpty(orderId) && panel.OrderId != orderId) continue;

                seq++;
                var area = (panel.ExtraLength * panel.ExtraWidth) / 1000000.0; // mm² → m²
                var item = new BomItem
                {
                    Seq = seq,
                    OrderId = panel.OrderId ?? "",
                    RoomId = panel.RoomId ?? "",
                    CabinetId = panel.CabinetId ?? "",
                    PanelName = panel.PanelName ?? "",
                    PanelType = PanelNumberingService.GetPanelTypeShortName(panel.PanelName ?? ""),
                    Material = panel.Material ?? "",
                    Length = panel.ExtraLength,
                    Width = panel.ExtraWidth,
                    Thickness = panel.Height,
                    Quantity = 1,
                    EdgeTop = panel.EdgeTop ?? "",
                    EdgeBottom = panel.EdgeBottom ?? "",
                    EdgeLeft = panel.EdgeLeft ?? "",
                    EdgeRight = panel.EdgeRight ?? "",
                    EdgeInfo = panel.EdgeBanding ?? "",
                    TextureDirection = panel.TextureDirection == TextureDirection.AlongLength ? "沿长边" : "沿宽边",
                    Remarks = panel.Remarks ?? "",
                    Area = area,
                    Category = "板件"
                };

                result.Items.Add(item);
                result.TotalPanelArea += area;
                result.TotalPanelCount++;
            }

            // 合并相同规格的板件（同材质同尺寸同封边）
            // 注意：这里不合并，保持明细。如需合并导出，在ExportBom中处理

            return result;
        }

        /// <summary>
        /// 添加五金件到BOM
        /// </summary>
        public static void AddHardwareToBom(BomResult bom, List<HardwareInfo> hardware)
        {
            int seq = bom.Items.Count;
            foreach (var hw in hardware)
            {
                seq++;
                bom.Items.Add(new BomItem
                {
                    Seq = seq,
                    OrderId = hw.OrderId ?? "",
                    RoomId = hw.RoomId ?? "",
                    CabinetId = hw.CabinetId ?? "",
                    PanelName = hw.Name ?? "",
                    PanelType = hw.TypeDisplayName,
                    Material = hw.Material ?? "",
                    Length = 0,
                    Width = 0,
                    Thickness = 0,
                    Quantity = (int)hw.Quantity,
                    Remarks = $"{hw.Brand} {hw.Model}",
                    Category = "五金件",
                    UnitPrice = hw.UnitPrice,
                    TotalPrice = hw.TotalPrice
                });
                bom.TotalHardwareCount += (int)hw.Quantity;
                bom.TotalPrice += hw.TotalPrice;
            }
        }

        /// <summary>
        /// 导出BOM为CSV文件
        /// </summary>
        public static string ExportBomToCsv(BomResult bom, string filePath)
        {
            var sb = new StringBuilder();

            // 表头
            sb.AppendLine("序号,订单号,房间,柜号,名称,类型,材质,长度(mm),宽度(mm),厚度(mm),数量,上封边,下封边,左封边,右封边,封边描述,纹理方向,面积(m²),备注,分类,单价,总价");

            foreach (var item in bom.Items)
            {
                sb.AppendLine($"{item.Seq},\"{item.OrderId}\",\"{item.RoomId}\",\"{item.CabinetId}\"," +
                    $"\"{item.PanelName}\",\"{item.PanelType}\",\"{item.Material}\"," +
                    $"{item.Length:F1},{item.Width:F1},{item.Thickness:F1},{item.Quantity}," +
                    $"\"{item.EdgeTop}\",\"{item.EdgeBottom}\",\"{item.EdgeLeft}\",\"{item.EdgeRight}\"," +
                    $"\"{item.EdgeInfo}\",\"{item.TextureDirection}\",{item.Area:F4}," +
                    $"\"{item.Remarks}\",\"{item.Category}\",{item.UnitPrice:F2},{item.TotalPrice:F2}");
            }

            // 统计行
            sb.AppendLine();
            sb.AppendLine($",,,,,总计,,,,,,{bom.TotalPanelCount + bom.TotalHardwareCount},,,,,,,,,{bom.TotalPanelArea:F4},,,{bom.TotalPrice:F2}");

            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
            bom.ExportPath = filePath;
            return filePath;
        }

        /// <summary>
        /// 导出合并版BOM（相同规格合并数量）
        /// </summary>
        public static string ExportMergedBomToCsv(BomResult bom, string filePath)
        {
            // 合并相同规格的板件
            var mergedItems = new List<BomItem>();
            var groups = bom.Items.Where(i => i.Category == "板件")
                .GroupBy(i => $"{i.Material}|{i.Length:F1}|{i.Width:F1}|{i.Thickness:F1}|{i.EdgeTop}|{i.EdgeBottom}|{i.EdgeLeft}|{i.EdgeRight}");

            int seq = 0;
            foreach (var group in groups)
            {
                seq++;
                var first = group.First();
                mergedItems.Add(new BomItem
                {
                    Seq = seq,
                    Material = first.Material,
                    Length = first.Length,
                    Width = first.Width,
                    Thickness = first.Thickness,
                    Quantity = group.Sum(i => i.Quantity),
                    EdgeTop = first.EdgeTop,
                    EdgeBottom = first.EdgeBottom,
                    EdgeLeft = first.EdgeLeft,
                    EdgeRight = first.EdgeRight,
                    EdgeInfo = first.EdgeInfo,
                    Area = first.Area * group.Sum(i => i.Quantity),
                    Category = "板件",
                    Remarks = $"合并自{group.Count()}种板件"
                });
            }

            // 添加五金件
            var hwGroups = bom.Items.Where(i => i.Category == "五金件")
                .GroupBy(i => $"{i.PanelName}|{i.PanelType}|{i.Material}");

            foreach (var group in hwGroups)
            {
                seq++;
                var first = group.First();
                mergedItems.Add(new BomItem
                {
                    Seq = seq,
                    PanelName = first.PanelName,
                    PanelType = first.PanelType,
                    Material = first.Material,
                    Quantity = group.Sum(i => i.Quantity),
                    Category = "五金件",
                    UnitPrice = first.UnitPrice,
                    TotalPrice = group.Sum(i => i.TotalPrice),
                    Remarks = first.Remarks
                });
            }

            var sb = new StringBuilder();
            sb.AppendLine("序号,名称,类型,材质,长度,宽度,厚度,数量,上封边,下封边,左封边,右封边,面积(m²),分类,单价,总价,备注");

            foreach (var item in mergedItems)
            {
                sb.AppendLine($"{item.Seq},\"{item.PanelName}\",\"{item.PanelType}\",\"{item.Material}\"," +
                    $"{item.Length:F1},{item.Width:F1},{item.Thickness:F1},{item.Quantity}," +
                    $"\"{item.EdgeTop}\",\"{item.EdgeBottom}\",\"{item.EdgeLeft}\",\"{item.EdgeRight}\"," +
                    $"{item.Area:F4},\"{item.Category}\",{item.UnitPrice:F2},{item.TotalPrice:F2},\"{item.Remarks}\"");
            }

            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
            bom.ExportPath = filePath;
            return filePath;
        }

    }
}
