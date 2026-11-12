using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace SculptPlus;

/// <summary>
/// The Sculpt+ window and the dots on the character. Dots: drag (move), wheel (size), right-drag (turn), right-click
/// (back to how it was). Panel: the numbers of the part you hold, sliders in plain words, dice and Evolve, blending,
/// kept shapes. All of it shows live through Customize+.
/// </summary>
public sealed partial class SculptWindow : Window
{
    private readonly Plugin _plugin;
    private Sculptor Sculptor => _plugin.Sculptor;
    private Configuration Config => _plugin.Config;

    private string? _selected;
    private string? _dragging;
    private bool _turning;
    private float _rightTravel;
    private bool _grabHovered;
    private Vector2? _grabMin;
    private Vector2 _grabSize;
    private DateTime _lastWheelCheckpoint;
    private string _shapeName = string.Empty;
    private string _message = string.Empty;
    private string _find = string.Empty;
    private string _test = string.Empty;
    private string _lastAction = string.Empty;
    private bool _hideDots;
    private Quaternion _rootRotation = Quaternion.Identity;
    private Vector3 _rootScale = Vector3.One;

    private readonly HashSet<string> _regions = new() { "lips", "eyes", "nose", "cheeks" };
    private float _strength = 1f;
    private bool _fromCurrent;
    private int _blendTarget = -1; // -1: how I started
    private float _blend;
    private float _presetStrength = 1f;
    private bool _evolvePlay;
    private DateTime _evolveNext;
    private SculptShape? _pictureFor;
    private int _pictureIn;

    public SculptWindow(Plugin plugin)
        : base("Sculpt+###SculptPlusMain", ImGuiWindowFlags.NoCollapse)
    {
        _plugin             = plugin;
        AllowBackgroundBlur = true;
        RespectCloseHotkey  = false;
        SizeConstraints     = FullConstraints;
        Size                = new Vector2(450, 720);
        SizeCondition       = ImGuiCond.FirstUseEver;

        // a button in the title bar that makes the window small (the dots keep working)
        _smallButton = new TitleBarButton
        {
            Icon        = FontAwesomeIcon.WindowMinimize,
            IconOffset  = new Vector2(2.5f, 1f),
            Priority    = 100, // next to the close button, before Dalamud's own menu button
            Click       = _ => ToggleSmall(),
            ShowTooltip = () => ImGui.SetTooltip(_small
                ? Loc.T("Back to the full window")
                : Loc.T("Make the window small: only the top row stays, the dots keep working")),
        };
        TitleBarButtons.Add(_smallButton);
    }

    private const ImGuiWindowFlags FullFlags = ImGuiWindowFlags.NoCollapse;
    private static readonly WindowSizeConstraints FullConstraints = new() { MinimumSize = new Vector2(400, 300), MaximumSize = new Vector2(800, 1800) };

    private readonly TitleBarButton _smallButton;
    private bool _small;
    private Vector2 _fullSize;
    private bool _restoreSize;
    private bool _titleSpacing;

    private void ToggleSmall()
    {
        _small            = !_small;
        _smallButton.Icon = _small ? FontAwesomeIcon.WindowMaximize : FontAwesomeIcon.WindowMinimize;
    }

    public override void PreDraw()
    {
        // small: as large as its one row, no minimum
        Flags           = _small ? FullFlags | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar : FullFlags;
        SizeConstraints = _small ? null : FullConstraints;
        _plugin.Theme.Push();
    }

    public override void PostDraw()
    {
        if (_titleSpacing)
        {
            ImGui.PopStyleVar();
            _titleSpacing = false;
        }

        _plugin.Theme.Pop();
    }

    public override void OnClose()
    {
        _dragging = null;
        _aimGroup = null;
        SetHatHidden(false);
        _plugin.Camera.End();
        Sculptor.Stop();
    }

    public override void Draw()
    {
        // the size of the full window is remembered, and put back when you come out of the small one
        if (_small)
            _restoreSize = true;
        else if (_restoreSize)
        {
            if (_fullSize.X > 0)
                ImGui.SetWindowSize(_fullSize);
            _restoreSize = false;
        }
        else
            _fullSize = ImGui.GetWindowSize();

        DrawContent();

        // Dalamud draws the title bar buttons after this, and places them by the spacing in force then. With this look's
        // wide padding they would sit on top of the close button; more spacing moves them clear of it and of each other.
        var style = ImGui.GetStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(style.FramePadding.X + 8f * Theme.S, style.ItemInnerSpacing.Y));
        _titleSpacing = true;
    }

    private void DrawContent()
    {
        var s = Theme.S;
        _plugin.Pictures.Tick();
        if (!Sculptor.Active)
        {
            _dragging = null;
            _aimGroup = null;
            SetHatHidden(false);
            DrawWelcome(s);
            return;
        }

        var address = Sculptor.Address;
        var bones   = address == nint.Zero
            ? new Dictionary<string, LiveBone>()
            : SculptBones.Read(address, out _rootRotation, out _rootScale);
        Sculptor.Live = bones;
        Hotkeys();

        // ---- top row ----
        if (Theme.Button(Loc.T("Stop"), new Vector2(0, 0), Theme.Kind.Ghost, FontAwesomeIcon.Stop, true, "##stop"))
        {
            // not kept yet: ask first
            if (Sculptor.Unkept)
                ImGui.OpenPopup("##stopask");
            else
            {
                IsOpen = false;
                return;
            }
        }

        if (ImGui.BeginPopup("##stopask"))
        {
            ImGui.TextColored(Theme.Warn, Loc.T("This sculpt is not kept as a shape yet."));
            ImGui.TextColored(Theme.Muted, Loc.T("Keep it under \"Keep it\" first, or stop anyway: \"Go on with my last sculpt\" on the start page brings it back."));
            ImGui.Spacing();
            if (Theme.Button(Loc.T("Go on sculpting"), new Vector2(0, 0), Theme.Kind.Primary, FontAwesomeIcon.HandSparkles, true, "##stopno"))
                ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            var stop = Theme.Button(Loc.T("Stop anyway"), new Vector2(0, 0), Theme.Kind.Ghost, FontAwesomeIcon.Stop, true, "##stopyes");
            if (stop)
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            if (stop)
            {
                IsOpen = false;
                return;
            }
        }

        Tip(Loc.T("Ends sculpting. A shape you wear stays on; otherwise the character looks like its own Customize+ profile again. Keep the shape first if you want it."));
        ImGui.SameLine();
        if (Theme.Button(Loc.T("Undo"), new Vector2(0, 0), Theme.Kind.Ghost, FontAwesomeIcon.Undo, Sculptor.CanUndo, "##undo"))
            Sculptor.Undo();
        Tip("Ctrl+Z");
        ImGui.SameLine();
        if (Theme.Button(Loc.T("Redo"), new Vector2(0, 0), Theme.Kind.Ghost, FontAwesomeIcon.Redo, Sculptor.CanRedo, "##redo"))
            Sculptor.Redo();
        Tip("Ctrl+Y");
        ImGui.SameLine();
        if (Theme.Button(Loc.T("Start over"), new Vector2(0, 0), Theme.Kind.Ghost, FontAwesomeIcon.Eraser, Sculptor.HasChanges, "##reset"))
        {
            Sculptor.Checkpoint();
            Sculptor.ResetAll();
        }

        Tip(Loc.T("Everything back to how Customize+ had the character when you started."));
        ImGui.SameLine();
        Theme.Button(Loc.T("Before"), new Vector2(0, 0), Theme.Kind.Ghost, FontAwesomeIcon.Eye, Sculptor.HasChanges, "##before");
        var beforeHeld = ImGui.IsItemActive();
        Tip(Loc.T("Hold the mouse button down on this (or hold Ctrl+Alt): you see the character without the sculpt. Let go: with it again."));
        var io = ImGui.GetIO();
        Sculptor.ShowOriginal = beforeHeld || io.KeyCtrl && io.KeyAlt && !io.WantTextInput;
        _hideDots             = io.KeyAlt && !io.WantTextInput;

        // A/B: hold to see one kept shape instead of the sculpt
        var other = Config.CompareShape.Length > 0 ? Config.Shapes.FirstOrDefault(x => x.Name == Config.CompareShape) : null;
        ImGui.SameLine();
        Theme.Button("A/B", new Vector2(0, 0), Theme.Kind.Ghost, FontAwesomeIcon.ExchangeAlt, other is not null, "##ab");
        var abHeld = ImGui.IsItemActive();
        Tip(other is null
            ? Loc.T("Compare the sculpt with a kept shape. Choose the shape first: its A/B button under \"Keep it\".")
            : Loc.F("Hold the mouse button down on this (or hold Alt+B): you see the kept shape \"{0}\" instead of the sculpt. Let go: the sculpt again.", other.Name));
        Sculptor.Compare = other is not null && !Sculptor.ShowOriginal && (abHeld || io.KeyAlt && !io.KeyCtrl && !io.WantTextInput && ImGui.IsKeyDown(ImGuiKey.B))
            ? other
            : null;

        // freeze: on or off with one click, also in the small window
        ImGui.SameLine();
        var frozen = Sculptor.Frozen;
        if (Theme.Button(frozen ? Loc.T("Frozen") : Loc.T("Freeze"), new Vector2(0, 0), frozen ? Theme.Kind.Primary : Theme.Kind.Ghost, FontAwesomeIcon.Snowflake, true, "##freeze"))
            Sculptor.Frozen = !frozen;
        Tip(frozen
            ? Loc.T("You stand completely still, so the dots stay where they are. Click (or Alt+F) to move again. Only on your screen.")
            : Loc.T("Stops every movement of your character (body, face, lips) while you sculpt, so the dots stay where they are. Also Alt+F. Only on your screen."));
        if (io.KeyAlt && !io.KeyCtrl && !io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F, false))
            Sculptor.Frozen = !frozen;

        if (_small)
        {
            // only the top row; everything on the character goes on as usual
            EvolveTick();
            PictureTick(bones);
            CameraTick(bones);
            if (!Sculptor.ShowOriginal && Sculptor.Compare is null && !_hideDots)
                DrawHandles(bones, s);
            return;
        }

        var speed = Sculptor.Speed * 100f;
        ImGui.SetNextItemWidth(120 * s);
        ImGui.BeginDisabled(Sculptor.Frozen);
        if (ImGui.SliderFloat(Loc.T("Movement") + "##speed", ref speed, 5f, 100f, Sculptor.Frozen ? Loc.T("frozen") : speed > 99.5f ? Loc.T("normal") : "%.0f %%"))
            Sculptor.Speed = speed / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            _plugin.SaveConfig();
        ImGui.EndDisabled();
        Tip(Loc.T("How fast the character moves when it is not frozen: a quarter speed lets you watch an expression or the idle pose slowly. Only on your screen."));
        ImGui.SameLine();
        var symmetry = Sculptor.Symmetry;
        if (ImGui.Checkbox(Loc.T("Both sides together"), ref symmetry))
            Sculptor.Symmetry = symmetry;
        Tip(Loc.T("A change to a left part is mirrored on the right one (and the other way round)."));

        var brush = Config.Brush * 1000f;
        ImGui.SetNextItemWidth(120 * s);
        if (ImGui.SliderFloat(Loc.T("Soft brush") + "##brush", ref brush, 0f, 80f, brush < 0.5f ? Loc.T("off") : "%.0f mm"))
            Config.Brush = brush / 1000f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            _plugin.SaveConfig();
        Tip(Loc.T("With a brush, dragging a dot also pulls the parts around it, less the further away they are: like clay instead of single points."));
        ImGui.SameLine();
        var natural = Config.Natural;
        if (ImGui.Checkbox(Loc.T("Keep it natural"), ref natural))
        {
            Config.Natural = natural;
            _plugin.SaveConfig();
        }

        Tip(Loc.T("No part goes further from where it started than a sensible range (a face part about 7 mm, sizes about 1.6 times). A dot at the edge of its range turns orange. Untick it for anything goes."));

        DrawDotFilter(bones, s);

        if (Sculptor.Note.Length > 0)
            Wrapped(Theme.Muted, Sculptor.Note);
        if (_message.Length > 0)
            Wrapped(Theme.Good, _message);
        if (Config.TestLine)
            Wrapped(Theme.Faint, _test + (_lastAction.Length > 0 ? " · last: " + _lastAction : string.Empty)
              + $" · sent to Customize+: {Sculptor.SentCount} ({(Sculptor.LastSendOk ? "ok" : "refused")})"
              + $" · animation speed now: {AnimationSpeed.Read(address):0.00}, layers: {AnimationSpeed.ReadSlots(address)} (held where the game sets it: {(_plugin.Speed.Listening ? "yes" : "no")})");

        ImGui.BeginChild("##sculptbody", new Vector2(0, 0), false);
        if (ImGui.CollapsingHeader(Loc.T("Camera"), ImGuiTreeNodeFlags.DefaultOpen))
            DrawCameraControls(bones, s);
        if (ImGui.CollapsingHeader(Loc.T("The area you picked"), ImGuiTreeNodeFlags.DefaultOpen))
            DrawZone(bones, s);
        if (ImGui.CollapsingHeader(Loc.T("The part you hold"), ImGuiTreeNodeFlags.DefaultOpen))
            DrawSelected(bones, s);
        if (ImGui.CollapsingHeader(Loc.T("Sliders")))
            DrawSliders(s);
        if (ImGui.CollapsingHeader(Loc.T("Roll the dice")))
            DrawRandomizer(s);
        if (ImGui.CollapsingHeader(Loc.T("Left and right")))
            DrawSides();
        if (ImGui.CollapsingHeader(Loc.T("Blend")))
            DrawBlend(s);
        if (ImGui.CollapsingHeader(Loc.T("Check with an expression")))
            DrawExpressions();
        if (ImGui.CollapsingHeader(Loc.T("Your Customize+ templates")))
            DrawTemplates();
        if (ImGui.CollapsingHeader(Loc.T("History")))
            DrawHistory();
        if (ImGui.CollapsingHeader(Loc.T("Keep it"), ImGuiTreeNodeFlags.DefaultOpen))
            DrawShapes(true, bones);
        if (ImGui.CollapsingHeader(Loc.T("Keys and options")))
            DrawOptions(s);
        ImGui.EndChild();

        EvolveTick();
        PictureTick(bones);
        CameraTick(bones);
        if (!Sculptor.ShowOriginal && Sculptor.Compare is null && !_hideDots)
            DrawHandles(bones, s);
    }

    private void DrawWelcome(float s)
    {
        if (_small)
        {
            if (Theme.Button(Loc.T("Sculpt me"), new Vector2(0, 0), Theme.Kind.Primary, FontAwesomeIcon.HandSparkles, true, "##startsmall"))
            {
                _message = string.Empty;
                Sculptor.Start();
            }

            DrawContinue(true);
            return;
        }

        Wrapped(Theme.Muted, Loc.T("Shape a character by hand: dots appear on it. Drag a dot to move that part, scroll on it to make it bigger or smaller, right-click to put it back. It shows live through Customize+ and starts from the profile you have there; nothing in Customize+ is changed."));
        ImGui.Spacing();
        if (!Sculptor.Available)
            Wrapped(Theme.Warn, Loc.T("Customize+ is not running (it does the actual reshaping)."));
        if (Theme.Button(Loc.T("Sculpt me"), new Vector2(0, 0), Theme.Kind.Primary, FontAwesomeIcon.HandSparkles, true, "##start"))
        {
            _message = string.Empty;
            Sculptor.Start();
        }

        DrawContinue(false);
        if (Sculptor.Note.Length > 0)
            Wrapped(Theme.Warn, Sculptor.Note);
        if (_message.Length > 0)
            Wrapped(Theme.Good, _message);
        ImGui.Separator();
        ImGui.BeginChild("##welcomebody", new Vector2(0, 0), false);
        DrawShapes(false, null);
        if (ImGui.CollapsingHeader(Loc.T("Keys and options")))
            DrawOptions(s);
        ImGui.EndChild();
    }

    /// <summary> On the start page: the sculpt you stopped (or closed the window on) is not lost. </summary>
    private void DrawContinue(bool small)
    {
        if (Config.Last.Length == 0)
            return;
        ImGui.SameLine();
        if (Theme.Button(Loc.T("Go on with my last sculpt"), new Vector2(0, 0), Config.LastUnkept ? Theme.Kind.Primary : Theme.Kind.Ghost, FontAwesomeIcon.History, true, "##continue"))
        {
            _message = string.Empty;
            if (Sculptor.ContinueLast())
                _plugin.SaveConfig();
        }

        Tip(Loc.T("Starts sculpting with everything as it was when you stopped last time."));
        if (Config.LastUnkept && !small)
            Wrapped(Theme.Warn, Loc.T("Your last sculpt was not kept as a shape. It is still here: go on with it, then keep it."));
    }

    /// <summary> Ctrl+Z, Ctrl+Y while sculpting (not while you type into a field). </summary>
    private void Hotkeys()
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput || !io.KeyCtrl || io.KeyAlt)
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Z, false))
        {
            if (io.KeyShift)
                Sculptor.Redo();
            else
                Sculptor.Undo();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Y, false))
            Sculptor.Redo();
    }

    // ---- the part you hold ----

    private void DrawSelected(Dictionary<string, LiveBone> bones, float s)
    {
        // find a part by name, for when its dot is hard to spot
        ImGui.SetNextItemWidth(220 * s);
        ImGui.InputTextWithHint("##findpart", Loc.T("Find a part (upper lip, cheek, tail…)"), ref _find, 48);
        if (_find.Trim().Length > 0)
        {
            var words = _find.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hits  = bones.Keys
                .Where(b => words.All(w => Label(b).Contains(w, StringComparison.OrdinalIgnoreCase) || b.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(b => SculptBones.Info(b) is null).ThenBy(Label, StringComparer.OrdinalIgnoreCase).Take(10).ToList();
            foreach (var hit in hits)
                if (ImGui.Selectable($"{Label(hit)}   ({hit})##find{hit}", _selected == hit))
                {
                    _selected = hit;
                    _find     = string.Empty;
                }

            if (hits.Count == 0)
                ImGui.TextColored(Theme.Faint, Loc.T("No part with that name on this character."));
        }

        if (_selected is null || !bones.ContainsKey(_selected))
        {
            ImGui.TextColored(Theme.Muted, Loc.T("Click a dot on the character to see its numbers here."));
            ImGui.TextColored(Theme.Faint, Loc.T("Drag: move · Shift+drag: nearer/further · Right-drag: turn · Wheel: size · Right-click: put back"));
            return;
        }

        var name = _selected;
        ImGui.TextColored(Theme.Accent, Label(name));
        ImGui.SameLine();
        ImGui.TextColored(Theme.Faint, name);
        if (Sculptor.AtLimit(name))
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warn, Config.Natural ? Loc.T("at the edge of its natural range") : Loc.T("past its natural range"));
        }

        // its dot in the way? hide just this one (Alt+H does the same)
        var dotHidden = Config.HiddenDots.Contains(name);
        var io        = ImGui.GetIO();
        var hideKey   = io.KeyAlt && !io.KeyCtrl && !io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.H, false);
        if (ImGui.SmallButton((dotHidden ? Loc.T("Show its dot again") : Loc.T("Hide its dot")) + "##hidedot") || hideKey)
        {
            SetDotHidden(name, !dotHidden);
            if (!dotHidden)
            {
                // let go of it, or it would stay on screen as the part you hold
                _selected = null;
                return;
            }
        }

        Tip(dotHidden
            ? Loc.T("The dot of this part is hidden; it only shows now because you hold it.")
            : Loc.T("Takes just this dot off the character (with \"Both sides together\", its twin too). Alt+H does the same. The part keeps its shape; find it by name to get it back."));

        DrawNudge(bones[name], bones, s);

        var (move, turn, size) = Sculptor.Get(name);
        var changed = false;
        ImGui.SetNextItemWidth(-110 * s);
        var m = move * 1000f; // millimetres read better
        if (ImGui.DragFloat3(Loc.T("Move (mm)") + "##move", ref m, 0.1f, -500f, 500f, "%.1f"))
        {
            move    = m / 1000f;
            changed = true;
        }

        Checkpoint();
        ImGui.SetNextItemWidth(-110 * s);
        if (ImGui.DragFloat3(Loc.T("Size") + "##size", ref size, 0.004f, 0.05f, 6f, "%.3f"))
            changed = true;
        Checkpoint();
        var even = (size.X + size.Y + size.Z) / 3f;
        ImGui.SetNextItemWidth(-110 * s);
        if (ImGui.DragFloat(Loc.T("Size, all ways") + "##even", ref even, 0.004f, 0.05f, 6f, "%.3f"))
        {
            var old = (size.X + size.Y + size.Z) / 3f;
            size    *= old > 1e-4f ? even / old : 1f;
            changed =  true;
        }

        Checkpoint();
        ImGui.SetNextItemWidth(-110 * s);
        if (ImGui.DragFloat3(Loc.T("Turn (°)") + "##turn", ref turn, 0.3f, -180f, 180f, "%.1f"))
            changed = true;
        Checkpoint();

        if (changed)
            Sculptor.SetWithTwin(name, move, turn, size);

        if (ImGui.SmallButton(Loc.T("Put this part back")))
        {
            Sculptor.Checkpoint();
            Sculptor.ResetBone(name);
        }

        ImGui.SameLine();
        var locked = Sculptor.Locked.Contains(name);
        if (ImGui.Checkbox(Loc.T("Lock for the dice"), ref locked))
            Sculptor.SetLocked(name, locked);
        Tip(Loc.T("A locked part is left alone when you roll. Its dot gets a ring."));

        void Checkpoint()
        {
            if (ImGui.IsItemActivated())
                Sculptor.Checkpoint();
        }
    }

    // ---- sliders in plain words ----

    private static string SliderLabel(string key)
        => key switch
        {
            "lips"      => Loc.T("Fuller lips"),
            "mouth"     => Loc.T("Wider mouth"),
            "eyes"      => Loc.T("Bigger eyes"),
            "eyesapart" => Loc.T("Eyes further apart"),
            "eyesup"    => Loc.T("Eyes higher"),
            "brows"     => Loc.T("Brows higher"),
            "nose"      => Loc.T("Bigger nose"),
            "cheeks"    => Loc.T("Fuller cheeks"),
            "jaw"       => Loc.T("Wider jaw"),
            "chin"      => Loc.T("Longer chin"),
            "head"      => Loc.T("Bigger head"),
            "ears"      => Loc.T("Bigger ears"),
            "chest"     => Loc.T("Bigger chest"),
            "waist"     => Loc.T("Wider waist"),
            "hips"      => Loc.T("Wider hips"),
            "shoulders" => Loc.T("Broader upper body"),
            "arms"      => Loc.T("Thicker arms"),
            "thighs"    => Loc.T("Thicker thighs"),
            "calves"    => Loc.T("Thicker calves"),
            "hands"     => Loc.T("Bigger hands"),
            "feet"      => Loc.T("Bigger feet"),
            "tail"      => Loc.T("Bigger tail"),
            "eyewide"    => Loc.T("Wider eyes"),
            "eyehigh"    => Loc.T("Taller eyes"),
            "iris"       => Loc.T("Bigger iris"),
            "eyeouter"   => Loc.T("Outer corners higher"),
            "lidup"      => Loc.T("Upper lid higher"),
            "liddown"    => Loc.T("Lower lid lower"),
            "browsouter" => Loc.T("Outer brows higher"),
            "browsinner" => Loc.T("Inner brows higher"),
            "browsapart" => Loc.T("Brows further apart"),
            "browsout"   => Loc.T("Brows further out"),
            "nosewide"   => Loc.T("Wider nose"),
            "nostrils"   => Loc.T("Wider nostrils"),
            "noseup"     => Loc.T("Nose higher"),
            "noseout"    => Loc.T("Nose further out"),
            "bridge"     => Loc.T("Bridge further out"),
            "upperlip"   => Loc.T("Fuller upper lip"),
            "lowerlip"   => Loc.T("Fuller lower lip"),
            "corners"    => Loc.T("Corners of the mouth higher"),
            "mouthup"    => Loc.T("Mouth higher"),
            "lipsout"    => Loc.T("Lips further out"),
            "cheekhigh"  => Loc.T("Cheekbones higher"),
            "cheekwide"  => Loc.T("Cheeks further apart"),
            "cheekout"   => Loc.T("Cheekbones further out"),
            "chinwide"   => Loc.T("Wider chin"),
            "chinout"    => Loc.T("Chin further out"),
            "earslong"   => Loc.T("Longer ears"),
            "earsapart"  => Loc.T("Ears further apart"),
            _           => Loc.T("Taller"),
        };

    private static string PresetLabel(string key)
        => key switch
        {
            "softer"   => Loc.T("Softer"),
            "sharper"  => Loc.T("Sharper"),
            "doll"     => Loc.T("Doll-like"),
            "mature"   => Loc.T("More mature"),
            "curvier"  => Loc.T("Curvier"),
            "athletic" => Loc.T("Athletic"),
            _          => Loc.T("Slender"),
        };

    private void DrawSliders(float s)
    {
        // presets: a few sliders at once, as a starting point
        ImGui.TextColored(Theme.Muted, Loc.T("Starting points:"));
        foreach (var preset in Sculptor.Presets)
        {
            ImGui.SameLine(0, 4 * s);
            if (ImGui.SmallButton(PresetLabel(preset.Key) + "##preset" + preset.Key))
                Sculptor.ApplyPreset(preset.Key, _presetStrength);
            Tip(string.Join(", ", preset.Moves.Select(mv => (mv.Value > 0 ? "+ " : "− ") + SliderLabel(mv.Slider))));
            if (ImGui.GetItemRectMax().X > ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - 90 * s)
                ImGui.NewLine();
        }

        ImGui.SetNextItemWidth(140 * s);
        ImGui.SliderFloat(Loc.T("How strongly a starting point is added") + "##presetstrength", ref _presetStrength, 0.25f, 2f, "%.2f ×");
        Tip(Loc.T("Each click adds the starting point to where the sliders stand. Click twice for double; Undo takes one click back."));

        ImGui.TextColored(Theme.Faint, Loc.T("Right: more, left: less. Double-click a slider to set it back to the middle."));
        foreach (var group in new[] { "Face", "Body" })
        {
            ImGui.TextColored(Theme.Accent, group == "Face" ? Loc.T("Face") : Loc.T("Body"));
            foreach (var slider in Sculptor.Sliders.Where(x => x.Group == group))
            {
                // only what the character has bones for (a tail, Dawntrail face bones…)
                if (Sculptor.Live.Count > 0 && !Sculptor.Live.Keys.Any(slider.Has))
                    continue;
                var v = Sculptor.SliderValue(slider.Key);
                ImGui.SetNextItemWidth(-150 * s);
                if (ImGui.SliderFloat(SliderLabel(slider.Key) + "##sl" + slider.Key, ref v, -1.5f, 1.5f, MathF.Abs(v) < 0.005f ? "·" : "%+.2f"))
                    Sculptor.SetSlider(slider.Key, v);
                if (ImGui.IsItemActivated())
                    Sculptor.Checkpoint();
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    Sculptor.SetSlider(slider.Key, 0);
            }
        }
    }

    // ---- the dice ----

    private void DrawRandomizer(float s)
    {
        var i = 0;
        foreach (var region in Sculptor.Regions)
        {
            var on = _regions.Contains(region.Key);
            if (ImGui.Checkbox(RegionLabel(region.Key) + "##r" + region.Key, ref on))
            {
                if (on)
                    _regions.Add(region.Key);
                else
                    _regions.Remove(region.Key);
            }

            if (++i % 2 == 1)
                ImGui.SameLine(190 * s);
        }

        if (i % 2 == 1)
            ImGui.NewLine();
        ImGui.SetNextItemWidth(160 * s);
        ImGui.SliderFloat(Loc.T("How much") + "##strength", ref _strength, 0.2f, 3f, _strength < 0.7f ? Loc.T("a touch") : _strength < 1.5f ? Loc.T("visible") : _strength < 2.3f ? Loc.T("bold") : Loc.T("wild"));
        ImGui.TextColored(Theme.Faint, Sculptor.Locked.Count > 0 ? Loc.F("{0} part(s) locked", Sculptor.Locked.Count) : Loc.T("Unticked parts stay as they are."));
        if (Sculptor.Locked.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Unlock all")))
                Sculptor.Locked.Clear();
        }

        if (Sculptor.Evolving)
        {
            DrawEvolve(s);
            return;
        }

        ImGui.Checkbox(Loc.T("On top of how it looks now"), ref _fromCurrent);
        Tip(Loc.T("Off: every roll starts from how the character was when you began, so rolls don't pile up. On: each roll changes what is there."));
        if (Theme.Button(Loc.T("Roll"), new Vector2(0, 0), Theme.Kind.Primary, FontAwesomeIcon.Dice, _regions.Count > 0, "##roll"))
            Sculptor.Randomize(_regions, _strength, _fromCurrent);
        Tip(Loc.T("Changes the ticked parts a little at random, both sides the same. Undo takes a roll back."));
        ImGui.SameLine();
        if (Theme.Button(Loc.T("Evolve"), new Vector2(0, 0), Theme.Kind.Secondary, FontAwesomeIcon.Seedling, _regions.Count > 0, "##evolve"))
        {
            Sculptor.EvolveStart(_regions, _strength, 4);
            _evolvePlay = true;
            _evolveNext = DateTime.UtcNow.AddSeconds(1.6);
        }

        Tip(Loc.T("Shows you four variations of how the character looks now, one after another. You take the one you like best, and the next four vary around that one, a little less each round. A few rounds usually land somewhere good."));
    }

    private void DrawEvolve(float s)
    {
        ImGui.Separator();
        ImGui.TextColored(Theme.Accent, Loc.F("Evolve · round {0}", Sculptor.EvolveRound));
        for (var k = 0; k < Sculptor.EvolveCount; ++k)
        {
            if (k > 0)
                ImGui.SameLine(0, 4 * s);
            var on = Sculptor.EvolveShown == k;
            ImGui.PushStyleColor(ImGuiCol.Button, on ? Theme.AccentDeep : Theme.Card);
            if (ImGui.Button($"  {k + 1}  ##evo{k}"))
            {
                Sculptor.EvolveShow(k);
                _evolvePlay = false;
            }

            ImGui.PopStyleColor();
        }

        ImGui.SameLine(0, 10 * s);
        if (ImGui.SmallButton((Sculptor.EvolveShown < 0 ? "> " : string.Empty) + Loc.T("Before these") + "##evoparent"))
        {
            Sculptor.EvolveShow(-1);
            _evolvePlay = false;
        }

        Tip(Loc.T("The one these four came from, to compare."));
        ImGui.SameLine();
        ImGui.Checkbox(Loc.T("Show in turn"), ref _evolvePlay);

        if (Theme.Button(Loc.T("Take this one"), new Vector2(0, 0), Theme.Kind.Primary, FontAwesomeIcon.Check, Sculptor.EvolveShown >= 0, "##evopick"))
        {
            Sculptor.EvolvePick();
            _evolvePlay = true;
            _evolveNext = DateTime.UtcNow.AddSeconds(1.6);
        }

        Tip(Loc.T("The one you see becomes the new starting point, and four new variations are made around it."));
        ImGui.SameLine();
        if (Theme.Button(Loc.T("None of these"), new Vector2(0, 0), Theme.Kind.Ghost, FontAwesomeIcon.Redo, true, "##evoagain"))
        {
            Sculptor.EvolveAgain();
            _evolveNext = DateTime.UtcNow.AddSeconds(1.6);
        }

        ImGui.SameLine();
        if (Theme.Button(Loc.T("Done"), new Vector2(0, 0), Theme.Kind.Ghost, FontAwesomeIcon.Stop, true, "##evodone"))
            Sculptor.EvolveEnd(true);
        Tip(Loc.T("Ends evolving and keeps what you see now."));
    }

    /// <summary> While "Show in turn" is on, the next variation goes on every second and a half. </summary>
    private void EvolveTick()
    {
        if (!Sculptor.Evolving || !_evolvePlay || Sculptor.EvolveCount == 0 || DateTime.UtcNow < _evolveNext)
            return;
        _evolveNext = DateTime.UtcNow.AddSeconds(1.6);
        Sculptor.EvolveShow((Math.Max(Sculptor.EvolveShown, -1) + 1) % Sculptor.EvolveCount);
    }

    private static string RegionLabel(string key)
        => key switch
        {
            "lips"   => Loc.T("Lips"),
            "eyes"   => Loc.T("Eyes and brows"),
            "nose"   => Loc.T("Nose"),
            "cheeks" => Loc.T("Cheeks and jaw"),
            "ears"   => Loc.T("Ears"),
            "chest"  => Loc.T("Chest"),
            "torso"  => Loc.T("Waist and hips"),
            "limbs"  => Loc.T("Arms and legs"),
            "tail"   => Loc.T("Tail"),
            _        => Loc.T("Overall size"),
        };

    // ---- left and right ----

    private void DrawSides()
    {
        if (ImGui.SmallButton(Loc.T("Make the right side like the left")))
            Sculptor.MirrorSide(true);
        Tip(Loc.T("Copies what you changed by hand on the character's left side onto its right side, mirrored. For when \"Both sides together\" was off for a while and the face has gone lopsided."));
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("Make the left side like the right")))
            Sculptor.MirrorSide(false);
        if (ImGui.SmallButton(Loc.T("A touch of asymmetry")))
            Sculptor.TouchOfAsymmetry();
        Tip(Loc.T("Nudges face parts by a hair, each on one side only (a quarter of a millimetre, a percent in size): real faces are not mirror images. Click again for a little more."));
    }

    // ---- blending ----

    private void DrawBlend(float s)
    {
        var shapes = Config.Shapes;
        if (_blendTarget >= shapes.Count)
            _blendTarget = -1;
        var current = _blendTarget < 0 ? Loc.T("How it started") : shapes[_blendTarget].Name;
        ImGui.SetNextItemWidth(200 * s);
        if (ImGui.BeginCombo(Loc.T("Towards") + "##blendto", current))
        {
            if (ImGui.Selectable(Loc.T("How it started"), _blendTarget < 0))
                Pick(-1);
            for (var i = 0; i < shapes.Count; ++i)
                if (ImGui.Selectable(shapes[i].Name + "##bt" + i, _blendTarget == i))
                    Pick(i);
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(200 * s);
        var pct = _blend * 100f;
        if (ImGui.SliderFloat(Loc.T("How far") + "##blend", ref pct, -50f, 150f, "%.0f %%"))
        {
            if (!Sculptor.Blending)
                Sculptor.BeginBlend(_blendTarget < 0 ? null : shapes[_blendTarget]);
            _blend = pct / 100f;
            Sculptor.SetBlend(_blend);
        }

        Tip(Loc.T("0 %: how it looks now. 100 %: the other shape. In between: a mix, so you can take a third of a roll you half like. Below 0 or above 100 goes further than either."));
        if (Sculptor.Blending)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Done") + "##blenddone"))
            {
                Sculptor.EndBlend();
                _blend = 0;
            }
        }
        else if (_blend != 0)
            _blend = 0;

        void Pick(int index)
        {
            if (Sculptor.Blending)
                Sculptor.EndBlend();
            _blendTarget = index;
            _blend       = 0;
        }
    }

    // ---- expressions ----

    private void DrawExpressions()
    {
        ImGui.TextColored(Theme.Faint, Loc.T("Plays one of your expressions, so you see the face move. Others near you see it too."));
        var first = true;
        foreach (var (label, command) in new[]
                 {
                     (Loc.T("Smile"), "/smile motion"), (Loc.T("Grin"), "/grin motion"), (Loc.T("Surprised"), "/surprised motion"),
                     (Loc.T("Angry"), "/angry motion"), (Loc.T("Sad"), "/sad motion"), (Loc.T("Kiss"), "/blowkiss motion"),
                     (Loc.T("Neutral"), "/straightface motion"),
                 })
        {
            if (!first)
                ImGui.SameLine();
            first = false;
            if (ImGui.SmallButton(label + "##expr" + command))
            {
                Sculptor.LetMove(4); // (frozen would freeze the expression too)
                GameChat.Run(command);
            }
        }
    }

    // ---- Customize+ templates ----

    private List<(Guid Id, string Name, int Bones, bool On)>? _templates;
    private DateTime _templatesAt;

    private void DrawTemplates()
    {
        if (_templates is null || (DateTime.UtcNow - _templatesAt).TotalSeconds > 2)
        {
            _templates   = Sculptor.Templates();
            _templatesAt = DateTime.UtcNow;
        }

        if (_templates is null)
        {
            ImGui.TextColored(Theme.Muted, Loc.T("No Customize+ profile is on you, so there are no templates to switch."));
            return;
        }

        Wrapped(Theme.Faint, Loc.T("The templates of the Customize+ profile you stand on. Switching one here switches it in Customize+ too; what you sculpted stays on top."));
        foreach (var (id, name, count, on) in _templates)
        {
            var value = on;
            if (ImGui.Checkbox($"{name}##tpl{id}", ref value))
            {
                Sculptor.SetTemplate(id, value);
                _templates = null;
                break;
            }

            ImGui.SameLine();
            ImGui.TextColored(Theme.Faint, Loc.F("{0} bones", count));
        }
    }

    // ---- history ----

    private void DrawHistory()
    {
        var history = Sculptor.History;
        if (history.Count == 0)
        {
            ImGui.TextColored(Theme.Muted, Loc.T("Nothing yet. A snapshot is kept every few minutes while you sculpt, and when you stop."));
            return;
        }

        ImGui.TextColored(Theme.Faint, Loc.T("Click one to go back to it. Undo brings you back here."));
        Sculptor.HistoryEntry? pick = null;
        foreach (var entry in history.Take(30))
        {
            var local = entry.Utc.ToLocalTime();
            var when  = local.Date == DateTime.Now.Date ? local.ToString("HH:mm") : local.ToString("dd.MM. HH:mm");
            var label = Loc.F("{0} · {1} change(s)", when, entry.Parts);
            if (ImGui.Selectable(label + "##hist" + entry.Utc.Ticks))
                pick = entry;
        }

        if (pick is not null)
            Sculptor.RestoreHistory(pick);
    }

    // ---- kept shapes ----

    private void DrawShapes(bool sculpting, Dictionary<string, LiveBone>? bones)
    {
        var s = Theme.S;
        if (sculpting)
        {
            ImGui.SetNextItemWidth(170 * s);
            ImGui.InputTextWithHint("##shapename", Loc.T("Name of the shape"), ref _shapeName, 48);
            ImGui.SameLine();
            if (Theme.Button(Loc.T("Keep"), new Vector2(0, 0), Theme.Kind.Primary, FontAwesomeIcon.Save, _shapeName.Trim().Length > 0, "##keep"))
            {
                var shape = Sculptor.Save(_shapeName.Trim());
                _message     = Loc.F("Kept as \"{0}\". \"Wear\" keeps it on you after you stop.", shape.Name);
                _pictureFor  = shape;
                _pictureIn   = 3; // a few frames, so the picture shows the shape as it is now
            }

            Tip(Loc.T("Keeps the shape under this name, with a picture of the face as you see it now (the same name again replaces it)."));
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Copy for Customize+")))
            {
                ImGui.SetClipboardText(Sculptor.ExportTemplate(_shapeName.Trim().Length > 0 ? _shapeName.Trim() : "Sculpt"));
                _message = Loc.T("Copied. In Customize+: Templates, then the import-from-clipboard button; add the template to your profile.");
            }

            Tip(Loc.T("Customize+ does not let other plugins save into it, so the sculpt travels by clipboard: it holds the whole shape (the profile plus what you changed here)."));
            if (ImGui.SmallButton(Loc.T("Copy as text to share")))
            {
                ImGui.SetClipboardText(Sculptor.ExportCode(_shapeName.Trim().Length > 0 ? _shapeName.Trim() : "Shape"));
                _message = Loc.T("Copied. Anyone with Sculpt+ can add it with \"Paste a shape\".");
            }

            if (Config.Last.Length > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.T("My last sculpt")))
                {
                    Sculptor.Checkpoint();
                    Sculptor.LoadLast();
                }

                Tip(Loc.T("Puts the shape back that was on you when you last stopped sculpting."));
            }
        }
        else
            ImGui.TextColored(Theme.Accent, Loc.T("Your shapes"));

        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("Paste a shape")))
        {
            string text;
            try
            {
                text = ImGui.GetClipboardText() ?? string.Empty;
            }
            catch
            {
                text = string.Empty;
            }

            _message = Sculptor.ImportCode(text) is { } added ? Loc.F("Added the shape \"{0}\".", added.Name) : Sculptor.Note;
        }

        Tip(Loc.T("Adds a shape someone copied as text (it starts with SCULPT1:) to your shapes."));

        if (Config.Shapes.Count == 0)
        {
            if (!sculpting)
                ImGui.TextColored(Theme.Muted, Loc.T("None yet. Sculpt, then keep the shape under a name."));
            return;
        }

        SculptShape? delete = null;
        var side = 56 * s;
        foreach (var shape in Config.Shapes)
        {
            ImGui.PushID(shape.Name);
            var worn = Config.Worn == shape.Name;

            // picture
            var pos  = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(pos, pos + new Vector2(side), Theme.U32(Theme.Card), 8 * s);
            if (_plugin.Pictures.Get(shape) is { } wrap)
            {
                draw.AddImageRounded(wrap.Handle, pos, pos + new Vector2(side), Vector2.Zero, Vector2.One, 0xFFFFFFFF, 8 * s);
                ImGui.Dummy(new Vector2(side));
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Image(wrap.Handle, new Vector2(256 * s));
                    ImGui.EndTooltip();
                }
            }
            else
                ImGui.Dummy(new Vector2(side));

            if (worn)
                draw.AddRect(pos, pos + new Vector2(side), Theme.U32(Theme.Good), 8 * s, ImDrawFlags.None, 2 * s);

            ImGui.SameLine(0, 8 * s);
            ImGui.BeginGroup();
            ImGui.TextColored(worn ? Theme.Good : Theme.Accent, shape.Name);
            if (worn)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Good, Loc.T("(on you)"));
            }

            if (worn)
            {
                if (ImGui.SmallButton(Loc.T("Take off")))
                    Sculptor.TakeShapeOff();
            }
            else
            {
                if (ImGui.SmallButton(Loc.T("Wear")))
                {
                    if (sculpting)
                        Sculptor.Checkpoint();
                    Sculptor.Wear(shape);
                }

                Tip(Loc.T("Wear: the shape goes on you and stays on, also after area changes and logins, until you take it off."));
            }

            if (sculpting && !worn)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.T("Open")))
                {
                    Sculptor.Checkpoint();
                    Sculptor.Load(shape);
                    _shapeName = shape.Name;
                }

                Tip(Loc.T("Continue sculpting from this shape, without wearing it."));
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Share")))
            {
                ImGui.SetClipboardText(Sculptor.ExportCode(shape));
                _message = Loc.T("Copied. Anyone with Sculpt+ can add it with \"Paste a shape\".");
            }

            Tip(Loc.T("Copies this shape as a line of text for a friend."));
            if (sculpting)
            {
                ImGui.SameLine();
                var isB = Config.CompareShape == shape.Name;
                ImGui.PushStyleColor(ImGuiCol.Button, isB ? Theme.AccentDeep : Theme.Card);
                if (ImGui.SmallButton("A/B"))
                {
                    Config.CompareShape = isB ? string.Empty : shape.Name;
                    _plugin.SaveConfig();
                }

                ImGui.PopStyleColor();
                Tip(Loc.T("Makes this the shape that the A/B button at the top (and Alt+B) flips to, for comparing it with what you sculpt."));
            }

            if (sculpting && bones is not null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.T("New picture")))
                {
                    _pictureFor = shape;
                    _pictureIn  = 1;
                }

                Tip(Loc.T("Takes its picture again from what you see now."));
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("×") && ImGui.GetIO().KeyCtrl)
                delete = shape;
            Tip(Loc.T("Ctrl+click: delete this shape."));
            ImGui.EndGroup();
            ImGui.PopID();
        }

        if (delete is not null)
            Sculptor.Delete(delete);
    }

    /// <summary> Takes the picture of a just-kept shape: the square of the screen around the face, without dots or windows. </summary>
    private void PictureTick(Dictionary<string, LiveBone> bones)
    {
        if (_pictureFor is null || --_pictureIn > 0)
            return;
        var shape = _pictureFor;
        _pictureFor = null;

        var viewport = ImGui.GetMainViewport();
        var min      = new Vector2(float.MaxValue);
        var max      = new Vector2(float.MinValue);
        var any      = 0;
        foreach (var bone in bones.Values)
        {
            if (!IsFace(bone.Name) || !_plugin.GameGui.WorldToScreen(bone.World, out var at))
                continue;
            at  -= viewport.Pos;
            min =  Vector2.Min(min, at);
            max =  Vector2.Max(max, at);
            any++;
        }

        if (any < 4)
            return; // the face is not on screen: no picture this time

        // a square, a good deal larger than the dots span (they sit between brows and chin; hair and ears want room)
        var centre = (min + max) / 2 - new Vector2(0, (max.Y - min.Y) * 0.25f);
        var half   = MathF.Max(max.X - min.X, max.Y - min.Y) * 1.15f;
        _plugin.Pictures.Take(shape, viewport.ID, viewport.Size, centre - new Vector2(half), centre + new Vector2(half));
    }

    // ---- keys and options ----

    private void DrawOptions(float s)
    {
        Wrapped(Theme.Muted, Loc.T("On a dot: drag moves · Shift+drag nearer/further · right-drag turns · wheel sizes · right-click puts back."));
        Wrapped(Theme.Muted, Loc.T("On the part you hold: drag an arrow to move in that direction only (red: left and right, green: up and down, blue: front and back) · wheel on an arrow sizes in that direction · drag round a ring to turn around its direction."));
        Wrapped(Theme.Muted, Loc.T("For fine work: Ctrl+drag moves a tenth as far · Alt + arrow keys move the part you hold a tiny step · with the glow, Ctrl + wheel goes through parts that lie on top of each other · hold Alt to turn the camera with the mouse over the face."));
        Wrapped(Theme.Muted, Loc.T("Anywhere: Ctrl+Z undo · Ctrl+Y redo · hold Alt hides the dots · hold Ctrl+Alt shows the character without the sculpt · hold Alt+B shows the A/B shape · Alt+F freezes or lets you move · Alt+H hides the dot of the part you hold."));

        var arrows = Config.Arrows;
        if (ImGui.Checkbox(Loc.T("Arrows and rings on the part you hold"), ref arrows))
        {
            Config.Arrows = arrows;
            _plugin.SaveConfig();
        }

        var size = Config.DotSize;
        ImGui.SetNextItemWidth(140 * s);
        if (ImGui.SliderFloat(Loc.T("Dot size") + "##dotsize", ref size, 0.5f, 2.5f, "%.1f ×"))
            Config.DotSize = size;
        if (ImGui.IsItemDeactivatedAfterEdit())
            _plugin.SaveConfig();

        var far = Config.HideFarSide;
        if (ImGui.Checkbox(Loc.T("Hide face dots on the far side"), ref far))
        {
            Config.HideFarSide = far;
            _plugin.SaveConfig();
        }

        Tip(Loc.T("Dots on the side of the head that is turned away from you are not drawn, so you don't see the back ones through the face."));

        var current = Config.Language == "auto" ? Loc.T("Like Dalamud") : Loc.Languages.FirstOrDefault(l => l.Code == Config.Language).Name ?? Config.Language;
        ImGui.SetNextItemWidth(140 * s);
        if (ImGui.BeginCombo(Loc.T("Language") + "##lang", current))
        {
            if (ImGui.Selectable(Loc.T("Like Dalamud"), Config.Language == "auto"))
                SetLanguage("auto");
            foreach (var (code, name) in Loc.Languages)
                if (ImGui.Selectable(name, Config.Language == code))
                    SetLanguage(code);
            ImGui.EndCombo();
        }

        var test = Config.TestLine;
        if (ImGui.Checkbox(Loc.T("Show a test line (for reporting a problem)"), ref test))
        {
            Config.TestLine = test;
            _plugin.SaveConfig();
        }

        void SetLanguage(string code)
        {
            Config.Language = code;
            _plugin.SaveConfig();
            _plugin.ApplyLanguage();
        }
    }

    private static void Wrapped(Vector4 colour, string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(colour, text);
        ImGui.PopTextWrapPos();
    }

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
