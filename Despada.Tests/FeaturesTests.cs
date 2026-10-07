// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core;
using Despada.Core.Bind;
using Xunit;

namespace Despada.Tests;

public class FeaturesTests
{
    private sealed class FakeFeature(string id, Binding[] requires, bool byDefault, bool throwOnEnable = false) : Feature
    {
        public int Enabled, Disabled;

        public override string Id => id;
        public override string Title => id;
        public override IReadOnlyList<Binding> Requires => requires;
        public override bool EnabledByDefault => byDefault;

        protected internal override void OnEnable()
        {
            if (throwOnEnable) throw new InvalidOperationException("boom");
            Enabled++;
        }

        protected internal override void OnDisable() => Disabled++;
    }

    private static TypeRef Present => new("Despada.Tests.Fakes.FakeManager");
    private static TypeRef Missing => new("Robust.Nope.Missing");

    [Fact]
    public void MissingBinding_MakesItUnavailable_WithReason()
    {
        var f = new FakeFeature("f-missing", [Present, Missing], byDefault: true);
        Features.Register(f);
        Assert.Equal(FeatureState.Unavailable, f.State);
        Assert.Contains("Missing", f.Reason);
        Assert.Equal(0, f.Enabled);

        Features.SetEnabled(f, true);
        Assert.Equal(FeatureState.Unavailable, f.State);
    }

    [Fact]
    public void EnabledByDefault_StartsOn_AndSwitchesOff()
    {
        var f = new FakeFeature("f-default", [Present], byDefault: true);
        Features.Register(f);
        Assert.Equal(FeatureState.On, f.State);
        Assert.Equal(1, f.Enabled);

        Features.SetEnabled(f, false);
        Assert.Equal(FeatureState.Off, f.State);
        Assert.Equal(1, f.Disabled);
    }

    [Fact]
    public void ThrowingOnEnable_MarksCrashed()
    {
        var f = new FakeFeature("f-throw", [], byDefault: true, throwOnEnable: true);
        Features.Register(f);
        Assert.Equal(FeatureState.Crashed, f.State);
        Assert.Contains("boom", f.Reason);
    }

    private sealed class BadHookFeature : Feature
    {
        public override string Id => "f-badhook";
        public override string Title => "bad hook";
        public override bool EnabledByDefault => true;

        protected internal override void OnEnable()
            => Hooks.Prefix(Id, new MethodRef(new TypeRef("Robust.Nope.Gone"), ["X"], null), "Nope", typeof(BadHookFeature));
    }

    [Fact]
    public void HookFailingDuringEnable_CrashesIt()
    {
        var f = new BadHookFeature();
        Features.Register(f);
        Assert.Equal(FeatureState.Crashed, f.State);
    }

    [Fact]
    public void RepeatedFailures_CrashIt()
    {
        var f = new FakeFeature("f-trip", [], byDefault: true);
        Features.Initialize();
        Features.Register(f);
        Assert.Equal(FeatureState.On, f.State);

        for (var i = 0; i < 3; i++)
            Hooks.Fail("f-trip", "tick", new Exception($"e{i}"));
        Lifecycle.Pump();

        Assert.Equal(FeatureState.Crashed, f.State);
        Assert.Contains("3×", f.Reason);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        Features.SetSetting("f-settings", "buffer", 20000);
        Assert.Equal(20000, Features.GetSetting("f-settings", "buffer", 0));
        Assert.Equal("x", Features.GetSetting("f-settings", "absent", "x"));
    }
}
