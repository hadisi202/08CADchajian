using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    /// <summary>
    /// 柜体外框管理服务
    /// 管理所有 CabinetFrame 实例的增删改查和持久化
    /// </summary>
    public static class CabinetFrameService
    {
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FurniturePlugin", "frames");
        private static Dictionary<string, CabinetFrame> _frames = new();
        private static bool _loaded = false;
        private static readonly Dictionary<IntPtr, Database> _hookedDatabases = new();

        /// <summary>获取所有外框</summary>
        public static List<CabinetFrame> GetAllFrames()
        {
            SyncFramesWithCurrentDrawing();
            return _frames.Values.ToList();
        }

        /// <summary>按ID获取外框</summary>
        public static CabinetFrame GetById(string frameId) =>
            _frames.GetValueOrDefault(frameId);

        /// <summary>添加外框</summary>
        public static void AddFrame(CabinetFrame frame)
        {
            _frames[frame.FrameId] = frame;
            EnsureActiveDocumentHooks();
            SaveFrame(frame);
        }

        /// <summary>删除外框</summary>
        public static void RemoveFrame(string frameId)
        {
            _frames.Remove(frameId);
            var path = GetFramePath(frameId);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>保存外框</summary>
        public static void SaveFrame(CabinetFrame frame)
        {
            EnsureDir();
            var path = GetFramePath(frame.FrameId);
            var json = frame.ToJson();
            File.WriteAllText(path, json);
        }

        /// <summary>加载所有外框</summary>
        public static void LoadAll()
        {
            if (_loaded) return;
            _loaded = true;
            _frames.Clear();
            EnsureDir();
            foreach (var file in Directory.GetFiles(DataDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var frame = CabinetFrame.FromJson(json);
                    if (frame != null)
                    {
                        frame.RebuildSpaceTree();
                        _frames[frame.FrameId] = frame;
                    }
                }
                catch { }
            }

            EnsureActiveDocumentHooks();
        }

        /// <summary>查找包含指定3D点的外框（世界坐标）</summary>
        public static CabinetFrame FindFrameContaining(Point3d worldPoint)
        {
            LoadAll();
            SyncFramesWithCurrentDrawing();
            foreach (var frame in _frames.Values)
            {
                var rx = worldPoint.X - frame.Origin.X;
                var ry = worldPoint.Y - frame.Origin.Y;
                var rz = worldPoint.Z - frame.Origin.Z;

                if (rx >= -5 && rx <= frame.Width + 5 &&
                    ry >= -50 && ry <= frame.Depth + 50 && // Y方向放宽（门板在前面）
                    rz >= -5 && rz <= frame.Height + 5)
                {
                    return frame;
                }
            }
            return null;
        }

        /// <summary>查找靠近指定3D点的外框（用于附属板件外挂区域）</summary>
        public static CabinetFrame FindFrameForAttachment(Point3d worldPoint)
        {
            LoadAll();
            SyncFramesWithCurrentDrawing();

            CabinetFrame bestFrame = null;
            double bestDistance = double.MaxValue;

            foreach (var frame in _frames.Values)
            {
                var rx = worldPoint.X - frame.Origin.X;
                var ry = worldPoint.Y - frame.Origin.Y;
                var rz = worldPoint.Z - frame.Origin.Z;

                double dx = rx < 0 ? -rx : (rx > frame.Width ? rx - frame.Width : 0);
                double dy = ry < -frame.DefaultThickness * 8 ? -frame.DefaultThickness * 8 - ry
                    : (ry > frame.Depth + frame.DefaultThickness * 8 ? ry - (frame.Depth + frame.DefaultThickness * 8) : 0);
                double dz = rz < -frame.DefaultThickness * 8 ? -frame.DefaultThickness * 8 - rz
                    : (rz > frame.Height + frame.DefaultThickness * 8 ? rz - (frame.Height + frame.DefaultThickness * 8) : 0);

                double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestFrame = frame;
                }
            }

            return bestDistance <= 180 ? bestFrame : null;
        }

        /// <summary>
        /// 查找最近的同柜号板件，继承业务信息
        /// </summary>
        public static void InheritInfoFromNeighbors(CabinetFrame frame, PanelSlot newSlot)
        {
            // 先找同框同类型板件
            var similar = frame.Panels.FirstOrDefault(p => p.PanelType == newSlot.PanelType && p.PanelId != newSlot.PanelId);
            if (similar != null)
            {
                newSlot.Material = similar.Material;
                newSlot.EdgeTop = similar.EdgeTop;
                newSlot.EdgeBottom = similar.EdgeBottom;
                newSlot.EdgeLeft = similar.EdgeLeft;
                newSlot.EdgeRight = similar.EdgeRight;
                return;
            }

            // 再找同框任意板件
            var any = frame.Panels.FirstOrDefault(p => p.PanelId != newSlot.PanelId);
            if (any != null)
            {
                newSlot.OrderId = any.OrderId;
                newSlot.CabinetId = any.CabinetId;
                newSlot.RoomId = any.RoomId;
                newSlot.Material = any.Material;
            }
        }

        public static void SyncFramesWithCurrentDrawing()
        {
            LoadAll();
            EnsureActiveDocumentHooks();

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            var db = doc.Database;
            var changedFrames = new List<CabinetFrame>();
            var removedFrameIds = new List<string>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var frame in _frames.Values)
                {
                    // 历史数据兼容: 没有锚点且无板件的外框视为失效参数，自动清理
                    if (string.IsNullOrWhiteSpace(frame.FrameAnchorHandle) && (frame.Panels == null || frame.Panels.Count == 0))
                    {
                        removedFrameIds.Add(frame.FrameId);
                        continue;
                    }

                    // 有锚点但锚点实体不存在，说明外框已被删，自动清理整框参数
                    if (!string.IsNullOrWhiteSpace(frame.FrameAnchorHandle) && !IsFrameAnchorStillValid(db, tr, frame))
                    {
                        removedFrameIds.Add(frame.FrameId);
                        continue;
                    }

                    int before = frame.Panels.Count;
                    frame.Panels = frame.Panels
                        .Where(panel => IsPanelEntityStillValid(db, tr, panel))
                        .ToList();

                    if (frame.Panels.Count != before)
                    {
                        frame.RebuildSpaceTree();
                        changedFrames.Add(frame);
                    }
                }

                tr.Commit();
            }

            foreach (var frame in changedFrames)
            {
                SaveFrame(frame);
            }

            foreach (var frameId in removedFrameIds)
            {
                RemoveFrame(frameId);
            }
        }

        public static void EnsureActiveDocumentHooks()
        {
            var docManager = Application.DocumentManager;
            if (docManager == null)
                return;

            docManager.DocumentCreated -= OnDocumentCreated;
            docManager.DocumentCreated += OnDocumentCreated;

            var doc = docManager.MdiActiveDocument;
            if (doc == null)
                return;

            HookDatabaseEvents(doc.Database);
        }

        public static void ShutdownDocumentHooks()
        {
            var docManager = Application.DocumentManager;
            if (docManager != null)
            {
                docManager.DocumentCreated -= OnDocumentCreated;
            }

            foreach (var db in _hookedDatabases.Values)
            {
                db.ObjectErased -= OnDatabaseObjectErased;
            }

            _hookedDatabases.Clear();
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            if (e?.Document?.Database == null)
                return;

            HookDatabaseEvents(e.Document.Database);
        }

        private static void HookDatabaseEvents(Database db)
        {
            if (db == null)
                return;

            IntPtr key = db.UnmanagedObject;
            if (key == IntPtr.Zero || _hookedDatabases.ContainsKey(key))
                return;

            db.ObjectErased += OnDatabaseObjectErased;
            _hookedDatabases[key] = db;
        }

        private static void OnDatabaseObjectErased(object sender, ObjectErasedEventArgs e)
        {
            if (e == null || e.DBObject == null || e.Erased == false)
                return;

            var entity = e.DBObject as Entity;
            if (entity == null)
                return;

            string handle = entity.Handle.Value.ToString();
            if (string.IsNullOrWhiteSpace(handle))
                return;

            LoadAll();

            bool hasChanges = false;
            var removedFrames = new List<string>();
            foreach (var frame in _frames.Values)
            {
                if (!string.IsNullOrWhiteSpace(frame.FrameAnchorHandle) &&
                    string.Equals(frame.FrameAnchorHandle, handle, StringComparison.OrdinalIgnoreCase))
                {
                    removedFrames.Add(frame.FrameId);
                    hasChanges = true;
                    continue;
                }

                int removed = frame.Panels.RemoveAll(p => string.Equals(p.EntityHandle, handle, StringComparison.OrdinalIgnoreCase));
                if (removed <= 0)
                    continue;

                frame.RebuildSpaceTree();
                SaveFrame(frame);
                hasChanges = true;
            }

            foreach (var frameId in removedFrames)
            {
                RemoveFrame(frameId);
            }

            if (hasChanges)
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor?.WriteMessage("\n检测到板件被删除，已实时同步外框数据并重建内部空间。");
            }
        }

        private static bool IsPanelEntityStillValid(Database db, Transaction tr, PanelSlot panel)
        {
            if (db == null || tr == null || panel == null)
                return false;

            if (string.IsNullOrWhiteSpace(panel.EntityHandle))
                return false;

            if (!long.TryParse(panel.EntityHandle, out long handleValue))
                return false;

            try
            {
                var objectId = db.GetObjectId(false, new Handle(handleValue), 0);
                if (objectId.IsNull || objectId.IsErased)
                    return false;

                var entity = tr.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                return entity != null && !entity.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFrameAnchorStillValid(Database db, Transaction tr, CabinetFrame frame)
        {
            if (db == null || tr == null || frame == null)
                return false;
            if (string.IsNullOrWhiteSpace(frame.FrameAnchorHandle))
                return false;
            if (!long.TryParse(frame.FrameAnchorHandle, out long handleValue))
                return false;

            try
            {
                var objectId = db.GetObjectId(false, new Handle(handleValue), 0);
                if (objectId.IsNull || objectId.IsErased)
                    return false;

                var entity = tr.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                return entity != null && !entity.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static string GetFramePath(string frameId) => Path.Combine(DataDir, $"{frameId}.json");
        private static void EnsureDir() { if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir); }
    }
}
