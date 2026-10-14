using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AutoClicker.Platform.Linux;

internal sealed record RecordingDevice(string Path, string Name, bool Mouse, bool Keyboard);

internal static class InputDeviceDiscovery
{
    internal static IReadOnlyList<RecordingDevice> Discover(string inputDirectory = "/sys/class/input", string deviceDirectory = "/dev/input")
    {
        var devices = new List<RecordingDevice>();
        if (!Directory.Exists(inputDirectory)) { return devices; }
        foreach (var directory in Directory.EnumerateDirectories(inputDirectory, "event*").Order(StringComparer.Ordinal))
        {
            try
            {
                var target = new DirectoryInfo(directory).ResolveLinkTarget(true)?.FullName;
                if (target == null || !IsHardwarePath(target)) { continue; }
                var words = File.ReadAllText(Path.Combine(directory, "device/capabilities/key")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var mouse = HasKey(words, 0x110) || HasKey(words, 0x111) || HasKey(words, 0x112);
                // Require ordinary typing keys, excluding power buttons and consumer-control interfaces.
                var keyboard = HasKey(words, 28) && HasKey(words, 30) && HasKey(words, 44);
                if (!mouse && !keyboard) { continue; }
                var name = File.ReadAllText(Path.Combine(directory, "device/name")).Trim();
                devices.Add(new(Path.Combine(deviceDirectory, Path.GetFileName(directory)), name, mouse, keyboard));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Devices can disappear while sysfs is being enumerated.
            }
        }
        return devices;
    }

    internal static bool IsHardwarePath(string path)
    {
        // Bluetooth HID devices may live under virtual/misc/uhid; uinput devices live under virtual/input.
        return !path.StartsWith("/sys/devices/virtual/input/", StringComparison.Ordinal);
    }

    internal static bool HasKey(string[] words, int code)
    {
        // sysfs prints native unsigned-long words, most significant first.
        var bitsPerWord = IntPtr.Size * 8;
        var index = words.Length - 1 - code / bitsPerWord;
        return index >= 0 && ulong.TryParse(words[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word) && (word & (1UL << (code % bitsPerWord))) != 0;
    }

    internal static string[] SelectPaths(IReadOnlyList<RecordingDevice> devices, bool clicks, bool keyboard)
    {
        if (!clicks && !keyboard) { throw new ArgumentException("Choose mouse clicks or keyboard input."); }
        if (clicks && !devices.Any(device => device.Mouse)) { throw new IOException("No physical mouse or touchpad buttons were found. Connect a device or turn off mouse recording."); }
        if (keyboard && !devices.Any(device => device.Keyboard)) { throw new IOException("No physical keyboard was found. Connect a keyboard or turn off keyboard recording."); }
        return devices.Where(device => clicks && device.Mouse || keyboard && device.Keyboard).Select(device => device.Path).Distinct().ToArray();
    }
}
