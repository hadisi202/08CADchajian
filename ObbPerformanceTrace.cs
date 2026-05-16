using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FurniturePlugin;

/// <summary>
/// OBB/FLATSHOT 相关链路的轻量性能埋点。
/// 仅在调用方显式传入时记录，不影响默认业务流程。
/// </summary>
public sealed class ObbPerformanceTrace
{
    public sealed class Phase
    {
        public string Name { get; init; } = "";
        public double ElapsedMs { get; init; }
        public string Note { get; init; } = "";
    }

    private readonly List<Phase> _phases = new();

    public IReadOnlyList<Phase> Phases => _phases;

    public double TotalMs => _phases.Sum(p => p.ElapsedMs);

    public void Measure(string name, Action action, string note = "")
    {
        if (action == null)
            return;

        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        Add(name, sw.Elapsed.TotalMilliseconds, note);
    }

    public T Measure<T>(string name, Func<T> func, string note = "")
    {
        if (func == null)
            return default;

        var sw = Stopwatch.StartNew();
        T result = func();
        sw.Stop();
        Add(name, sw.Elapsed.TotalMilliseconds, note);
        return result;
    }

    public void Add(string name, double elapsedMs, string note = "")
    {
        _phases.Add(new Phase
        {
            Name = name ?? "",
            ElapsedMs = elapsedMs < 0 ? 0 : elapsedMs,
            Note = note ?? ""
        });
    }

    public string ToDisplayText()
    {
        if (_phases.Count == 0)
            return "无性能数据";

        var lines = new List<string>
        {
            $"总耗时: {TotalMs:F2} ms"
        };

        foreach (var phase in _phases)
        {
            string line = $"{phase.Name}: {phase.ElapsedMs:F2} ms";
            if (!string.IsNullOrWhiteSpace(phase.Note))
                line += $" ({phase.Note})";
            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
