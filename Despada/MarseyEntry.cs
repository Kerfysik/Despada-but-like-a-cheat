// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Despada.Config;
using Despada.Core;
using Despada.ImGui;
using Despada.ImGui.Hook;

public static class MarseyEntry
{
    public static void Entry()
    {
        try
        {
            Start();
        }
        catch (Exception ex)
        {
            // Nothing may escape into the engine's module loading.
            MarseyLogger.Fatal($"Despada failed to start: {ex}");
        }
    }

    private static void Start()
    {
        MarseyLogger.Info("Entry for patching started.");

        // Entry runs from MarseyLoader's postfix on ModLoader.TryLoadModules, so content is
        // already loaded by now; there's nothing to wait for.
        if (FindAssembly("Content.Client") is null)
        {
            MarseyLogger.Fatal("Content.Client is not loaded — Despada disabled.");
            return;
        }

        DespadaConfig.Load();

        // Find everything first, then hook; each step only disables what depends on a missing piece.
        Engine.ResolveAll();
        Lifecycle.Install();
        // No render hook, no overlay: the input hooks would only eat the toggle key and queue
        // events nothing ever drains.
        if (ClydeRenderHook.Install())
        {
            ImGuiInputHook.Install();
            ImGuiSdl3TextInputStopPatch.Install();
        }
        else ImGuiRenderer.Disable("render hook not installed");
        Features.Initialize();

        Sedition.Hide();
        Sedition.Apply(new Sedition.Manifest
        {
            StackNames = { "Despada" },
        });
    }

    private static Assembly? FindAssembly(string assemblyName)
    {
        var asmList = AppDomain.CurrentDomain.GetAssemblies();
        return asmList.FirstOrDefault(asm => asm.GetName().Name == assemblyName);
    }
}