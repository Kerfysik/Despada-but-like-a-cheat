// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core;
using Despada.Core.Bind;
using Despada.Tests.Fakes;
using Xunit;

namespace Despada.Tests;

public static class TestPatches
{
    public static bool ReturnMinusOne(ref int __result)
    {
        __result = -1;
        return false;
    }
}

internal static class PrivatePatches
{
    private static bool PrivateMinusOne(ref int __result)
    {
        __result = -1;
        return false;
    }
}

public class HooksTests
{
    private static MethodRef Target(string name)
    {
        var m = new MethodRef(new TypeRef("Despada.Tests.Fakes.FakeManager"), [name], null);
        m.Resolve();
        return m;
    }

    [Fact]
    public void Prefix_AppliesAndRemoves()
    {
        Assert.True(Hooks.Prefix("t-apply", Target("Hooked"), nameof(TestPatches.ReturnMinusOne), typeof(TestPatches)));
        Assert.Equal(-1, new FakeManager().Hooked(5));
        Assert.Contains(Hooks.Of("t-apply"), h => h.State == Hooks.State.Active);

        Hooks.RemoveAll("t-apply");
        Assert.Equal(10, new FakeManager().Hooked(5));
        Assert.All(Hooks.Of("t-apply"), h => Assert.Equal(Hooks.State.Removed, h.State));
    }

    [Fact]
    public void MissingTarget_ReturnsFalse_AndIsRecordedAsFailed()
    {
        var missing = new MethodRef(new TypeRef("Robust.Nope.Gone"), ["X"], null);
        Assert.False(Hooks.Prefix("t-missing", missing, nameof(TestPatches.ReturnMinusOne), typeof(TestPatches)));
        Assert.Contains(Hooks.Of("t-missing"), h => h.State == Hooks.State.Failed);
    }

    [Fact]
    public void PrivatePatchMethodsAreFound()
    {
        Assert.True(Hooks.Prefix("t-private", Target("Hooked"), "PrivateMinusOne", typeof(PrivatePatches)));
        Assert.Equal(-1, new FakeManager().Hooked(5));
        Hooks.RemoveAll("t-private");
        Assert.Equal(10, new FakeManager().Hooked(5));
    }

    [Fact]
    public void ThreeFailuresWithinAMinute_RemoveTheOwnersHooks()
    {
        string? tripped = null;
        void OnTrip(string owner, string _) { if (owner == "t-trip") tripped = owner; }
        Hooks.Tripped += OnTrip;
        try
        {
            Assert.True(Hooks.Prefix("t-trip", Target("Hooked2"), nameof(TestPatches.ReturnMinusOne), typeof(TestPatches)));

            Hooks.Fail("t-trip", "test", new Exception("1"));
            Hooks.Fail("t-trip", "test", new Exception("2"));
            Lifecycle.Pump();
            Assert.Null(tripped);
            Assert.Equal(-1, new FakeManager().Hooked2(5));

            Hooks.Fail("t-trip", "test", new Exception("3"));
            Lifecycle.Pump();
            Assert.Equal("t-trip", tripped);
            Assert.Equal(15, new FakeManager().Hooked2(5));
        }
        finally
        {
            Hooks.Tripped -= OnTrip;
        }
    }
}
