namespace AutoClicker.Views;

// UI preview data; runtime recording and playback state belong in the macro backend.
public sealed class MacroPreviewItem
{
    public string Name { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty;
    public decimal Repeat { get; set; }
    public bool Infinite { get; set; }
    public string Length { get; set; } = string.Empty;
    public string Created { get; set; } = string.Empty;
}
