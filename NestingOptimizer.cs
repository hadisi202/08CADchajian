using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FurniturePlugin
{
    /// <summary>
    /// 排版持续优化调度器
    /// 后台持续搜索更优排列方案，发现更高利用率时通知UI
    /// </summary>
    public sealed class NestingOptimizer : IDisposable
    {
        private CancellationTokenSource _cts;
        private Task _optimizeTask;
        private readonly object _lock = new();
        private NestingService.NestingResult _bestResult;
        private double _bestUtilization;
        private int _iterationCount;
        private bool _disposed;

        public event Action<OptimizationUpdate> OnBetterResultFound;
        public event Action<int, double> OnIterationComplete;

        public NestingService.NestingResult BestResult
        {
            get { lock (_lock) { return _bestResult; } }
        }

        public double BestUtilization
        {
            get { lock (_lock) { return _bestUtilization; } }
        }

        public int IterationCount
        {
            get { lock (_lock) { return _iterationCount; } }
        }

        public bool IsRunning => _optimizeTask != null && !_optimizeTask.IsCompleted;

        /// <summary>
        /// 开始持续优化
        /// </summary>
        public void StartOptimization(
            List<NestingService.NestingItem> items,
            NestingService.NestingConfig config,
            NestingService.NestingResult initialResult)
        {
            Stop();

            lock (_lock)
            {
                _bestResult = initialResult;
                _bestUtilization = initialResult?.TotalUtilization ?? 0;
                _iterationCount = 0;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _optimizeTask = Task.Run(() => OptimizeLoop(items, config, token), token);
        }

        /// <summary>停止优化</summary>
        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _optimizeTask?.Wait(2000);
            }
            catch { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _optimizeTask = null;
            }
        }

        private void OptimizeLoop(
            List<NestingService.NestingItem> items,
            NestingService.NestingConfig config,
            CancellationToken token)
        {
            var rng = new Random();
            var workingItems = items.ToList();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var permuted = PermuteItems(workingItems, rng);
                    var candidate = NestingService.Nest(permuted, config);

                    int iteration;
                    lock (_lock) { iteration = ++_iterationCount; }

                    double candidateUtil = candidate.TotalUtilization;
                    double currentBest;
                    lock (_lock) { currentBest = _bestUtilization; }

                    if (candidateUtil > currentBest + 0.001)
                    {
                        lock (_lock)
                        {
                            _bestResult = candidate;
                            _bestUtilization = candidateUtil;
                        }

                        OnBetterResultFound?.Invoke(new OptimizationUpdate
                        {
                            Iteration = iteration,
                            PreviousUtilization = currentBest,
                            NewUtilization = candidateUtil,
                            Result = candidate
                        });
                    }

                    OnIterationComplete?.Invoke(iteration, candidateUtil);

                    if (candidateUtil > 0.98) break;

                    if (iteration % 10 == 0)
                        Thread.Sleep(10);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        /// <summary>随机扰动排列顺序</summary>
        private static List<NestingService.NestingItem> PermuteItems(
            List<NestingService.NestingItem> items, Random rng)
        {
            var list = items.ToList();
            int strategy = rng.Next(4);

            switch (strategy)
            {
                case 0:
                    for (int i = list.Count - 1; i > 0; i--)
                    {
                        int j = rng.Next(i + 1);
                        (list[i], list[j]) = (list[j], list[i]);
                    }
                    break;

                case 1:
                    int swapCount = Math.Max(2, list.Count / 4);
                    for (int s = 0; s < swapCount; s++)
                    {
                        int a = rng.Next(list.Count);
                        int b = rng.Next(list.Count);
                        (list[a], list[b]) = (list[b], list[a]);
                    }
                    break;

                case 2:
                    list.Sort((a, b) =>
                    {
                        double areaA = a.Length * a.Width;
                        double areaB = b.Length * b.Width;
                        double noise = (rng.NextDouble() - 0.5) * 0.3;
                        return (areaB * (1 + noise)).CompareTo(areaA * (1 + noise));
                    });
                    break;

                case 3:
                    list.Sort((a, b) =>
                    {
                        double maxA = Math.Max(a.Length, a.Width);
                        double maxB = Math.Max(b.Length, b.Width);
                        double noise = (rng.NextDouble() - 0.5) * 0.2;
                        return (maxB * (1 + noise)).CompareTo(maxA * (1 + noise));
                    });
                    break;
            }

            return list;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }

    public class OptimizationUpdate
    {
        public int Iteration { get; set; }
        public double PreviousUtilization { get; set; }
        public double NewUtilization { get; set; }
        public NestingService.NestingResult Result { get; set; }
    }

    /// <summary>
    /// 余料分析服务
    /// 计算排版后大板上的最大矩形余料区
    /// </summary>
    public static class RemnantService
    {
        public class RemnantArea
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double Area => Width * Height;
        }

        /// <summary>
        /// 计算一张大板上的最大矩形余料区
        /// 使用直方图最大矩形算法
        /// </summary>
        public static RemnantArea FindLargestRemnant(
            NestingService.NestingSheet sheet, double cellSize = 5.0)
        {
            if (sheet == null || sheet.Width <= 0 || sheet.Height <= 0)
                return new RemnantArea();

            int cols = (int)Math.Ceiling(sheet.Width / cellSize);
            int rows = (int)Math.Ceiling(sheet.Height / cellSize);

            var grid = new bool[rows, cols];

            foreach (var placed in sheet.PlacedItems ?? new List<NestingService.PlacedItem>())
            {
                if (placed?.Item == null) continue;
                double pw = placed.IsRotated ? placed.Item.Width : placed.Item.Length;
                double ph = placed.IsRotated ? placed.Item.Length : placed.Item.Width;

                int x1 = Math.Max(0, (int)(placed.X / cellSize));
                int y1 = Math.Max(0, (int)(placed.Y / cellSize));
                int x2 = Math.Min(cols, (int)Math.Ceiling((placed.X + pw) / cellSize));
                int y2 = Math.Min(rows, (int)Math.Ceiling((placed.Y + ph) / cellSize));

                for (int r = y1; r < y2; r++)
                    for (int c = x1; c < x2; c++)
                        grid[r, c] = true;
            }

            var heights = new int[cols];
            int bestArea = 0;
            int bestX = 0, bestY = 0, bestW = 0, bestH = 0;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                    heights[c] = grid[r, c] ? 0 : heights[c] + 1;

                var stack = new Stack<int>();
                for (int c = 0; c <= cols; c++)
                {
                    int h = c < cols ? heights[c] : 0;
                    while (stack.Count > 0 && heights[stack.Peek()] > h)
                    {
                        int height = heights[stack.Pop()];
                        int width = stack.Count > 0 ? c - stack.Peek() - 1 : c;
                        int area = height * width;
                        if (area > bestArea)
                        {
                            bestArea = area;
                            bestW = width;
                            bestH = height;
                            bestX = stack.Count > 0 ? stack.Peek() + 1 : 0;
                            bestY = r - height + 1;
                        }
                    }
                    stack.Push(c);
                }
            }

            return new RemnantArea
            {
                X = bestX * cellSize,
                Y = bestY * cellSize,
                Width = bestW * cellSize,
                Height = bestH * cellSize
            };
        }

        /// <summary>计算所有大板的余料区</summary>
        public static List<(int SheetIndex, RemnantArea Remnant)> FindAllRemnants(
            NestingService.NestingResult result, double cellSize = 5.0)
        {
            var remnants = new List<(int, RemnantArea)>();

            var allSheets = (result.Sheets ?? new List<NestingService.NestingSheet>())
                .Concat(result.IrregularSheets ?? new List<NestingService.NestingSheet>());

            foreach (var sheet in allSheets)
            {
                var remnant = FindLargestRemnant(sheet, cellSize);
                if (remnant.Area > 100 * 100)
                    remnants.Add((sheet.SheetIndex, remnant));
            }

            return remnants;
        }
    }
}
