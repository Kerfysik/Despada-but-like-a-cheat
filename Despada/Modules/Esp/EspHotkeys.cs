// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Despada.Modules.Esp;

/// <summary>
/// Global hotkeys, Cerberus' way: polled once a frame from the game thread, so they fire whether
/// the menu is open or not and never depend on SDL's focus routing. Edge-triggered — a held key is
/// one press until it is released.
/// </summary>
internal static class EspHotkeys
{
    public const int VkFov        = 0x75; // F6
    public const int VkFullBright = 0x76; // F7
    public const int VkLock       = 0x77; // F8
    public const int VkZoomUp     = 0xBB; // +
    public const int VkZoomDown   = 0xBD; // -

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static readonly HashSet<int> _held = new();

    /// <summary>True once per press of <paramref name="virtualKey"/>.</summary>
    public static bool Pressed(int virtualKey)
    {
        var down = (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        if (down) return _held.Add(virtualKey);
        _held.Remove(virtualKey);
        return false;
    }
}