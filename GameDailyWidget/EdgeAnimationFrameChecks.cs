using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameDailyWidget;

/// <summary>
/// Isolated native move cadence measurement. It records actual HWND positions,
/// rather than inferring animation smoothness from the requested timer interval.
/// It neither moves the cursor nor reads accounts or saves preferences.
/// </summary>
public static class EdgeAnimationFrameChecks
{
    public static async Task<object> RunAsync(MainWindow window, EdgeAutoHideController controller)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(controller);
        if (!App.IsPreview) throw new InvalidOperationException("帧率验证仅支持隔离预览模式。");
        var initial = controller.ReadNativeState().Window;
        var originalEnabled = controller.Enabled;
        var originalEdge = controller.DockedEdge;
        var motions = new List<MotionReport>();
        var allIntervals = new List<double>();
        using var hold = controller.HoldPointerForProbe();
        controller.Suspend();
        try
        {
            controller.SetEnabled(true);
            foreach (var edge in new[] { "left", "right", "top", "bottom" })
            {
                for (var round = 1; round <= 2; ++round)
                {
                    Assert(controller.ProbeDock(edge, false), "帧率验证无法吸附预览窗口。");
                    await SettleAsync(window);
                    var expanded = controller.ReadNativeState();
                    Assert(expanded.VisiblePart is { }, "帧率验证没有有效工作区交集。");
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
                        expanded.Window.X + expanded.Window.Width / 2,
                        expanded.Window.Y + expanded.Window.Height / 2));
                    var work = screen.WorkingArea;
                    var area = new EdgeRectangle(work.X, work.Y, work.Width, work.Height);
                    var hidden = EdgeDockGeometry.Collapse(expanded.Window, area,
                        EdgeDockGeometry.Parse(edge), dpi.DpiScaleX, dpi.DpiScaleY);
                    motions.Add(await MeasureMotionAsync(window, controller, edge, round, true, hidden, allIntervals));
                    motions.Add(await MeasureMotionAsync(window, controller, edge, round, false, expanded.Window, allIntervals));
                }
            }
        }
        finally
        {
            controller.Restore();
            controller.SetEnabled(false);
            FloatingWindowPlacement.MoveToScreen(window, initial.X, initial.Y);
            controller.SetEnabled(originalEnabled);
            if (originalEnabled && EdgeDockGeometry.Parse(originalEdge) != EdgeDockSide.None)
            {
                controller.ProbeDock(originalEdge, false);
                controller.Restore();
            }
            controller.Resume();
            await SettleAsync(window);
        }
        return new
        {
            status = "passed",
            definitions = "native move callback cadence, not actual DWM presented FPS",
            realCredentialsRead = false,
            personalPreferencesSaved = false,
            physicalCursorMoved = false,
            motionCount = motions.Count,
            intervalMedianMs = Rounded(Percentile(allIntervals, .5)),
            intervalP95Ms = Rounded(Percentile(allIntervals, .95)),
            intervalMaxMs = Rounded(allIntervals.Max()),
            estimatedUpdateHz = Rounded(allIntervals.Count * 1000d / allIntervals.Sum()),
            totalFrames = motions.Sum(motion => motion.frames),
            motions
        };
    }

    private static async Task<MotionReport> MeasureMotionAsync(MainWindow window,
        EdgeAutoHideController controller, string direction, int round, bool collapse,
        EdgeRectangle expected, List<double> allIntervals)
    {
        var initial = controller.ReadNativeState().Window;
        var samples = new List<(long Timestamp, int X, int Y)>();
        Exception? callbackFailure = null;
        var startedAt = Stopwatch.GetTimestamp();
        void Frame()
        {
            try
            {
                // A single GetWindowRect per callback avoids region probes or
                // serialization work distorting the animation being measured.
                var timestamp = Stopwatch.GetTimestamp();
                var position = FloatingWindowPlacement.GetScreenPosition(window);
                samples.Add((timestamp, position.X, position.Y));
            }
            catch (Exception exception) { callbackFailure ??= exception; }
        }

        controller.AnimationFrameForCheck += Frame;
        try
        {
            startedAt = Stopwatch.GetTimestamp();
            Assert(await controller.AnimateForProbeAsync(collapse).WaitAsync(TimeSpan.FromSeconds(3)),
                $"帧率验证中的动画没有成功抵达终点：{direction}/{collapse} {controller.LastAnimationStopForCheck} frames={samples.Count}。");
            var finishedAt = Stopwatch.GetTimestamp();
            if (callbackFailure is not null) throw callbackFailure;
            var final = controller.ReadNativeState();
            Assert(samples.Count >= 3, "动画不足三个真实原生位置回调。");
            Assert(samples.Any(sample =>
                (sample.X != initial.X || sample.Y != initial.Y) &&
                (sample.X != expected.X || sample.Y != expected.Y)), "动画缺少真实原生中间位置。");
            Assert(final.Window == expected && !controller.IsAnimating && controller.IsCollapsed == collapse &&
                !controller.HasAnimationFrameSubscriptionForCheck, "动画终点位置、完成状态或渲染订阅清理错误。");
            Assert(collapse ? final.HasNativeRegion : !final.HasNativeRegion,
                "动画终点的原生裁剪状态错误。");
            var intervals = samples.Skip(1).Select((sample, index) =>
                Milliseconds(sample.Timestamp - samples[index].Timestamp)).ToList();
            allIntervals.AddRange(intervals);
            return new MotionReport(
                direction, round, collapse, samples.Count,
                samples.Select(sample => (sample.X, sample.Y)).Distinct().Count(),
                Rounded(Milliseconds(finishedAt - startedAt)),
                Rounded(Percentile(intervals, .5)), Rounded(Percentile(intervals, .95)),
                Rounded(intervals.Max()), Rounded(intervals.Count * 1000d / intervals.Sum()),
                samples.Select(sample => new FrameSample(
                    Rounded(Milliseconds(sample.Timestamp - startedAt)),
                    sample.X - initial.X, sample.Y - initial.Y)).ToArray());
        }
        finally { controller.AnimationFrameForCheck -= Frame; }
    }

    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    private static double Rounded(double value) => Math.Round(value, 3);

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        Assert(values.Count > 0, "没有可用于计算帧间隔的样本。");
        var ordered = values.OrderBy(value => value).ToArray();
        var index = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower);
    }

    private static async Task SettleAsync(MainWindow window) =>
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public sealed record MotionReport(string direction, int round, bool collapse, int frames,
        int distinctPositionFrames, double durationMs, double intervalMedianMs, double intervalP95Ms,
        double maxMs, double estimatedUpdateHz, FrameSample[] samples);
    public sealed record FrameSample(double elapsedMs, int relativeX, int relativeY);
}
