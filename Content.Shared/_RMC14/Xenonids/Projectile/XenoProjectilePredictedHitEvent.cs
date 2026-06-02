using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Xenonids.Projectile;

[Serializable, NetSerializable]
public sealed class XenoProjectilePredictedHitEvent(int id, NetEntity target, GameTick lastRealTick, TimeSpan time, TimeSpan projTime) : EntityEventArgs
{
    public readonly int Id = id;
    public readonly NetEntity Target = target;
    public readonly GameTick LastRealTick = lastRealTick; // last update the client received from server
    public readonly TimeSpan Time = time; // The time the client predicts the hit occurs
    public readonly TimeSpan ProjTime = projTime; // How far the projectile was simulated (yes it's different)
}
