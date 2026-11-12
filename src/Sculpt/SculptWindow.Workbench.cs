using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace SculptPlus;

/// <summary>
/// The workbench: a window of its own with your head as a plain clay model. Turn it freely, come as close as you like,
/// and take hold of the skin itself: the area a part moves lights up under the mouse, and dragging shapes it. Your
/// character in the game changes along and stays the place to judge the result (skin, make-up, light).
/// </summary>
public sealed partial class SculptWindow
{
    private Workbench? _bench;
    private bool _benchOpen;
    private bool _benchFitted;
    private float _benchYaw, _benchPitch, _benchZoom = 1500f;
    private Vector2 _benchPan;

    private string? _benchPick;                                  // the part that is lit
    private List<(string Bone, float Weight)> _benchStack = new(); // the parts that move the spot under the mouse, strongest first
    private List<(string Bone, float Weight)> _benchGrab = new();  // the same, as it was when you took hold
    private string? _benchDrag;
    private int _benchMode;        // 0: nothing, 1: a part, 2: turning the head, 3: moving the view
    private bool _benchRight;
    private string? _benchRightPart;
    private float _benchTravel;

    private readonly BenchView _benchView = new();

    private static readonly HashSet<string> BenchTrunk = new(StringComparer.Ordinal) { "j_kao", "j_kubi", "j_f_face", "j_sebo_c", "n_hara", "n_root" };

    private void CloseWorkbench()
    {
        _benchOpen   = false;
        _benchMode   = 0;
        _benchDrag   = null;
        _benchPick   = null;
        _benchFitted = false;
    }

    private void DrawWorkbench(Dictionary<string, LiveBone> bones, float s)
    {
        if (!_benchOpen)
            return;
        _bench ??= new Workbench(_plugin);
        if (!_bench.Ready && !_bench.Loading && _bench.Problem.Length == 0)
            _bench.Load(Sculptor.Address);

        ImGui.SetNextWindowSize(new Vector2(540, 620) * s, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(340, 340) * s, new Vector2(4000, 4000));
        var open = true;
        if (ImGui.Begin(Loc.T("Sculpt+ workbench") + "###sculptplusbench", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            try
            {
                DrawBenchBar(s);
                DrawBenchCanvas(bones, s);
            }
            catch (Exception ex)
            {
                _plugin.Log.Warning($"Sculpt+: workbench failed: {ex}");
                ImGui.TextColored(Theme.Bad, ex.Message);
            }
        }

        ImGui.End();
        if (!open)
            CloseWorkbench();
    }

    private void DrawBenchBar(float s)
    {
        (string Label, float Yaw, string Tip)[] views =
        {
            (Loc.T("Front"), 0f, Loc.T("The face from the front.")),
            (Loc.T("¾"), -0.7f, Loc.T("Three-quarter view of the left side of the face. Right-click: the right side.")),
            (Loc.T("Side"), -MathF.PI / 2, Loc.T("The profile, left side. Right-click: the right side.")),
        };
        for (var i = 0; i < views.Length; ++i)
        {
            if (i > 0)
                ImGui.SameLine(0, 4 * s);
            if (ImGui.Button(views[i].Label + "##benchview" + i))
                (_benchYaw, _benchPitch, _benchPan) = (views[i].Yaw, 0f, Vector2.Zero);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                (_benchYaw, _benchPitch, _benchPan) = (-views[i].Yaw, 0f, Vector2.Zero);
            Tip(views[i].Tip);
        }

        ImGui.SameLine();
        var hair = Config.BenchHair;
        if (ImGui.Checkbox(Loc.T("Hair"), ref hair))
        {
            Config.BenchHair = hair;
            _plugin.SaveConfig();
        }

        Tip(Loc.T("Show the hair too. Without it you see the face alone (the back of the head belongs to the hair)."));
        ImGui.SameLine();
        var pull = Config.BenchPull;
        if (ImGui.Checkbox(Loc.T("Pull the skin"), ref pull))
        {
            Config.BenchPull = pull;
            _plugin.SaveConfig();
        }

        Tip(Loc.T("Dragging takes along every part that moves the spot you grabbed, each as much as it holds that spot: like pulling skin. Without it, only the lit part moves."));
        ImGui.SameLine();
        if (Icons.Button(FontAwesomeIcon.Sync, "benchreload"))
        {
            _bench?.Clear();
            _benchFitted = false;
        }

        Tip(Loc.T("Load the head again (after changing your face, hair or a mod)."));
    }

    private void DrawBenchCanvas(Dictionary<string, LiveBone> bones, float s)
    {
        var bench = _bench!;
        if (bench.Problem.Length > 0)
        {
            Wrapped(Theme.Warn, bench.Problem);
            Wrapped(Theme.Muted, Loc.T("You can go on sculpting on the character itself."));
            return;
        }

        if (!bench.Ready)
        {
            ImGui.TextColored(Theme.Muted, Loc.T("Loading your head…"));
            return;
        }

        if (bench.Still)
            Wrapped(Theme.Warn, Loc.T("The model and its skeleton do not line up here, so the workbench shows your head as it is stored, without the sculpt. Taking hold of the skin still works: watch your character in the game for the result."));
        if (Config.TestLine)
            Wrapped(Theme.Faint, bench.Info);

        var size = ImGui.GetContentRegionAvail();
        size.Y -= ImGui.GetTextLineHeightWithSpacing();
        if (size.X < 50 || size.Y < 50)
            return;
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##benchcanvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered   = ImGui.IsItemHovered();
        var active    = ImGui.IsItemActive();
        var activated = ImGui.IsItemActivated();
        var io        = ImGui.GetIO();
        var mouse     = io.MousePos;
        var draw      = ImGui.GetWindowDrawList();

        if (!_benchFitted)
        {
            _benchZoom   = 0.42f * MathF.Min(size.X, size.Y) / bench.Radius;
            _benchPan    = Vector2.Zero;
            _benchFitted = true;
        }

        bench.Pose(bones);

        // ---- where every point is on screen, its colour, and the triangles that face you (far ones first) ----
        var view    = Quaternion.CreateFromAxisAngle(Vector3.UnitX, _benchPitch) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, _benchYaw);
        var origin  = min + size / 2 + _benchPan;
        var meshes  = bench.Meshes.Where(m => m.Kind == "face" || Config.BenchHair).ToList();
        var lit     = _benchMode == 1 ? _benchDrag : _benchPick;
        var shown   = _benchView;
        shown.Project(meshes, bench.Centre, view, origin, _benchZoom, lit, _selected == lit ? null : _selected,
            name => !BenchTrunk.Contains(name) && Sculptor.IsEdited(name),
            new Vector3(Theme.Accent.X, Theme.Accent.Y, Theme.Accent.Z), new Vector3(Theme.Good.X, Theme.Good.Y, Theme.Good.Z));

        // ---- what is under the mouse ----
        if (_benchMode == 0)
        {
            _benchStack.Clear();
            if (hovered && shown.Pick(mouse) is { } spot)
            {
                // every part that moves this spot, strongest first
                var (mesh, v) = spot;
                for (var k = 0; k < mesh.Slots; ++k)
                {
                    var w    = mesh.Weights[v * mesh.Slots + k];
                    var bone = mesh.BoneIndices[v * mesh.Slots + k];
                    if (w < 0.06f || bone >= mesh.BoneNames.Length || !mesh.Known[bone])
                        continue;
                    var name  = mesh.BoneNames[bone];
                    var index = _benchStack.FindIndex(x => x.Bone == name);
                    if (index >= 0)
                        _benchStack[index] = (name, _benchStack[index].Weight + w);
                    else
                        _benchStack.Add((name, w));
                }

                // the head and neck hold nearly everything a little: they only count where nothing else does
                if (_benchStack.Any(x => !BenchTrunk.Contains(x.Bone)))
                    _benchStack.RemoveAll(x => BenchTrunk.Contains(x.Bone));
                _benchStack.Sort((x, y) => y.Weight.CompareTo(x.Weight));
            }

            if (_benchStack.Count == 0)
                _benchPick = null;
            else
            {
                var index = _benchStack.FindIndex(x => x.Bone == _benchPick);
                if (index < 0)
                    index = 0;
                if (hovered && io.KeyCtrl && io.MouseWheel != 0 && _benchStack.Count > 1)
                    index = ((index + (io.MouseWheel > 0 ? -1 : 1)) % _benchStack.Count + _benchStack.Count) % _benchStack.Count;
                _benchPick = _benchStack[index].Bone;
            }
        }

        // ---- painting ----
        draw.AddRectFilled(min, min + size, Theme.U32(new Vector4(0.10f, 0.09f, 0.11f, 1f)), 8 * s);
        draw.PushClipRect(min, min + size, true);
        var white = ImGui.GetFontTexUvWhitePixel();
        const int chunk = 4000;
        for (var start = 0; start < shown.TriCount; start += chunk)
        {
            var n = Math.Min(chunk, shown.TriCount - start);
            draw.PrimReserve(n * 3, n * 3);
            for (var i = start; i < start + n; ++i)
            {
                var t = shown.Order[i];
                int a = shown.Tri[t * 3], b = shown.Tri[t * 3 + 1], c = shown.Tri[t * 3 + 2];
                draw.PrimVtx(shown.Screen[a], white, shown.Colour[a]);
                draw.PrimVtx(shown.Screen[b], white, shown.Colour[b]);
                draw.PrimVtx(shown.Screen[c], white, shown.Colour[c]);
            }
        }

        draw.PopClipRect();

        // ---- the mouse ----
        var hot = _benchPick;
        if (hovered && io.MouseWheel != 0 && _benchMode == 0)
        {
            if (io.KeyCtrl)
            {
                // went to the next part underneath (above)
            }
            else if (io.KeyShift && hot is not null)
            {
                if ((DateTime.UtcNow - _lastWheelCheckpoint).TotalSeconds > 0.8)
                    Sculptor.Checkpoint();
                _lastWheelCheckpoint = DateTime.UtcNow;
                _selected            = hot;
                Sculptor.Scale(hot, new Vector3(1f + 0.03f * io.MouseWheel));
            }
            else
            {
                // towards the mouse: what is under it stays under it
                var before = _benchZoom;
                _benchZoom = Math.Clamp(_benchZoom * (1f + 0.14f * io.MouseWheel), 200f, 60000f);
                _benchPan += (mouse - origin) * (1f - _benchZoom / before);
            }
        }

        if (activated)
        {
            _benchTravel    = 0;
            _benchRight     = !ImGui.IsMouseDown(ImGuiMouseButton.Left) && ImGui.IsMouseDown(ImGuiMouseButton.Right);
            _benchRightPart = hot;
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && hot is not null && bones.ContainsKey(hot))
            {
                _benchMode = 1;
                _benchDrag = hot;
                _benchGrab = _benchStack.ToList();
                _selected  = hot;
                Sculptor.Checkpoint();
            }
            else if (ImGui.IsMouseDown(ImGuiMouseButton.Middle) || io.KeyShift && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                _benchMode = 3;
            else
                _benchMode = 2;
        }

        if (active && _benchMode != 0)
        {
            var d = io.MouseDelta;
            _benchTravel += d.Length();
            if (d != Vector2.Zero)
                switch (_benchMode)
                {
                    case 1 when _benchDrag is not null && bones.ContainsKey(_benchDrag):
                        var fine    = io.KeyCtrl ? 0.1f : 1f;
                        var onView  = io.KeyShift ? new Vector3(0, 0, d.Y / _benchZoom) : new Vector3(d.X / _benchZoom, -d.Y / _benchZoom, 0);
                        var onBench = Vector3.Transform(onView * fine, Quaternion.Inverse(view));
                        BenchDrag(bench.ToCharacter(onBench, bones), bones);
                        break;
                    case 2:
                        _benchYaw   += d.X * 0.01f;
                        _benchPitch =  Math.Clamp(_benchPitch + d.Y * 0.01f, -1.45f, 1.45f);
                        break;
                    case 3:
                        _benchPan += d;
                        break;
                }
        }
        else if (_benchMode != 0)
        {
            // a right-click that did not travel puts the part back
            if (_benchMode == 2 && _benchRight && _benchTravel < 4f && _benchRightPart is not null)
            {
                Sculptor.Checkpoint();
                Sculptor.ResetBone(_benchRightPart);
            }

            _benchMode = 0;
            _benchDrag = null;
        }

        if (hovered && _benchMode == 0 && hot is not null)
        {
            var text = Label(hot);
            if (_benchStack.Count > 1)
                text += "\n" + string.Join("  ·  ", _benchStack.Take(6).Select(x => x.Bone == hot ? "[" + Label(x.Bone) + "]" : Label(x.Bone))) + "\n" + Loc.T("Ctrl + wheel: the next part underneath");
            ImGui.SetTooltip(text + "\n" + Loc.T("Drag: shape · Shift+drag: nearer/further · Ctrl+drag: ten times finer · Shift+wheel: size · Right-click: put back"));
        }

        ImGui.TextColored(Theme.Faint, Loc.T("On the skin: drag shapes it. Beside it (or right button): drag turns the head · wheel zooms · middle button moves the view."));
    }

    /// <summary>
    /// A drag on the workbench. With "Pull the skin", every part that holds the grabbed spot goes along, as much as it
    /// holds it compared with the part you took; twins follow mirrored, as everywhere.
    /// </summary>
    private void BenchDrag(Vector3 model, Dictionary<string, LiveBone> bones)
    {
        if (_benchDrag is null || !bones.TryGetValue(_benchDrag, out var grabbed))
            return;
        var mine = _benchGrab.FirstOrDefault(x => x.Bone == _benchDrag).Weight;
        if (!Config.BenchPull || mine <= 0 || _benchGrab.Count < 2)
        {
            Drag(grabbed, model, bones, Config.Brush);
            return;
        }

        var side  = MathF.Abs(grabbed.Model.X) < 0.002f ? 0 : MathF.Sign(grabbed.Model.X);
        var moved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, weight) in _benchGrab)
        {
            var share = name == _benchDrag ? 1f : MathF.Min(1f, weight / mine);
            if (share < 0.25f || !bones.TryGetValue(name, out var bone))
                continue;
            var twin = SculptBones.MirrorOf(name);
            if (Sculptor.Symmetry && side != 0 && twin is not null && MathF.Abs(bone.Model.X) > 0.002f && MathF.Sign(bone.Model.X) != side)
                continue;
            if (!moved.Add(name))
                continue;
            Sculptor.Nudge(bone, model * share);
            if (Sculptor.Symmetry && twin is not null && bones.TryGetValue(twin, out var other) && moved.Add(twin))
                Sculptor.Nudge(other, (model with { X = -model.X }) * share);
        }
    }
}
