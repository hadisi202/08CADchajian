using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FurniturePlugin
{
    /// <summary>
    /// 板件智能编号服务
    /// 支持按柜号、房间、板件类型等维度自动编号
    /// 编号格式: {柜号}-{板件类型}-{序号}，如 "A1-左侧板-01"
    /// </summary>
    public static class PanelNumberingService
    {
        /// <summary>
        /// 编号规则配置
        /// </summary>
        public class NumberingConfig
        {
            /// <summary>编号前缀（如柜号）</summary>
            public string Prefix { get; set; } = "";

            /// <summary>分隔符</summary>
            public string Separator { get; set; } = "-";

            /// <summary>序号位数（补零）</summary>
            public int SequenceDigits { get; set; } = 2;

            /// <summary>是否包含板件类型</summary>
            public bool IncludePanelType { get; set; } = true;

            /// <summary>是否按材质分组编号</summary>
            public bool GroupByMaterial { get; set; } = false;

            /// <summary>是否按封边分组编号</summary>
            public bool GroupByEdgeBand { get; set; } = false;

            /// <summary>自定义编号模板（支持 {prefix}、{type}、{seq}、{material} 占位符）</summary>
            public string? CustomTemplate { get; set; }
        }

        /// <summary>
        /// 对板件列表执行智能编号
        /// </summary>
        public static Dictionary<string, string> GenerateNumbers(
            List<PanelInfo> panels, NumberingConfig config)
        {
            var result = new Dictionary<string, string>();

            if (panels == null || panels.Count == 0) return result;

            // 按板件类型分组
            var groups = panels.GroupBy(p => GetPanelTypeShortName(p.PanelName ?? ""));

            foreach (var group in groups)
            {
                int seq = 1;
                foreach (var panel in group)
                {
                    var number = BuildNumber(config, group.Key, seq);
                    result[panel.EntityId ?? $"unknown_{seq}"] = number;
                    seq++;
                }
            }

            return result;
        }

        /// <summary>
        /// 按柜号分组编号
        /// </summary>
        public static Dictionary<string, string> GenerateNumbersByCabinet(
            List<PanelInfo> panels, NumberingConfig config)
        {
            var result = new Dictionary<string, string>();

            // 先按柜号分组
            var cabinetGroups = panels.GroupBy(p => p.CabinetId ?? "未分配");

            foreach (var cabinetGroup in cabinetGroups)
            {
                var cabinetPrefix = string.IsNullOrEmpty(config.Prefix) 
                    ? cabinetGroup.Key 
                    : config.Prefix;

                // 再按板件类型分组
                var typeGroups = cabinetGroup.GroupBy(p => GetPanelTypeShortName(p.PanelName ?? ""));

                // 按类型优先级排序：侧板 → 顶底板 → 层板 → 背板 → 门板 → 其他
                var orderedTypes = typeGroups.OrderBy(g => GetTypePriority(g.Key));

                int seq = 1;
                foreach (var typeGroup in orderedTypes)
                {
                    foreach (var panel in typeGroup)
                    {
                        var number = config.IncludePanelType
                            ? BuildNumber(config, cabinetPrefix, typeGroup.Key, seq)
                            : BuildNumber(config, cabinetPrefix, "", seq);
                        result[panel.EntityId ?? $"unknown_{seq}"] = number;
                        panel.PanelName = number;
                        seq++;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 获取板件类型简称
        /// </summary>
        public static string GetPanelTypeShortName(string panelName)
        {
            if (string.IsNullOrEmpty(panelName)) return "未知";

            if (panelName.Contains("抽屉盒侧板")) return "抽屉盒侧板";
            if (panelName.Contains("抽屉盒背板")) return "抽屉盒背板";
            if (panelName.Contains("抽屉盒底板")) return "抽屉盒底板";
            if (panelName.Contains("抽屉面板") || panelName.Contains("抽面")) return "抽屉面板";
            if (panelName.Contains("左侧板")) return "左侧板";
            if (panelName.Contains("右侧板")) return "右侧板";
            if (panelName.Contains("侧板")) return "侧板";
            if (panelName.Contains("顶板")) return "顶板";
            if (panelName.Contains("底板")) return "底板";
            if (panelName.Contains("固定层板")) return "固定层板";
            if (panelName.Contains("活动层板")) return "活动层板";
            if (panelName.Contains("层板")) return "层板";
            if (panelName.Contains("竖隔板") || panelName.Contains("竖板") || panelName.Contains("立板")) return "立板";
            if (panelName.Contains("背板")) return "背板";
            if (panelName.Contains("门板")) return "门板";
            if (panelName.Contains("踢脚板")) return "踢脚板";
            if (panelName.Contains("收口板")) return "收口板";
            if (panelName.Contains("辅助板")) return "辅助板";
            if (panelName.Contains("见光板")) return "见光板";
            if (panelName.Contains("封板")) return "封板";
            if (panelName.Contains("拉条") || panelName.Contains("加强条")) return "拉条";
            if (panelName.Contains("抽屉")) return "抽屉";

            return "其他";
        }

        /// <summary>
        /// 板件类型排序优先级（编号时侧板先编号）
        /// </summary>
        private static int GetTypePriority(string typeName) => typeName switch
        {
            "左侧板" => 1,
            "右侧板" => 2,
            "侧板" => 3,
            "顶板" => 4,
            "底板" => 5,
            "踢脚板" => 6,
            "固定层板" => 7,
            "活动层板" => 8,
            "层板" => 9,
            "立板" => 10,
            "背板" => 11,
            "门板" => 12,
            "抽屉面板" => 13,
            "抽屉盒侧板" => 14,
            "抽屉盒背板" => 15,
            "抽屉盒底板" => 16,
            "抽屉" => 17,
            "收口板" => 18,
            "辅助板" => 19,
            "见光板" => 20,
            "封板" => 21,
            "拉条" => 22,
            _ => 99
        };

        private static string BuildNumber(NumberingConfig config, string type, int seq)
        {
            var prefix = config.Prefix ?? "";
            var seqStr = seq.ToString($"D{config.SequenceDigits}");

            if (!string.IsNullOrEmpty(config.CustomTemplate))
            {
                return config.CustomTemplate
                    .Replace("{prefix}", prefix)
                    .Replace("{type}", type)
                    .Replace("{seq}", seqStr);
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(prefix)) parts.Add(prefix);
            if (config.IncludePanelType && !string.IsNullOrEmpty(type)) parts.Add(type);
            parts.Add(seqStr);

            return string.Join(config.Separator, parts);
        }

        private static string BuildNumber(NumberingConfig config, string prefix, string type, int seq)
        {
            var seqStr = seq.ToString($"D{config.SequenceDigits}");

            if (!string.IsNullOrEmpty(config.CustomTemplate))
            {
                return config.CustomTemplate
                    .Replace("{prefix}", prefix)
                    .Replace("{type}", type)
                    .Replace("{seq}", seqStr);
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(prefix)) parts.Add(prefix);
            if (config.IncludePanelType && !string.IsNullOrEmpty(type)) parts.Add(type);
            parts.Add(seqStr);

            return string.Join(config.Separator, parts);
        }
    }
}
