using System.Runtime.InteropServices;
using Content.Shared._RMC.Movement;
using Content.Shared._RMC14.CCVar;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared._RMC14.Movement;

public abstract class SharedRMCLagCompensationSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public float MarginTiles { get; private set; }

    private GameTick _lastPhysicCurTimeUpdateTick;
    private TimeSpan _physicsCurTime;
    public TimeSpan PhysicsCurTime
    {
        get
        {
            if (_lastPhysicCurTimeUpdateTick != _timing.CurTick)
            {
                // _timing.CurTime cares about if it's in simulation or not.
                // We need it to always be calculated as if it's in simulation.
                // For now, we rip out its code for ourselves to ensure the
                // calculation is exactly how we want it.
                var (time, lastTimeTick) = _timing.TimeBase;
                time += _timing.TickPeriod.Mul(_timing.CurTick.Value - lastTimeTick.Value);

                _physicsCurTime = time;
                _lastPhysicCurTimeUpdateTick = _timing.CurTick;
            }
            return _physicsCurTime;
        }
        private set
        {
            _physicsCurTime = value;
        }
    }

    public TimeSpan BufferTime = TimeSpan.FromMilliseconds(750);

    private EntityQuery<ActorComponent> _actorQuery;
    private EntityQuery<FixturesComponent> _fixturesQuery;

    private int _substeps;
    private float _substepPeriod;
    private TimeSpan _substepSpan;
    private float _tickPeriod;
    private TimeSpan _tickSpan;

    private bool _logPrediction = true;

    private readonly Dictionary<NetUserId, GameTick> _lastRealTicks = new();


    public override void Initialize()
    {
        base.Initialize();

        _actorQuery = GetEntityQuery<ActorComponent>();
        _fixturesQuery = GetEntityQuery<FixturesComponent>();

        SubscribeNetworkEvent<RMCSetLastRealTickEvent>(OnSetLastRealTick);

        SubscribeLocalEvent<RMCLagCompensationComponent, MoveEvent>(OnLagMove);

        SubscribeLocalEvent<PhysicsUpdateAfterSolveEvent>(OnAfterSolve);

        Subs.CVar(_config,
            RMCCVars.RMCLagCompensationMilliseconds,
            v => BufferTime = TimeSpan.FromMilliseconds(v),
            true);
        Subs.CVar(_config, RMCCVars.RMCLagCompensationMarginTiles, v => MarginTiles = v, true);
        Subs.CVar(_config, CVars.NetTickrate, UpdateSubsteps, true);
        Subs.CVar(_config, CVars.TargetMinimumTickrate, UpdateSubsteps, true);
    }

    private void OnSetLastRealTick(RMCSetLastRealTickEvent msg, EntitySessionEventArgs args)
    {
        SetLastRealTick(args.SenderSession.UserId, msg.Tick - 1);
    }

    private void OnLagMove(Entity<RMCLagCompensationComponent> ent, ref MoveEvent args)
    {
        if (!args.NewPosition.EntityId.IsValid()
            || !_timing.IsFirstTimePredicted)
            return;

        ent.Comp.Records.Enqueue((_timing.CurTime, args.NewPosition, args.NewRotation));
    }

    private void OnAfterSolve(ref PhysicsUpdateAfterSolveEvent ev)
    {
        PhysicsCurTime = (_physics.EffectiveCurTime ?? _timing.CurTime) + TimeSpan.FromSeconds(ev.DeltaTime);
        _lastPhysicCurTimeUpdateTick = _timing.CurTick;
    }

    private void UpdateSubsteps(int _)
    {
        // This is just ripped out from SharedPhysicsSystem
        var targetMinTickrate = (float)_config.GetCVar(CVars.TargetMinimumTickrate);
        var serverTickrate = (float)_config.GetCVar(CVars.NetTickrate);
        _substeps = (int)Math.Ceiling(targetMinTickrate / serverTickrate);
        _tickPeriod = 1.0f / serverTickrate;
        _tickSpan = TimeSpan.FromSeconds(_tickPeriod);
        _substepPeriod = _tickPeriod / _substeps;
        _substepSpan = TimeSpan.FromSeconds(_substepPeriod);
    }

    private float AABBDistanceSquared(Box2 a, Box2 b)
    {
        var xDist = Math.Max(a.Left - b.Right, b.Left - a.Right);
        var yDist = Math.Max(a.Bottom - b.Top, b.Bottom - a.Top);

        xDist = Math.Max(0, xDist);
        yDist = Math.Max(0, yDist);

        return xDist * xDist + yDist * yDist;
    }

    public (EntityCoordinates Coordinates, Angle Angle) GetCoordinatesAngle(Entity<TransformComponent?> ent, TimeSpan time)
    {
        if (!Resolve(ent, ref ent.Comp))
            return (EntityCoordinates.Invalid, Angle.Zero);

        if (!TryComp<RMCLagCompensationComponent>(ent, out var lag)
            || lag.Records.Count <= 0)
            return (ent.Comp.Coordinates, ent.Comp.LocalRotation);

        var angle = Angle.Zero;
        var coordinates = EntityCoordinates.Invalid;

        TimeSpan? found = null;
        foreach (var record in lag.Records)
        {
            if (found != null && record.Time > time)
                break;

            coordinates = record.Position;
            angle = record.Angle;
            found = record.Time;
        }

        return (coordinates, angle);
    }

    public virtual (EntityCoordinates Coordinates, Angle Angle) GetCoordinatesAngle(Entity<TransformComponent?> ent,
        ICommonSession? perspectiveSession)
    {
        if (!Resolve(ent, ref ent.Comp))
            return (EntityCoordinates.Invalid, Angle.Zero);

        if (perspectiveSession == null)
            return (ent.Comp.Coordinates, ent.Comp.LocalRotation);

        var offset = _timing.CurTick - GetLastRealTick(perspectiveSession.UserId).Value;
        var offsetTime = offset.Value * _timing.TickPeriod;
        offsetTime += GetCurrentSubstep() * _substepSpan;
        if (offsetTime > BufferTime)
            offsetTime = BufferTime;

        return GetCoordinatesAngle(ent, _timing.CurTime - offsetTime);
    }

    public Angle GetAngle(Entity<TransformComponent?> ent, ICommonSession? perspectiveSession)
    {
        var (_, angle) = GetCoordinatesAngle(ent, perspectiveSession);
        return angle;
    }

    public EntityCoordinates GetCoordinates(Entity<TransformComponent?> ent, ICommonSession? perspectiveSession)
    {
        var (coordinates, _) = GetCoordinatesAngle(ent, perspectiveSession);
        return coordinates;
    }

    public EntityCoordinates GetCoordinates(Entity<TransformComponent?> ent, EntityUid? perspectiveEntity)
    {
        if (!_actorQuery.TryComp(perspectiveEntity, out var actor))
            return GetCoordinates(ent, (ICommonSession?) null);

        return GetCoordinates(ent, actor.PlayerSession);
    }

    public EntityCoordinates GetCoordinates(Entity<TransformComponent?> ent, TimeSpan time)
    {
        return GetCoordinatesAngle(ent, time).Coordinates;
    }

    /// <summary>
    /// Compares the positions of two entities from the perspective of a given session,
    /// where one of the entities is being predicted by the session and NOT rewound.
    /// </summary>
    /// <param name="ent">The non-predicted entity that will have their position rewound.</param>
    /// <param name="predictedEnt">The predicted entity that will not be rewound.</param>
    /// <param name="perspectiveSession">The session we are using for perspective.</param>
    /// <param name="range">In tiles. Margin will be added on the server.</param>
    /// <returns></returns>
    public bool IsWithinMargin(Entity<TransformComponent?> ent,
        Entity<TransformComponent?> predictedEnt,
        ICommonSession? perspectiveSession,
        float range)
    {
        if (!Resolve(predictedEnt, ref predictedEnt.Comp))
            return false;

        var entCoords = GetCoordinates(ent, perspectiveSession);

        if (_net.IsServer)
            range += MarginTiles;

        return _transform.InRange(entCoords, predictedEnt.Comp.Coordinates, range);
    }

    public virtual GameTick GetLastRealTick(NetUserId? session)
    {
        return session == null ? _timing.CurTick : _lastRealTicks.GetValueOrDefault(session.Value, _timing.CurTick);
    }

    public void SetLastRealTick(NetUserId session, GameTick tick)
    {
        if (_net.IsClient)
            return;

        _lastRealTicks[session] = tick;
    }

    public void SendLastRealTick()
    {
        if (_net.IsServer)
            return;

        RaiseNetworkEvent(new RMCSetLastRealTickEvent(GetLastRealTick(null)));
    }

    public bool Collides(Entity<FixturesComponent?> target, Entity<PhysicsComponent?> projectile, MapCoordinates targetCoordinates, TimeSpan offset = default)
    {
        if (!Resolve(target, ref target.Comp, false) ||
            !Resolve(projectile, ref projectile.Comp, false))
        {
            return false;
        }

        // Clamp offset to not predict over one tick away
        if (offset < -_tickSpan)
        {
            offset = -_tickSpan;
        }
        else if (offset > _tickSpan)
        {
            offset = _tickSpan;
        }

        var projectileCoordinates = _transform.GetMapCoordinates(projectile);
        var projectileVelocity = _physics.GetLinearVelocity(projectile, projectile.Comp.LocalCenter);
        var substeppedProjectilePos = projectileCoordinates.Position + projectileVelocity * (float)offset.TotalSeconds;

        var transform = new Transform(targetCoordinates.Position, 0);
        var targetBounds = new Box2(transform.Position, transform.Position);

        foreach (var fixture in target.Comp.Fixtures.Values)
        {
            if ((fixture.CollisionLayer & projectile.Comp.CollisionMask) == 0)
                continue;

            for (var i = 0; i < fixture.Shape.ChildCount; i++)
            {
                var boundy = fixture.Shape.ComputeAABB(transform, i);
                targetBounds = targetBounds.Union(boundy);
            }
        }

        var projectileTransform = new Transform(substeppedProjectilePos, 0);
        var projectileBounds = new Box2(projectileTransform.Position, projectileTransform.Position);

        if (_fixturesQuery.TryComp(projectile, out var projFixtureComp))
        {
            foreach (var fixture in projFixtureComp.Fixtures.Values)
            {
                // TODO RMC14 maybe be more selective on which fixtures to include?
                // Don't think it's a problem right now though.
                for (var i = 0; i < fixture.Shape.ChildCount; i++)
                {
                    var boundy = fixture.Shape.ComputeAABB(projectileTransform, i);
                    projectileBounds = projectileBounds.Union(boundy);
                }
            }
        }

        if (_logPrediction)
        {
            Log.Debug($"""
                Lag comp collide data:
                  Pre-Substep
                    Proj Coords:  {projectileCoordinates}
                  CurTime:        {PhysicsCurTime.TotalSeconds:F3}
                  Offset:         {offset.TotalSeconds:F3}
                  Projectile Pos: {substeppedProjectilePos}
                  Target Pos:     {targetCoordinates.Position}
                  Proj AABB:      {projectileBounds.BottomLeft}
                                  {projectileBounds.TopRight}
                  Target AABB:    {targetBounds.BottomLeft}
                                  {targetBounds.TopRight}
                  AABB Intersect? {targetBounds.Intersects(projectileBounds)}
                  AABB Distance:  {Math.Sqrt(AABBDistanceSquared(targetBounds, projectileBounds))}
                """);
        }

        if (targetBounds.Intersects(projectileBounds))
        {
            return true;
        }

        if (AABBDistanceSquared(targetBounds, projectileBounds) <= MarginTiles * MarginTiles)
        {
            return true;
        }

        return false;
    }

    public bool Collides(Entity<FixturesComponent?> ent, Entity<PhysicsComponent?> predictedEnt, ICommonSession? perspectiveSession, TimeSpan offset)
    {
        var coordinates = _transform.ToMapCoordinates(GetCoordinates(ent.Owner, perspectiveSession));
        return Collides(ent, predictedEnt, coordinates, offset);
    }

    public bool Collides(Entity<FixturesComponent?> ent, Entity<PhysicsComponent?> predictedEnt, EntityUid? perspectiveEntity, TimeSpan offset)
    {
        var coordinates = _transform.ToMapCoordinates(GetCoordinates(ent.Owner, perspectiveEntity));
        return Collides(ent, predictedEnt, coordinates, offset);
    }

    public bool Collides(Entity<FixturesComponent?> ent, Entity<PhysicsComponent?> predictedEnt, TimeSpan entTime, TimeSpan predictedEntoffset = default)
    {
        var coordinates = _transform.ToMapCoordinates(GetCoordinatesAngle(ent.Owner, entTime).Coordinates);
        return Collides(ent, predictedEnt, coordinates, predictedEntoffset);
    }

    /// <summary>
    /// Returns the current substep the physics system is in.
    /// If physics isn't running, returns null.
    /// </summary>
    /// <returns></returns>
    public int? GetPhysicsSubstep()
    {
        if (_physics.EffectiveCurTime is not { } physicsTime)
            return null;

        var diff = physicsTime - _timing.CurTime;
        return (int)Math.Round(diff.TotalSeconds / _substepPeriod);
    }

    public int GetSubsteps()
    {
        return _substeps;
    }

    /// <summary>
    /// Returns how many substeps into the current tick the physics system is in.
    /// If not inside physics, returns 0.
    /// </summary>
    /// <returns>0 if physics isn't running. Current physics substep if it is.</returns>
    public int GetCurrentSubstep()
    {
        var substep = GetPhysicsSubstep();

        if (!substep.HasValue)
            substep = 0; // not in a physics substep

        return substep.Value;
    }

    public TimeSpan GetCurrentPhysicsTime()
    {
        return _timing.CurTime + GetCurrentSubstep() * _substepSpan;
    }

    /// <summary>
    /// Passes every stored event that is predicted to occur now or in the past
    /// into a handler for handling, and then removes it from the list.
    /// `handler` MUST NOT modify storage in any way. (Do not add events to the
    /// storage inside the handler.)
    /// </summary>
    public void ProcessEvents<T>(PredictedEventStorage<T> storage,
        Action<EntityUid, T, ICommonSession?> handler,
        EntityUid? source = null)
    {
        if (storage.Iterating)
        {
            Log.Error("Tried processing event messages while they are already being processed.");
            DebugTools.Assert(!storage.Iterating);
            return;
        }
        storage.Iterating = true;

        var physCurTime = PhysicsCurTime;
        var eventsSpan = CollectionsMarshal.AsSpan(storage.EarlyEvents);

        // Iterate over the events, deleting items as they are handled, while preserving item order.
        int writeIndex = 0;
        for (var readIndex = 0; readIndex < eventsSpan.Length; ++readIndex)
        {
            ref var item = ref eventsSpan[readIndex];

            if (item.PredictedTime <= physCurTime
                && (source == null || source == item.Source))
            {
                handler(item.Source, item.Event, item.Session);
                continue;
            }

            if (writeIndex != readIndex)
                eventsSpan[writeIndex] = item;

            ++writeIndex;
        }
        // finally done iterating and moving items, remove excess items from the list
        storage.EarlyEvents.RemoveRange(writeIndex, eventsSpan.Length - writeIndex);
        storage.Iterating = false;
    }

    /// <summary>
    /// Passes every stored event that is predicted to occur now or in the past
    /// into a handler for handling. Does NOT remove events from the list.
    /// `handler` MUST NOT modify storage in any way. (Do not add events to the
    /// storage inside the handler.)
    /// </summary>
    public void ProcessEventsWithoutRemoval<T>(PredictedEventStorage<T> storage,
        Action<EntityUid, T, ICommonSession?> handler,
        EntityUid? source = null)
    {
        if (storage.Iterating)
        {
            Log.Error("Tried processing event messages while they are already being processed.");
            DebugTools.Assert(!storage.Iterating);
            return;
        }
        storage.Iterating = true;

        var physCurTime = PhysicsCurTime;
        var eventsSpan = CollectionsMarshal.AsSpan(storage.EarlyEvents);

        for (var readIndex = 0; readIndex < eventsSpan.Length; ++readIndex)
        {
            ref var item = ref eventsSpan[readIndex];

            if (item.PredictedTime <= physCurTime
                && (source == null || source == item.Source))
            {
                handler(item.Source, item.Event, item.Session);
            }
        }
        storage.Iterating = false;
    }

    public override void Update(float frameTime)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        var curTime = _timing.CurTime;
        var earliestTime = curTime - BufferTime;

        if (_net.IsClient)
            earliestTime -= _tickSpan * (_timing.CurTick.Value - GetLastRealTick(null).Value);

        var query = AllEntityQuery<RMCLagCompensationComponent>();

        while (query.MoveNext(out var comp))
        {
            while (comp.Records.TryPeek(out var pos))
            {
                if (pos.Time < earliestTime)
                {
                    comp.Records.Dequeue();
                    continue;
                }

                break;
            }
        }
    }
}

public struct PredictedEventStorage<T>()
{
    public bool Iterating = false; // EarlyEvents must not be modified while we iterate over it. Can't process or add items while true.
    public List<(EntityUid Source, TimeSpan PredictedTime, T Event, ICommonSession? Session)> EarlyEvents = [];

    public void Add(EntityUid source, TimeSpan predictedTime, T ev, ICommonSession? session)
    {
        DebugTools.Assert(!Iterating);
        if (Iterating)
            return;

        EarlyEvents.Add((source, predictedTime, ev, session));
    }
}
