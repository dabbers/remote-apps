// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace RemoteApp.Client.Input;

/// <summary>
/// Maps Windows keys to protocol key events (PROTOCOL.md §9): non-printing
/// keys become protocol key codes, modifier combinations carry the base
/// character plus modifier bits, and PC modifiers are translated for a Mac
/// host (Ctrl → ⌘ by default, Alt → ⌥, Shift → ⇧).
/// </summary>
public static class KeyMapper
{
    /// <summary>Protocol key code for a non-printing key, or null for a printable one.</summary>
    public static ushort? SpecialKeyCode(Keys key) => key switch
    {
        Keys.Return => KeyCodes.Return,
        Keys.Back => KeyCodes.Delete,
        Keys.Tab => KeyCodes.Tab,
        Keys.Escape => KeyCodes.Escape,
        Keys.Left => KeyCodes.LeftArrow,
        Keys.Right => KeyCodes.RightArrow,
        Keys.Down => KeyCodes.DownArrow,
        Keys.Up => KeyCodes.UpArrow,
        Keys.Delete => KeyCodes.ForwardDelete,
        Keys.Home => KeyCodes.Home,
        Keys.End => KeyCodes.End,
        Keys.PageUp => KeyCodes.PageUp,
        Keys.PageDown => KeyCodes.PageDown,
        Keys.F1 => KeyCodes.F1,
        Keys.F2 => KeyCodes.F2,
        Keys.F3 => KeyCodes.F3,
        Keys.F4 => KeyCodes.F4,
        Keys.F5 => KeyCodes.F5,
        Keys.F6 => KeyCodes.F6,
        Keys.F7 => KeyCodes.F7,
        Keys.F8 => KeyCodes.F8,
        Keys.F9 => KeyCodes.F9,
        Keys.F10 => KeyCodes.F10,
        Keys.F11 => KeyCodes.F11,
        Keys.F12 => KeyCodes.F12,
        _ => null,
    };

    public static bool IsModifierKey(Keys key) => key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
        or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu
        or Keys.LWin or Keys.RWin or Keys.CapsLock or Keys.NumLock or Keys.Scroll;

    /// <summary>Protocol modifier bits for the held PC modifiers.</summary>
    public static KeyModifiers Modifiers(Keys modifiers, bool ctrlSendsCommand)
    {
        var result = KeyModifiers.None;
        if (modifiers.HasFlag(Keys.Shift)) result |= KeyModifiers.Shift;
        if (modifiers.HasFlag(Keys.Control)) result |= ctrlSendsCommand ? KeyModifiers.Command : KeyModifiers.Control;
        if (modifiers.HasFlag(Keys.Alt)) result |= KeyModifiers.Option;
        return result;
    }

    /// <summary>
    /// The unshifted character a virtual key produces in the current layout
    /// (e.g. Keys.C → "c", Keys.OemPeriod → "."), lowercased, for shortcuts.
    /// </summary>
    public static string? CharForKey(Keys key)
    {
        var mapped = PInvoke.MapVirtualKey((uint)key, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_CHAR);
        var ch = (char)(mapped & 0xFFFF);
        if (ch == 0 || char.IsControl(ch)) return null;
        return char.ToLowerInvariant(ch).ToString();
    }
}
