using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FurniturePlugin
{
    /// <summary>
    /// 柜体模板管理服务
    /// 提供预设模板和用户自定义模板的增删改查
    /// 模板数据存储在 %AppData%/FurniturePlugin/Templates/ 目录下
    /// </summary>
    public static class CabinetTemplateService
    {
        private static readonly string _templateDirectory;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        static CabinetTemplateService()
        {
            _templateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FurniturePlugin", "Templates");
            try
            {
                if (!Directory.Exists(_templateDirectory))
                    Directory.CreateDirectory(_templateDirectory);
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("模板目录创建失败", ex);
            }
        }

        /// <summary>
        /// 获取所有预设模板
        /// </summary>
        public static List<CabinetTemplate> GetPresetTemplates()
        {
            return new List<CabinetTemplate>
            {
                new() { Name = "标准衣柜", Category = "衣柜", Description = "标准双门衣柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 1800, CabinetDepth = 600, CabinetHeight = 2400,
                    HasDoor = true, DoorCount = 2, ShelfCount = 3,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
                new() { Name = "三门衣柜", Category = "衣柜", Description = "三门大衣柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 2400, CabinetDepth = 600, CabinetHeight = 2400,
                    HasDoor = true, DoorCount = 3, ShelfCount = 4,
                    HasDrawer = true, DrawerCount = 3,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
                new() { Name = "书柜", Category = "书柜", Description = "标准四层书柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 800, CabinetDepth = 350, CabinetHeight = 2000,
                    HasDoor = false, DoorCount = 0, ShelfCount = 5,
                    HasKickPlate = false, BottomKickHeight = 0,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
                new() { Name = "酒柜", Category = "酒柜", Description = "玻璃门酒柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 1000, CabinetDepth = 350, CabinetHeight = 2200,
                    HasDoor = true, DoorCount = 2, ShelfCount = 6,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
                new() { Name = "鞋柜", Category = "鞋柜", Description = "入户鞋柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 1200, CabinetDepth = 350, CabinetHeight = 1000,
                    HasDoor = true, DoorCount = 2, ShelfCount = 3,
                    HasDrawer = true, DrawerCount = 2, DrawerHeight = 120,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
                new() { Name = "橱柜地柜", Category = "橱柜", Description = "标准橱柜地柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 800, CabinetDepth = 580, CabinetHeight = 720,
                    HasDoor = true, DoorCount = 1, ShelfCount = 0,
                    HasDrawer = true, DrawerCount = 3, DrawerHeight = 130,
                    HasKickPlate = true, BottomKickHeight = 100,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
                new() { Name = "橱柜吊柜", Category = "橱柜", Description = "标准橱柜吊柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 800, CabinetDepth = 350, CabinetHeight = 700,
                    HasDoor = true, DoorCount = 1, ShelfCount = 1,
                    HasKickPlate = false, BottomKickHeight = 0,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
                new() { Name = "电视柜", Category = "电视柜", Description = "客厅电视柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 1800, CabinetDepth = 400, CabinetHeight = 450,
                    HasDoor = true, DoorCount = 2, ShelfCount = 1,
                    HasKickPlate = true, BottomKickHeight = 50,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
                new() { Name = "床头柜", Category = "床头柜", Description = "标准床头柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 500, CabinetDepth = 420, CabinetHeight = 500,
                    HasDoor = false, DoorCount = 0, ShelfCount = 0,
                    HasDrawer = true, DrawerCount = 2, DrawerHeight = 130,
                    HasKickPlate = false, BottomKickHeight = 0,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
                new() { Name = "阳台柜", Category = "阳台柜", Description = "洗衣机阳台柜", Config = new CabinetDecomposeConfig
                {
                    CabinetWidth = 1200, CabinetDepth = 600, CabinetHeight = 900,
                    HasDoor = true, DoorCount = 2, ShelfCount = 1,
                    Material = "18mm多层实木板", BackPanelMaterial = "9mm密度板"
                }},
            };
        }

        /// <summary>
        /// 获取所有用户自定义模板
        /// </summary>
        public static List<CabinetTemplate> GetUserTemplates()
        {
            var templates = new List<CabinetTemplate>();
            try
            {
                foreach (var file in Directory.GetFiles(_templateDirectory, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var template = JsonSerializer.Deserialize<CabinetTemplate>(json, _jsonOptions);
                        if (template != null)
                        {
                            template.FilePath = file;
                            templates.Add(template);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        PluginLogger.Warning($"加载模板 {file} 失败: {ex.Message}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("扫描用户模板失败", ex);
            }
            return templates;
        }

        /// <summary>
        /// 保存用户模板
        /// </summary>
        public static bool SaveTemplate(CabinetTemplate template)
        {
            if (template == null || string.IsNullOrEmpty(template.Name)) return false;

            try
            {
                var fileName = $"{template.Category ?? "其他"}_{template.Name}_{DateTime.Now:yyyyMMddHHmmss}.json";
                // 清理文件名中的非法字符
                foreach (var c in Path.GetInvalidFileNameChars())
                    fileName = fileName.Replace(c, '_');

                var filePath = Path.Combine(_templateDirectory, fileName);
                var json = JsonSerializer.Serialize(template, _jsonOptions);
                File.WriteAllText(filePath, json);
                template.FilePath = filePath;
                PluginLogger.Info($"保存模板: {template.Name}");
                return true;
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("保存模板失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 删除用户模板
        /// </summary>
        public static bool DeleteTemplate(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    PluginLogger.Info($"删除模板: {filePath}");
                    return true;
                }
                return false;
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("删除模板失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 获取所有模板分类
        /// </summary>
        public static List<string> GetAllCategories()
        {
            var categories = new HashSet<string>();
            foreach (var t in GetPresetTemplates())
                categories.Add(t.Category ?? "其他");
            foreach (var t in GetUserTemplates())
                categories.Add(t.Category ?? "其他");
            return categories.OrderBy(c => c).ToList();
        }
    }

    /// <summary>
    /// 柜体模板
    /// </summary>
    public class CabinetTemplate
    {
        /// <summary>模板名称</summary>
        public string Name { get; set; } = "";

        /// <summary>分类</summary>
        public string Category { get; set; } = "";

        /// <summary>描述</summary>
        public string Description { get; set; } = "";

        /// <summary>柜体参数配置</summary>
        public CabinetDecomposeConfig Config { get; set; } = new();

        /// <summary>缩略图路径</summary>
        public string? ThumbnailPath { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>是否为预设模板</summary>
        public bool IsPreset { get; set; }

        /// <summary>文件路径（仅用户模板）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? FilePath { get; set; }
    }
}
