using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;

namespace AutoClicker.Services;

public interface IPhysicalCapture : IAsyncDisposable
{
    long StartedMicroseconds { get; }
    event Action<PhysicalInputEvent>? Input;
    Task Completion { get; }
}

public interface IDesktopBackend : IAsyncDisposable
{
    bool Ready { get; }
    DesktopSnapshot? Snapshot { get; }
    IInputPlayback Playback { get; }
    IReadOnlyList<ShortcutBinding> Bindings { get; }
    event Action<ShortcutEvent>? Shortcut;
    event Action? BindingsChanged;
    event Action<Exception>? Lost;
    Task ConnectAsync(IReadOnlyList<ShortcutDefinition> shortcuts, Action<string> status, CancellationToken cancellationToken);
    Task BindShortcutsAsync(IReadOnlyList<ShortcutDefinition> shortcuts, CancellationToken cancellationToken);
    Task ConfigureShortcutsAsync(CancellationToken cancellationToken);
    Task ResetShortcutsAsync(IReadOnlyList<ShortcutDefinition> shortcuts, CancellationToken cancellationToken);
    bool HasRecordingPermissions(bool clicks, bool keyboard);
    Task GrantRecordingPermissionsAsync(bool clicks, bool keyboard, CancellationToken cancellationToken);
    Task<IPhysicalCapture> StartCaptureAsync(bool clicks, bool keyboard, CancellationToken cancellationToken);
}

public interface IAutomationClock
{
    double NowMs { get; }
    Task DelayAsync(double milliseconds, CancellationToken cancellationToken);
}

public interface IAppInteractions
{
    Task<bool> ConfirmAsync(string title, string message);
    Task<string?> NameRecordingAsync();
    Task<MacroDefinition?> EditMacroAsync(MacroDefinition macro);
    Task<DesktopPoint?> PickPositionAsync(CancellationToken cancellationToken);
}
