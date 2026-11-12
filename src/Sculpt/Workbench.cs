using System.Numerics;
using SculptPlus.Model;

namespace SculptPlus;

/// <summary>
/// The workbench's model: your character's own head (the face model the game draws for you right now, mods included),
/// loaded once and then moved every frame by the same bones the game moves it with. That way it shows the sculpt exactly
/// as Customize+ made it, without redoing its sums: the bones are read from the game after Customize+ changed them.
/// The head is shown upright whatever your character is doing with it.
/// </summary>
public sealed class Workbench
{
    private readonly Plugin _plugin;
    private Dictionary<string, (Vector3 Position, Quaternion Rotation)> _rest = new();
    private int _loadId;

    public Workbench(Plugin plugin)
        => _plugin = plugin;

    public List<BenchMesh> Meshes { get; private set; } = new();
    public bool Loading { get; private set; }
    public bool Ready { get; private set; }
    public string Problem { get; private set; } = string.Empty;

    /// <summary> How far the skeleton's rest pose is from the model on average (metres): small when the two belong together. </summary>
    public float Fit { get; private set; }

    /// <summary>
    /// The skeleton's rest pose does not belong to the model (it would tear the picture apart): the model is shown as
    /// stored, without pose and sculpt. Picking a spot and its parts still works.
    /// </summary>
    public bool Still => Fit > 0.05f;

    public string Info { get; private set; } = string.Empty;

    private Dictionary<string, Vector3> _spots = new();

    /// <summary>
    /// Where on the character (in its model's space) the skin lies that this bone moves; false when that is not known
    /// (the head is not loaded, the bone moves no skin of the face, or model and skeleton do not belong together).
    /// </summary>
    public bool SkinSpot(LiveBone bone, out Vector3 model)
    {
        model = default;
        if (!Ready || Still || !_spots.TryGetValue(bone.Name, out var spot) || !_rest.TryGetValue(bone.Name, out var rest))
            return false;
        model = Bench.Carry(spot, rest, bone);
        return !float.IsNaN(model.X);
    }
    public Vector3 Centre { get; private set; }
    public float Radius { get; private set; } = 0.15f;

    public void Clear()
    {
        _loadId++;
        Meshes  = new List<BenchMesh>();
        _spots  = new Dictionary<string, Vector3>();
        Ready   = false;
        Loading = false;
        Problem = string.Empty;
    }

    /// <summary> Draw thread: reads which models the character shows and its skeleton, then loads the files in the background. </summary>
    public void Load(nint address)
    {
        Clear();
        var live = LiveModels.Read(address, out var problem);
        if (live is null)
        {
            Problem = Loc.T("The character's models could not be read.") + " (" + problem + ")";
            return;
        }

        var wanted = new List<(LiveModel Model, string Kind)>();
        foreach (var model in live)
        {
            var path = model.Path.Replace('\\', '/').ToLowerInvariant();
            if (path.Contains("/face/") || path.EndsWith("_fac.mdl", StringComparison.Ordinal))
                wanted.Add((model, "face"));
            else if (path.Contains("/hair/") || path.EndsWith("_hir.mdl", StringComparison.Ordinal))
                wanted.Add((model, "hair"));
        }

        if (wanted.All(w => w.Kind != "face"))
        {
            Problem = Loc.T("No face model found on the character.");
            return;
        }

        // the rest pose two ways (see ReadRest); the one that fits the model is kept
        var hung  = SculptBones.ReadRest(address, true);
        var plain = SculptBones.ReadRest(address, false);
        var id    = _loadId;
        Loading = true;
        Task.Run(() =>
        {
            try
            {
                var meshes = new List<BenchMesh>();
                var notes  = new List<string>();
                foreach (var (model, kind) in wanted)
                {
                    var bytes = Path.IsPathRooted(model.Path)
                        ? File.Exists(model.Path) ? File.ReadAllBytes(model.Path) : null
                        : _plugin.Data.GetFile(model.Path)?.Data;
                    if (bytes is null)
                    {
                        notes.Add($"{Path.GetFileName(model.Path)}: not found");
                        continue;
                    }

                    var mdl     = MdlFile.Read(bytes);
                    var decoded = ModelDecode.DecodeLod0(mdl, model.Attributes, model.Shapes);
                    var kept    = 0;
                    foreach (var mesh in decoded)
                    {
                        // lashes and brows are flat cards with a see-through picture: as clay they would be flaps over the eyes
                        var material = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < mdl.Materials.Count ? mdl.Materials[mesh.MaterialIndex] : string.Empty;
                        if (kind == "face" && material.Contains("_etc_", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (Bench.Convert(mesh, kind) is { } bench)
                        {
                            meshes.Add(bench);
                            kept++;
                        }
                    }

                    notes.Add($"{Path.GetFileName(model.Path)}: {kept} mesh(es)");
                }

                if (meshes.All(m => m.Kind != "face"))
                    throw new InvalidOperationException(Loc.T("The face model could not be read.") + " " + string.Join("; ", notes));

                var fitHung  = Bench.FitOf(meshes, hung);
                var fitPlain = Bench.FitOf(meshes, plain);
                var rest     = fitHung <= fitPlain ? hung : plain;

                // A spot on the skin for each bone. One that lies far from its bone is left out: that bone holds a
                // wide area a little (the head, the neck) and has no one place.
                var spots = Bench.SkinSpots(meshes)
                    .Where(kv => rest.TryGetValue(kv.Key, out var at) && Vector3.Distance(kv.Value, at.Position) < 0.06f)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

                // the middle and size of the face, for the first view
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var m in meshes.Where(m => m.Kind == "face"))
                    foreach (var p in m.Rest)
                    {
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }

                _plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (id != _loadId)
                        return;
                    _rest   = rest;
                    _spots  = spots;
                    Meshes  = meshes;
                    Fit     = MathF.Min(fitHung, fitPlain);
                    Centre  = (min + max) / 2;
                    Radius  = MathF.Max(0.05f, (max - min).Length() / 2);
                    Info    = $"{string.Join("; ", notes)} · {meshes.Sum(m => m.Rest.Length)} points, {meshes.Sum(m => m.Triangles.Length / 3)} triangles · rest pose fits to {fitHung * 1000:0} mm hung, {fitPlain * 1000:0} mm plain";
                    Loading = false;
                    Ready   = true;
                });
            }
            catch (Exception ex)
            {
                _plugin.Log.Warning($"Sculpt+: workbench could not load the head: {ex}");
                _plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (id != _loadId)
                        return;
                    Problem = ex is InvalidOperationException ? ex.Message : Loc.T("The head could not be loaded.") + " (" + ex.Message + ")";
                    Loading = false;
                });
            }
        });
    }

    /// <summary> Every frame: moves the points the way the game does (see <see cref="Bench.Pose"/>). </summary>
    public void Pose(Dictionary<string, LiveBone> bones)
    {
        if (Ready)
            Bench.Pose(Meshes, _rest, bones, Still);
    }

    /// <summary> Turns a direction on the workbench into the character's own (the head there is upright, on the character it may not be). </summary>
    public Vector3 ToCharacter(Vector3 onBench, Dictionary<string, LiveBone> bones)
        => Bench.ToCharacter(onBench, _rest, bones);
}
