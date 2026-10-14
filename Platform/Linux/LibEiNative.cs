using System;
using System.Runtime.InteropServices;

namespace AutoClicker.Platform.Linux;

internal enum EiCapability { Pointer = 1, AbsolutePointer = 2, Keyboard = 4, Button = 32 }
internal enum EiEventType { Connect = 1, Disconnect, SeatAdded, SeatRemoved, DeviceAdded, DeviceRemoved, DevicePaused, DeviceResumed }

// C bool is one byte, size_t is pointer-sized, and EI keycodes use evdev values without X11's +8.
internal static class LibEiNative
{
    private const string Library = "libei.so.1";
    [DllImport(Library)] internal static extern nint ei_new_sender(nint userData);
    [DllImport(Library)] internal static extern nint ei_unref(nint context);
    [DllImport(Library)] internal static extern void ei_configure_name(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library)] internal static extern int ei_setup_backend_fd(nint context, int fd);
    [DllImport(Library)] internal static extern void ei_dispatch(nint context);
    [DllImport(Library)] internal static extern nint ei_get_event(nint context);
    [DllImport(Library)] internal static extern nint ei_event_unref(nint inputEvent);
    [DllImport(Library)] internal static extern EiEventType ei_event_get_type(nint inputEvent);
    [DllImport(Library)] internal static extern nint ei_event_get_seat(nint inputEvent);
    [DllImport(Library)] internal static extern nint ei_event_get_device(nint inputEvent);
    // Bind one capability per call. The variadic C API requires a pointer-sized NULL sentinel.
    [DllImport(Library)] internal static extern void ei_seat_bind_capabilities(nint seat, EiCapability capability, nint sentinel);
    [DllImport(Library)] internal static extern nint ei_device_ref(nint device);
    [DllImport(Library)] internal static extern nint ei_device_unref(nint device);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool ei_device_has_capability(nint device, EiCapability capability);
    [DllImport(Library)] internal static extern nint ei_device_get_region_at(nint device, double x, double y);
    [DllImport(Library)] internal static extern void ei_device_start_emulating(nint device, uint sequence);
    [DllImport(Library)] internal static extern void ei_device_stop_emulating(nint device);
    [DllImport(Library)] internal static extern void ei_device_button_button(nint device, uint button, [MarshalAs(UnmanagedType.I1)] bool pressed);
    [DllImport(Library)] internal static extern void ei_device_keyboard_key(nint device, uint key, [MarshalAs(UnmanagedType.I1)] bool pressed);
    [DllImport(Library)] internal static extern void ei_device_pointer_motion_absolute(nint device, double x, double y);
    [DllImport(Library)] internal static extern void ei_device_frame(nint device, ulong timestamp);
    [DllImport(Library)] internal static extern ulong ei_now(nint context);
    [DllImport("libc", SetLastError = true)] internal static extern int dup(int fd);
}
