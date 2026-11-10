using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using CsCharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using CsSceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace SculptPlus;

/// <summary> What is known about a bone by name: what to call it, which part of the body it belongs to, its twin. </summary>
public sealed record BoneInfo(string Name, string Display, string Family, bool Standard, string? Mirror);

/// <summary> A bone as it stands on a character this frame. </summary>
public readonly record struct LiveBone(string Name, Vector3 World, Vector3 Model, Quaternion ModelRotation);

/// <summary>
/// The bones of a character: the table of names (from Customize+, so the words match what you see there) and where each
/// one is right now, read from the game's skeleton.
/// </summary>
public static unsafe class SculptBones
{
    private static Dictionary<string, BoneInfo>? _table;

    public static IReadOnlyDictionary<string, BoneInfo> Table
    {
        get
        {
            if (_table is not null)
                return _table;
            var table = new Dictionary<string, BoneInfo>(StringComparer.Ordinal);
            using var stream = typeof(SculptBones).Assembly.GetManifestResourceStream("SculptPlus.bones.csv");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (line.StartsWith('#'))
                        continue;
                    var p = line.Split(',');
                    if (p.Length < 7)
                        continue;
                    table[p[0]] = new BoneInfo(p[0], p[1], p[2], p[3] == "TRUE", p[6].Length > 0 ? p[6] : null);
                }
            }

            return _table = table;
        }
    }

    public static BoneInfo? Info(string bone)
        => Table.GetValueOrDefault(bone);

    /// <summary> The twin on the other side (from the table, else by the _l/_r ending). </summary>
    public static string? MirrorOf(string bone)
    {
        if (Info(bone)?.Mirror is { } m)
            return m;
        if (bone.EndsWith("_l", StringComparison.Ordinal))
            return bone[..^2] + "_r";
        if (bone.EndsWith("_r", StringComparison.Ordinal))
            return bone[..^2] + "_l";
        return null;
    }

    /// <summary>
    /// Sets how fast a character's animations play (0: stands frozen, 1: normal). Only the picture on your screen: the
    /// game and other players still see you move normally.
    /// </summary>
    public static void SetAnimationSpeed(nint gameObject, float speed)
    {
        if (gameObject == nint.Zero || !((CsGameObject*)gameObject)->IsCharacter())
            return;
        ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)gameObject)->Timeline.OverallSpeed = speed;
    }

    /// <summary> Every bone of a character with its place in the world and in the model. Draw or framework thread. </summary>
    public static Dictionary<string, LiveBone> Read(nint gameObject, out Quaternion rootRotation, out Vector3 rootScale)
    {
        rootRotation = Quaternion.Identity;
        rootScale    = Vector3.One;
        var result = new Dictionary<string, LiveBone>(StringComparer.Ordinal);
        if (gameObject == nint.Zero)
            return result;
        var draw = ((CsGameObject*)gameObject)->DrawObject;
        if (draw == null || ((CsSceneObject*)draw)->GetObjectType() != ObjectType.CharacterBase)
            return result;
        var skeleton = ((CsCharacterBase*)draw)->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount is <= 0 or > 32)
            return result;

        var position = (Vector3)skeleton->Transform.Position;
        rootRotation = (Quaternion)skeleton->Transform.Rotation;
        rootScale    = (Vector3)skeleton->Transform.Scale;
        for (var i = 0; i < skeleton->PartialSkeletonCount; ++i)
        {
            var poses = skeleton->PartialSkeletons[i].HavokPoses;
            if (poses.Length == 0 || poses[0] == 0)
                continue;
            var pose  = (hkaPose*)poses[0];
            var havok = pose->Skeleton;
            if (havok == null || havok->Bones.Data == null || pose->ModelPose.Data == null)
                continue;
            var count = Math.Min(havok->Bones.Length, pose->ModelPose.Length);
            if (count <= 0 || count > 2048)
                continue;
            for (var b = 0; b < count; ++b)
            {
                var name = havok->Bones.Data[b].Name.String;
                if (string.IsNullOrEmpty(name) || result.ContainsKey(name))
                    continue;
                var t     = pose->ModelPose.Data[b];
                var model = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
                var rot   = new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W);
                if (float.IsNaN(model.X) || float.IsNaN(rot.W))
                    continue;
                var world = position + Vector3.Transform(model * rootScale, rootRotation);
                result[name] = new LiveBone(name, world, model, rot);
            }
        }

        return result;
    }
}
