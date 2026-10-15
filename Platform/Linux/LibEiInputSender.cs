using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;
using AutoClicker.Services;
using Microsoft.Win32.SafeHandles;
using static AutoClicker.Platform.Linux.LibEiNative;

namespace AutoClicker.Platform.Linux;

internal sealed class LibEiInputSender : IInputPlayback
{
    private sealed class DeviceState { internal bool Resumed; internal uint Sequence; }
    private readonly object gate = new();
    private readonly Dictionary<nint, DeviceState> devices = new();
    private readonly HashSet<(nint Device, uint Code, bool Keyboard)> held = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task pump;
    private readonly BackendEventQueue callbacks;
    private nint context;
    private int disposed;
    private Exception? failure;
    internal event Action<Exception>? Lost;

    internal LibEiInputSender(SafeFileHandle fd)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) { throw new PlatformNotSupportedException("Native input requires Linux x64 or arm64."); }
        context = ei_new_sender(0);
        if (context == 0) { throw new InvalidOperationException("libei could not allocate a sender context."); }
        // libei takes ownership of its fd; retain independent ownership of the D-Bus handle.
        var nativeFd = dup(fd.DangerousGetHandle().ToInt32());
        if (nativeFd < 0) { ei_unref(context); context = 0; throw new InvalidOperationException("Could not duplicate the portal EIS descriptor."); }
        ei_configure_name(context, "AutoClicker");
        if (ei_setup_backend_fd(context, nativeFd) != 0) { ei_unref(context); context = 0; throw new InvalidOperationException("libei could not connect to the portal EIS descriptor."); }
        callbacks = new();
        pump = Task.Run(PumpAsync);
    }

    internal async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        lock (gate) { RequireDevice(EiCapability.Keyboard); RequireDevice(EiCapability.Button); }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                lock (gate)
                {
                    ei_dispatch(context);
                    nint inputEvent;
                    while ((inputEvent = ei_get_event(context)) != 0)
                    {
                        try { HandleEvent(inputEvent); }
                        finally { ei_event_unref(inputEvent); }
                    }
                }
                // A short cancellable wait keeps all libei calls serialized without a dedicated blocking native thread.
                await Task.Delay(5, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Invalidate(error); }
    }

    private void HandleEvent(nint inputEvent)
    {
        var type = ei_event_get_type(inputEvent);
        if (type == EiEventType.Disconnect) { throw new InvalidOperationException("The compositor disconnected the libei sender."); }
        if (type == EiEventType.SeatAdded)
        {
            var seat = ei_event_get_seat(inputEvent);
            foreach (var capability in new[] { EiCapability.Pointer, EiCapability.AbsolutePointer, EiCapability.Keyboard, EiCapability.Button }) { ei_seat_bind_capabilities(seat, capability, 0); }
            return;
        }
        if (type is not (EiEventType.DeviceAdded or EiEventType.DeviceRemoved or EiEventType.DevicePaused or EiEventType.DeviceResumed)) { return; }
        var device = ei_event_get_device(inputEvent);
        if (type == EiEventType.DeviceAdded)
        {
            devices[ei_device_ref(device)] = new();
        }
        else if (type == EiEventType.DeviceRemoved)
        {
            var interrupted = held.Any(item => item.Device == device);
            held.RemoveWhere(item => item.Device == device);
            devices.Remove(device);
            ei_device_unref(device);
            if (interrupted) { Invalidate(new InvalidOperationException("An input device was removed during a gesture.")); }
        }
        else if (devices.TryGetValue(device, out var state))
        {
            state.Resumed = type == EiEventType.DeviceResumed;
            if (state.Resumed)
            {
                ei_device_start_emulating(device, ++state.Sequence);
                if (failure != null) { ReleaseDevice(device); }
            }
            else if (held.Any(item => item.Device == device)) { Invalidate(new InvalidOperationException("An input device was paused during a gesture.")); }
        }
        if (FindDevice(EiCapability.Keyboard) != 0 && FindDevice(EiCapability.Button) != 0) { ready.TrySetResult(); }
    }

    private nint FindDevice(EiCapability capability)
    {
        foreach (var item in devices)
        {
            if (item.Value.Resumed && ei_device_has_capability(item.Key, capability)) { return item.Key; }
        }
        return 0;
    }

    private nint RequireDevice(EiCapability capability)
    {
        if (context == 0 || Volatile.Read(ref disposed) != 0) { throw new ObjectDisposedException(nameof(LibEiInputSender)); }
        if (failure != null) { throw new InvalidOperationException("Input backend access was lost.", failure); }
        var device = FindDevice(capability);
        if (device == 0) { throw new InvalidOperationException("No resumed EIS device provides " + capability + "."); }
        return device;
    }

    public void SetKey(uint evdevCode, bool pressed)
    {
        if (evdevCode is 0 or >= 0x100) { throw new ArgumentOutOfRangeException(nameof(evdevCode), "Use an evdev keyboard code between 1 and 255."); }
        SetHeld(evdevCode, pressed, true);
    }

    public void SetButton(uint evdevCode, bool pressed)
    {
        if (evdevCode is not (0x110 or 0x111 or 0x112)) { throw new ArgumentOutOfRangeException(nameof(evdevCode), "Only left, right, and middle mouse buttons are supported."); }
        SetHeld(evdevCode, pressed, false);
    }

    private void SetHeld(uint code, bool pressed, bool keyboard)
    {
        lock (gate)
        {
            var device = RequireDevice(keyboard ? EiCapability.Keyboard : EiCapability.Button);
            if (!pressed)
            {
                // A newly resumed device must not receive another device's release.
                device = 0;
                foreach (var item in held)
                {
                    if (item.Code == code && item.Keyboard == keyboard) { device = item.Device; break; }
                }
                if (device == 0) { return; }
            }
            var input = (device, code, keyboard);
            if (pressed ? !held.Add(input) : !held.Remove(input)) { return; }
            SendHeld(device, code, pressed, keyboard);
        }
    }

    private void SendHeld(nint device, uint code, bool pressed, bool keyboard)
    {
        if (keyboard) { ei_device_keyboard_key(device, code, pressed); } else { ei_device_button_button(device, code, pressed); }
        ei_device_frame(device, ei_now(context));
    }

    public void MoveTo(DesktopPoint position)
    {
        lock (gate)
        {
            RequireDevice(EiCapability.AbsolutePointer);
            // Select the device whose desktop region actually contains this logical coordinate.
            var device = devices.FirstOrDefault(item => item.Value.Resumed && ei_device_has_capability(item.Key, EiCapability.AbsolutePointer) && ei_device_get_region_at(item.Key, position.X, position.Y) != 0).Key;
            if (device == 0) { throw new ArgumentOutOfRangeException(nameof(position), "The point is outside the compositor's EIS regions."); }
            ei_device_pointer_motion_absolute(device, position.X, position.Y);
            ei_device_frame(device, ei_now(context));
        }
    }

    private void ReleaseDevice(nint device)
    {
        foreach (var item in held.Where(item => item.Device == device).ToArray()) { SendHeld(item.Device, item.Code, false, item.Keyboard); held.Remove(item); }
    }

    public void ReleaseAll()
    {
        lock (gate)
        {
            if (context == 0) { return; }
            foreach (var item in devices.Where(item => item.Value.Resumed)) { ReleaseDevice(item.Key); }
            ei_dispatch(context);
        }
    }

    internal void Invalidate(Exception error)
    {
        lock (gate) { if (failure != null) { return; } failure = error; ready.TrySetException(error); ReleaseAll(); }
        callbacks.Post(() => Lost?.Invoke(error));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        lifetime.Cancel();
        pump.GetAwaiter().GetResult();
        lock (gate)
        {
            if (context == 0) { return; }
            ReleaseAll();
            foreach (var item in devices) { if (item.Value.Resumed) { ei_device_stop_emulating(item.Key); } ei_device_unref(item.Key); }
            devices.Clear();
            held.Clear();
            ei_dispatch(context);
            ei_unref(context);
            context = 0;
        }
        lifetime.Dispose();
        callbacks.Dispose();
    }
}
