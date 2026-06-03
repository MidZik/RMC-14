using System.Numerics;
using Content.Shared._RMC14.Explosion;
using Content.Shared._RMC14.Light;
using Content.Shared._RMC14.Movement;
using Content.Shared._RMC14.Projectiles;
using Content.Shared._RMC14.Random;
using Content.Shared._RMC14.Weapons.Ranged;
using Content.Shared._RMC14.Weapons.Ranged.Prediction;
using Content.Shared._RMC14.Xenonids.Construction;
using Content.Shared._RMC14.Xenonids.Hive;
using Content.Shared._RMC14.Xenonids.Plasma;
using Content.Shared.FixedPoint;
using Content.Shared.GameTicking;
using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared._RMC14.Xenonids.Projectile;

public sealed class XenoProjectileSystem : EntitySystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedGunPredictionSystem _gunPrediction = default!;
    [Dependency] private readonly SharedXenoHiveSystem _hive = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedProjectileSystem _projectile = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedRMCLagCompensationSystem _rmcLag = default!;
    [Dependency] private readonly CMPoweredLightSystem _rmcPoweredLight = default!;
    [Dependency] private readonly RMCPseudoRandomSystem _rmcPseudoRandom = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly XenoSystem _xeno = default!;
    [Dependency] private readonly XenoPlasmaSystem _xenoPlasma = default!;

    private EntityQuery<ProjectileComponent> _projectileQuery;
    private EntityQuery<PreventAttackLightOffComponent> _preventAttackLightOffQuery;

    private int _limitHitsId;
    private bool _logPrediction = false;
    private PredictedEventStorage<XenoProjectilePredictedHitEvent> _predictedEventStorage = new();

    public override void Initialize()
    {
        _projectileQuery = GetEntityQuery<ProjectileComponent>();
        _preventAttackLightOffQuery = GetEntityQuery<PreventAttackLightOffComponent>();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeNetworkEvent<XenoProjectilePredictedHitEvent>(OnPredictedHit);

        SubscribeLocalEvent<XenoProjectileShooterComponent, ComponentRemove>(OnShooterRemove);
        SubscribeLocalEvent<XenoProjectileShooterComponent, EntityTerminatingEvent>(OnShooterRemove);

        SubscribeLocalEvent<XenoProjectileShotComponent, ComponentRemove>(OnShotRemove);
        SubscribeLocalEvent<XenoProjectileShotComponent, EntityTerminatingEvent>(OnShotRemove);

        SubscribeLocalEvent<XenoClientProjectileShotComponent, ProjectileHitEvent>(OnShotHit);

        SubscribeLocalEvent<XenoProjectileComponent, PreventCollideEvent>(OnPreventCollide);
        SubscribeLocalEvent<XenoProjectileComponent, ProjectileHitEvent>(OnProjectileHit);
        SubscribeLocalEvent<XenoProjectileComponent, CMClusterSpawnedEvent>(OnClusterSpawned);

        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _limitHitsId = 0;
    }

    private void OnPredictedHit(XenoProjectilePredictedHitEvent msg, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } ent)
            return;

        _rmcLag.SetLastRealTick(args.SenderSession.UserId, msg.LastRealTick);

        _predictedEventStorage.Add(ent, msg.Time, msg, args.SenderSession);
    }

    private void HandlePredictedHit(EntityUid xeno, XenoProjectilePredictedHitEvent msg, ICommonSession? perspectiveSession)
    {
        if (_net.IsClient || !_gunPrediction.GunPrediction)
            return;

        var msgTime = msg.Time;
        var projTime = msg.ProjTime;
        var curTime = _timing.CurTime;

        if (msgTime < curTime && _logPrediction)
        {
            Log.Warning($"Predicted hit message arrived late (Message for {msgTime.TotalSeconds:F3}, current time {curTime.TotalSeconds:F3}).");
        }

        if (GetEntity(msg.Target) is not { Valid: true } target)
            return;

        if (!TryComp(xeno, out XenoProjectileShooterComponent? shooter)
            || shooter.Shot.Count == 0
            || !shooter.Shot.TryFirstOrNull(e => CompOrNull<XenoProjectileShotComponent>(e)?.Id == msg.Id, out var shot))
        {
            if (_logPrediction)
                Log.Debug($"Predicted shot ID {msg.Id} not found! (Time {curTime.TotalSeconds:F3})");
            return;
        }

        if (TerminatingOrDeleted(shot))
        {
            if (_logPrediction)
                Log.Warning($"Predicted shot ID {msg.Id} already deleted! (Time {curTime.TotalSeconds:F3})");
            return;
        }

        var coordinates = _transform.ToMapCoordinates(_rmcLag.GetCoordinates(target, perspectiveSession));

        if (!TryComp(shot, out ProjectileComponent? projectile) ||
            !TryComp(shot, out PhysicsComponent? physics))
        {
            return;
        }

        if (!_rmcLag.Collides(target, (shot.Value, physics), coordinates, projTime - curTime))
            return;

        _projectile.ProjectileCollide((shot.Value, projectile, physics), target, true);
    }

    private void OnShooterRemove<T>(Entity<XenoProjectileShooterComponent> ent, ref T args)
    {
        if (_timing.ApplyingState)
            return;

        foreach (var shot in ent.Comp.Shot)
        {
            RemCompDeferred<XenoProjectileShotComponent>(shot);
        }

        ent.Comp.Shot.Clear();
        Dirty(ent);
    }

    private void OnShotRemove<T>(Entity<XenoProjectileShotComponent> ent, ref T args)
    {
        if (ent.Comp.ShooterEnt is not { } shooter)
            return;

        if (TryComp(shooter, out XenoProjectileShooterComponent? shooterComp) &&
            shooterComp.Shot.Remove(ent))
        {
            Dirty(shooter, shooterComp);
        }
    }

    // TODO RMC14 There is a bug with clients trying to predict StartCollideEvent on our version of RT.
    // This should be fixed in the newest versions of RT (2026 or later), and then we can change this back
    // to react to StartCollideEvent.
    private void OnShotHit(Entity<XenoClientProjectileShotComponent> ent, ref ProjectileHitEvent args)
    {
        if (_net.IsServer || !IsClientSide(ent))
            return;

        if (!TryComp(ent, out XenoProjectileShotComponent? shot))
            return;

        // LatestPredictionTime is the time of the last processed tick. Due to how the physics system works,
        // collisions that occur on the last substep aren't processed until the start of the next tick.
        // This leads to a situation where at the end of a tick, the positions of entities are actually where
        // they should be at the start of the NEXT tick. Thus, if we rewind the game state, LatestPredictionTime
        // is one full tick behind, because the next tick which would process collisions for those positions
        // hasn't actually run, but also the code that updates LatestPredictedTime ALSO hasn't run. To compensate
        // for this, we correct curTime and projTime to what they would have been at the start of the next tick.
        var curTime = ent.Comp.LatestPredictedTime;
        var projTime = _rmcLag.PhysicsCurTime;
        if (!_timing.IsFirstTimePredicted)
        {
            curTime += _timing.TickPeriod;
            projTime = curTime;
        }

        if (_logPrediction)
        {
            TryComp(args.Target, out TransformComponent? targetTransform);
            TryComp(ent, out TransformComponent? shotTransform);
            Log.Debug($"""
                SENDING PREDICTED PROJECTILE HIT!!
                  ShotId:          {shot.Id}
                  CurTime:         {_timing.CurTime.TotalSeconds:F3}
                  LastRealTick:    {_rmcLag.GetLastRealTick(null)}
                  Phys Substep:    {_rmcLag.GetPhysicsSubstep()}
                  In simulation?   {_timing.InSimulation}
                  ApplyingState?   {_timing.ApplyingState}
                  FirstTimePred?   {_timing.IsFirstTimePredicted}
                  ProjLatestTime:  {curTime.TotalSeconds:F3}
                  ProjPhysicsTime: {projTime.TotalSeconds:F3}
                  ShotCoords:      {shotTransform?.Coordinates}
                  Target Coords:   {targetTransform?.Coordinates}
                """);
        }

        var ev = new XenoProjectilePredictedHitEvent(
            shot.Id,
            GetNetEntity(args.Target),
            _rmcLag.GetLastRealTick(null),
            curTime,
            projTime
        );
        RaiseNetworkEvent(ev);
    }

    private void OnPreventCollide(Entity<XenoProjectileComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        if (_preventAttackLightOffQuery.HasComp(args.OtherEntity) &&
            _rmcPoweredLight.IsOff(args.OtherEntity))
        {
            args.Cancelled = true;
            return;
        }

        if (ent.Comp.DeleteOnFriendlyXeno)
            return;

        if (_hive.FromSameHive(ent.Owner, args.OtherEntity) &&
            (HasComp<XenoComponent>(args.OtherEntity) || HasComp<HiveCoreComponent>(args.OtherEntity)))
            args.Cancelled = true;
    }

    private void OnProjectileHit(Entity<XenoProjectileComponent> ent, ref ProjectileHitEvent args)
    {
        if (_hive.FromSameHive(ent.Owner, args.Target))
        {
            args.Handled = true;

            if (_net.IsServer || IsClientSide(ent))
                QueueDel(ent);

            return;
        }

        if (HasComp<XenoComponent>(args.Target))
            args.Damage = _xeno.TryApplyXenoProjectileDamageMultiplier(args.Target, args.Damage);

        if (_projectileQuery.TryComp(ent, out var projectile) &&
            projectile.Shooter is { } shooter)
        {
            var ev = new XenoProjectileHitUserEvent(args.Target);
            RaiseLocalEvent(shooter, ref ev);
        }
    }

    private void OnClusterSpawned(Entity<XenoProjectileComponent> ent, ref CMClusterSpawnedEvent args)
    {
        if (_hive.GetHive(ent.Owner) is not {} hive)
            return;

        foreach (var spawned in args.Spawned)
        {
            _hive.SetHive(spawned, hive);
        }
    }

    public bool TryShoot(
        EntityUid xeno,
        EntityCoordinates targetCoords,
        FixedPoint2 plasma,
        EntProtoId projectileId,
        SoundSpecifier? sound,
        int shots,
        Angle deviation,
        float speed,
        float? stopAtDistance = null,
        EntityUid? target = null,
        bool predicted = true,
        int? projectileHitLimit = null)
    {
        if (!predicted && _net.IsClient)
            return false;

        var origin = _transform.GetMapCoordinates(xeno);
        var targetMap = _transform.ToMapCoordinates(targetCoords);
        if (origin.MapId != targetMap.MapId ||
            origin.Position == targetMap.Position)
        {
            return false;
        }

        if (!_xenoPlasma.TryRemovePlasmaPopup(xeno, plasma))
            return false;

        _audio.PlayPredicted(sound, xeno, xeno);
        if (_net.IsClient && !_gunPrediction.GunPrediction || !_timing.IsFirstTimePredicted)
            return true;

        var ammoShotEvent = new AmmoShotEvent { FiredProjectiles = new List<EntityUid>(shots) };

        if (target != null && HasComp<MobStateComponent>(target) && !_xeno.CanAbilityAttackTarget(xeno, target.Value))
            target = null;

        XenoProjectileShooterComponent? shooter = null;
        var shooterPlayer = CompOrNull<ActorComponent>(xeno)?.PlayerSession;
        var xoroshiro = _rmcPseudoRandom.GetXoroshiro64S(xeno);

        var originalDiff = targetMap.Position - origin.Position;
        var halfDeviation = deviation / 2;
        if (projectileHitLimit != null)
            _limitHitsId++;

        var curTime = _timing.CurTime;
        for (var i = 0; i < shots; i++)
        {
            // center projectile has no deviation; others are randomly offset within deviation
            var angleOffset = Angle.Zero;
            if (i > 0 && deviation != Angle.Zero)
                angleOffset = _rmcPseudoRandom.NextAngle(ref xoroshiro, -halfDeviation, halfDeviation);

            var projTarget = new MapCoordinates(origin.Position + angleOffset.RotateVec(originalDiff), targetMap.MapId);

            var diff = projTarget.Position - origin.Position;
            var projectile = Spawn(projectileId, origin);
            diff *= speed / diff.Length();

            _gun.ShootProjectile(projectile, diff, Vector2.Zero, xeno, xeno, speed);

            var ev = new ProjectileShotEvent(xeno, predicted);
            RaiseLocalEvent(projectile, ref ev);

            ammoShotEvent.FiredProjectiles.Add(projectile);

            // let hive member logic apply
            EnsureComp<XenoProjectileComponent>(projectile);

            _hive.SetSameHive(xeno, projectile);

            if (stopAtDistance != null)
            {
                var fixedDistanceComp = EnsureComp<ProjectileFixedDistanceComponent>(projectile);
                fixedDistanceComp.FlyEndTime = _timing.CurTime + TimeSpan.FromSeconds(stopAtDistance.Value / speed);
                Dirty(projectile, fixedDistanceComp);
            }

            if (target != null)
            {
                var targeted = EnsureComp<TargetedProjectileComponent>(projectile);
                targeted.Target = target.Value;
                Dirty(projectile, targeted);
            }

            if (projectileHitLimit != null)
            {
                var limitHits = EnsureComp<ProjectileLimitHitsComponent>(projectile);
                limitHits.Limit = projectileHitLimit.Value;
                limitHits.OriginEntityId = xeno.Id;
                limitHits.ExtraId = _limitHitsId;
                Dirty(projectile, limitHits);
            }

            if (predicted)
            {
                shooter ??= EnsureComp<XenoProjectileShooterComponent>(xeno);
                shooter.Shot.Add(projectile);
                Dirty(xeno, shooter);

                var shot = EnsureComp<XenoProjectileShotComponent>(projectile);
                shot.Id = shooter.NextId++;
                shot.Shooter = shooterPlayer;
                shot.ShooterEnt = xeno;
                Dirty(projectile, shot);
            }

            if (_net.IsServer)
                continue;

            var clientShot = EnsureComp<XenoClientProjectileShotComponent>(projectile);
            clientShot.LatestPredictedTime = curTime;
            _physics.UpdateIsPredicted(projectile);
        }

        RaiseLocalEvent(xeno, ammoShotEvent);

        // Client may have already predicted hits for this projectile, check before we test collisions.
        if (_net.IsServer && predicted)
        {
            _rmcLag.ProcessEvents(_predictedEventStorage, HandlePredictedHit, xeno);
        }

        return true;
    }

    public override void Update(float frameTime)
    {
        if (_net.IsClient)
        {
            if (!_timing.IsFirstTimePredicted)
                return;

            var shotQuery = EntityQueryEnumerator<XenoClientProjectileShotComponent>();
            while (shotQuery.MoveNext(out var uid, out var comp))
            {
                comp.LatestPredictedTime = _timing.CurTime;
            }
        }
        else // server
        {
            _rmcLag.ProcessEvents(_predictedEventStorage, HandlePredictedHit);
        }
    }
}
