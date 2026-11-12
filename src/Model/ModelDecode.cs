using System.Buffers.Binary;
using System.Numerics;

namespace SculptPlus.Model;

/// <summary> One mesh of a model as plain arrays: positions, normals, texture coordinates, triangles and skinning. </summary>
public sealed class DecodedMesh
{
    public float[] Positions = Array.Empty<float>(); // x, y, z
    public float[] Normals = Array.Empty<float>();   // x, y, z
    public float[] Uvs = Array.Empty<float>();       // u, v
    public ushort[] Indices = Array.Empty<ushort>();
    public int MaterialIndex;

    /// <summary> Skinning: the mesh's bones by name, and per vertex k bone slots (index into BoneNames) with weights. </summary>
    public string[] BoneNames = Array.Empty<string>();
    public byte[] BoneIndices = Array.Empty<byte>();
    public float[] Weights = Array.Empty<float>();
    public int VertexCount => Positions.Length / 3;
}

/// <summary>
/// Reads the meshes of a game model (.mdl, highest detail) into plain arrays. The model is in its rest pose (the way it
/// is stored), which is how every piece of a character fits together without the skeleton. Pure code, for the tests.
/// </summary>
public static class ModelDecode
{
    // vertex element types and usages of the .mdl format
    private const byte Single1 = 0, Single2 = 1, Single3 = 2, Single4 = 3, UByte4 = 5, UByte4N = 8, Half2 = 13, Half4 = 14, UShort2 = 16, UShort4 = 17;
    private const byte UsagePosition = 0, UsageWeights = 1, UsageBones = 2, UsageNormal = 3, UsageUv = 4;

    /// <summary>
    /// The meshes of the highest detail. Only the parts the game would show: a part with attributes (atr_…) is kept when all of
    /// them are in <paramref name="enabledAttributes"/>, and the shape keys in <paramref name="enabledShapes"/> are applied
    /// (bit i = the model's attribute / shape i, like the game counts them). The defaults keep everything and no shapes.
    /// </summary>
    public static List<DecodedMesh> DecodeLod0(MdlFile m, uint enabledAttributes = uint.MaxValue, uint enabledShapes = 0)
    {
        var list = new List<DecodedMesh>();
        if (m.LodCount == 0)
            return list;

        var lod   = m.Lods[0];
        var data  = m.Data;
        for (var mi = lod.MeshIndex; mi < lod.MeshIndex + lod.MeshCount && mi < m.Meshes.Count; ++mi)
        {
            var mesh = m.Meshes[mi];
            if (mesh.VertexCount == 0 || mesh.IndexCount < 3 || mi >= m.VertexDeclarations.Count)
                continue;

            var decl = m.VertexDeclarations[mi];
            var pos  = decl.FirstOrDefault(e => e.Usage == UsagePosition);
            if (decl.All(e => e.Usage != UsagePosition))
                continue;
            var nrm = decl.FirstOrDefault(e => e.Usage == UsageNormal);
            var hasNormal = decl.Any(e => e.Usage == UsageNormal);
            var uv  = decl.FirstOrDefault(e => e.Usage == UsageUv);
            var hasUv = decl.Any(e => e.Usage == UsageUv);

            var n   = mesh.VertexCount;
            var d   = new DecodedMesh
            {
                Positions     = new float[n * 3],
                Normals       = new float[n * 3],
                Uvs           = new float[n * 2],
                MaterialIndex = mesh.MaterialIndex,
            };

            ReadSkinning(m, mesh, decl, n, d);
            for (var v = 0; v < n; ++v)
            {
                var p = Read(data, Start(m, mesh, pos, v), pos.Type);
                d.Positions[v * 3]     = p.X;
                d.Positions[v * 3 + 1] = p.Y;
                d.Positions[v * 3 + 2] = p.Z;
                if (hasNormal)
                {
                    var q = Read(data, Start(m, mesh, nrm, v), nrm.Type);
                    var len = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
                    if (len > 1e-6f)
                    {
                        d.Normals[v * 3]     = q.X / len;
                        d.Normals[v * 3 + 1] = q.Y / len;
                        d.Normals[v * 3 + 2] = q.Z / len;
                    }
                    else
                    {
                        d.Normals[v * 3 + 1] = 1;
                    }
                }
                else
                {
                    d.Normals[v * 3 + 1] = 1;
                }

                if (hasUv)
                {
                    var t = Read(data, Start(m, mesh, uv, v), uv.Type);
                    d.Uvs[v * 2]     = float.IsFinite(t.X) ? t.X : 0;
                    d.Uvs[v * 2 + 1] = float.IsFinite(t.Y) ? t.Y : 0;
                }
            }

            var indexStart = (long)m.IndexOffset[0] + mesh.StartIndex * 2L;
            var count      = (int)mesh.IndexCount;
            if (indexStart < 0 || indexStart + count * 2L > data.Length)
                continue;
            var all = new ushort[count];
            for (var i = 0; i < count; ++i)
            {
                var index = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)(indexStart + i * 2)));
                all[i] = index < n ? index : (ushort)0;
            }

            ApplyShapes(m, mesh, all, n, enabledShapes);
            d.Indices = VisibleTriangles(m, mesh, all, enabledAttributes);
            if (d.Indices.Length == 0)
                continue;

            if (d.Positions.All(float.IsFinite))
                list.Add(d);
        }

        return list;
    }

    /// <summary> A shape key swaps some corners of the mesh's triangles for other vertices (stored after the normal ones). </summary>
    private static void ApplyShapes(MdlFile m, MdlMesh mesh, ushort[] indices, int vertexCount, uint enabled)
    {
        if (enabled == 0)
            return;
        for (var s = 0; s < m.Shapes.Count && s < 32; ++s)
        {
            if ((enabled >> s & 1) == 0)
                continue;
            var shape = m.Shapes[s];
            for (var k = 0; k < shape.ShapeMeshCount[0]; ++k)
            {
                var at = shape.ShapeMeshStart[0] + k;
                if (at >= m.ShapeMeshes.Count)
                    break;
                var sm = m.ShapeMeshes[at];
                if (sm.MeshIndexOffset != mesh.StartIndex)
                    continue;
                for (var v = 0; v < sm.ShapeValueCount; ++v)
                {
                    var vi = (int)(sm.ShapeValueOffset + v);
                    if (vi >= m.ShapeValues.Count)
                        break;
                    var value = m.ShapeValues[vi];
                    if (value.BaseIndicesIndex < indices.Length && value.ReplacingVertexIndex < vertexCount)
                        indices[value.BaseIndicesIndex] = value.ReplacingVertexIndex;
                }
            }
        }
    }

    /// <summary> The triangles of the mesh's parts that are shown (a mesh without parts is shown whole). </summary>
    private static ushort[] VisibleTriangles(MdlFile m, MdlMesh mesh, ushort[] indices, uint enabled)
    {
        if (mesh.SubMeshCount == 0 || enabled == uint.MaxValue)
            return indices;

        var kept = new List<ushort>(indices.Length);
        for (var k = 0; k < mesh.SubMeshCount; ++k)
        {
            var at = mesh.SubMeshIndex + k;
            if (at >= m.SubMeshes.Count)
                break;
            var sub = m.SubMeshes[at];
            if ((sub.AttributeIndexMask & ~enabled) != 0)
                continue;
            var start = (long)sub.IndexOffset - mesh.StartIndex;
            var end   = Math.Min(start + sub.IndexCount, indices.Length);
            if (start < 0)
                continue;
            for (var i = start; i + 2 < end; i += 3)
            {
                kept.Add(indices[i]);
                kept.Add(indices[i + 1]);
                kept.Add(indices[i + 2]);
            }
        }

        return kept.ToArray();
    }

    /// <summary> Bone weights (4, or 8 in newer models stored as 8 bytes), for reshaping a model to another race. </summary>
    private static void ReadSkinning(MdlFile m, MdlMesh mesh, List<VertexElement> decl, int n, DecodedMesh d)
    {
        if (!decl.Any(e => e.Usage == UsageWeights) || !decl.Any(e => e.Usage == UsageBones) || mesh.BoneTableIndex >= m.BoneTables.Count)
            return;
        var we = decl.First(e => e.Usage == UsageWeights);
        var be = decl.First(e => e.Usage == UsageBones);
        if (we.Type is not (UByte4N or UByte4 or UShort4) || be.Type is not (UByte4 or UByte4N or UShort4))
            return;
        int Width(byte type) => type is UShort4 ? 8 : 4;
        var k = Math.Min(Width(we.Type), Width(be.Type));
        var table = m.BoneTables[mesh.BoneTableIndex];
        d.BoneNames   = table.Select(b => b < m.Bones.Count ? m.Bones[b] : string.Empty).ToArray();
        d.BoneIndices = new byte[n * k];
        d.Weights     = new float[n * k];
        var data = m.Data;
        for (var v = 0; v < n; ++v)
        {
            var wAt = Start(m, mesh, we, v);
            var bAt = Start(m, mesh, be, v);
            if (wAt < 0 || bAt < 0 || wAt + k > data.Length || bAt + k > data.Length)
                continue;
            for (var j = 0; j < k; ++j)
            {
                d.Weights[v * k + j]     = data[wAt + j] / 255f;
                d.BoneIndices[v * k + j] = data[bAt + j];
            }
        }
    }

    private static long Start(MdlFile m, MdlMesh mesh, VertexElement e, int vertex)
        => m.VertexOffset[0] + mesh.VertexBufferOffset[e.Stream] + (long)vertex * mesh.VertexBufferStride[e.Stream] + e.Offset;

    private static Vector4 Read(byte[] b, long at, byte type)
    {
        if (at < 0 || at + Size(type) > b.Length)
            return Vector4.Zero;
        var s = b.AsSpan((int)at);
        return type switch
        {
            Single1 => new Vector4(F(s, 0), 0, 0, 0),
            Single2 => new Vector4(F(s, 0), F(s, 4), 0, 0),
            Single3 => new Vector4(F(s, 0), F(s, 4), F(s, 8), 0),
            Single4 => new Vector4(F(s, 0), F(s, 4), F(s, 8), F(s, 12)),
            Half2   => new Vector4(H(s, 0), H(s, 2), 0, 0),
            Half4   => new Vector4(H(s, 0), H(s, 2), H(s, 4), H(s, 6)),
            UByte4  => new Vector4(s[0], s[1], s[2], s[3]),
            UByte4N => new Vector4(s[0] / 255f, s[1] / 255f, s[2] / 255f, s[3] / 255f),
            UShort2 => new Vector4(BinaryPrimitives.ReadUInt16LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), 0, 0),
            UShort4 => new Vector4(BinaryPrimitives.ReadUInt16LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), BinaryPrimitives.ReadUInt16LittleEndian(s[6..])),
            _ => Vector4.Zero,
        };

        static float F(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleLittleEndian(s[o..]);
        static float H(ReadOnlySpan<byte> s, int o) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(s[o..]));
    }

    private static int Size(byte type)
        => type switch
        {
            Single1 => 4, Single2 => 8, Single3 => 12, Single4 => 16, Half2 => 4, Half4 => 8, UShort2 => 4, UShort4 => 8, _ => 4,
        };
}
