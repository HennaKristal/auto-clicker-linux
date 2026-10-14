using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;
using Tmds.DBus.Protocol;

namespace AutoClicker.Platform.Linux;

internal sealed class GlobalShortcutSession : IAsyncDisposable
{
    private const string Interface = "org.freedesktop.portal.GlobalShortcuts";
    private readonly LinuxPortalConnection portal;
    private readonly string session;
    private readonly List<IDisposable> watches = new();
    private readonly HashSet<string> pressed = new();
    private readonly HashSet<string> deliveredPresses = new();
    private readonly CancellationTokenSource backendLifetime = new();
    private readonly BackendEventQueue callbacks = new();
    private Exception? failure;
    private TaskCompletionSource? closing;
    internal event Action<ShortcutEvent>? Changed;
    internal event Action<IReadOnlyList<ShortcutBinding>>? BindingsChanged;
    internal event Action<Exception>? Lost;
    internal IReadOnlyList<ShortcutBinding> Bindings { get; private set; } = System.Array.Empty<ShortcutBinding>();

    private GlobalShortcutSession(LinuxPortalConnection portal, string session) { this.portal = portal; this.session = session; }

    internal static async Task<GlobalShortcutSession> OpenAsync(LinuxPortalConnection portal, CancellationToken cancellationToken, IReadOnlyList<ShortcutDefinition>? definitions = null)
    {
        if (await portal.VersionAsync(Interface).WaitAsync(cancellationToken) < 1) { throw new NotSupportedException("The desktop does not support the Global Shortcuts portal."); }
        var result = new GlobalShortcutSession(portal, await portal.CreateSessionAsync(Interface, cancellationToken));
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, result.backendLifetime.Token);
        try
        {
            foreach (var member in new[] { "Activated", "Deactivated" })
            {
                var isPress = member == "Activated";
                result.watches.Add(await portal.Bus.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = LinuxPortalConnection.Destination, Path = LinuxPortalConnection.DesktopPath, Interface = Interface, Member = member }, (message, _) => { var reader = message.GetBodyReader(); return (Session: reader.ReadObjectPath().ToString(), Id: reader.ReadString(), Timestamp: reader.ReadUInt64()); }, notification =>
                {
                    if (notification.IsCompletion) { result.Fail(notification.Exception ?? new InvalidOperationException("Shortcut connection closed.")); return; }
                    if (!notification.HasValue) { return; }
                    var value = notification.Value;
                    if (value.Session != result.session) { return; }
                    bool changed;
                    lock (result.pressed)
                    {
                        if (result.failure != null || result.closing != null) { return; }
                        changed = isPress ? result.pressed.Add(value.Id) : result.pressed.Remove(value.Id);
                        // Preserve ordering, including a matching release when authorization disappears during Hold.
                        if (changed) { result.callbacks.Post(() => result.PublishShortcut(new(value.Id, isPress, value.Timestamp))); }
                    }
                }, emitOnCapturedContext: false, flags: LinuxPortalConnection.WatchFailures));
            }
            result.watches.Add(await portal.Bus.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = LinuxPortalConnection.Destination, Path = LinuxPortalConnection.DesktopPath, Interface = Interface, Member = "ShortcutsChanged" }, (message, _) => { var reader = message.GetBodyReader(); var path = reader.ReadObjectPath().ToString(); return (Session: path, Bindings: ReadBindings(ref reader)); }, notification => { if (notification.IsCompletion) { result.Fail(notification.Exception ?? new InvalidOperationException("Shortcut connection closed.")); } else if (notification.HasValue && notification.Value.Session == result.session) { result.Bindings = notification.Value.Bindings; result.callbacks.Post(() => result.BindingsChanged?.Invoke(notification.Value.Bindings)); } }, emitOnCapturedContext: false, flags: LinuxPortalConnection.WatchFailures));
            result.watches.Add(await portal.WatchClosedAsync(result.session, result.Fail));

            var options = new Dictionary<string, VariantValue>();
            var response = await portal.RequestAsync(Interface, "BindShortcuts", "oa(sa{sv})sa{sv}", options, (ref MessageWriter writer) =>
            {
                writer.WriteObjectPath(result.session);
                var array = writer.WriteArrayStart(DBusType.Struct);
                foreach (var shortcut in definitions ?? new[] { new ShortcutDefinition("activate", "Toggle / hold clicking", "CTRL+SHIFT+F8"), new ShortcutDefinition("record-start", "Start recording", "CTRL+SHIFT+F9"), new ShortcutDefinition("record-stop", "Stop recording", "CTRL+SHIFT+F10") })
                {
                    writer.WriteStructureStart();
                    writer.WriteString(shortcut.Id);
                    var properties = new Dictionary<string, VariantValue> { ["description"] = shortcut.Description };
                    if (!string.IsNullOrWhiteSpace(shortcut.PreferredTrigger)) { properties["preferred_trigger"] = shortcut.PreferredTrigger; }
                    writer.WriteDictionary(properties);
                }
                writer.WriteArrayEnd(array);
                writer.WriteString("");
                writer.WriteDictionary(options);
            }, opening.Token);
            result.Bindings = response.TryGetValue("shortcuts", out var bindings) ? ReadBindings(bindings) : System.Array.Empty<ShortcutBinding>();
            opening.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && result.failure != null)
        {
            await result.DisposeAsync();
            throw new InvalidOperationException("Desktop shortcut authorization was lost during connection setup.", result.failure);
        }
        catch { await result.DisposeAsync(); throw; }
    }

    private static IReadOnlyList<ShortcutBinding> ReadBindings(ref Reader reader)
    {
        var result = new List<ShortcutBinding>();
        var end = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(end)) { reader.AlignStruct(); var id = reader.ReadString(); var properties = reader.ReadDictionaryOfStringToVariantValue(); result.Add(new(id, properties.TryGetValue("trigger_description", out var value) ? value.GetString() : "")); }
        return result;
    }

    private static IReadOnlyList<ShortcutBinding> ReadBindings(VariantValue value)
    {
        var result = new List<ShortcutBinding>();
        for (var index = 0; index < value.Count; index++) { var entry = value.GetItem(index); var properties = entry.GetItem(1).GetDictionary<string, VariantValue>(); result.Add(new(entry.GetItem(0).GetString(), properties.TryGetValue("trigger_description", out var trigger) ? trigger.GetString() : "")); }
        return result;
    }

    internal async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        if (await portal.VersionAsync(Interface).WaitAsync(cancellationToken) < 2) { throw new NotSupportedException("This KDE setup needs Global Shortcuts portal version 2 to edit the registered bindings."); }
        lock (pressed) { if (failure != null || closing != null) { throw new InvalidOperationException("The shortcut session is no longer available.", failure); } }
        await portal.Bus.CallMethodAsync(portal.Message(LinuxPortalConnection.Destination, LinuxPortalConnection.DesktopPath, Interface, "ConfigureShortcuts", "osa{sv}", (ref MessageWriter writer) => { writer.WriteObjectPath(session); writer.WriteString(""); writer.WriteDictionary(new Dictionary<string, VariantValue>()); })).WaitAsync(cancellationToken);
    }

    private void ReleasePressed()
    {
        foreach (var id in pressed.Union(deliveredPresses).ToArray()) { callbacks.Post(() => PublishShortcut(new(id, false, 0))); }
        pressed.Clear();
    }

    private void PublishShortcut(ShortcutEvent input)
    {
        lock (pressed)
        {
            // A queued activation must not start automation after its shortcut session has closed.
            if (input.Pressed) { if (failure != null || closing != null) { return; } deliveredPresses.Add(input.Id); }
            else if (!deliveredPresses.Remove(input.Id)) { return; }
        }
        Changed?.Invoke(input);
    }

    private void Fail(Exception error)
    {
        lock (pressed)
        {
            if (failure != null || closing != null) { return; }
            failure = error;
            backendLifetime.Cancel();
            ReleasePressed();
            callbacks.Post(() => Lost?.Invoke(error));
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (pressed)
        {
            if (closing != null) { return new ValueTask(closing.Task); }
            completion = closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            backendLifetime.Cancel();
            ReleasePressed();
        }
        _ = CloseAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task CloseAsync(TaskCompletionSource completion)
    {
        try { foreach (var watch in watches) { watch.Dispose(); } watches.Clear(); await portal.CloseAsync(session); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { backendLifetime.Dispose(); callbacks.Dispose(); }
    }
}
