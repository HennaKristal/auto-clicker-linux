using System;
using System.Linq;
using AutoClicker.Models;

namespace AutoClicker.ViewModels;

public sealed class MacroViewModel : ObservableObject
{
    private readonly MacroDefinition definition;
    public MainViewModel Owner { get; }
    public Guid Id => definition.Id;
    public int EventCount => definition.Events.Count;
    public double DurationMs => definition.Events.Last().OffsetMs;
    public DateTimeOffset CreatedAt => definition.Created;
    public string Name => definition.Name;
    public string Hotkey => Owner.Trigger("macro_" + Id.ToString("N"));
    public decimal? Repeat { get => Get<decimal?>(definition.Repeat); set { Put(value); Owner.MacroChanged(); } }
    public bool Infinite { get => Get(definition.Infinite); set { Put(value); Owner.MacroChanged(); } }
    public bool RepeatEditable => Owner.SettingsEditable && !Infinite;
    public string Length => (definition.Events.LastOrDefault()?.OffsetMs / 1000 ?? 0).ToString("0.0") + "s";
    public string Created => definition.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string PlayTip => Owner.ActiveMacroId == Id ? "Stop macro" : "Play macro";
    public MacroViewModel(MainViewModel owner, MacroDefinition definition) { Owner = owner; this.definition = definition; }
    public MacroDefinition Build()
    {
        if (!Infinite && (!Repeat.HasValue || Repeat < 1 || Repeat > 1_000_000 || Repeat % 1 != 0)) { throw new ArgumentException("Repeat count must be a whole number between 1 and 1,000,000."); }
        return definition with { Repeat = Infinite ? Math.Max(1, definition.Repeat) : (int)Repeat!.Value, Infinite = Infinite };
    }
}
