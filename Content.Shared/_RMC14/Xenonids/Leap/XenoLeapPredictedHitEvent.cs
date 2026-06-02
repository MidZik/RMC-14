using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Xenonids.Leap;

[Serializable, NetSerializable]
public sealed class XenoLeapPredictedHitEvent(NetEntity target, GameTick lastRealTick, TimeSpan time) : EntityEventArgs
{
    public readonly NetEntity Target = target;
    public readonly GameTick LastRealTick = lastRealTick;
    public readonly TimeSpan Time = time; // Time the client predicted the event.
}
