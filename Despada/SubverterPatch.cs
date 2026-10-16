// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using HarmonyLib;

public static class SubverterPatch
{
    public static string Name = "Despada";
    public static string Description = "Active analytics patch || version 0.0.1";
    public static string IconResource = "Despada.Assets.icon.png";
    public static Harmony Harm = new("Despada");
}