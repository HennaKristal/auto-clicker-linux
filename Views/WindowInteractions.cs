using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;
using AutoClicker.Services;
using AutoClicker.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AutoClicker.Views;

// Dialogs and the desktop picking overlay are presentation concerns, separate from input services.
public sealed class WindowInteractions : IAppInteractions
{
    private readonly Window owner;
    private readonly Func<DesktopSnapshot?> snapshot;
    private readonly List<Window> dialogs = new();
    public MainViewModel? ViewModel { get; set; }
    public WindowInteractions(Window owner, Func<DesktopSnapshot?> snapshot) { this.owner = owner; this.snapshot = snapshot; }
    private Window Dialog(string title, double height = 220) => new() { Title = title, Width = 520, Height = height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#17191d")), Content = new StackPanel { Margin = new Thickness(22), Spacing = 12 } };
    private Task<T> ShowAsync<T>(Window window) { dialogs.Add(window); window.Closed += (_, _) => dialogs.Remove(window); return window.ShowDialog<T>(owner); }
    public void CloseDialogs() { foreach (var dialog in dialogs.ToArray()) { dialog.Close(); } }
    public Task ShowSaveErrorAsync(string message)
    {
        var window = Dialog("Changes could not be saved", 260);
        var panel = (StackPanel)window.Content!;
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Button("Close application", () => window.Close(true)));
        return ShowAsync<bool>(window);
    }
    private static Button Button(string text, Action action) { var button = new Button { Content = text }; button.Click += (_, _) => action(); return button; }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        var window = Dialog(title, 235);
        var panel = (StackPanel)window.Content!;
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button("Cancel", () => window.Close(false)));
        buttons.Children.Add(Button("Confirm", () => window.Close(true)));
        panel.Children.Add(buttons);
        return ShowAsync<bool>(window);
    }

    public Task<string?> NameRecordingAsync()
    {
        var window = Dialog("Save recording");
        var panel = (StackPanel)window.Content!;
        panel.Children.Add(new TextBlock { Text = "Macro name" });
        var name = new TextBox { MaxLength = 120, Text = "New macro" };
        panel.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button("Discard", () => window.Close(null)));
        buttons.Children.Add(Button("Save", () => { if (!string.IsNullOrWhiteSpace(name.Text)) { window.Close(name.Text.Trim()); } }));
        panel.Children.Add(buttons);
        window.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        return ShowAsync<string?>(window);
    }

    public Task<MacroDefinition?> EditMacroAsync(MacroDefinition macro)
    {
        var window = Dialog("Edit macro", 610);
        window.CanResize = true;
        window.MinWidth = 520;
        window.MinHeight = 420;
        var editor = new MacroEditorViewModel(macro);
        var panel = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 10 };
        window.Content = panel;
        var name = new TextBox { MaxLength = 120, PlaceholderText = "Macro name" };
        name.Bind(TextBox.TextProperty, new Binding(nameof(editor.Name)) { Source = editor, Mode = BindingMode.TwoWay });
        panel.Children.Add(name);
        var repeatPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        repeatPanel.Children.Add(new TextBlock { Text = "Repeat", VerticalAlignment = VerticalAlignment.Center });
        var repeat = new NumericUpDown { Minimum = 1, Maximum = 1_000_000, Width = 100 };
        repeat.Bind(NumericUpDown.ValueProperty, new Binding(nameof(editor.Repeat)) { Source = editor, Mode = BindingMode.TwoWay });
        repeat.Bind(Control.IsEnabledProperty, new Binding("!Infinite") { Source = editor });
        repeatPanel.Children.Add(repeat);
        var infinite = new CheckBox { Content = "Infinite" };
        infinite.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(editor.Infinite)) { Source = editor, Mode = BindingMode.TwoWay });
        repeatPanel.Children.Add(infinite);
        Grid.SetRow(repeatPanel, 1);
        panel.Children.Add(repeatPanel);
        var shortcut = new Button { Content = "Edit play hotkey in KDE", IsEnabled = ViewModel?.BackendReady == true };
        shortcut.Click += async (_, _) => { if (ViewModel != null) { await ViewModel.ConfigureFromDialogAsync(); } };
        Grid.SetRow(shortcut, 2);
        panel.Children.Add(shortcut);
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var events = new ListBox { ItemsSource = editor.Events };
        events.ItemTemplate = new FuncDataTemplate<EventEditorRow>((row, _) =>
        {
            // Recycled containers temporarily have no row.
            if (row == null) { return null; }
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,105,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 3) };
            grid.Children.Add(new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center });
            var delay = new NumericUpDown { Minimum = 0, Maximum = 600_000, Width = 105, FormatString = "0.##" };
            delay.Bind(NumericUpDown.ValueProperty, new Binding(nameof(row.DelayMs)) { Source = row, Mode = BindingMode.TwoWay });
            ToolTip.SetTip(delay, "Delay before this event (milliseconds)");
            Grid.SetColumn(delay, 1);
            grid.Children.Add(delay);
            var remove = Button("Delete", () => { try { editor.RemovePair(row); error.Text = ""; } catch (ArgumentException failure) { error.Text = failure.Message; } });
            ToolTip.SetTip(remove, "Remove this press and its matching release");
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
            return grid;
        });
        Grid.SetRow(events, 3);
        panel.Children.Add(events);
        Grid.SetRow(error, 4);
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button("Cancel", () => window.Close(null)));
        buttons.Children.Add(Button("Save", () => { try { window.Close(editor.Build()); } catch (Exception failure) { error.Text = failure.Message; } }));
        Grid.SetRow(buttons, 5);
        panel.Children.Add(buttons);
        return ShowAsync<MacroDefinition?>(window);
    }

    public Task<DesktopPoint?> PickPositionAsync(CancellationToken cancellationToken)
    {
        if (snapshot() == null) { throw new InvalidOperationException("Cursor position is unavailable."); }
        if (owner.Screens.All.Count == 0) { throw new InvalidOperationException("No screens are available for position picking."); }
        var result = new TaskCompletionSource<DesktopPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays = new List<Window>();
        foreach (var screen in owner.Screens.All)
        {
            // Window placement uses Avalonia's screen space; saved input uses KWin's logical coordinates.
            var overlay = new Window
            {
                Width = screen.Bounds.Width / screen.Scaling,
                Height = screen.Bounds.Height / screen.Scaling,
                Position = screen.Bounds.Position,
                WindowDecorations = WindowDecorations.None,
                Topmost = true,
                ShowInTaskbar = false,
                CanResize = false,
                Background = new SolidColorBrush(Color.FromArgb(24, 65, 140, 230)),
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                Cursor = new Cursor(StandardCursorType.Cross),
                Title = "Pick click position"
            };
            overlay.Content = new TextBlock { Text = "Click to save this position - Escape to cancel", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.Parse("#17191d")), Padding = new Thickness(16) };
            overlay.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(overlay).Properties.IsLeftButtonPressed) { return; }
                if (snapshot() is { } current) { result.TrySetResult(current.Cursor); }
                else { result.TrySetException(new InvalidOperationException("Cursor position is unavailable.")); }
                args.Handled = true;
            };
            overlay.KeyDown += (_, args) => { if (args.Key == Key.Escape) { result.TrySetResult(null); args.Handled = true; } };
            overlay.Closed += (_, _) => result.TrySetResult(null);
            overlays.Add(overlay);
            dialogs.Add(overlay);
        }
        foreach (var overlay in overlays) { overlay.Show(owner); }
        var cancellation = cancellationToken.Register(() => result.TrySetCanceled(cancellationToken));
        return FinishPickAsync();
        async Task<DesktopPoint?> FinishPickAsync()
        {
            try { return await result.Task; }
            finally { cancellation.Dispose(); foreach (var overlay in overlays) { dialogs.Remove(overlay); overlay.Close(); } }
        }
    }
}
