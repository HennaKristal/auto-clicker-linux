using System.Collections.Generic;

namespace AutoClicker.Models;

public sealed record DesktopPoint(double X, double Y);
public sealed record DesktopMonitor(string Name, double X, double Y, double Width, double Height, double Scale);
public sealed record DesktopRectangle(double X, double Y, double Width, double Height);
public sealed record DesktopWindow(int ProcessId, string Caption, DesktopRectangle ClientBounds);
public sealed record DesktopSnapshot(DesktopPoint Cursor, IReadOnlyList<DesktopMonitor> Monitors, DesktopWindow? ActiveWindow = null);
public sealed record ShortcutBinding(string Id, string TriggerDescription)
{
    public bool IsAssigned => !string.IsNullOrWhiteSpace(TriggerDescription);
}
public sealed record ShortcutEvent(string Id, bool Pressed, ulong TimestampMilliseconds);
public sealed record PhysicalInputEvent(string Device, long TimestampMicroseconds, ushort Type, ushort Code, int Value);
