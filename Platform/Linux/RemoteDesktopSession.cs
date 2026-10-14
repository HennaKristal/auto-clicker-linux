using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace AutoClicker.Platform.Linux;

internal sealed class RemoteDesktopSession : IAsyncDisposable
{
    private const string Interface = "org.freedesktop.portal.RemoteDesktop";
    private readonly object gate = new();
    private readonly LinuxPortalConnection portal;
    private readonly string session;
    private readonly CancellationTokenSource backendLifetime = new();
    private readonly BackendEventQueue callbacks = new();
    private IDisposable? closedWatch;
    private Exception? failure;
    private TaskCompletionSource? closing;
    internal LibEiInputSender? Sender { get; private set; }
    internal event Action<Exception>? Lost;

    private RemoteDesktopSession(LinuxPortalConnection portal, string session) { this.portal = portal; this.session = session; }

    internal static async Task<RemoteDesktopSession> OpenAsync(LinuxPortalConnection portal, CancellationToken cancellationToken)
    {
        if (await portal.VersionAsync(Interface).WaitAsync(cancellationToken) < 2) { throw new NotSupportedException("Remote Desktop portal version 2 with ConnectToEIS is required."); }
        var result = new RemoteDesktopSession(portal, await portal.CreateSessionAsync(Interface, cancellationToken));
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, result.backendLifetime.Token);
        try
        {
            result.closedWatch = await portal.WatchClosedAsync(result.session, result.Fail);
            // Keep one authorized connection for this process. Saving restore tokens belongs to persistence later.
            var options = new Dictionary<string, VariantValue> { ["types"] = 3u, ["persist_mode"] = 1u };
            await portal.RequestAsync(Interface, "SelectDevices", "oa{sv}", options, (ref MessageWriter writer) => { writer.WriteObjectPath(result.session); writer.WriteDictionary(options); }, opening.Token);
            options = new();
            var started = await portal.RequestAsync(Interface, "Start", "osa{sv}", options, (ref MessageWriter writer) => { writer.WriteObjectPath(result.session); writer.WriteString(""); writer.WriteDictionary(options); }, opening.Token);
            if (!started.TryGetValue("devices", out var devices) || (devices.GetUInt32() & 3) != 3) { throw new InvalidOperationException("The desktop must grant both keyboard and pointer access for AutoClicker."); }
            var descriptor = portal.Bus.CallMethodAsync(portal.Message(LinuxPortalConnection.Destination, LinuxPortalConnection.DesktopPath, Interface, "ConnectToEIS", "oa{sv}", (ref MessageWriter writer) => { writer.WriteObjectPath(result.session); writer.WriteDictionary(new Dictionary<string, VariantValue>()); }), (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>());
            using var fd = await ReceiveDescriptorAsync(descriptor, opening.Token);
            lock (result.gate)
            {
                opening.Token.ThrowIfCancellationRequested();
                result.Sender = new LibEiInputSender(fd);
                result.Sender.Lost += result.Fail;
            }
            await result.Sender.WaitUntilReadyAsync(opening.Token);
            opening.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && result.failure != null)
        {
            await result.DisposeAsync();
            throw new InvalidOperationException("Desktop playback authorization was lost during connection setup.", result.failure);
        }
        catch { await result.DisposeAsync(); throw; }
    }

    private static async Task<SafeFileHandle> ReceiveDescriptorAsync(Task<SafeFileHandle> request, CancellationToken cancellationToken)
    {
        try { return await request.WaitAsync(cancellationToken); }
        catch { _ = DisposeLateDescriptorAsync(request); throw; }
    }

    private static async Task DisposeLateDescriptorAsync(Task<SafeFileHandle> request)
    {
        // D-Bus cannot retract a method reply. Own and close a descriptor that arrives after cancellation.
        try { using var descriptor = await request; }
        catch { /* A failed request has no descriptor to release. */ }
    }

    private void Fail(Exception error)
    {
        LibEiInputSender? sender;
        lock (gate)
        {
            if (failure != null || closing != null) { return; }
            failure = error;
            backendLifetime.Cancel();
            sender = Sender;
        }
        sender?.Invalidate(error);
        callbacks.Post(() => Lost?.Invoke(error));
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (closing != null) { return new ValueTask(closing.Task); }
            completion = closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            backendLifetime.Cancel();
        }
        _ = CloseAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task CloseAsync(TaskCompletionSource completion)
    {
        try { closedWatch?.Dispose(); Sender?.Dispose(); await portal.CloseAsync(session); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { backendLifetime.Dispose(); callbacks.Dispose(); }
    }
}
