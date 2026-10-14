using System;
using System.Collections.ObjectModel;
using System.Linq;
using AutoClicker.Models;
using AutoClicker.Services;

namespace AutoClicker.ViewModels;

public sealed class EventEditorRow : ObservableObject
{
    public RecordedInput Input { get; }
    public decimal? DelayMs { get => Get<decimal?>(0); set => Put(value); }
    public string Label => (Input.Keyboard ? "Key " : "Mouse ") + Input.Code + (Input.Pressed ? " down" : " up") + (Input.Position is { } point ? " at " + point.X + ", " + point.Y : "");
    public EventEditorRow(RecordedInput input, double delay) { Input = input; DelayMs = (decimal)delay; }
}

public sealed class MacroEditorViewModel : ObservableObject
{
    private readonly MacroDefinition original;
    public string Name { get => Get(""); set => Put(value); }
    public decimal? Repeat { get => Get<decimal?>(1); set => Put(value); }
    public bool Infinite { get => Get(false); set => Put(value); }
    public ObservableCollection<EventEditorRow> Events { get; } = new();
    public MacroEditorViewModel(MacroDefinition macro) { original = macro; Name = macro.Name; Repeat = macro.Repeat; Infinite = macro.Infinite; LoadEvents(macro.Events); }
    private void LoadEvents(System.Collections.Generic.IReadOnlyList<RecordedInput> inputs)
    {
        Events.Clear();
        double previous = 0;
        foreach (var input in inputs) { Events.Add(new(input, input.OffsetMs - previous)); previous = input.OffsetMs; }
    }
    private RecordedInput[] BuildEvents()
    {
        double offset = 0;
        return Events.Select(row => { if (!row.DelayMs.HasValue || row.DelayMs < 0) { throw new ArgumentException("Delays must be zero or positive."); } offset += (double)row.DelayMs.Value; return row.Input with { OffsetMs = offset }; }).ToArray();
    }
    public void RemovePair(EventEditorRow row)
    {
        var inputs = BuildEvents();
        var index = Events.IndexOf(row);
        if (index < 0) { return; }
        var paired = -1;
        var direction = row.Input.Pressed ? 1 : -1;
        for (var cursor = index + direction; cursor >= 0 && cursor < inputs.Length; cursor += direction) { if (inputs[cursor].Keyboard == row.Input.Keyboard && inputs[cursor].Code == row.Input.Code && inputs[cursor].Pressed != row.Input.Pressed) { paired = cursor; break; } }
        LoadEvents(inputs.Where((_, position) => position != index && position != paired).ToArray());
    }
    public MacroDefinition Build()
    {
        if (!Infinite && (!Repeat.HasValue || Repeat < 1 || Repeat > 1_000_000 || Repeat % 1 != 0)) { throw new ArgumentException("Repeat count must be a positive whole number."); }
        var macro = original with { Name = Name.Trim(), Repeat = Infinite ? Math.Max(1, original.Repeat) : (int)Repeat!.Value, Infinite = Infinite, Events = BuildEvents() };
        MacroValidation.Validate(macro);
        return macro;
    }
}
