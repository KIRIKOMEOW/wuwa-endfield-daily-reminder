using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GameDailyWidget;

/// <summary>
/// Four-edge auto hide in physical screen coordinates. The window's native region
/// limits a collapsed card to its small visible strip, including between monitors.
/// </summary>
public sealed class EdgeAutoHideController : IDisposable
{
    private const double AnimationSeconds = .24;
    private readonly MainWindow _window;
    private readonly UnifiedSettings _settings;
    private readonly bool _preview;
    private readonly DispatcherTimer _pointerTimer;
    private bool _renderingSubscribed;
    private TimeSpan? _lastRenderingTime;
    internal string LastAnimationStopForCheck { get; private set; } = "none";
    private readonly Func<(int X, int Y)?> _positionProvider;
    private readonly Func<(int X, int Y)?>? _previousPositionProvider;
    private EdgeDockSide _edge;
    private EdgeRectangle? _expanded;
    private EdgeRectangle? _workingArea;
    private double _scaleX = 1;
    private double _scaleY = 1;
    private int _suspensions;
    private bool _enabled;
    private bool _collapsed;
    private bool _dragging;
    private bool _moving;
    private bool _regionApplied;
    private bool _probeActive;
    private int _pointerProbeHolds;
    private bool _disposed;
    private Motion? _motion;
    private TaskCompletionSource<bool>? _motionCompletion;
    private (int X, int Y, string Edge)? _lastPersisted;

    public EdgeAutoHideController(MainWindow window, UnifiedSettings settings, bool preview)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _preview = preview;
        _enabled = preview || settings.EdgeAutoHide;
        _edge = preview ? EdgeDockSide.None : EdgeDockGeometry.Parse(settings.DockedEdge);
        _positionProvider = () => ExpandedPosition;
        _previousPositionProvider = window.ExpandedPositionProvider;
        window.ExpandedPositionProvider = _positionProvider;
        _pointerTimer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _pointerTimer.Tick += OnPointerTick;
        window.Loaded += OnLoaded;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.Closed += OnClosed;
        window.MouseEnter += OnWindowPointerInput;
        window.MouseLeave += OnWindowPointerInput;
        window.AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler(OnWindowPointerInput), handledEventsToo: true);
        if (window.IsLoaded) InitializeGeometry();
    }

    public bool Enabled => _enabled;
    public bool IsCollapsed => _collapsed;
    public bool IsAnimating => _motion is not null;
    public string DockedEdge => EdgeDockGeometry.Name(_edge);
    public (int X, int Y)? ExpandedPosition => !_dragging && _expanded is { } expanded && _edge != EdgeDockSide.None
        ? (expanded.X, expanded.Y) : null;

    public EdgeDockNativeState ReadNativeState()
    {
        if (!TryReadWindow(out var rectangle)) throw new InvalidOperationException("验证窗口没有有效的原生边界。");
        var handle = new WindowInteropHelper(_window).Handle;
        EdgeRectangle? nativeRegion = null;
        var copy = CreateRectRgn(0, 0, 0, 0);
        if (copy != nint.Zero)
        {
            try
            {
                if (GetWindowRgn(handle, copy) != 0 && GetRgnBox(copy, out var box) != 0)
                    nativeRegion = new EdgeRectangle(box.Left, box.Top,
                        Math.Max(0, box.Right - box.Left), Math.Max(0, box.Bottom - box.Top));
            }
            finally { DeleteObject(copy); }
        }
        var child = (GetWindowLongPtr(handle, -16).ToInt64() & 0x40000000L) != 0;
        return new EdgeDockNativeState(rectangle, nativeRegion,
            _workingArea is { } area ? EdgeDockGeometry.VisiblePart(rectangle, area) : null, child);
    }

    public void OnDragStarted()
    {
        if (_disposed) return;
        Restore();
        _dragging = true;
    }

    public void OnDragFinished()
    {
        if (_disposed) return;
        _dragging = false;
        _probeActive = false;
        ReadAndSnapCurrentGeometry(preserveEdge: false);
        PersistExpandedPosition();
    }

    public void OnGeometryChanged()
    {
        if (_disposed || _moving || _dragging || !_window.IsLoaded) return;
        if (!TryReadWindow(out var actual)) return;
        var wasAnimating = _motion is not null;
        StopAnimation();
        if (_edge == EdgeDockSide.None || _expanded is null)
        {
            ReadAndSnapCurrentGeometry(preserveEdge: false);
        }
        else
        {
            var area = WorkingAreaFor(_expanded.Value);
            var anchor = EdgeDockGeometry.ReflowExpanded(_expanded.Value, actual.Width, actual.Height, area, _edge);
            _expanded = anchor;
            _workingArea = area;
            if (_collapsed && !wasAnimating)
            {
                // Native DPI changes can occur when a hidden window's body crosses
                // a neighbouring monitor. Keep it hidden; reflow and reclip its new
                // physical size. Restore will update the expanded monitor's DPI.
                MoveCollapsed(anchor, area);
            }
            else
            {
                UpdateScale();
                MoveExpanded(anchor);
            }
        }
        PersistExpandedPosition();
    }

    public void Restore()
    {
        if (_disposed) return;
        StopAnimation();
        _probeActive = false;
        if (_expanded is not { } anchor || _edge == EdgeDockSide.None)
        {
            ClearRegion();
            _collapsed = false;
            _window.SetCollapsedSkinStrip(false);
            _window.AllowOffscreenForAutoHide = false;
            return;
        }
        var area = WorkingAreaFor(anchor);
        if (TryReadWindow(out var actual))
            anchor = EdgeDockGeometry.ReflowExpanded(anchor, actual.Width, actual.Height, area, _edge);
        else anchor = EdgeDockGeometry.SnapExpanded(anchor, area, _edge);
        _expanded = anchor;
        _workingArea = area;
        MoveExpanded(anchor);
        UpdateScale();
        // Reflect synchronous native size/DPI changes before SavePosition reads us.
        if (TryReadWindow(out var expandedActual) &&
            (expandedActual.Width != anchor.Width || expandedActual.Height != anchor.Height))
        {
            anchor = EdgeDockGeometry.ReflowExpanded(anchor, expandedActual.Width, expandedActual.Height, area, _edge);
            _expanded = anchor;
            MoveExpanded(anchor);
        }
    }

    public void Suspend()
    {
        if (_disposed) return;
        ++_suspensions;
        Restore();
    }

    public void Resume()
    {
        if (_disposed) return;
        if (_suspensions > 0) --_suspensions;
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed) return;
        Restore();
        _enabled = enabled;
        if (!enabled)
        {
            _edge = EdgeDockSide.None;
            _expanded = null;
            _workingArea = null;
        }
        else ReadAndSnapCurrentGeometry(preserveEdge: false);
        if (!_preview)
        {
            _settings.EdgeAutoHide = enabled;
            PersistExpandedPosition(force: true);
        }
        UpdateTimer();
    }

    /// <summary>
    /// Synchronous window-only lifecycle probe. It never moves the user's mouse,
    /// reads accounts or saves preview preferences. Polling is held until Restore.
    /// </summary>
    public bool ProbeDock(string edge, bool collapse = true)
    {
        if (_disposed || !TryReadWindow(out var actual)) return false;
        var side = EdgeDockGeometry.Parse(edge);
        if (side == EdgeDockSide.None) return false;
        Restore();
        if (!TryReadWindow(out actual)) return false;
        _edge = side;
        _workingArea = WorkingAreaFor(actual);
        _expanded = EdgeDockGeometry.SnapExpanded(actual, _workingArea.Value, side);
        UpdateScale();
        MoveExpanded(_expanded.Value);
        _probeActive = true;
        return !collapse || MoveCollapsed(_expanded.Value, _workingArea.Value);
    }

    internal Task<bool> AnimateForProbeAsync(bool collapse)
    {
        if (!_preview) throw new InvalidOperationException("动画验证仅支持隔离预览模式。");
        _probeActive = true;
        return BeginAnimation(collapse, bypassInput: true);
    }

    internal event Action? AnimationFrameForCheck;

    private void InitializeGeometry()
    {
        if (_disposed) return;
        if (_enabled) ReadAndSnapCurrentGeometry(preserveEdge: true);
        UpdateTimer();
    }

    private void ReadAndSnapCurrentGeometry(bool preserveEdge)
    {
        if (!TryReadWindow(out var actual)) return;
        UpdateScale();
        var area = WorkingAreaFor(actual);
        var detected = _enabled ? EdgeDockGeometry.DetectNearestEdge(actual, area, _scaleX, _scaleY) : EdgeDockSide.None;
        if (preserveEdge && _edge != EdgeDockSide.None && detected != EdgeDockSide.None)
        {
            var storedAnchor = EdgeDockGeometry.SnapExpanded(actual, area, _edge);
            var thresholdX = EdgeDockGeometry.DefaultThresholdDip * _scaleX;
            var thresholdY = EdgeDockGeometry.DefaultThresholdDip * _scaleY;
            if (Math.Abs((long)storedAnchor.X - actual.X) <= thresholdX &&
                Math.Abs((long)storedAnchor.Y - actual.Y) <= thresholdY) detected = _edge;
        }
        _edge = detected;
        _workingArea = detected == EdgeDockSide.None ? null : area;
        _expanded = detected == EdgeDockSide.None ? null : EdgeDockGeometry.SnapExpanded(actual, area, detected);
        if (_expanded is { } anchor) MoveExpanded(anchor);
        else
        {
            ClearRegion();
            _collapsed = false;
            _window.SetCollapsedSkinStrip(false);
            _window.AllowOffscreenForAutoHide = false;
        }
    }

    private void OnPointerTick(object? sender, EventArgs e)
    {
        if (!CanEvaluatePhysicalPointer()) return;
        try
        {
            var mouse = Forms.Control.MousePosition;
            ProcessPointer(mouse.X, mouse.Y,
                _suspensions > 0 || IsWidgetInteractionBlocked(), Forms.Control.MouseButtons != Forms.MouseButtons.None,
                animate: true, pointerOverWidget: IsPointerOverWidget(mouse.X, mouse.Y));
        }
        catch
        {
            // No unhandled timer error should strand the only visible grip.
            Restore();
        }
    }

    private bool CanEvaluatePhysicalPointer() => !_disposed && _enabled && _window.IsVisible &&
        !_probeActive && _pointerProbeHolds == 0 && !_moving;

    private void OnWindowPointerInput(object sender, MouseEventArgs e)
    {
        if (!CanEvaluatePhysicalPointer()) return;
        try
        {
            // Use this WPF input position, rather than an OS cursor that may have
            // advanced ahead of a batched input stream. PointToScreen is physical.
            var point = _window.PointToScreen(e.GetPosition(_window));
            var pressed = e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed ||
                e.MiddleButton == MouseButtonState.Pressed || Forms.Control.MouseButtons != Forms.MouseButtons.None;
            var x = (int)Math.Round(point.X);
            var y = (int)Math.Round(point.Y);
            ProcessPointer(x, y, _suspensions > 0 || IsWidgetInteractionBlocked(), pressed,
                animate: true, pointerOverWidget: IsPointerOverWidget(x, y));
        }
        catch { Restore(); }
    }

    // Same state machine as the physical timer. A lifecycle check supplies virtual
    // coordinates/time and leaves the user's real cursor and input entirely alone.
    internal void ProbePointer(int x, int y, TimeSpan elapsed, bool interactionBlocked = false,
        bool buttonsPressed = false, bool animate = false, bool pointerOverWidget = true)
    {
        if (_disposed) return;
        _probeActive = true;
        ProcessPointer(x, y, interactionBlocked, buttonsPressed, animate, pointerOverWidget);
    }

    internal bool InteractionBlockedForCheck => IsWidgetInteractionBlocked();
    internal double ConfiguredScaleForCheck => _settings.Scale;
    internal bool HasActiveTimersForCheck => _pointerTimer.IsEnabled || _renderingSubscribed;
    internal bool HasAnimationFrameSubscriptionForCheck => _renderingSubscribed;
    internal bool IsPointerOverWidgetForCheck(int x, int y) => IsPointerOverWidget(x, y);
    internal IDisposable HoldPointerForProbe()
    {
        ++_pointerProbeHolds;
        return new ProbeHold(this);
    }

    private void ProcessPointer(int mouseX, int mouseY, bool interactionBlocked, bool buttonsPressed,
        bool animate, bool pointerOverWidget)
    {
        if (interactionBlocked)
        {
            if (_collapsed || _motion is not null) Restore();
            return;
        }
        if (_edge == EdgeDockSide.None || _expanded is null || _workingArea is null) return;
        var currentArea = WorkingAreaFor(_expanded.Value);
        if (currentArea != _workingArea.Value)
        {
            OnGeometryChanged();
            return;
        }
        if (!TryReadWindow(out var actual)) return;
        if (_motion is { } motion)
        {
            var visible = EdgeDockGeometry.VisiblePart(actual, _workingArea.Value);
            if (buttonsPressed && pointerOverWidget) { Restore(); return; }
            var inside = pointerOverWidget && visible.Contains(mouseX, mouseY);
            if (inside && motion.Collapse) _ = BeginAnimation(false, bypassInput: _probeActive);
            else if (!inside && !motion.Collapse) _ = BeginAnimation(true, bypassInput: _probeActive);
            return;
        }
        if (_collapsed)
        {
            if (buttonsPressed) return; // Do not open under another application's drag.
            var strip = EdgeDockGeometry.VisiblePart(actual, _workingArea.Value);
            if (pointerOverWidget && strip.Contains(mouseX, mouseY))
            {
                if (animate) _ = BeginAnimation(false, bypassInput: _probeActive);
                else Restore();
            }
            return;
        }
        if (actual.X != _expanded.Value.X || actual.Y != _expanded.Value.Y ||
            actual.Width != _expanded.Value.Width || actual.Height != _expanded.Value.Height)
        {
            ReadAndSnapCurrentGeometry(preserveEdge: false);
            return;
        }
        if (pointerOverWidget && (actual.Contains(mouseX, mouseY) || buttonsPressed))
            return;
        if (animate) _ = BeginAnimation(true, bypassInput: _probeActive);
        else MoveCollapsed(_expanded.Value, _workingArea.Value);
    }

    private bool IsWidgetInteractionBlocked()
    {
        if (_dragging || _window.IsInteractionActive || !_window.IsEnabled || Mouse.Captured is not null) return true;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != nint.Zero && !IsWindowEnabled(handle)) return true;
        var application = Application.Current;
        return application is not null && application.Windows.Cast<Window>().Any(window =>
            !ReferenceEquals(window, _window) && window.IsVisible);
    }

    private bool IsPointerOverWidget(int x, int y)
    {
        var widget = new WindowInteropHelper(_window).Handle;
        if (widget == nint.Zero) return false;
        var hit = WindowFromPoint(new NativePoint { X = x, Y = y });
        // Coordinate overlap is not a hover when another application's window
        // covers the strip. Resolve the actual visible input target first.
        return hit != nint.Zero && (hit == widget || GetAncestor(hit, 2) == widget);
    }

    private Task<bool> BeginAnimation(bool collapse, bool bypassInput = false)
    {
        if (_disposed || !_enabled || _expanded is not { } anchor || _workingArea is not { } area ||
            !TryReadWindow(out var actual)) return Task.FromResult(false);
        if (_motion is { } existing && existing.Collapse == collapse)
            return _motionCompletion!.Task;
        // A reversal starts exactly at the last physical frame, rather than
        // snapping to either endpoint. Only its remaining distance is animated.
        StopAnimation();
        if (!collapse) _window.SetCollapsedSkinStrip(false);
        var hidden = EdgeDockGeometry.Collapse(anchor, area, _edge, _scaleX, _scaleY);
        var target = collapse ? hidden : anchor;
        if (actual.X == target.X && actual.Y == target.Y)
        {
            var success = collapse ? MoveCollapsed(anchor, area) : CompleteExpanded(anchor);
            return Task.FromResult(success);
        }
        var fullDistance = Math.Max(Math.Abs((long)hidden.X - anchor.X), Math.Abs((long)hidden.Y - anchor.Y));
        var remaining = Math.Max(Math.Abs((long)target.X - actual.X), Math.Abs((long)target.Y - actual.Y));
        var seconds = AnimationSeconds * Math.Clamp((double)remaining / Math.Max(1, fullDistance), .35, 1);
        _motion = new Motion(actual, target, area, collapse, Stopwatch.GetTimestamp(), seconds, bypassInput);
        LastAnimationStopForCheck = "running";
        _motionCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.AllowOffscreenForAutoHide = true;
        _lastRenderingTime = null;
        _renderingSubscribed = true;
        CompositionTarget.Rendering += OnAnimationRendering;
        _window.InvalidateVisual();
        return _motionCompletion.Task;
    }

    private void OnAnimationRendering(object? sender, EventArgs e)
    {
        if (_disposed || !_renderingSubscribed || _motion is not { } motion) return;
        // Rendering can be raised more than once for one presentation frame.
        // Advance the native window once per distinct frame, in phase with WPF.
        if (e is RenderingEventArgs frame)
        {
            if (_lastRenderingTime == frame.RenderingTime) return;
            _lastRenderingTime = frame.RenderingTime;
        }
        var elapsed = Stopwatch.GetElapsedTime(motion.StartedAt).TotalSeconds;
        try
        {
            if (!_window.IsVisible || (!motion.BypassInput &&
                (_suspensions > 0 || IsWidgetInteractionBlocked())))
            {
                LastAnimationStopForCheck = "visibility/input";
                Restore();
                return;
            }
            if (!TryReadWindow(out var actual)) { LastAnimationStopForCheck = "native window unavailable"; Restore(); return; }
            if (actual.Width != motion.From.Width || actual.Height != motion.From.Height ||
                _expanded is null || WorkingAreaFor(_expanded.Value) != motion.Area)
            {
                LastAnimationStopForCheck = $"geometry {actual.Width}x{actual.Height} / {motion.From.Width}x{motion.From.Height}";
                OnGeometryChanged();
                return;
            }
            var progress = Math.Clamp(elapsed / motion.Seconds, 0, 1);
            var next = EdgeDockGeometry.Interpolate(motion.From, motion.To, progress);
            if (!MoveClippedFrame(actual, next, motion.Area)) { LastAnimationStopForCheck = "native move/clip"; Restore(); return; }
            if (progress >= 1)
            {
                var success = motion.Collapse
                    ? MoveCollapsed(_expanded.Value, motion.Area) : CompleteExpanded(_expanded.Value);
                StopAnimation(success);
                if (!success) Restore();
            }
            AnimationFrameForCheck?.Invoke();
            // Native HWND movement alone may leave WPF's scene unchanged and
            // its render loop throttled. Keep the scene active only in motion.
            if (_motion is not null && _renderingSubscribed) _window.InvalidateVisual();
        }
        catch (Exception error)
        {
            LastAnimationStopForCheck = error.GetType().Name;
            // Native move/region errors must never leave an unusable offscreen card.
            Restore();
        }
    }

    private bool MoveClippedFrame(EdgeRectangle current, EdgeRectangle next, EdgeRectangle area)
    {
        var oldVisible = EdgeDockGeometry.VisiblePart(current, area);
        var nextVisible = EdgeDockGeometry.VisiblePart(next, area);
        if (oldVisible.Width <= 0 || oldVisible.Height <= 0 || nextVisible.Width <= 0 || nextVisible.Height <= 0)
            return false;
        // A region legal at BOTH positions prevents a one-frame leak while native
        // position and clipping are changed separately, especially when expanding
        // from a shared edge onto a neighbouring monitor.
        var safeLeft = Math.Max(oldVisible.X - current.X, nextVisible.X - next.X);
        var safeTop = Math.Max(oldVisible.Y - current.Y, nextVisible.Y - next.Y);
        var safeRight = Math.Min(oldVisible.Right - current.X, nextVisible.Right - next.X);
        var safeBottom = Math.Min(oldVisible.Bottom - current.Y, nextVisible.Bottom - next.Y);
        if (safeRight <= safeLeft || safeBottom <= safeTop) return false;
        _moving = true;
        _window.AllowOffscreenForAutoHide = true;
        try
        {
            if (!ApplyRegion(safeLeft, safeTop, checked((int)(safeRight - safeLeft)),
                checked((int)(safeBottom - safeTop)), redraw: false)) return false;
            FloatingWindowPlacement.MoveToScreen(_window, next.X, next.Y);
            if (!TryReadWindow(out var actual) || Math.Abs((long)actual.X - next.X) > 1 ||
                Math.Abs((long)actual.Y - next.Y) > 1) return false;
            var visible = EdgeDockGeometry.VisiblePart(actual, area);
            return visible.Width > 0 && visible.Height > 0 && ApplyRegion(
                visible.X - actual.X, visible.Y - actual.Y, visible.Width, visible.Height);
        }
        finally { _moving = false; }
    }

    private bool CompleteExpanded(EdgeRectangle anchor)
    {
        MoveExpanded(anchor);
        UpdateScale();
        return TryReadWindow(out var actual) && actual.X == anchor.X && actual.Y == anchor.Y;
    }

    private void StopAnimation(bool completed = false)
    {
        if (_renderingSubscribed)
        {
            CompositionTarget.Rendering -= OnAnimationRendering;
            _renderingSubscribed = false;
        }
        _lastRenderingTime = null;
        _motion = null;
        var completion = _motionCompletion;
        _motionCompletion = null;
        completion?.TrySetResult(completed);
    }

    private sealed record Motion(EdgeRectangle From, EdgeRectangle To, EdgeRectangle Area,
        bool Collapse, long StartedAt, double Seconds, bool BypassInput);

    private bool MoveCollapsed(EdgeRectangle anchor, EdgeRectangle area)
    {
        var collapsed = EdgeDockGeometry.Collapse(anchor, area, _edge, _scaleX, _scaleY);
        var visible = EdgeDockGeometry.VisiblePart(collapsed, area);
        if (visible.Width <= 0 || visible.Height <= 0) return false;
        _moving = true;
        _window.AllowOffscreenForAutoHide = true;
        try
        {
            // Clip before moving so no card body flashes onto an adjacent monitor.
            if (!ApplyRegion(visible.X - collapsed.X, visible.Y - collapsed.Y, visible.Width, visible.Height))
            {
                ClearRegion();
                _window.AllowOffscreenForAutoHide = false;
                return false;
            }
            FloatingWindowPlacement.MoveToScreen(_window, collapsed.X, collapsed.Y);
            if (!TryReadWindow(out var actual) || Math.Abs((long)actual.X - collapsed.X) > 1 ||
                Math.Abs((long)actual.Y - collapsed.Y) > 1)
            {
                MoveExpanded(anchor);
                return false;
            }
            _collapsed = true;
            _window.SetTwinStripEdge(_edge);
            _window.SetCollapsedSkinStrip(true);
            return true;
        }
        finally { _moving = false; }
    }

    private void MoveExpanded(EdgeRectangle anchor)
    {
        _moving = true;
        // The flag also suppresses already-queued clamp callbacks during restore.
        _window.AllowOffscreenForAutoHide = true;
        try
        {
            FloatingWindowPlacement.MoveToScreen(_window, anchor.X, anchor.Y);
            ClearRegion();
            _collapsed = false;
            _window.SetCollapsedSkinStrip(false);
        }
        finally
        {
            _window.AllowOffscreenForAutoHide = false;
            _moving = false;
        }
    }

    private void UpdateScale()
    {
        var dpi = VisualTreeHelper.GetDpi(_window);
        _scaleX = dpi.DpiScaleX;
        _scaleY = dpi.DpiScaleY;
    }

    private static EdgeRectangle WorkingAreaFor(EdgeRectangle rectangle)
    {
        var screen = Forms.Screen.FromRectangle(new Drawing.Rectangle(rectangle.X, rectangle.Y,
            Math.Max(1, rectangle.Width), Math.Max(1, rectangle.Height)));
        var work = screen.WorkingArea;
        return new EdgeRectangle(work.X, work.Y, work.Width, work.Height);
    }

    private bool TryReadWindow(out EdgeRectangle rectangle)
    {
        rectangle = default;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == nint.Zero || !GetWindowRect(handle, out var native)) return false;
        var width = native.Right - native.Left;
        var height = native.Bottom - native.Top;
        if (width <= 0 || height <= 0) return false;
        // GetWindowRect already gives physical top-level placement; do not query
        // the same native rectangle a second time on every animation frame.
        rectangle = new EdgeRectangle(native.Left, native.Top, width, height);
        return true;
    }

    private bool ApplyRegion(int left, int top, int width, int height, bool redraw = true)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        var region = CreateRectRgn(left, top, checked(left + width), checked(top + height));
        if (region == nint.Zero) return false;
        if (SetWindowRgn(handle, region, redraw) == 0)
        {
            DeleteObject(region);
            return false;
        }
        // Windows takes ownership of a region after a successful SetWindowRgn.
        _regionApplied = true;
        return true;
    }

    private void ClearRegion()
    {
        if (!_regionApplied) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != nint.Zero) SetWindowRgn(handle, nint.Zero, true);
        _regionApplied = false;
    }

    private void PersistExpandedPosition(bool force = false)
    {
        if (_preview || !TryReadWindow(out var actual)) return;
        var position = ExpandedPosition ?? (actual.X, actual.Y);
        var value = (position.X, position.Y, DockedEdge);
        if (!force && _lastPersisted == value) return;
        _settings.Left = position.X;
        _settings.Top = position.Y;
        _settings.DockedEdge = _edge == EdgeDockSide.None ? null : DockedEdge;
        _settings.Save();
        _lastPersisted = value;
    }

    private void UpdateTimer()
    {
        if (_disposed) return;
        if (_enabled && _window.IsLoaded && _window.IsVisible) _pointerTimer.Start();
        else _pointerTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => InitializeGeometry();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        Restore();
        UpdateTimer();
    }
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private sealed class ProbeHold(EdgeAutoHideController owner) : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (owner._pointerProbeHolds > 0) --owner._pointerProbeHolds;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Restore();
        _disposed = true;
        _pointerTimer.Stop();
        _pointerTimer.Tick -= OnPointerTick;
        _window.Loaded -= OnLoaded;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.Closed -= OnClosed;
        _window.MouseEnter -= OnWindowPointerInput;
        _window.MouseLeave -= OnWindowPointerInput;
        _window.RemoveHandler(Mouse.MouseMoveEvent, new MouseEventHandler(OnWindowPointerInput));
        _window.AllowOffscreenForAutoHide = false;
        if (ReferenceEquals(_window.ExpandedPositionProvider, _positionProvider))
            _window.ExpandedPositionProvider = _previousPositionProvider;
        ClearRegion();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out NativeRect rectangle);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}

public sealed record EdgeDockNativeState(EdgeRectangle Window, EdgeRectangle? Region,
    EdgeRectangle? VisiblePart, bool IsDesktopChild)
{
    public bool HasNativeRegion => Region is not null;
}
