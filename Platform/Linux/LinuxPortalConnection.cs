using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace AutoClicker.Platform.Linux;

internal delegate void WriteBody(ref MessageWriter writer);
internal sealed record PortalResponse(uint Code, Dictionary<string, VariantValue> Results);

internal sealed class LinuxPortalConnection : IDisposable
{
    internal const string Destination = "org.freedesktop.portal.Desktop";
    internal const string ApplicationId = "org.hennakristal.AutoClicker";
    internal const string DesktopPath = "/org/freedesktop/portal/desktop";
    internal const ObserverFlags WatchFailures = ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnOwnerChanged | ObserverFlags.EmitOnReaderFailed;
    internal DBusConnection Bus { get; } = new(DBusAddress.Session!);

    internal async Task ConnectAsync()
    {
        await Bus.ConnectAsync();
        // dotnet launched by an editor inherits the editor's desktop identity unless registered first.
        await Bus.CallMethodAsync(Message(Destination, DesktopPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}", (ref MessageWriter writer) => { writer.WriteString(ApplicationId); writer.WriteDictionary(new Dictionary<string, VariantValue>()); }));
    }

    internal MessageBuffer Message(string destination, string path, string iface, string member, string? signature = null, WriteBody? body = null)
    {
        var writer = Bus.GetMessageWriter();
        try { writer.WriteMethodCallHeader(destination: destination, path: path, @interface: iface, member: member, signature: signature); body?.Invoke(ref writer); return writer.CreateMessage(); }
        finally { writer.Dispose(); }
    }

    internal async Task<uint> VersionAsync(string iface)
    {
        return await Bus.CallMethodAsync(Message(Destination, DesktopPath, "org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter writer) => { writer.WriteString(iface); writer.WriteString("version"); }), (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32());
    }

    internal async Task<Dictionary<string, VariantValue>> RequestAsync(string iface, string member, string signature, Dictionary<string, VariantValue> options, WriteBody body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = "autoclicker_" + Guid.NewGuid().ToString("N");
        var sender = Bus.UniqueName!.TrimStart(':').Replace('.', '_');
        var requestPath = DesktopPath + "/request/" + sender + "/" + token;
        options["handle_token"] = token;
        var completion = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe before the method call: a portal can send Response before its method reply.
        using var watch = await Bus.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = Destination, Path = requestPath, Interface = "org.freedesktop.portal.Request", Member = "Response" }, (message, _) => { var reader = message.GetBodyReader(); return new PortalResponse(reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue()); }, notification => { if (notification.IsCompletion) { completion.TrySetException(notification.Exception ?? new InvalidOperationException("Portal connection closed.")); } else if (notification.HasValue) { completion.TrySetResult(notification.Value); } }, emitOnCapturedContext: false, flags: WatchFailures);
        var sent = false;
        try
        {
            var reply = Bus.CallMethodAsync(Message(Destination, DesktopPath, iface, member, signature, body), (message, _) => message.GetBodyReader().ReadObjectPath().ToString());
            sent = true;
            var returnedPath = await reply.WaitAsync(cancellationToken);
            if (returnedPath != requestPath) { throw new InvalidOperationException("The portal did not honor handle_token; this backend requires modern portal request paths."); }
            var response = await completion.Task.WaitAsync(cancellationToken);
            if (response.Code != 0) { throw new InvalidOperationException(response.Code == 1 ? "Desktop authorization was cancelled." : "The desktop denied or failed the request."); }
            return response.Results;
        }
        catch
        {
            if (sent) { await CloseAsync(requestPath, "org.freedesktop.portal.Request"); }
            throw;
        }
    }

    internal async Task<string> CreateSessionAsync(string iface, CancellationToken cancellationToken)
    {
        var options = new Dictionary<string, VariantValue> { ["session_handle_token"] = "autoclicker_" + Guid.NewGuid().ToString("N") };
        try
        {
            var results = await RequestAsync(iface, "CreateSession", "a{sv}", options, (ref MessageWriter writer) => writer.WriteDictionary(options), cancellationToken);
            return results["session_handle"].GetString();
        }
        catch
        {
            // The session can already exist when cancellation races the CreateSession response.
            var sender = Bus.UniqueName!.TrimStart(':').Replace('.', '_');
            await CloseAsync(DesktopPath + "/session/" + sender + "/" + options["session_handle_token"].GetString());
            throw;
        }
    }

    internal async Task CloseAsync(string path, string iface = "org.freedesktop.portal.Session")
    {
        try { await Bus.CallMethodAsync(Message(Destination, path, iface, "Close")).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* Closing an already revoked session or a disconnected bus is best effort. */ }
    }

    internal ValueTask<IDisposable> WatchClosedAsync(string session, Action<Exception> handler)
    {
        return Bus.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = Destination, Path = session, Interface = "org.freedesktop.portal.Session", Member = "Closed" }, (_, _) => true, notification => { if (notification.IsCompletion) { handler(notification.Exception ?? new InvalidOperationException("Portal connection closed.")); } else if (notification.HasValue) { handler(new InvalidOperationException("Desktop input authorization was revoked.")); } }, emitOnCapturedContext: false, flags: WatchFailures);
    }

    public void Dispose() => Bus.Dispose();
}
