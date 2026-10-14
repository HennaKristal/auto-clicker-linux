using System;
using System.Collections.Generic;
using System.Linq;
using AutoClicker.Models;

namespace AutoClicker.Services;

public static class StopRegions
{
    public static string? Check(ClickerSettings settings, DesktopSnapshot snapshot)
    {
        var point = snapshot.Cursor;
        foreach (var monitor in snapshot.Monitors)
        {
            if (!Contains(monitor, point)) { continue; }
            var x = point.X - monitor.X;
            var y = point.Y - monitor.Y;
            if (settings.CornerStop && (Corner(x, y, settings.TopLeft) || Corner(monitor.Width - 1 - x, y, settings.TopRight) || Corner(x, monitor.Height - 1 - y, settings.BottomLeft) || Corner(monitor.Width - 1 - x, monitor.Height - 1 - y, settings.BottomRight))) { return "Stopped at a screen corner"; }
            if (!settings.EdgeStop) { continue; }
            if (NearEdge(point, monitor, snapshot.Monitors, true, true, settings.LeftEdge) || NearEdge(point, monitor, snapshot.Monitors, true, false, settings.RightEdge) || NearEdge(point, monitor, snapshot.Monitors, false, true, settings.TopEdge) || NearEdge(point, monitor, snapshot.Monitors, false, false, settings.BottomEdge)) { return "Stopped at a screen edge"; }
        }
        return null;
    }

    public static bool Contains(DesktopMonitor monitor, DesktopPoint point) => point.X >= monitor.X && point.X < monitor.X + monitor.Width && point.Y >= monitor.Y && point.Y < monitor.Y + monitor.Height;
    private static bool Corner(double x, double y, double size) => size > 0 && x < size && y < size;

    private static bool NearEdge(DesktopPoint point, DesktopMonitor monitor, IReadOnlyList<DesktopMonitor> monitors, bool vertical, bool leading, double distance)
    {
        if (distance <= 0) { return false; }
        var boundary = vertical ? monitor.X + (leading ? 0 : monitor.Width) : monitor.Y + (leading ? 0 : monitor.Height);
        var normal = vertical ? point.X : point.Y;
        if (Math.Abs(normal - boundary) > distance) { return false; }
        var start = vertical ? monitor.Y : monitor.X;
        var end = start + (vertical ? monitor.Height : monitor.Width);
        var segments = new List<(double Start, double End)> { (start, end) };
        // Subtract neighboring coverage, including partial shared borders on staggered monitors.
        foreach (var other in monitors.Where(other => !ReferenceEquals(other, monitor)))
        {
            var otherNormalStart = vertical ? other.X : other.Y;
            var otherNormalEnd = otherNormalStart + (vertical ? other.Width : other.Height);
            var crosses = leading ? otherNormalStart < boundary && otherNormalEnd >= boundary : otherNormalStart <= boundary && otherNormalEnd > boundary;
            if (!crosses) { continue; }
            var cutStart = vertical ? other.Y : other.X;
            var cutEnd = cutStart + (vertical ? other.Height : other.Width);
            segments = segments.SelectMany(segment => Subtract(segment, cutStart, cutEnd)).ToList();
        }
        var projection = vertical ? point.Y : point.X;
        return segments.Any(segment => Math.Pow(normal - boundary, 2) + Math.Pow(projection - Math.Clamp(projection, segment.Start, segment.End), 2) <= distance * distance);
    }

    private static IEnumerable<(double Start, double End)> Subtract((double Start, double End) segment, double start, double end)
    {
        if (end <= segment.Start || start >= segment.End) { yield return segment; yield break; }
        if (start > segment.Start) { yield return (segment.Start, start); }
        if (end < segment.End) { yield return (end, segment.End); }
    }
}
