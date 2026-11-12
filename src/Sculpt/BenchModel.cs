using System.Numerics;
using SculptPlus.Model;

namespace SculptPlus;

/// <summary> One mesh on the workbench: how it is stored, who moves each point, and where the points are right now. </summary>
public sealed class BenchMesh
{
    public string Kind = "face";                       // "face" or "hair"
    public Vector3[] Rest = Array.Empty<Vector3>();    // as stored
    public Vector3[] Now = Array.Empty<Vector3>();     // with the pose and the sculpt, head upright
    public Vector3[] Normals = Array.Empty<Vector3>(); // of Now, smooth
    public int[] Triangles = Array.Empty<int>();       // three corners each, wound so the stored normals point out
    public string[] BoneNames = Array.Empty<string>();
    public byte[] BoneIndices = Array.Empty<byte>();   // Slots per point
    public float[] Weights = Array.Empty<float>();
    public int Slots;
    public Matrix4x4[] Moves = Array.Empty<Matrix4x4>(); // per bone of the mesh, this frame
    public bool[] Known = Array.Empty<bool>();           // the bone is one of the character's (and can be sculpted)
}

/// <summary> The workbench's sums, free of the game and of the screen (so they can be tested). </summary>
public static class Bench
{
    public static BenchMesh? Convert(DecodedMesh mesh, string kind)
    {
        var n = mesh.VertexCount;
        if (n == 0 || mesh.Indices.Length < 3 || mesh.BoneNames.Length == 0 || mesh.Weights.Length == 0)
            return null;
        var bench = new BenchMesh
        {
            Kind        = kind,
            Rest        = new Vector3[n],
            Now         = new Vector3[n],
            Normals     = new Vector3[n],
            BoneNames   = mesh.BoneNames,
            BoneIndices = mesh.BoneIndices,
            Weights     = mesh.Weights,
            Slots       = mesh.Weights.Length / n,
            Moves       = new Matrix4x4[mesh.BoneNames.Length],
            Known       = new bool[mesh.BoneNames.Length],
        };
        for (var v = 0; v < n; ++v)
            bench.Rest[v] = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);

        // Wind every triangle so that it faces the way the model's own normals point: which side is "front" is then
        // known without knowing how the game stores it.
        var agree = 0;
        var count = mesh.Indices.Length / 3;
        for (var t = 0; t < count; ++t)
        {
            int a = mesh.Indices[t * 3], b = mesh.Indices[t * 3 + 1], c = mesh.Indices[t * 3 + 2];
            var face   = Vector3.Cross(bench.Rest[b] - bench.Rest[a], bench.Rest[c] - bench.Rest[a]);
            var stored = new Vector3(mesh.Normals[a * 3] + mesh.Normals[b * 3] + mesh.Normals[c * 3],
                mesh.Normals[a * 3 + 1] + mesh.Normals[b * 3 + 1] + mesh.Normals[c * 3 + 1],
                mesh.Normals[a * 3 + 2] + mesh.Normals[b * 3 + 2] + mesh.Normals[c * 3 + 2]);
            agree += Vector3.Dot(face, stored) >= 0 ? 1 : -1;
        }

        var flip = agree < 0;
        bench.Triangles = new int[count * 3];
        for (var t = 0; t < count; ++t)
        {
            bench.Triangles[t * 3]     = mesh.Indices[t * 3];
            bench.Triangles[t * 3 + 1] = mesh.Indices[t * 3 + (flip ? 2 : 1)];
            bench.Triangles[t * 3 + 2] = mesh.Indices[t * 3 + (flip ? 1 : 2)];
        }

        Array.Copy(bench.Rest, bench.Now, n);
        return bench;
    }

    /// <summary>
    /// For every bone, a spot on the skin it moves (as the model is stored): the middle of its points, strong holds
    /// counting most, moved onto the nearest point it holds firmly so that it lies on the surface. A bone's own place
    /// says little about what it moves (the eyelids all turn around the middle of the eye); this spot does.
    /// </summary>
    public static Dictionary<string, Vector3> SkinSpots(List<BenchMesh> meshes)
    {
        var sum    = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var weight = new Dictionary<string, float>(StringComparer.Ordinal);
        var most   = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var mesh in meshes.Where(m => m.Kind == "face"))
            for (var v = 0; v < mesh.Rest.Length; ++v)
                for (var k = 0; k < mesh.Slots; ++k)
                {
                    var w = mesh.Weights[v * mesh.Slots + k];
                    var b = mesh.BoneIndices[v * mesh.Slots + k];
                    if (w <= 0 || b >= mesh.BoneNames.Length)
                        continue;
                    var name = mesh.BoneNames[b];
                    sum[name]    = sum.GetValueOrDefault(name) + mesh.Rest[v] * (w * w);
                    weight[name] = weight.GetValueOrDefault(name) + w * w;
                    most[name]   = MathF.Max(most.GetValueOrDefault(name), w);
                }

        var middle = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var (name, total) in weight)
            if (total > 0.5f)
                middle[name] = sum[name] / total;

        // onto the surface: the nearest point the bone holds firmly
        var result  = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var nearest = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var mesh in meshes.Where(m => m.Kind == "face"))
            for (var v = 0; v < mesh.Rest.Length; ++v)
                for (var k = 0; k < mesh.Slots; ++k)
                {
                    var w = mesh.Weights[v * mesh.Slots + k];
                    var b = mesh.BoneIndices[v * mesh.Slots + k];
                    if (w <= 0 || b >= mesh.BoneNames.Length)
                        continue;
                    var name = mesh.BoneNames[b];
                    if (!middle.TryGetValue(name, out var m) || w < most[name] * 0.6f)
                        continue;
                    var d = Vector3.DistanceSquared(mesh.Rest[v], m);
                    if (d < nearest.GetValueOrDefault(name, float.MaxValue))
                    {
                        nearest[name] = d;
                        result[name]  = mesh.Rest[v];
                    }
                }

        return result;
    }

    /// <summary> Where a spot of the stored model is on the character right now: it goes with its bone (place, turn and size). </summary>
    public static Vector3 Carry(Vector3 spot, (Vector3 Position, Quaternion Rotation) rest, LiveBone live)
        => live.Model + Vector3.Transform(Vector3.Transform(spot - rest.Position, Quaternion.Inverse(rest.Rotation)) * live.ModelScale, live.ModelRotation);

    /// <summary>
    /// How well a rest pose belongs to the meshes: for each face bone, how far it is from the middle of the points it
    /// moves (a bone sits in or near what it moves). The middle value over all bones, in metres.
    /// </summary>
    public static float FitOf(List<BenchMesh> meshes, Dictionary<string, (Vector3 Position, Quaternion Rotation)> rest)
    {
        var distances = new List<float>();
        foreach (var mesh in meshes.Where(m => m.Kind == "face"))
        {
            var sum    = new Vector3[mesh.BoneNames.Length];
            var weight = new float[mesh.BoneNames.Length];
            for (var v = 0; v < mesh.Rest.Length; ++v)
                for (var k = 0; k < mesh.Slots; ++k)
                {
                    var w = mesh.Weights[v * mesh.Slots + k];
                    var b = mesh.BoneIndices[v * mesh.Slots + k];
                    if (w <= 0 || b >= sum.Length)
                        continue;
                    sum[b]    += mesh.Rest[v] * w;
                    weight[b] += w;
                }

            for (var b = 0; b < sum.Length; ++b)
                if (weight[b] > 1f && mesh.BoneNames[b].StartsWith("j_f_", StringComparison.Ordinal) && rest.TryGetValue(mesh.BoneNames[b], out var at))
                    distances.Add(Vector3.Distance(sum[b] / weight[b], at.Position));
        }

        if (distances.Count == 0)
            return float.MaxValue;
        distances.Sort();
        return distances[distances.Count / 2];
    }

    /// <summary>
    /// Every frame: moves the points the way the game does. A point goes with each of its bones from where the bone rests
    /// to where it is now (place, turn and size, as read from the game after Customize+), and the whole is turned back
    /// so that the head stands as it rests.
    /// </summary>
    public static void Pose(List<BenchMesh> meshes, Dictionary<string, (Vector3 Position, Quaternion Rotation)> _rest, Dictionary<string, LiveBone> bones, bool still = false)
    {
        // what the head itself does (nodding, turning with the body) is taken out again
        var upright = Matrix4x4.Identity;
        if (bones.TryGetValue("j_kao", out var head) && _rest.TryGetValue("j_kao", out var headRest))
            upright = Matrix4x4.CreateTranslation(-head.Model) * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(head.ModelRotation))
              * Matrix4x4.CreateFromQuaternion(headRest.Rotation) * Matrix4x4.CreateTranslation(headRest.Position);

        foreach (var mesh in meshes)
        {
            for (var b = 0; b < mesh.BoneNames.Length; ++b)
            {
                var name = mesh.BoneNames[b];
                if (still)
                {
                    // shown as stored; the bone still counts as the character's when it has it
                    mesh.Known[b] = bones.ContainsKey(name);
                    mesh.Moves[b] = Matrix4x4.Identity;
                }
                else if (bones.TryGetValue(name, out var live) && _rest.TryGetValue(name, out var rest))
                {
                    mesh.Known[b] = true;
                    mesh.Moves[b] = Matrix4x4.CreateTranslation(-rest.Position) * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(rest.Rotation))
                      * Matrix4x4.CreateScale(live.ModelScale) * Matrix4x4.CreateFromQuaternion(live.ModelRotation) * Matrix4x4.CreateTranslation(live.Model)
                      * upright;
                }
                else
                {
                    // a bone the character does not have: the point stays where it rests
                    mesh.Known[b] = false;
                    mesh.Moves[b] = Matrix4x4.Identity;
                }
            }

            var slots = mesh.Slots;
            for (var v = 0; v < mesh.Rest.Length; ++v)
            {
                var p     = mesh.Rest[v];
                var sum   = Vector3.Zero;
                var total = 0f;
                for (var k = 0; k < slots; ++k)
                {
                    var w = mesh.Weights[v * slots + k];
                    if (w <= 0)
                        continue;
                    var b = mesh.BoneIndices[v * slots + k];
                    if (b >= mesh.Moves.Length)
                        continue;
                    sum   += Vector3.Transform(p, mesh.Moves[b]) * w;
                    total += w;
                }

                mesh.Now[v] = total > 1e-4f ? sum / total : p;
            }

            // smooth normals of the moved mesh
            Array.Clear(mesh.Normals);
            var tris = mesh.Triangles;
            for (var t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var n = Vector3.Cross(mesh.Now[b] - mesh.Now[a], mesh.Now[c] - mesh.Now[a]);
                mesh.Normals[a] += n;
                mesh.Normals[b] += n;
                mesh.Normals[c] += n;
            }

            for (var v = 0; v < mesh.Normals.Length; ++v)
            {
                var n = mesh.Normals[v];
                mesh.Normals[v] = n.LengthSquared() > 1e-16f ? Vector3.Normalize(n) : Vector3.UnitZ;
            }
        }
    }

    /// <summary> Turns a direction on the workbench into the character's own (the head there is upright, on the character it may not be). </summary>
    public static Vector3 ToCharacter(Vector3 onBench, Dictionary<string, (Vector3 Position, Quaternion Rotation)> _rest, Dictionary<string, LiveBone> bones)
    {
        if (bones.TryGetValue("j_kao", out var head) && _rest.TryGetValue("j_kao", out var headRest))
            return Vector3.Transform(Vector3.Transform(onBench, Quaternion.Inverse(headRest.Rotation)), head.ModelRotation);
        return onBench;
    }
}

/// <summary>
/// The workbench's picture, as numbers: where every point lands on screen for a view, what colour it has, and the
/// triangles that face you, far ones first (painted in that order, nearer ones cover what lies behind). No depth buffer
/// is needed for a head: it has no parts that pass through each other.
/// </summary>
public sealed class BenchView
{
    public Vector2[] Screen = Array.Empty<Vector2>();
    public float[] Depth = Array.Empty<float>();
    public uint[] Colour = Array.Empty<uint>();
    public int[] Tri = Array.Empty<int>();   // three points each, numbered across all meshes
    public int[] Order = Array.Empty<int>(); // triangles, far to near
    public int TriCount;

    private float[] _key = Array.Empty<float>();
    private int[] _offsets = Array.Empty<int>();
    private List<BenchMesh> _meshes = new();

    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.4f, 0.55f, 0.75f));

    /// <summary>
    /// <paramref name="view"/> turns the head (x right, y up, z towards you); <paramref name="zoom"/> is pixels per
    /// metre. The area the lit part moves is coloured strongly, the held part's faintly, changed parts get a shimmer.
    /// </summary>
    public void Project(List<BenchMesh> meshes, Vector3 centre, Quaternion view, Vector2 origin, float zoom, string? lit, string? held,
        Func<string, bool> isChanged, Vector3 accent, Vector3 good)
    {
        _meshes = meshes;
        var points  = meshes.Sum(m => m.Now.Length);
        var corners = meshes.Sum(m => m.Triangles.Length);
        if (Screen.Length < points)
        {
            Screen = new Vector2[points];
            Depth  = new float[points];
            Colour = new uint[points];
        }

        if (Tri.Length < corners)
        {
            Tri   = new int[corners];
            _key  = new float[corners / 3];
            Order = new int[corners / 3];
        }

        if (_offsets.Length != meshes.Count)
            _offsets = new int[meshes.Count];

        var at = 0;
        TriCount = 0;
        for (var mi = 0; mi < meshes.Count; ++mi)
        {
            var mesh = meshes[mi];
            _offsets[mi] = at;
            var clay     = mesh.Kind == "hair" ? new Vector3(0.44f, 0.41f, 0.42f) : new Vector3(0.82f, 0.75f, 0.71f);
            var litSlot  = lit is null ? -1 : Array.IndexOf(mesh.BoneNames, lit);
            var heldSlot = held is null ? -1 : Array.IndexOf(mesh.BoneNames, held);
            var changed  = new bool[mesh.BoneNames.Length];
            var any      = false;
            for (var b = 0; b < changed.Length; ++b)
                if (mesh.Known[b] && isChanged(mesh.BoneNames[b]))
                    changed[b] = any = true;

            var slots = mesh.Slots;
            for (var v = 0; v < mesh.Now.Length; ++v)
            {
                var p = Vector3.Transform(mesh.Now[v] - centre, view);
                Screen[at + v] = origin + new Vector2(p.X, -p.Y) * zoom;
                Depth[at + v]  = p.Z;

                var n      = Vector3.Transform(mesh.Normals[v], view);
                var shade  = 0.26f + 0.74f * MathF.Max(0f, Vector3.Dot(n, Light));
                var colour = clay * shade;
                if (litSlot >= 0 || heldSlot >= 0 || any)
                {
                    float wLit = 0, wHeld = 0, wChanged = 0;
                    for (var k = 0; k < slots; ++k)
                    {
                        var w = mesh.Weights[v * slots + k];
                        if (w <= 0)
                            continue;
                        var b = mesh.BoneIndices[v * slots + k];
                        if (b == litSlot)
                            wLit += w;
                        else if (b == heldSlot)
                            wHeld += w;
                        if (b < changed.Length && changed[b])
                            wChanged += w;
                    }

                    if (wChanged > 0)
                        colour = Vector3.Lerp(colour, good * shade, MathF.Min(1f, wChanged) * 0.30f);
                    if (wHeld > 0)
                        colour = Vector3.Lerp(colour, accent * shade, MathF.Min(1f, wHeld) * 0.35f);
                    if (wLit > 0)
                        colour = Vector3.Lerp(colour, accent * (0.45f + 0.55f * shade), MathF.Min(1f, wLit * 1.15f) * 0.85f);
                }

                Colour[at + v] = 0xFF000000u | (uint)(Math.Clamp(colour.Z, 0, 1) * 255) << 16 | (uint)(Math.Clamp(colour.Y, 0, 1) * 255) << 8 | (uint)(Math.Clamp(colour.X, 0, 1) * 255);
            }

            // A triangle faces you when its corners run anticlockwise as you see it; on a screen that counts y downwards
            // that is a negative turn.
            var tris = mesh.Triangles;
            for (var t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = at + tris[t], b = at + tris[t + 1], c = at + tris[t + 2];
                var turn = (Screen[b].X - Screen[a].X) * (Screen[c].Y - Screen[a].Y) - (Screen[b].Y - Screen[a].Y) * (Screen[c].X - Screen[a].X);
                if (turn >= 0)
                    continue;
                Tri[TriCount * 3]     = a;
                Tri[TriCount * 3 + 1] = b;
                Tri[TriCount * 3 + 2] = c;
                _key[TriCount]        = Depth[a] + Depth[b] + Depth[c];
                Order[TriCount]       = TriCount;
                TriCount++;
            }

            at += mesh.Now.Length;
        }

        Array.Sort(_key, Order, 0, TriCount);
    }

    /// <summary> The point of the model under a place on screen: the nearest corner of the nearest triangle there. </summary>
    public (BenchMesh Mesh, int Vertex)? Pick(Vector2 at)
    {
        for (var i = TriCount - 1; i >= 0; --i)
        {
            var t = Order[i];
            int a = Tri[t * 3], b = Tri[t * 3 + 1], c = Tri[t * 3 + 2];
            if (!Inside(at, Screen[a], Screen[b], Screen[c]))
                continue;
            var corner = a;
            if (Vector2.DistanceSquared(at, Screen[b]) < Vector2.DistanceSquared(at, Screen[corner]))
                corner = b;
            if (Vector2.DistanceSquared(at, Screen[c]) < Vector2.DistanceSquared(at, Screen[corner]))
                corner = c;
            var mi = _meshes.Count - 1;
            while (mi > 0 && _offsets[mi] > corner)
                mi--;
            return (_meshes[mi], corner - _offsets[mi]);
        }

        return null;
    }

    private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
        var d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
        var d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
        var negative = d1 < 0 || d2 < 0 || d3 < 0;
        var positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }
}
