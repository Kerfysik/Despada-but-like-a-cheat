// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.Tests.Fakes;

// Stand-ins for engine types: bindings resolve them by name exactly like Robust's.
public sealed class FakeArgs(int value)
{
    public int Value { get; } = value;
}

public sealed class FakeOther;

public class FakeManager
{
    private int _counter = 41;
    private static string _static = "s";

    public int Calls;

    public int Add(int a, int b) => a + b;
    public string Add(string a, string b) => a + b;

    public void Handle(FakeArgs args) => Calls += args.Value;
    public void Handle(FakeOther other) => Calls -= 1;

    private int Secret() => _counter + 1;

    public static string Shout(string s) => s.ToUpperInvariant() + _static;

    // Patched by HooksTests; kept out of line so the JIT can't inline it away.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public int Hooked(int x) => x * 2;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public int Hooked2(int x) => x * 3;
}
