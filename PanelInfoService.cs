using System;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;

namespace FurniturePlugin
{
    /// <summary>
    /// 统一的板件信息读写服务
    /// 所有 Extension Dictionary 的 PanelInfo 读写操作必须通过此类进行，
    /// 确保键名一致，避免数据丢失。
    /// 
    /// 字典键名统一为 "FurniturePluginData"，Xrecord 中使用管道分隔字符串格式。
    /// 读取时兼容旧的 "FurniturePlugin" 和 "PanelInfo" 键名。
    /// </summary>
    public static class PanelInfoService
    {
        /// <summary>
        /// 统一的 Extension Dictionary 键名
        /// </summary>
        public const string DictionaryKeyName = "FurniturePluginData";

        /// <summary>
        /// 旧版键名列表（用于读取兼容，写入时统一使用 DictionaryKeyName）
        /// </summary>
        private static readonly string[] LegacyKeyNames = { "FurniturePlugin", "PanelInfo" };

        #region 读取 PanelInfo

        /// <summary>
        /// 从实体读取 PanelInfo（自动创建短生命周期事务）
        /// </summary>
        /// <param name="obj">数据库对象（Entity/Solid3d）</param>
        /// <returns>PanelInfo 对象，如果不存在则返回 null</returns>
        public static PanelInfo GetPanelInfo(DBObject obj)
        {
            if (obj == null) return null;

            var db = obj.Database;
            using var tr = db.TransactionManager.StartTransaction();
            try
            {
                var info = GetPanelInfo(obj, tr);
                tr.Commit();
                return info;
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("从扩展字典读取 PanelInfo 失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 从实体读取 PanelInfo（使用已有事务）
        /// </summary>
        /// <param name="obj">数据库对象（Entity/Solid3d）</param>
        /// <param name="tr">已有事务</param>
        /// <returns>PanelInfo 对象，如果不存在则返回 null</returns>
        public static PanelInfo GetPanelInfo(DBObject obj, Transaction tr)
        {
            if (obj == null || tr == null) return null;

            ObjectId dictId = obj.ExtensionDictionary;
            if (dictId == ObjectId.Null) return null;

            DBDictionary extDict;
            try
            {
                extDict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
            }
            catch (System.Exception ex)
            {
                PluginLogger.Warning($"获取扩展字典失败: {ex.Message}");
                return null;
            }

            // 1. 首先尝试用当前键名读取
            if (extDict.Contains(DictionaryKeyName))
            {
                var info = ReadPanelInfoFromXrecord(extDict, tr, DictionaryKeyName);
                if (info != null) return info;
            }

            // 2. 回退尝试旧版键名（向后兼容）
            foreach (var legacyKey in LegacyKeyNames)
            {
                if (extDict.Contains(legacyKey))
                {
                    var info = ReadPanelInfoFromXrecord(extDict, tr, legacyKey);
                    if (info != null)
                    {
                        // 找到旧版数据，自动迁移到新键名
                        PluginLogger.Info($"检测到旧版键名 '{legacyKey}'，自动迁移到 '{DictionaryKeyName}'");
                        MigrateToNewKey(obj, tr, extDict, legacyKey, info);
                        return info;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 从 Xrecord 读取并反序列化 PanelInfo
        /// </summary>
        private static PanelInfo ReadPanelInfoFromXrecord(DBDictionary extDict, Transaction tr, string keyName)
        {
            try
            {
                ObjectId xrecId = extDict.GetAt(keyName);
                Xrecord xrec = (Xrecord)tr.GetObject(xrecId, OpenMode.ForRead);

                var dataArray = xrec.Data?.AsArray();
                if (dataArray == null || dataArray.Length <= 0) return null;

                var firstValue = (TypedValue)dataArray[0];
                if (firstValue.TypeCode != (short)DxfCode.Text) return null;

                string dataString = firstValue.Value as string;
                if (string.IsNullOrWhiteSpace(dataString)) return null;

                // 优先尝试管道分隔字符串格式
                try
                {
                    return PanelInfo.FromString(dataString);
                }
                catch
                {
                    // 回退尝试 JSON 格式（兼容旧版本）
                    try
                    {
                        return JsonSerializer.Deserialize<PanelInfo>(dataString);
                    }
                    catch (System.Exception ex)
                    {
                        PluginLogger.Warning($"PanelInfo 反序列化失败 (key={keyName}): {ex.Message}");
                        return null;
                    }
                }
            }
            catch (System.Exception ex)
            {
                PluginLogger.Warning($"从 Xrecord 读取失败 (key={keyName}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 将旧键名数据迁移到新键名（延迟到下次 SavePanelInfo 时执行，避免事务内锁升级冲突）
        /// </summary>
        private static void MigrateToNewKey(DBObject obj, Transaction tr, DBDictionary extDict, string oldKey, PanelInfo info)
        {
            try
            {
                if (!extDict.Contains(oldKey))
                    return;

                ObjectId oldXrecId = extDict.GetAt(oldKey);
                if (oldXrecId.IsNull || oldXrecId == ObjectId.Null)
                    return;

                var oldXrec = (Xrecord)tr.GetObject(oldXrecId, OpenMode.ForWrite);
                oldXrec.Erase();
            }
            catch (System.Exception ex)
            {
                PluginLogger.Warning($"清除旧键名 '{oldKey}' 失败: {ex.Message}");
            }
        }

        #endregion

        #region 写入 PanelInfo

        /// <summary>
        /// 保存 PanelInfo 到实体的扩展字典
        /// </summary>
        /// <param name="obj">数据库对象（Entity/Solid3d）</param>
        /// <param name="data">要保存的 PanelInfo</param>
        /// <param name="tr">事务</param>
        public static void SavePanelInfo(DBObject obj, PanelInfo data, Transaction tr)
        {
            if (obj == null || data == null || tr == null)
            {
                PluginLogger.Warning("SavePanelInfo 参数为空，跳过保存");
                return;
            }

            try
            {
                var extDict = GetOrCreateExtensionDictionary(obj, tr);

                // 使用自定义字符串格式序列化
                string dataString = data.ToString();

                Xrecord xrec;
                if (extDict.Contains(DictionaryKeyName))
                {
                    // 更新已有记录
                    ObjectId xrecId = extDict.GetAt(DictionaryKeyName);
                    xrec = (Xrecord)tr.GetObject(xrecId, OpenMode.ForWrite);
                }
                else
                {
                    // 创建新记录
                    xrec = new Xrecord();
                    extDict.SetAt(DictionaryKeyName, xrec);
                    tr.AddNewlyCreatedDBObject(xrec, true);
                }

                xrec.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, dataString));
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("保存 PanelInfo 到扩展字典失败", ex);
                throw;
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 获取或创建实体的扩展字典
        /// </summary>
        public static DBDictionary GetOrCreateExtensionDictionary(DBObject obj, Transaction tr)
        {
            ObjectId dictId = obj.ExtensionDictionary;
            if (dictId == ObjectId.Null)
            {
                obj.CreateExtensionDictionary();
                dictId = obj.ExtensionDictionary;
            }
            return (DBDictionary)tr.GetObject(dictId, OpenMode.ForWrite);
        }

        /// <summary>
        /// 检查实体是否包含 PanelInfo 数据
        /// </summary>
        public static bool HasPanelInfo(DBObject obj, Transaction tr)
        {
            if (obj == null || tr == null) return false;

            ObjectId dictId = obj.ExtensionDictionary;
            if (dictId == ObjectId.Null) return false;

            try
            {
                var extDict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
                return extDict.Contains(DictionaryKeyName) ||
                       extDict.Contains("FurniturePlugin") ||
                       extDict.Contains("PanelInfo");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 删除实体的 PanelInfo 数据
        /// </summary>
        public static bool DeletePanelInfo(DBObject obj, Transaction tr)
        {
            if (obj == null || tr == null) return false;

            try
            {
                ObjectId dictId = obj.ExtensionDictionary;
                if (dictId == ObjectId.Null) return false;

                var extDict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForWrite);
                bool deleted = false;

                // 删除所有可能的键名
                foreach (var key in new[] { DictionaryKeyName, "FurniturePlugin", "PanelInfo" })
                {
                    if (extDict.Contains(key))
                    {
                        var xrecId = extDict.GetAt(key);
                        var xrec = (Xrecord)tr.GetObject(xrecId, OpenMode.ForWrite);
                        xrec.Erase();
                        deleted = true;
                    }
                }

                return deleted;
            }
            catch (System.Exception ex)
            {
                PluginLogger.Error("删除 PanelInfo 失败", ex);
                return false;
            }
        }

        #endregion
    }
}
