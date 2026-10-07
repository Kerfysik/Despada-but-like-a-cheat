// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using System.Reflection.Emit;
using Despada.Core;
using HarmonyLib;

namespace Despada.ImGui.Hook;

public static class ClydeRenderHook
{
    private static MethodInfo? _renderFrameMethod;

    /// <summary>Calls ImGuiRenderer.RenderFrame() inside Clyde.Render, right after the final screenshot.</summary>
    public static void Install()
        => Hooks.Transpiler("render", Engine.ClydeRender, nameof(Transpiler), typeof(ClydeRenderHook));

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator il)
    {
        _renderFrameMethod ??= AccessTools.Method(
            typeof(ImGuiRenderer),
            nameof(ImGuiRenderer.RenderFrame));
        
        // Throwing makes Harmony reject the patch, so the hook is recorded as failed (Render untouched).
        var takeScreenshot = Engine.ClydeTakeScreenshot.Method
            ?? throw new InvalidOperationException("Clyde.TakeScreenshot not found");

        var found = false;
        var result = new List<CodeInstruction>();
        foreach (var code in instructions)
        {
            result.Add(code);
            if (!found && code.Calls(takeScreenshot))
            {
                result.Add(new CodeInstruction(OpCodes.Call, _renderFrameMethod));
                found = true;
            }
        }

        if (!found)
            throw new InvalidOperationException("TakeScreenshot call not found in Clyde.Render — engine changed?");
        return result;
    }
}