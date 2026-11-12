using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace SculptPlus;

/// <summary> The dots on the character: which ones show, how they are drawn, and what the mouse does with them. </summary>
public sealed partial class SculptWindow
{
    private static readonly string[] FaceGroups = { "lips", "eyes", "brows", "nose", "cheeks", "jaw", "teeth", "face" };
    private static readonly string[] BodyGroups = { "ears", "chest", "torso", "arms", "hands", "legs", "feet", "tail" };
    private static readonly string[] OtherGroups = { "hair", "clothes", "other" };

    /// <summary> Which filter a bone belongs to (by what it is called and the part of the body Customize+ files it under). </summary>
    private static string GroupOf(string bone)
    {
        var family = SculptBones.Info(bone)?.Family;
        if (family is null or "Unknown")
            return "other";
        if (bone.Contains("haguki", StringComparison.Ordinal) || family == "Tongue")
            return "teeth";
        switch (family)
        {
            case "Lips":   return "lips";
            case "Eyes":   return "eyes";
            case "Cheeks": return "cheeks";
            case "Jaw":    return "jaw";
            case "Ears":   return "ears";
            case "Chest":  return "chest";
            case "Arms":   return "arms";
            case "Hands":  return bone is "j_te_l" or "j_te_r" ? "arms" : "hands";
            case "Legs":   return "legs";
            case "Feet":   return "feet";
            case "Tail":   return "tail";
            case "Hair":   return "hair";
            case "Spine" or "Root" or "Groin": return "torso";
            case "Earrings" or "Hat" or "Cape" or "Armor" or "Skirt" or "Equipment": return "clothes";
        }

        // the older face bones are all filed under "Face"
        if (bone.Contains("lip", StringComparison.Ordinal))
            return "lips";
        if (bone.Contains("mayu", StringComparison.Ordinal) || bone.Contains("miken", StringComparison.Ordinal))
            return "brows";
        if (bone.Contains("eye", StringComparison.Ordinal) || bone.Contains("mab", StringComparison.Ordinal))
            return "eyes";
        if (bone.Contains("hana", StringComparison.Ordinal) || bone.Contains("memoto", StringComparison.Ordinal))
            return "nose";
        if (bone.Contains("hoho", StringComparison.Ordinal))
            return "cheeks";
        if (bone.Contains("ago", StringComparison.Ordinal))
            return "jaw";
        return "face";
    }

    private static string GroupLabel(string group)
        => group switch
        {
            "lips"    => Loc.T("Lips"),
            "eyes"    => Loc.T("Eyes"),
            "brows"   => Loc.T("Brows"),
            "nose"    => Loc.T("Nose"),
            "cheeks"  => Loc.T("Cheeks"),
            "jaw"     => Loc.T("Jaw"),
            "teeth"   => Loc.T("Teeth, tongue"),
            "face"    => Loc.T("Rest of the face"),
            "ears"    => Loc.T("Ears"),
            "chest"   => Loc.T("Chest"),
            "torso"   => Loc.T("Torso, head"),
            "arms"    => Loc.T("Arms"),
            "hands"   => Loc.T("Fingers"),
            "legs"    => Loc.T("Legs"),
            "feet"    => Loc.T("Feet"),
            "tail"    => Loc.T("Tail"),
            "hair"    => Loc.T("Hair"),
            "clothes" => Loc.T("Clothes"),
            _         => Loc.T("Everything else"),
        };

    private HashSet<string> ShownGroups => Config.Dots;

    /// <summary> Where a dot may go when another part's dot is in its exact place: below, above, then to the sides. </summary>
    private static readonly Vector2[] Beside = { new(0, 1), new(0, -1), new(1, 0), new(-1, 0), new(1, 1), new(-1, 1), new(1, -1), new(-1, -1) };

    private bool Shown(string bone)
        => ShownGroups.Contains(GroupOf(bone)) && !Config.HiddenDots.Contains(bone);

    /// <summary> Hides or shows the dot of one part (and of its twin on the other side while both sides go together). </summary>
    private void SetDotHidden(string bone, bool hidden)
    {
        foreach (var name in new[] { bone, Sculptor.Symmetry ? SculptBones.MirrorOf(bone) : null })
        {
            if (name is null)
                continue;
            if (hidden)
                Config.HiddenDots.Add(name);
            else
                Config.HiddenDots.Remove(name);
        }

        _plugin.SaveConfig();
    }

    private static bool IsFace(string bone)
        => Array.IndexOf(FaceGroups, GroupOf(bone)) >= 0;

    private static string Label(string bone)
        => SculptBones.Info(bone)?.Display ?? bone;

    /// <summary> How parts show (glow or dots), the map to pick an area, and every area as a button for the rest. </summary>
    private void DrawDotFilter(Dictionary<string, LiveBone> bones, float s)
    {
        DrawMap(bones, s);

        // next to the maps when there is room, else below them: how parts show, and the quick choices
        var glowLabel = Loc.T("a glow under the mouse");
        var need      = ImGui.CalcTextSize(glowLabel).X + 40 * s;
        if (ImGui.GetContentRegionAvail().X - MapWidth(s) - 12 * s >= need)
            ImGui.SameLine(0, 12 * s);
        ImGui.BeginGroup();
        ImGui.TextColored(Theme.Muted, Loc.T("Parts show as:"));
        if (ImGui.RadioButton(glowLabel + "##glow", Config.Glow))
        {
            Config.Glow = true;
            _plugin.SaveConfig();
        }

        Tip(Loc.T("Nothing lies on the character. The part under the mouse lights up, with its name; where parts lie on top of each other, Ctrl + wheel goes through them. What you changed keeps a faint shimmer."));
        if (ImGui.RadioButton(Loc.T("dots") + "##dots", !Config.Glow))
        {
            Config.Glow = false;
            _plugin.SaveConfig();
        }

        Tip(Loc.T("Every part has a dot on the character, all visible at once."));
        if (ImGui.SmallButton(Loc.T("Face") + "##dotsface"))
            ShowOnly(FaceGroups.Where(g => g != "teeth"));
        Tip(Loc.T("Only the face (without teeth and tongue)."));
        ImGui.SameLine(0, 4 * s);
        if (ImGui.SmallButton(Loc.T("Body") + "##dotsbody"))
            ShowOnly(BodyGroups.Where(g => g != "hands"));
        Tip(Loc.T("Only the body (without fingers)."));
        if (ImGui.SmallButton(Loc.T("Both") + "##dotsboth"))
            ShowOnly(FaceGroups.Concat(BodyGroups).Where(g => g is not ("teeth" or "hands")));
        ImGui.SameLine(0, 4 * s);
        if (ImGui.SmallButton(Loc.T("None") + "##dotsnone"))
            ShowOnly(Array.Empty<string>());
        if (Theme.Button(Loc.T("Workbench"), new Vector2(0, 0), _benchOpen ? Theme.Kind.Primary : Theme.Kind.Secondary, Dalamud.Interface.FontAwesomeIcon.Cube, true, "##bench"))
        {
            if (_benchOpen)
                CloseWorkbench();
            else
                _benchOpen = true;
        }

        Tip(Loc.T("A window with your head as a plain clay model: turn it freely, come as close as you like, and take hold of the skin itself. Your character in the game changes along."));
        ImGui.EndGroup();

        var all = ImGui.TreeNodeEx(Loc.T("Every area as a button") + "###allareas", ImGuiTreeNodeFlags.SpanAvailWidth);
        Tip(Loc.T("Also the areas that are not on the map: teeth and tongue, hair, clothes, and what has no name."));
        if (all)
        {
            var width = ImGui.GetContentRegionAvail().X;
            var x     = 0f;
            foreach (var group in FaceGroups.Concat(BodyGroups).Concat(OtherGroups))
            {
                var label = GroupLabel(group);
                var w     = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + 6 * s;
                if (x > 0 && x + w > width)
                    x = 0;
                else if (x > 0)
                    ImGui.SameLine(0, 4 * s);
                x += w + 4 * s;

                var on = ShownGroups.Contains(group);
                ImGui.PushStyleColor(ImGuiCol.Button, on ? Theme.AccentDeep : Theme.Card);
                ImGui.PushStyleColor(ImGuiCol.Text, on ? new Vector4(1, 1, 1, 1) : Theme.Muted);
                if (ImGui.SmallButton(label + "##dots" + group))
                {
                    if (!ShownGroups.Remove(group))
                        ShownGroups.Add(group);
                    _plugin.SaveConfig();
                }

                ImGui.PopStyleColor(2);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    ShowOnly(new[] { group });
            }

            ImGui.TextColored(Theme.Faint, Loc.T("right-click a part: only that one"));
            ImGui.TreePop();
        }

        // single dots you hid
        if (Config.HiddenDots.Count > 0)
        {
            ImGui.TextColored(Theme.Faint, Loc.F("{0} single dot(s) hidden", Config.HiddenDots.Count));
            Tip(string.Join("\n", Config.HiddenDots.Select(Label).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(40)));
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Show them again") + "##unhidedots"))
            {
                Config.HiddenDots.Clear();
                _plugin.SaveConfig();
            }

            Tip(Loc.T("To bring back just one: find it by name under \"The part you hold\" and click \"Show its dot again\"."));
        }
    }

    /// <summary>
    /// How many pixels a metre covers at a point (a perspective camera draws near things larger): comparing two points
    /// says which one is nearer to you, without knowing anything about the camera.
    /// </summary>
    private float PixelsPerMetre(Vector3 at)
    {
        var gui = _plugin.GameGui;
        const float e = 0.03f;
        gui.WorldToScreen(at, out var p0);
        gui.WorldToScreen(at + new Vector3(e, 0, 0), out var px);
        gui.WorldToScreen(at + new Vector3(0, e, 0), out var py);
        gui.WorldToScreen(at + new Vector3(0, 0, e), out var pz);
        return MathF.Sqrt(Vector2.DistanceSquared(px, p0) + Vector2.DistanceSquared(py, p0) + Vector2.DistanceSquared(pz, p0)) / e;
    }

    /// <summary> A point inside the head, for telling the near half of the face from the far half (null: no head bone). </summary>
    private static Vector3? HeadCentre(Dictionary<string, LiveBone> bones)
    {
        if (!bones.TryGetValue("j_kao", out var head))
            return null;
        var sum   = Vector3.Zero;
        var count = 0;
        foreach (var bone in bones.Values)
            if (IsFace(bone.Name))
            {
                sum += bone.World;
                count++;
            }

        if (count < 4)
            return null;
        var face = sum / count;
        // The face bones sit on the front of the face, the head bone further back at the base of the skull: a point at the
        // height of the face, a hand's width behind it, is inside the head whichever way it is turned.
        var back = (head.World - face) with { Y = 0 };
        if (back.LengthSquared() < 1e-6f)
            return null;
        var depth = MathF.Max(Vector3.Distance(head.World, face) * 0.75f, 0.03f);
        return face + Vector3.Normalize(back) * depth;
    }

    private enum GrabKind
    {
        Dot,
        Arrow,
        Ring,
    }

    private GrabKind _dragKind;
    private int _dragAxis;

    private static readonly Vector4[] AxisColours = { new(0.96f, 0.38f, 0.44f, 1f), new(0.47f, 0.86f, 0.52f, 1f), new(0.42f, 0.63f, 1f, 1f) };
    private static readonly Vector3[] Axes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };

    private static string AxisName(int axis)
        => axis switch
        {
            0 => Loc.T("left and right"),
            1 => Loc.T("up and down"),
            _ => Loc.T("front and back"),
        };

    /// <summary>
    /// The arrows and rings on the part you hold: three directions of the model (side, up, front), each with an arrow to
    /// move along it and a ring to turn around it. Where things are on screen, for drawing and for the mouse.
    /// </summary>
    private sealed class Gizmo
    {
        public LiveBone Bone;
        public Vector2 Centre;
        public float Length;                       // of an arrow, in metres
        public readonly Vector3[] World = new Vector3[3]; // the three directions in the world
        public readonly Vector2[] Tip = new Vector2[3];
        public readonly bool[] TipUsable = new bool[3];
        public readonly Vector2[][] Ring = new Vector2[3][];
    }

    private Gizmo? BuildGizmo(LiveBone bone, float s)
    {
        var gui = _plugin.GameGui;
        if (!gui.WorldToScreen(bone.World, out var centre))
            return null;
        var perMetre = PixelsPerMetre(bone.World);
        if (perMetre < 1f)
            return null;
        var gizmo = new Gizmo { Bone = bone, Centre = centre, Length = Math.Clamp(70f * s * 1.5f / perMetre, 0.004f, 0.8f) };
        for (var a = 0; a < 3; ++a)
            gizmo.World[a] = Vector3.Transform(Axes[a], _rootRotation);
        for (var a = 0; a < 3; ++a)
        {
            gui.WorldToScreen(bone.World + gizmo.World[a] * gizmo.Length, out gizmo.Tip[a]);
            // an arrow that points at you (or away) is a stub on screen: shown faintly, not for grabbing
            gizmo.TipUsable[a] = Vector2.Distance(gizmo.Tip[a], centre) >= 16f * s;

            // the ring around this direction lies in the plane of the other two; going round it is a turn the right-handed way
            var u = gizmo.World[(a + 1) % 3];
            var v = gizmo.World[(a + 2) % 3];
            const int steps = 40;
            var ring = new Vector2[steps + 1];
            for (var i = 0; i <= steps; ++i)
            {
                var angle = i * MathF.Tau / steps;
                gui.WorldToScreen(bone.World + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * gizmo.Length * 0.72f, out ring[i]);
            }

            gizmo.Ring[a] = ring;
        }

        return gizmo;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t  = ab.LengthSquared() < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary> The ring vertex nearest to a point (and how far it is). </summary>
    private static (int Index, float Distance) NearestOnRing(Vector2[] ring, Vector2 p)
    {
        var best  = 0;
        var bestD = float.MaxValue;
        for (var i = 0; i + 1 < ring.Length; ++i)
        {
            var d = DistanceToSegment(p, ring[i], ring[i + 1]);
            if (d < bestD)
            {
                bestD = d;
                best  = i;
            }
        }

        return (best, bestD);
    }

    private void DrawHandles(Dictionary<string, LiveBone> bones, float s)
    {
        if (bones.Count == 0)
            return;
        var gui    = _plugin.GameGui;
        var draw   = ImGui.GetBackgroundDrawList();
        var io     = ImGui.GetIO();
        var mouse  = io.MousePos;
        var radius = 5f * s * Math.Clamp(Config.DotSize, 0.5f, 2.5f);
        var glow   = Config.Glow;

        // the far half of the face is left out (you would see those dots through the head)
        // (told by direction, not by depth: a part counts as "far" when, seen from the middle of the head, it points
        // clearly away from you. Parts that lie deep in the head, like the eyes, would otherwise come and go as you turn.)
        var centre = Config.HideFarSide ? HeadCentre(bones) : null;
        var away   = centre is { } c ? Camera(c)?.Away : null;

        // which dots, and where on screen; the held and the changed ones first so they are never hidden by a neighbour
        var shown = new List<(LiveBone Bone, Vector2 At)>();
        foreach (var bone in bones.Values
                     .Where(b => b.Name == _dragging || b.Name == _selected || Shown(b.Name))
                     .OrderByDescending(b => b.Name == _dragging || b.Name == _selected)
                     .ThenByDescending(b => Sculptor.IsEdited(b.Name))
                     .ThenBy(b => b.Name.Length)
                     .Select(AtSkin))
        {
            if (!gui.WorldToScreen(bone.World, out var at))
                continue;
            var held = bone.Name == _dragging || bone.Name == _selected;
            if (!held && centre is { } mid && away is { } dir && IsFace(bone.Name))
            {
                var out_ = bone.World - mid;
                if (out_.LengthSquared() > 1e-8f && Vector3.Dot(Vector3.Normalize(out_), dir) > 0.35f)
                    continue;
            }

            // A dot that would lie on top of another one is put next to it, so no part goes missing as you turn the
            // camera; only when there is no room around it at all is it left out until you zoom in.
            var gap = (radius * 1.7f) * (radius * 1.7f);
            if (!held && !glow && shown.Any(o => Vector2.DistanceSquared(o.At, at) < gap))
            {
                var placed = false;
                for (var ring = 1; ring <= 2 && !placed; ++ring)
                    foreach (var step in Beside)
                    {
                        var spot = at + step * radius * 1.8f * ring;
                        if (shown.Any(o => Vector2.DistanceSquared(o.At, spot) < gap))
                            continue;
                        at     = spot;
                        placed = true;
                        break;
                    }

                if (!placed)
                    continue;
            }

            shown.Add((bone, at));
        }

        // arrows and rings on the part you hold (not while you drag its dot freely: they would be in the way)
        Gizmo? gizmo = null;
        if (Config.Arrows && _selected is not null && bones.TryGetValue(_selected, out var selected) && !(_dragging is not null && _dragKind == GrabKind.Dot))
            gizmo = BuildGizmo(AtSkin(selected), s);

        // what is under the mouse (not through another window)
        (GrabKind Kind, int Axis, LiveBone Bone, Vector2 At)? target = null;
        if (_dragging is not null)
        {
            if (_dragKind == GrabKind.Dot)
            {
                foreach (var x in shown)
                    if (x.Bone.Name == _dragging)
                        target = (GrabKind.Dot, 0, x.Bone, x.At);
            }
            else if (gizmo is not null && gizmo.Bone.Name == _dragging)
                target = (_dragKind, _dragAxis, gizmo.Bone, mouse);
        }
        else
        {
            // Over another window (this panel, another plugin's window) the dots are not for clicking.
            var overWindow = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow | ImGuiHoveredFlags.AllowWhenBlockedByPopup | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            // The window under the mouse may be our own little grab window from the last frame: that one counts as "the
            // game" (asking whether its button was hovered comes a frame too late: the dots would flicker and take no click).
            var mine    = _grabHovered || _grabMin is { } gm && mouse.X >= gm.X && mouse.Y >= gm.Y && mouse.X <= gm.X + _grabSize.X && mouse.Y <= gm.Y + _grabSize.Y;
            var free    = !overWindow || mine;
            var nearest = float.MaxValue;

            // the arrows and rings come first: they lie on top of the dots around the part you hold
            if (free && gizmo is not null && Vector2.Distance(mouse, gizmo.Centre) > radius * 1.8f)
            {
                var best = float.MaxValue;
                for (var a = 0; a < 3; ++a)
                {
                    if (gizmo.TipUsable[a])
                    {
                        var d = MathF.Min(Vector2.Distance(mouse, gizmo.Tip[a]) - 5f * s, DistanceToSegment(mouse, gizmo.Centre, gizmo.Tip[a]));
                        if (d < 5f * s && d < best)
                        {
                            best   = d;
                            target = (GrabKind.Arrow, a, gizmo.Bone, mouse);
                        }
                    }

                    var ring = NearestOnRing(gizmo.Ring[a], mouse).Distance;
                    if (ring < 4.5f * s && ring < best - 1f)
                    {
                        best   = ring;
                        target = (GrabKind.Ring, a, gizmo.Bone, mouse);
                    }
                }
            }

            if (target is null && glow)
            {
                // nothing is drawn on the character until the mouse is on a part: that one lights up
                if (free && PickGlow(shown, mouse, s) is { } lit)
                    target = (GrabKind.Dot, 0, lit.Bone, lit.At);
                else if (!free)
                    _glowPick = null;
            }
            else if (target is null)
            {
                var best = (radius * 2.6f) * (radius * 2.6f);
                foreach (var x in shown)
                {
                    var dist = Vector2.DistanceSquared(x.At, mouse);
                    nearest = MathF.Min(nearest, dist);
                    if (dist < best && free)
                    {
                        best   = dist;
                        target = (GrabKind.Dot, 0, x.Bone, x.At);
                    }
                }
            }

            if (Config.TestLine)
                _test = $"mouse {mouse.X:0},{mouse.Y:0} · nearest dot {MathF.Sqrt(nearest):0} px · over a window: {(overWindow ? "yes" : "no")} · wants mouse: {(io.WantCaptureMouse ? "yes" : "no")} · grab: {(_grabHovered ? "yes" : "no")} · dots: {shown.Count}";
        }

        var brush = Config.Brush;
        foreach (var (bone, at) in shown)
        {
            var hot    = target is { Kind: GrabKind.Dot } t && t.Bone.Name == bone.Name;
            var edited = Sculptor.IsEdited(bone.Name);
            if (glow)
            {
                // only the part under the mouse lights up; what you changed keeps a faint shimmer, what you hold a thin ring
                if (hot)
                {
                    Halo(draw, at, Math.Clamp(PixelsPerMetre(bone.World) * 0.011f, 13f * s, 70f * s), Theme.Accent, 0.55f);
                    draw.AddCircleFilled(at, 2.2f * s, Theme.U32(new Vector4(1, 1, 1, 0.9f)));
                }
                else if (edited)
                    Halo(draw, at, 7f * s, Sculptor.AtLimit(bone.Name) ? Theme.Warn : Theme.Good, 0.3f);

                if (bone.Name == _selected)
                    draw.AddCircle(at, 6f * s, Theme.U32(Theme.Accent with { W = 0.9f }), 0, 1.5f * s);
                if (Sculptor.Locked.Contains(bone.Name) && (hot || bone.Name == _selected))
                    draw.AddCircle(at, 9f * s, Theme.U32(Theme.Warn), 0, 1.5f * s);
                if (brush > 0 && target is { } lit && lit.Bone.Name != bone.Name && BrushWeight(lit.Bone, bone, brush) is var weight and > 0)
                    Halo(draw, at, 9f * s, Theme.Accent, 0.25f * weight);
                continue;
            }

            var colour = bone.Name == _selected ? Theme.Accent : edited && Sculptor.AtLimit(bone.Name) ? Theme.Warn : edited ? Theme.Good : new Vector4(1, 1, 1, 0.75f);
            draw.AddCircleFilled(at, hot ? radius * 1.5f : radius, Theme.U32(new Vector4(0, 0, 0, 0.55f)));
            draw.AddCircleFilled(at, hot ? radius * 1.2f : radius * 0.7f, Theme.U32(colour));
            if (Sculptor.Locked.Contains(bone.Name))
                draw.AddCircle(at, radius * 1.6f, Theme.U32(Theme.Warn), 0, 1.5f * s);
            // what the brush would take along
            if (brush > 0 && target is { } g && g.Bone.Name != bone.Name && BrushWeight(g.Bone, bone, brush) > 0)
                draw.AddCircle(at, radius * 1.3f, Theme.U32(Theme.Accent with { W = 0.6f }), 0, 1f * s);
        }

        if (gizmo is not null)
            DrawGizmo(draw, gizmo, target is { Kind: not GrabKind.Dot } hotPart ? (hotPart.Kind, hotPart.Axis) : null, s);

        _grabHovered = false;
        if (target is not { } grab)
        {
            _dragging = null;
            _grabMin  = null;
            return;
        }

        // a tiny window under the mouse takes the click, so the game does not turn the camera or target something
        var box = new Vector2(MathF.Max(radius * 4.4f, (glow ? 30f : 18f) * s));
        var pos = _dragging is not null || grab.Kind != GrabKind.Dot || glow ? mouse - box / 2 : grab.At - box / 2;
        _grabMin  = pos;
        _grabSize = box;
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(box);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        if (ImGui.Begin("##sculptplusgrab",
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
              | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.InvisibleButton("##grab", box, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            _grabHovered = ImGui.IsItemHovered() || ImGui.IsItemActive();
            var name = grab.Bone.Name;
            if (ImGui.IsItemActivated())
            {
                var left = ImGui.IsMouseDown(ImGuiMouseButton.Left);
                if (grab.Kind == GrabKind.Dot || left)
                {
                    _lastAction  = "pressed " + name;
                    _selected    = name;
                    _dragging    = name;
                    _dragKind    = grab.Kind;
                    _dragAxis    = grab.Axis;
                    _turning     = grab.Kind == GrabKind.Dot && !left;
                    _rightTravel = 0;
                    if (!_turning)
                        Sculptor.Checkpoint();
                }
            }

            var held = _turning ? ImGui.IsMouseDown(ImGuiMouseButton.Right) : ImGui.IsMouseDown(ImGuiMouseButton.Left);
            if (_dragging == name && ImGui.IsItemActive() && held)
            {
                // Ctrl: a tenth of the way, for the last bit
                var delta = io.KeyCtrl ? io.MouseDelta * 0.1f : io.MouseDelta;
                if (delta == Vector2.Zero)
                {
                }
                else if (_dragKind == GrabKind.Arrow && gizmo is not null)
                {
                    // along the arrow only: the part of the mouse's way that runs along it on screen
                    var along = gizmo.Tip[_dragAxis] - gizmo.Centre;
                    if (along.LengthSquared() > 1f)
                    {
                        var metres = Vector2.Dot(delta, along) / along.LengthSquared() * gizmo.Length;
                        var move   = ToModel(gizmo.World[_dragAxis] * metres);
                        _lastAction = $"moved {name} {metres * 1000:0.0} mm {AxisName(_dragAxis)}";
                        Drag(grab.Bone, move, bones, brush);
                    }
                }
                else if (_dragKind == GrabKind.Ring && gizmo is not null)
                {
                    // round the ring: the mouse's way along the ring where you are, as an angle
                    var ring    = gizmo.Ring[_dragAxis];
                    var at      = NearestOnRing(ring, mouse).Index;
                    var tangent = ring[at + 1] - ring[at];
                    var reach   = MathF.Max(Vector2.Distance(mouse, gizmo.Centre), 12f * s);
                    if (tangent.LengthSquared() > 1e-4f)
                    {
                        var angle = Vector2.Dot(delta, Vector2.Normalize(tangent)) / reach;
                        _lastAction = $"turned {name} {angle * 180 / MathF.PI:0.0}° around {AxisName(_dragAxis)}";
                        Sculptor.TurnModel(grab.Bone, Axes[_dragAxis], angle);
                    }
                }
                else if (_dragKind == GrabKind.Dot && Camera(grab.Bone.World) is { } cam)
                {
                    if (_turning)
                    {
                        // a click that does not travel is "put back"; the turn only starts after a few pixels
                        if (_rightTravel < 4f && _rightTravel + delta.Length() >= 4f)
                            Sculptor.Checkpoint();
                        _rightTravel += delta.Length();
                        if (_rightTravel >= 4f)
                            Sculptor.TurnModel(grab.Bone, ToModel(cam.Away), delta.X * 0.01f);
                    }
                    else if (io.KeyShift)
                    {
                        // mouse up: further away from you; mouse down: nearer
                        Drag(grab.Bone, ToModel(cam.Away) * (-delta.Y * 0.0004f), bones, brush);
                    }
                    else
                    {
                        var move = ToModel(cam.Pixels(delta));
                        _lastAction = $"dragged {name} {move.Length() * 1000:0.0} mm";
                        Drag(grab.Bone, move, bones, brush);
                    }
                }
            }
            else if (_dragging == name)
            {
                if (_dragKind == GrabKind.Dot && _turning && _rightTravel < 4f)
                {
                    Sculptor.Checkpoint();
                    Sculptor.ResetBone(name);
                }

                _dragging = null;
            }

            if (ImGui.IsItemHovered())
            {
                var wheel = io.MouseWheel;
                if (wheel != 0 && grab.Kind != GrabKind.Ring && !io.KeyCtrl)
                {
                    if ((DateTime.UtcNow - _lastWheelCheckpoint).TotalSeconds > 0.8)
                        Sculptor.Checkpoint();
                    _lastWheelCheckpoint = DateTime.UtcNow;
                    _selected            = name;
                    if (grab.Kind == GrabKind.Arrow)
                        Sculptor.ScaleModel(grab.Bone, Axes[grab.Axis] * (0.03f * wheel)); // bigger in that one direction
                    else
                        Sculptor.Scale(name, new Vector3(1f + 0.03f * wheel));
                }

                if (_dragging is null)
                    ImGui.SetTooltip(grab.Kind switch
                    {
                        GrabKind.Arrow => Loc.F("Drag: move {0} only · Wheel: bigger or smaller in that direction", AxisName(grab.Axis)),
                        GrabKind.Ring  => Loc.F("Drag round the ring: turn around the direction {0}", AxisName(grab.Axis)),
                        _              => (glow ? GlowLabel(name) : Label(name)) + "\n" + Loc.T("Drag: move · Shift+drag: nearer/further · Ctrl+drag: ten times finer · Right-drag: turn · Wheel: size · Right-click: put back"),
                    });
            }
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private static void DrawGizmo(ImDrawListPtr draw, Gizmo gizmo, (GrabKind Kind, int Axis)? hot, float s)
    {
        for (var a = 0; a < 3; ++a)
        {
            var colour  = AxisColours[a];
            var ringHot = hot is { Kind: GrabKind.Ring } r && r.Axis == a;
            var ring    = gizmo.Ring[a];
            var ringCol = Theme.U32(colour with { W = ringHot ? 1f : 0.55f });
            for (var i = 0; i + 1 < ring.Length; ++i)
                draw.AddLine(ring[i], ring[i + 1], ringCol, (ringHot ? 3f : 1.5f) * s);
        }

        for (var a = 0; a < 3; ++a)
        {
            var colour   = AxisColours[a];
            var arrowHot = hot is { Kind: GrabKind.Arrow } h && h.Axis == a;
            var usable   = gizmo.TipUsable[a];
            var col      = Theme.U32(colour with { W = usable ? 1f : 0.35f });
            draw.AddLine(gizmo.Centre, gizmo.Tip[a], Theme.U32(new Vector4(0, 0, 0, usable ? 0.5f : 0.2f)), (arrowHot ? 5.5f : 4f) * s);
            draw.AddLine(gizmo.Centre, gizmo.Tip[a], col, (arrowHot ? 3.5f : 2f) * s);
            draw.AddCircleFilled(gizmo.Tip[a], (arrowHot ? 7.5f : 5.5f) * s, Theme.U32(new Vector4(0, 0, 0, usable ? 0.55f : 0.2f)));
            draw.AddCircleFilled(gizmo.Tip[a], (arrowHot ? 6f : 4f) * s, col);
        }
    }

    private Vector3 ToModel(Vector3 world)
        => Vector3.Transform(world, Quaternion.Inverse(_rootRotation)) / SafeScale(_rootScale);

    /// <summary>
    /// The part with its place moved onto the skin it moves (when the head's model is loaded and knows that part): the
    /// dot, the glow and the arrows then sit where you see the change, not on the point the part turns around.
    /// </summary>
    private LiveBone AtSkin(LiveBone bone)
        => _skin.TryGetValue(bone.Name, out var model)
            ? bone with { World = bone.World + Vector3.Transform((model - bone.Model) * _rootScale, _rootRotation) }
            : bone;

    /// <summary> Per frame: where each part's skin is, in the model's space (empty until the head is loaded). </summary>
    private readonly Dictionary<string, Vector3> _skin = new(StringComparer.Ordinal);

    private void SkinTick(Dictionary<string, LiveBone> bones)
    {
        _skin.Clear();
        if (!Config.SkinDots)
            return;

        // the head's model: loaded once per sculpt, in the background (the workbench shows the same one)
        _bench ??= new Workbench(_plugin);
        if (!_bench.Ready && !_bench.Loading && _bench.Problem.Length == 0)
            _bench.Load(Sculptor.Address);
        if (!_bench.Ready)
            return;
        foreach (var bone in bones.Values)
            if (_bench.SkinSpot(bone, out var model))
                _skin[bone.Name] = model;
    }

    /// <summary> How much of a drag a neighbour takes: 1 at the grabbed part, falling to 0 at the brush's edge. </summary>
    private float BrushWeight(LiveBone grabbed, LiveBone other, float brush)
    {
        if (brush <= 0 || IsFace(grabbed.Name) != IsFace(other.Name) || SculptBones.Info(other.Name) is not { Standard: true })
            return 0;

        // how far apart the two are: by the skin they move where that is known, else by the bones themselves
        var known = _skin.TryGetValue(grabbed.Name, out var a) & _skin.TryGetValue(other.Name, out var b);
        var d     = known ? Vector3.Distance(a, b) : Vector3.Distance(grabbed.Model, other.Model);

        // Bones in the very same spot are not neighbours: they are different parts that turn around one point (every
        // eyelid part and the iris sit in the middle of the eye). Taking them along would move the whole eye as one.
        if (d >= brush || !known && d < 0.003f)
            return 0;
        var f = 1f - d / brush;
        return f * f;
    }

    /// <summary> Moves the grabbed part and, with a brush, its neighbours; twins follow mirrored. </summary>
    private void Drag(LiveBone grabbed, Vector3 model, Dictionary<string, LiveBone> bones, float brush)
    {
        if (brush <= 0)
        {
            Sculptor.MoveModel(grabbed, model);
            return;
        }

        // one side only (the twins are mirrored below), so a part and its twin are never both moved twice
        var side  = MathF.Abs(grabbed.Model.X) < 0.002f ? 0 : MathF.Sign(grabbed.Model.X);
        var moved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in bones.Values)
        {
            var w = bone.Name == grabbed.Name ? 1f : BrushWeight(grabbed, bone, brush);
            if (w <= 0)
                continue;
            var twin = SculptBones.MirrorOf(bone.Name);
            if (Sculptor.Symmetry && side != 0 && twin is not null && MathF.Sign(bone.Model.X) != side)
                continue;
            if (!moved.Add(bone.Name))
                continue;
            Sculptor.Nudge(bone, model * w);
            if (Sculptor.Symmetry && twin is not null && bones.TryGetValue(twin, out var other) && moved.Add(twin))
                Sculptor.Nudge(other, (model with { X = -model.X }) * w);
        }
    }

    private static Vector3 SafeScale(Vector3 v)
        => new(MathF.Abs(v.X) < 1e-4f ? 1 : v.X, MathF.Abs(v.Y) < 1e-4f ? 1 : v.Y, MathF.Abs(v.Z) < 1e-4f ? 1 : v.Z);

    /// <summary> How the screen relates to the world at one point, found by projecting small steps (no camera maths). </summary>
    private readonly record struct CameraAt(Vector2 Jx, Vector2 Jy, Vector2 Jz, Vector3 Away)
    {
        /// <summary> The move in the world that makes the point travel these pixels, staying in the plane you look at. </summary>
        public Vector3 Pixels(Vector2 pixels)
        {
            var a   = Jx.X * Jx.X + Jy.X * Jy.X + Jz.X * Jz.X;
            var b   = Jx.X * Jx.Y + Jy.X * Jy.Y + Jz.X * Jz.Y;
            var c   = Jx.Y * Jx.Y + Jy.Y * Jy.Y + Jz.Y * Jz.Y;
            var det = a * c - b * b;
            if (MathF.Abs(det) < 1e-3f)
                return Vector3.Zero;
            var u = (c * pixels.X - b * pixels.Y) / det;
            var v = (-b * pixels.X + a * pixels.Y) / det;
            var d = new Vector3(Jx.X * u + Jx.Y * v, Jy.X * u + Jy.Y * v, Jz.X * u + Jz.Y * v);
            return d.Length() > 0.2f ? Vector3.Zero : d;
        }
    }

    private CameraAt? Camera(Vector3 at)
    {
        var gui = _plugin.GameGui;
        const float e = 0.02f;
        if (!gui.WorldToScreen(at, out var p0))
            return null;
        gui.WorldToScreen(at + new Vector3(e, 0, 0), out var px);
        gui.WorldToScreen(at + new Vector3(0, e, 0), out var py);
        gui.WorldToScreen(at + new Vector3(0, 0, e), out var pz);
        var jx = (px - p0) / e;
        var jy = (py - p0) / e;
        var jz = (pz - p0) / e;

        // the direction that does not move on screen is the line you look down; which way along it is "away" is told by
        // perspective: a step away makes things smaller
        var n = Vector3.Cross(new Vector3(jx.X, jy.X, jz.X), new Vector3(jx.Y, jy.Y, jz.Y));
        n = n.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(n);
        if (PixelsPerMetre(at + n * 0.25f) > PixelsPerMetre(at))
            n = -n;
        return new CameraAt(jx, jy, jz, n);
    }
}
