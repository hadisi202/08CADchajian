using System;
using System.Collections.Generic;
using System.Linq;

namespace FurniturePlugin;

/// <summary>
/// 异形板件排版的纯算法工具：带内孔的多边形快速碰撞检测、利用率/板件间距计算。
/// 不依赖AutoCAD，可单元测试。
/// </summary>
public static class IrregularNestingMath
{
    public sealed class PolygonWithHoles
    {
        public List<ProfileExtractionMath.PtXY> Outer { get; set; } = new();
        public List<List<ProfileExtractionMath.PtXY>> Holes { get; set; } = new();

        public double NetArea
        {
            get
            {
                double a = Math.Abs(ProfileExtractionMath.SignedArea(Outer));
                if (Holes != null)
                    foreach (var h in Holes)
                        a -= Math.Abs(ProfileExtractionMath.SignedArea(h));
                return Math.Max(0, a);
            }
        }

        public (double minX, double minY, double maxX, double maxY) BBox
        {
            get
            {
                if (Outer == null || Outer.Count == 0) return (0, 0, 0, 0);
                double mnX = Outer.Min(p => p.X), mxX = Outer.Max(p => p.X);
                double mnY = Outer.Min(p => p.Y), mxY = Outer.Max(p => p.Y);
                return (mnX, mnY, mxX, mxY);
            }
        }
    }

    /// <summary>
    /// 带间距的快速碰撞：先 BBox 粗筛，再 SAT 多边形重叠检测。
    /// 间距 spacing>0 时，把 a 的轮廓向外膨胀 spacing/2，b 同样膨胀，再判重叠
    /// （这里用 Minkowski-bbox 近似 + 边到边距离阈值，工程足够用）。
    /// </summary>
    public static bool PolygonsOverlap(
        IReadOnlyList<ProfileExtractionMath.PtXY> a,
        IReadOnlyList<ProfileExtractionMath.PtXY> b,
        double spacing = 0)
    {
        if (a == null || b == null || a.Count < 3 || b.Count < 3) return false;
        var (ax1, ay1, ax2, ay2) = BBox(a);
        var (bx1, by1, bx2, by2) = BBox(b);
        double s = Math.Max(0, spacing);
        if (ax2 + s < bx1 || bx2 + s < ax1) return false;
        if (ay2 + s < by1 || by2 + s < ay1) return false;

        // 顶点互含
        foreach (var p in a)
            if (ProfileExtractionMath.PointInPolygon(p, b)) return true;
        foreach (var p in b)
            if (ProfileExtractionMath.PointInPolygon(p, a)) return true;

        // 边与边相交 / 间距过近
        int na = a.Count, nb = b.Count;
        for (int i = 0; i < na; i++)
        {
            var a1 = a[i];
            var a2 = a[(i + 1) % na];
            for (int j = 0; j < nb; j++)
            {
                var b1 = b[j];
                var b2 = b[(j + 1) % nb];
                if (SegmentsIntersect(a1, a2, b1, b2)) return true;
                if (s > 0)
                {
                    if (SegmentDistance(a1, a2, b1, b2) < s) return true;
                }
            }
        }
        return false;
    }

    /// <summary>带内孔的复合多边形碰撞：外环重叠 且 不能放进对方任一内孔</summary>
    public static bool ShapesOverlap(PolygonWithHoles a, PolygonWithHoles b, double spacing = 0)
    {
        if (a == null || b == null) return false;
        if (!PolygonsOverlap(a.Outer, b.Outer, spacing))
            return false;

        // a 完全落在 b 的某个内孔里 → 不重叠
        if (b.Holes != null)
        {
            foreach (var h in b.Holes)
                if (PolygonInsidePolygon(a.Outer, h)) return false;
        }
        if (a.Holes != null)
        {
            foreach (var h in a.Holes)
                if (PolygonInsidePolygon(b.Outer, h)) return false;
        }
        return true;
    }

    public static bool PolygonInsidePolygon(IReadOnlyList<ProfileExtractionMath.PtXY> inner,
        IReadOnlyList<ProfileExtractionMath.PtXY> outer)
    {
        if (inner == null || outer == null || inner.Count < 3 || outer.Count < 3) return false;
        foreach (var p in inner)
            if (!ProfileExtractionMath.PointInPolygon(p, outer)) return false;

        // 不允许边相交
        int ni = inner.Count, no = outer.Count;
        for (int i = 0; i < ni; i++)
        {
            var i1 = inner[i];
            var i2 = inner[(i + 1) % ni];
            for (int j = 0; j < no; j++)
            {
                var o1 = outer[j];
                var o2 = outer[(j + 1) % no];
                if (SegmentsIntersect(i1, i2, o1, o2)) return false;
            }
        }
        return true;
    }

    /// <summary>计算大板利用率：净排版面积(外面积-内孔面积) / 大板面积</summary>
    public static double ComputeUtilization(double sheetArea, IEnumerable<PolygonWithHoles> placedShapes)
    {
        if (sheetArea <= 0 || placedShapes == null) return 0;
        double used = placedShapes.Where(s => s != null).Sum(s => s.NetArea);
        return Math.Min(1.0, used / sheetArea);
    }

    /// <summary>两个多边形的最小边到边距离（间距）</summary>
    public static double MinDistanceBetweenPolygons(
        IReadOnlyList<ProfileExtractionMath.PtXY> a,
        IReadOnlyList<ProfileExtractionMath.PtXY> b)
    {
        if (a == null || b == null || a.Count < 2 || b.Count < 2) return double.MaxValue;
        double best = double.MaxValue;
        int na = a.Count, nb = b.Count;
        for (int i = 0; i < na; i++)
        {
            var a1 = a[i];
            var a2 = a[(i + 1) % na];
            for (int j = 0; j < nb; j++)
            {
                var b1 = b[j];
                var b2 = b[(j + 1) % nb];
                double d = SegmentDistance(a1, a2, b1, b2);
                if (d < best) best = d;
            }
        }
        return best;
    }

    public static (double minX, double minY, double maxX, double maxY) BBox(
        IReadOnlyList<ProfileExtractionMath.PtXY> poly)
    {
        if (poly == null || poly.Count == 0) return (0, 0, 0, 0);
        double mnX = poly[0].X, mxX = poly[0].X, mnY = poly[0].Y, mxY = poly[0].Y;
        for (int i = 1; i < poly.Count; i++)
        {
            if (poly[i].X < mnX) mnX = poly[i].X;
            if (poly[i].X > mxX) mxX = poly[i].X;
            if (poly[i].Y < mnY) mnY = poly[i].Y;
            if (poly[i].Y > mxY) mxY = poly[i].Y;
        }
        return (mnX, mnY, mxX, mxY);
    }

    private static bool SegmentsIntersect(
        ProfileExtractionMath.PtXY p1, ProfileExtractionMath.PtXY p2,
        ProfileExtractionMath.PtXY p3, ProfileExtractionMath.PtXY p4)
    {
        double d1 = Cross(p4.X - p3.X, p4.Y - p3.Y, p1.X - p3.X, p1.Y - p3.Y);
        double d2 = Cross(p4.X - p3.X, p4.Y - p3.Y, p2.X - p3.X, p2.Y - p3.Y);
        double d3 = Cross(p2.X - p1.X, p2.Y - p1.Y, p3.X - p1.X, p3.Y - p1.Y);
        double d4 = Cross(p2.X - p1.X, p2.Y - p1.Y, p4.X - p1.X, p4.Y - p1.Y);

        if (((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) &&
            ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9)))
            return true;
        return false;
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    /// <summary>线段-线段最短距离</summary>
    private static double SegmentDistance(
        ProfileExtractionMath.PtXY a1, ProfileExtractionMath.PtXY a2,
        ProfileExtractionMath.PtXY b1, ProfileExtractionMath.PtXY b2)
    {
        if (SegmentsIntersect(a1, a2, b1, b2)) return 0;
        double d = PointToSegment(a1, b1, b2);
        d = Math.Min(d, PointToSegment(a2, b1, b2));
        d = Math.Min(d, PointToSegment(b1, a1, a2));
        d = Math.Min(d, PointToSegment(b2, a1, a2));
        return d;
    }

    private static double PointToSegment(
        ProfileExtractionMath.PtXY p,
        ProfileExtractionMath.PtXY a,
        ProfileExtractionMath.PtXY b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-18)
            return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        double cx = a.X + t * dx, cy = a.Y + t * dy;
        double ex = p.X - cx, ey = p.Y - cy;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
