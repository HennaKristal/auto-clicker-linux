using System;
using AutoClicker.Models;

namespace AutoClicker.Services;

// Linux key and button codes are explicit here; text-to-keymap translation is a later concern.
public interface IInputPlayback : IDisposable
{
    void SetKey(uint evdevCode, bool pressed);
    void SetButton(uint evdevCode, bool pressed);
    void MoveTo(DesktopPoint position);
    void ReleaseAll();
}
