using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameDailyWidget;

public static class EdgeDockChecks
{
    public static Task<IReadOnlyList<string>> RunAsync() => Task.FromResult(Run());

    public static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        var work = new EdgeRectangle(0, 40, 1920, 1000);
        var body = new EdgeRectangle(600, 300, 340, 420);
        foreach (var edge in new[] { EdgeDockSide.Top, EdgeDockSide.Right, EdgeDockSide.Bottom, EdgeDockSide.Left })
        {
            var expanded = EdgeDockGeometry.SnapExpanded(body, work, edge);
            Assert(EdgeDockGeometry.DetectNearestEdge(expanded, work) == edge, "四边吸附识别失败。");
            var collapsed = EdgeDockGeometry.Collapse(expanded, work, edge);
            var visible = EdgeDockGeometry.VisiblePart(collapsed, work);
            Assert((edge is EdgeDockSide.Top or EdgeDockSide.Bottom ? visible.Height : visible.Width) == 8,
                "收起后可见条宽度不是八个 DIP。");
            Assert(EdgeDockGeometry.SnapExpanded(expanded, work, edge) == expanded, "收起恢复后锚点漂移。");
            checks.Add($"PASS 贴边 {EdgeDockGeometry.Name(edge)} 吸附、可见小条和恢复锚点");
        }

        var negative = new EdgeRectangle(-2560, -200, 2560, 1400);
        var negativeBody = new EdgeRectangle(-900, 50, 510, 630);
        var negativeAnchor = EdgeDockGeometry.SnapExpanded(negativeBody, negative, EdgeDockSide.Left);
        var negativeCollapsed = EdgeDockGeometry.Collapse(negativeAnchor, negative, EdgeDockSide.Left, 1.5, 1.5);
        Assert(negativeAnchor.X == -2560 && EdgeDockGeometry.VisiblePart(negativeCollapsed, negative).Width == 12,
            "负坐标显示器或百分之一百五十 DPI 计算失败。");
        checks.Add("PASS 贴边负坐标显示器与百分之一百五十 DPI");

        var scaled = EdgeDockGeometry.SnapExpanded(new EdgeRectangle(400, 60, 680, 840), work, EdgeDockSide.Top);
        var scaledCollapsed = EdgeDockGeometry.Collapse(scaled, work, EdgeDockSide.Top, 2, 2);
        Assert(EdgeDockGeometry.VisiblePart(scaledCollapsed, work).Height == 16,
            "高 DPI 小条没有按实际像素缩放。");
        Assert(EdgeDockGeometry.DetectNearestEdge(new EdgeRectangle(500, 63, 680, 840), work, 2, 2) == EdgeDockSide.Top,
            "十二 DIP 吸附阈值没有按高 DPI 缩放。");
        Assert(EdgeDockGeometry.DetectNearestEdge(new EdgeRectangle(500, 65, 680, 840), work, 2, 2) == EdgeDockSide.None,
            "吸附阈值外窗口被误吸附。");
        checks.Add("PASS 贴边高 DPI 阈值和物理小条尺寸");

        Assert(EdgeDockGeometry.DetectNearestEdge(new EdgeRectangle(1580, 40, 340, 420), work) == EdgeDockSide.Top,
            "右上角相等距离没有确定选择上边。");
        Assert(EdgeDockGeometry.DetectNearestEdge(new EdgeRectangle(1580, 49, 340, 420), work) == EdgeDockSide.Right,
            "角落没有选择更近的边。");
        Assert(EdgeDockGeometry.DetectNearestEdge(new EdgeRectangle(0, 620, 340, 420), work) == EdgeDockSide.Bottom,
            "左下角相等距离没有确定选择下边。");
        checks.Add("PASS 贴边角落最近边选择与固定平局规则");

        var far = EdgeDockGeometry.ClampExpanded(new EdgeRectangle(-9000, 8000, 340, 420), work);
        Assert(far.X == work.X && far.Y == 620, "离屏恢复没有回到工作区。");
        var movedMonitor = EdgeDockGeometry.ReflowExpanded(negativeAnchor, 340, 420, work, EdgeDockSide.Left);
        Assert(movedMonitor.X == 0 && movedMonitor.Y >= 40 && movedMonitor.Bottom <= work.Bottom,
            "显示器移除后的展开锚点不可见。");
        checks.Add("PASS 贴边离屏位置与显示器移除后的恢复");

        var rightAnchor = EdgeDockGeometry.SnapExpanded(body, work, EdgeDockSide.Right);
        var resized = EdgeDockGeometry.ReflowExpanded(rightAnchor, 600, 700, work, EdgeDockSide.Right);
        Assert(resized.Right == work.Right && resized.X == 1320 && resized.Bottom <= work.Bottom,
            "卡片变大后没有保持右边展开锚点。");
        var bottomAnchor = EdgeDockGeometry.SnapExpanded(body, work, EdgeDockSide.Bottom);
        var taller = EdgeDockGeometry.ReflowExpanded(bottomAnchor, 400, 700, work, EdgeDockSide.Bottom);
        Assert(taller.Bottom == work.Bottom && taller.Y == 340, "详情展开后底边位置计算失败。");
        checks.Add("PASS 贴边缩放和详情高度改变时保持边缘锚点");

        var restored = EdgeDockGeometry.ReflowExpanded(rightAnchor, 340, 420, work, EdgeDockSide.Right);
        for (var index = 0; index < 100; index++)
        {
            _ = EdgeDockGeometry.Collapse(restored, work, EdgeDockSide.Right, 1.25, 1.25);
            restored = EdgeDockGeometry.ReflowExpanded(restored, 340, 420, work, EdgeDockSide.Right);
        }
        Assert(restored == rightAnchor, "重复收起恢复积累位置漂移。");
        var tooLarge = EdgeDockGeometry.ClampExpanded(new EdgeRectangle(-1, -1, 2500, 1500), work);
        Assert(tooLarge.X == work.X && tooLarge.Y == work.Y, "超大卡片没有保留可操作的左上位置。");
        checks.Add("PASS 贴边重复恢复无漂移与超大卡片安全定位");

        Assert(EdgeDockGeometry.DetectNearestEdge(new EdgeRectangle(600, 300, 340, 420), work) == EdgeDockSide.None,
            "工作区内部的卡片被误收起。");
        Assert(EdgeDockGeometry.DetectNearestEdge(new EdgeRectangle(-340, -500, 340, 420), work) == EdgeDockSide.None,
            "无正交重叠的离屏卡片被误吸附。");
        checks.Add("PASS 贴边内部位置和无重叠位置不误吸附");

        foreach (var edge in new[] { EdgeDockSide.Top, EdgeDockSide.Right, EdgeDockSide.Bottom, EdgeDockSide.Left })
        {
            var start = EdgeDockGeometry.SnapExpanded(negativeBody, negative, edge);
            var end = EdgeDockGeometry.Collapse(start, negative, edge, 1.5, 1.5);
            Assert(EdgeDockGeometry.Interpolate(start, end, 0) == start &&
                EdgeDockGeometry.Interpolate(start, end, 1) == end, "动画起终点计算失败。");
            var middle = EdgeDockGeometry.Interpolate(start, end, .5);
            Assert(middle != start && middle != end, "动画中间帧瞬间跳到终点。");
            var lastDistance = 0L;
            for (var frame = 0; frame <= 20; frame++)
            {
                var point = EdgeDockGeometry.Interpolate(start, end, frame / 20d);
                var distance = Math.Abs((long)point.X - start.X) + Math.Abs((long)point.Y - start.Y);
                Assert(distance >= lastDistance, "动画出现倒退或越过目标。");
                lastDistance = distance;
            }
        }
        checks.Add("PASS 贴边四向立方缓动起终点、中间帧和负坐标单调轨迹");
        return checks.AsReadOnly();
    }

    /// <summary>Checks only this isolated preview window, never the desktop or real mouse.</summary>
    public static async Task<IReadOnlyList<string>> RunWindowChecksAsync(MainWindow window, EdgeAutoHideController controller)
    {
        if (!App.IsPreview) throw new InvalidOperationException("实窗贴边验证仅支持隔离预览模式。");
        var checks = new List<string>();
        var initial = controller.ReadNativeState();
        var originalScale = controller.ConfiguredScaleForCheck;
        var originalTopmost = window.Topmost;
        var originalEnabled = controller.Enabled;
        var originalSide = controller.DockedEdge;
        var originalIsEnabled = window.IsEnabled;
        using var pointerHold = controller.HoldPointerForProbe();
        controller.Suspend();
        try
        {
            controller.SetEnabled(true);
            window.SetAlwaysOnTop(false);
            await SettleAsync(window);
            Assert(!controller.ReadNativeState().IsDesktopChild, "悬浮组件不是普通顶层窗口。");
            controller.OnGeometryChanged();
            foreach (var edge in new[] { "left", "right", "top", "bottom" })
            {
                Assert(controller.ProbeDock(edge, true), "实际窗口未能收起到指定边缘。");
                var collapsed = controller.ReadNativeState();
                AssertNativeStrip(collapsed, edge, window);
                var anchor = controller.ExpandedPosition;
                Assert(anchor is not null, "收起窗口没有展开锚点。");
                controller.Restore();
                var expanded = controller.ReadNativeState();
                Assert(!controller.IsCollapsed && !expanded.HasNativeRegion, "恢复没有清除原生可见区域。");
                Assert(expanded.Window.X == anchor!.Value.X && expanded.Window.Y == anchor.Value.Y,
                    "恢复后的原生坐标与展开锚点不同。");
                checks.Add($"PASS 实窗 {edge} 原生位置、小条 region 和恢复清除 region");
            }

            foreach (var screen in PreviewDisplay.TestScreens)
            {
                if (screen.Primary) continue;
                controller.SetEnabled(false);
                var monitorArea = screen.WorkingArea;
                var size = controller.ReadNativeState().Window;
                FloatingWindowPlacement.MoveToScreen(window,
                    monitorArea.X + Math.Max(0, (monitorArea.Width - size.Width) / 2),
                    monitorArea.Y + Math.Max(0, (monitorArea.Height - size.Height) / 2));
                await SettleAsync(window);
                controller.SetEnabled(true);
                controller.OnGeometryChanged();
                foreach (var edge in new[] { "left", "right", "top", "bottom" })
                {
                    Assert(controller.ProbeDock(edge, true), "副显示器窗口未能收起到指定边缘。");
                    var collapsed = controller.ReadNativeState();
                    AssertNativeStrip(collapsed, edge, window);
                    var visible = collapsed.VisiblePart!.Value;
                    Assert(visible.X >= monitorArea.X && visible.Y >= monitorArea.Y &&
                        visible.Right <= monitorArea.Right && visible.Bottom <= monitorArea.Bottom,
                        "副显示器的小条落到了其他显示器或工作区外。");
                    var anchor = controller.ExpandedPosition;
                    controller.Restore();
                    var expanded = controller.ReadNativeState();
                    Assert(anchor is not null && !controller.IsCollapsed && !expanded.HasNativeRegion &&
                        expanded.Window.X == anchor.Value.X && expanded.Window.Y == anchor.Value.Y,
                        "副显示器恢复后区域未清除或展开锚点漂移。");
                    checks.Add($"PASS 副显示器 ({monitorArea.X},{monitorArea.Y}) {edge} 原生裁剪与展开锚点");
                }
            }

            Assert(controller.ProbeDock("right", true), "悬停验证无法收起窗口。");
            var strip = controller.ReadNativeState().VisiblePart!.Value;
            var hoverX = strip.X + strip.Width / 2;
            var hoverY = strip.Y + strip.Height / 2;
            controller.ProbePointer(hoverX, hoverY, TimeSpan.Zero, buttonsPressed: true);
            Assert(controller.IsCollapsed, "拖动其他窗口时小条仍抢先展开。");
            controller.ProbePointer(hoverX, hoverY, TimeSpan.FromMilliseconds(1));
            Assert(!controller.IsCollapsed && !controller.ReadNativeState().HasNativeRegion,
                "虚拟鼠标进入小条没有走真实恢复路径。");
            checks.Add("PASS 实窗小条悬停恢复且不抢其他窗口的拖动");

            Assert(controller.ProbeDock("right", false), "立即收起验证无法吸附窗口。");
            var full = controller.ReadNativeState().Window;
            var outsideX = full.X - 60;
            var outsideY = full.Y + full.Height / 2;
            controller.ProbePointer(outsideX, outsideY, TimeSpan.Zero);
            Assert(controller.IsCollapsed, "同一次离开输入没有立即执行收起。");
            controller.ProbePointer(outsideX, outsideY, TimeSpan.Zero, interactionBlocked: true);
            Assert(!controller.IsCollapsed, "交互暂停没有恢复完整卡片。");
            controller.ProbePointer(outsideX, outsideY, TimeSpan.Zero, interactionBlocked: true);
            Assert(!controller.IsCollapsed, "交互暂停期间卡片再次收起。");
            controller.ProbePointer(outsideX, outsideY, TimeSpan.Zero, buttonsPressed: true);
            Assert(!controller.IsCollapsed, "鼠标按下期间卡片仍抢先收起。");
            controller.ProbePointer(outsideX, outsideY, TimeSpan.Zero);
            Assert(controller.IsCollapsed, "交互结束后的第一次离开评估没有立即收起。");
            controller.Restore();
            checks.Add("PASS 实窗同次离开立即收起并保留交互暂停与鼠标按下保护");

            Assert(controller.ProbeDock("right", false), "菜单暂停验证无法吸附窗口。");
            var menuMotion = controller.AnimateForProbeAsync(true);
            try
            {
                window.ShowSettings();
                Assert(controller.InteractionBlockedForCheck, "打开的软件设置菜单未识别为交互暂停。");
                controller.ProbePointer(outsideX, outsideY, TimeSpan.Zero,
                    interactionBlocked: controller.InteractionBlockedForCheck, animate: true);
                Assert(!await menuMotion.WaitAsync(TimeSpan.FromSeconds(3)) && !controller.IsAnimating &&
                    !controller.IsCollapsed && !controller.ReadNativeState().HasNativeRegion,
                    "设置菜单打开时即时贴边仍抢先收起或留下裁剪。");
                controller.ProbePointer(outsideX, outsideY, TimeSpan.Zero,
                    interactionBlocked: controller.InteractionBlockedForCheck, animate: true);
                Assert(!controller.IsAnimating && !controller.IsCollapsed, "在软件菜单操作期间重新启动了收起。");
                checks.Add("PASS 实窗设置菜单视为软件内交互并暂停即时收起");
            }
            finally
            {
                if (window.ActiveSettingsMenu is { } activeMenu) activeMenu.IsOpen = false;
                await SettleAsync(window);
                controller.Restore();
            }

            Assert(controller.ProbeDock("top", true), "隐藏恢复验证无法收起窗口。");
            var hideAnchor = controller.ExpandedPosition;
            window.Hide();
            Assert(!controller.IsCollapsed && !controller.ReadNativeState().HasNativeRegion,
                "隐藏到托盘前未恢复锚点和 region。");
            window.ShowWidget();
            await SettleAsync(window);
            var shown = controller.ReadNativeState();
            Assert(window.IsVisible && !controller.IsCollapsed && !shown.HasNativeRegion &&
                shown.Window.X == hideAnchor!.Value.X && shown.Window.Y == hideAnchor.Value.Y,
                "从托盘恢复后窗口仍离屏或裁剪。");
            checks.Add("PASS 实窗 Hide/ShowWidget 恢复展开锚点和完整区域");

            Assert(controller.ProbeDock("right", true), "modal 暂停验证无法收起窗口。");
            window.IsEnabled = false;
            Assert(controller.InteractionBlockedForCheck, "modal 禁用的窗口未被识别为交互暂停。");
            controller.ProbePointer(outsideX, outsideY, TimeSpan.Zero,
                interactionBlocked: controller.InteractionBlockedForCheck);
            Assert(!controller.IsCollapsed, "modal 禁用时没有恢复完整卡片。");
            window.IsEnabled = originalIsEnabled;
            checks.Add("PASS 实窗 modal 禁用状态阻止自动收起");

            window.SetAlwaysOnTop(true);
            await SettleAsync(window);
            controller.OnGeometryChanged();
            Assert(controller.ProbeDock("left", true), "置顶悬浮验证无法收起窗口。");
            var topmostCollapsed = controller.ReadNativeState();
            AssertNativeStrip(topmostCollapsed, "left", window);
            Assert(window.Topmost && !topmostCollapsed.IsDesktopChild, "置顶悬浮窗口失去顶层或置顶状态。");
            var topmostAnchor = controller.ExpandedPosition;
            controller.Restore();
            window.Hide();
            window.ShowWidget();
            await SettleAsync(window);
            var topmostRestored = controller.ReadNativeState();
            Assert(window.IsVisible && window.Topmost && !topmostRestored.IsDesktopChild &&
                !topmostRestored.HasNativeRegion && !controller.IsCollapsed &&
                topmostRestored.Window.X == topmostAnchor!.Value.X && topmostRestored.Window.Y == topmostAnchor.Value.Y,
                "置顶悬浮窗口隐藏后未恢复完整区域、锚点或顶层状态。");
            window.SetAlwaysOnTop(false);
            checks.Add("PASS 实窗普通顶层悬浮置顶切换、收起和隐藏恢复");

            Assert(controller.ProbeDock("right", true), "缩放验证无法收起窗口。");
            window.SetScale(Math.Clamp(originalScale * 1.15, .5, 1.6));
            await SettleAsync(window);
            controller.OnGeometryChanged();
            Assert(controller.IsCollapsed, "收起期间缩放丢失收起状态。");
            AssertNativeStrip(controller.ReadNativeState(), "right", window);
            var resizedAnchor = controller.ExpandedPosition;
            controller.Restore();
            await SettleAsync(window);
            controller.OnGeometryChanged();
            var resizedExpanded = controller.ReadNativeState();
            Assert(!resizedExpanded.HasNativeRegion && resizedExpanded.Window.X == resizedAnchor!.Value.X &&
                resizedExpanded.Window.Y == resizedAnchor.Value.Y, "缩放后恢复没有采用新的展开锚点。");
            checks.Add("PASS 实窗收起时缩放重算小条和展开锚点");

            checks.AddRange(await RunAnimationChecksAsync(window, controller));
        }
        finally
        {
            window.IsEnabled = originalIsEnabled;
            controller.Restore();
            controller.SetEnabled(false);
            window.SetScale(originalScale);
            window.SetAlwaysOnTop(originalTopmost);
            await SettleAsync(window);
            FloatingWindowPlacement.MoveToScreen(window, initial.Window.X, initial.Window.Y);
            controller.SetEnabled(originalEnabled);
            if (originalEnabled && EdgeDockGeometry.Parse(originalSide) != EdgeDockSide.None)
            {
                controller.ProbeDock(originalSide, false);
                controller.Restore();
            }
            controller.Resume();
        }
        return checks.AsReadOnly();
    }

    private static async Task<IReadOnlyList<string>> RunAnimationChecksAsync(MainWindow window,
        EdgeAutoHideController controller)
    {
        var checks = new List<string>();
        window.SetAlwaysOnTop(false);
        await SettleAsync(window);
        foreach (var screen in PreviewDisplay.TestScreens)
        {
            controller.SetEnabled(false);
            var area = screen.WorkingArea;
            var size = controller.ReadNativeState().Window;
            FloatingWindowPlacement.MoveToScreen(window, area.X + Math.Max(0, (area.Width - size.Width) / 2),
                area.Y + Math.Max(0, (area.Height - size.Height) / 2));
            await SettleAsync(window);
            controller.SetEnabled(true);
            foreach (var edge in new[] { "left", "right", "top", "bottom" })
            {
                Assert(controller.ProbeDock(edge, false), "动画验证无法吸附窗口。");
                var anchor = controller.ExpandedPosition;
                await ObserveMotionAsync(controller, () =>
                {
                    var full = controller.ReadNativeState().Window;
                    controller.ProbePointer(full.X - 60, full.Y + full.Height / 2, TimeSpan.Zero, animate: true);
                    Assert(controller.IsAnimating, "同一次离开输入没有立即启动真实收起动画。");
                    return controller.AnimateForProbeAsync(true);
                }, area, collapse: true);
                AssertNativeStrip(controller.ReadNativeState(), edge, window);
                await ObserveMotionAsync(controller, () =>
                {
                    var visible = controller.ReadNativeState().VisiblePart!.Value;
                    controller.ProbePointer(visible.X + visible.Width / 2, visible.Y + visible.Height / 2,
                        TimeSpan.Zero, animate: true);
                    return controller.AnimateForProbeAsync(false);
                }, area, collapse: false);
                Assert(controller.ExpandedPosition == anchor, "动画收起展开修改了持久化展开锚点。");
                checks.Add($"PASS 动画显示器 ({area.X},{area.Y}) {edge} 真实定时中间帧、逐帧裁剪和悬停展开");
            }
        }

        var reversalArea = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            controller.ReadNativeState().Window.X, controller.ReadNativeState().Window.Y)).WorkingArea;
        Assert(controller.ProbeDock("right", false), "反向动画验证无法吸附窗口。");
        var expanded = controller.ReadNativeState().Window;
        var reversed = new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reversalFailure = null;
        EdgeRectangle? reversalPoint = null;
        var returnFrames = 0;
        void ReverseOnHover()
        {
            try
            {
                var state = controller.ReadNativeState();
                AssertAnimationClip(state, reversalArea);
                if (reversalPoint is not null) { ++returnFrames; return; }
                if (state.Window.X - expanded.X < expanded.Width * .35) return;
                reversalPoint = state.Window;
                var visible = state.VisiblePart!.Value;
                controller.ProbePointer(visible.X + visible.Width / 2, visible.Y + visible.Height / 2,
                    TimeSpan.FromMilliseconds(100), animate: true);
                Assert(controller.IsAnimating && controller.ReadNativeState().Window == reversalPoint.Value,
                    "悬停反向动画先瞬移到了起点或终点。");
                reversed.TrySetResult(controller.AnimateForProbeAsync(false));
            }
            catch (Exception exception) { reversalFailure = exception; reversed.TrySetException(exception); }
        }
        controller.AnimationFrameForCheck += ReverseOnHover;
        try
        {
            var hiding = controller.AnimateForProbeAsync(true);
            var returning = await reversed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(!await hiding.WaitAsync(TimeSpan.FromSeconds(3)), "反向展开没有取消原收起动画。");
            Assert(await returning.WaitAsync(TimeSpan.FromSeconds(3)), "反向展开未抵达展开位置。");
            if (reversalFailure is not null) throw reversalFailure;
            Assert(returnFrames >= 2 && !controller.IsCollapsed && !controller.IsAnimating &&
                !controller.ReadNativeState().HasNativeRegion && controller.ReadNativeState().Window == expanded,
                "反向展开没有产生连续中间帧或没有回到原展开锚点。");
            checks.Add("PASS 动画收起途中悬停从当前原生位置连续反向展开");
        }
        finally { controller.AnimationFrameForCheck -= ReverseOnHover; controller.Restore(); }

        Assert(controller.ProbeDock("right", true), "展开时离开验证无法收起窗口。");
        var hidden = controller.ReadNativeState().Window;
        var rehiding = new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? rehideFailure = null;
        EdgeRectangle? rehidePoint = null;
        var rehideFrames = 0;
        void ReverseOnLeave()
        {
            try
            {
                var state = controller.ReadNativeState();
                AssertAnimationClip(state, reversalArea);
                if (rehidePoint is not null) { ++rehideFrames; return; }
                if (hidden.X - state.Window.X < hidden.Width * .35) return;
                rehidePoint = state.Window;
                controller.ProbePointer(state.Window.X - 60, state.Window.Y + state.Window.Height / 2,
                    TimeSpan.Zero, animate: true);
                Assert(controller.IsAnimating && controller.ReadNativeState().Window == rehidePoint.Value,
                    "展开中离开没有立即从当前原生位置平滑反向。");
                rehiding.TrySetResult(controller.AnimateForProbeAsync(true));
            }
            catch (Exception exception) { rehideFailure = exception; rehiding.TrySetException(exception); }
        }
        controller.AnimationFrameForCheck += ReverseOnLeave;
        try
        {
            var strip = controller.ReadNativeState().VisiblePart!.Value;
            controller.ProbePointer(strip.X + strip.Width / 2, strip.Y + strip.Height / 2, TimeSpan.Zero, animate: true);
            var opening = controller.AnimateForProbeAsync(false);
            var hidingAgain = await rehiding.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(!await opening.WaitAsync(TimeSpan.FromSeconds(3)), "离开没有取消尚未完成的展开动画。");
            Assert(await hidingAgain.WaitAsync(TimeSpan.FromSeconds(3)), "反向收起未抵达小条位置。");
            if (rehideFailure is not null) throw rehideFailure;
            Assert(rehideFrames >= 2 && controller.IsCollapsed && !controller.IsAnimating &&
                controller.ReadNativeState().Window == hidden, "展开中离开后的收起缺少中间帧或终点漂移。");
            AssertNativeStrip(controller.ReadNativeState(), "right", window);
            checks.Add("PASS 动画展开途中离开在同次输入立即从当前位置反向收起");
        }
        finally { controller.AnimationFrameForCheck -= ReverseOnLeave; controller.Restore(); }

        foreach (var operation in new[] { "suspend", "drag", "disable", "modal", "scale" })
        {
            controller.SetEnabled(true);
            Assert(controller.ProbeDock("right", false), "取消验证无法吸附窗口。");
            var running = await StartAndWaitForIntermediateAsync(controller);
            switch (operation)
            {
                case "suspend": controller.Suspend(); break;
                case "drag": controller.OnDragStarted(); break;
                case "disable": controller.SetEnabled(false); break;
                case "modal":
                    controller.ProbePointer(0, 0, TimeSpan.Zero, interactionBlocked: true, animate: true);
                    break;
                case "scale":
                    window.SetScale(Math.Clamp(controller.ConfiguredScaleForCheck * .92, .5, 1.6));
                    await SettleAsync(window);
                    controller.OnGeometryChanged();
                    break;
            }
            Assert(!await running.WaitAsync(TimeSpan.FromSeconds(3)), "交互动作没有取消尚未完成的动画。");
            Assert(!controller.IsAnimating && !controller.IsCollapsed && !controller.ReadNativeState().HasNativeRegion,
                "交互动作后仍有动画或离屏裁剪。");
            if (operation == "suspend") controller.Resume();
            if (operation == "drag") controller.OnDragFinished();
            if (operation == "disable") controller.SetEnabled(true);
            checks.Add($"PASS 动画 {operation} 安全取消并立即恢复完整窗口");
        }

        window.SetAlwaysOnTop(true);
        await SettleAsync(window);
        controller.OnGeometryChanged();
        Assert(controller.ProbeDock("left", false), "置顶悬浮动画验证无法吸附窗口。");
        var topmostArea = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            controller.ReadNativeState().Window.X, controller.ReadNativeState().Window.Y)).WorkingArea;
        await ObserveMotionAsync(controller, () => controller.AnimateForProbeAsync(true), topmostArea, collapse: true);
        Assert(window.Topmost && !controller.ReadNativeState().IsDesktopChild, "动画窗口失去顶层悬浮或置顶状态。");
        await ObserveMotionAsync(controller, () => controller.AnimateForProbeAsync(false), topmostArea, collapse: false);
        window.SetAlwaysOnTop(false);
        checks.Add("PASS 动画普通顶层悬浮置顶时逐帧裁剪与展开");

        using (var disposable = new EdgeAutoHideController(window, UnifiedSettings.Load(preview: true), preview: true))
        using (disposable.HoldPointerForProbe())
        {
            Assert(disposable.ProbeDock("right", false), "释放验证无法吸附窗口。");
            var running = await StartAndWaitForIntermediateAsync(disposable);
            disposable.Dispose();
            Assert(!await running.WaitAsync(TimeSpan.FromSeconds(3)) && !disposable.HasActiveTimersForCheck &&
                !disposable.IsAnimating && !disposable.IsCollapsed && !disposable.ReadNativeState().HasNativeRegion,
                "释放后仍保留动画、定时器或原生裁剪。");
            Assert(!await disposable.AnimateForProbeAsync(true), "释放后仍能重启动画。");
        }
        controller.OnGeometryChanged();
        checks.Add("PASS 动画 Dispose 取消完成任务、停止所有定时器并清除 region");
        return checks.AsReadOnly();
    }

    private static async Task ObserveMotionAsync(EdgeAutoHideController controller, Func<Task<bool>> begin,
        System.Drawing.Rectangle area, bool collapse)
    {
        var initial = controller.ReadNativeState().Window;
        var frames = new List<EdgeRectangle>();
        Exception? failure = null;
        void Frame()
        {
            try
            {
                var state = controller.ReadNativeState();
                AssertAnimationClip(state, area);
                frames.Add(state.Window);
            }
            catch (Exception exception) { failure ??= exception; }
        }
        controller.AnimationFrameForCheck += Frame;
        try
        {
            var completed = begin();
            Assert(controller.IsAnimating && controller.ReadNativeState().Window == initial,
                "动画开始时立即跳到了终点。");
            Assert(await completed.WaitAsync(TimeSpan.FromSeconds(3)), "真实定时动画未成功完成。");
            if (failure is not null) throw failure;
            var final = controller.ReadNativeState();
            Assert(frames.Count >= 3 && frames.Exists(frame => frame != initial && frame != final.Window),
                "真实定时动画缺少实际原生中间帧。");
            Assert(!controller.IsAnimating && controller.IsCollapsed == collapse &&
                !controller.HasAnimationFrameSubscriptionForCheck &&
                (collapse || !final.HasNativeRegion), "动画终点状态、渲染订阅释放或展开清除 region 失败。");
        }
        finally { controller.AnimationFrameForCheck -= Frame; }
    }

    private static async Task<Task<bool>> StartAndWaitForIntermediateAsync(EdgeAutoHideController controller)
    {
        var initial = controller.ReadNativeState().Window;
        var intermediate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Frame()
        {
            if (controller.IsAnimating && controller.ReadNativeState().Window != initial) intermediate.TrySetResult(true);
        }
        controller.AnimationFrameForCheck += Frame;
        try
        {
            var running = controller.AnimateForProbeAsync(true);
            await intermediate.Task.WaitAsync(TimeSpan.FromSeconds(3));
            return running;
        }
        finally { controller.AnimationFrameForCheck -= Frame; }
    }

    private static void AssertAnimationClip(EdgeDockNativeState state, System.Drawing.Rectangle area)
    {
        Assert(!state.IsDesktopChild, "动画中的悬浮组件意外变为子窗口。");
        if (state.Region is { } region)
        {
            var visible = state.VisiblePart!.Value;
            Assert(region.X == visible.X - state.Window.X && region.Y == visible.Y - state.Window.Y &&
                region.Width == visible.Width && region.Height == visible.Height,
                "动画中间帧 region 与实际工作区交集不符。");
            Assert(state.Window.X + region.X >= area.X && state.Window.Y + region.Y >= area.Y &&
                state.Window.X + region.Right <= area.Right && state.Window.Y + region.Bottom <= area.Bottom,
                "动画中间帧泄漏到相邻显示器或任务栏。");
        }
        else
        {
            Assert(state.Window.X >= area.X && state.Window.Y >= area.Y && state.Window.Right <= area.Right &&
                state.Window.Bottom <= area.Bottom, "未裁剪的动画终点仍处于工作区外。");
        }
    }

    private static async Task SettleAsync(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }

    private static void AssertNativeStrip(EdgeDockNativeState state, string edge, MainWindow window)
    {
        Assert(!state.IsDesktopChild, "收起中的悬浮组件意外变为子窗口。");
        Assert(state.Region is not null && state.VisiblePart is not null, "收起窗口没有实际 native region 或可见小条。");
        var region = state.Region!.Value;
        var visible = state.VisiblePart!.Value;
        var dpi = VisualTreeHelper.GetDpi(window);
        var expected = (int)Math.Round(8 * (edge is "top" or "bottom" ? dpi.DpiScaleY : dpi.DpiScaleX), MidpointRounding.AwayFromZero);
        Assert((edge is "top" or "bottom" ? visible.Height : visible.Width) == expected,
            "实际可见小条尺寸与八个 DIP 不符。");
        Assert(region.X == visible.X - state.Window.X && region.Y == visible.Y - state.Window.Y &&
            region.Width == visible.Width && region.Height == visible.Height,
            "实际 native region 没有限制到可见小条。");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
