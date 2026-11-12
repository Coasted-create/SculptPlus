using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using CsCharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using CsResourceHandle = FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.ResourceHandle;
using CsSceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace SculptPlus.Model;

/// <summary> A material the game uses for a drawn model: its file and the files of its textures (in the material's order). </summary>
public sealed record LiveMaterial(string Path, string[] Textures);

/// <summary>
/// A model the game draws for your character right now: the file it was loaded from, which of its parts are switched on
/// (attributes: e.g. gloves hide the bare hands, a dress hides the arm skin) and which shape keys are on (e.g. a body
/// shape for a top), and the materials it really uses.
/// </summary>
public sealed record LiveModel(int Slot, string Path, uint Attributes, uint Shapes, LiveMaterial?[] Materials);

/// <summary> Reads the models of your character from the game, read only. Call on the framework thread only. </summary>
public static unsafe class LiveModels
{
    private const int MaxSlots = 32;
    private const int MaxMaterials = 64;
    private const int MaxTextures = 32;

    public static List<LiveModel>? Read(nint gameObject, out string problem)
    {
        problem = string.Empty;
        if (gameObject == nint.Zero)
        {
            problem = "no character";
            return null;
        }

        var draw = ((CsGameObject*)gameObject)->DrawObject;
        if (draw == null || ((CsSceneObject*)draw)->GetObjectType() != ObjectType.CharacterBase)
        {
            problem = "character not drawn";
            return null;
        }

        var cb    = (CsCharacterBase*)draw;
        var slots = cb->SlotCount;
        if (slots <= 0 || slots > MaxSlots || cb->Models == null)
        {
            problem = "no models";
            return null;
        }

        var list = new List<LiveModel>();
        for (var i = 0; i < slots; ++i)
        {
            var model = cb->Models[i];
            if (model == null || model->ModelResourceHandle == null)
                continue;
            var path = Name((CsResourceHandle*)model->ModelResourceHandle);
            if (path.Length == 0)
                continue;

            var count     = Math.Clamp(model->MaterialCount, 0, MaxMaterials);
            var materials = new LiveMaterial?[count];
            if (model->Materials != null)
                for (var j = 0; j < count; ++j)
                {
                    var material = model->Materials[j];
                    if (material == null || material->MaterialResourceHandle == null)
                        continue;
                    var handle = material->MaterialResourceHandle;
                    var mtrl   = Name((CsResourceHandle*)handle);
                    if (mtrl.Length == 0)
                        continue;
                    var textures = new string[Math.Min((int)handle->TextureCount, MaxTextures)];
                    for (var k = 0; k < textures.Length; ++k)
                    {
                        var tex = handle->Textures == null ? null : handle->Textures[k].TextureResourceHandle;
                        textures[k] = tex == null ? string.Empty : Name((CsResourceHandle*)tex);
                    }

                    materials[j] = new LiveMaterial(mtrl, textures);
                }

            list.Add(new LiveModel(i, path, model->EnabledAttributeIndexMask, model->EnabledShapeKeyIndexMask, materials));
        }

        if (list.Count == 0)
            problem = "no models";
        return list;
    }

    /// <summary> The file of a loaded resource. Penumbra puts "|collection|" in front of files it replaced; that goes. </summary>
    private static string Name(CsResourceHandle* handle)
    {
        var bytes = handle->FileName.AsSpan();
        var end   = bytes.IndexOf((byte)0);
        if (end >= 0)
            bytes = bytes[..end];
        if (bytes.Length == 0 || bytes.Length > 1024)
            return string.Empty;
        return Clean(Encoding.UTF8.GetString(bytes));
    }

    /// <summary> "|abc_123|C:\mods\x.mdl" → "C:\mods\x.mdl"; game paths stay as they are (lower case, forward slashes). </summary>
    public static string Clean(string name)
    {
        var bar = name.LastIndexOf('|');
        if (bar >= 0)
            name = name[(bar + 1)..];
        return System.IO.Path.IsPathRooted(name) ? name : name.Replace('\\', '/').ToLowerInvariant();
    }
}
