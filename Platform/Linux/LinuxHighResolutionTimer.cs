using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AutoClicker.Platform.Linux;

// timerfd sleeps without rounding short waits to whole milliseconds.
internal sealed class LinuxHighResolutionTimer : IDisposable
{
    private const int MonotonicClock = 1;
    private const int CloseOnExecute = 0x80000;
    private const int Interrupted = 4;
    private readonly SafeFileHandle descriptor;

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpecification
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimerSpecification
    {
        public TimeSpecification Interval;
        public TimeSpecification Value;
    }

    public LinuxHighResolutionTimer()
    {
        var handle = timerfd_create(MonotonicClock, CloseOnExecute);
        if (handle < 0) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the playback timer."); }
        descriptor = new SafeFileHandle(handle, true);
    }

    public void Wait(double milliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nanoseconds = Math.Max(1, (long)Math.Ceiling(milliseconds * 1_000_000));
        var specification = new TimerSpecification
        {
            Value = new TimeSpecification { Seconds = nanoseconds / 1_000_000_000, Nanoseconds = nanoseconds % 1_000_000_000 }
        };
        if (timerfd_settime(descriptor, 0, in specification, 0) != 0) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not arm the playback timer."); }
        while (true)
        {
            var received = read(descriptor, out _, 8);
            if (received == 8) { break; }
            var error = Marshal.GetLastPInvokeError();
            cancellationToken.ThrowIfCancellationRequested();
            if (received < 0 && error == Interrupted) { continue; }
            throw new Win32Exception(error, "Could not wait for the playback timer.");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose() => descriptor.Dispose();

    [DllImport("libc", SetLastError = true)]
    private static extern int timerfd_create(int clock, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int timerfd_settime(SafeFileHandle descriptor, int flags, in TimerSpecification specification, nint previous);
    [DllImport("libc", SetLastError = true)]
    private static extern nint read(SafeFileHandle descriptor, out ulong expirations, nuint count);
}
