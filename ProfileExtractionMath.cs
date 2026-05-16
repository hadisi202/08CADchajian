using System;
using System.Collections.Generic;
using System.Linq;

namespace FurniturePlugin;

/// <summary>
/// 异形板件投影轮廓提取的纯算法工具集（不依赖AutoCAD），可被单元测试。
/// 用于支撑 PAIBAN/BZBJ + FLATSHOT 流程：把投影后的散乱边段链接为
/// 闭合多段线，识别外轮廓 + 内孔，并验证封闭性、自交性、绕向。
/// </summary>
public static class ProfileExtractionMath
{
    public readonly struct PtXY : IEquatable<PtXY>
    {
        public double X { get; }
        public double Y { get; }
        public PtXY(double x, double y) { X = x; Y = y; }
        public bool Equals(PtXY other) =>
            Math.Abs(X - other.X) < 1e-9 && Math.Abs(Y - other.Y) < 1e-9;
        public override bool Equals(object? obj) => obj is PtXY p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(Math.Round(X, 4), Math.Round(Y, 4));
    }

    public sealed class Edge
    {
        public PtXY Start { get; set; }
        public PtXY End { get; set; }
        /// <summary>是否参与了链接（避免重复使用）</summary>
        public bool Used { get; set; }
    }

    public sealed class ExtractionReport
    {
        public List<List<PtXY>> ClosedLoops { get; } = new();
        public List<PtXY>? OuterLoop { get; set; }
        public List<List<PtXY>> InnerLoops { get; } = new();
        public int TotalEdges { get; set; }
        public int ChainedEdges { get; set; }
        public int OpenChainCount { get; set; }
        public bool OuterIsClosed { get; set; }
        public bool OuterIsSimple { get; set; }
        public string Reason { get; set; } = "";

        public bool Success =>
            OuterLoop != null && OuterLoop.Count >= 3 && OuterIsClosed && OuterIsSimple;

        public double SuccessRate =>
            TotalEdges == 0 ? 1.0 : (double)ChainedEdges / TotalEdges;
    }

    /// <summary>
    /// 把一组无序2D边段链接成若干闭合环；按面积降序，最大的为外轮廓，其余落在外轮廓内的为内孔。
    /// 返回的环都已 Dedup + 强制绕向：外CCW、内CW。
    /// </summary>
    /// <param name="edges">所有2D边段（未排序、未配对）</param>
    /// <param name="tol">端点合并容差（mm，建议0.01~1.0）</param>
    public static ExtractionReport ChainEdgesToProfile(IReadOnlyList<Edge> edges, double tol = 0.05)
    {
        var report = new ExtractionReport { TotalEdges = edges?.Count ?? 0 };
        if (edges == null || edges.Count == 0)
        {
            report.Reason = "无可用边段";
            return report;
        }

        var working = edges
            .Where(e => Distance(e.Start, e.End) > tol)
            .Select(e => new Edge { Start = e.Start, End = e.End })
            .ToList();
        if (working.Count == 0)
        {
            report.Reason = "全部边段过短";
            return report;
        }

        // 1. 端点桶化：bucket 边长 = 2*tol，确保 tol 内的点最多跨 1 个桶（用 3×3 邻居查询兜底）
        double q = Math.Max(tol * 2, 1e-6);
        var buckets = new Dictionary<long, List<int>>();
        for (int i = 0; i < working.Count; i++)
        {
            AddToBucket(buckets, HashKey(working[i].Start, q), i);
            AddToBucket(buckets, HashKey(working[i].End, q), i);
        }

        // 2. 贪心串联
        for (int i = 0; i < working.Count; i++)
        {
            if (working[i].Used) continue;

            var loop = new List<PtXY> { working[i].Start, working[i].End };
            working[i].Used = true;
            report.ChainedEdges++;

            PtXY tail = working[i].End;
            PtXY prev = working[i].Start;
            bool extended = true;
            int safetyCounter = working.Count + 4;
            while (extended && safetyCounter-- > 0)
            {
                extended = false;

                // 当前切向（用于在多个候选边中选"最连续"的一条，避免错走入旁支）
                double curDx = tail.X - prev.X;
                double curDy = tail.Y - prev.Y;
                double curLen = Math.Sqrt(curDx * curDx + curDy * curDy);

                Edge? bestEdge = null;
                int bestIdx = -1;
                double bestScore = double.MaxValue;
                bool reverse = false;
                foreach (long key in NeighborKeys(tail, q))
                {
                    if (!buckets.TryGetValue(key, out var indices)) continue;
                    foreach (int idx in indices)
                    {
                        var e = working[idx];
                        if (e.Used) continue;
                        double dStart = Distance(e.Start, tail);
                        double dEnd = Distance(e.End, tail);
                        if (dStart <= tol)
                        {
                            double score = ChainScore(dStart, curDx, curDy, curLen,
                                e.End.X - e.Start.X, e.End.Y - e.Start.Y);
                            if (score < bestScore) { bestScore = score; bestEdge = e; bestIdx = idx; reverse = false; }
                        }
                        if (dEnd <= tol)
                        {
                            double score = ChainScore(dEnd, curDx, curDy, curLen,
                                e.Start.X - e.End.X, e.Start.Y - e.End.Y);
                            if (score < bestScore) { bestScore = score; bestEdge = e; bestIdx = idx; reverse = true; }
                        }
                    }
                }
                if (bestEdge == null) break;

                bestEdge.Used = true;
                report.ChainedEdges++;
                prev = tail;
                tail = reverse ? bestEdge.Start : bestEdge.End;
                loop.Add(tail);

                // 检查闭合
                if (Distance(tail, loop[0]) <= tol && loop.Count >= 4)
                {
                    loop.RemoveAt(loop.Count - 1);
                    var dedup = DedupConsecutive(loop, tol);
                    if (dedup.Count >= 3) report.ClosedLoops.Add(dedup);
                    extended = false;
                    break;
                }
                extended = true;
            }

            if (Distance(tail, loop[0]) > tol)
            {
                report.OpenChainCount++;
            }
        }

        if (report.ClosedLoops.Count == 0)
        {
            report.Reason = "未能链接出任何闭合环";
            return report;
        }

        // 3. 选最大面积为外轮廓
        var withArea = report.ClosedLoops
            .Select(l => (Loop: l, Area: Math.Abs(SignedArea(l))))
            .OrderByDescending(x => x.Area)
            .ToList();

        report.OuterLoop = EnsureWindingCcw(withArea[0].Loop);
        report.OuterIsClosed = true;
        report.OuterIsSimple = !HasSelfIntersection(report.OuterLoop);

        for (int i = 1; i < withArea.Count; i++)
        {
            var inner = withArea[i].Loop;
            if (inner.Count < 3) continue;
            if (Math.Abs(SignedArea(inner)) < 0.5) continue;
            // 必须严格在外轮廓内部
            if (!IsLoopInsideLoop(inner, report.OuterLoop, tol)) continue;
            report.InnerLoops.Add(EnsureWindingCw(inner));
        }

        return report;
    }

    /// <summary>判断点是否在多边形内（射线法）</summary>
    public static bool PointInPolygon(PtXY p, IReadOnlyList<PtXY> poly)
    {
        if (poly == null || poly.Count < 3) return false;
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            double yi = poly[i].Y, yj = poly[j].Y;
            double xi = poly[i].X, xj = poly[j].X;
            bool intersect = ((yi > p.Y) != (yj > p.Y)) &&
                (p.X < (xj - xi) * (p.Y - yi) / (yj - yi + 1e-18) + xi);
            if (intersect) inside = !inside;
        }
        return inside;
    }

    public static double SignedArea(IReadOnlyList<PtXY> poly)
    {
        if (poly == null || poly.Count < 3) return 0;
        double a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            a += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
        return a * 0.5;
    }

    /// <summary>
    /// 自交检测：O(n²) 段对段相交判断，足以应对≤2k点的板件外轮廓
    /// </summary>
    public static bool HasSelfIntersection(IReadOnlyList<PtXY> loop)
    {
        if (loop == null || loop.Count < 4) return false;
        int n = loop.Count;
        for (int i = 0; i < n; i++)
        {
            var a1 = loop[i];
            var a2 = loop[(i + 1) % n];
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue; // 相邻段
                var b1 = loop[j];
                var b2 = loop[(j + 1) % n];
                if (SegmentsIntersect(a1, a2, b1, b2)) return true;
            }
        }
        return false;
    }

    public static List<PtXY> EnsureWindingCcw(List<PtXY> loop)
    {
        if (loop == null || loop.Count < 3) return loop ?? new List<PtXY>();
        if (SignedArea(loop) < 0) loop.Reverse();
        return loop;
    }

    public static List<PtXY> EnsureWindingCw(List<PtXY> loop)
    {
        if (loop == null || loop.Count < 3) return loop ?? new List<PtXY>();
        if (SignedArea(loop) > 0) loop.Reverse();
        return loop;
    }

    public static bool IsLoopInsideLoop(IReadOnlyList<PtXY> inner, IReadOnlyList<PtXY> outer, double tol)
    {
        if (inner == null || outer == null || inner.Count < 3 || outer.Count < 3) return false;
        // 取内环若干样本点，全部在外环内部即视为包含
        int sampleCount = Math.Min(8, inner.Count);
        int hits = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            int idx = (i * inner.Count) / sampleCount;
            if (PointInPolygon(inner[idx], outer)) hits++;
        }
        return hits >= sampleCount - 1;
    }

    /// <summary>验证闭合：首尾距离 ≤ tol，且最少有3个不同顶点</summary>
    public static bool IsClosed(IReadOnlyList<PtXY> loop, double tol = 0.05)
    {
        if (loop == null || loop.Count < 3) return false;
        return Distance(loop[0], loop[^1]) <= tol;
    }

    public static List<PtXY> DedupConsecutive(IReadOnlyList<PtXY> pts, double tol)
    {
        var r = new List<PtXY>();
        foreach (var p in pts)
        {
            if (r.Count == 0 || Distance(r[^1], p) > tol) r.Add(p);
        }
        // 去除首尾重复
        if (r.Count > 1 && Distance(r[0], r[^1]) <= tol) r.RemoveAt(r.Count - 1);
        return r;
    }

    public static double Distance(PtXY a, PtXY b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool SegmentsIntersect(PtXY p1, PtXY p2, PtXY p3, PtXY p4)
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

    /// <summary>
    /// 链接评分 = 端点距离 + 切向不连续惩罚。
    /// 用于在多个 tol 范围内的候选边中挑出"最自然延续"的那条，
    /// 避免在外环边附近遇到内孔顶点时误转入孔的边线。
    /// </summary>
    private static double ChainScore(double endpointDist,
        double curDx, double curDy, double curLen,
        double nextDx, double nextDy)
    {
        // 没有"前向方向"时(链刚开始)，直接按端点距离
        if (curLen < 1e-9) return endpointDist;
        double nextLen = Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
        if (nextLen < 1e-9) return endpointDist;
        // cosθ：1=同向，0=垂直，-1=反向
        double cos = (curDx * nextDx + curDy * nextDy) / (curLen * nextLen);
        cos = Math.Max(-1.0, Math.Min(1.0, cos));
        // 惩罚：偏离同向越大，惩罚越重；折角 90° 给 1.0 权重，180° 给 2.0 权重
        double penalty = (1.0 - cos) * 0.5; // 0~1
        // 端点距离仍是主因子；切向只在距离接近时起决定作用
        return endpointDist + penalty * 0.5;
    }

    private static long HashKey(PtXY p, double q)
    {
        long ix = (long)Math.Floor(p.X / q);
        long iy = (long)Math.Floor(p.Y / q);
        return CellKey(ix, iy);
    }

    private static long CellKey(long ix, long iy)
        => (ix * 73856093L) ^ (iy * 19349663L);

    private static IEnumerable<long> NeighborKeys(PtXY p, double q)
    {
        long ix = (long)Math.Floor(p.X / q);
        long iy = (long)Math.Floor(p.Y / q);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                yield return CellKey(ix + dx, iy + dy);
    }

    private static void AddToBucket(Dictionary<long, List<int>> buckets, long key, int idx)
    {
        if (!buckets.TryGetValue(key, out var list))
        {
            list = new List<int>(2);
            buckets[key] = list;
        }
        list.Add(idx);
    }
}
