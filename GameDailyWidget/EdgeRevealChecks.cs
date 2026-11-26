using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameDailyWidget;

/// <summary>Checks occlusion and activation using only the isolated preview windows.</summary>
public static class EdgeRevealChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(MainWindow window, EdgeAutoHideController controller)
    {
        if (!App.IsPreview) throw new InvalidOperationException("贴边误展开验证仅支持隔离预览模式。");
        var checks = new List<string>();
        var initial = controller.ReadNativeState();
        var originalTopmost = window.Topmost;
        var originalEnabled = controller.Enabled;
        var originalSide = controller.DockedEdge;
        var originalCollapsed = controller.IsCollapsed;
        var originalIsActive = window.IsActive;
        Window? overlay = null;
        using var pointerHold = controller.HoldPointerForProbe();
        controller.Suspend();
        try
        {
            controller.SetEnabled(true);
            window.SetAlwaysOnTop(true);
            await SettleAsync(window);
            Assert(controller.ProbeDock("right", true), "遮挡验证无法收起窗口。");
            var collapsed = controller.ReadNativeState();
            var strip = collapsed.VisiblePart ?? throw new InvalidOperationException("收起窗口缺少可见小条。");
            var x = strip.X + strip.Width / 2;
            var y = strip.Y + strip.Height / 2;
            Assert(controller.IsPointerOverWidgetForCheck(x, y), "无遮挡时系统没有命中预览组件的小条。");

            overlay = new Window
            {
                Title = "贴边遮挡检查（隔离预览）",
                Width = 128,
                Height = 128,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(42, 45, 51)),
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            PreviewDisplay.Place(overlay);
            overlay.Show();
            var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x,y)).WorkingArea;
            FloatingWindowPlacement.MoveToScreen(overlay, Math.Clamp(x-32,area.Left,area.Right-128), Math.Clamp(y-32,area.Top,area.Bottom-128));
            await SettleAsync(window);
            Assert(!controller.IsPointerOverWidgetForCheck(x, y), "真实遮挡窗口未成为小条坐标的实际输入目标。");
            controller.ProbePointer(x, y, TimeSpan.Zero, pointerOverWidget: false);
            Assert(controller.IsCollapsed && !controller.IsAnimating && controller.ReadNativeState().Window == collapsed.Window,
                "小条坐标被其他窗口遮挡时仍误展开。");

            overlay.Hide();
            await SettleAsync(window);
            Assert(controller.IsPointerOverWidgetForCheck(x, y), "移除遮挡后系统未重新命中小条。");
            controller.ProbePointer(x, y, TimeSpan.Zero, pointerOverWidget: true);
            Assert(!controller.IsCollapsed && !controller.ReadNativeState().HasNativeRegion,
                "真实可命中的小条无法正常悬停展开。");
            checks.Add("PASS 真实顶层窗口遮挡小条时不展开，移除遮挡并命中组件后可展开");

            Assert(controller.ProbeDock("right", false), "外部点击验证无法吸附窗口。");
            var intermediate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = controller.ReadNativeState().Window;
            void Frame()
            {
                if (controller.IsAnimating && controller.ReadNativeState().Window != start) intermediate.TrySetResult(true);
            }
            controller.AnimationFrameForCheck += Frame;
            Task<bool> hiding;
            try
            {
                hiding = controller.AnimateForProbeAsync(true);
                await intermediate.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var moving = controller.ReadNativeState();
                var visible = moving.VisiblePart ?? throw new InvalidOperationException("收起动画缺少可见区域。");
                controller.ProbePointer(visible.X + visible.Width / 2, visible.Y + visible.Height / 2,
                    TimeSpan.Zero, buttonsPressed: true, animate: true, pointerOverWidget: false);
                Assert(controller.IsAnimating && controller.ReadNativeState().Window == moving.Window,
                    "外部窗口点击中断了收起动画或使组件瞬移展开。");
            }
            finally { controller.AnimationFrameForCheck -= Frame; }
            Assert(await hiding.WaitAsync(TimeSpan.FromSeconds(3)) && controller.IsCollapsed && !controller.IsAnimating,
                "外部点击后收起动画没有继续完成。");
            AssertStrip(controller.ReadNativeState(), window);
            checks.Add("PASS 收起动画途中外部点击不恢复组件，动画继续完成并保留正确裁剪");

            // Deliver activation messages to this HWND only. This tests the real
            // WPF Activated event without changing the user's foreground window.
            Assert(controller.ProbeDock("right", true), "激活事件验证无法收起窗口。");
            var activationStart = controller.ReadNativeState();
            var activated = 0;
            void Activated(object? sender, EventArgs args) => ++activated;
            window.Activated += Activated;
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                SendMessage(handle, 0x0006, nint.Zero, nint.Zero);
                SendMessage(handle, 0x0006, new nint(1), nint.Zero);
                await SettleAsync(window);
                Assert(activated > 0, "未实际触发 WPF Activated 事件，不能宣称激活回归通过。");
                Assert(controller.IsCollapsed && !controller.IsAnimating &&
                    controller.ReadNativeState().Window == activationStart.Window,
                    "没有悬停或显式显示操作，仅激活事件就展开了组件。");
                AssertStrip(controller.ReadNativeState(), window);
            }
            finally { window.Activated -= Activated; }
            checks.Add("PASS 仅向预览窗口发送 WM_ACTIVATE 并触发 WPF Activated 不展开已收起组件");
        }
        finally
        {
            overlay?.Close();
            controller.Restore();
            controller.SetEnabled(false);
            window.SetAlwaysOnTop(originalTopmost);
            await SettleAsync(window);
            FloatingWindowPlacement.MoveToScreen(window, initial.Window.X, initial.Window.Y);
            controller.SetEnabled(originalEnabled);
            if (originalEnabled && EdgeDockGeometry.Parse(originalSide) != EdgeDockSide.None)
                controller.ProbeDock(originalSide, originalCollapsed);
            if (window.IsActive != originalIsActive)
                SendMessage(new WindowInteropHelper(window).Handle, 0x0006,
                    originalIsActive ? new nint(1) : nint.Zero, nint.Zero);
            controller.Resume();
        }
        return checks.AsReadOnly();
    }

    private static void AssertStrip(EdgeDockNativeState state, MainWindow window)
    {
        var region = state.Region ?? throw new InvalidOperationException("收起后没有原生裁剪区域。");
        var visible = state.VisiblePart ?? throw new InvalidOperationException("收起后没有可见小条。");
        var expected = (int)Math.Round(8 * VisualTreeHelper.GetDpi(window).DpiScaleX, MidpointRounding.AwayFromZero);
        Assert(visible.Width == expected && region.X == visible.X - state.Window.X &&
            region.Y == visible.Y - state.Window.Y && region.Width == visible.Width && region.Height == visible.Height,
            "右侧小条或原生裁剪与实际可见范围不一致。");
    }

    private static async Task SettleAsync(MainWindow window) =>
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
