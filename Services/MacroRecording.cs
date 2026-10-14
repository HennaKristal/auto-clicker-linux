using System;
using System.Collections.Generic;
using System.Linq;
using AutoClicker.Models;

namespace AutoClicker.Services;

public static class MacroValidation
{
    public static void Validate(MacroDefinition macro)
    {
        if (macro == null || macro.Id == Guid.Empty || string.IsNullOrWhiteSpace(macro.Name) || macro.Name.Length > 120) { throw new ArgumentException("Macro names must contain 1-120 characters."); }
        if (macro.Repeat is < 1 or > 1_000_000) { throw new ArgumentException("Repeat count must be between 1 and 1,000,000."); }
        if (macro.Events == null || macro.Events.Count is 0 or > 20000) { throw new ArgumentException("A macro needs between 1 and 20,000 events."); }
        var held = new HashSet<(bool Keyboard, uint Code)>();
        double previous = 0;
        foreach (var item in macro.Events)
        {
            if (item == null || !double.IsFinite(item.OffsetMs) || item.OffsetMs < previous || item.OffsetMs > 600_000) { throw new ArgumentException("Macro delays must be ordered and fit within ten minutes."); }
            if (item.Keyboard ? item.Code is 0 or >= 0x100 : item.Code is not (0x110 or 0x111 or 0x112)) { throw new ArgumentException("Macro contains an unsupported input code."); }
            if (item.Position != null && (!double.IsFinite(item.Position.X) || !double.IsFinite(item.Position.Y) || Math.Abs(item.Position.X) > int.MaxValue || Math.Abs(item.Position.Y) > int.MaxValue)) { throw new ArgumentException("Macro contains an invalid cursor position."); }
            var key = (item.Keyboard, item.Code);
            if (item.Pressed ? !held.Add(key) : !held.Remove(key)) { throw new ArgumentException("Every input press must have one matching release."); }
            previous = item.OffsetMs;
        }
        if (held.Count > 0) { throw new ArgumentException("The macro ends with held input."); }
    }
}

public sealed class MacroRecording
{
    private readonly object gate = new();
    private readonly List<RecordedInput> events = new();
    private readonly Dictionary<(string Device, bool Keyboard, uint Code), RecordedInput> held = new();
    private readonly Dictionary<RecordedInput, RecordedInput> releases = new(ReferenceEqualityComparer.Instance);
    private readonly List<(IReadOnlySet<uint> Keys, double Offset)> observedControls = new();
    private readonly List<(uint Code, double Offset, RecordedInput? Retained)> physicalPresses = new();
    private readonly List<(uint Code, double Offset)> applicationPresses = new();
    private readonly long started;
    private readonly bool clicks;
    private readonly bool keyboard;
    public MacroRecording(long startedMicroseconds, bool clicks, bool keyboard) { started = startedMicroseconds; this.clicks = clicks; this.keyboard = keyboard; }
    public int Count { get { lock (gate) { return events.Count; } } }
    public void SuppressControl(string trigger, double monotonicMs)
    {
        var keys = ShortcutChord.ParseAlternatives(trigger).SelectMany(chord => chord).ToHashSet();
        if (keys.Count == 0) { return; }
        lock (gate) { observedControls.Add((keys, monotonicMs - started / 1000d)); }
    }
    public void NoteApplicationPress(uint code, double monotonicMs) { if (!clicks) { return; } lock (gate) { applicationPresses.Add((code, monotonicMs - started / 1000d)); } }

    public void Add(PhysicalInputEvent input, DesktopPoint? position, bool applicationInput = false)
    {
        if (input.Type != 1 || input.Value is not (0 or 1)) { return; } // Desktop key repeat is reconstructed from the held key.
        var isKeyboard = input.Code < 0x100;
        if (isKeyboard ? !keyboard : !clicks) { return; }
        var offset = Math.Max(0, (input.TimestampMicroseconds - started) / 1000d);
        lock (gate)
        {
            if (events.Count >= 19_500 || events.Count + held.Count >= 19_998 || physicalPresses.Count >= 20_000 || offset > 599_000) { throw new InvalidOperationException("Recording reached its size or duration limit."); }
            var key = (input.Device, isKeyboard, (uint)input.Code);
            if (applicationInput && input.Value == 1) { if (!isKeyboard) { physicalPresses.Add((input.Code, offset, null)); } return; }
            var recorded = new RecordedInput(offset, isKeyboard, input.Code, input.Value == 1, !isKeyboard && input.Value == 1 ? position : null);
            if (recorded.Pressed)
            {
                if (held.ContainsKey(key)) { return; }
                if (!isKeyboard && position == null) { throw new InvalidOperationException("Cursor position is unavailable; this recording cannot be saved."); }
                held.Add(key, recorded);
            }
            else
            {
                // Ignore releases for keys held before recording began.
                if (!held.Remove(key, out var press)) { return; }
                releases.Add(press, recorded);
            }
            events.Add(recorded);
            if (!isKeyboard && input.Value == 1) { physicalPresses.Add((input.Code, offset, recorded)); }
        }
    }

    public IReadOnlyList<RecordedInput> Finish(double elapsedMs, IReadOnlyList<ShortcutBinding> controls)
    {
        lock (gate)
        {
            var end = Math.Min(600_000, Math.Max(elapsedMs, events.Count == 0 ? 0 : events.Max(input => input.OffsetMs)));
            foreach (var pair in held.OrderBy(pair => !pair.Key.Keyboard ? 0 : ShortcutChord.IsModifier(pair.Key.Code) ? 2 : 1))
            {
                var release = new RecordedInput(end, pair.Key.Keyboard, pair.Key.Code, false);
                events.Add(release);
                releases.Add(pair.Value, release);
            }
            held.Clear();
            // Separate evdev interfaces can arrive out of order; retain their shared kernel clock ordering.
            var filtered = RemoveApplicationClicks(events.OrderBy(input => input.OffsetMs).ToArray());
            return RemoveControlShortcuts(MergeDevicePresses(filtered), controls, observedControls);
        }
    }

    private static IReadOnlyList<RecordedInput> MergeDevicePresses(IReadOnlyList<RecordedInput> source)
    {
        var counts = new Dictionary<(bool Keyboard, uint Code), int>();
        var result = new List<RecordedInput>();
        foreach (var input in source)
        {
            var key = (input.Keyboard, input.Code);
            counts.TryGetValue(key, out var count);
            counts[key] = input.Pressed ? count + 1 : count - 1;
            // Keep a shared key down until the final physical device releases it.
            if (input.Pressed ? count == 0 : count == 1) { result.Add(input); }
        }
        return result;
    }

    private IReadOnlyList<RecordedInput> RemoveApplicationClicks(IReadOnlyList<RecordedInput> source)
    {
        var ignored = new HashSet<RecordedInput>(ReferenceEqualityComparer.Instance);
        var samples = physicalPresses.GroupBy(press => press.Code).ToDictionary(group => group.Key, group => group.OrderBy(press => press.Offset).ToArray());
        foreach (var marker in applicationPresses)
        {
            if (!samples.TryGetValue(marker.Code, out var presses) || presses.Length == 0) { continue; }
            var lower = 0;
            var upper = presses.Length;
            while (lower < upper) { var middle = (lower + upper) / 2; if (presses[middle].Offset < marker.Offset) { lower = middle + 1; } else { upper = middle; } }
            var nearest = Math.Min(lower, presses.Length - 1);
            if (nearest > 0 && Math.Abs(presses[nearest - 1].Offset - marker.Offset) < Math.Abs(presses[nearest].Offset - marker.Offset)) { nearest--; }
            // Match actual UI events, including already-ignored samples, rather than screen rectangles.
            // A short receipt-time tolerance covers the independent UI/evdev dispatch queues.
            var sample = presses[nearest];
            if (Math.Abs(sample.Offset - marker.Offset) > 50 || sample.Retained == null) { continue; }
            ignored.Add(sample.Retained);
            ignored.Add(releases[sample.Retained]);
        }
        return source.Where(input => !ignored.Contains(input)).ToArray();
    }

    private static IReadOnlyList<RecordedInput> RemoveControlShortcuts(IReadOnlyList<RecordedInput> source, IReadOnlyList<ShortcutBinding> controls, IReadOnlyList<(IReadOnlySet<uint> Keys, double Offset)> observed)
    {
        var chords = controls.Where(binding => binding.IsAssigned).SelectMany(binding => ShortcutChord.ParseAlternatives(binding.TriggerDescription)).ToArray();
        var observedPresses = new HashSet<int>();
        foreach (var marker in observed)
        {
            var nearest = -1;
            var distance = 150d;
            for (var index = 0; index < source.Count; index++)
            {
                var input = source[index];
                if (!input.Keyboard || !input.Pressed || ShortcutChord.IsModifier(input.Code) || !marker.Keys.Contains(ShortcutChord.Canonical(input.Code))) { continue; }
                var difference = Math.Abs(input.OffsetMs - marker.Offset);
                if (difference > distance) { continue; }
                nearest = index;
                distance = difference;
            }
            if (nearest >= 0) { observedPresses.Add(nearest); }
        }
        var pressed = new Dictionary<uint, int>();
        var ignored = new HashSet<int>();
        var controlKeys = new HashSet<uint>();
        for (var index = 0; index < source.Count; index++)
        {
            var input = source[index];
            if (!input.Keyboard) { continue; }
            if (input.Pressed)
            {
                pressed[input.Code] = index;
                if (observedPresses.Contains(index)) { ignored.Add(index); controlKeys.Add(input.Code); }
                foreach (var chord in chords.Where(chord => chord.All(key => pressed.Keys.Any(code => ShortcutChord.Canonical(code) == key))))
                {
                    foreach (var pair in pressed.Where(pair => chord.Contains(ShortcutChord.Canonical(pair.Key))))
                    {
                        if (!ShortcutChord.IsModifier(pair.Key)) { ignored.Add(pair.Value); controlKeys.Add(pair.Key); }
                    }
                }
            }
            else { if (controlKeys.Remove(input.Code)) { ignored.Add(index); } pressed.Remove(input.Code); }
        }
        // Remove modifiers used only for a control chord; preserve modifiers used by retained actions.
        pressed.Clear();
        for (var index = 0; index < source.Count; index++)
        {
            var input = source[index];
            if (!input.Keyboard || !ShortcutChord.IsModifier(input.Code)) { continue; }
            if (input.Pressed) { pressed[input.Code] = index; }
            else if (pressed.Remove(input.Code, out var start))
            {
                var containsControl = Enumerable.Range(start, index - start).Any(ignored.Contains);
                var used = Enumerable.Range(start + 1, index - start - 1).Any(i => !ignored.Contains(i) && (!source[i].Keyboard || !ShortcutChord.IsModifier(source[i].Code)));
                if (containsControl && !used) { ignored.Add(start); ignored.Add(index); }
            }
        }
        return source.Where((_, index) => !ignored.Contains(index)).ToArray();
    }
}

public static class ShortcutChord
{
    public static bool IsModifier(uint code) => code is 29 or 97 or 42 or 54 or 56 or 100 or 125 or 126;
    public static uint Canonical(uint code) => code switch { 97 => 29, 54 => 42, 100 => 56, 126 => 125, _ => code };
    public static IReadOnlyList<IReadOnlySet<uint>> ParseAlternatives(string trigger)
    {
        // KDE joins alternative bindings in one human-readable description.
        return trigger.Split(new[] { ',', ';', '\t' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Parse).Where(chord => chord.Count > 0).ToArray();
    }

    public static IReadOnlySet<uint> Parse(string trigger)
    {
        var keys = new HashSet<uint>();
        var parts = trigger.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var keypad = parts.Any(part => part.Equals("num", StringComparison.OrdinalIgnoreCase) || part.Equals("numpad", StringComparison.OrdinalIgnoreCase) || part.Equals("keypad", StringComparison.OrdinalIgnoreCase));
        const string letters = "qwertyuiopasdfghjklzxcvbnm";
        uint[] letterCodes = { 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 30, 31, 32, 33, 34, 35, 36, 37, 38, 44, 45, 46, 47, 48, 49, 50 };
        foreach (var part in parts)
        {
            var token = part.ToLowerInvariant();
            if (token is "num" or "numpad" or "keypad") { continue; }
            if (token.StartsWith("kp_", StringComparison.Ordinal)) { keypad = true; token = token[3..]; }
            uint code = token switch { "ctrl" or "control" => 29, "shift" => 42, "alt" => 56, "meta" or "super" => 125, "esc" or "escape" => 1, "space" => 57, "return" or "enter" => 28, "tab" => 15, "backspace" => 14, "insert" or "ins" => 110, "delete" or "del" => 111, "home" => 102, "end" => 107, "pgup" or "pageup" or "page up" => 104, "pgdown" or "pagedown" or "page down" => 109, "left" => 105, "right" => 106, "up" => 103, "down" => 108, "pause" => 119, "print" or "printscreen" or "print screen" => 99, _ => 0 };
            if (token.Length == 1 && letters.IndexOf(token[0]) is var letter && letter >= 0) { code = letterCodes[letter]; }
            if (token.Length == 1 && char.IsAsciiDigit(token[0])) { code = token[0] == '0' ? 11u : (uint)(token[0] - '1' + 2); }
            if (keypad && !IsModifier(code)) { code = KeypadCode(token); }
            if (token.StartsWith('f') && int.TryParse(token.AsSpan(1), out var function) && function is >= 1 and <= 24) { code = function <= 10 ? (uint)(58 + function) : function <= 12 ? (uint)(76 + function) : (uint)(170 + function); }
            if (code == 0) { return new HashSet<uint>(); }
            keys.Add(code);
        }
        return keys.Any(key => !IsModifier(key)) ? keys : new HashSet<uint>();
    }

    private static uint KeypadCode(string token) => token switch
    {
        "0" or "insert" or "ins" => 82,
        "1" or "end" => 79,
        "2" or "down" => 80,
        "3" or "pgdown" or "pagedown" or "page down" or "next" => 81,
        "4" or "left" => 75,
        "5" or "begin" or "clear" => 76,
        "6" or "right" => 77,
        "7" or "home" => 71,
        "8" or "up" => 72,
        "9" or "pgup" or "pageup" or "page up" or "prior" => 73,
        "return" or "enter" => 96,
        "." or "decimal" or "delete" or "del" => 83,
        _ => 0
    };
}
