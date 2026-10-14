# AutoClicker

A desktop auto clicker and macro recorder for **Linux with KDE Plasma on Wayland**, built with C# and Avalonia.

Automate mouse clicks, record mouse and keyboard actions, and replay saved macros with their original timing and click positions. Desktop control uses KDE's portals and KWin scripting. KDE Plasma on Wayland is the supported desktop; X11 sessions and other desktop environments are outside this project's scope.

## Features

- Configurable click rate per second, minute, hour, or day.
- Left, middle, and right mouse buttons, with toggle or hold activation.
- Optional double clicks, button hold duration through duty cycle, and randomized speed variation.
- Click count and time limits, plus configurable screen corner and edge stops.
- Fixed click positions with editable coordinates and a position picker.
- Mouse click and keyboard recording, including press/release timing and cursor positions for clicks.
- Saved macro playback with repeat counts, infinite repetition, and individual play hotkeys.
- Macro editing, search, sorting, and drag-to-reorder support.
- Global shortcuts configured through KDE.
- Automatically saved settings, macros, and lifetime clicker statistics.

## Requirements

| Component | Requirement |
| --- | --- |
| Desktop | 64-bit Linux, KDE Plasma on Wayland, and KWin scripting support |
| Build tools | [.NET 10 SDK](https://learn.microsoft.com/en-us/dotnet/core/install/linux) |
| Desktop portals | `xdg-desktop-portal` and the KDE backend, with Remote Desktop version 2 / `ConnectToEIS` and Global Shortcuts support |
| Shortcut editing | Global Shortcuts portal version 2 |
| Input playback | `libei.so.1` |
| Macro recording | `libevdev.so.2`, plus read access to the selected physical input devices |
| Recording permission prompt | `pkexec` and `setfacl` on `PATH`, plus a desktop authentication agent |
| Development desktop entry | Bash and Python 3 |
| App window | XWayland, used by the current Avalonia configuration on Wayland desktops |

Install native dependencies using your distribution's package manager; package names vary. The desktop integration relies on the [Remote Desktop portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html), [Global Shortcuts portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html), and [KWin scripting API](https://develop.kde.org/docs/plasma/kwin/api/). Avalonia's default Linux window backend uses [XWayland on Wayland desktops](https://docs.avaloniaui.net/docs/platform-specific-guides/linux).

## Build and run

Clone or download the repository, then run these commands from its root directory:

```bash
dotnet build
bash Tools/install-desktop-entry.sh
dotnet run --no-build
```

Run AutoClicker as your normal desktop user. The desktop-entry script registers the development build's name and icon for KDE permission dialogs. It points to `bin/Debug/net10.0/AutoClicker` and is hidden from the application launcher. Run it again if you move the project directory.

After changing the code, use `dotnet run` to rebuild and launch. `dotnet run --no-build` requires an existing build; it will fail if the build output has been removed.

## Permissions

### Clicking and macro playback

Click **Allow permissions** and approve KDE's desktop input and shortcut dialogs. AutoClicker requests both mouse and keyboard access. These permissions are used for clicking, macro playback, global shortcuts, and cursor access.

Desktop authorization is requested for the running application. You may need to approve it again after restarting AutoClicker.

### Macro recording

Recording discovers physical keyboards and mouse/touchpad buttons from Linux input capabilities. It does not depend on device names or fixed event numbers. All matching devices for the enabled recording options are included. Recording requires separate read access to these devices:

1. Open **Macros** and select the mouse and keyboard recording options you want.
2. If access is missing, click **Allow recording** and approve the system password prompt.
3. The button changes to **Record**. Click it to start recording.

The [system authentication prompt](https://polkit.pages.freedesktop.org/polkit/pkexec.1.html) authorizes `setfacl` to grant your user read access to the selected devices that are not already readable. AutoClicker continues running as your normal user. Approving access does not start a recording automatically.

Access applies to the current device nodes. Because Linux manages these dynamically, you may need to allow recording again after a reboot or device reconnection. See the [udev documentation](https://man7.org/linux/man-pages/man7/udev.7.html).

To inspect the devices that AutoClicker detects without opening the app or requesting permissions:

```bash
dotnet run --no-build -- --list-input-devices
```

## Using the app

### Auto clicking

Set the rate and mouse button on the **Clicker** tab, then click **Start**. Starting from the button gives you a two-second countdown to focus the target application. The activation hotkey starts clicking immediately and follows the selected toggle or hold mode.

Use **Stop** to stop clicking. Corner and edge stops are enabled by default with a distance of 50 pixels. Keep the cursor and any saved click position outside those areas, or adjust the stop settings.

To click a fixed location, turn **Position** on, then use **Pick** or enter coordinates. Press Escape to cancel picking.

### Recording and replaying macros

Recording continues while another application has focus. Finish with **Stop recording** or the recording stop hotkey, then choose a name for the macro. The recorder filters AutoClicker's control shortcuts and clicks on its own UI from saved recordings.

Use a saved macro's **Play** button or assigned hotkey to replay it. During playback, the footer **Start** button and clicker activation hotkey are disabled. The footer **Stop** button stops the macro and releases held inputs. The macro's own play button or hotkey also toggles its playback.

The recording control has its own stop action; the footer Stop button handles auto clicking and macro playback.

### Editing macros

The editor lists events in playback order. Each numeric value is the wait **before that event**, in milliseconds, measured from the previous event. The first row's delay is the initial wait before playback sends input.

A click consists of a mouse press and release. The delay on the release row determines how long the button stays pressed. Changing a delay shifts that event and all following events. Keep the recorded sequence order; sorting these values would change the macro. **Delete** removes the selected press and its matching release together.

## Default hotkeys

| Action | Requested default |
| --- | --- |
| Toggle or hold auto clicking | `Numpad 1` |
| Start recording | `Numpad 2` |
| Stop recording | `Numpad 3` |
| Play or stop a saved macro | Assign individually in KDE |

KDE may use a different binding if there is a conflict. The app displays the actual assigned shortcuts. Click **Edit hotkeys** or a shortcut button to open KDE's configuration, then select **Done editing hotkeys** when finished. Playback and recording controls are disabled while editing hotkeys.

These are keypad keys, separate from the number row. KDE displays them as `Num+1`, `Num+2`, and `Num+3`. Existing KDE assignments are preserved; use **About > Reset** to apply the current defaults to an existing installation.

## Saved data

| File | Contents |
| --- | --- |
| `~/.config/auto-clicker/settings.json` | Clicker settings and recording options |
| `~/.local/share/auto-clicker/data.json` | Saved macros and lifetime clicker statistics |

These locations follow `XDG_CONFIG_HOME` and `XDG_DATA_HOME` when set to absolute paths; empty or relative values use the defaults above. Previous valid copies are kept as `.bak` files. Global shortcut assignments are managed by KDE. Macro creation dates display in the user's local timezone.

**About > Reset** restores clicker settings, recording options, and activation/recording hotkeys. Saved macros, their play hotkeys, and statistics are preserved.

## Current limitations

- Recording discovers devices when starting or checking permissions. Devices connected during an active recording are included the next time recording starts. Virtual `uinput` devices are excluded; Bluetooth HID devices are eligible.
- Recording captures keyboard keys and left, middle, and right mouse buttons. It saves click positions, but does not record continuous mouse movement or scrolling.
- Touchpad taps and gestures synthesized by the desktop are not physical button events and are not recorded. Key playback uses physical key codes and the current keyboard layout.
- Shortcut filtering supports multiple KDE bindings per action, common English key names, and keypad keys. Letter shortcuts assume QWERTY positions. An unsupported description does not block recording, but its key presses may remain in the saved macro.
- Keep recordings under ten minutes and 20,000 events. Recording reserves space for final releases and can reach its limit earlier. The library is limited to 1,000 macros and 200,000 saved events in total.
- Device disconnection or lost input events cause an incomplete recording to be discarded.
- Saved positions use desktop coordinates. Moving windows or changing monitor layouts can change where a macro clicks.
- Requested click speed is a target; actual throughput depends on hold duration, double-click delay, system load, and the target application.

## Troubleshooting

- **Missing executable with `--no-build`:** run `dotnet build`, then launch again.
- **Permissions not granted:** approve all requested desktop input permissions and make sure the KDE portal backend is running.
- **Allow recording fails:** check that `pkexec` and `setfacl` are on `PATH`, run the device-list command above, and reconnect any missing hardware. Turn off keyboard or mouse recording if you have no matching device. After a reconnection, allow access again if prompted.
- **Clicking stops immediately:** check the corner/edge stop areas and any saved click position.
- **Hotkey editing is unavailable:** the KDE backend must provide Global Shortcuts portal version 2.

When reporting an issue, include your distribution, KDE Plasma version, session type, .NET version, steps to reproduce it, and the full error message.

## Development checks

```bash
dotnet build
dotnet run --project Tests/AutoClicker.RegressionTests.csproj
git diff --check
```

The regression runner has no external test-package dependencies and never sends desktop input or requests permissions. It covers device discovery, multi-device recording, shortcut/UI filtering, backup recovery, storage validation, timing limits, cancellation, monitor borders, and macro editing.

Desktop integration still needs a manual KDE Wayland check: approve playback and recording, exercise toggle/hold shortcuts, record and replay input in another application, reconnect hardware, revoke permissions during playback, and pick positions on mixed-scale monitors. A passing build or regression run does not verify these desktop interactions.
