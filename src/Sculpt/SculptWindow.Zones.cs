using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace SculptPlus;

/// <summary>
/// The map of the face and body (pick an area instead of hunting for it on the character), the sliders that belong to
/// the picked area, the focus camera's controls, and the tiny steps for the part you hold.
/// </summary>
public sealed partial class SculptWindow
{
    /// <summary> One clickable shape on a map: which area it is and where it lies (0..1 across and down the map). </summary>
    private readonly record struct MapZone(string Group, float X, float Y, float Rx, float Ry);

    private static readonly MapZone[] FaceMap =
    {
        new("brows", 0.33f, 0.335f, 0.115f, 0.034f), new("brows", 0.67f, 0.335f, 0.115f, 0.034f),
        new("eyes", 0.33f, 0.430f, 0.105f, 0.050f), new("eyes", 0.67f, 0.430f, 0.105f, 0.050f),
        new("nose", 0.50f, 0.555f, 0.065f, 0.100f),
        new("cheeks", 0.25f, 0.615f, 0.090f, 0.085f), new("cheeks", 0.75f, 0.615f, 0.090f, 0.085f),
        new("lips", 0.50f, 0.740f, 0.140f, 0.048f),
        new("jaw", 0.50f, 0.880f, 0.190f, 0.055f),
        new("ears", 0.075f, 0.470f, 0.050f, 0.100f), new("ears", 0.925f, 0.470f, 0.050f, 0.100f),
    };

    private static readonly MapZone[] BodyMap =
    {
        new("head", 0.50f, 0.095f, 0.085f, 0.075f),
        new("chest", 0.50f, 0.270f, 0.170f, 0.065f),
        new("torso", 0.50f, 0.430f, 0.140f, 0.090f),
        new("arms", 0.23f, 0.370f, 0.055f, 0.150f), new("arms", 0.77f, 0.370f, 0.055f, 0.150f),
        new("hands", 0.19f, 0.580f, 0.050f, 0.040f), new("hands", 0.81f, 0.580f, 0.050f, 0.040f),
        new("legs", 0.41f, 0.720f, 0.075f, 0.160f), new("legs", 0.59f, 0.720f, 0.075f, 0.160f),
        new("feet", 0.40f, 0.945f, 0.075f, 0.035f), new("feet", 0.60f, 0.945f, 0.075f, 0.035f),
        new("tail", 0.91f, 0.740f, 0.045f, 0.110f),
    };

    private string? _zone;          // the area picked on the map (or the one the part you hold belongs to)
    private string? _zoneOfPart;    // the part the area was last taken from
    private string? _aimGroup;      // the area the camera keeps in the middle
    private string? _glowPick;      // the part that lights up under the mouse
    private List<string> _stack = new(); // the parts lying under the mouse, nearest first
    private DateTime _lastNudge;
    private bool _hatHidden;

    private void ShowOnly(IEnumerable<string> groups)
    {
        var list = groups.ToList();
        ShownGroups.Clear();
        foreach (var g in list)
            ShownGroups.Add(g);
        _plugin.SaveConfig();
    }

    // ---- the map ----

    private static void Oval(ImDrawListPtr draw, Vector2 centre, Vector2 radius, uint fill, uint line, float thick)
    {
        const int n = 26;
        for (var pass = 0; pass < 2; ++pass)
        {
            if (pass == 0 && fill == 0 || pass == 1 && line == 0)
                continue;
            draw.PathClear();
            for (var i = 0; i < n; ++i)
            {
                var a = i * MathF.Tau / n;
                draw.PathLineTo(centre + new Vector2(MathF.Cos(a) * radius.X, MathF.Sin(a) * radius.Y));
            }

            if (pass == 0)
                draw.PathFillConvex(fill);
            else
                draw.PathStroke(line, ImDrawFlags.Closed, thick);
        }
    }

    /// <summary> Draws one map and says which of its areas the mouse is on (null: none) and whether it was clicked. </summary>
    private (string? Hot, bool Clicked) DrawOneMap(string id, MapZone[] zones, Vector2 size, bool face, Dictionary<string, LiveBone> bones, float s)
    {
        var draw = ImGui.GetWindowDrawList();
        var min  = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked();
        var mouse   = ImGui.GetIO().MousePos;
        var hasTail = bones.Count == 0 || bones.ContainsKey("n_sippo_a");

        // the outline first: the head, or the trunk as a faint line figure
        var lineFaint = Theme.U32(Theme.Faint with { W = 0.55f });
        if (face)
            Oval(draw, min + size * new Vector2(0.5f, 0.52f), size * new Vector2(0.385f, 0.47f), Theme.U32(Theme.Card with { W = 0.9f }), lineFaint, 1.2f * s);

        string? hot = null;
        if (hovered)
        {
            // the smallest shape under the mouse wins (the nose lies on the cheeks' edge)
            var best = float.MaxValue;
            foreach (var z in zones)
            {
                if (z.Group == "tail" && !hasTail)
                    continue;
                var d = (mouse - (min + size * new Vector2(z.X, z.Y))) / (size * new Vector2(z.Rx, z.Ry) + new Vector2(2 * s));
                if (d.LengthSquared() <= 1f && z.Rx * z.Ry < best)
                {
                    best = z.Rx * z.Ry;
                    hot  = z.Group;
                }
            }

            // anywhere else on the head: the rest of the face
            if (hot is null && face)
            {
                var d = (mouse - (min + size * new Vector2(0.5f, 0.52f))) / (size * new Vector2(0.385f, 0.47f));
                if (d.LengthSquared() <= 1f)
                    hot = "face";
            }
        }

        foreach (var z in zones)
        {
            if (z.Group == "tail" && !hasTail)
                continue;
            var group = z.Group == "head" ? "face" : z.Group;
            var on    = z.Group == "head" ? FaceGroups.Any(ShownGroups.Contains) : ShownGroups.Contains(group);
            var isHot = hot == z.Group;
            var fill  = _zone == group && z.Group != "head" ? Theme.Accent with { W = 0.85f } : on ? Theme.AccentDeep with { W = 0.75f } : Theme.Panel with { W = 0.9f };
            Oval(draw, min + size * new Vector2(z.X, z.Y), size * new Vector2(z.Rx, z.Ry), Theme.U32(fill),
                Theme.U32(isHot ? new Vector4(1, 1, 1, 0.95f) : Theme.Faint with { W = 0.7f }), (isHot ? 2f : 1f) * s);
        }

        return (hot, clicked && hot is not null);
    }

    /// <summary> How wide the two maps are together. </summary>
    private static float MapWidth(float s)
        => MapHeight * s * (0.86f + 0.62f) + 12 * s;

    private const float MapHeight = 132f;

    private void DrawMap(Dictionary<string, LiveBone> bones, float s)
    {
        var height = MapHeight * s;
        var (faceHot, faceClick) = DrawOneMap("##facemap", FaceMap, new Vector2(height * 0.86f, height), true, bones, s);
        ImGui.SameLine(0, 12 * s);
        var (bodyHot, bodyClick) = DrawOneMap("##bodymap", BodyMap, new Vector2(height * 0.62f, height), false, bones, s);
        var hot = faceHot ?? bodyHot;
        if (hot is not null)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip((hot == "head" ? Loc.T("Whole face") : hot == "face" ? Loc.T("Rest of the face") : GroupLabel(hot)) + "\n"
              + Loc.T("Click: only this area, with its sliders (the camera goes there) · Ctrl+click: add or remove it"));
        }

        if (faceClick || bodyClick)
        {
            var add = ImGui.GetIO().KeyCtrl;
            if (hot == "head")
            {
                ShowOnly(FaceGroups.Where(g => g != "teeth"));
                _zone = "face";
            }
            else if (add)
            {
                if (!ShownGroups.Remove(hot!))
                    ShownGroups.Add(hot!);
                _plugin.SaveConfig();
                _zone = hot;
            }
            else
            {
                ShowOnly(new[] { hot! });
                _zone = hot;
            }

            _zoneOfPart = _selected;
            if (Config.FocusOnZone && !add)
                FocusOn(hot == "head" ? "face" : hot!, bones, true);
        }

    }

    // ---- the focus camera ----

    /// <summary> Which way the face looks and where the character's left is, level with the ground. </summary>
    private bool FaceDirections(Dictionary<string, LiveBone> bones, out Vector3 forward, out Vector3 left)
    {
        var front = 1f;
        if (bones.TryGetValue("j_kao", out var head) && (bones.TryGetValue("j_f_hana", out var nose) || bones.TryGetValue("j_f_memoto", out nose)))
            front = nose.Model.Z - head.Model.Z < 0 ? -1f : 1f;
        var leftSign = 1f;
        foreach (var name in new[] { "j_f_eye_l", "j_mimi_l", "j_ude_a_l", "j_asi_a_l" })
            if (bones.TryGetValue(name, out var b) && MathF.Abs(b.Model.X) > 0.001f)
            {
                leftSign = MathF.Sign(b.Model.X);
                break;
            }

        forward = Vector3.Transform(new Vector3(0, 0, front), _rootRotation) with { Y = 0 };
        left    = Vector3.Transform(new Vector3(leftSign, 0, 0), _rootRotation) with { Y = 0 };
        if (forward.LengthSquared() < 1e-6f || left.LengthSquared() < 1e-6f)
            return false;
        forward = Vector3.Normalize(forward);
        left    = Vector3.Normalize(left);
        return true;
    }

    /// <summary> Turns the camera to look at the character from this side: 0 front, a quarter turn is its left, half is the back. </summary>
    private void TurnView(float angle, Dictionary<string, LiveBone> bones)
    {
        var cam = _plugin.Camera;
        cam.Begin();
        if (!cam.On || !FaceDirections(bones, out var forward, out var left))
            return;
        var from = forward * MathF.Cos(angle) + left * MathF.Sin(angle);
        cam.TurnTo(MathF.Atan2(from.X, from.Z), 0.05f);
    }

    /// <summary> Sends the camera to an area: the view that shows it best, as close as suits it, the area in the middle. </summary>
    private void FocusOn(string group, Dictionary<string, LiveBone> bones, bool turn)
    {
        var cam = _plugin.Camera;
        cam.Begin();
        if (!cam.On)
            return;
        var (view, zoom) = group switch
        {
            "eyes"   => (0f, 4f),
            "brows"  => (0f, 3.5f),
            "nose"   => (0f, 4f),
            "lips"   => (0f, 4f),
            "teeth"  => (0f, 4f),
            "cheeks" => (0f, 3f),
            "jaw"    => (0f, 3f),
            "face"   => (0f, 2.4f),
            "ears"   => (0f, 2.4f),
            "tail"   => (MathF.PI, 1f),
            _        => (0f, 1f),
        };
        cam.Zoom = zoom;
        if (turn)
            TurnView(view, bones);
        if (zoom > 1.01f)
        {
            _aimGroup = group;
            cam.Centre();
        }
        else
        {
            _aimGroup = null;
            cam.Uncentre();
        }
    }

    /// <summary> Every frame while sculpting: tells the camera what the screen shows, so it can steer. </summary>
    private void CameraTick(Dictionary<string, LiveBone> bones)
    {
        var cam = _plugin.Camera;
        if (!cam.On)
            return;
        Vector3? away = null;
        if ((HeadCentre(bones) ?? (bones.TryGetValue("j_kao", out var head) ? head.World : null)) is { } centre && Camera(centre) is { } at)
            away = at.Away;

        float? aimY = null;
        if (_aimGroup is not null)
        {
            var sum   = Vector3.Zero;
            var count = 0;
            foreach (var bone in bones.Values)
                if (GroupOf(bone.Name) == _aimGroup || _aimGroup == "face" && IsFace(bone.Name))
                {
                    sum += bone.World;
                    count++;
                }

            if (count > 0)
            {
                var view    = ImGui.GetMainViewport();
                var inFront = _plugin.GameGui.WorldToScreen(sum / count, out var p, out _);
                if (inFront && view.Size.Y > 1)
                    aimY = Math.Clamp((p.Y - view.Pos.Y) / view.Size.Y, -1f, 2f);
            }
        }

        cam.Update(away, aimY);
    }

    private void DrawCameraControls(Dictionary<string, LiveBone> bones, float s)
    {
        var cam = _plugin.Camera;
        (string Label, float Angle, string Tip)[] views =
        {
            (Loc.T("Front"), 0f, Loc.T("Look at the face from the front.")),
            (Loc.T("¾"), MathF.PI / 4, Loc.T("Three-quarter view from your character's left. Right-click: from the right.")),
            (Loc.T("Side"), MathF.PI / 2, Loc.T("The profile, from your character's left. Right-click: from the right.")),
            (Loc.T("Back"), MathF.PI, Loc.T("From behind.")),
        };
        for (var i = 0; i < views.Length; ++i)
        {
            if (i > 0)
                ImGui.SameLine(0, 4 * s);
            if (ImGui.Button(views[i].Label + "##view" + i))
                TurnView(views[i].Angle, bones);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                TurnView(-views[i].Angle, bones);
            Tip(views[i].Tip);
        }

        ImGui.SameLine();
        var zoom = cam.Zoom;
        ImGui.SetNextItemWidth(Math.Max(90 * s, Math.Min(150 * s, ImGui.GetContentRegionAvail().X)));
        if (ImGui.SliderFloat("##focuszoom", ref zoom, 1f, 8f, zoom < 1.02f ? Loc.T("zoom: normal") : Loc.T("zoom %.1f ×")))
        {
            cam.Begin();
            cam.Zoom = zoom;
            if (cam.On && zoom > 1.01f && _aimGroup is null)
                _aimGroup = _zone is not null && FaceGroups.Contains(_zone) ? _zone : "face";
        }

        if (ImGui.IsItemDeactivatedAfterEdit() && cam.On)
        {
            if (cam.Zoom > 1.01f)
                cam.Centre();
            else
            {
                _aimGroup = null;
                cam.Uncentre();
            }
        }

        Tip(Loc.T("Closer than the game's closest view (the lens gets narrower, the camera stays where it is, so nothing is cut off). The area you picked is brought to the middle of the screen."));

        var follow = Config.FocusOnZone;
        if (ImGui.Checkbox(Loc.T("Go to the area I pick"), ref follow))
        {
            Config.FocusOnZone = follow;
            _plugin.SaveConfig();
        }

        Tip(Loc.T("Clicking an area on the map turns the camera to it and zooms in. Untick it to keep the camera where you put it."));
        ImGui.SameLine();
        var hat = _hatHidden;
        if (ImGui.Checkbox(Loc.T("Hide my hat"), ref hat))
            SetHatHidden(hat);
        Tip(Loc.T("Takes your headgear off on your screen while you sculpt (like the game's own setting). It comes back when you stop."));

        if (cam.On)
        {
            if (ImGui.SmallButton(Loc.T("Normal camera") + "##camoff"))
            {
                _aimGroup = null;
                cam.End();
            }

            Tip(Loc.T("Zoom and view height go back to how the game had them."));
        }

        ImGui.TextColored(Theme.Faint, Loc.T("You keep turning and zooming with the mouse as always. Over the face the mouse grabs parts: hold Alt there to turn the camera."));
        if (Config.TestLine)
            ImGui.TextColored(Theme.Faint, "camera: " + cam.State);
    }

    private bool _hatWasHidden;

    /// <summary> Hides the headgear on your screen, or puts it back as it was before (not if you had it hidden yourself). </summary>
    private unsafe void SetHatHidden(bool hidden)
    {
        if (hidden == _hatHidden)
            return;
        var address = Sculptor.Address != nint.Zero ? Sculptor.Address : _plugin.Objects[0]?.Address ?? nint.Zero;
        if (address == nint.Zero)
        {
            _hatHidden = false;
            return;
        }

        try
        {
            var data = &((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address)->DrawData;
            if (hidden)
            {
                _hatWasHidden = data->IsHatHidden;
                if (!_hatWasHidden)
                    data->HideHeadgear(0, true);
            }
            else if (!_hatWasHidden)
                data->HideHeadgear(0, false);

            _hatHidden = hidden;
        }
        catch (Exception ex)
        {
            _hatHidden = false;
            _plugin.Log.Warning($"Sculpt+: could not {(hidden ? "hide" : "show")} the headgear: {ex.Message}");
        }
    }

    // ---- the sliders of the picked area ----

    private void DrawZone(Dictionary<string, LiveBone> bones, float s)
    {
        // holding a part picks its area too
        if (_selected != _zoneOfPart)
        {
            _zoneOfPart = _selected;
            if (_selected is not null)
                _zone = GroupOf(_selected);
        }

        if (_zone is null)
        {
            ImGui.TextColored(Theme.Muted, Loc.T("Click an area on the map above (or a part on the character): its sliders show here."));
            return;
        }

        var sliders = Sculptor.Sliders.Where(x => Sculptor.ZoneOf(x.Key) == _zone && (bones.Count == 0 || bones.Keys.Any(x.Has))).ToList();
        ImGui.TextColored(Theme.Accent, GroupLabel(_zone));
        if (_plugin.Camera.On || Config.FocusOnZone)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Look at it") + "##zonelook"))
                FocusOn(_zone, bones, true);
            Tip(Loc.T("Turns the camera to this area and zooms in."));
        }

        if (sliders.Count == 0)
        {
            ImGui.TextColored(Theme.Faint, Loc.T("No sliders for this area: shape it by hand on the character."));
            return;
        }

        if (sliders.Any(x => MathF.Abs(Sculptor.SliderValue(x.Key)) > 0.001f))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("All to the middle") + "##zonereset"))
            {
                Sculptor.Checkpoint();
                foreach (var slider in sliders)
                    Sculptor.SetSlider(slider.Key, 0);
            }
        }

        foreach (var slider in sliders)
        {
            var v = Sculptor.SliderValue(slider.Key);
            ImGui.SetNextItemWidth(-170 * s);
            if (ImGui.SliderFloat(SliderLabel(slider.Key) + "##zs" + slider.Key, ref v, -1.5f, 1.5f, MathF.Abs(v) < 0.005f ? "·" : "%+.2f"))
                Sculptor.SetSlider(slider.Key, v);
            if (ImGui.IsItemActivated())
                Sculptor.Checkpoint();
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                Sculptor.SetSlider(slider.Key, 0);
        }

        ImGui.TextColored(Theme.Faint, Loc.T("Both sides together. Ctrl+click a slider to type a number; double-click sets it back."));
    }

    // ---- tiny steps for the part you hold ----

    private void DrawNudge(LiveBone bone, Dictionary<string, LiveBone> bones, float s)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, Loc.T("Tiny steps:"));
        var dir = Vector2.Zero;
        var io  = ImGui.GetIO();
        var key = io.KeyAlt && !io.KeyCtrl && !io.WantTextInput;
        (FontAwesomeIcon Icon, Vector2 Way, ImGuiKey Key)[] arrows =
        {
            (FontAwesomeIcon.ArrowLeft, new Vector2(-1, 0), ImGuiKey.LeftArrow),
            (FontAwesomeIcon.ArrowUp, new Vector2(0, -1), ImGuiKey.UpArrow),
            (FontAwesomeIcon.ArrowDown, new Vector2(0, 1), ImGuiKey.DownArrow),
            (FontAwesomeIcon.ArrowRight, new Vector2(1, 0), ImGuiKey.RightArrow),
        };
        foreach (var (icon, way, k) in arrows)
        {
            ImGui.SameLine(0, 3 * s);
            if (Icons.Button(icon, "nudge" + k) || key && ImGui.IsKeyPressed(k, true))
                dir = way;
        }

        Tip(Loc.T("Moves the part you hold one small step, as you see it on screen. Alt + arrow keys do the same."));
        ImGui.SameLine();
        var step = Config.NudgeStep;
        ImGui.SetNextItemWidth(90 * s);
        if (ImGui.SliderFloat("##nudgestep", ref step, 0.05f, 2f, "%.2f mm"))
            Config.NudgeStep = step;
        if (ImGui.IsItemDeactivatedAfterEdit())
            _plugin.SaveConfig();
        Tip(Loc.T("How far one step goes."));

        if (dir == Vector2.Zero || Camera(bone.World) is not { } cam)
            return;
        var way3 = cam.Pixels(dir * 10f);
        if (way3.LengthSquared() < 1e-12f)
            return;
        if ((DateTime.UtcNow - _lastNudge).TotalSeconds > 0.8)
            Sculptor.Checkpoint();
        _lastNudge = DateTime.UtcNow;
        Drag(bone, ToModel(Vector3.Normalize(way3) * (Math.Clamp(Config.NudgeStep, 0.05f, 2f) / 1000f)), bones, 0);
    }

    // ---- the glow ----

    /// <summary> A soft light on a spot: rings of the same faint colour on top of each other, brightest in the middle. </summary>
    private static void Halo(ImDrawListPtr draw, Vector2 at, float radius, Vector4 colour, float strength)
    {
        const int rings = 8;
        var each = Theme.U32(colour with { W = Math.Clamp(strength / rings * 1.6f, 0.01f, 0.4f) });
        for (var i = 0; i < rings; ++i)
            draw.AddCircleFilled(at, radius * (1f - i / (float)rings), each, 28);
    }

    /// <summary>
    /// Glow mode: which part the mouse is on. Parts that lie on top of each other (the iris in the eye) form a stack; the
    /// one that lit up stays lit while the mouse stays on the stack, and Ctrl + wheel goes to the next one in it.
    /// </summary>
    private (LiveBone Bone, Vector2 At)? PickGlow(List<(LiveBone Bone, Vector2 At)> shown, Vector2 mouse, float s)
    {
        var io    = ImGui.GetIO();
        var reach = 22f * s;
        var near  = shown.Select(x => (x.Bone, x.At, Distance: Vector2.Distance(x.At, mouse)))
            .Where(x => x.Distance < reach).OrderBy(x => x.Distance).ToList();
        if (near.Count == 0)
        {
            _stack.Clear();
            _glowPick = null;
            return null;
        }

        var stack = near.Where(x => x.Distance <= near[0].Distance + 10f * s).Take(8).ToList();
        var index = stack.FindIndex(x => x.Bone.Name == _glowPick);
        if (index < 0)
            index = 0;
        if (io.KeyCtrl && io.MouseWheel != 0 && stack.Count > 1)
            index = ((index + (io.MouseWheel > 0 ? -1 : 1)) % stack.Count + stack.Count) % stack.Count;
        _glowPick = stack[index].Bone.Name;
        _stack    = stack.Select(x => x.Bone.Name).ToList();
        return (stack[index].Bone, stack[index].At);
    }

    /// <summary> What the label next to the mouse says about the lit part and the ones underneath. </summary>
    private string GlowLabel(string name)
    {
        var text = Label(name);
        if (_stack.Count > 1)
            text += "\n" + string.Join("  ·  ", _stack.Select(n => n == name ? "[" + Label(n) + "]" : Label(n))) + "\n" + Loc.T("Ctrl + wheel: the next part underneath");
        return text;
    }
}
