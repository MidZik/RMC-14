using System.Numerics;
using Content.Shared._RMC.Movement;
using Content.Shared._RMC14.Damage.ObstacleSlamming;
using Content.Shared._RMC14.Movement;
using Content.Shared._RMC14.Pulling;
using Content.Shared._RMC14.Stun;
using Content.Shared._RMC14.Weapons.Melee;
using Content.Shared._RMC14.Xenonids.Leap;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.StatusEffect;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Xenonids.Lunge;

public sealed class XenoLungeSystem : EntitySystem
{
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ThrownItemSystem _thrownItem = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly XenoSystem _xeno = default!;
    [Dependency] private readonly RMCPullingSystem _rmcPulling = default!;
    [Dependency] private readonly SharedRMCLagCompensationSystem _rmcLag = default!;
    [Dependency] private readonly RMCObstacleSlammingSystem _rmcObstacleSlamming = default!;
    [Dependency] private readonly XenoLeapSystem _leap = default!;
    [Dependency] private readonly RMCSizeStunSystem _size = default!;

    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<ThrownItemComponent> _thrownItemQuery;
    private EntityQuery<RMCLagCompensationComponent> _lagCompQuery;

    private bool _logPrediction = true;

    private PredictedEventStorage<XenoLungePredictedHitEvent> _predictedEventStorage = new();

    public override void Initialize()
    {
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _thrownItemQuery = GetEntityQuery<ThrownItemComponent>();
        _lagCompQuery = GetEntityQuery<RMCLagCompensationComponent>();

        SubscribeLocalEvent<PhysicsUpdateBeforeSolveEvent>(OnBeforeSolve);

        SubscribeNetworkEvent<XenoLungePredictedHitEvent>(OnPredictedHit);
        SubscribeLocalEvent<XenoActiveLungeComponent, XenoLungePredictedHitEvent>(OnLocalPredictedHit);

        SubscribeLocalEvent<XenoLungeComponent, XenoLungeActionEvent>(OnXenoLungeAction);
        SubscribeLocalEvent<XenoLungeComponent, MeleeAttackAttemptEvent>(OnAttackAttempt);

        SubscribeLocalEvent<XenoActiveLungeComponent, PreventCollideEvent>(OnXenoLungingPreventCollide);
        SubscribeLocalEvent<XenoActiveLungeComponent, ThrowDoHitEvent>(OnXenoLungingHit);
        SubscribeLocalEvent<XenoActiveLungeComponent, LandEvent>(OnXenoLungeLand);

        SubscribeLocalEvent<RMCLungeProtectionComponent, XenoLungeHitAttempt>(OnXenoLungeHitAttempt);

        SubscribeLocalEvent<XenoLungeStunnedComponent, PullStoppedMessage>(OnXenoLungeStunnedPullStopped);

        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
    }

    private void OnBeforeSolve(ref PhysicsUpdateBeforeSolveEvent ev)
    {
        _rmcLag.ProcessEvents(_predictedEventStorage, HandlePredictedHit);
    }

    private void OnPredictedHit(XenoLungePredictedHitEvent msg, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } ent)
            return;

        _rmcLag.SetLastRealTick(args.SenderSession.UserId, msg.LastRealTick);

        _predictedEventStorage.Add(ent, msg.Time, msg, args.SenderSession);
    }

    private void OnLocalPredictedHit(Entity<XenoActiveLungeComponent> xeno, ref XenoLungePredictedHitEvent msg)
    {
        TryComp<ActorComponent>(xeno, out var actor);
        _predictedEventStorage.Add(xeno, msg.Time, msg, actor?.PlayerSession);
    }

    private void HandlePredictedHit(EntityUid xeno, XenoLungePredictedHitEvent msg, ICommonSession? perspectiveSession)
    {
        var offset = msg.Time - _rmcLag.PhysicsCurTime;

        if (offset < -_timing.TickPeriod)
            return; // don't handle events that arrived over a tick too late

        if (!TryComp<XenoActiveLungeComponent>(xeno, out var lunging)
            || !lunging.Running)
            return;

        if (GetEntity(msg.Target) is not { Valid: true } target)
            return;

        if (lunging.Target != target)
            return;

        if (_net.IsServer)
        {
            if (!_rmcLag.Collides(target, xeno, perspectiveSession, offset))
                return;
        }

        TryLungeHit((xeno, lunging), target, true);
    }

    private void OnXenoLungeAction(Entity<XenoLungeComponent> xeno, ref XenoLungeActionEvent args)
    {
        if (args.Entity is not { } target)
            return;

        if (!_xeno.CanAbilityAttackTarget(xeno, target))
            return;

        if (args.Handled)
            return;

        var attempt = new XenoLungeAttemptEvent();
        RaiseLocalEvent(xeno, ref attempt);

        if (attempt.Cancelled)
            return;

        args.Handled = true;

        _rmcPulling.TryStopAllPullsFromAndOn(xeno);

        var origin = _transform.GetMapCoordinates(xeno);
        var targetCoords = _net.IsClient ?
            _rmcLag.GetCoordinates(target, _rmcLag.PhysicsCurTime)
            : _rmcLag.GetCoordinates(target, xeno);
        var diff = targetCoords.Position - origin.Position;
        diff = diff.Normalized() * xeno.Comp.Range;

        TryComp<ActorComponent>(xeno, out var actor);
        var session = actor?.PlayerSession;

        var active = EnsureComp<XenoActiveLungeComponent>(xeno);
        active.Origin = origin;
        active.Charge = diff;
        active.Target = target;
        active.TargetCoordinates = _transform.ToMapCoordinates(targetCoords);
        active.Range = xeno.Comp.Range;
        active.StunTime = xeno.Comp.StunTime;
        active.ClientTickDelay = _timing.CurTick.Value - _rmcLag.GetLastRealTick(session?.UserId).Value;
        Dirty(xeno);

        _rmcObstacleSlamming.MakeImmune(xeno, 0.5f);
        _throwing.TryThrow(xeno, diff, 30, animated: false);

        if (!_physicsQuery.TryGetComponent(xeno, out var physics))
            return;

        // Handle close-range or same-tile lunges
        foreach (var ent in _physics.GetContactingEntities(xeno.Owner, physics))
        {
            if (ent != target)
                continue;

            if (TryLungeHit((xeno.Owner, active), ent, true))
                return;
        }
    }

    private void OnAttackAttempt(Entity<XenoLungeComponent> ent, ref MeleeAttackAttemptEvent args)
    {
        var netAttacker = GetNetEntity(ent);
        if (!TryComp(GetEntity(args.Target), out XenoLungeStunnedComponent? stunned) ||
            netAttacker != stunned.Stunner)
        {
            return;
        }

        switch (args.Attack)
        {
            case DisarmAttackEvent disarm:
                args.Attack = new LightAttackEvent(disarm.Target, netAttacker, disarm.Coordinates);
                break;
        }
    }

    private void OnXenoLungingPreventCollide(Entity<XenoActiveLungeComponent> xeno, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        if (!_lagCompQuery.HasComp(args.OtherEntity))
            return; // Only check and prevent collisions with lag comp'd entities

        if (!_timing.IsFirstTimePredicted)
        {
            // Predict lag comp.
            args.Cancelled = !_rmcLag.Collides(args.OtherEntity,
                xeno.Owner,
                _rmcLag.PhysicsCurTime - xeno.Comp.ClientTickDelay * _timing.TickPeriod);
        }

        if (_net.IsServer)
        {
            // Prevent a collision if the client wouldn't have collided on their end.
            if (TryComp<ActorComponent>(xeno, out var actor)
                && actor.PlayerSession is { } session)
            {
                args.Cancelled = !_rmcLag.Collides(args.OtherEntity,
                    xeno.Owner,
                    _rmcLag.PhysicsCurTime - xeno.Comp.ClientTickDelay * _timing.TickPeriod);
            }
        }

        if (args.Cancelled && _logPrediction)
            Log.Debug($"""
                Prevented lunge collision.
                  PhysTime: {_rmcLag.PhysicsCurTime.TotalSeconds:F3}
                  CompTime: {(_rmcLag.PhysicsCurTime - xeno.Comp.ClientTickDelay * _timing.TickPeriod).TotalSeconds:F3}
                  Delay:    {xeno.Comp.ClientTickDelay}
                  Source:   {xeno}
                  Target:   {args.OtherEntity}
                """);
    }

    private void OnXenoLungingHit(Entity<XenoActiveLungeComponent> xeno, ref ThrowDoHitEvent args)
    {
        if (!_mobState.IsAlive(xeno)
            || HasComp<StunnedComponent>(xeno))
        {
            StopLunge(xeno);
            return;
        }

        if (_mobState.IsDead(args.Target))
            return;

        Log.Debug($"Lunge ThrowDoHitEvent on {_rmcLag.PhysicsCurTime.TotalSeconds:F3}");

        var predictedEv = new XenoLungePredictedHitEvent(
            GetNetEntity(args.Target),
            _rmcLag.GetLastRealTick(null),
            _rmcLag.PhysicsCurTime);

        if (_net.IsClient && _timing.IsFirstTimePredicted)
        {
            if (_logPrediction)
            {
                TryComp(xeno, out TransformComponent? leaperTransform);
                TryComp(args.Target, out TransformComponent? targetTransform);
                Log.Debug($"""
                    SENDING PREDICTED LUNGE HIT!!
                      CurTime:        {_rmcLag.PhysicsCurTime.TotalSeconds:F3}
                      LastRealTick:   {_rmcLag.GetLastRealTick(null)}
                      Phys Substep:   {_rmcLag.GetPhysicsSubstep()}
                      In simulation?  {_timing.InSimulation}
                      ApplyingState?  {_timing.ApplyingState}
                      FirstTimePred?  {_timing.IsFirstTimePredicted}
                      Leaper Coords:  {leaperTransform?.Coordinates}
                      Target Coords:  {targetTransform?.Coordinates}
                    """);
            }

            _rmcLag.SendLastRealTick();
            RaiseNetworkEvent(predictedEv);
        }
        RaiseLocalEvent(predictedEv);
        _rmcLag.ProcessEvents(_predictedEventStorage, HandlePredictedHit, xeno);
    }

    private void OnXenoLungeLand(Entity<XenoActiveLungeComponent> ent, ref LandEvent args)
    {
        // RMC14 TODO why was this here in the first place? investigate
        //if (!_pulling.IsPulling(ent))
        //    TryLungeHit(ent, ent.Comp.Target, false);

        //StopLunge(ent);
    }

    private bool TryLungeHit(Entity<XenoActiveLungeComponent> xeno, EntityUid target, bool stopThrow)
    {
        if (!_mobState.IsAlive(xeno)
            || HasComp<StunnedComponent>(xeno)
            || _mobState.IsDead(target))
            return false;

        if (_logPrediction)
        {
            Log.Debug($"""
                APPLYING LUNGE HIT!!
                  CurTime:        {_rmcLag.PhysicsCurTime.TotalSeconds:F3}
                  Phys Substep:   {_rmcLag.GetPhysicsSubstep()}
                  In simulation?  {_timing.InSimulation}
                  ApplyingState?  {_timing.ApplyingState}
                  FirstTimePred?  {_timing.IsFirstTimePredicted}
                  Leaper Coords:  {Transform(xeno).Coordinates}
                  Target Coords:  {Transform(target).Coordinates}
                """);
        }

        //if (_physicsQuery.TryGetComponent(xeno, out var physics) &&
        //    _thrownItemQuery.TryGetComponent(xeno, out var thrown))
        //{
        //    _thrownItem.LandComponent(xeno, thrown, physics, true);

        //    if (stopThrow)
        //        _thrownItem.StopThrow(xeno, thrown);
        //}

        var ev = new XenoLungeHitAttempt(xeno);
        RaiseLocalEvent(target, ref ev);

        if (ev.Cancelled)
        {
            StopLunge(xeno);
            return true;
        }

        if (!_xeno.CanAbilityAttackTarget(xeno, target) ||
            (_size.TryGetSize(target, out var size) && size >= RMCSizes.Big) ||
            (TryComp<XenoComponent>(target, out var xenoComp) && xenoComp.Tier >= 2)) //Fails if big or tier 2 or more
        {
            StopLunge(xeno);
            return true;
        }

        var curTime = _timing.CurTime;

        if (_net.IsServer)
        {
            var stunTime = _xeno.TryApplyXenoDebuffMultiplier(target, xeno.Comp.StunTime);
            _stun.TryParalyze(target, stunTime, true);

            var stunned = EnsureComp<XenoLungeStunnedComponent>(target);
            stunned.ExpireAt = curTime + stunTime;
            stunned.Stunner = GetNetEntity(xeno);
            Dirty(target, stunned);
        }

        if (TryComp(xeno, out MeleeWeaponComponent? melee))
        {
            melee.NextAttack = curTime;
            Dirty(xeno, melee);
        }

        var targetCoords = xeno.Comp.TargetCoordinates;

        StopLunge(xeno);

        _transform.SetMapCoordinates(target, targetCoords);

        // Fixes lunges done when hugging a wall that would otherwise not move you
        var coordinates = _transform.GetMapCoordinates(xeno);
        if (targetCoords.MapId == coordinates.MapId &&
            !targetCoords.InRange(coordinates, 1.25f))
        {
            var distance = targetCoords.Position - coordinates.Position;
            var length = distance.Length();
            var newPosition = coordinates.Offset(((float)(length - 1.25) / length) * distance);
            _transform.SetMapCoordinates(xeno, newPosition);
        }

        _pulling.TryStartPull(xeno, target);

        return true;
    }

    private void OnXenoLungeStunnedPullStopped(Entity<XenoLungeStunnedComponent> ent, ref PullStoppedMessage args)
    {
        if (args.PulledUid != ent.Owner)
            return;

        foreach (var effect in ent.Comp.Effects)
        {
            _statusEffects.TryRemoveStatusEffect(ent, effect);
        }

        RemCompDeferred<XenoLungeStunnedComponent>(ent.Owner);
    }

    private void OnXenoLungeHitAttempt(Entity<RMCLungeProtectionComponent> ent, ref XenoLungeHitAttempt args)
    {
        if (args.Cancelled)
            return;

        if (!TryComp(args.Lunging, out XenoActiveLungeComponent? lunging))
            return;

        args.Cancelled = _leap.AttemptBlockLeap(ent.Owner, ent.Comp.StunDuration,ent.Comp.BlockSound, args.Lunging, _transform.ToCoordinates(lunging.Origin), ent.Comp.FullProtection);
    }

    private void StopLunge(EntityUid lunging)
    {
        RemCompDeferred<XenoActiveLungeComponent>(lunging);

        if (!_physicsQuery.TryGetComponent(lunging, out var physics))
            return;

        _physics.SetLinearVelocity(lunging, Vector2.Zero, body: physics);
        _physics.SetBodyStatus(lunging, physics, BodyStatus.OnGround);

        if (_thrownItemQuery.TryGetComponent(lunging, out var thrown))
        {
            _thrownItem.LandComponent(lunging, thrown, physics, true);
            _thrownItem.StopThrow(lunging, thrown);
        }
    }

    public override void Update(float frameTime)
    {
        var time = _timing.CurTime;
        var stunnedQuery = EntityQueryEnumerator<XenoLungeStunnedComponent>();
        while (stunnedQuery.MoveNext(out var uid, out var stunned))
        {
            if (time < stunned.ExpireAt)
                continue;

            RemCompDeferred<XenoLungeStunnedComponent>(uid);
        }

        // var activeLungeQuery = EntityQueryEnumerator<XenoActiveLungeComponent>();
        // while (activeLungeQuery.MoveNext(out var uid, out var comp))
        // {
        //     if (!TryComp(uid, out ThrownItemComponent? thrown))
        //     {
        //         RemCompDeferred<XenoActiveLungeComponent>(uid);
        //         continue;
        //     }
        //
        //     if (comp.Origin.MapId != comp.TargetCoordinates.MapId)
        //     {
        //         _thrownItem.StopThrow(uid, thrown);
        //         continue;
        //     }
        //
        //     var coords = _transform.GetMapCoordinates(uid);
        //     var range = (comp.Origin.Position - comp.TargetCoordinates.Position).Length();
        //     if (!comp.Origin.InRange(coords, range))
        //     {
        //         if (!_pulling.IsPulling(uid))
        //             TryLungeHit((uid, comp), comp.Target, true);
        //     }
        // }
    }
}

[ByRefEvent]
public record struct XenoLungeHitAttempt(EntityUid Lunging, bool Cancelled = false);
