using System;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;

namespace AutoClicker.Services;

public sealed class AutomationRunner : IDisposable
{
    private sealed class ProgressReporter
    {
        private readonly Action<long, double> callback;
        private double nextUpdateMs;
        public ProgressReporter(Action<long, double> callback) { this.callback = callback; }
        public void Report(long clicks, double elapsedMs, bool force = false)
        {
            if (!force && elapsedMs < nextUpdateMs) { return; }
            nextUpdateMs = elapsedMs + 75;
            callback(clicks, elapsedMs);
        }
    }

    private readonly IAutomationClock clock;
    private readonly Func<double> random;
    private readonly bool ownsClock;
    public AutomationRunner(IAutomationClock? clock = null, Func<double>? random = null)
    {
        this.clock = clock ?? new MonotonicClock();
        ownsClock = clock == null;
        this.random = random ?? Random.Shared.NextDouble;
    }

    public void Dispose() { if (ownsClock) { ((MonotonicClock)clock).Dispose(); } }

    public async Task<RunResult> ClickAsync(ClickerSettings settings, IInputPlayback input, Func<DesktopSnapshot?> snapshot, Action<long, double> progress, CancellationToken cancellationToken)
    {
        settings.Validate();
        var started = clock.NowMs;
        var deadline = settings.TimeLimitEnabled ? started + settings.DurationMs : double.PositiveInfinity;
        var reporter = new ProgressReporter(progress);
        var nextCycleStart = started;
        long completed = 0;
        var buttonHeld = false;
        string? reason = null;
        string? Stop()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.NowMs >= deadline) { return "Time limit reached"; }
            if (settings.ClickLimitEnabled && completed >= settings.ClickLimit) { return "Click limit reached"; }
            var desktop = snapshot() ?? throw new InvalidOperationException("Cursor access was lost.");
            return StopRegions.Check(settings, desktop);
        }
        async Task<bool> Wait(double until)
        {
            while (true)
            {
                reason = Stop();
                if (reason != null) { return false; }
                var remaining = until - clock.NowMs;
                if (remaining <= 0) { return true; }
                await clock.DelayAsync(Math.Max(0, Math.Min(20, Math.Min(remaining, deadline - clock.NowMs))), cancellationToken);
            }
        }
        try
        {
            while ((reason = Stop()) == null)
            {
                var cycleStart = clock.NowMs;
                var variation = settings.VariationEnabled ? settings.VariationPercent / 100 : 0;
                var interval = Math.Max(1000d / ClickerSettings.MaximumRate, settings.IntervalMs * (1 + (variation > 0 ? (Math.Clamp(random(), 0, 1) * 2 - 1) * variation : 0)));
                var hold = settings.HoldDurationMs(interval);
                if (cycleStart - nextCycleStart >= interval) { nextCycleStart = cycleStart; }
                for (var click = 0; click < (settings.DoubleClick ? 2 : 1); click++)
                {
                    reason = Stop();
                    if (reason != null) { break; }
                    if (settings.PositionEnabled)
                    {
                        var desktop = snapshot()!;
                        if (!System.Linq.Enumerable.Any(desktop.Monitors, monitor => StopRegions.Contains(monitor, settings.Position))) { throw new ArgumentException("Saved position is outside the connected monitors."); }
                        if (StopRegions.Check(settings, desktop with { Cursor = settings.Position }) is { } targetStop) { reason = targetStop; break; }
                        input.MoveTo(settings.Position);
                    }
                    input.SetButton(settings.ButtonCode, true);
                    buttonHeld = true;
                    if (!await Wait(clock.NowMs + hold)) { break; }
                    input.SetButton(settings.ButtonCode, false);
                    buttonHeld = false;
                    completed++;
                    reporter.Report(completed, clock.NowMs - started);
                    if (click == 0 && settings.DoubleClick && !await Wait(clock.NowMs + settings.DoubleDelayMs)) { break; }
                }
                if (reason != null) { break; }
                // Absorb small wake-up delays, and reset after overruns instead of accumulating overdue cycles.
                nextCycleStart = Math.Max(nextCycleStart + interval, clock.NowMs);
                if (!await Wait(nextCycleStart)) { break; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { reason = "Stopped"; }
        finally { input.ReleaseAll(); if (buttonHeld) { completed++; } reporter.Report(completed, clock.NowMs - started, force: true); }
        return new(completed, clock.NowMs - started, reason ?? "Stopped");
    }

    public async Task<RunResult> PlayAsync(MacroDefinition macro, IInputPlayback input, Action<long, double> progress, CancellationToken cancellationToken)
    {
        MacroValidation.Validate(macro);
        var started = clock.NowMs;
        var reporter = new ProgressReporter(progress);
        long clicks = 0;
        try
        {
            for (long repetition = 0; macro.Infinite || repetition < macro.Repeat; repetition++)
            {
                double previousOffset = 0;
                foreach (var item in macro.Events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Preserve gaps even after a slow dispatch instead of bursting overdue events.
                    await clock.DelayAsync(item.OffsetMs - previousOffset, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    previousOffset = item.OffsetMs;
                    if (item.Position != null && item.Pressed && !item.Keyboard) { input.MoveTo(item.Position); }
                    if (item.Keyboard) { input.SetKey(item.Code, item.Pressed); } else { input.SetButton(item.Code, item.Pressed); if (!item.Pressed) { clicks++; } }
                    reporter.Report(clicks, clock.NowMs - started);
                }
                input.ReleaseAll();
                await clock.DelayAsync(1, cancellationToken); // Bound empty-delay repetitions and keep cancellation responsive.
            }
            return new(clicks, clock.NowMs - started, "Macro finished");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(clicks, clock.NowMs - started, "Stopped"); }
        finally { input.ReleaseAll(); reporter.Report(clicks, clock.NowMs - started, force: true); }
    }
}
