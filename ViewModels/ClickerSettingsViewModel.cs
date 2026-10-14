using System;
using AutoClicker.Models;

namespace AutoClicker.ViewModels;

// Nullable numeric fields retain incomplete edits until the user supplies a valid value.
public sealed class ClickerSettingsViewModel : ObservableObject
{
    // Keep model, UI, and incomplete disabled fields on the same default values.
    private static readonly ClickerSettings Defaults = new();
    public decimal? Rate { get => Get<decimal?>((decimal)Defaults.Rate); set => Put(value); }
    public decimal? DoubleDelayMs { get => Get<decimal?>((decimal)Defaults.DoubleDelayMs); set => Put(value); }
    public decimal? DutyPercent { get => Get<decimal?>((decimal)Defaults.DutyPercent); set => Put(value); }
    public decimal? VariationPercent { get => Get<decimal?>((decimal)Defaults.VariationPercent); set => Put(value); }
    public decimal? ClickLimit { get => Get<decimal?>(Defaults.ClickLimit); set => Put(value); }
    public decimal? TimeLimit { get => Get<decimal?>((decimal)Defaults.TimeLimit); set => Put(value); }
    public decimal? TopLeft { get => Get<decimal?>((decimal)Defaults.TopLeft); set => Put(value); }
    public decimal? TopRight { get => Get<decimal?>((decimal)Defaults.TopRight); set => Put(value); }
    public decimal? BottomLeft { get => Get<decimal?>((decimal)Defaults.BottomLeft); set => Put(value); }
    public decimal? BottomRight { get => Get<decimal?>((decimal)Defaults.BottomRight); set => Put(value); }
    public decimal? TopEdge { get => Get<decimal?>((decimal)Defaults.TopEdge); set => Put(value); }
    public decimal? LeftEdge { get => Get<decimal?>((decimal)Defaults.LeftEdge); set => Put(value); }
    public decimal? RightEdge { get => Get<decimal?>((decimal)Defaults.RightEdge); set => Put(value); }
    public decimal? BottomEdge { get => Get<decimal?>((decimal)Defaults.BottomEdge); set => Put(value); }
    public decimal? PositionX { get => Get<decimal?>((decimal)Defaults.Position.X); set => Put(value); }
    public decimal? PositionY { get => Get<decimal?>((decimal)Defaults.Position.Y); set => Put(value); }
    public bool DoubleClick { get => Get(Defaults.DoubleClick); set => Put(value); }
    public bool DutyEnabled { get => Get(Defaults.DutyEnabled); set => Put(value); }
    public bool VariationEnabled { get => Get(Defaults.VariationEnabled); set => Put(value); }
    public bool ClickLimitEnabled { get => Get(Defaults.ClickLimitEnabled); set => Put(value); }
    public bool TimeLimitEnabled { get => Get(Defaults.TimeLimitEnabled); set => Put(value); }
    public bool CornerStop { get => Get(Defaults.CornerStop); set => Put(value); }
    public bool EdgeStop { get => Get(Defaults.EdgeStop); set => Put(value); }
    public bool PositionEnabled { get => Get(Defaults.PositionEnabled); set => Put(value); }
    public RateUnit RateUnit { get => Get(Defaults.RateUnit); set => Put(value); }
    public bool RateUnitSecond { get => RateUnit == RateUnit.Second; set { if (value) { RateUnit = RateUnit.Second; } } }
    public bool RateUnitMinute { get => RateUnit == RateUnit.Minute; set { if (value) { RateUnit = RateUnit.Minute; } } }
    public bool RateUnitHour { get => RateUnit == RateUnit.Hour; set { if (value) { RateUnit = RateUnit.Hour; } } }
    public bool RateUnitDay { get => RateUnit == RateUnit.Day; set { if (value) { RateUnit = RateUnit.Day; } } }
    public ActivationMode Activation { get => Get(Defaults.Activation); set => Put(value); }
    public bool ActivationToggle { get => Activation == ActivationMode.Toggle; set { if (value) { Activation = ActivationMode.Toggle; } } }
    public bool ActivationHold { get => Activation == ActivationMode.Hold; set { if (value) { Activation = ActivationMode.Hold; } } }
    public ClickButton Button { get => Get(Defaults.Button); set => Put(value); }
    public bool ButtonLeft { get => Button == ClickButton.Left; set { if (value) { Button = ClickButton.Left; } } }
    public bool ButtonMiddle { get => Button == ClickButton.Middle; set { if (value) { Button = ClickButton.Middle; } } }
    public bool ButtonRight { get => Button == ClickButton.Right; set { if (value) { Button = ClickButton.Right; } } }
    public DurationUnit TimeUnit { get => Get(Defaults.TimeUnit); set => Put(value); }
    public bool TimeUnitSecond { get => TimeUnit == DurationUnit.Second; set { if (value) { TimeUnit = DurationUnit.Second; } } }
    public bool TimeUnitMinute { get => TimeUnit == DurationUnit.Minute; set { if (value) { TimeUnit = DurationUnit.Minute; } } }
    public bool TimeUnitHour { get => TimeUnit == DurationUnit.Hour; set { if (value) { TimeUnit = DurationUnit.Hour; } } }
    public bool DoubleDelayEditable => DoubleClick;
    public bool DutyEditable => DutyEnabled;
    public bool VariationEditable => VariationEnabled;
    public bool ClickLimitEditable => ClickLimitEnabled;
    public bool TimeLimitEditable => TimeLimitEnabled;
    public bool CornerEditable => CornerStop;
    public bool EdgeEditable => EdgeStop;
    public bool PositionEditable => PositionEnabled;

    public ClickerSettingsViewModel(ClickerSettings settings)
    {
        Rate = (decimal)settings.Rate;
        DoubleDelayMs = (decimal)settings.DoubleDelayMs;
        DutyPercent = (decimal)settings.DutyPercent;
        VariationPercent = (decimal)settings.VariationPercent;
        ClickLimit = (decimal)settings.ClickLimit;
        TimeLimit = (decimal)settings.TimeLimit;
        TopLeft = (decimal)settings.TopLeft;
        TopRight = (decimal)settings.TopRight;
        BottomLeft = (decimal)settings.BottomLeft;
        BottomRight = (decimal)settings.BottomRight;
        TopEdge = (decimal)settings.TopEdge;
        LeftEdge = (decimal)settings.LeftEdge;
        RightEdge = (decimal)settings.RightEdge;
        BottomEdge = (decimal)settings.BottomEdge;
        PositionX = (decimal)settings.Position.X;
        PositionY = (decimal)settings.Position.Y;
        DoubleClick = settings.DoubleClick;
        DutyEnabled = settings.DutyEnabled;
        VariationEnabled = settings.VariationEnabled;
        ClickLimitEnabled = settings.ClickLimitEnabled;
        TimeLimitEnabled = settings.TimeLimitEnabled;
        CornerStop = settings.CornerStop;
        EdgeStop = settings.EdgeStop;
        PositionEnabled = settings.PositionEnabled;
        RateUnit = settings.RateUnit;
        Activation = settings.Activation;
        Button = settings.Button;
        TimeUnit = settings.TimeUnit;
    }

    public ClickerSettings Build()
    {
        var settings = new ClickerSettings
        {
            Rate = Number(Rate, "Rate"),
            DoubleDelayMs = DoubleClick ? Number(DoubleDelayMs, "DoubleDelayMs") : (double)(DoubleDelayMs ?? (decimal)Defaults.DoubleDelayMs),
            DutyPercent = DutyEnabled ? Number(DutyPercent, "DutyPercent") : (double)(DutyPercent ?? (decimal)Defaults.DutyPercent),
            VariationPercent = VariationEnabled ? Number(VariationPercent, "VariationPercent") : (double)(VariationPercent ?? (decimal)Defaults.VariationPercent),
            ClickLimit = WholeCount(ClickLimit ?? (ClickLimitEnabled ? null : (decimal)Defaults.ClickLimit)),
            TimeLimit = TimeLimitEnabled ? Number(TimeLimit, "TimeLimit") : (double)(TimeLimit ?? (decimal)Defaults.TimeLimit),
            TopLeft = CornerStop ? Number(TopLeft, "TopLeft") : (double)(TopLeft ?? (decimal)Defaults.TopLeft),
            TopRight = CornerStop ? Number(TopRight, "TopRight") : (double)(TopRight ?? (decimal)Defaults.TopRight),
            BottomLeft = CornerStop ? Number(BottomLeft, "BottomLeft") : (double)(BottomLeft ?? (decimal)Defaults.BottomLeft),
            BottomRight = CornerStop ? Number(BottomRight, "BottomRight") : (double)(BottomRight ?? (decimal)Defaults.BottomRight),
            TopEdge = EdgeStop ? Number(TopEdge, "TopEdge") : (double)(TopEdge ?? (decimal)Defaults.TopEdge),
            LeftEdge = EdgeStop ? Number(LeftEdge, "LeftEdge") : (double)(LeftEdge ?? (decimal)Defaults.LeftEdge),
            RightEdge = EdgeStop ? Number(RightEdge, "RightEdge") : (double)(RightEdge ?? (decimal)Defaults.RightEdge),
            BottomEdge = EdgeStop ? Number(BottomEdge, "BottomEdge") : (double)(BottomEdge ?? (decimal)Defaults.BottomEdge),
            DoubleClick = DoubleClick,
            DutyEnabled = DutyEnabled,
            VariationEnabled = VariationEnabled,
            ClickLimitEnabled = ClickLimitEnabled,
            TimeLimitEnabled = TimeLimitEnabled,
            CornerStop = CornerStop,
            EdgeStop = EdgeStop,
            PositionEnabled = PositionEnabled,
            RateUnit = RateUnit,
            Activation = Activation,
            Button = Button,
            TimeUnit = TimeUnit,
            Position = new(PositionEnabled ? Number(PositionX, "Cursor X") : (double)(PositionX ?? (decimal)Defaults.Position.X), PositionEnabled ? Number(PositionY, "Cursor Y") : (double)(PositionY ?? (decimal)Defaults.Position.Y))
        };
        if (ClickLimitEnabled && ClickLimit % 1 != 0) { throw new ArgumentException("Click limit must be a whole number."); }
        settings.Validate();
        return settings;
    }

    private static long WholeCount(decimal? value) { if (!value.HasValue || value < 1 || value > long.MaxValue || value % 1 != 0) { throw new ArgumentException("Click limit must be a positive whole number within the supported range."); } return (long)value.Value; }
    private static double Number(decimal? value, string label) => value.HasValue ? (double)value.Value : throw new ArgumentException(label + " needs a value.");
}
