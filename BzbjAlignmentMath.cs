using System;
using System.Collections.Generic;
using System.Linq;

namespace FurniturePlugin;

public static class BzbjAlignmentMath
{
    public readonly struct Pt2
    {
        public double X { get; }
        public double Y { get; }
        public Pt2(double x, double y) { X = x; Y = y; }
    }

    public static bool TryGetLongestEdgeDirection(IReadOnlyList<Pt2> points, out Pt2 direction, out double length)
    {
        direction = new Pt2(1, 0);
        length = 0;
        if (points == null || points.Count < 2) return false;

        var hull = ConvexHull(points);
        if (hull.Count < 2) return false;

        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > length)
            {
                length = len;
                direction = new Pt2(dx / len, dy / len);
            }
        }
        return length > 1e-9;
    }

    /// <summary>
    /// 计算将边方向最小旋转到与X轴平行所需角度（度）。
    /// 返回值范围[-90, 90]，正值=逆时针，负值=顺时针。
    /// </summary>
    public static double ComputeMinimalRotationToXAxisDeg(Pt2 unitDirection)
    {
        double a = Math.Atan2(unitDirection.Y, unitDirection.X) * 180.0 / Math.PI; // [-180,180]
        if (a > 90.0) a -= 180.0;
        if (a <= -90.0) a += 180.0;
        return -a;
    }

    /// <summary>与世界X轴的最小夹角（度，范围[0,90]）</summary>
    public static double ComputeAngleToXAxisDeg(Pt2 unitDirection)
    {
        double a = Math.Abs(Math.Atan2(unitDirection.Y, unitDirection.X) * 180.0 / Math.PI);
        if (a > 90.0) a = 180.0 - a;
        return a;
    }

    private static List<Pt2> ConvexHull(IReadOnlyList<Pt2> points)
    {
        var sorted = points
            .Select(p => new Pt2(Math.Round(p.X, 6), Math.Round(p.Y, 6)))
            .Distinct(new Pt2Comparer())
            .OrderBy(p => p.X).ThenBy(p => p.Y)
            .ToList();
        if (sorted.Count <= 2) return sorted;

        var lower = new List<Pt2>();
        foreach (var p in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }

        var upper = new List<Pt2>();
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            var p = sorted[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double Cross(Pt2 o, Pt2 a, Pt2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private sealed class Pt2Comparer : IEqualityComparer<Pt2>
    {
        public bool Equals(Pt2 a, Pt2 b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;
        public int GetHashCode(Pt2 p) => HashCode.Combine(p.X, p.Y);
    }
}
