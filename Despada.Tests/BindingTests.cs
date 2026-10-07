// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core.Bind;
using Despada.Tests.Fakes;
using Xunit;

namespace Despada.Tests;

public class BindingTests
{
    private const string Mgr = "Despada.Tests.Fakes.FakeManager";

    [Fact]
    public void TypeRef_FindsByFullName_AndFallsBack()
    {
        var t = new TypeRef("Robust.Nope.Renamed", Mgr);
        t.Resolve();
        Assert.True(t.Ok);
        Assert.Equal(typeof(FakeManager), t.Type);
    }

    [Fact]
    public void TypeRef_Missing_ReportsInsteadOfThrowing()
    {
        var t = new TypeRef("Robust.Nope.Missing");
        t.Resolve();
        Assert.False(t.Ok);
        Assert.Contains("Robust.Nope.Missing", t.Error);
    }

    [Fact]
    public void MethodRef_PicksOverloadByParameterTypeNames()
    {
        var owner = new TypeRef(Mgr);
        var m = new MethodRef(owner, ["Handle"], ["FakeArgs"]);
        m.Resolve();
        Assert.True(m.Ok, m.Error);

        var mgr = new FakeManager();
        m.Invoke(mgr, new FakeArgs(5));
        Assert.Equal(5, mgr.Calls);
    }

    [Fact]
    public void MethodRef_FullTypeNamesWork()
    {
        var m = new MethodRef(new TypeRef(Mgr), ["Add"], ["System.Int32", "System.Int32"]);
        m.Resolve();
        Assert.True(m.Ok, m.Error);
        Assert.Equal(7, m.Invoke(new FakeManager(), 3, 4));
    }

    [Fact]
    public void MethodRef_AmbiguousWithoutParameters()
    {
        var m = new MethodRef(new TypeRef(Mgr), ["Add"], null);
        m.Resolve();
        Assert.False(m.Ok);
        Assert.Contains("ambiguous", m.Error);
    }

    [Fact]
    public void MethodRef_PrivateAndStatic_AndFallbackNames()
    {
        var owner = new TypeRef(Mgr);
        var secret = new MethodRef(owner, ["OldSecret", "Secret"], null);
        var shout  = new MethodRef(owner, ["Shout"], null);
        secret.Resolve();
        shout.Resolve();

        Assert.Equal(42, secret.Invoke(new FakeManager()));
        Assert.Equal("HIs", shout.Invoke(null, "hi"));
    }

    [Fact]
    public void MethodRef_MissingOwner_CarriesOwnerError()
    {
        var m = new MethodRef(new TypeRef("Robust.Nope.Gone"), ["X"], null);
        m.Resolve();
        Assert.False(m.Ok);
        Assert.Contains("Robust.Nope.Gone", m.Error);
        Assert.Throws<InvalidOperationException>(() => m.Invoke(null));
    }

    [Fact]
    public void FieldRef_ReadsAndWritesPrivateAndStatic()
    {
        var owner   = new TypeRef(Mgr);
        var counter = new FieldRef(owner, "_renamed", "_counter");
        var stat    = new FieldRef(owner, "_static");
        counter.Resolve();
        stat.Resolve();

        var mgr = new FakeManager();
        Assert.Equal(41, counter.Get(mgr));
        counter.Set(mgr, 1);
        Assert.Equal(1, counter.Get(mgr));
        Assert.Equal("s", stat.Get(null));
    }

    [Fact]
    public void ManagerRef_WithoutIoC_IsNotOk()
    {
        // No Robust here: IoCManager isn't loaded, so the manager can't be resolved.
        var m = new ManagerRef(new TypeRef(Mgr));
        m.Resolve();
        Assert.False(m.Ok);
        Assert.Null(m.Instance);
    }
}
