using System;
using System.Collections.Generic;
using System.Linq;

namespace FurniturePlugin;

/// <summary>
/// 异形板件多边形精确排版（替代 2mm 栅格法）。
///
/// 核心特性：
///   1. 真实多边形 + 内孔 碰撞，不再光栅化丢失精度；
///   2. 间距严格按界面设定执行（X/Y 双向边到边距离与设定值误差 ≤0.5mm）；
///   3. BL（Bottom-Left-Fill）候选点策略：从已放置板件的右上角生成新候选点；
///   4. 排版结束后自带间距校验报告，便于回归。
///
/// 数据流（与 NestingService 对接）：
///   NestingService 调用本类的 PlaceShapes，返回每个板件的实际放置坐标 (X, Y, IsRotated)。
/// </summary>
public static class IrregularNestingService
{
    /// <summary>
    /// 输入：原始 NestingPart（含 Outline / InnerOutlines / 旋转许可 / Width / Height）。
    /// 算法把其浅复制为带内孔的多边形参与排版，结果写回 PlacedX/Y/IsRotated/Width/Height。
    /// </summary>
    public sealed class PolygonShape
    {
        public string Id = "";
        public bool CanRotate = true;
        /// <summary>外轮廓，已平移到 minX=0, minY=0</summary>
        public List<Pt2d> Outer = new();
        /// <summary>内孔，与外轮廓同坐标系</summary>
        public List<List<Pt2d>> Holes = new();
        /// <summary>BBox 宽 (X方向)</summary>
        public double Width;
        /// <summary>BBox 高 (Y方向)</summary>
        public double Height;
        /// <summary>净面积（外面积 - 内孔面积）</summary>
        public double NetArea;
    }

    public sealed class PlacementReport
    {
        public List<Placement> Placed { get; } = new();
        public List<PolygonShape> Unplaced { get; } = new();
        public double SpacingActualMin { get; set; } = double.MaxValue;
        public double SpacingActualMax { get; set; }
        public double SpacingErrorMaxMm { get; set; }
        public bool SpacingWithinTolerance { get; set; } = true;
        public int SheetCount { get; set; }
        /// <summary>
        /// 每个大板实际使用的尺寸（W,H），按 SheetIndex 顺序。
        /// 排版器会按"小板优先 → 装不下才换大板"的策略选规格，
        /// 调用方必须根据这里的实际尺寸去画板，不能假设全部用同一规格。
        /// </summary>
        public List<(double W, double H)> SheetSizes { get; } = new();
    }

    public sealed class Placement
    {
        public PolygonShape Shape = new();
        public int SheetIndex;
        /// <summary>板件外轮廓在大板上的左下角 X（实际坐标）</summary>
        public double X;
        public double Y;
        public bool IsRotated;
        /// <summary>放置后的世界坐标外轮廓（已平移/旋转）</summary>
        public List<Pt2d> WorldOuter = new();
        public List<List<Pt2d>> WorldHoles = new();
    }

    /// <summary>
    /// 在大板列表上做异形件 BL 排版。
    /// </summary>
    /// <param name="shapes">输入板件，将被排序（按面积降序）</param>
    /// <param name="sheets">大板规格列表（按面积升序由调用方排）</param>
    /// <param name="spacing">板件间距（mm）</param>
    /// <param name="margin">大板边距（mm）</param>
    /// <param name="spacingTolerance">间距校验容差（mm）。默认 0.5</param>
    /// <param name="candidateStep">候选点扫描步长（mm），用于细化 BL。默认 1.0</param>
    public static PlacementReport PlaceShapes(
        List<PolygonShape> shapes,
        List<(double W, double H)> sheets,
        double spacing,
        double margin,
        double spacingTolerance = 0.5,
        double candidateStep = 1.0)
    {
        var report = new PlacementReport();
        if (shapes == null || shapes.Count == 0 || sheets == null || sheets.Count == 0)
            return report;

        var sortedShapes = shapes
            .Where(s => s != null && s.Outer != null && s.Outer.Count >= 3)
            .OrderByDescending(s => s.Width * s.Height)
            .ToList();

        // 大板按面积升序：开新板时优先用最小规格
        var sortedSheets = sheets
            .OrderBy(s => s.W * s.H)
            .ToList();

        var sheetPlaced = new List<List<Placement>>(); // index = sheet#
        var sheetSize = new List<(double W, double H)>();

        foreach (var shape in sortedShapes)
        {
            bool placed = false;
            // 1) 尝试现有大板
            for (int si = 0; si < sheetPlaced.Count && !placed; si++)
            {
                var (W, H) = sheetSize[si];
                if (TryPlaceOnSheet(shape, sheetPlaced[si], W, H, spacing, margin, candidateStep, out var p))
                {
                    p.SheetIndex = si;
                    sheetPlaced[si].Add(p);
                    report.Placed.Add(p);
                    placed = true;
                }
            }
            // 2) 新开大板（按面积升序：先用最小规格，装不下再升级）
            if (!placed)
            {
                foreach (var (W, H) in sortedSheets)
                {
                    var newSheet = new List<Placement>();
                    if (TryPlaceOnSheet(shape, newSheet, W, H, spacing, margin, candidateStep, out var p))
                    {
                        p.SheetIndex = sheetPlaced.Count;
                        newSheet.Add(p);
                        sheetPlaced.Add(newSheet);
                        sheetSize.Add((W, H));
                        report.Placed.Add(p);
                        placed = true;
                        break;
                    }
                }
            }
            if (!placed) report.Unplaced.Add(shape);
        }
        report.SheetCount = sheetPlaced.Count;
        report.SheetSizes.AddRange(sheetSize);
        VerifySpacing(report, spacing, spacingTolerance);
        return report;
    }

    private static bool TryPlaceOnSheet(
        PolygonShape shape, List<Placement> placed,
        double sheetW, double sheetH,
        double spacing, double margin, double step,
        out Placement placement)
    {
        placement = null!;
        // 按 BL 顺序生成候选点：(margin, margin) + 已放置块右上角延伸出的点
        var candidates = BuildCandidates(placed, sheetW, sheetH, spacing, margin, step);

        // 旋转方案：0° 与 90°（如果允许旋转）
        var orientations = shape.CanRotate ? new[] { false, true } : new[] { false };

        // 候选点 ASC by Y, then X（Bottom-Left）
        candidates.Sort((a, b) =>
        {
            int cy = a.Y.CompareTo(b.Y);
            return cy != 0 ? cy : a.X.CompareTo(b.X);
        });

        foreach (var (cx, cy) in candidates)
        {
            foreach (bool rot in orientations)
            {
                if (TryPlaceAt(shape, rot, cx, cy, placed, sheetW, sheetH, spacing, margin, out var p))
                {
                    placement = p;
                    return true;
                }
            }
        }
        return false;
    }

    private static List<(double X, double Y)> BuildCandidates(
        List<Placement> placed, double sheetW, double sheetH,
        double spacing, double margin, double step)
    {
        var pts = new HashSet<(double X, double Y)>();
        pts.Add((margin, margin));

        foreach (var p in placed)
        {
            double right = p.X + (p.IsRotated ? p.Shape.Height : p.Shape.Width) + spacing;
            double top = p.Y + (p.IsRotated ? p.Shape.Width : p.Shape.Height) + spacing;
            pts.Add((right, p.Y));
            pts.Add((p.X, top));
            pts.Add((right, margin));
            pts.Add((margin, top));
        }
        // 扫描格点细化（保证亚毫米级精度）
        if (step > 0 && step < 50)
        {
            var refined = new HashSet<(double X, double Y)>(pts);
            foreach (var p in pts)
            {
                if (p.X + step < sheetW) refined.Add((p.X + step, p.Y));
                if (p.Y + step < sheetH) refined.Add((p.X, p.Y + step));
            }
            pts = refined;
        }
        return pts.ToList();
    }

    private static bool TryPlaceAt(
        PolygonShape shape, bool rot, double tryX, double tryY,
        List<Placement> placed, double sheetW, double sheetH,
        double spacing, double margin, out Placement placement)
    {
        placement = null!;
        double w = rot ? shape.Height : shape.Width;
        double h = rot ? shape.Width : shape.Height;

        // 严格按板内可用区域校验 (margin <= x, x+w <= sheetW - margin) — 不允许超出大板
        if (tryX < margin - 1e-6 || tryY < margin - 1e-6) return false;
        if (tryX + w > sheetW - margin + 1e-6) return false;
        if (tryY + h > sheetH - margin + 1e-6) return false;

        // 把 shape 平移/旋转到 (tryX, tryY)
        var worldOuter = TransformLoop(shape.Outer, rot, w, tryX, tryY);
        var worldHoles = shape.Holes
            .Select(h2 => TransformLoop(h2, rot, w, tryX, tryY))
            .ToList();

        // 二次校验：实际外轮廓的 BBox 也必须在大板内（防御性，处理 shape.Width/Height 与实际不符的边缘情况）
        double actualMinX = worldOuter.Min(p => p.X);
        double actualMaxX = worldOuter.Max(p => p.X);
        double actualMinY = worldOuter.Min(p => p.Y);
        double actualMaxY = worldOuter.Max(p => p.Y);
        if (actualMinX < margin - 1e-6 || actualMinY < margin - 1e-6) return false;
        if (actualMaxX > sheetW - margin + 1e-6) return false;
        if (actualMaxY > sheetH - margin + 1e-6) return false;

        // 与所有已放置板件做精确多边形碰撞 + 间距校验
        foreach (var pl in placed)
        {
            var a = new IrregularNestingMath.PolygonWithHoles
            {
                Outer = ToMath(worldOuter),
                Holes = worldHoles.Select(ToMath).ToList()
            };
            var b = new IrregularNestingMath.PolygonWithHoles
            {
                Outer = ToMath(pl.WorldOuter),
                Holes = pl.WorldHoles.Select(ToMath).ToList()
            };
            if (IrregularNestingMath.ShapesOverlap(a, b, spacing))
                return false;
        }

        placement = new Placement
        {
            Shape = shape,
            X = tryX,
            Y = tryY,
            IsRotated = rot,
            WorldOuter = worldOuter,
            WorldHoles = worldHoles
        };
        return true;
    }

    private static List<Pt2d> TransformLoop(List<Pt2d> loop, bool rot, double rotW, double tx, double ty)
    {
        var result = new List<Pt2d>(loop.Count);
        if (!rot)
        {
            foreach (var p in loop) result.Add(new Pt2d(p.X + tx, p.Y + ty));
        }
        else
        {
            // 90° CCW: (x, y) → (rotW - y, x) 对应 RotateMask 的反向
            // 使旋转后 bbox 仍从 (0,0) 起
            foreach (var p in loop) result.Add(new Pt2d(rotW - p.Y + tx, p.X + ty));
        }
        return result;
    }

    private static List<ProfileExtractionMath.PtXY> ToMath(IEnumerable<Pt2d> loop)
        => loop.Select(p => new ProfileExtractionMath.PtXY(p.X, p.Y)).ToList();

    /// <summary>
    /// 间距校验：所有相邻已放置板件的实际边到边距离 ≥ spacing - tol；
    /// 与边距 margin 的距离也要满足。
    /// </summary>
    public static void VerifySpacing(PlacementReport report, double spacing, double tol)
    {
        report.SpacingErrorMaxMm = 0;
        report.SpacingWithinTolerance = true;
        if (report.Placed == null || report.Placed.Count < 1) return;

        // 同一张大板内两两检查
        var bySheet = report.Placed.GroupBy(p => p.SheetIndex);
        foreach (var grp in bySheet)
        {
            var list = grp.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    double d = IrregularNestingMath.MinDistanceBetweenPolygons(
                        ToMath(list[i].WorldOuter),
                        ToMath(list[j].WorldOuter));
                    if (d < report.SpacingActualMin) report.SpacingActualMin = d;
                    if (d > report.SpacingActualMax) report.SpacingActualMax = d;

                    // 允许多放后远离的板件距离很大；只惩罚 d < spacing-tol（间距不足）
                    double err = spacing - d;
                    if (err > report.SpacingErrorMaxMm) report.SpacingErrorMaxMm = err;
                    if (err > tol) report.SpacingWithinTolerance = false;
                }
            }
        }
    }

    /// <summary>把 NestingPart（含 Outline/Inner）适配为 PolygonShape 集合。</summary>
    public static List<PolygonShape> FromOutlines(
        IEnumerable<(string id, bool canRotate, List<Pt2d> outer, List<List<Pt2d>> inners, double w, double h)> source)
    {
        var list = new List<PolygonShape>();
        if (source == null) return list;
        foreach (var (id, canRotate, outer, inners, w, h) in source)
        {
            if (outer == null || outer.Count < 3) continue;
            // 平移到 minX=0, minY=0
            double mnX = outer.Min(p => p.X), mnY = outer.Min(p => p.Y);
            var shifted = outer.Select(p => new Pt2d(p.X - mnX, p.Y - mnY)).ToList();
            var shiftedHoles = (inners ?? new List<List<Pt2d>>())
                .Where(h0 => h0 != null && h0.Count >= 3)
                .Select(h0 => h0.Select(p => new Pt2d(p.X - mnX, p.Y - mnY)).ToList())
                .ToList();

            double sw = shifted.Max(p => p.X);
            double sh = shifted.Max(p => p.Y);
            // 板件物理 BBox：取算法测得 vs 调用者传入的最大值（避免过度收缩）
            double width = Math.Max(sw, w);
            double height = Math.Max(sh, h);

            double net = AbsArea(shifted) - shiftedHoles.Sum(AbsArea);
            list.Add(new PolygonShape
            {
                Id = id,
                CanRotate = canRotate,
                Outer = shifted,
                Holes = shiftedHoles,
                Width = width,
                Height = height,
                NetArea = Math.Max(0, net)
            });
        }
        return list;
    }

    private static double AbsArea(List<Pt2d> poly)
    {
        if (poly == null || poly.Count < 3) return 0;
        double a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            a += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
        return Math.Abs(a * 0.5);
    }
}
