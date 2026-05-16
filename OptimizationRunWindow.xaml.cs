using System;
using System.Windows;
using System.Windows.Threading;

namespace FurniturePlugin;

public partial class OptimizationRunWindow : Window
{
    public bool RequestAdoptCurrentBest { get; private set; }
    public bool RequestCancelAndRollback { get; private set; }
    private readonly DateTime _startAt = DateTime.Now;
    private readonly DispatcherTimer _uiTimer;
    private double _lastElapsedSeconds;
    private double _bestKnownUtilization;

    public OptimizationRunWindow()
    {
        InitializeComponent();
        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _uiTimer.Tick += (_, __) =>
        {
            var liveElapsed = Math.Max(_lastElapsedSeconds, (DateTime.Now - _startAt).TotalSeconds);
            ElapsedTextBlock.Text = FormatElapsed(liveElapsed);
        };
        _uiTimer.Start();
    }

    public void UpdateProgress(Optimization.OptimizationProgress progress)
    {
        if (progress == null) return;
        GroupTextBlock.Text = progress.GroupCount > 0
            ? $"{progress.GroupIndex}/{progress.GroupCount} ({progress.GroupKey})"
            : "-/-";
        _lastElapsedSeconds = Math.Max(_lastElapsedSeconds, progress.ElapsedSeconds);
        ElapsedTextBlock.Text = FormatElapsed(_lastElapsedSeconds);
        _bestKnownUtilization = Math.Max(_bestKnownUtilization, progress.BestUtilization);
        UtilTextBlock.Text = $"{_bestKnownUtilization * 100:F1}%";
        GapTextBlock.Text = progress.GapPercent > 0 ? $"{progress.GapPercent:F2}%" : "-";
        RouteTextBlock.Text = string.IsNullOrWhiteSpace(progress.RouteStatus) ? "-" : progress.RouteStatus;
        StatusTextBlock.Text = string.IsNullOrWhiteSpace(progress.Message) ? "运行中..." : progress.Message;
    }

    public void SetCompleted(string message)
    {
        _uiTimer.Stop();
        StatusTextBlock.Text = message;
        AdoptButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
    }

    private void AdoptButton_Click(object sender, RoutedEventArgs e)
    {
        RequestAdoptCurrentBest = true;
        RequestCancelAndRollback = false;
        AdoptButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusTextBlock.Text = "正在请求停止并采用当前最优结果...";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        RequestCancelAndRollback = true;
        RequestAdoptCurrentBest = false;
        AdoptButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusTextBlock.Text = "正在请求取消并回退...";
    }

    private static string FormatElapsed(double elapsedSeconds)
    {
        if (elapsedSeconds < 0) elapsedSeconds = 0;
        int total = (int)Math.Floor(elapsedSeconds);
        int mm = total / 60;
        int ss = total % 60;
        return $"{mm:00}:{ss:00}";
    }
}
