using System;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AutoClicker.Platform.Linux;

// Application callbacks run sequentially outside the native pump and D-Bus reader.
// A callback may close its backend; shutdown must never wait for this callback queue itself.
internal sealed class BackendEventQueue : IDisposable
{
    private readonly Channel<Action> events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    internal BackendEventQueue() { _ = Task.Run(DispatchAsync); }
    internal void Post(Action callback) => events.Writer.TryWrite(callback);

    private async Task DispatchAsync()
    {
        await foreach (var callback in events.Reader.ReadAllAsync())
        {
            try { callback(); }
            catch (Exception error) { Trace.TraceError("Input backend callback failed: {0}", error); }
        }
    }

    public void Dispose() => events.Writer.TryComplete();
}
