using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;
using AutoClicker.Services;

namespace AutoClicker.Platform.Linux;

internal sealed class PhysicalInputCapture : IPhysicalCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Timespec { internal long Seconds; internal long Nanoseconds; }
    [DllImport("libc")] private static extern int clock_gettime(int clockId, out Timespec time);
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration cancellation;
    private int disposed;
    public long StartedMicroseconds { get; }
    public event Action<PhysicalInputEvent>? Input;
    public Task Completion { get; }

    private PhysicalInputCapture(Process process, long started, CancellationToken cancellationToken)
    {
        this.process = process;
        StartedMicroseconds = started;
        errors = ReadErrorsAsync();
        Completion = ReadAsync();
        cancellation = cancellationToken.Register(() => { try { process.StandardInput.Close(); } catch (InvalidOperationException) { } });
    }

    private static string[] GetDevicePaths(bool clicks, bool keyboard)
    {
        // Read only the configured physical mouse and keyboard devices.
        var devices = new[]
        {
            (Path: "/dev/input/event3", Enabled: clicks, Name: "USB OPTICAL MOUSE"),
            (Path: "/dev/input/event5", Enabled: keyboard, Name: "Logitech USB Receiver"),
            (Path: "/dev/input/event6", Enabled: clicks, Name: "Logitech USB Receiver Mouse")
        }.Where(device => device.Enabled).ToArray();
        foreach (var device in devices)
        {
            var path = device.Path;
            if (!LibEvdevReader.IsPhysicalDevice(path)) { throw new IOException("Recording device is unavailable: " + path + ". Check that the selected mouse or keyboard is connected."); }
            if (File.ReadAllText("/sys/class/input/" + Path.GetFileName(path) + "/device/name").Trim() != device.Name) { throw new IOException("The device at " + path + " has changed. Update the recording device paths before trying again."); }
        }
        return devices.Select(device => device.Path).ToArray();
    }

    private static bool CanReadDevice(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static bool HasReadAccess(bool clicks, bool keyboard)
    {
        try { return GetDevicePaths(clicks, keyboard).All(CanReadDevice); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static ProcessStartInfo CreatePermissionCommand(uint userId, string[] paths)
    {
        var command = new ProcessStartInfo("/usr/bin/pkexec") { RedirectStandardError = true, UseShellExecute = false };
        command.ArgumentList.Add("--disable-internal-agent");
        command.ArgumentList.Add("/usr/bin/setfacl");
        command.ArgumentList.Add("-m");
        command.ArgumentList.Add("u:" + userId.ToString(CultureInfo.InvariantCulture) + ":r");
        command.ArgumentList.Add("--");
        foreach (var path in paths) { command.ArgumentList.Add(path); }
        return command;
    }

    internal static async Task GrantReadAccessAsync(bool clicks, bool keyboard, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = GetDevicePaths(clicks, keyboard);
        if (paths.Length == 0) { throw new ArgumentException("Choose mouse clicks or keyboard input."); }
        var userId = LibEvdevReader.geteuid();
        if (userId == 0) { throw new InvalidOperationException("Allow recording as your normal desktop user."); }
        paths = paths.Where(path => !CanReadDevice(path)).ToArray();
        if (paths.Length == 0) { return; }
        if (!File.Exists("/usr/bin/pkexec") || !File.Exists("/usr/bin/setfacl")) { throw new InvalidOperationException("Allowing recording requires pkexec and setfacl to be installed."); }

        // Let the desktop prompt authorize only the device permission command.
        using var permissionProcess = Process.Start(CreatePermissionCommand(userId, paths)) ?? throw new IOException("Could not open the recording authorization prompt.");
        var errorOutput = permissionProcess.StandardError.ReadToEndAsync(cancellationToken);
        try { await permissionProcess.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { permissionProcess.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            throw;
        }
        var errorMessage = (await errorOutput).Trim();
        if (permissionProcess.ExitCode == 126) { throw new InvalidOperationException("Recording authorization was cancelled."); }
        if (permissionProcess.ExitCode != 0) { throw new InvalidOperationException("Could not allow recording." + (errorMessage.Length == 0 ? "" : " " + errorMessage)); }
        if (!HasReadAccess(clicks, keyboard)) { throw new UnauthorizedAccessException("Recording access was not granted. Try allowing recording again."); }
    }

    internal static async Task<IPhysicalCapture> StartAsync(bool clicks, bool keyboard, CancellationToken cancellationToken)
    {
        var paths = GetDevicePaths(clicks, keyboard);
        if (paths.Length == 0) { throw new ArgumentException("Choose mouse clicks or keyboard input."); }
        foreach (var path in paths)
        {
            try { using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
            catch (UnauthorizedAccessException error) { throw new UnauthorizedAccessException("Recording needs read access to " + path + ". Select Allow recording in Macros to grant access.", error); }
        }
        if (clock_gettime(1, out var time) != 0) { throw new IOException("Could not read the recording clock."); }
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") { start.ArgumentList.Add(typeof(PhysicalInputCapture).Assembly.Location); }
        start.ArgumentList.Add("--input-reader");
        foreach (var path in paths) { start.ArgumentList.Add(path); }
        var result = new PhysicalInputCapture(Process.Start(start) ?? throw new IOException("Could not start the input reader."), time.Seconds * 1_000_000 + time.Nanoseconds / 1000, cancellationToken);
        try { await result.ready.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); return result; }
        catch { await result.DisposeAsync(); throw; }
    }

    private async Task<string> ReadErrorsAsync()
    {
        var lines = new System.Text.StringBuilder();
        while (await process.StandardError.ReadLineAsync() is { } line) { if (line.StartsWith("Reader ready:", StringComparison.Ordinal)) { ready.TrySetResult(); } else { lines.AppendLine(line); } }
        return lines.ToString();
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                var input = JsonSerializer.Deserialize<PhysicalInputEvent>(line) ?? throw new IOException("Invalid recording event.");
                Input?.Invoke(input);
            }
            await process.WaitForExitAsync();
            var error = await errors;
            if (process.ExitCode != 0) { throw new IOException(string.IsNullOrWhiteSpace(error) ? "Recording device disconnected." : error.Trim()); }
            ready.TrySetException(new IOException("The input reader stopped before it was ready."));
        }
        catch (Exception error) { ready.TrySetException(error); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        cancellation.Dispose();
        try
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { process.Kill(); await process.WaitForExitAsync(); }
            await Completion;
        }
        finally { process.Dispose(); }
    }
}
