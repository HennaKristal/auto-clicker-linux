using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;

namespace AutoClicker.Platform.Linux;

internal static class LibEvdevReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent { internal nint Seconds; internal nint Microseconds; internal ushort Type; internal ushort Code; internal int Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InputMask { internal uint EventType; internal uint CodesSize; internal nint Codes; }
    private sealed record Device(string Path, int FileDescriptor, nint Context);
    private const string Library = "libevdev.so.2";
    private const uint NormalRead = 2;
    private const uint SynchronizeRead = 1;
    private const int SynchronizationRequired = 1;
    private const int NoEventsAvailable = -11;
    private const uint SetEventMask = 0x40104593;
    [DllImport(Library)] private static extern int libevdev_new_from_fd(int fd, out nint context);
    [DllImport(Library)] private static extern void libevdev_free(nint context);
    [DllImport(Library)] private static extern int libevdev_set_clock_id(nint context, int clockId);
    [DllImport(Library)] private static extern int libevdev_next_event(nint context, uint flags, out InputEvent inputEvent);
    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fileDescriptor, uint request, ref InputMask mask);
    [DllImport("libc")] private static extern int close(int fd);
    [DllImport("libc")] internal static extern uint geteuid();

    internal static bool IsRecordable(ushort type, ushort code) => type == 1 && (code is > 0 and < 0x100 || code is 0x110 or 0x111 or 0x112);

    private static void FilterRecordingEvents(int fileDescriptor)
    {
        var eventTypes = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            // Queue keys and buttons only; synchronization events remain enabled.
            Marshal.WriteInt64(eventTypes, 1L << 1);
            var mask = new InputMask { EventType = 0, CodesSize = sizeof(long), Codes = eventTypes };
            if (ioctl(fileDescriptor, SetEventMask, ref mask) != 0) { throw new IOException("Could not filter recording events (errno " + Marshal.GetLastPInvokeError() + ")."); }
        }
        finally { Marshal.FreeHGlobal(eventTypes); }
    }

    private static void ClearPendingEvents(Device device)
    {
        var mode = NormalRead;
        while (true)
        {
            var result = libevdev_next_event(device.Context, mode, out _);
            if (result == NoEventsAvailable && mode == SynchronizeRead) { mode = NormalRead; continue; }
            if (result == NoEventsAvailable) { return; }
            if (result < 0) { throw new IOException("Could not prepare recording device: " + device.Path + " (errno " + -result + ")."); }
            mode = result == SynchronizationRequired ? SynchronizeRead : NormalRead;
        }
    }

    internal static bool IsPhysicalDevice(string path)
    {
        if (System.IO.Path.GetDirectoryName(path) != "/dev/input" || !System.IO.Path.GetFileName(path).StartsWith("event", StringComparison.Ordinal)) { return false; }
        var sysfs = new DirectoryInfo("/sys/class/input/" + System.IO.Path.GetFileName(path));
        var target = sysfs.ResolveLinkTarget(true)?.FullName;
        return target != null && !target.StartsWith("/sys/devices/virtual/", StringComparison.Ordinal);
    }

    internal static int Run(string[] arguments)
    {
        var devices = new List<Device>();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) { throw new PlatformNotSupportedException("The input reader requires 64-bit Linux."); }
            if (geteuid() == 0) { throw new InvalidOperationException("Run AutoClicker and its input reader as your normal desktop user."); }
            if (arguments.Length < 2) { throw new ArgumentException("Explicit /dev/input/eventN paths are required."); }
            // Prepare serialization before input queues start receiving events.
            _ = JsonSerializer.Serialize(new PhysicalInputEvent("", 0, 1, 0, 0));
            foreach (var path in arguments.Skip(1).Distinct())
            {
                if (!IsPhysicalDevice(path)) { throw new ArgumentException("Not a physical input device: " + path); }
                var fileDescriptor = open(path, 0x800 | 0x80000); // O_RDONLY | O_NONBLOCK | O_CLOEXEC, never EVIOCGRAB.
                if (fileDescriptor < 0) { throw new UnauthorizedAccessException("Cannot read " + path + ". Grant your normal user read access to this device first (errno " + Marshal.GetLastPInvokeError() + ")."); }
                var result = libevdev_new_from_fd(fileDescriptor, out var context);
                if (result < 0) { close(fileDescriptor); throw new IOException("libevdev could not initialize " + path + " (errno " + -result + ")."); }
                devices.Add(new(path, fileDescriptor, context));
                FilterRecordingEvents(fileDescriptor);
                if (libevdev_set_clock_id(context, 1) < 0) { throw new IOException("Could not select CLOCK_MONOTONIC for " + path + "."); }
            }
            // Changing the clock can queue SYN_DROPPED before recording begins.
            foreach (var device in devices) { ClearPendingEvents(device); }
            Console.Error.WriteLine("Reader ready: " + string.Join(", ", devices.Select(device => device.Path)));
            // The parent closes stdin to stop the helper. No device privileges are elevated or inherited from root.
            _ = Task.Run(() => { Console.ReadLine(); cancellation.Cancel(); });
            var count = 0;
            var timer = Stopwatch.StartNew();
            while (!cancellation.IsCancellationRequested && timer.Elapsed < TimeSpan.FromMinutes(10))
            {
                foreach (var device in devices)
                {
                    for (var batch = 0; batch < 256; batch++)
                    {
                        var result = libevdev_next_event(device.Context, NormalRead, out var inputEvent);
                        if (result == NoEventsAvailable) { break; }
                        // An overflow cannot preserve the original timings. Stop instead of saving a partial sequence.
                        if (result == SynchronizationRequired) { throw new IOException("Input events were lost (SYN_DROPPED) on " + device.Path + "; the recording was discarded."); }
                        if (result < 0) { throw new IOException("Input device disconnected or failed: " + device.Path + " (errno " + -result + ")."); }
                        if (!IsRecordable(inputEvent.Type, inputEvent.Code)) { continue; }
                        Console.WriteLine(JsonSerializer.Serialize(new PhysicalInputEvent(device.Path, checked((long)inputEvent.Seconds * 1_000_000 + (long)inputEvent.Microseconds), inputEvent.Type, inputEvent.Code, inputEvent.Value)));
                        if (++count >= 20000) { throw new IOException("Recording reached its 20,000-event limit."); }
                    }
                }
                Thread.Sleep(5);
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            foreach (var device in devices) { libevdev_free(device.Context); close(device.FileDescriptor); }
        }
    }
}
