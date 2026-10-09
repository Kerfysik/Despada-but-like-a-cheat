// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

// Stand-ins for the engine types NetTypes recognises by name.

namespace Robust.Shared.GameObjects
{
    public abstract class EntityEventArgs { }
    public abstract class BaseBoundUserInterfaceEvent : EntityEventArgs { }
    public class BoundUserInterfaceMessage : BaseBoundUserInterfaceEvent { }   // as in Robust
    public abstract class BaseBoundUIWrapMessage : EntityEventArgs { }
}

namespace Despada.Tests
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class NetSerializableAttribute : Attribute { }
    public abstract class FakeNetMessage { }
    public sealed class MsgFakePing : FakeNetMessage { }
    [NetSerializable] public sealed class FakeInteractEvent : Robust.Shared.GameObjects.EntityEventArgs { }
    public sealed class LocalOnlyEvent : Robust.Shared.GameObjects.EntityEventArgs { }   // not sent over the wire
    [NetSerializable] public sealed class FakeStorageMessage : Robust.Shared.GameObjects.BoundUserInterfaceMessage { }
    [NetSerializable] public sealed class FakeWrapMessage : Robust.Shared.GameObjects.BaseBoundUIWrapMessage { }
}
