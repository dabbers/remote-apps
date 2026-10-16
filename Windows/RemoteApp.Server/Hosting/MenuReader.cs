// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RemoteApp.Server.Hosting;

/// <summary>
/// Reads a window's classic Win32 menu bar (<c>GetMenu</c>) as a
/// <see cref="MenuNode"/> tree and triggers items with <c>WM_COMMAND</c>, so the
/// client can show native menus. Apps that draw their own menus (WinUI,
/// Electron, Office) report none; their menus still appear in the video.
/// </summary>
internal static unsafe class MenuReader
{
    private const int MaxDepth = 8;
    private const int MaxItems = 200;

    public static List<MenuNode> Read(HWND hwnd)
    {
        var menu = PInvoke.GetMenu(hwnd);
        if (menu.IsNull) return [];
        return ReadMenu(menu, 0);
    }

    private static List<MenuNode> ReadMenu(HMENU menu, int depth)
    {
        var nodes = new List<MenuNode>();
        var count = PInvoke.GetMenuItemCount(menu);
        for (var i = 0; i < Math.Min(count, MaxItems); i++)
        {
            var info = new MENUITEMINFOW
            {
                cbSize = (uint)sizeof(MENUITEMINFOW),
                fMask = MENU_ITEM_MASK.MIIM_STRING | MENU_ITEM_MASK.MIIM_SUBMENU | MENU_ITEM_MASK.MIIM_STATE
                        | MENU_ITEM_MASK.MIIM_ID | MENU_ITEM_MASK.MIIM_FTYPE,
            };
            if (!PInvoke.GetMenuItemInfo(menu, (uint)i, true, &info)) continue;
            var isSeparator = info.fType.HasFlag(MENU_ITEM_TYPE.MFT_SEPARATOR);
            var title = "";
            if (!isSeparator && info.cch > 0)
            {
                var buffer = new char[info.cch + 1];
                fixed (char* p = buffer)
                {
                    info.dwTypeData = new PWSTR(p);
                    info.cch = (uint)buffer.Length;
                    info.fMask = MENU_ITEM_MASK.MIIM_STRING;
                    if (PInvoke.GetMenuItemInfo(menu, (uint)i, true, &info)) title = new string(p, 0, (int)info.cch);
                }
                // Re-read the state/submenu fields we overwrote fMask for.
                info.fMask = MENU_ITEM_MASK.MIIM_SUBMENU | MENU_ITEM_MASK.MIIM_STATE | MENU_ITEM_MASK.MIIM_ID | MENU_ITEM_MASK.MIIM_FTYPE;
                PInvoke.GetMenuItemInfo(menu, (uint)i, true, &info);
            }
            var (cleanTitle, shortcut) = SplitTitle(title);
            var children = !info.hSubMenu.IsNull && depth < MaxDepth ? ReadMenu(info.hSubMenu, depth + 1) : [];
            nodes.Add(new MenuNode(
                cleanTitle,
                Enabled: !info.fState.HasFlag(MENU_ITEM_STATE.MFS_DISABLED),
                IsSeparator: isSeparator,
                Checked: info.fState.HasFlag(MENU_ITEM_STATE.MFS_CHECKED),
                Shortcut: children.Count == 0 ? shortcut : null,
                Children: children));
        }
        return nodes;
    }

    /// <summary>"&amp;Save\tCtrl+S" → ("Save", "Ctrl+S").</summary>
    private static (string Title, string? Shortcut) SplitTitle(string raw)
    {
        var tab = raw.IndexOf('\t');
        var title = tab >= 0 ? raw[..tab] : raw;
        var shortcut = tab >= 0 && tab + 1 < raw.Length ? raw[(tab + 1)..] : null;
        title = title.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
        return (title, shortcut);
    }

    /// <summary>Resolves a child-index path and posts the item's command to the window.</summary>
    public static bool Perform(HWND hwnd, List<int> path)
    {
        var menu = PInvoke.GetMenu(hwnd);
        if (menu.IsNull || path.Count == 0) return false;
        var current = menu;
        uint id = 0;
        for (var level = 0; level < path.Count; level++)
        {
            var index = path[level];
            if (index < 0 || index >= PInvoke.GetMenuItemCount(current)) return false;
            var info = new MENUITEMINFOW
            {
                cbSize = (uint)sizeof(MENUITEMINFOW),
                fMask = MENU_ITEM_MASK.MIIM_SUBMENU | MENU_ITEM_MASK.MIIM_ID | MENU_ITEM_MASK.MIIM_STATE,
            };
            if (!PInvoke.GetMenuItemInfo(current, (uint)index, true, &info)) return false;
            if (level == path.Count - 1)
            {
                if (!info.hSubMenu.IsNull || info.fState.HasFlag(MENU_ITEM_STATE.MFS_DISABLED)) return false;
                id = info.wID;
            }
            else
            {
                if (info.hSubMenu.IsNull) return false;
                current = info.hSubMenu;
            }
        }
        return PInvoke.PostMessage(hwnd, PInvoke.WM_COMMAND, new WPARAM(id), new LPARAM(0));
    }
}
