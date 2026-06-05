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
    private bool _predictingSpecificShooter = false;
    private PredictedEventStorage<XenoProjectilePredictedHitEvent> _predictedEventStorage = new();

    public override void Initialize()
    {
        _projectileQuery = GetEntityQuery<ProjectileComponent>();
        _preventAttackLightOffQuery = GetEntityQuery<PreventAttackLightOffComponent>();

        SubscribeLocalEvent<PhysicsUpdateBeforeSolveEvent>(OnBeforeSolve);

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

    private void OnBeforeSolve(ref PhysicsUpdateBeforeSolveEvent ev)
    {
        if (_net.IsServer)
            _rmcLag.ProcessEvents(_predictedEventStorage, HandlePredictedHit);
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _limitHitsId = 0;
    }

    private void OnPredictedHit(XenoProjectilePredictedHitEvent msg, EntitySessionEventArgs args)
    {
        if (_net.IsClient || !_gunPrediction.GunPrediction)
            return;

        var curTime = _timing.CurTime;

        if (msg.HitTime > curTime + TimeSpan.FromSeconds(0.7))
        {
            // protection against naughty clients, or weird networking issues, or server is lagging bad.
            Log.Warning($"Discarding extremely early predicted hit message from '{args.SenderSession} for time {msg.HitTime.TotalSeconds:F3}. Current time is {_timing.CurTime.TotalSeconds:F3}.");
            return;
        }

        if (args.SenderSession.AttachedEntity is not { } ent)
            return;

        if (_logPrediction)
            Log.Debug($"""
                Received predicted hit:
                  Session:   {args.SenderSession}
                  Cur Time:  {curTime.TotalSeconds:F3}
                  Target:    {msg.Target}
                  Shot ID:   {msg.Id}
                  Shot Time: {msg.ShotAtTime.TotalSeconds:F3}
                  Hit Time:  {msg.HitTime.TotalSeconds:F3}
                """);

        _rmcLag.SetLastRealTick(args.SenderSession.UserId, msg.LastRealTick);


        _predictedEventStorage.Add(ent, msg.HitTime, msg, args.SenderSession);
    }

    private bool HandlePredictedHit(ref PredictedEvent<XenoProjectilePredictedHitEvent> @event)
    {
        var xeno = @event.Source;
        var msg = @event.Event;
        var perspectiveSession = @event.Session;

        if (GetEntity(msg.Target) is not { Valid: true } target
            || !TryComp<XenoProjectileShooterComponent>(xeno, out var shooter))
        {
            if (_logPrediction)
                Log.Warning($"Predicted hit from '{perspectiveSession}' discarded due to invalid data.");
            return true;
        }

        var hitTime = msg.HitTime;
        var curTime = _timing.CurTime;
        var curPhysTime = _rmcLag.PhysicsCurTime;

        if (hitTime > curPhysTime)
        {
            // This shouldn't happen, ProcessEvents should be delaying it.
            DebugTools.Assert(!(hitTime > curPhysTime));
            return false;
        }

        if (shooter.NextId <= msg.Id)
        {
            // The shooter hasn't shot our predicted shot yet.
            // The message is either being processed too early, OR
            // the server is shooting the shot later than expected, OR
            // the server was unable to shoot the projectile as predicted.
            if (_logPrediction)
                Log.Debug($"Predicted hit from '{perspectiveSession}' for shot {msg.Id} at time {hitTime.TotalSeconds:F3}, but the latest shot was {shooter.NextId - 1} at time {curPhysTime.TotalSeconds:F3})");
            return false;
        }

        if (shooter.Shot.Count == 0
            || !shooter.Shot.TryFirstOrNull(e => CompOrNull<XenoProjectileShotComponent>(e)?.Id == msg.Id, out var shot)
            || TerminatingOrDeleted(shot))
        {
            // The shooter shot our predicted shot, but we failed to find it. It has either hit something
            // or its duration has expired.
            if (_logPrediction)
                Log.Debug($"Predicted hit from '{perspectiveSession}' could not find shot {msg.Id} after it was shot.");
            return true;
        }

        if (!TryComp(shot, out XenoProjectileShotComponent? xenoShot)
            || !TryComp(shot, out ProjectileComponent? projectile)
            || !TryComp(shot, out PhysicsComponent? physics))
        {
            Log.Warning($"Predicted hit from '{perspectiveSession}' found a shot without a necessary component.");
            return true;
        }

        if (projectile.ProjectileSpent)
        {
            if (_logPrediction)
                Log.Debug($"Predicted hit from '{perspectiveSession}' shot {msg.Id} is spent and cannot hit anything anymore.");
            return true;
        }

        // if server shot later than predicted, adjust the shot forward to try
        var shotAtDiff = xenoShot.ShotAtTime - msg.ShotAtTime;
        hitTime += shotAtDiff;
        var offset = hitTime - curPhysTime;

        if (_logPrediction && shotAtDiff > _timing.TickPeriod)
        {
            Log.Debug($"Predicted hit from '{perspectiveSession}' predicted shot at {msg.ShotAtTime.TotalSeconds:F3}" +
                $" but it was shot at {xenoShot.ShotAtTime.TotalSeconds:F3}. Adjusting hit time to {hitTime.TotalSeconds:F3}.");
        }

        if (hitTime > curPhysTime + _timing.TickPeriod)
        {
            if (_logPrediction)
                Log.Debug($"Predicted hit from '{perspectiveSession}' had its hit time adjusted too far forward. Delaying hit processing.");
            // To prevent the event from being constantly processed, we can change the expected hit time
            @event.PredictedTime = hitTime;
            return false;
        }

        if (hitTime < curPhysTime - _timing.TickPeriod)
        {
            if (_logPrediction)
                Log.Debug($"Predicted hit from '{perspectiveSession}' is too far in the past. Discarding.");
            return true;
        }

        if (_logPrediction)
            Log.Debug($"""
                Predicted hit checks passed, will test collision. Details:
                  Session Name:    {perspectiveSession}
                  Last Real Tick:  {msg.LastRealTick}
                  Shot ID:         {msg.Id}
                  During shoot?    {_predictingSpecificShooter}

                  Cur Time:        {curPhysTime.TotalSeconds:F3}
                  Pred Hit Time:   {hitTime.TotalSeconds:F3}
                  Hit Time Offset: {offset.TotalMilliseconds} ms

                  Real Shot Time:  {xenoShot.ShotAtTime.TotalSeconds:F3}
                  Pred Shot Time:  {msg.ShotAtTime.TotalSeconds:F3}
                """);

        if (perspectiveSession != null)
            _rmcLag.SetLastRealTick(perspectiveSession.UserId, msg.LastRealTick);
        var hitConfirmed = _rmcLag.Collides(target, (shot.Value, physics), perspectiveSession, offset);

        if (hitConfirmed)
        {
            if (_logPrediction)
                Log.Debug($"Predicted hit from '{perspectiveSession}' ++ CONFIRMED!! ++");

            _projectile.ProjectileCollide((shot.Value, projectile, physics), target, true);
        }
        else if (_logPrediction)
        {
            Log.Warning($"Predicted hit from '{perspectiveSession}' -- denied --");
        }

        return true;
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

        var hitTime = _rmcLag.PhysicsCurTime;
        var shotTime = shot.ShotAtTime;

        if (!_timing.IsFirstTimePredicted)
        {
            // If a collision happens during a re-predicted frame, the projectile is actually at substep 0
            // for the next frame. This is because on tick x, after the physics system runs, objects will
            // actually be at their starting positions for tick x + 1. This includes our projectile.
            // We don't have an up-to-date value for LatestPredictedTime because tick x + 1 hasn't run yet.
            hitTime = ent.Comp.LatestPredictedTime + _timing.TickPeriod;
        }

        if (_logPrediction)
        {
            TryComp(args.Target, out TransformComponent? targetTransform);
            TryComp(ent, out TransformComponent? shotTransform);
            Log.Debug($"""
                SENDING PREDICTED PROJECTILE HIT!!
                  Shot ID:         {shot.Id}
                  Cur Time:        {_timing.CurTime.TotalSeconds:F3}
                  LastRealTick:    {_rmcLag.GetLastRealTick(null)}
                  Phys Substep:    {_rmcLag.GetPhysicsSubstep()}
                  In simulation?   {_timing.InSimulation}
                  ApplyingState?   {_timing.ApplyingState}
                  FirstTimePred?   {_timing.IsFirstTimePredicted}
                  Proj Shot At:    {shotTime.TotalSeconds:F3}
                  Proj Hit Time:   {hitTime.TotalSeconds:F3}
                  Shot Coords:     {shotTransform?.Coordinates}
                  Target Coords:   {targetTransform?.Coordinates}
                """);
        }

        var ev = new XenoProjectilePredictedHitEvent(
            shot.Id,
            GetNetEntity(args.Target),
            _rmcLag.GetLastRealTick(null),
            hitTime,
            shotTime
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
            if (_logPrediction
                && TryComp<XenoProjectileShotComponent>(ent, out var shot)
                && TryComp<ActorComponent>(shooter, out var actor))
                Log.Debug($"""
                    --- ACTUAL HIT!
                      Session:   {actor.PlayerSession}
                      Shot ID:   {shot.Id}
                      Target:    {args.Target}
                      Phys Time: {_rmcLag.PhysicsCurTime}
                    """);
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
                shot.ShotAtTime = _rmcLag.PhysicsCurTime;
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
            _predictingSpecificShooter = true;
            _rmcLag.ProcessEvents(_predictedEventStorage, HandlePredictedHit, xeno);
            _predictingSpecificShooter = false;
        }

        return true;
    }

    public override void Update(float frameTime)
    {
        if (_net.IsClient)
        {
            if (!_timing.IsFirstTimePredicted)
                return;

            var curTime = _timing.CurTime;
            var shotQuery = EntityQueryEnumerator<XenoClientProjectileShotComponent>();
            while (shotQuery.MoveNext(out var uid, out var comp))
            {
                comp.LatestPredictedTime = curTime;
            }
        }
    }
}
