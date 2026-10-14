using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;
using Tmds.DBus.Protocol;

namespace AutoClicker.Platform.Linux;

internal sealed class KWinCursorBridge : IPathMethodHandler, IAsyncDisposable
{
    private readonly LinuxPortalConnection portal;
    private readonly string pluginName = "autoclicker-cursor-" + Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource<DesktopSnapshot> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? scriptPath;
    private string? kwinOwner;
    private bool registered;
    private int disposed;
    private IDisposable? ownerWatch;
    private readonly BackendEventQueue callbacks = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    internal event Action<Exception>? Lost;
    internal DesktopSnapshot? Latest { get; private set; }
    public string Path => "/org/autoclicker/Cursor";
    public bool HandlesChildPaths => false;

    internal KWinCursorBridge(LinuxPortalConnection portal) { this.portal = portal; }

    internal async Task<DesktopSnapshot> StartAsync(CancellationToken cancellationToken)
    {
        kwinOwner = await portal.Bus.CallMethodAsync(portal.Message("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetNameOwner", "s", (ref MessageWriter writer) => writer.WriteString("org.kde.KWin")), (message, _) => message.GetBodyReader().ReadString());
        ownerWatch = await portal.Bus.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = "org.freedesktop.DBus", Path = "/org/freedesktop/DBus", Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged" }, (message, _) => { var reader = message.GetBodyReader(); return (Name: reader.ReadString(), Previous: reader.ReadString(), Current: reader.ReadString()); }, notification =>
        {
            if (Volatile.Read(ref disposed) != 0) { return; }
            if (notification.IsCompletion || notification.HasValue && notification.Value.Name == "org.kde.KWin" && notification.Value.Current != kwinOwner)
            {
                var error = notification.Exception ?? new InvalidOperationException("KWin cursor access was lost.");
                Latest = null;
                first.TrySetException(error);
                callbacks.Post(() => Lost?.Invoke(error));
            }
        }, emitOnCapturedContext: false, flags: LinuxPortalConnection.WatchFailures);
        portal.Bus.AddMethodHandler(this);
        registered = true;
        var template = await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "Platform/Linux/KWin/cursor.js"), cancellationToken);
        scriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), pluginName + ".js");
        await File.WriteAllTextAsync(scriptPath, template.Replace("__DESTINATION__", portal.Bus.UniqueName), cancellationToken);
        var scriptId = await portal.Bus.CallMethodAsync(portal.Message("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss", (ref MessageWriter writer) => { writer.WriteString(scriptPath); writer.WriteString(pluginName); }), (message, _) => message.GetBodyReader().ReadInt32());
        if (scriptId < 0) { throw new InvalidOperationException("KWin refused to load the cursor bridge."); }
        // Run only our script; Scripting.start() would also start unrelated installed scripts.
        await portal.Bus.CallMethodAsync(portal.Message("org.kde.KWin", "/Scripting/Script" + scriptId, "org.kde.kwin.Script", "run"));
        return await first.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.Request.InterfaceAsString != "org.autoclicker.Cursor" || context.Request.MemberAsString != "Report") { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
        if (context.Request.SenderAsString != kwinOwner) { context.ReplyError("org.freedesktop.DBus.Error.AccessDenied", "Only KWin may report cursor state."); return ValueTask.CompletedTask; }
        try
        {
            var json = context.Request.GetBodyReader().ReadString();
            var snapshot = JsonSerializer.Deserialize<DesktopSnapshot>(json, JsonOptions) ?? throw new JsonException("KWin returned an empty snapshot.");
            Latest = snapshot;
            first.TrySetResult(snapshot);
            using var reply = context.CreateReplyWriter(null);
            context.Reply(reply.CreateMessage());
        }
        catch (Exception error) { first.TrySetException(error); context.ReplyError("org.freedesktop.DBus.Error.Failed", error.Message); }
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        ownerWatch?.Dispose();
        try { if (scriptPath != null) { await portal.Bus.CallMethodAsync(portal.Message("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "unloadScript", "s", (ref MessageWriter writer) => writer.WriteString(pluginName))).WaitAsync(TimeSpan.FromSeconds(2)); } }
        catch { /* KWin may have gone away during logout. */ }
        finally
        {
            if (registered) { portal.Bus.RemoveMethodHandler(Path); registered = false; }
            if (scriptPath != null) { File.Delete(scriptPath); scriptPath = null; }
            callbacks.Dispose();
        }
    }
}
