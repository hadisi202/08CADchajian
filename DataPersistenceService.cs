using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FurniturePlugin
{
    /// <summary>
    /// 数据持久化服务类，负责保存和加载参数化建模的配置数据
    /// </summary>
    public static class DataPersistenceService
    {
        private static readonly string _baseDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FurniturePlugin", "ParametricData");

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        static DataPersistenceService()
        {
            // 确保数据目录存在
            if (!Directory.Exists(_baseDataPath))
            {
                Directory.CreateDirectory(_baseDataPath);
            }
        }

        #region 侧板数据持久化

        /// <summary>
        /// 保存侧板配置数据
        /// </summary>
        public static void SaveSidePanelData(SidePanelData data, string configName = "default")
        {
            try
            {
                var filePath = Path.Combine(_baseDataPath, $"SidePanel_{configName}.json");
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                File.WriteAllText(filePath, json);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存侧板数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载侧板配置数据
        /// </summary>
        public static SidePanelData LoadSidePanelData(string configName = "default")
        {
            try
            {
                var filePath = Path.Combine(_baseDataPath, $"SidePanel_{configName}.json");
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    return JsonSerializer.Deserialize<SidePanelData>(json, _jsonOptions) ?? new SidePanelData();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载侧板数据失败: {ex.Message}");
            }
            return new SidePanelData();
        }

        /// <summary>
        /// 获取侧板配置列表
        /// </summary>
        public static List<string> GetSidePanelConfigs()
        {
            return GetConfigList("SidePanel_");
        }

        #endregion

        #region 顶底板数据持久化

        /// <summary>
        /// 保存顶底板配置数据
        /// </summary>
        public static void SaveTopBottomPanelData(TopBottomPanelData data, string configName = "default")
        {
            try
            {
                var filePath = Path.Combine(_baseDataPath, $"TopBottomPanel_{configName}.json");
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                File.WriteAllText(filePath, json);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存顶底板数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载顶底板配置数据
        /// </summary>
        public static TopBottomPanelData LoadTopBottomPanelData(string configName = "default")
        {
            try
            {
                var filePath = Path.Combine(_baseDataPath, $"TopBottomPanel_{configName}.json");
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    return JsonSerializer.Deserialize<TopBottomPanelData>(json, _jsonOptions) ?? new TopBottomPanelData();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载顶底板数据失败: {ex.Message}");
            }
            return new TopBottomPanelData();
        }

        /// <summary>
        /// 获取顶底板配置列表
        /// </summary>
        public static List<string> GetTopBottomPanelConfigs()
        {
            return GetConfigList("TopBottomPanel_");
        }

        #endregion

        #region 层板/竖隔板数据持久化

        /// <summary>
        /// 保存层板/竖隔板配置数据
        /// </summary>
        public static void SaveShelfPartitionData(ShelfPartitionData data, string configName = "default")
        {
            try
            {
                var filePath = Path.Combine(_baseDataPath, $"ShelfPartition_{configName}.json");
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                File.WriteAllText(filePath, json);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存层板/竖隔板数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载层板/竖隔板配置数据
        /// </summary>
        public static ShelfPartitionData LoadShelfPartitionData(string configName = "default")
        {
            try
            {
                var filePath = Path.Combine(_baseDataPath, $"ShelfPartition_{configName}.json");
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    return JsonSerializer.Deserialize<ShelfPartitionData>(json, _jsonOptions) ?? new ShelfPartitionData();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载层板/竖隔板数据失败: {ex.Message}");
            }
            return new ShelfPartitionData();
        }

        /// <summary>
        /// 获取层板/竖隔板配置列表
        /// </summary>
        public static List<string> GetShelfPartitionConfigs()
        {
            return GetConfigList("ShelfPartition_");
        }

        #endregion

        #region 背板数据持久化

        /// <summary>
        /// 保存背板配置数据
        /// </summary>
        public static void SaveBackPanelData(BackPanelData data, string configName = "default")
        {
            try
            {
                var filePath = Path.Combine(_baseDataPath, $"BackPanel_{configName}.json");
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                File.WriteAllText(filePath, json);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存背板数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载背板配置数据
        /// </summary>
        public static BackPanelData LoadBackPanelData(string configName = "default")
        {
            try
            {
                var filePath = Path.Combine(_baseDataPath, $"BackPanel_{configName}.json");
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    return JsonSerializer.Deserialize<BackPanelData>(json, _jsonOptions) ?? new BackPanelData();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载背板数据失败: {ex.Message}");
            }
            return new BackPanelData();
        }

        /// <summary>
        /// 获取背板配置列表
        /// </summary>
        public static List<string> GetBackPanelConfigs()
        {
            return GetConfigList("BackPanel_");
        }

        #endregion

        #region 通用方法

        /// <summary>
        /// 获取指定前缀的配置文件列表
        /// </summary>
        private static List<string> GetConfigList(string prefix)
        {
            var configs = new List<string>();
            try
            {
                if (Directory.Exists(_baseDataPath))
                {
                    var files = Directory.GetFiles(_baseDataPath, $"{prefix}*.json");
                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        var configName = fileName.Substring(prefix.Length);
                        configs.Add(configName);
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"导入板件信息失败: {ex.Message}");
            }
            return configs;
        }

        /// <summary>
        /// 删除指定配置
        /// </summary>
        public static bool DeleteConfig(string configType, string configName)
        {
            try
            {
                var filePath = Path.Combine(_baseDataPath, $"{configType}_{configName}.json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载会话配置失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 重命名配置
        /// </summary>
        public static bool RenameConfig(string configType, string oldName, string newName)
        {
            try
            {
                var oldPath = Path.Combine(_baseDataPath, $"{configType}_{oldName}.json");
                var newPath = Path.Combine(_baseDataPath, $"{configType}_{newName}.json");
                
                if (File.Exists(oldPath) && !File.Exists(newPath))
                {
                    File.Move(oldPath, newPath);
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存板件信息失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 导出配置到指定路径
        /// </summary>
        public static bool ExportConfig(string configType, string configName, string exportPath)
        {
            try
            {
                var sourcePath = Path.Combine(_baseDataPath, $"{configType}_{configName}.json");
                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, exportPath, true);
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"导出板件信息失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 从指定路径导入配置
        /// </summary>
        public static bool ImportConfig(string configType, string configName, string importPath)
        {
            try
            {
                if (File.Exists(importPath))
                {
                    var targetPath = Path.Combine(_baseDataPath, $"{configType}_{configName}.json");
                    File.Copy(importPath, targetPath, true);
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存会话配置失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 清理所有配置数据
        /// </summary>
        public static void ClearAllConfigs()
        {
            try
            {
                if (Directory.Exists(_baseDataPath))
                {
                    var files = Directory.GetFiles(_baseDataPath, "*.json");
                    foreach (var file in files)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"导出板件信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取数据存储路径
        /// </summary>
        public static string GetDataPath()
        {
            return _baseDataPath;
        }

        #endregion
    }

    /// <summary>
    /// 配置管理器，提供更高级的配置管理功能
    /// </summary>
    public static class ConfigurationManager
    {
        /// <summary>
        /// 保存当前会话的所有配置
        /// </summary>
        public static void SaveSessionConfigs(string sessionName)
        {
            try
            {
                var sessionData = new SessionConfiguration
                {
                    SessionName = sessionName,
                    CreatedTime = DateTime.Now,
                    SidePanelConfig = "default",
                    TopBottomPanelConfig = "default",
                    ShelfPartitionConfig = "default",
                    BackPanelConfig = "default"
                };

                var sessionPath = Path.Combine(DataPersistenceService.GetDataPath(), $"Session_{sessionName}.json");
                var json = JsonSerializer.Serialize(sessionData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(sessionPath, json);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存板件信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载会话配置
        /// </summary>
        public static SessionConfiguration LoadSessionConfig(string sessionName)
        {
            try
            {
                var sessionPath = Path.Combine(DataPersistenceService.GetDataPath(), $"Session_{sessionName}.json");
                if (File.Exists(sessionPath))
                {
                    var json = File.ReadAllText(sessionPath);
                    return JsonSerializer.Deserialize<SessionConfiguration>(json) ?? new SessionConfiguration();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载板件信息失败: {ex.Message}");
            }
            return new SessionConfiguration();
        }

        /// <summary>
        /// 获取所有会话列表
        /// </summary>
        public static List<string> GetSessionList()
        {
            var sessions = new List<string>();
            try
            {
                var dataPath = DataPersistenceService.GetDataPath();
                if (Directory.Exists(dataPath))
                {
                    var files = Directory.GetFiles(dataPath, "Session_*.json");
                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        var sessionName = fileName.Substring(8); // 移除"Session_"前缀
                        sessions.Add(sessionName);
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载板件信息失败: {ex.Message}");
            }
            return sessions;
        }
    }

    /// <summary>
    /// 会话配置类
    /// </summary>
    public class SessionConfiguration
    {
        public string SessionName { get; set; } = "";
        public DateTime CreatedTime { get; set; }
        public string SidePanelConfig { get; set; } = "default";
        public string TopBottomPanelConfig { get; set; } = "default";
        public string ShelfPartitionConfig { get; set; } = "default";
        public string BackPanelConfig { get; set; } = "default";
        public string Description { get; set; } = "";
    }
}