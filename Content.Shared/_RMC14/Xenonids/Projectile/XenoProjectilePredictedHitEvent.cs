using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Xenonids.Projectile;

[Serializable, NetSerializable]
public sealed class XenoProjectilePredictedHitEvent(int id, NetEntity target, GameTick lastRealTick, TimeSpan hitTime, TimeSpan shotAtTime) : EntityEventArgs
{
    public readonly int Id = id;
    public readonly NetEntity Target = target;
    public readonly GameTick LastRealTick = lastRealTick; // last update the client received from server
    public readonly TimeSpan HitTime = hitTime; // The time the client predicts the hit occurs
    public readonly TimeSpan ShotAtTime = shotAtTime; // The tick the client predicted the projectile was shot
}
