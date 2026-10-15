using System;
using System.Collections.Generic;

namespace AutoClicker.Models;

public enum AutomationState { Idle, Starting, Clicking, Recording, PlayingMacro, Stopping }
public enum ActivationMode { Toggle, Hold }
public enum RateUnit { Second, Minute, Hour, Day }
public enum DurationUnit { Second, Minute, Hour }
public enum ClickButton { Left, Middle, Right }

public sealed record ClickerSettings
{
    public const int MaximumRate = 100_000;
    public double Rate { get; init; } = 100;
    public RateUnit RateUnit { get; init; }
    public ActivationMode Activation { get; init; }
    public ClickButton Button { get; init; }
    public bool DoubleClick { get; init; }
    public double DoubleDelayMs { get; init; } = 20;
    public bool DutyEnabled { get; init; }
    public double DutyPercent { get; init; } = 0;
    public bool VariationEnabled { get; init; }
    public double VariationPercent { get; init; } = 0;
    public bool ClickLimitEnabled { get; init; }
    public long ClickLimit { get; init; } = 1000;
    public bool TimeLimitEnabled { get; init; }
    public double TimeLimit { get; init; } = 60;
    public DurationUnit TimeUnit { get; init; }
    public bool CornerStop { get; init; } = true;
    public double TopLeft { get; init; } = 50;
    public double TopRight { get; init; } = 50;
    public double BottomLeft { get; init; } = 50;
    public double BottomRight { get; init; } = 50;
    public bool EdgeStop { get; init; } = true;
    public double TopEdge { get; init; } = 50;
    public double LeftEdge { get; init; } = 50;
    public double RightEdge { get; init; } = 50;
    public double BottomEdge { get; init; } = 50;
    public bool PositionEnabled { get; init; }
    public DesktopPoint Position { get; init; } = new(0, 0);
    public double IntervalMs => (RateUnit switch { RateUnit.Minute => 60_000, RateUnit.Hour => 3_600_000, RateUnit.Day => 86_400_000, _ => 1000 }) / Rate;
    public double DurationMs => TimeLimit * (TimeUnit switch { DurationUnit.Minute => 60_000, DurationUnit.Hour => 3_600_000, _ => 1000 });
    public uint ButtonCode => Button switch { ClickButton.Middle => 0x112, ClickButton.Right => 0x111, _ => 0x110 };

    // Preserve short default presses at normal rates and reserve release time at high rates.
    public double HoldDurationMs(double intervalMs) => DutyEnabled ? intervalMs * DutyPercent / 100 : Math.Min(1, intervalMs / 2);

    public void Validate()
    {
        if (!Enum.IsDefined(RateUnit) || !Enum.IsDefined(Activation) || !Enum.IsDefined(Button) || !Enum.IsDefined(TimeUnit)) { throw new ArgumentException("Choose valid units, activation mode, and mouse button."); }
        Check(Rate, 1, MaximumRate, "Click rate");
        Check(DoubleDelayMs, 0, 60_000, "Double-click delay");
        Check(DutyPercent, 0, 100, "Duty cycle");
        Check(VariationPercent, 0, 100, "Speed variation");
        if (ClickLimit < 1) { throw new ArgumentException("Click limit must be positive."); }
        Check(TimeLimit, 1, 86_400, "Time limit");
        foreach (var size in new[] { TopLeft, TopRight, BottomLeft, BottomRight, TopEdge, LeftEdge, RightEdge, BottomEdge }) { Check(size, 0, 10_000, "Stop distance"); }
        if (Position == null || !double.IsFinite(Position.X) || !double.IsFinite(Position.Y) || Math.Abs(Position.X) > int.MaxValue || Math.Abs(Position.Y) > int.MaxValue) { throw new ArgumentException("Choose a finite cursor position within the desktop coordinate range."); }
    }

    private static void Check(double value, double minimum, double maximum, string label) { if (!double.IsFinite(value) || value < minimum || value > maximum) { throw new ArgumentException(label + " must be between " + minimum + " and " + maximum + "."); } }
}

public sealed record RecordedInput(double OffsetMs, bool Keyboard, uint Code, bool Pressed, DesktopPoint? Position = null);
public sealed record MacroDefinition(Guid Id, string Name, DateTimeOffset Created, IReadOnlyList<RecordedInput> Events, int Repeat = 1, bool Infinite = false);
public sealed record UsageStatistics(long Clicks = 0, double RunningMs = 0, long Sessions = 0);
public sealed record AppConfiguration(int Version, ClickerSettings Clicker, bool RecordClicks = true, bool RecordKeyboard = true);
public sealed record AppData(int Version, IReadOnlyList<MacroDefinition> Macros, UsageStatistics Statistics);
public sealed record ShortcutDefinition(string Id, string Description, string PreferredTrigger = "");
public sealed record RunProgress(AutomationState State, long Clicks, double ElapsedMs, string Message, Guid? MacroId = null, Exception? Error = null, long Generation = 0);
public sealed record RunResult(long Clicks, double ElapsedMs, string Reason);
