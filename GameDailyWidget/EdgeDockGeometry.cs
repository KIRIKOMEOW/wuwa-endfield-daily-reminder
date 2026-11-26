using System;
using System.Collections.Generic;

namespace GameDailyWidget;

public enum EdgeDockSide { None, Top, Right, Bottom, Left }

/// <summary>Screen coordinates and sizes in physical pixels, including negative monitor origins.</summary>
public readonly record struct EdgeRectangle(int X, int Y, int Width, int Height)
{
    public long Right => (long)X + Width;
    public long Bottom => (long)Y + Height;
    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
}

public static class EdgeDockGeometry
{
    public const double DefaultThresholdDip = 12;
    public const double DefaultStripDip = 8;

    public static EdgeDockSide DetectNearestEdge(EdgeRectangle window, EdgeRectangle workingArea,
        double scaleX = 1, double scaleY = 1, double thresholdDip = DefaultThresholdDip)
    {
        Validate(window, workingArea, scaleX, scaleY);
        if (!double.IsFinite(thresholdDip) || thresholdDip < 0) throw new ArgumentOutOfRangeException(nameof(thresholdDip));
        var horizontalOverlap = Math.Min(window.Right, workingArea.Right) - Math.Max(window.X, workingArea.X);
        var verticalOverlap = Math.Min(window.Bottom, workingArea.Bottom) - Math.Max(window.Y, workingArea.Y);
        // Explicit order gives deterministic corner ties: top, right, bottom, left.
        var candidates = new List<(EdgeDockSide Side, double Distance)>();
        if (horizontalOverlap > 0)
        {
            candidates.Add((EdgeDockSide.Top, Math.Abs((long)window.Y - workingArea.Y) / scaleY));
            candidates.Add((EdgeDockSide.Bottom, Math.Abs(window.Bottom - workingArea.Bottom) / scaleY));
        }
        if (verticalOverlap > 0)
        {
            candidates.Add((EdgeDockSide.Right, Math.Abs(window.Right - workingArea.Right) / scaleX));
            candidates.Add((EdgeDockSide.Left, Math.Abs((long)window.X - workingArea.X) / scaleX));
        }
        var best = EdgeDockSide.None;
        var distance = double.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            if (candidate.Distance > thresholdDip) continue;
            if (candidate.Distance < distance - 0.000001 ||
                (Math.Abs(candidate.Distance - distance) <= 0.000001 && Priority(candidate.Side) < Priority(best)))
            {
                best = candidate.Side;
                distance = candidate.Distance;
            }
        }
        return best;
    }

    public static EdgeRectangle ClampExpanded(EdgeRectangle window, EdgeRectangle workingArea)
    {
        Validate(window, workingArea, 1, 1);
        var x = window.Width <= workingArea.Width
            ? Clamp(window.X, workingArea.X, workingArea.Right - window.Width) : workingArea.X;
        var y = window.Height <= workingArea.Height
            ? Clamp(window.Y, workingArea.Y, workingArea.Bottom - window.Height) : workingArea.Y;
        return window with { X = x, Y = y };
    }

    public static EdgeRectangle SnapExpanded(EdgeRectangle window, EdgeRectangle workingArea, EdgeDockSide edge)
    {
        var clamped = ClampExpanded(window, workingArea);
        return edge switch
        {
            EdgeDockSide.Top => clamped with { Y = workingArea.Y },
            EdgeDockSide.Right => clamped with
            {
                X = window.Width <= workingArea.Width ? checked((int)(workingArea.Right - window.Width)) : workingArea.X
            },
            EdgeDockSide.Bottom => clamped with
            {
                Y = window.Height <= workingArea.Height ? checked((int)(workingArea.Bottom - window.Height)) : workingArea.Y
            },
            EdgeDockSide.Left => clamped with { X = workingArea.X },
            _ => clamped
        };
    }

    public static EdgeRectangle Collapse(EdgeRectangle expanded, EdgeRectangle workingArea, EdgeDockSide edge,
        double scaleX = 1, double scaleY = 1, double stripDip = DefaultStripDip)
    {
        Validate(expanded, workingArea, scaleX, scaleY);
        if (!double.IsFinite(stripDip) || stripDip <= 0) throw new ArgumentOutOfRangeException(nameof(stripDip));
        var snapped = SnapExpanded(expanded, workingArea, edge);
        var horizontalStrip = Math.Clamp(DipPixels(stripDip, scaleX), 1, expanded.Width);
        var verticalStrip = Math.Clamp(DipPixels(stripDip, scaleY), 1, expanded.Height);
        return edge switch
        {
            EdgeDockSide.Top => snapped with { Y = checked(workingArea.Y - expanded.Height + verticalStrip) },
            EdgeDockSide.Right => snapped with { X = checked((int)(workingArea.Right - horizontalStrip)) },
            EdgeDockSide.Bottom => snapped with { Y = checked((int)(workingArea.Bottom - verticalStrip)) },
            EdgeDockSide.Left => snapped with { X = checked(workingArea.X - expanded.Width + horizontalStrip) },
            _ => snapped
        };
    }

    public static EdgeRectangle VisiblePart(EdgeRectangle window, EdgeRectangle workingArea)
    {
        Validate(window, workingArea, 1, 1);
        var left = Math.Max(window.X, workingArea.X);
        var top = Math.Max(window.Y, workingArea.Y);
        var right = Math.Min(window.Right, workingArea.Right);
        var bottom = Math.Min(window.Bottom, workingArea.Bottom);
        return right <= left || bottom <= top
            ? new EdgeRectangle(left, top, 0, 0)
            : new EdgeRectangle(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
    }

    public static EdgeRectangle ReflowExpanded(EdgeRectangle anchor, int newWidth, int newHeight,
        EdgeRectangle workingArea, EdgeDockSide edge) =>
        SnapExpanded(anchor with { Width = newWidth, Height = newHeight }, workingArea, edge);

    public static EdgeRectangle Interpolate(EdgeRectangle from, EdgeRectangle to, double progress)
    {
        if (!double.IsFinite(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        var t = Math.Clamp(progress, 0, 1);
        // Cubic ease in/out starts and ends at rest. Integer positions remain in
        // physical pixels so negative monitor origins do not lose precision.
        var eased = t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        return to with
        {
            X = checked((int)Math.Round(from.X + ((double)to.X - from.X) * eased)),
            Y = checked((int)Math.Round(from.Y + ((double)to.Y - from.Y) * eased))
        };
    }

    public static string Name(EdgeDockSide edge) => edge switch
    {
        EdgeDockSide.Top => "top", EdgeDockSide.Right => "right", EdgeDockSide.Bottom => "bottom",
        EdgeDockSide.Left => "left", _ => ""
    };

    public static EdgeDockSide Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "top" => EdgeDockSide.Top, "right" => EdgeDockSide.Right, "bottom" => EdgeDockSide.Bottom,
        "left" => EdgeDockSide.Left, _ => EdgeDockSide.None
    };

    private static int Priority(EdgeDockSide edge) => edge switch
    {
        EdgeDockSide.Top => 0, EdgeDockSide.Right => 1, EdgeDockSide.Bottom => 2,
        EdgeDockSide.Left => 3, _ => int.MaxValue
    };
    private static int DipPixels(double dip, double scale) => checked((int)Math.Round(dip * scale, MidpointRounding.AwayFromZero));
    private static int Clamp(int value, long minimum, long maximum) => checked((int)Math.Clamp((long)value, minimum, maximum));
    private static void Validate(EdgeRectangle window, EdgeRectangle workingArea, double scaleX, double scaleY)
    {
        if (window.Width <= 0 || window.Height <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        if (workingArea.Width <= 0 || workingArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workingArea));
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX));
    }
}
