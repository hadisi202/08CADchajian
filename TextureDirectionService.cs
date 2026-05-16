using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin
{
    /// <summary>
    /// 纹路方向管理服务
    /// </summary>
    public static class TextureDirectionService
    {
        public const string TextureLayerName = "纹路显示";
        public const string TextureMarker = "FURNITURE_TEXTURE_ARROW";
        private const string TextureRegAppName = "FURNITURE_TEXTURE_META";

        /// <summary>
        /// 根据板件的实际物理尺寸与摆放姿态，自动判定纹理的初始方向。
        ///
        /// 核心逻辑：
        /// - 克隆实体，用 BZBJ 算法在 clone 上找局部坐标系（厚轴/长轴/宽轴）
        /// - 将长轴和宽轴投影到世界 XY 平面，计算各自的长度
        /// - 投影后宽度 ≈ 0 的情况（板件窄面朝上/朝前），降级为"厚度仅在 Z 向"的竖板逻辑
        /// - 长轴 > 宽轴 投影长度 → 纹理沿长边（AlongLength）
        /// - 宽轴 > 长轴 投影长度 → 纹理沿宽边（AlongWidth）
        /// - 克隆体不写回数据库，安全无害
        ///
        /// 返回值：AlongLength 表示纹理沿板件物理长边方向；
        ///         AlongWidth  表示纹理沿板件物理宽边方向。
        /// </summary>
        public static TextureDirection DetectInitialTextureDirection(Solid3d solid, PanelInfo panelInfo)
        {
            if (solid == null || solid.IsErased) return TextureDirection.AlongLength;

            try
            {
                var clone = (Solid3d)solid.Clone();
                try
                {
                    var alignResult = BzbjAlignmentService.AlignToWorldXY(clone);
                    if (!alignResult.Success) return TextureDirection.AlongLength;

                    var ext = clone.GeometricExtents;
                    double dx = ext.MaxPoint.X - ext.MinPoint.X;
                    double dy = ext.MaxPoint.Y - ext.MinPoint.Y;
                    double dz = ext.MaxPoint.Z - ext.MinPoint.Z;

                    double lenLong, lenShort;
                    if (dz <= dy && dz <= dx)
                    {
                        lenLong = Math.Max(dx, dy);
                        lenShort = Math.Min(dx, dy);
                    }
                    else if (dy <= dx && dy <= dz)
                    {
                        lenLong = Math.Max(dx, dz);
                        lenShort = Math.Min(dx, dz);
                    }
                    else
                    {
                        lenLong = Math.Max(dy, dz);
                        lenShort = Math.Min(dy, dz);
                    }

                    double ratio = lenShort / (lenLong > 0 ? lenLong : 1);
                    if (ratio < 0.12)
                        return TextureDirection.AlongLength;

                    return lenLong > lenShort ? TextureDirection.AlongLength : TextureDirection.AlongWidth;
                }
                finally
                {
                    clone.Dispose();
                }
            }
            catch
            {
                return TextureDirection.AlongLength;
            }
        }

        /// <summary>
        /// 切换板件的纹路方向（同时交换长宽）
        /// </summary>
        public static void ToggleTextureDirection(PanelInfo panelInfo)
        {
            if (panelInfo == null) return;

            double oldLength = panelInfo.ExtraLength;
            double oldWidth = panelInfo.ExtraWidth;

            panelInfo.ExtraLength = oldWidth;
            panelInfo.ExtraWidth = oldLength;

            string oldEdgeLeft = panelInfo.EdgeLeft;
            string oldEdgeRight = panelInfo.EdgeRight;
            string oldEdgeTop = panelInfo.EdgeTop;
            string oldEdgeBottom = panelInfo.EdgeBottom;

            panelInfo.EdgeTop = oldEdgeLeft;
            panelInfo.EdgeBottom = oldEdgeRight;
            panelInfo.EdgeLeft = oldEdgeTop;
            panelInfo.EdgeRight = oldEdgeBottom;

            panelInfo.TextureDirection = panelInfo.TextureDirection == TextureDirection.AlongLength
                ? TextureDirection.AlongWidth
                : TextureDirection.AlongLength;
        }

        public static string GetTextureDisplayName(TextureDirection direction, double length, double width)
        {
            return direction switch
            {
                TextureDirection.AlongLength => $"竖纹(沿{length:F0}长边)",
                TextureDirection.AlongWidth => $"横纹(沿{width:F0}宽边)",
                _ => "未知"
            };
        }

        public static string GetDisplayDimensions(PanelInfo panelInfo)
        {
            if (panelInfo == null) return "";

            string dir = panelInfo.TextureDirection == TextureDirection.AlongLength ? "竖纹" : "横纹";
            return $"长{panelInfo.ExtraLength:F0}×宽{panelInfo.ExtraWidth:F0}×厚{panelInfo.Height:F0} [{dir}]";
        }

        /// <summary>
        /// 在板件两个大面外偏 2mm 绘制箭头纹路（双面显示）。
        /// 图元放到锁定图层上，避免误编辑。
        /// </summary>
        public static int DrawTextureLines(Solid3d solid, PanelInfo panelInfo, Transaction tr, BlockTableRecord modelSpace, string panelHandle = "")
        {
            if (solid == null || panelInfo == null || tr == null || modelSpace == null)
                return 0;

            int created = 0;

            try
            {
                if (!TryGetDisplayFrame(solid, out var frame))
                    return 0;

                if (frame.PlaneWidth <= 1 || frame.PlaneHeight <= 1)
                    return 0;

                EnsureTextureLayer(modelSpace.Database, tr, true);
                EnsureRegAppRegistered(modelSpace.Database, tr);

                Point3d reverseOrigin = frame.ReverseOrigin;

                created += DrawArrowsOnFace(
                    frame.FrontOrigin, frame.LongAxis, frame.ShortAxis, frame.PlaneWidth, frame.PlaneHeight,
                    panelInfo.TextureDirection, modelSpace, tr, panelHandle);

                created += DrawArrowsOnFace(
                    reverseOrigin, frame.LongAxis, frame.ShortAxis, frame.PlaneWidth, frame.PlaneHeight,
                    panelInfo.TextureDirection, modelSpace, tr, panelHandle);
            }
            catch
            {
                return created;
            }

            return created;
        }

        private static int DrawArrowsOnFace(
            Point3d planeOrigin, Vector3d uAxis, Vector3d vAxis,
            double planeWidth, double planeHeight,
            TextureDirection textureDirection,
            BlockTableRecord modelSpace, Transaction tr, string panelHandle)
        {
            int created = 0;

            Vector3d grainAxis = textureDirection == TextureDirection.AlongLength ? uAxis : vAxis;
            Vector3d crossAxis = textureDirection == TextureDirection.AlongLength ? vAxis : uAxis;

            double grainLength = textureDirection == TextureDirection.AlongLength ? planeWidth : planeHeight;
            double crossLength = textureDirection == TextureDirection.AlongLength ? planeHeight : planeWidth;

            double arrowSpacing = Clamp(crossLength / 6.0, 60.0, 180.0);
            double arrowLength = Clamp(grainLength * 0.32, 80.0, 260.0);
            double headLength = Clamp(arrowLength * 0.18, 12.0, 28.0);
            double headWidth = Clamp(headLength * 0.7, 8.0, 20.0);
            double shaftGap = Clamp(headLength * 0.5, 6.0, 14.0);
            int arrowCount = Math.Max(1, (int)Math.Floor(crossLength / arrowSpacing));
            double startOffset = (crossLength - (arrowCount - 1) * arrowSpacing) / 2.0;

            for (int i = 0; i < arrowCount; i++)
            {
                double offset = startOffset + i * arrowSpacing;
                Point3d crossCenter = planeOrigin + crossAxis.MultiplyBy(offset);

                double grainStart = Math.Max((grainLength - arrowLength * 2.0 - shaftGap) / 2.0, 10.0);
                Point3d shaftStart = crossCenter + grainAxis.MultiplyBy(grainStart);
                Point3d shaftMid = shaftStart + grainAxis.MultiplyBy(arrowLength);
                Point3d shaftEnd = shaftMid + grainAxis.MultiplyBy(arrowLength + shaftGap);
                Point3d headBase1 = shaftMid - grainAxis.MultiplyBy(headLength);
                Point3d headBase2 = shaftEnd - grainAxis.MultiplyBy(headLength);
                Vector3d headOffset = crossAxis.MultiplyBy(headWidth / 2.0);

                created += AppendTextureLine(modelSpace, tr, shaftStart, shaftMid, panelHandle);
                created += AppendTextureLine(modelSpace, tr, shaftMid, headBase1 + headOffset, panelHandle);
                created += AppendTextureLine(modelSpace, tr, shaftMid, headBase1 - headOffset, panelHandle);
                created += AppendTextureLine(modelSpace, tr, shaftMid, shaftEnd, panelHandle);
                created += AppendTextureLine(modelSpace, tr, shaftEnd, headBase2 + headOffset, panelHandle);
                created += AppendTextureLine(modelSpace, tr, shaftEnd, headBase2 - headOffset, panelHandle);
            }

            return created;
        }

        private sealed class DisplayFrame
        {
            public Point3d FrontOrigin { get; set; }
            public Point3d ReverseOrigin { get; set; }
            public Vector3d LongAxis { get; set; }
            public Vector3d ShortAxis { get; set; }
            public double PlaneWidth { get; set; }
            public double PlaneHeight { get; set; }
        }

        private static bool TryGetDisplayFrame(Solid3d solid, out DisplayFrame frame)
        {
            frame = null;
            var vertices = BzbjAlignmentService.CollectVertices(solid);
            if (!TryGetOrientedBoxFrame(
                    vertices,
                    out var axisX,
                    out var axisY,
                    out var axisZ,
                    out var minX,
                    out var maxX,
                    out var minY,
                    out var maxY,
                    out var minZ,
                    out var maxZ))
            {
                return TryGetDisplayFrameFromExtents(solid, out frame);
            }

            double lenX = maxX - minX;
            double lenY = maxY - minY;
            double lenZ = maxZ - minZ;
            if (lenX <= 0.01 || lenY <= 0.01 || lenZ <= 0.01)
                return false;

            var axes = new[]
            {
                new AxisRange(axisX, minX, maxX),
                new AxisRange(axisY, minY, maxY),
                new AxisRange(axisZ, minZ, maxZ)
            }
            .OrderBy(a => a.Length)
            .ToArray();

            var thicknessAxis = axes[0];
            var faceAxisA = axes[1];
            var faceAxisB = axes[2];

            var longAxis = faceAxisA.Length >= faceAxisB.Length ? faceAxisA : faceAxisB;
            var shortAxis = faceAxisA.Length >= faceAxisB.Length ? faceAxisB : faceAxisA;

            Vector3d normal = longAxis.Axis.CrossProduct(shortAxis.Axis);
            if (normal.Length <= 1e-6)
                return TryGetDisplayFrameFromExtents(solid, out frame);

            normal = normal.GetNormal();
            if (normal.DotProduct(thicknessAxis.Axis) < 0)
                normal = normal.Negate();

            // 保持与面法向一致的厚度轴，确保正反面 origin 位于板件外侧。
            var thicknessVector = thicknessAxis.Axis.GetNormal();
            if (thicknessVector.DotProduct(normal) < 0)
                thicknessVector = thicknessVector.Negate();

            const double offset = 2.0;
            Point3d frontOrigin = ComposePoint(
                longAxis.Axis, longAxis.Min,
                shortAxis.Axis, shortAxis.Min,
                thicknessVector, thicknessAxis.Max + offset);
            Point3d reverseOrigin = ComposePoint(
                longAxis.Axis, longAxis.Min,
                shortAxis.Axis, shortAxis.Min,
                thicknessVector, thicknessAxis.Min - offset);

            frame = new DisplayFrame
            {
                FrontOrigin = frontOrigin,
                ReverseOrigin = reverseOrigin,
                LongAxis = longAxis.Axis.GetNormal(),
                ShortAxis = shortAxis.Axis.GetNormal(),
                PlaneWidth = longAxis.Length,
                PlaneHeight = shortAxis.Length
            };
            return true;
        }

        private static bool TryGetDisplayFrameFromExtents(Solid3d solid, out DisplayFrame frame)
        {
            frame = null;
            try
            {
                var extents = solid.GeometricExtents;
                double minX = extents.MinPoint.X;
                double maxX = extents.MaxPoint.X;
                double minY = extents.MinPoint.Y;
                double maxY = extents.MaxPoint.Y;
                double minZ = extents.MinPoint.Z;
                double maxZ = extents.MaxPoint.Z;

                double lenX = Math.Abs(maxX - minX);
                double lenY = Math.Abs(maxY - minY);
                double lenZ = Math.Abs(maxZ - minZ);
                if (lenX <= 0.01 || lenY <= 0.01 || lenZ <= 0.01)
                    return false;

                Vector3d longAxis;
                Vector3d shortAxis;
                Point3d frontOrigin;
                Point3d reverseOrigin;
                double planeWidth;
                double planeHeight;
                const double offset = 2.0;

                if (lenZ <= lenX && lenZ <= lenY)
                {
                    longAxis = lenX >= lenY ? Vector3d.XAxis : Vector3d.YAxis;
                    shortAxis = lenX >= lenY ? Vector3d.YAxis : Vector3d.XAxis;
                    planeWidth = Math.Max(lenX, lenY);
                    planeHeight = Math.Min(lenX, lenY);
                    frontOrigin = new Point3d(minX, minY, maxZ + offset);
                    reverseOrigin = new Point3d(minX, minY, minZ - offset);
                }
                else if (lenY <= lenX && lenY <= lenZ)
                {
                    longAxis = lenX >= lenZ ? Vector3d.XAxis : Vector3d.ZAxis;
                    shortAxis = lenX >= lenZ ? Vector3d.ZAxis : Vector3d.XAxis;
                    planeWidth = Math.Max(lenX, lenZ);
                    planeHeight = Math.Min(lenX, lenZ);
                    frontOrigin = new Point3d(minX, maxY + offset, minZ);
                    reverseOrigin = new Point3d(minX, minY - offset, minZ);
                }
                else
                {
                    longAxis = lenY >= lenZ ? Vector3d.YAxis : Vector3d.ZAxis;
                    shortAxis = lenY >= lenZ ? Vector3d.ZAxis : Vector3d.YAxis;
                    planeWidth = Math.Max(lenY, lenZ);
                    planeHeight = Math.Min(lenY, lenZ);
                    frontOrigin = new Point3d(maxX + offset, minY, minZ);
                    reverseOrigin = new Point3d(minX - offset, minY, minZ);
                }

                frame = new DisplayFrame
                {
                    FrontOrigin = frontOrigin,
                    ReverseOrigin = reverseOrigin,
                    LongAxis = longAxis,
                    ShortAxis = shortAxis,
                    PlaneWidth = planeWidth,
                    PlaneHeight = planeHeight
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int AppendTextureLine(BlockTableRecord modelSpace, Transaction tr, Point3d start, Point3d end, string panelHandle)
        {
            if (start.DistanceTo(end) <= 0.01)
                return 0;

            var line = new Line(start, end)
            {
                Layer = TextureLayerName,
                ColorIndex = 1,
                LineWeight = LineWeight.LineWeight030,
                LinetypeScale = 0.75
            };

            modelSpace.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);

            line.Visible = true;

            if (!string.IsNullOrWhiteSpace(panelHandle))
            {
                line.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, TextureRegAppName),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, TextureMarker),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, panelHandle));
            }

            SetLineUnselectable(line, tr);

            return 1;
        }

        private static void SetLineUnselectable(Line line, Transaction tr)
        {
            if (line == null || tr == null) return;
            try
            {
                var layerTable = (LayerTable)tr.GetObject(line.Database.LayerTableId, OpenMode.ForWrite);
                if (layerTable.Has(TextureLayerName))
                {
                    var layer = (LayerTableRecord)tr.GetObject(layerTable[TextureLayerName], OpenMode.ForWrite);
                    layer.IsLocked = true;
                }
            }
            catch { }
        }

        public static int RefreshTextureLinesForPanel(Database db, Transaction tr, Solid3d solid, PanelInfo panelInfo, string panelHandle)
        {
            if (db == null || tr == null || solid == null || panelInfo == null)
            {
                return 0;
            }

            RemoveTextureLinesForPanel(db, tr, panelHandle);
            EnsureTextureLayer(db, tr, true);

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            return DrawTextureLines(solid, panelInfo, tr, modelSpace, panelHandle);
        }

        public static void RemoveTextureLinesForPanel(Database db, Transaction tr, string panelHandle)
        {
            if (db == null || tr == null || string.IsNullOrWhiteSpace(panelHandle))
            {
                return;
            }

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            UnlockTextureLayer(db, tr);

            foreach (ObjectId id in modelSpace.Cast<ObjectId>().ToList())
            {
                if (id.IsNull)
                {
                    continue;
                }

                var line = tr.GetObject(id, OpenMode.ForWrite, false) as Line;
                if (line == null || line.IsErased || line.Layer != TextureLayerName)
                {
                    continue;
                }

                if (TryReadTexturePanelHandle(line, out string existingHandle) &&
                    string.Equals(existingHandle, panelHandle, StringComparison.OrdinalIgnoreCase))
                {
                    line.Erase();
                }
            }
        }

        public static void SetTextureLayerVisibility(Database db, Transaction tr, bool visible)
        {
            EnsureTextureLayer(db, tr, visible);
        }

        private static void UnlockTextureLayer(Database db, Transaction tr)
        {
            if (db == null || tr == null) return;
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
            if (!layerTable.Has(TextureLayerName)) return;
            var layer = (LayerTableRecord)tr.GetObject(layerTable[TextureLayerName], OpenMode.ForWrite);
            layer.IsLocked = false;
        }

        private static void EnsureTextureLayer(Database db, Transaction tr, bool visible)
        {
            if (db == null || tr == null)
            {
                return;
            }

            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            LayerTableRecord layerRecord;
            if (!layerTable.Has(TextureLayerName))
            {
                layerTable.UpgradeOpen();
                layerRecord = new LayerTableRecord
                {
                    Name = TextureLayerName
                };
                layerTable.Add(layerRecord);
                tr.AddNewlyCreatedDBObject(layerRecord, true);
            }
            else
            {
                layerRecord = (LayerTableRecord)tr.GetObject(layerTable[TextureLayerName], OpenMode.ForWrite);
            }

            layerRecord.IsOff = !visible;
            layerRecord.IsFrozen = false;
            layerRecord.IsLocked = false;
        }

        private static bool TryReadTexturePanelHandle(Entity entity, out string panelHandle)
        {
            panelHandle = "";
            if (entity?.XData == null)
            {
                return false;
            }

            var array = entity.XData.AsArray();
            if (array == null || array.Length < 3)
            {
                return false;
            }

            if (!string.Equals(array[0].Value?.ToString(), TextureRegAppName, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(array[1].Value?.ToString(), TextureMarker, StringComparison.Ordinal))
            {
                return false;
            }

            panelHandle = array[2].Value?.ToString() ?? "";
            return !string.IsNullOrWhiteSpace(panelHandle);
        }

        private static void EnsureRegAppRegistered(Database db, Transaction tr)
        {
            if (db == null || tr == null)
            {
                return;
            }

            var regAppTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (regAppTable.Has(TextureRegAppName))
            {
                return;
            }

            regAppTable.UpgradeOpen();
            var regAppRecord = new RegAppTableRecord
            {
                Name = TextureRegAppName
            };
            regAppTable.Add(regAppRecord);
            tr.AddNewlyCreatedDBObject(regAppRecord, true);
        }

        private sealed class AxisRange
        {
            public AxisRange(Vector3d axis, double min, double max)
            {
                Axis = axis;
                Min = min;
                Max = max;
            }

            public Vector3d Axis { get; }
            public double Min { get; }
            public double Max { get; }
            public double Length => Max - Min;
        }

        private static Point3d ComposePoint(
            Vector3d axisA, double distanceA,
            Vector3d axisB, double distanceB,
            Vector3d axisC, double distanceC)
        {
            Vector3d vector =
                axisA.GetNormal().MultiplyBy(distanceA) +
                axisB.GetNormal().MultiplyBy(distanceB) +
                axisC.GetNormal().MultiplyBy(distanceC);
            return Point3d.Origin + vector;
        }

        private static bool TryGetOrientedBoxFrame(
            List<Point3d> vertices,
            out Vector3d axisX,
            out Vector3d axisY,
            out Vector3d axisZ,
            out double minX,
            out double maxX,
            out double minY,
            out double maxY,
            out double minZ,
            out double maxZ)
        {
            axisX = Vector3d.XAxis;
            axisY = Vector3d.YAxis;
            axisZ = Vector3d.ZAxis;
            minX = maxX = minY = maxY = minZ = maxZ = 0;

            if (vertices == null || vertices.Count < 4)
                return false;

            const double epsilon = 1e-6;
            var distances = new List<double>(vertices.Count * vertices.Count);
            for (int i = 0; i < vertices.Count; i++)
            {
                for (int j = i + 1; j < vertices.Count; j++)
                {
                    double distance = vertices[i].DistanceTo(vertices[j]);
                    if (distance > epsilon)
                        distances.Add(distance);
                }
            }

            if (distances.Count == 0)
                return false;

            distances.Sort();
            var uniqueDistances = new List<double>(3);
            foreach (double distance in distances)
            {
                double tolerance = Math.Max(0.001, distance * 0.0001);
                if (uniqueDistances.Count == 0 ||
                    Math.Abs(distance - uniqueDistances[uniqueDistances.Count - 1]) > tolerance)
                {
                    uniqueDistances.Add(distance);
                    if (uniqueDistances.Count == 3)
                        break;
                }
            }

            if (uniqueDistances.Count < 2)
                return false;

            var candidates = new List<Vector3d>(64);
            foreach (double distance in uniqueDistances)
            {
                double tolerance = Math.Max(0.01, distance * 0.001);
                for (int i = 0; i < vertices.Count; i++)
                {
                    for (int j = i + 1; j < vertices.Count; j++)
                    {
                        if (Math.Abs(vertices[i].DistanceTo(vertices[j]) - distance) > tolerance)
                            continue;

                        Vector3d direction = vertices[j] - vertices[i];
                        if (direction.Length > epsilon)
                            candidates.Add(direction.GetNormal());
                    }
                }
            }

            if (candidates.Count == 0)
                return false;

            var uniqueDirs = new List<Vector3d>(3);
            foreach (var candidate in candidates)
            {
                Vector3d normal = candidate.GetNormal();
                bool duplicated = uniqueDirs.Any(dir => Math.Abs(normal.DotProduct(dir)) > 0.99);
                if (duplicated)
                    continue;

                uniqueDirs.Add(normal);
                if (uniqueDirs.Count == 3)
                    break;
            }

            if (uniqueDirs.Count < 2)
                return false;

            axisX = uniqueDirs[0].GetNormal();

            int bestIndex = -1;
            double bestDot = double.MaxValue;
            for (int i = 1; i < uniqueDirs.Count; i++)
            {
                double dot = Math.Abs(axisX.DotProduct(uniqueDirs[i]));
                if (dot < bestDot)
                {
                    bestDot = dot;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                return false;

            axisY = uniqueDirs[bestIndex].GetNormal();
            axisZ = axisX.CrossProduct(axisY);
            if (axisZ.Length <= epsilon)
                return false;

            axisZ = axisZ.GetNormal();
            axisY = axisZ.CrossProduct(axisX).GetNormal();

            if (uniqueDirs.Count >= 3)
            {
                Vector3d normal = uniqueDirs[2].GetNormal();
                if (axisZ.DotProduct(normal) < 0)
                {
                    axisZ = axisZ.Negate();
                    axisY = axisZ.CrossProduct(axisX).GetNormal();
                }
            }

            bool first = true;
            foreach (var vertex in vertices)
            {
                Vector3d vector = vertex.GetAsVector();
                double x = vector.DotProduct(axisX);
                double y = vector.DotProduct(axisY);
                double z = vector.DotProduct(axisZ);

                if (first)
                {
                    minX = maxX = x;
                    minY = maxY = y;
                    minZ = maxZ = z;
                    first = false;
                    continue;
                }

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }

            return !first;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
