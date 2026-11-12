using System.Numerics;

namespace SculptPlus;

/// <summary> A bone as it stands on a character this frame. </summary>
public readonly record struct LiveBone(string Name, Vector3 World, Vector3 Model, Quaternion ModelRotation)
{
    /// <summary> The bone's size right now (1 unless something, like the sculpt, resized it). </summary>
    public Vector3 ModelScale { get; init; } = Vector3.One;
}
