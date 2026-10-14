using System;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;

namespace AutoClicker.Services;

// One owner serializes transitions. Native input is released before another activity can start.
public sealed class AutomationController : IAsyncDisposable
{
    private readonly SemaphoreSlim transitions = new(1, 1);
    private readonly IInputPlayback input;
    private CancellationTokenSource? activity;
    private Task? running;
    private bool disposed;
    private long generation;
    public RunProgress Progress { get; private set; } = new(AutomationState.Idle, 0, 0, "Ready");
    public event Action<RunProgress>? Changed;
    public event Action<AutomationState, RunResult>? Finished;
    public AutomationController(IInputPlayback input) { this.input = input; }

    public async Task StartAsync(AutomationState state, Func<CancellationToken, Task<RunResult>> work, Guid? macroId = null)
    {
        await transitions.WaitAsync();
        try
        {
            if (disposed) { throw new ObjectDisposedException(nameof(AutomationController)); }
            await StopCoreAsync();
            activity = new();
            var token = activity.Token;
            var currentGeneration = ++generation;
            Publish(new(state, 0, 0, state == AutomationState.Recording ? "Recording" : state == AutomationState.PlayingMacro ? "Playing macro" : "Clicking", macroId, Generation: currentGeneration));
            running = Task.Run(async () =>
            {
                RunResult? result = null;
                Exception? error = null;
                try { result = await work(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new(Progress.Clicks, Progress.ElapsedMs, "Stopped"); }
                catch (Exception failure) { error = failure; result = new(Progress.Clicks, Progress.ElapsedMs, "Stopped by an error"); }
                finally
                {
                    try { input.ReleaseAll(); } catch (Exception failure) { error ??= failure; }
                    if (result != null) { Finished?.Invoke(state, result); }
                    Publish(new(AutomationState.Idle, result?.Clicks ?? Progress.Clicks, result?.ElapsedMs ?? Progress.ElapsedMs, error?.Message ?? result?.Reason ?? "Stopped", Error: error, Generation: currentGeneration));
                }
            });
        }
        finally { transitions.Release(); }
    }

    public void Report(long clicks, double elapsed) { Publish(Progress with { Clicks = clicks, ElapsedMs = elapsed }); }
    public void MarkRunning() { if (Progress.State == AutomationState.Starting) { Publish(Progress with { State = AutomationState.Clicking, Message = "Clicking" }); } }
    private void Publish(RunProgress progress) { Progress = progress; Changed?.Invoke(progress); }
    public void Cancel() { try { activity?.Cancel(); } catch (ObjectDisposedException) { } }

    public async Task StopAsync()
    {
        Cancel();
        await transitions.WaitAsync();
        try { await StopCoreAsync(); }
        finally { transitions.Release(); }
    }

    private async Task StopCoreAsync()
    {
        if (running != null && !running.IsCompleted) { Publish(Progress with { State = AutomationState.Stopping, Message = "Stopping" }); activity?.Cancel(); await running; }
        running = null;
        activity?.Dispose();
        activity = null;
        input.ReleaseAll();
    }

    public async ValueTask DisposeAsync() { await transitions.WaitAsync(); try { disposed = true; await StopCoreAsync(); } finally { transitions.Release(); } }
}
