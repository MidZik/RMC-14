using Robust.Shared.Map;

namespace Content.Shared._RMC.Movement;

[RegisterComponent]
public sealed partial class RMCLagCompensationComponent : Component
{
    [ViewVariables]
    public readonly Queue<(TimeSpan Time, EntityCoordinates Position, Angle Angle)> Records = new();
}
