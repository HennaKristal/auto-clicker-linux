using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;
using AutoClicker.Services;

namespace AutoClicker.Platform.Linux;

internal sealed class LinuxDesktopBackend : IDesktopBackend
{
    private LinuxPortalConnection? portal;
    private RemoteDesktopSession? playback;
    private KWinCursorBridge? cursor;
    private GlobalShortcutSession? shortcuts;
    public bool Ready { get; private set; }
    public DesktopSnapshot? Snapshot => cursor?.Latest;
    public IInputPlayback Playback => playback?.Sender ?? throw new InvalidOperationException("Enable desktop input first.");
    public IReadOnlyList<ShortcutBinding> Bindings => shortcuts?.Bindings ?? Array.Empty<ShortcutBinding>();
    public event Action<ShortcutEvent>? Shortcut;
    public event Action? BindingsChanged;
    public event Action<Exception>? Lost;

    public async Task ConnectAsync(IReadOnlyList<ShortcutDefinition> definitions, Action<string> status, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || LibEvdevReader.geteuid() == 0) { throw new InvalidOperationException("Run AutoClicker as your normal Linux desktop user."); }
        if (IntPtr.Size != 8) { throw new PlatformNotSupportedException("AutoClicker requires 64-bit Linux."); }
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (!string.Equals(session, "wayland", StringComparison.OrdinalIgnoreCase)) { throw new NotSupportedException("AutoClicker requires a KDE Plasma Wayland session. Log into Plasma (Wayland) before enabling desktop input."); }
        if (Ready) { return; }
        await DisposeAsync();
        try
        {
            portal = new();
            await portal.ConnectAsync().WaitAsync(cancellationToken);
            cursor = new(portal);
            cursor.Lost += Fail;
            await cursor.StartAsync(cancellationToken);
            playback = await RemoteDesktopSession.OpenAsync(portal, cancellationToken);
            playback.Lost += Fail;
            status("Waiting for shortcut authorization");
            await BindShortcutsAsync(definitions, cancellationToken);
            await playback.Sender!.WaitUntilReadyAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Ready = true;
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task BindShortcutsAsync(IReadOnlyList<ShortcutDefinition> definitions, CancellationToken cancellationToken)
    {
        if (portal == null) { return; }
        try
        {
            var removed = Bindings.Where(binding => binding.Id.StartsWith("macro_", StringComparison.Ordinal) && !definitions.Any(definition => definition.Id == binding.Id)).Select(binding => binding.Id).ToArray();
            foreach (var id in removed) { await UnregisterAsync(portal, id, cancellationToken); }
            if (shortcuts != null) { shortcuts.Lost -= Fail; await shortcuts.DisposeAsync(); shortcuts = null; }
            shortcuts = await GlobalShortcutSession.OpenAsync(portal, cancellationToken, definitions);
            shortcuts.Changed += input => Shortcut?.Invoke(input);
            shortcuts.BindingsChanged += _ => BindingsChanged?.Invoke();
            shortcuts.Lost += Fail;
            BindingsChanged?.Invoke();
        }
        catch (Exception error) { Fail(error); throw; }
    }

    public Task ConfigureShortcutsAsync(CancellationToken cancellationToken) => shortcuts?.ConfigureAsync(cancellationToken) ?? throw new InvalidOperationException("Enable desktop input first.");
    public async Task ResetShortcutsAsync(IReadOnlyList<ShortcutDefinition> definitions, CancellationToken cancellationToken)
    {
        using var reset = new LinuxPortalConnection();
        await reset.ConnectAsync().WaitAsync(cancellationToken);
        foreach (var id in new[] { "activate", "record-start", "record-stop" }) { await UnregisterAsync(reset, id, cancellationToken); }
        if (Ready) { await BindShortcutsAsync(definitions, cancellationToken); }
    }
    private static Task UnregisterAsync(LinuxPortalConnection connection, string id, CancellationToken cancellationToken) => connection.Bus.CallMethodAsync(connection.Message("org.kde.kglobalaccel", "/kglobalaccel", "org.kde.KGlobalAccel", "unregister", "ss", (ref Tmds.DBus.Protocol.MessageWriter writer) => { writer.WriteString(LinuxPortalConnection.ApplicationId); writer.WriteString(id); })).WaitAsync(cancellationToken);
    public bool HasRecordingPermissions(bool clicks, bool keyboard) => PhysicalInputCapture.HasReadAccess(clicks, keyboard);
    public Task GrantRecordingPermissionsAsync(bool clicks, bool keyboard, CancellationToken cancellationToken) => PhysicalInputCapture.GrantReadAccessAsync(clicks, keyboard, cancellationToken);
    public Task<IPhysicalCapture> StartCaptureAsync(bool clicks, bool keyboard, CancellationToken cancellationToken) => PhysicalInputCapture.StartAsync(clicks, keyboard, cancellationToken);
    private void Fail(Exception error) { Ready = false; Lost?.Invoke(error); }

    public async ValueTask DisposeAsync()
    {
        Ready = false;
        if (shortcuts != null) { shortcuts.Lost -= Fail; await shortcuts.DisposeAsync(); shortcuts = null; }
        if (playback != null) { playback.Lost -= Fail; await playback.DisposeAsync(); playback = null; }
        if (cursor != null) { cursor.Lost -= Fail; await cursor.DisposeAsync(); cursor = null; }
        portal?.Dispose();
        portal = null;
    }
}
