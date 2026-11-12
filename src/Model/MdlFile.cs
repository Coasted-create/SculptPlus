using System.Buffers.Binary;
using System.Text;

namespace SculptPlus.Model;

/// <summary> One vertex element of a vertex declaration. </summary>
public record struct VertexElement(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex);

/// <summary> One level of detail. Data offsets are relative to the start of the vertex / index data after reading. </summary>
public sealed class MdlLod
{
    public ushort MeshIndex;
    public ushort MeshCount;
    public float ModelLodRange;
    public float TextureLodRange;
    public ushort WaterMeshIndex, WaterMeshCount, ShadowMeshIndex, ShadowMeshCount;
    public ushort TerrainShadowMeshIndex, TerrainShadowMeshCount, VerticalFogMeshIndex, VerticalFogMeshCount;
    public uint EdgeGeometrySize, EdgeGeometryDataOffset, PolygonCount, Unknown1;
    public uint VertexBufferSize, IndexBufferSize, VertexDataOffset, IndexDataOffset;

    public MdlLod Clone() => (MdlLod)MemberwiseClone();
}

public sealed class MdlMesh
{
    public ushort VertexCount;
    public ushort Padding;
    public uint IndexCount;
    public ushort MaterialIndex;
    public ushort SubMeshIndex;
    public ushort SubMeshCount;
    public ushort BoneTableIndex;

    /// <summary> First index of this mesh, counted in indices from the start of its LOD's index buffer. </summary>
    public uint StartIndex;

    /// <summary> Byte offsets of the vertex streams, from the start of the LOD's vertex buffer. </summary>
    public uint[] VertexBufferOffset = new uint[3];

    public byte[] VertexBufferStride = new byte[3];

    /// <summary> Raw byte; the low two bits are the stream count. </summary>
    public byte VertexStreamCountRaw;

    public int StreamCount => VertexStreamCountRaw & 3;

    public MdlMesh Clone()
    {
        var m = (MdlMesh)MemberwiseClone();
        m.VertexBufferOffset = (uint[])VertexBufferOffset.Clone();
        m.VertexBufferStride = (byte[])VertexBufferStride.Clone();
        return m;
    }
}

public sealed class MdlSubMesh
{
    /// <summary> First index, counted from the start of the LOD's index buffer (not the mesh). </summary>
    public uint IndexOffset;

    public uint IndexCount;
    public uint AttributeIndexMask;
    public ushort BoneStartIndex;
    public ushort BoneCount;

    public MdlSubMesh Clone() => (MdlSubMesh)MemberwiseClone();
}

public sealed class MdlShape
{
    public string Name = string.Empty;
    public ushort[] ShapeMeshStart = new ushort[3];
    public ushort[] ShapeMeshCount = new ushort[3];
}

/// <summary> A mesh's part of a shape key. MeshIndexOffset is the mesh's StartIndex; the values are relative to the mesh. </summary>
public record struct MdlShapeMesh(uint MeshIndexOffset, uint ShapeValueCount, uint ShapeValueOffset);

/// <summary> Index position (relative to the mesh's first index) that is replaced by another vertex of the mesh. </summary>
public record struct MdlShapeValue(ushort BaseIndicesIndex, ushort ReplacingVertexIndex);

/// <summary>
/// Reader / writer for FFXIV .mdl files (v5 and v6), after Penumbra.GameData's MdlFile and the Python tool used to port and
/// merge the Riptide bracelets. Vertex and index buffers are kept as raw data. Unmodified files are written back byte for byte:
/// the original string table, padding bytes, unused LOD offsets and any trailing runtime data (e.g. neck morphs) are kept.
/// </summary>
public sealed class MdlFile
{
    public const uint V5 = 0x01000005;
    public const uint V6 = 0x01000006;
    public const int HeaderSize = 0x44;
    private const int ElementsPerDeclaration = 17;

    public uint Version = V6;
    public uint[] VertexOffset = new uint[3];
    public uint[] IndexOffset = new uint[3];
    public uint[] VertexBufferSize = new uint[3];
    public uint[] IndexBufferSize = new uint[3];
    public byte LodCount;
    public byte EnableIndexBufferStreaming;
    public byte EnableEdgeGeometry;

    public List<List<VertexElement>> VertexDeclarations = new();

    public float Radius;
    public byte Flags1;
    public byte Flags2;
    public float ModelClipOutDistance;
    public float ShadowClipOutDistance;
    public ushort CullingGridCount;
    public byte Flags3;
    public byte BgChangeMaterialIndex;
    public byte BgCrestChangeMaterialIndex;
    public byte NeckMorphCount;
    public ushort Unknown8;
    public ushort Unknown9;

    public List<byte[]> ElementIds = new();
    public MdlLod[] Lods = { new(), new(), new() };
    public List<byte[]> ExtraLods = new();
    public List<MdlMesh> Meshes = new();
    public List<string> Attributes = new();
    public List<byte[]> TerrainShadowMeshes = new();
    public List<MdlSubMesh> SubMeshes = new();
    public List<byte[]> TerrainShadowSubMeshes = new();
    public List<string> Materials = new();
    public List<string> Bones = new();
    public List<ushort[]> BoneTables = new();
    public List<MdlShape> Shapes = new();
    public List<MdlShapeMesh> ShapeMeshes = new();
    public List<MdlShapeValue> ShapeValues = new();
    public List<ushort> SubMeshBoneMap = new();
    public float[][] BoundingBoxes = { new float[8], new float[8], new float[8], new float[8] };
    public List<float[]> BoneBoundingBoxes = new();

    /// <summary> Bytes between the bone bounding boxes and the vertex data (neck morph data and the like), kept as they are. </summary>
    public byte[] RuntimeTail = Array.Empty<byte>();

    /// <summary> Vertex and index buffers of all LODs, as stored after the header. </summary>
    public byte[] Data = Array.Empty<byte>();

    // Kept from reading so unmodified parts come back byte for byte.
    private List<string>? _stringPool;
    private byte[]? _rawStrings;
    private byte[]? _padBytes;

    public bool ExtraLodEnabled => (Flags2 & 0x10) != 0;

    /// <summary> Set by edits that rebuild the buffers: the LODs' EdgeGeometryDataOffset is then written as the absolute index buffer offset. </summary>
    public bool FixEdgeOffsetsOnWrite;

    // ---------------------------------------------------------------- reading

    public static MdlFile Read(byte[] b)
    {
        var m = new MdlFile();
        var r = new Reader(b);
        m.Version = r.U32();
        var stack            = r.U32();
        var runtime          = r.U32();
        var declarationCount = r.U16();
        var materialCount    = r.U16();
        for (var i = 0; i < 3; ++i) m.VertexOffset[i]     = r.U32();
        for (var i = 0; i < 3; ++i) m.IndexOffset[i]      = r.U32();
        for (var i = 0; i < 3; ++i) m.VertexBufferSize[i] = r.U32();
        for (var i = 0; i < 3; ++i) m.IndexBufferSize[i]  = r.U32();
        m.LodCount                   = r.U8();
        m.EnableIndexBufferStreaming = r.U8();
        m.EnableEdgeGeometry         = r.U8();
        r.U8();

        if (m.Version is not (V5 or V6))
            throw new InvalidDataException($"Unsupported model version 0x{m.Version:X}.");
        if (m.LodCount > 3)
            throw new InvalidDataException("Bad LOD count.");

        var dataOffset = (uint)HeaderSize + runtime + stack;
        if (dataOffset > b.Length)
            throw new InvalidDataException("The model file is cut off.");

        // Offsets of unused LODs are kept as stored (they usually point at the end of the file).
        for (var i = 0; i < m.LodCount; ++i)
        {
            m.VertexOffset[i] -= dataOffset;
            m.IndexOffset[i]  -= dataOffset;
        }

        for (var d = 0; d < declarationCount; ++d)
        {
            var els = new List<VertexElement>();
            for (var k = 0; k < ElementsPerDeclaration; ++k)
            {
                var e = new VertexElement(r.U8(), r.U8(), r.U8(), r.U8(), r.U8());
                r.Skip(3);
                if (e.Stream == 255)
                {
                    r.Skip((ElementsPerDeclaration - 1 - k) * 8);
                    break;
                }

                els.Add(e);
            }

            m.VertexDeclarations.Add(els);
        }

        if (r.Position != HeaderSize + stack)
            throw new InvalidDataException("Unexpected vertex declaration size.");

        var stringCount = r.U16();
        r.U16();
        var stringSize = (int)r.U32();
        var strings    = r.Bytes(stringSize);
        var offsets    = new Dictionary<uint, string>();
        var pool       = new List<string>();
        var start      = 0;
        for (var i = 0; i < stringCount && start < strings.Length; ++i)
        {
            var end = Array.IndexOf(strings, (byte)0, start);
            if (end < 0)
                end = strings.Length;
            var s = Encoding.UTF8.GetString(strings, start, end - start);
            offsets[(uint)start] = s;
            pool.Add(s);
            start = end + 1;
        }

        m._stringPool = pool;
        m._rawStrings = strings;

        string S(uint o) => offsets.TryGetValue(o, out var s) ? s : string.Empty;

        m.Radius = r.F32();
        var meshCount       = r.U16();
        var attributeCount  = r.U16();
        var subMeshCount    = r.U16();
        var mhMaterialCount = r.U16();
        var boneCount       = r.U16();
        var boneTableCount  = r.U16();
        var shapeCount      = r.U16();
        var shapeMeshCount  = r.U16();
        var shapeValueCount = r.U16();
        r.U8(); // lod count (repeated)
        m.Flags1 = r.U8();
        var elementIdCount         = r.U16();
        var terrainShadowMeshCount = r.U8();
        m.Flags2                = r.U8();
        m.ModelClipOutDistance  = r.F32();
        m.ShadowClipOutDistance = r.F32();
        m.CullingGridCount      = r.U16();
        var terrainShadowSubMeshCount = r.U16();
        m.Flags3                     = r.U8();
        m.BgChangeMaterialIndex      = r.U8();
        m.BgCrestChangeMaterialIndex = r.U8();
        m.NeckMorphCount             = r.U8();
        var boneTableArrayCountTotal = r.U16();
        m.Unknown8 = r.U16();
        m.Unknown9 = r.U16();
        r.Skip(6);

        if (mhMaterialCount != materialCount)
            throw new InvalidDataException("Material counts in the headers disagree.");

        for (var i = 0; i < elementIdCount; ++i)
            m.ElementIds.Add(r.Bytes(32));

        for (var i = 0; i < 3; ++i)
        {
            var l = m.Lods[i];
            l.MeshIndex              = r.U16(); l.MeshCount              = r.U16();
            l.ModelLodRange          = r.F32(); l.TextureLodRange        = r.F32();
            l.WaterMeshIndex         = r.U16(); l.WaterMeshCount         = r.U16(); l.ShadowMeshIndex      = r.U16(); l.ShadowMeshCount      = r.U16();
            l.TerrainShadowMeshIndex = r.U16(); l.TerrainShadowMeshCount = r.U16(); l.VerticalFogMeshIndex = r.U16(); l.VerticalFogMeshCount = r.U16();
            l.EdgeGeometrySize       = r.U32(); l.EdgeGeometryDataOffset = r.U32(); l.PolygonCount         = r.U32(); l.Unknown1             = r.U32();
            l.VertexBufferSize       = r.U32(); l.IndexBufferSize        = r.U32(); l.VertexDataOffset     = r.U32(); l.IndexDataOffset      = r.U32();
            if (i < m.LodCount)
            {
                l.VertexDataOffset -= dataOffset;
                l.IndexDataOffset  -= dataOffset;
            }
        }

        if (m.ExtraLodEnabled)
            for (var i = 0; i < 3; ++i)
                m.ExtraLods.Add(r.Bytes(40));

        for (var i = 0; i < meshCount; ++i)
        {
            var me = new MdlMesh
            {
                VertexCount   = r.U16(), Padding       = r.U16(), IndexCount     = r.U32(), MaterialIndex = r.U16(),
                SubMeshIndex  = r.U16(), SubMeshCount  = r.U16(), BoneTableIndex = r.U16(), StartIndex    = r.U32(),
            };
            for (var k = 0; k < 3; ++k) me.VertexBufferOffset[k] = r.U32();
            for (var k = 0; k < 3; ++k) me.VertexBufferStride[k] = r.U8();
            me.VertexStreamCountRaw = r.U8();
            m.Meshes.Add(me);
        }

        for (var i = 0; i < attributeCount; ++i)
            m.Attributes.Add(S(r.U32()));
        for (var i = 0; i < terrainShadowMeshCount; ++i)
            m.TerrainShadowMeshes.Add(r.Bytes(20));
        for (var i = 0; i < subMeshCount; ++i)
            m.SubMeshes.Add(new MdlSubMesh { IndexOffset = r.U32(), IndexCount = r.U32(), AttributeIndexMask = r.U32(), BoneStartIndex = r.U16(), BoneCount = r.U16() });
        for (var i = 0; i < terrainShadowSubMeshCount; ++i)
            m.TerrainShadowSubMeshes.Add(r.Bytes(12));
        for (var i = 0; i < materialCount; ++i)
            m.Materials.Add(S(r.U32()));
        for (var i = 0; i < boneCount; ++i)
            m.Bones.Add(S(r.U32()));

        if (m.Version == V5)
        {
            for (var i = 0; i < boneTableCount; ++i)
            {
                var idx = new ushort[64];
                for (var k = 0; k < 64; ++k) idx[k] = r.U16();
                var cnt = (int)r.U32();
                m.BoneTables.Add(idx[..Math.Min(cnt, 64)]);
            }
        }
        else
        {
            for (var i = 0; i < boneTableCount; ++i)
            {
                var entry  = r.Position;
                var offset = r.U16();
                var size   = r.U16();
                var table  = new ushort[size];
                var p      = entry + offset * 4;
                for (var k = 0; k < size; ++k)
                    table[k] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 2 * k));
                m.BoneTables.Add(table);
            }

            r.Skip(boneTableArrayCountTotal * 2);
        }

        for (var i = 0; i < shapeCount; ++i)
        {
            var sh = new MdlShape { Name = S(r.U32()) };
            for (var k = 0; k < 3; ++k) sh.ShapeMeshStart[k] = r.U16();
            for (var k = 0; k < 3; ++k) sh.ShapeMeshCount[k] = r.U16();
            m.Shapes.Add(sh);
        }

        for (var i = 0; i < shapeMeshCount; ++i)
            m.ShapeMeshes.Add(new MdlShapeMesh(r.U32(), r.U32(), r.U32()));
        for (var i = 0; i < shapeValueCount; ++i)
            m.ShapeValues.Add(new MdlShapeValue(r.U16(), r.U16()));

        var boneMapBytes = r.U32();
        for (var i = 0; i < boneMapBytes / 2; ++i)
            m.SubMeshBoneMap.Add(r.U16());

        var pad = r.U8();
        m._padBytes = r.Bytes(pad);

        for (var i = 0; i < 4; ++i)
            m.BoundingBoxes[i] = r.Box();
        for (var i = 0; i < boneCount; ++i)
            m.BoneBoundingBoxes.Add(r.Box());

        if (r.Position > dataOffset)
            throw new InvalidDataException("The model header is larger than stated.");

        m.RuntimeTail = r.Bytes((int)dataOffset - r.Position);
        m.Data        = b.AsSpan((int)dataOffset).ToArray();
        return m;
    }

    // ---------------------------------------------------------------- strings

    /// <summary>
    /// Renames a string everywhere it is used (attributes, bones, materials, shapes). Unlike editing the lists directly this
    /// keeps the original string table layout, so a same-length rename changes only those bytes.
    /// </summary>
    public void RenameString(string from, string to)
    {
        if (from == to)
            return;

        Replace(Attributes);
        Replace(Bones);
        Replace(Materials);
        foreach (var s in Shapes)
            if (s.Name == from)
                s.Name = to;

        if (_stringPool is not null)
        {
            Replace(_stringPool);
            _rawStrings = null;
        }

        void Replace(List<string> list)
        {
            for (var i = 0; i < list.Count; ++i)
                if (list[i] == from)
                    list[i] = to;
        }
    }

    /// <summary> Forgets the original string table: the next write lays it out fresh (attributes, bones, materials, shapes). </summary>
    public void ResetStringTable()
    {
        _stringPool = null;
        _rawStrings = null;
        _padBytes   = null;
    }

    private (byte[] Block, Dictionary<string, uint> Offsets) BuildStrings(List<string> needed, int alignBase)
    {
        // Reuse the original table when every name is still in it.
        if (_stringPool is not null && needed.All(_stringPool.Contains))
        {
            var block   = new List<byte>();
            var offsets = new Dictionary<string, uint>();
            foreach (var s in _stringPool)
            {
                offsets.TryAdd(s, (uint)block.Count);
                block.AddRange(Encoding.UTF8.GetBytes(s));
                block.Add(0);
            }

            if (_rawStrings is not null)
                return (_rawStrings, offsets);

            while ((alignBase + block.Count) % 4 != 0)
                block.Add(0);
            return (block.ToArray(), offsets);
        }

        {
            var block   = new List<byte>();
            var offsets = new Dictionary<string, uint>();
            foreach (var s in needed)
            {
                // Same layout as Penumbra / the Python tool: every name written in order, duplicates included.
                offsets.TryAdd(s, (uint)block.Count);
                block.AddRange(Encoding.UTF8.GetBytes(s));
                block.Add(0);
            }

            while ((alignBase + block.Count) % 4 != 0)
                block.Add(0);
            return (block.ToArray(), offsets);
        }
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Writes the model. With <paramref name="fixEdgeGeometryOffset"/> the LODs' EdgeGeometryDataOffset is set to the absolute
    /// index buffer offset (the convention vanilla files follow); otherwise the read value is kept.
    /// </summary>
    public byte[] Write(bool fixEdgeGeometryOffset = false)
    {
        if (Attributes.Count > 32)
            throw new InvalidOperationException("A model can have at most 32 attributes.");

        var w = new Writer();
        w.Zero(HeaderSize);

        foreach (var els in VertexDeclarations)
        {
            if (els.Count >= ElementsPerDeclaration)
                throw new InvalidOperationException("Too many vertex elements.");
            foreach (var e in els)
            {
                w.U8(e.Stream); w.U8(e.Offset); w.U8(e.Type); w.U8(e.Usage); w.U8(e.UsageIndex); w.Zero(3);
            }

            w.U8(255); w.Zero(7);
            w.Zero((ElementsPerDeclaration - 1 - els.Count) * 8);
        }

        var stack = (uint)(VertexDeclarations.Count * ElementsPerDeclaration * 8);

        var needed = Attributes.Concat(Bones).Concat(Materials).Concat(Shapes.Select(s => s.Name)).ToList();
        var (block, offsets) = BuildStrings(needed, HeaderSize + (int)stack + 8);
        w.U16((ushort)(_stringPool is not null && needed.All(_stringPool.Contains) ? _stringPool.Count : needed.Count));
        w.U16(0);
        w.U32((uint)block.Length);
        w.Bytes(block);

        var boneTableSum = Version == V5 ? 0 : BoneTables.Sum(t => t.Length + (t.Length & 1));

        w.F32(Radius);
        w.U16((ushort)Meshes.Count); w.U16((ushort)Attributes.Count); w.U16((ushort)SubMeshes.Count); w.U16((ushort)Materials.Count);
        w.U16((ushort)Bones.Count); w.U16((ushort)BoneTables.Count); w.U16((ushort)Shapes.Count); w.U16((ushort)ShapeMeshes.Count);
        w.U16((ushort)ShapeValues.Count);
        w.U8(LodCount); w.U8(Flags1); w.U16((ushort)ElementIds.Count); w.U8((byte)TerrainShadowMeshes.Count); w.U8(Flags2);
        w.F32(ModelClipOutDistance); w.F32(ShadowClipOutDistance); w.U16(CullingGridCount); w.U16((ushort)TerrainShadowSubMeshes.Count);
        w.U8(Flags3); w.U8(BgChangeMaterialIndex); w.U8(BgCrestChangeMaterialIndex); w.U8(NeckMorphCount);
        w.U16((ushort)boneTableSum); w.U16(Unknown8); w.U16(Unknown9); w.Zero(6);

        foreach (var e in ElementIds)
            w.Bytes(e);

        var lodPosition = w.Position;
        w.Zero(3 * 60);
        if (ExtraLodEnabled)
            foreach (var e in ExtraLods)
                w.Bytes(e);

        foreach (var me in Meshes)
        {
            w.U16(me.VertexCount); w.U16(me.Padding); w.U32(me.IndexCount); w.U16(me.MaterialIndex); w.U16(me.SubMeshIndex);
            w.U16(me.SubMeshCount); w.U16(me.BoneTableIndex); w.U32(me.StartIndex);
            foreach (var o in me.VertexBufferOffset) w.U32(o);
            foreach (var s in me.VertexBufferStride) w.U8(s);
            w.U8(me.VertexStreamCountRaw);
        }

        foreach (var a in Attributes)
            w.U32(offsets[a]);
        foreach (var t in TerrainShadowMeshes)
            w.Bytes(t);
        foreach (var s in SubMeshes)
        {
            w.U32(s.IndexOffset); w.U32(s.IndexCount); w.U32(s.AttributeIndexMask); w.U16(s.BoneStartIndex); w.U16(s.BoneCount);
        }

        foreach (var t in TerrainShadowSubMeshes)
            w.Bytes(t);
        foreach (var mat in Materials)
            w.U32(offsets[mat]);
        foreach (var bone in Bones)
            w.U32(offsets[bone]);

        if (Version == V5)
        {
            foreach (var t in BoneTables)
            {
                if (t.Length > 64)
                    throw new InvalidOperationException("Version 5 models allow at most 64 bones per mesh.");
                for (var k = 0; k < 64; ++k) w.U16(k < t.Length ? t[k] : (ushort)0);
                w.U32((uint)t.Length);
            }
        }
        else
        {
            var current = BoneTables.Count;
            var header  = new Writer();
            var arrays  = new Writer();
            foreach (var t in BoneTables)
            {
                header.U16((ushort)current); header.U16((ushort)t.Length);
                foreach (var bone in t) arrays.U16(bone);
                if ((t.Length & 1) == 1) arrays.U16(0);
                current += (t.Length + 1) / 2 - 1;
            }

            w.Bytes(header.ToArray());
            w.Bytes(arrays.ToArray());
        }

        foreach (var shape in Shapes)
        {
            w.U32(offsets[shape.Name]);
            foreach (var v in shape.ShapeMeshStart) w.U16(v);
            foreach (var v in shape.ShapeMeshCount) w.U16(v);
        }

        foreach (var s in ShapeMeshes)
        {
            w.U32(s.MeshIndexOffset); w.U32(s.ShapeValueCount); w.U32(s.ShapeValueOffset);
        }

        foreach (var s in ShapeValues)
        {
            w.U16(s.BaseIndicesIndex); w.U16(s.ReplacingVertexIndex);
        }

        w.U32((uint)(SubMeshBoneMap.Count * 2));
        foreach (var v in SubMeshBoneMap)
            w.U16(v);

        // The padding before the bounding boxes: kept as read (tools differ in what they align to), else 8-byte aligned.
        if (_padBytes is not null)
        {
            w.U8((byte)_padBytes.Length);
            w.Bytes(_padBytes);
        }
        else
        {
            var pos = w.Position + 1;
            var pad = (8 - (pos & 7)) & 7;
            w.U8((byte)pad);
            for (var i = 0; i < pad; ++i)
                w.U8((byte)((0xDEADBEEFF00DCAFEUL >> (8 * (7 - i))) & 0xFF));
        }

        foreach (var box in BoundingBoxes)
            w.Box(box);
        foreach (var box in BoneBoundingBoxes)
            w.Box(box);
        w.Bytes(RuntimeTail);

        var total = (uint)w.Position;
        w.Bytes(Data);
        var output = w.ToArray();

        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], stack);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], total - stack - HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)VertexDeclarations.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)Materials.Count);
        for (var i = 0; i < 3; ++i)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[(0x10 + 4 * i)..], i < LodCount ? VertexOffset[i] + total : VertexOffset[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(0x1C + 4 * i)..], i < LodCount ? IndexOffset[i] + total : IndexOffset[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(0x28 + 4 * i)..], VertexBufferSize[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(0x34 + 4 * i)..], IndexBufferSize[i]);
        }

        span[0x40] = LodCount;
        span[0x41] = EnableIndexBufferStreaming;
        span[0x42] = EnableEdgeGeometry;
        span[0x43] = 0;

        var lw = new Writer();
        for (var i = 0; i < 3; ++i)
        {
            var l    = Lods[i];
            var edge = (fixEdgeGeometryOffset || FixEdgeOffsetsOnWrite) && i < LodCount ? total + l.IndexDataOffset : l.EdgeGeometryDataOffset;
            lw.U16(l.MeshIndex); lw.U16(l.MeshCount); lw.F32(l.ModelLodRange); lw.F32(l.TextureLodRange);
            lw.U16(l.WaterMeshIndex); lw.U16(l.WaterMeshCount); lw.U16(l.ShadowMeshIndex); lw.U16(l.ShadowMeshCount);
            lw.U16(l.TerrainShadowMeshIndex); lw.U16(l.TerrainShadowMeshCount); lw.U16(l.VerticalFogMeshIndex); lw.U16(l.VerticalFogMeshCount);
            lw.U32(l.EdgeGeometrySize); lw.U32(edge); lw.U32(l.PolygonCount); lw.U32(l.Unknown1);
            lw.U32(l.VertexBufferSize); lw.U32(l.IndexBufferSize);
            lw.U32(i < LodCount ? l.VertexDataOffset + total : l.VertexDataOffset);
            lw.U32(i < LodCount ? l.IndexDataOffset + total : l.IndexDataOffset);
        }

        lw.ToArray().CopyTo(span[lodPosition..]);
        return output;
    }

    /// <summary> Sets the offsets of unused LODs to 0, as Penumbra and the Python tool write them. </summary>
    public void ClearUnusedLods()
    {
        for (var i = LodCount; i < 3; ++i)
        {
            VertexOffset[i]           = 0;
            IndexOffset[i]            = 0;
            Lods[i].VertexDataOffset  = 0;
            Lods[i].IndexDataOffset   = 0;
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary> The vertex buffer bytes of one LOD. </summary>
    public byte[] VertexBuffer(int lod)
        => Data.AsSpan((int)Lods[lod].VertexDataOffset, (int)Lods[lod].VertexBufferSize).ToArray();

    /// <summary> The index buffer bytes of one LOD. </summary>
    public byte[] IndexBuffer(int lod)
        => Data.AsSpan((int)Lods[lod].IndexDataOffset, (int)Lods[lod].IndexBufferSize).ToArray();

    /// <summary> Names of the attributes set in a sub mesh's mask. </summary>
    public IEnumerable<string> AttributesOf(MdlSubMesh sub)
    {
        for (var bit = 0; bit < 32 && bit < Attributes.Count; ++bit)
            if ((sub.AttributeIndexMask >> bit & 1) == 1)
                yield return Attributes[bit];
    }

    /// <summary> The LOD a mesh belongs to (by the LODs' main mesh ranges), or -1. </summary>
    public int LodOf(int meshIndex)
    {
        for (var i = 0; i < LodCount; ++i)
            if (meshIndex >= Lods[i].MeshIndex && meshIndex < Lods[i].MeshIndex + Lods[i].MeshCount)
                return i;
        return -1;
    }

    private sealed class Reader(byte[] data)
    {
        public int Position;

        public byte U8()
        {
            if (Position >= data.Length)
                throw new InvalidDataException("The model file is cut off.");
            return data[Position++];
        }

        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position)); Position += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position)); Position += 4; return v; }
        public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(Position)); Position += 4; return v; }
        public byte[] Bytes(int n) { var v = data.AsSpan(Position, n).ToArray(); Position += n; return v; }
        public void Skip(int n) => Position += n;

        public float[] Box()
        {
            var b = new float[8];
            for (var i = 0; i < 8; ++i) b[i] = F32();
            return b;
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();
        public int Position => (int)_ms.Position;
        public void U8(byte v) => _ms.WriteByte(v);
        public void U16(ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); _ms.Write(s); }
        public void U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); _ms.Write(s); }
        public void F32(float v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(s, v); _ms.Write(s); }
        public void Bytes(byte[] v) => _ms.Write(v);
        public void Zero(int n) { for (var i = 0; i < n; ++i) _ms.WriteByte(0); }
        public void Box(float[] b) { foreach (var f in b) F32(f); }
        public byte[] ToArray() => _ms.ToArray();
    }
}
