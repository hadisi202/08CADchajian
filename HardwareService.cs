using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FurniturePlugin
{
    /// <summary>
    /// 五金件管理服务
    /// 负责五金件数据的增删改查和持久化
    /// 数据存储在 %AppData%/FurniturePlugin/Hardware/ 目录下
    /// </summary>
    public static class HardwareService
    {
        private static readonly string _dataDirectory;
        private static readonly object _lock = new();
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// 内存缓存：订单号 -> 五金件列表
        /// </summary>
        private static readonly Dictionary<string, List<HardwareInfo>> _cache = new();

        static HardwareService()
        {
            _dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FurniturePlugin", "Hardware");
            try
            {
                if (!Directory.Exists(_dataDirectory))
                    Directory.CreateDirectory(_dataDirectory);
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("五金件数据目录创建失败", ex);
            }
        }

        #region CRUD 操作

        /// <summary>
        /// 添加五金件
        /// </summary>
        public static bool AddHardware(HardwareInfo hardware)
        {
            if (hardware == null) return false;

            lock (_lock)
            {
                try
                {
                    var list = GetHardwareList(hardware.OrderId, writable: true);
                    hardware.Id = Guid.NewGuid().ToString("N");
                    list.Add(hardware);
                    SaveToFile(hardware.OrderId);
                    PluginLogger.Info($"添加五金件: {hardware.Name} ({hardware.TypeDisplayName}) x{hardware.Quantity}");
                    return true;
                }
                catch (System.Exception ex)
                {
                    PluginLogger.Error("添加五金件失败", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// 批量添加五金件
        /// </summary>
        public static int AddHardwareRange(IEnumerable<HardwareInfo> items)
        {
            if (items == null) return 0;
            int count = 0;
            lock (_lock)
            {
                try
                {
                    var groups = items.GroupBy(h => h.OrderId);
                    foreach (var group in groups)
                    {
                        var list = GetHardwareList(group.Key, writable: true);
                        foreach (var item in group)
                        {
                            item.Id = Guid.NewGuid().ToString("N");
                            list.Add(item);
                            count++;
                        }
                        SaveToFile(group.Key);
                    }
                    PluginLogger.Info($"批量添加五金件: {count} 件");
                    return count;
                }
                catch (System.Exception ex)
                {
                    PluginLogger.Error("批量添加五金件失败", ex);
                    return count;
                }
            }
        }

        /// <summary>
        /// 更新五金件
        /// </summary>
        public static bool UpdateHardware(HardwareInfo hardware)
        {
            if (hardware == null || string.IsNullOrEmpty(hardware.Id)) return false;

            lock (_lock)
            {
                try
                {
                    var list = GetHardwareList(hardware.OrderId, writable: true);
                    var index = list.FindIndex(h => h.Id == hardware.Id);
                    if (index < 0) return false;
                    list[index] = hardware;
                    SaveToFile(hardware.OrderId);
                    PluginLogger.Info($"更新五金件: {hardware.Name}");
                    return true;
                }
                catch (System.Exception ex)
                {
                    PluginLogger.Error("更新五金件失败", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// 删除五金件
        /// </summary>
        public static bool DeleteHardware(string orderId, string hardwareId)
        {
            if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(hardwareId)) return false;

            lock (_lock)
            {
                try
                {
                    var list = GetHardwareList(orderId, writable: true);
                    var index = list.FindIndex(h => h.Id == hardwareId);
                    if (index < 0) return false;
                    var removed = list[index];
                    list.RemoveAt(index);
                    SaveToFile(orderId);
                    PluginLogger.Info($"删除五金件: {removed.Name}");
                    return true;
                }
                catch (System.Exception ex)
                {
                    PluginLogger.Error("删除五金件失败", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// 获取某个订单的所有五金件
        /// </summary>
        public static List<HardwareInfo> GetHardwareByOrder(string orderId)
        {
            if (string.IsNullOrEmpty(orderId)) return new List<HardwareInfo>();
            return GetHardwareList(orderId, writable: false);
        }

        /// <summary>
        /// 获取某个柜体的所有五金件
        /// </summary>
        public static List<HardwareInfo> GetHardwareByCabinet(string orderId, string cabinetId)
        {
            return GetHardwareByOrder(orderId)
                .Where(h => h.CabinetId == cabinetId)
                .ToList();
        }

        /// <summary>
        /// 获取关联到某个板件的所有五金件
        /// </summary>
        public static List<HardwareInfo> GetHardwareByPanel(string panelEntityId)
        {
            if (string.IsNullOrEmpty(panelEntityId)) return new List<HardwareInfo>();

            var result = new List<HardwareInfo>();
            lock (_lock)
            {
                foreach (var kvp in _cache)
                {
                    result.AddRange(kvp.Value.Where(h => h.AssociatedPanelId == panelEntityId));
                }
            }

            // 也检查文件中未缓存的数据
            try
            {
                foreach (var file in Directory.GetFiles(_dataDirectory, "*.json"))
                {
                    var orderId = Path.GetFileNameWithoutExtension(file);
                    if (!_cache.ContainsKey(orderId))
                    {
                        var list = LoadFromFile(orderId);
                        if (list != null)
                            result.AddRange(list.Where(h => h.AssociatedPanelId == panelEntityId));
                    }
                }
            }
            catch (System.Exception ex)
            {
                PluginLogger.Warning($"扫描五金件文件失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 获取所有五金件（跨订单）
        /// </summary>
        public static List<HardwareInfo> GetAllHardware()
        {
            var result = new List<HardwareInfo>();
            try
            {
                foreach (var file in Directory.GetFiles(_dataDirectory, "*.json"))
                {
                    var orderId = Path.GetFileNameWithoutExtension(file);
                    var list = GetHardwareList(orderId, writable: false);
                    result.AddRange(list);
                }
            }
            catch (System.Exception ex)
            {
                PluginLogger.Warning($"获取全部五金件失败: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 按类型统计五金件数量
        /// </summary>
        public static Dictionary<HardwareType, double> GetHardwareSummaryByType(string orderId)
        {
            var items = GetHardwareByOrder(orderId);
            return items.GroupBy(h => h.Type)
                .ToDictionary(g => g.Key, g => g.Sum(h => h.Quantity));
        }

        /// <summary>
        /// 计算某个订单的五金件总金额
        /// </summary>
        public static double GetTotalPrice(string orderId)
        {
            return GetHardwareByOrder(orderId).Sum(h => h.TotalPrice);
        }

        /// <summary>
        /// 清除某个订单的所有五金件
        /// </summary>
        public static bool ClearOrderHardware(string orderId)
        {
            lock (_lock)
            {
                try
                {
                    _cache.Remove(orderId);
                    var filePath = Path.Combine(_dataDirectory, $"{orderId}.json");
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                    PluginLogger.Info($"清除订单五金件: {orderId}");
                    return true;
                }
                catch (System.Exception ex)
                {
                    PluginLogger.Error("清除订单五金件失败", ex);
                    return false;
                }
            }
        }

        #endregion

        #region 五金件模板（常用五金件快速选用）

        /// <summary>
        /// 获取常用五金件模板列表
        /// </summary>
        public static List<HardwareInfo> GetCommonTemplates()
        {
            return new List<HardwareInfo>
            {
                new() { Name = "全盖铰链", Type = HardwareType.Hinge, Model = "DTC 35mm全盖", Brand = "DTC", Unit = "个", UnitPrice = 3.5 },
                new() { Name = "半盖铰链", Type = HardwareType.Hinge, Model = "DTC 35mm半盖", Brand = "DTC", Unit = "个", UnitPrice = 3.5 },
                new() { Name = "内藏铰链", Type = HardwareType.Hinge, Model = "DTC 35mm内藏", Brand = "DTC", Unit = "个", UnitPrice = 4.0 },
                new() { Name = "三节滑轨", Type = HardwareType.SlideRail, Model = "DTC 三节钢珠滑轨", Brand = "DTC", Unit = "付", UnitPrice = 15.0 },
                new() { Name = "隐藏滑轨", Type = HardwareType.SlideRail, Model = "百隆隐藏式阻尼滑轨", Brand = "Blum", Unit = "付", UnitPrice = 65.0 },
                new() { Name = "铝合金拉手", Type = HardwareType.Handle, Model = "128mm孔距", Material = "铝合金", Unit = "个", UnitPrice = 5.0 },
                new() { Name = "暗拉手", Type = HardwareType.Handle, Model = "Gola槽", Material = "铝合金", Unit = "米", UnitPrice = 25.0 },
                new() { Name = "三合一连接件", Type = HardwareType.Connector, Model = "三合一+木榫", Unit = "套", UnitPrice = 0.5 },
                new() { Name = "层板托", Type = HardwareType.ShelfSupport, Model = "钢制层板托", Unit = "个", UnitPrice = 0.5 },
                new() { Name = "衣通", Type = HardwareType.ClothesRail, Model = "铝合金衣通+支座", Material = "铝合金", Unit = "根", UnitPrice = 20.0 },
                new() { Name = "裤架", Type = HardwareType.TrouserRack, Model = "阻尼裤架", Unit = "个", UnitPrice = 80.0 },
                new() { Name = "穿衣镜", Type = HardwareType.Mirror, Model = "全身镜", Unit = "块", UnitPrice = 45.0 },
                new() { Name = "感应灯带", Type = HardwareType.LightStrip, Model = "手扫感应灯", Unit = "条", UnitPrice = 35.0 },
            };
        }

        #endregion

        #region 持久化

        private static List<HardwareInfo> GetHardwareList(string orderId, bool writable)
        {
            if (_cache.TryGetValue(orderId, out var cached))
                return cached;

            var list = LoadFromFile(orderId) ?? new List<HardwareInfo>();
            if (writable)
                _cache[orderId] = list;
            return list;
        }

        private static List<HardwareInfo>? LoadFromFile(string orderId)
        {
            var filePath = Path.Combine(_dataDirectory, $"{orderId}.json");
            if (!File.Exists(filePath)) return null;

            try
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<List<HardwareInfo>>(json, _jsonOptions);
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error($"加载五金件数据失败: {orderId}", ex);
                return null;
            }
        }

        private static void SaveToFile(string orderId)
        {
            if (!_cache.TryGetValue(orderId, out var list)) return;

            var filePath = Path.Combine(_dataDirectory, $"{orderId}.json");
            try
            {
                var json = JsonSerializer.Serialize(list, _jsonOptions);
                File.WriteAllText(filePath, json);
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error($"保存五金件数据失败: {orderId}", ex);
            }
        }

        #endregion
    }
}
