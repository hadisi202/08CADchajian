using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FurniturePlugin;

/// <summary>
/// OBB 快速计算服务：
/// 1. 优先走「克隆体 + BZBJ 摆正 + Extents」的快速主路径；
/// 2. 命中缓存时直接返回，避免同一实体反复做几何分析；
/// 3. 仅当快速路径失败时，才让上层回退到旧版高成本算法。
/// </summary>
public static class ObbComputationService
{
    public sealed class Result
    {
        public bool Success { get; set; }
        public double Length { get; set; }
        public double Width { get; set; }
        public double Thickness { get; set; }
        public string Method { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    private sealed class CacheEntry
    {
        public Result Result { get; init; }
        public DateTime CachedAtUtc { get; init; }
    }

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public static bool TryCompute(Solid3d solid, out Result result)
    {
        return TryCompute(solid, null, out result);
    }

    public static bool TryCompute(Solid3d solid, ObbPerformanceTrace trace, out Result result)
    {
        result = new Result { Success = false, Reason = "实体无效" };
        if (solid == null || solid.IsErased)
            return false;

        string cacheKey = trace == null
            ? BuildCacheKey(solid)
            : trace.Measure("fast.cache_key", () => BuildCacheKey(solid));
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            bool cacheHit = false;
            if (trace == null)
            {
                cacheHit = TryGetCached(cacheKey, out result);
            }
            else
            {
                var sw = Stopwatch.StartNew();
                cacheHit = TryGetCached(cacheKey, out result);
                sw.Stop();
                trace.Add("fast.cache_lookup", sw.Elapsed.TotalMilliseconds);
            }

            if (cacheHit)
            {
                trace?.Add("fast.cache_hit", 0.0, result.Method);
                return result.Success;
            }
        }

        try
        {
            var computed = ComputeByAlignedClone(solid, trace);
            if (!computed.Success)
            {
                result = computed;
                return false;
            }

            result = computed;
            if (!string.IsNullOrWhiteSpace(cacheKey))
                SetCache(cacheKey, computed);
            return true;
        }
        catch (System.Exception ex)
        {
            result = new Result
            {
                Success = false,
                Method = "fast-obb-exception",
                Reason = ex.Message
            };
            return false;
        }
    }

    private static Result ComputeByAlignedClone(Solid3d solid, ObbPerformanceTrace trace)
    {
        Solid3d clone = null;
        try
        {
            clone = trace == null
                ? solid.Clone() as Solid3d
                : trace.Measure("fast.clone", () => solid.Clone() as Solid3d);
            if (clone == null)
            {
                return new Result
                {
                    Success = false,
                    Method = "clone",
                    Reason = "实体无法克隆"
                };
            }

            var align = trace == null
                ? BzbjAlignmentService.AlignToWorldXY(clone, normalToleranceDeg: 0.01)
                : trace.Measure("fast.align_xy", () => BzbjAlignmentService.AlignToWorldXY(clone, normalToleranceDeg: 0.01));
            if (!align.Success && align.NormalAngleToZDeg > 1.0)
            {
                return new Result
                {
                    Success = false,
                    Method = "align",
                    Reason = $"摆正失败: {align.Reason}"
                };
            }

            var ext = trace == null
                ? ((Entity)clone).GeometricExtents
                : trace.Measure("fast.extents", () => ((Entity)clone).GeometricExtents);
            double dx = Math.Abs(ext.MaxPoint.X - ext.MinPoint.X);
            double dy = Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y);
            double dz = Math.Abs(ext.MaxPoint.Z - ext.MinPoint.Z);
            var dims = new[] { dx, dy, dz };
            Array.Sort(dims);

            double thickness = Math.Round(dims[0], 2);
            double width = Math.Round(dims[1], 2);
            double length = Math.Round(dims[2], 2);
            if (thickness <= 0 || width <= 0 || length <= 0)
            {
                return new Result
                {
                    Success = false,
                    Method = "aligned-extents",
                    Reason = "尺寸无效"
                };
            }

            return new Result
            {
                Success = true,
                Length = length,
                Width = width,
                Thickness = thickness,
                Method = "aligned-extents",
                Reason = string.IsNullOrWhiteSpace(align.Reason) ? "克隆体摆正后直接取包围盒" : align.Reason
            };
        }
        catch (System.Exception ex)
        {
            return new Result
            {
                Success = false,
                Method = "aligned-extents-exception",
                Reason = ex.Message
            };
        }
        finally
        {
            try { clone?.Dispose(); } catch { }
        }
    }

    private static string BuildCacheKey(Solid3d solid)
    {
        try
        {
            var ext = ((Entity)solid).GeometricExtents;
            double dx = Math.Abs(ext.MaxPoint.X - ext.MinPoint.X);
            double dy = Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y);
            double dz = Math.Abs(ext.MaxPoint.Z - ext.MinPoint.Z);
            string handle = solid.ObjectId.IsNull ? "transient" : solid.ObjectId.Handle.ToString();
            double volume = ReadMassVolume(solid);

            return string.Join("|",
                handle,
                RoundKey(dx),
                RoundKey(dy),
                RoundKey(dz),
                RoundKey(volume),
                RoundKey(ext.MinPoint.X),
                RoundKey(ext.MinPoint.Y),
                RoundKey(ext.MinPoint.Z),
                RoundKey(ext.MaxPoint.X),
                RoundKey(ext.MaxPoint.Y),
                RoundKey(ext.MaxPoint.Z));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static double ReadMassVolume(Solid3d solid)
    {
        try
        {
            object mp = solid.MassProperties;
            if (mp == null)
                return 0;

            var type = mp.GetType();
            var prop = type.GetProperty("Volume");
            if (prop != null)
            {
                object value = prop.GetValue(mp);
                if (value is double volume)
                    return volume;
            }

            var field = type.GetField("Volume");
            if (field != null)
            {
                object value = field.GetValue(mp);
                if (value is double volume)
                    return volume;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static string RoundKey(double value)
    {
        return Math.Round(value, 3, MidpointRounding.AwayFromZero).ToString("F3", CultureInfo.InvariantCulture);
    }

    private static bool TryGetCached(string key, out Result result)
    {
        lock (SyncRoot)
        {
            TrimExpiredEntries();
            if (Cache.TryGetValue(key, out var entry))
            {
                result = entry.Result;
                return true;
            }
        }

        result = null;
        return false;
    }

    private static void SetCache(string key, Result result)
    {
        lock (SyncRoot)
        {
            TrimExpiredEntries();
            Cache[key] = new CacheEntry
            {
                Result = result,
                CachedAtUtc = DateTime.UtcNow
            };
        }
    }

    private static void TrimExpiredEntries()
    {
        if (Cache.Count == 0)
            return;

        DateTime cutoff = DateTime.UtcNow - CacheTtl;
        var expiredKeys = new List<string>();
        foreach (var entry in Cache)
        {
            if (entry.Value.CachedAtUtc < cutoff)
                expiredKeys.Add(entry.Key);
        }

        foreach (string key in expiredKeys)
        {
            Cache.Remove(key);
        }
    }
}
