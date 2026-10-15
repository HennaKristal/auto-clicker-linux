using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Platform.Linux;

namespace AutoClicker.Services;

public sealed class MonotonicClock : IAutomationClock, IDisposable
{
    private readonly object gate = new();
    private LinuxHighResolutionTimer? shortWaitTimer;
    private bool disposed;

    public double NowMs => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).TotalMilliseconds;

    public Task DelayAsync(double milliseconds, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (milliseconds <= 0) { return Task.CompletedTask; }
        if (OperatingSystem.IsLinux() && milliseconds < 2)
        {
            // Playback already runs on a worker; sleep it instead of polling Task.Delay(0).
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                shortWaitTimer ??= new LinuxHighResolutionTimer();
                shortWaitTimer.Wait(milliseconds, cancellationToken);
            }
            return Task.CompletedTask;
        }
        return Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Floor(milliseconds))), cancellationToken);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            shortWaitTimer?.Dispose();
        }
    }
}
