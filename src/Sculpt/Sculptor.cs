using System.IO.Compression;
using System.Numerics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SculptPlus;

/// <summary> A shape you kept: what you changed by hand, where the sliders stood, and the finished bones to put on. </summary>
public sealed class SculptShape
{
    public string Name { get; set; } = string.Empty;
    public string Hand { get; set; } = "{}";
    public Dictionary<string, float> Sliders { get; set; } = new();
    public string Output { get; set; } = "{}";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;

    /// <summary> File name of its picture in the plugin's data folder (empty: none yet). </summary>
    public string Picture { get; set; } = string.Empty;
}

/// <summary>
/// Shaping your character: every change is a bone edit (move, size, turn), shown live through a temporary Customize+
/// profile on you. Two layers make the shape: what you did by hand (drags, numbers, dice) on top of the profile
/// Customize+ has on you, and sliders in plain words that move groups of bones. Nothing of yours in Customize+ is changed.
/// </summary>
public sealed class Sculptor : IDisposable
{
    private readonly Plugin _plugin;
    private readonly CustomizePlusIpc _ipc;
    private JObject _base = new();
    private JObject _hand = new();
    private readonly Dictionary<string, float> _sliders = new();
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private bool _dirty;
    private DateTime _lastSent;
    private readonly Random _random = new();
    private bool _showOriginal;
    private nint _wornFor;
    private DateTime _wornRetry;
    private JObject? _blendFrom;
    private JObject? _blendTo;

    public bool Active { get; private set; }
    public bool Symmetry { get; set; } = true;
    public string Note { get; private set; } = string.Empty;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary> Bones you locked: the dice leave them alone. </summary>
    public HashSet<string> Locked { get; } = new(StringComparer.Ordinal);

    /// <summary> Your bones this frame (the window reads them); sliders need them to know which way a bone faces. </summary>
    public IReadOnlyDictionary<string, LiveBone> Live { get; set; } = new Dictionary<string, LiveBone>();

    public Sculptor(Plugin plugin)
    {
        _plugin = plugin;
        _ipc    = new CustomizePlusIpc(plugin.PluginInterface);
    }

    public bool Available => _ipc.Available;

    private Configuration Config => _plugin.Config;

    private nint _address;
    private Guid? _profileId;

    /// <summary> Your character while you sculpt (zero when you don't, or are not there). </summary>
    public nint Address
        => Active ? _plugin.Objects.LocalPlayer?.Address ?? nint.Zero : nint.Zero;

    /// <summary> Begins: reads the profile Customize+ has on you and works on a copy of it (or on the shape you wear). </summary>
    public bool Start()
    {
        if (Active)
            return true;
        if (!_ipc.Available)
        {
            Note = Loc.T("Customize+ is not running (it does the actual reshaping).");
            return false;
        }

        if (_plugin.Objects.LocalPlayer is not { } me)
        {
            Note = Loc.T("Log in first.");
            return false;
        }

        _address = me.Address;
        ReadRestPose();
        _ipc.DeleteTemporary(0); // a leftover of ours would hide the real profile
        _base      = new JObject();
        _profileId = _ipc.ActiveProfileId(0);
        if (_profileId is { } id && _ipc.ProfileJson(id) is { } json)
        {
            try
            {
                if (JObject.Parse(json)["Bones"] is JObject bones)
                    _base = bones;
            }
            catch
            {
                // start from nothing
            }
        }
        else
            _profileId = null;

        _hand = (JObject)_base.DeepClone();
        _sliders.Clear();
        _undo.Clear();
        _redo.Clear();
        _blendFrom = _blendTo = null;
        EvolveEnd(false);
        Active = true;
        _dirty = true;
        _historyAt = DateTime.UtcNow;
        if (WornShape() is { } worn)
        {
            Load(worn);
            Note = Loc.F("Continuing with the shape you wear, \"{0}\".", worn.Name);
        }
        else
            Note = _base.Count > 0
                ? Loc.F("Started from your Customize+ profile ({0} bones).", _base.Count)
                : Loc.T("Started from scratch (no Customize+ profile is on you).");
        return true;
    }

    /// <summary> Ends: the shape you wear (if any) stays on, otherwise your own Customize+ profile shows again. </summary>
    public void Stop()
    {
        if (!Active)
            return;
        if (HasChanges)
            AddHistory();
        // (a sculpt without any change does not replace the last real one)
        if (HasChanges)
        {
            Config.LastUnkept = Unkept;
            Config.Last       = Snapshot();
        }

        EvolveEnd(false);
        Active = false;
        UpdateFreeze();
        _showOriginal = false;
        _compare      = null;
        _plugin.SaveConfig();
        if (WornShape() is { } worn)
            Send(worn.Output);
        else
            _ipc.DeleteTemporary(0);
    }

    /// <summary> Framework thread: sends what changed (at most twenty times a second), and keeps a worn shape on you. </summary>
    public void Update()
    {
        UpdateFreeze();
        if (!Active)
        {
            KeepWorn();
            return;
        }

        // you logged out or changed area: nothing to sculpt any more
        if (Address != _address)
        {
            Stop();
            return;
        }

        if (HasChanges && (DateTime.UtcNow - _historyAt).TotalMinutes >= 3)
            AddHistory();

        if (_showOriginal || _compare is not null || !_dirty || (DateTime.UtcNow - _lastSent).TotalMilliseconds < 50)
            return;
        if (_sliders.Values.Any(v => MathF.Abs(v) > 0.001f) && Live.Count == 0)
            return; // the sliders need your bones; next frame
        _dirty    = false;
        _lastSent = DateTime.UtcNow;
        if (!Send(Compose().ToString(Newtonsoft.Json.Formatting.None)))
            Note = Loc.T("Customize+ did not take the change.");
    }

    /// <summary> How often a shape was sent to Customize+, and whether the last one was taken. </summary>
    public int SentCount { get; private set; }
    public bool LastSendOk { get; private set; } = true;

    /// <summary> Puts bones on you as a temporary Customize+ profile. </summary>
    private bool Send(string bonesJson)
    {
        SentCount++;
        return LastSendOk = _ipc.SetTemporary(0, "{\"Bones\":" + bonesJson + "}");
    }

    private bool _frozen;
    private DateTime _moveUntil;

    /// <summary> Lets you move for a moment although Hold still is on (to watch an expression play). </summary>
    public void LetMove(double seconds)
        => _moveUntil = DateTime.UtcNow.AddSeconds(seconds);

    /// <summary> The Freeze button: completely still while you sculpt (only on your screen). </summary>
    public bool Frozen
    {
        get => _plugin.Config.Frozen;
        set
        {
            _plugin.Config.Frozen = value;
            _moveUntil            = DateTime.MinValue;
            _plugin.SaveConfig();
        }
    }

    /// <summary> How fast the character moves when not frozen: 0.05 slow motion … 1 normal (0, from older versions, counts as normal). </summary>
    public float Speed
    {
        get => _plugin.Config.Speed <= 0f ? 1f : Math.Clamp(_plugin.Config.Speed, 0.05f, 1f);
        set => _plugin.Config.Speed = Math.Clamp(value, 0.05f, 1f);
    }

    /// <summary> The speed in force: 0 while frozen. </summary>
    private float HeldSpeed => Frozen ? 0f : Speed;

    /// <summary> Framework thread: holds you at the chosen speed while sculpting, and lets go otherwise. </summary>
    private void UpdateFreeze()
    {
        var want    = Active && HeldSpeed < 0.995f && DateTime.UtcNow >= _moveUntil;
        var address = want ? Address : nint.Zero;
        if (address == nint.Zero)
        {
            if (_frozen)
            {
                var held = _plugin.Speed.Held;
                _plugin.Speed.Release(held != nint.Zero && _plugin.Objects.LocalPlayer?.Address == held);
            }

            _frozen = false;
            return;
        }

        // (every frame: the speed may have changed, and where the game's own setting could not be listened to, writing
        // it again and again is the only way to hold it at all)
        _plugin.Speed.Hold(address, HeldSpeed);
        _frozen = true;
    }

    private SculptShape? _compare;

    /// <summary> A/B: while a kept shape is set here, you see that shape instead of the sculpt; null shows the sculpt again. </summary>
    public SculptShape? Compare
    {
        get => _compare;
        set
        {
            if (ReferenceEquals(_compare, value) || !Active)
                return;
            _compare = value;
            if (value is null)
                _dirty = true;
            else
                Send(value.Output);
        }
    }

    /// <summary> Before / after: true shows you without the sculpt, false with it again. </summary>
    public bool ShowOriginal
    {
        get => _showOriginal;
        set
        {
            if (_showOriginal == value || !Active)
                return;
            _showOriginal = value;
            if (value)
                _ipc.DeleteTemporary(0);
            else
                _dirty = true;
        }
    }

    // ---- the two layers, and what comes out ----

    /// <summary> Hand layer plus sliders: the bones as Customize+ gets them. </summary>
    public JObject Compose()
    {
        var result = (JObject)_hand.DeepClone();

        // which way is out of the face: the nose lies in front of the head bone
        var front = 1f;
        if (_restPose.TryGetValue("j_kao", out var headRest) && (_restPose.TryGetValue("j_f_hana", out var noseRest) || _restPose.TryGetValue("j_f_memoto", out noseRest)))
            front = noseRest.Position.Z - headRest.Position.Z < 0 ? -1f : 1f;
        else if (Live.TryGetValue("j_kao", out var headBone) && (Live.TryGetValue("j_f_hana", out var noseBone) || Live.TryGetValue("j_f_memoto", out noseBone)))
            front = noseBone.Model.Z - headBone.Model.Z < 0 ? -1f : 1f;
        foreach (var (key, value) in _sliders)
        {
            if (MathF.Abs(value) < 0.001f || Array.Find(Sliders, s => s.Key == key) is not { } slider)
                continue;
            foreach (var bone in Live.Values)
            {
                if (!slider.Has(bone.Name))
                    continue;
                var (m, t, s) = Read(result[bone.Name]);
                if (slider.Size != Vector3.Zero)
                    s *= slider.Key is "arms" or "thighs" or "calves" && LimbRuns(bone) is { } along
                        ? SculptMath.AroundSize(Stand(bone), along, slider.Size.X * value)
                        : LocalSize(bone, slider.Size * value);
                if (slider.Outward != 0 && SideOf(bone) != 0)
                    m += ToLocal(bone, new Vector3(SideOf(bone) * slider.Outward * value, 0, 0));
                if (slider.Move != Vector3.Zero)
                    m += ToLocal(bone, slider.Move * value);
                if (slider.Forward != 0)
                    m += ToLocal(bone, new Vector3(0, 0, front * slider.Forward * value));
                Write(result, bone.Name, m, t, s);
            }
        }

        return result;
    }

    /// <summary>
    /// How every bone stands in the skeleton's rest pose. Side, up and front are meant for the body as it rests (upright,
    /// arms out, face straight ahead), not for the pose of the moment: a tilted head would otherwise turn "up" for the
    /// face into something else, and the other side of the face would not mirror. Empty when it could not be read; the
    /// pose of the moment is used then, as before.
    /// </summary>
    private Dictionary<string, (Vector3 Position, Quaternion Rotation)> _restPose = new();

    private void ReadRestPose()
    {
        _restPose = new Dictionary<string, (Vector3, Quaternion)>();
        try
        {
            var rest = SculptBones.ReadRest(_address, true);

            // only a rest pose that is its own mirror image is believed: every left part lies where its right twin does, flipped
            var apart = new List<float>();
            foreach (var (name, at) in rest)
                if (name.EndsWith("_l", StringComparison.Ordinal) && SculptBones.MirrorOf(name) is { } twin && rest.TryGetValue(twin, out var other))
                    apart.Add(Vector3.Distance(at.Position, other.Position with { X = -other.Position.X }));
            apart.Sort();
            if (apart.Count >= 4 && apart[apart.Count / 2] < 0.01f)
                _restPose = rest;
            else
                _plugin.Log.Warning($"Sculpt+: the skeleton's rest pose is not used ({apart.Count} pairs, {(apart.Count > 0 ? apart[apart.Count / 2] * 1000 : 0):0} mm apart).");
        }
        catch (Exception ex)
        {
            _plugin.Log.Warning($"Sculpt+: the skeleton's rest pose could not be read: {ex.Message}");
        }
    }

    /// <summary> True when directions go by the rest pose (for the test line). </summary>
    public bool ByRestPose => _restPose.Count > 0;

    /// <summary> How the bone is turned for the purpose of directions: as it rests, else as it is. </summary>
    private Quaternion Stand(LiveBone bone)
        => _restPose.TryGetValue(bone.Name, out var rest) ? rest.Rotation : bone.ModelRotation;

    /// <summary> Which side of the body a bone is on (as it rests): 1, -1, or 0 for the middle. </summary>
    private float SideOf(LiveBone bone)
    {
        var x = _restPose.TryGetValue(bone.Name, out var rest) ? rest.Position.X : bone.Model.X;
        return MathF.Abs(x) > 0.0005f ? MathF.Sign(x) : 0f;
    }

    /// <summary>
    /// The direction an arm or leg part runs in as the body rests (towards the next part down the limb); null for
    /// anything else, or without a rest pose. Arms rest slanted, so "thicker" cannot be said in side, up and front.
    /// </summary>
    private Vector3? LimbRuns(LiveBone bone)
    {
        var name = bone.Name;
        if (name.Length < 3 || !_restPose.TryGetValue(name, out var here))
            return null;
        var side = name[^2..];
        var next = name[..^2] switch
        {
            "j_ude_a" => "j_ude_b",
            "j_ude_b" => "j_te",
            "j_asi_a" => "j_asi_b",
            "j_asi_b" => "j_asi_c",
            "j_asi_c" => "j_asi_d",
            _         => null,
        };
        if (next is null || !_restPose.TryGetValue(next + side, out var there))
            return null;
        var runs = there.Position - here.Position;
        return runs.LengthSquared() > 1e-8f ? Vector3.Normalize(runs) : null;
    }

    /// <summary> A size change given in the body's directions (wide, high, deep), as factors for the bone's own axes. </summary>
    private Vector3 LocalSize(LiveBone bone, Vector3 wideHighDeep)
        => SculptMath.LocalSize(Stand(bone), wideHighDeep);

    /// <summary>
    /// A move given in the body's directions (side, up, front; as the body rests), in the bone's own directions: that is
    /// how Customize+ stores it (it adds a move turned by the bone's own rotation).
    /// </summary>
    private Vector3 ToLocal(LiveBone bone, Vector3 body)
        => Vector3.Transform(body, Quaternion.Inverse(Stand(bone)));

    /// <summary> A move as you see it on the character right now (in the model as it is posed), in the bone's own directions. </summary>
    private static Vector3 SeenToLocal(LiveBone bone, Vector3 seen)
        => Vector3.Transform(seen, Quaternion.Inverse(bone.ModelRotation));

    /// <summary> A direction as you see it on the character right now, as a direction of the resting body. </summary>
    private Vector3 SeenToBody(LiveBone bone, Vector3 seen)
        => Vector3.Transform(SeenToLocal(bone, seen), Stand(bone));

    /// <summary>
    /// A direction of the resting body (side, up, front) as it lies on the character right now for this bone: with the
    /// head tilted, "up" for a part of the face is tilted with it. For the arrows on the part you hold.
    /// </summary>
    public Vector3 BodyToSeen(LiveBone bone, Vector3 body)
        => Vector3.Transform(ToLocal(bone, body), bone.ModelRotation);

    private static (Vector3 Move, Vector3 Turn, Vector3 Size) Read(JToken? t)
        => t is JObject o ? (Vec(o["Translation"], 0), Vec(o["Rotation"], 0), Vec(o["Scaling"], 1)) : (Vector3.Zero, Vector3.Zero, Vector3.One);

    private static void Write(JObject set, string bone, Vector3 move, Vector3 turn, Vector3 size)
    {
        if (set[bone] is not JObject o)
            set[bone] = o = new JObject { ["ChildScaling"] = Json(Vector3.One), ["ChildScaleIndependent"] = false };
        o["Translation"] = Json(Clamp(move, -1f, 1f));
        o["Rotation"]    = Json(Clamp(turn, -180f, 180f));
        o["Scaling"]     = Json(Clamp(size, 0.05f, 6f));
    }

    // ---- one bone, by hand ----

    public (Vector3 Move, Vector3 Turn, Vector3 Size) Get(string bone)
        => Read(_hand[bone]);

    public bool IsEdited(string bone)
        => !JToken.DeepEquals(_hand[bone], _base[bone]) && Differs(Read(_hand[bone]), Read(_base[bone]));

    private static bool Differs((Vector3 Move, Vector3 Turn, Vector3 Size) a, (Vector3 Move, Vector3 Turn, Vector3 Size) b)
        => (a.Move - b.Move).LengthSquared() > 1e-10f || (a.Turn - b.Turn).LengthSquared() > 1e-6f || (a.Size - b.Size).LengthSquared() > 1e-8f;

    public void Set(string bone, Vector3 move, Vector3 turn, Vector3 size)
    {
        if (Config.Natural)
            (move, turn, size) = Limit(bone, move, turn, size);
        Write(_hand, bone, move, turn, size);
        _dirty = true;
    }

    // ---- keep it natural ----

    /// <summary> How far a part may go from where it started: (move in metres, turn in degrees, size as a factor). </summary>
    private static (float Move, float Turn, float Size) Range(string bone)
    {
        var family = SculptBones.Info(bone)?.Family;
        return family switch
        {
            "Face" or "Eyes" or "Cheeks" or "Lips" or "Jaw" or "Tongue" => (0.007f, 25f, 1.6f),
            "Ears" or "Tail" or "Hair"                                  => (0.05f, 45f, 1.8f),
            "Root"                                                      => (0.05f, 20f, 1.25f),
            _                                                           => (0.03f, 25f, 1.5f),
        };
    }

    private (Vector3 Move, Vector3 Turn, Vector3 Size) Limit(string bone, Vector3 move, Vector3 turn, Vector3 size)
    {
        var (m0, t0, s0) = Read(_base[bone]);
        var (rm, rt, rs) = Range(bone);
        var dm = move - m0;
        if (dm.Length() > rm)
            move = m0 + dm * (rm / dm.Length());
        var dt = turn - t0;
        turn = t0 + Clamp(dt, -rt, rt);
        size = new Vector3(Math.Clamp(size.X, s0.X / rs, s0.X * rs), Math.Clamp(size.Y, s0.Y / rs, s0.Y * rs), Math.Clamp(size.Z, s0.Z / rs, s0.Z * rs));
        return (move, turn, size);
    }

    /// <summary> True when a part sits at (or past) the edge of the natural range. </summary>
    public bool AtLimit(string bone)
    {
        if (_hand[bone] is null)
            return false;
        var (m, t, s)    = Read(_hand[bone]);
        var (m0, t0, s0) = Read(_base[bone]);
        var (rm, rt, rs) = Range(bone);
        if ((m - m0).Length() >= rm * 0.995f)
            return true;
        var dt = t - t0;
        if (MathF.Max(MathF.Abs(dt.X), MathF.Max(MathF.Abs(dt.Y), MathF.Abs(dt.Z))) >= rt * 0.995f)
            return true;
        return Ratio(s.X, s0.X) >= rs * 0.995f || Ratio(s.Y, s0.Y) >= rs * 0.995f || Ratio(s.Z, s0.Z) >= rs * 0.995f;

        static float Ratio(float a, float b)
            => a <= 0 || b <= 0 ? 1 : MathF.Max(a / b, b / a);
    }

    /// <summary> Call before a change the user can undo (the start of a drag, a roll, a reset). </summary>
    public void Checkpoint()
    {
        _undo.Add(Snapshot());
        if (_undo.Count > 60)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    private string Snapshot()
        => new JObject { ["h"] = _hand, ["s"] = JObject.FromObject(_sliders) }.ToString(Newtonsoft.Json.Formatting.None);

    private void Restore(string snapshot)
    {
        var o = JObject.Parse(snapshot);
        // (older saves were the bare bones)
        _hand = o["h"] as JObject ?? (o["h"] is null && o["s"] is null ? o : new JObject());
        _sliders.Clear();
        if (o["s"] is JObject s)
            foreach (var p in s.Properties())
                _sliders[p.Name] = p.Value.Value<float>();
        _dirty = true;
    }

    public void Undo()
        => Swap(_undo, _redo);

    public void Redo()
        => Swap(_redo, _undo);

    private void Swap(List<string> from, List<string> to)
    {
        if (from.Count == 0)
            return;
        to.Add(Snapshot());
        Restore(from[^1]);
        from.RemoveAt(from.Count - 1);
        _blendFrom = _blendTo = null;
    }

    /// <summary> Moves a bone by a distance as you see it on the character (in the model as it is posed), no twin. </summary>
    public void Nudge(LiveBone bone, Vector3 modelDelta)
    {
        var (m, t, s) = Get(bone.Name);
        Set(bone.Name, m + SeenToLocal(bone, modelDelta), t, s);
    }

    /// <summary>
    /// Moves a bone's twin the mirrored way. The mirror is the body's own (through the middle of the face for a part of
    /// the face, however the head is held), not the room's: the move is taken to the resting body, flipped left to right
    /// there, and given to the twin.
    /// </summary>
    public void NudgeTwin(LiveBone bone, LiveBone twin, Vector3 modelDelta)
    {
        var (m, t, s) = Get(twin.Name);
        Set(twin.Name, m + SculptMath.TwinMove(modelDelta, bone.ModelRotation, Stand(bone), Stand(twin)), t, s);
    }

    /// <summary> Moves a bone; its twin moves mirrored when symmetry is on. </summary>
    public void MoveModel(LiveBone bone, Vector3 modelDelta)
    {
        Nudge(bone, modelDelta);
        if (Symmetry && SculptBones.MirrorOf(bone.Name) is { } twin && Live.TryGetValue(twin, out var other))
            NudgeTwin(bone, other, modelDelta);
    }

    /// <summary> Turns a bone around an axis as you see it on the character; its twin turns the mirrored way. </summary>
    public void TurnModel(LiveBone bone, Vector3 modelAxis, float radians)
    {
        Spin(bone, SeenToLocal(bone, modelAxis), radians);
        if (Symmetry && SculptBones.MirrorOf(bone.Name) is { } twin && Live.TryGetValue(twin, out var other))
        {
            var body = SeenToBody(bone, modelAxis);
            Spin(other, ToLocal(other, body with { X = -body.X }), -radians);
        }
    }

    private void Spin(LiveBone bone, Vector3 axis, float radians)
    {
        if (axis.LengthSquared() < 1e-8f)
            return;
        var (m, t, s) = Get(bone.Name);
        var q = ToQuaternion(t) * Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians);
        Set(bone.Name, m, ToEuler(q), s);
    }

    private static Quaternion ToQuaternion(Vector3 degrees)
        => SculptMath.ToQuaternion(degrees);

    private static Vector3 ToEuler(Quaternion q)
        => SculptMath.ToEuler(q);

    public void Scale(string bone, Vector3 factor)
    {
        Apply(bone);
        if (Symmetry && SculptBones.MirrorOf(bone) is { } twin)
            Apply(twin);

        void Apply(string name)
        {
            var (m, t, s) = Get(name);
            Set(name, m, t, s * factor);
        }
    }

    /// <summary>
    /// Sizes a bone in the model's directions (wide, high, deep; as powers), whichever way the bone is turned; its twin gets
    /// the same.
    /// </summary>
    public void ScaleModel(LiveBone bone, Vector3 wideHighDeep)
    {
        Apply(bone);
        if (Symmetry && SculptBones.MirrorOf(bone.Name) is { } twin && Live.TryGetValue(twin, out var other))
            Apply(other);

        void Apply(LiveBone b)
        {
            var (m, t, s) = Get(b.Name);
            Set(b.Name, m, t, s * LocalSize(b, wideHighDeep));
        }
    }

    public void SetWithTwin(string bone, Vector3 move, Vector3 turn, Vector3 size)
    {
        Set(bone, move, turn, size);
        if (Symmetry && SculptBones.MirrorOf(bone) is { } twin)
        {
            // sizes are the same on both sides; moves and turns are left to the drag (their directions differ per bone)
            var (m, t, _) = Get(twin);
            Set(twin, m, t, size);
        }
    }

    public void ResetBone(string bone)
    {
        Back(bone);
        if (Symmetry && SculptBones.MirrorOf(bone) is { } twin)
            Back(twin);

        void Back(string name)
        {
            if (_base[name] is JObject original)
                _hand[name] = original.DeepClone();
            else
                _hand.Remove(name);
            _dirty = true;
        }
    }

    public void ResetAll()
    {
        _hand = (JObject)_base.DeepClone();
        _sliders.Clear();
        _blendFrom = _blendTo = null;
        _dirty = true;
    }

    public void LoadLast()
    {
        if (Config.Last.Length == 0)
            return;
        try
        {
            Restore(Config.Last);
        }
        catch
        {
            Note = Loc.T("The last sculpt could not be read.");
        }
    }

    public bool HasChanges
        => _sliders.Values.Any(v => MathF.Abs(v) > 0.001f) || !JToken.DeepEquals(_hand, _base);

    public void SetLocked(string bone, bool locked)
    {
        foreach (var name in new[] { bone, Symmetry ? SculptBones.MirrorOf(bone) : null })
        {
            if (name is null)
                continue;
            if (locked)
                Locked.Add(name);
            else
                Locked.Remove(name);
        }
    }

    // ---- sliders in plain words ----

    /// <summary>
    /// One slider: which bones it touches and what one full step does to them. Sizes are in the model's directions (wide,
    /// high, deep; as powers, 0.2 is about a fifth bigger); Outward pushes a part away from your middle line (metres).
    /// </summary>
    public sealed record Slider(string Key, string Group, string[] Families, string[] Bones, Vector3 Size, float Outward, Vector3 Move, float Forward = 0)
    {
        public bool Has(string bone)
            => Bones.Contains(bone) || Families.Length > 0 && SculptBones.Info(bone) is { } info && Families.Contains(info.Family);
    }

    private static string[] Both(params string[] stems)
        => stems.SelectMany(s => new[] { s + "_l", s + "_r" }).ToArray();

    private static readonly string[] None = Array.Empty<string>();

    public static readonly Slider[] Sliders =
    {
        new("lips", "Face", new[] { "Lips" }, None, new Vector3(0.22f), 0, Vector3.Zero),
        new("mouth", "Face", None, Both("j_f_uslip", "j_f_dslip", "j_f_lip"), Vector3.Zero, 0.0022f, Vector3.Zero),
        new("eyes", "Face", new[] { "Eyes" }, Both("j_f_eye"), new Vector3(0.14f), 0, Vector3.Zero),
        new("eyesapart", "Face", new[] { "Eyes" }, Both("j_f_eye"), Vector3.Zero, 0.0020f, Vector3.Zero),
        new("eyesup", "Face", new[] { "Eyes" }, Both("j_f_eye"), Vector3.Zero, 0, new Vector3(0, 0.0020f, 0)),
        new("brows", "Face", None, Both("j_f_mayu", "j_f_miken", "j_f_miken_01", "j_f_miken_02"), Vector3.Zero, 0, new Vector3(0, 0.0022f, 0)),
        new("nose", "Face", None, new[] { "j_f_hana", "j_f_hana_l", "j_f_hana_r", "j_f_uhana" }, new Vector3(0.20f), 0, Vector3.Zero),
        new("cheeks", "Face", new[] { "Cheeks" }, None, new Vector3(0.16f), 0, Vector3.Zero),
        new("jaw", "Face", new[] { "Jaw" }, new[] { "j_ago" }, new Vector3(0.20f, 0, 0), 0, Vector3.Zero),
        new("chin", "Face", None, new[] { "j_f_dago", "j_f_ago", "j_ago" }, new Vector3(0, 0.18f, 0), 0, Vector3.Zero),
        new("head", "Face", None, new[] { "j_kao" }, new Vector3(0.08f), 0, Vector3.Zero),
        new("ears", "Face", new[] { "Ears" }, None, new Vector3(0.22f), 0, Vector3.Zero),

        new("chest", "Body", None, Both("j_mune"), new Vector3(0.25f), 0, Vector3.Zero),
        new("waist", "Body", None, new[] { "j_sebo_a", "j_sebo_b" }, new Vector3(0.16f, 0, 0.16f), 0, Vector3.Zero),
        new("hips", "Body", None, new[] { "j_kosi", "iv_shiri_l", "iv_shiri_r" }, new Vector3(0.14f, 0, 0.14f), 0, Vector3.Zero),
        new("shoulders", "Body", None, new[] { "j_sebo_c" }, new Vector3(0.14f, 0, 0.06f), 0, Vector3.Zero),
        new("arms", "Body", None, Both("j_ude_a", "j_ude_b"), new Vector3(0.20f, 0, 0.20f), 0, Vector3.Zero),
        new("thighs", "Body", None, Both("j_asi_a"), new Vector3(0.20f, 0, 0.20f), 0, Vector3.Zero),
        new("calves", "Body", None, Both("j_asi_b", "j_asi_c"), new Vector3(0.20f, 0, 0.20f), 0, Vector3.Zero),
        new("hands", "Body", None, Both("j_te"), new Vector3(0.16f), 0, Vector3.Zero),
        new("feet", "Body", None, Both("j_asi_d"), new Vector3(0.16f), 0, Vector3.Zero),
        new("tail", "Body", new[] { "Tail" }, None, new Vector3(0.22f), 0, Vector3.Zero),
        new("height", "Body", None, new[] { "n_root" }, new Vector3(0.07f), 0, Vector3.Zero),

        // The fine ones: they show next to the area you picked, not in the long list. Forward pushes a part out of the
        // face (towards where the nose points), in metres.
        new("eyewide", "Fine", None, Both("j_f_mab", "j_f_eye"), new Vector3(0.14f, 0, 0), 0, Vector3.Zero),
        new("eyehigh", "Fine", None, Both("j_f_mab", "j_f_eye"), new Vector3(0, 0.14f, 0), 0, Vector3.Zero),
        new("iris", "Fine", None, Both("j_f_eyepuru"), new Vector3(0.20f), 0, Vector3.Zero),
        new("eyeouter", "Fine", None, Both("j_f_mabup_02out", "j_f_mabdn_02out"), Vector3.Zero, 0, new Vector3(0, 0.0015f, 0)),
        new("lidup", "Fine", None, Both("j_f_mabup_01", "j_f_mabup_02out", "j_f_mabup_03in", "j_f_umab"), Vector3.Zero, 0, new Vector3(0, 0.0012f, 0)),
        new("liddown", "Fine", None, Both("j_f_mabdn_01", "j_f_mabdn_02out", "j_f_mabdn_03in", "j_f_dmab"), Vector3.Zero, 0, new Vector3(0, -0.0012f, 0)),

        new("browsouter", "Fine", None, Both("j_f_mayu"), Vector3.Zero, 0, new Vector3(0, 0.0020f, 0)),
        new("browsinner", "Fine", None, Both("j_f_miken", "j_f_miken_01"), Vector3.Zero, 0, new Vector3(0, 0.0020f, 0)),
        new("browsapart", "Fine", None, Both("j_f_mayu", "j_f_miken", "j_f_mmayu", "j_f_miken_01", "j_f_miken_02"), Vector3.Zero, 0.0015f, Vector3.Zero),
        new("browsout", "Fine", None, Both("j_f_mayu", "j_f_miken", "j_f_mmayu", "j_f_miken_01", "j_f_miken_02"), Vector3.Zero, 0, Vector3.Zero, 0.0015f),

        new("nosewide", "Fine", None, new[] { "j_f_hana", "j_f_hana_l", "j_f_hana_r" }, new Vector3(0.20f, 0, 0), 0, Vector3.Zero),
        new("nostrils", "Fine", None, Both("j_f_hana"), Vector3.Zero, 0.0012f, Vector3.Zero),
        new("noseup", "Fine", None, new[] { "j_f_hana", "j_f_hana_l", "j_f_hana_r" }, Vector3.Zero, 0, new Vector3(0, 0.0015f, 0)),
        new("noseout", "Fine", None, new[] { "j_f_hana", "j_f_hana_l", "j_f_hana_r" }, Vector3.Zero, 0, Vector3.Zero, 0.0020f),
        new("bridge", "Fine", None, new[] { "j_f_uhana", "j_f_memoto" }, Vector3.Zero, 0, Vector3.Zero, 0.0015f),

        new("upperlip", "Fine", None, Both("j_f_ulip_01", "j_f_ulip_02", "j_f_umlip_01", "j_f_umlip_02").Concat(new[] { "j_f_ulip_a", "j_f_ulip_b" }).ToArray(), new Vector3(0.22f), 0, Vector3.Zero),
        new("lowerlip", "Fine", None, Both("j_f_dlip_01", "j_f_dlip_02", "j_f_dmlip_01", "j_f_dmlip_02").Concat(new[] { "j_f_dlip_a", "j_f_dlip_b" }).ToArray(), new Vector3(0.22f), 0, Vector3.Zero),
        new("corners", "Fine", None, Both("j_f_uslip", "j_f_dslip", "j_f_lip"), Vector3.Zero, 0, new Vector3(0, 0.0012f, 0)),
        new("mouthup", "Fine", new[] { "Lips" }, Both("j_f_lip").Concat(new[] { "j_f_ulip_a", "j_f_ulip_b", "j_f_dlip_a", "j_f_dlip_b" }).ToArray(), Vector3.Zero, 0, new Vector3(0, 0.0015f, 0)),
        new("lipsout", "Fine", new[] { "Lips" }, Both("j_f_lip").Concat(new[] { "j_f_ulip_a", "j_f_ulip_b", "j_f_dlip_a", "j_f_dlip_b" }).ToArray(), Vector3.Zero, 0, Vector3.Zero, 0.0015f),

        new("cheekhigh", "Fine", None, Both("j_f_hoho", "j_f_shoho"), Vector3.Zero, 0, new Vector3(0, 0.0018f, 0)),
        new("cheekwide", "Fine", new[] { "Cheeks" }, None, Vector3.Zero, 0.0015f, Vector3.Zero),
        new("cheekout", "Fine", None, Both("j_f_hoho", "j_f_shoho"), Vector3.Zero, 0, Vector3.Zero, 0.0015f),

        new("chinwide", "Fine", None, new[] { "j_f_dago", "j_f_ago" }, new Vector3(0.18f, 0, 0), 0, Vector3.Zero),
        new("chinout", "Fine", None, new[] { "j_f_dago", "j_f_ago" }, Vector3.Zero, 0, Vector3.Zero, 0.0020f),

        new("earslong", "Fine", new[] { "Ears" }, None, new Vector3(0, 0.22f, 0), 0, Vector3.Zero),
        new("earsapart", "Fine", None, Both("j_mimi"), Vector3.Zero, 0.0020f, Vector3.Zero),
    };

    /// <summary> The area on the map a slider belongs to (the same names as the dot filters). </summary>
    public static string ZoneOf(string key)
        => key switch
        {
            "lips" or "mouth" or "upperlip" or "lowerlip" or "corners" or "mouthup" or "lipsout"            => "lips",
            "eyes" or "eyesapart" or "eyesup" or "eyewide" or "eyehigh" or "iris" or "eyeouter" or "lidup" or "liddown" => "eyes",
            "brows" or "browsouter" or "browsinner" or "browsapart" or "browsout"                           => "brows",
            "nose" or "nosewide" or "nostrils" or "noseup" or "noseout" or "bridge"                         => "nose",
            "cheeks" or "cheekhigh" or "cheekwide" or "cheekout"                                            => "cheeks",
            "jaw" or "chin" or "chinwide" or "chinout"                                                      => "jaw",
            "ears" or "earslong" or "earsapart"                                                             => "ears",
            "head"                                                                                          => "face",
            "chest"                                                                                         => "chest",
            "waist" or "hips" or "shoulders" or "height"                                                    => "torso",
            "arms"                                                                                          => "arms",
            "hands"                                                                                         => "hands",
            "thighs" or "calves"                                                                            => "legs",
            "feet"                                                                                          => "feet",
            "tail"                                                                                          => "tail",
            _                                                                                               => "other",
        };

    public float SliderValue(string key)
        => _sliders.GetValueOrDefault(key);

    public void SetSlider(string key, float value)
    {
        value = Math.Clamp(value, -2f, 2f);
        if (MathF.Abs(value) < 0.001f)
            _sliders.Remove(key);
        else
            _sliders[key] = value;
        _dirty = true;
    }

    // ---- the dice ----

    /// <summary> What a region is made of, and how it may be changed at random. </summary>
    public sealed record Region(string Key, string[] Families, string[] Bones, float Size, float Move, bool EvenSize, bool Thickness);

    public static readonly Region[] Regions =
    {
        new("lips", new[] { "Lips" }, None, 0.10f, 0.0012f, false, false),
        new("eyes", new[] { "Eyes" }, Both("j_f_mayu", "j_f_miken", "j_f_miken_01", "j_f_miken_02", "j_f_eye"), 0.07f, 0.0010f, false, false),
        new("nose", None, new[] { "j_f_hana", "j_f_hana_l", "j_f_hana_r", "j_f_uhana" }, 0.10f, 0.0012f, false, false),
        new("cheeks", new[] { "Cheeks", "Jaw" }, None, 0.08f, 0.0015f, false, false),
        new("ears", new[] { "Ears" }, None, 0.12f, 0f, true, false),
        new("chest", None, Both("j_mune"), 0.14f, 0f, false, false),
        new("torso", None, new[] { "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "iv_shiri_l", "iv_shiri_r" }, 0.07f, 0f, false, true),
        new("limbs", None, Both("j_ude_a", "j_ude_b", "j_asi_a", "j_asi_b", "j_asi_c"), 0.07f, 0f, false, true),
        new("tail", new[] { "Tail" }, None, 0.15f, 0f, true, false),
        new("size", None, new[] { "n_root" }, 0.04f, 0f, true, false),
    };

    /// <summary>
    /// Rolls the chosen regions: sizes a few percent up or down and, in the face, small moves; both sides the same. Locked
    /// bones and unticked regions stay as they are. Strength 1 is clearly visible but still you; 2 is a different person.
    /// </summary>
    public void Randomize(IReadOnlySet<string> regions, float strength, bool fromCurrent)
    {
        Checkpoint();
        var count = RollRegions(regions, strength, fromCurrent);
        Note = count == 0 ? Loc.T("Nothing to roll: tick at least one part.") : Loc.F("Rolled {0} part(s). Not you? Undo, or roll again.", count);
    }

    private int RollRegions(IReadOnlySet<string> regions, float strength, bool fromCurrent)
    {
        var done  = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var region in Regions.Where(r => regions.Contains(r.Key)))
        {
            foreach (var bone in Live.Values)
            {
                var info     = SculptBones.Info(bone.Name);
                var inRegion = region.Bones.Contains(bone.Name) || info is not null && region.Families.Contains(info.Family);
                if (!inRegion || Locked.Contains(bone.Name) || !done.Add(bone.Name))
                    continue;
                var twin = SculptBones.MirrorOf(bone.Name);
                if (twin is not null)
                    done.Add(twin);

                var even = Gauss() * region.Size * strength;
                // in the model's directions: wide, high, deep (limbs and torso keep their length)
                var whd = region.EvenSize
                    ? new Vector3(even)
                    : region.Thickness
                        ? new Vector3(even + Gauss() * region.Size * 0.3f * strength, 0, even + Gauss() * region.Size * 0.3f * strength)
                        : new Vector3(even) + new Vector3(Gauss(), Gauss(), Gauss()) * region.Size * 0.4f * strength;
                whd = Clamp(whd, -0.5f, 0.5f);
                var move = region.Move > 0
                    ? new Vector3(twin is null ? 0 : Gauss(), Gauss(), Gauss()) * region.Move * strength
                    : Vector3.Zero;

                Roll(bone, whd, move, fromCurrent);
                if (twin is not null && Live.TryGetValue(twin, out var other) && !Locked.Contains(twin))
                    Roll(other, whd, move with { X = -move.X }, fromCurrent);
                count++;
            }
        }

        return count;
    }

    // ---- evolve: pick the best of a few, the next few vary around it ----

    private JObject? _evolveParent;
    private readonly List<JObject> _evolveKids = new();
    private HashSet<string> _evolveRegions = new();
    private float _evolveStrength;

    public bool Evolving => _evolveParent is not null;
    public int EvolveCount => _evolveKids.Count;
    public int EvolveShown { get; private set; } = -1;
    public int EvolveRound { get; private set; }

    /// <summary> Starts from how you look now and makes the first few variations. </summary>
    public void EvolveStart(IReadOnlySet<string> regions, float strength, int count)
    {
        if (regions.Count == 0 || Live.Count == 0)
        {
            Note = Loc.T("Nothing to roll: tick at least one part.");
            return;
        }

        Checkpoint();
        _evolveParent   = (JObject)_hand.DeepClone();
        _evolveRegions  = new HashSet<string>(regions);
        _evolveStrength = strength;
        EvolveRound     = 0;
        Breed(count);
    }

    private void Breed(int count)
    {
        _evolveKids.Clear();
        for (var i = 0; i < count; ++i)
        {
            _hand = (JObject)_evolveParent!.DeepClone();
            RollRegions(_evolveRegions, _evolveStrength, true);
            _evolveKids.Add(_hand);
        }

        EvolveRound++;
        EvolveShow(0);
        Note = Loc.F("Round {0}: look at each one, then take the one you like best.", EvolveRound);
    }

    /// <summary> Puts one of the variations on (−1: the one they all came from). </summary>
    public void EvolveShow(int index)
    {
        if (_evolveParent is null)
            return;
        EvolveShown = index;
        _hand       = (JObject)(index >= 0 && index < _evolveKids.Count ? _evolveKids[index] : _evolveParent).DeepClone();
        _dirty      = true;
    }

    /// <summary> The shown one becomes the starting point; the next few vary around it, a little less each round. </summary>
    public void EvolvePick()
    {
        if (_evolveParent is null)
            return;
        if (EvolveShown >= 0 && EvolveShown < _evolveKids.Count)
            _evolveParent = _evolveKids[EvolveShown];
        _evolveStrength = MathF.Max(0.25f, _evolveStrength * 0.8f);
        Breed(_evolveKids.Count);
    }

    /// <summary> New variations around the same starting point (none of these was it). </summary>
    public void EvolveAgain()
    {
        if (_evolveParent is not null)
            Breed(_evolveKids.Count);
    }

    /// <summary> Ends evolving: with keep, what is shown stays; without, you are back where the last pick left you. </summary>
    public void EvolveEnd(bool keep)
    {
        if (_evolveParent is null)
            return;
        if (!keep)
            _hand = _evolveParent;
        _evolveParent = null;
        _evolveKids.Clear();
        EvolveShown = -1;
        _dirty      = true;
    }

    // ---- presets: a few sliders at once ----

    public static readonly (string Key, string Group, (string Slider, float Value)[] Moves)[] Presets =
    {
        ("softer", "Face", new[] { ("cheeks", 0.5f), ("jaw", -0.4f), ("lips", 0.3f), ("eyes", 0.3f), ("chin", -0.3f), ("nose", -0.2f) }),
        ("sharper", "Face", new[] { ("cheeks", -0.4f), ("jaw", -0.2f), ("chin", 0.4f), ("nose", 0.1f), ("brows", 0.2f) }),
        ("doll", "Face", new[] { ("eyes", 0.8f), ("lips", 0.4f), ("nose", -0.5f), ("jaw", -0.5f), ("chin", -0.4f), ("mouth", -0.3f), ("head", 0.3f) }),
        ("mature", "Face", new[] { ("eyes", -0.3f), ("chin", 0.3f), ("cheeks", -0.3f), ("nose", 0.2f), ("lips", -0.1f) }),
        ("curvier", "Body", new[] { ("chest", 0.5f), ("hips", 0.6f), ("waist", -0.5f), ("thighs", 0.4f) }),
        ("athletic", "Body", new[] { ("arms", 0.4f), ("thighs", 0.3f), ("calves", 0.3f), ("shoulders", 0.4f), ("waist", -0.2f) }),
        ("slender", "Body", new[] { ("waist", -0.4f), ("arms", -0.3f), ("thighs", -0.3f), ("calves", -0.3f), ("hips", -0.2f) }),
    };

    /// <summary> Adds a preset to where the sliders stand (strength 1: as written; negative: the opposite). </summary>
    public void ApplyPreset(string key, float strength)
    {
        var preset = Array.Find(Presets, p => p.Key == key);
        if (preset.Moves is null)
            return;
        Checkpoint();
        foreach (var (slider, value) in preset.Moves)
            SetSlider(slider, SliderValue(slider) + value * strength);
    }

    // ---- one side onto the other ----

    /// <summary> Copies what you did by hand on one side onto the other, mirrored (fromLeft: the parts ending in _l are the model). </summary>
    public int MirrorSide(bool fromLeft)
    {
        Checkpoint();
        var count = 0;
        foreach (var bone in Live.Values)
        {
            var isLeft = bone.Name.EndsWith("_l", StringComparison.Ordinal);
            if (isLeft != fromLeft || !bone.Name.EndsWith(fromLeft ? "_l" : "_r", StringComparison.Ordinal))
                continue;
            if (SculptBones.MirrorOf(bone.Name) is not { } twin || !Live.TryGetValue(twin, out var other))
                continue;
            var (m, t, s)    = Get(bone.Name);
            var (m0, t0, s0) = Read(_base[bone.Name]);
            var (bm, bt, bs) = Read(_base[twin]);

            // the move, in the model's directions, flipped left to right
            var move = Vector3.Transform(m - m0, Stand(bone));
            var mine = ToLocal(other, move with { X = -move.X });

            // the turn: as a turn in the model, flipped (a mirror keeps X and W and flips Y and Z), back into the twin's own directions
            var dq    = Quaternion.Inverse(ToQuaternion(t0)) * ToQuaternion(t);
            var local = SculptMath.MirrorTurn(dq, Stand(bone), Stand(other));
            var turn  = ToEuler(ToQuaternion(bt) * local);

            var size = new Vector3(bs.X * Safe(s.X, s0.X), bs.Y * Safe(s.Y, s0.Y), bs.Z * Safe(s.Z, s0.Z));
            Set(twin, bm + mine, turn, size);
            count++;
        }

        Note = count == 0 ? Loc.T("No left and right parts found.") : Loc.F("{0} part(s) copied to the other side.", count);
        return count;

        static float Safe(float a, float b)
            => b <= 0.0001f ? 1 : a / b;
    }

    /// <summary> A touch of difference between left and right in the face, as real faces have. </summary>
    public void TouchOfAsymmetry()
    {
        Checkpoint();
        var count = 0;
        foreach (var bone in Live.Values)
        {
            if (!bone.Name.EndsWith("_l", StringComparison.Ordinal) || Locked.Contains(bone.Name))
                continue;
            if (SculptBones.Info(bone.Name) is not { Family: "Face" or "Eyes" or "Cheeks" or "Lips" or "Jaw" })
                continue;
            var side = _random.Next(2) == 0 ? bone.Name : SculptBones.MirrorOf(bone.Name);
            if (side is null || !Live.TryGetValue(side, out var one))
                continue;
            var (m, t, s) = Get(one.Name);
            Set(one.Name, m + ToLocal(one, new Vector3(Gauss(), Gauss(), Gauss()) * 0.00025f), t, s * new Vector3(MathF.Exp(Gauss() * 0.012f)));
            count++;
        }

        Note = Loc.F("{0} part(s) nudged a hair, each on one side only.", count);
    }

    // ---- your Customize+ templates ----

    /// <summary> The templates of the profile the sculpt stands on (null: there is no saved profile to switch). </summary>
    public List<(Guid Id, string Name, int Bones, bool On)>? Templates()
        => Active && _profileId is { } id ? _ipc.Templates(id) : null;

    /// <summary> Switches one of your Customize+ templates on or off (this does change Customize+), and moves the sculpt onto the result. </summary>
    public bool SetTemplate(Guid template, bool on)
    {
        if (_profileId is not { } id || !_ipc.SetTemplate(id, template, on))
        {
            Note = Loc.T("Customize+ did not switch that template.");
            return false;
        }

        if (_ipc.ProfileJson(id) is { } json)
        {
            try
            {
                Rebase(JObject.Parse(json)["Bones"] as JObject ?? new JObject());
            }
            catch
            {
                // keep the old ground
            }
        }

        return true;
    }

    /// <summary> Puts the hand layer onto new ground: every change you made stays the same change, on top of the new start. </summary>
    private void Rebase(JObject ground)
    {
        Checkpoint();
        var result = (JObject)ground.DeepClone();
        foreach (var name in _hand.Properties().Select(p => p.Name).Union(_base.Properties().Select(p => p.Name)).ToList())
        {
            var (m, t, s)    = Read(_hand[name]);
            var (m0, t0, s0) = Read(_base[name]);
            if (!Differs((m, t, s), (m0, t0, s0)))
                continue;
            var (gm, gt, gs) = Read(ground[name]);
            Write(result, name, gm + (m - m0), gt + (t - t0),
                new Vector3(gs.X * Div(s.X, s0.X), gs.Y * Div(s.Y, s0.Y), gs.Z * Div(s.Z, s0.Z)));
        }

        _base  = ground;
        _hand  = result;
        _dirty = true;

        static float Div(float a, float b)
            => b <= 0.0001f ? 1 : a / b;
    }

    // ---- history: what you made, kept by itself ----

    public sealed record HistoryEntry(DateTime Utc, int Parts, string Snapshot);

    private List<HistoryEntry>? _history;
    private DateTime _historyAt;
    private string _historyLast = string.Empty;

    private string HistoryFile => Path.Combine(_plugin.DataDirectory, "history.json");

    public IReadOnlyList<HistoryEntry> History
    {
        get
        {
            if (_history is not null)
                return _history;
            try
            {
                _history = File.Exists(HistoryFile)
                    ? Newtonsoft.Json.JsonConvert.DeserializeObject<List<HistoryEntry>>(File.ReadAllText(HistoryFile)) ?? new List<HistoryEntry>()
                    : new List<HistoryEntry>();
            }
            catch
            {
                _history = new List<HistoryEntry>();
            }

            return _history;
        }
    }

    /// <summary> Remembers how the sculpt looks now (every few minutes and when you stop), newest first, thirty at most. </summary>
    private void AddHistory()
    {
        _historyAt = DateTime.UtcNow;
        var snapshot = Snapshot();
        if (snapshot == _historyLast)
            return;
        _historyLast = snapshot;
        var list  = (List<HistoryEntry>)History;
        var parts = _hand.Properties().Count(p => IsEdited(p.Name)) + _sliders.Count;
        list.Insert(0, new HistoryEntry(DateTime.UtcNow, parts, snapshot));
        while (list.Count > 30)
            list.RemoveAt(list.Count - 1);
        try
        {
            File.WriteAllText(HistoryFile, Newtonsoft.Json.JsonConvert.SerializeObject(list));
        }
        catch
        {
            // kept for this session only
        }
    }

    public void RestoreHistory(HistoryEntry entry)
    {
        Checkpoint();
        try
        {
            Restore(entry.Snapshot);
        }
        catch
        {
            Note = Loc.T("That entry could not be read.");
        }
    }

    // ---- sharing a shape as text ----

    private const string CodePrefix = "SCULPT1:";

    /// <summary> How you look now as a line of text someone else with Sculpt+ can paste in. </summary>
    public string ExportCode(string name)
    {
        var o = new JObject
        {
            ["n"] = name, ["h"] = _hand, ["s"] = JObject.FromObject(_sliders), ["o"] = Compose(),
        };
        return CodePrefix + Pack(o.ToString(Newtonsoft.Json.Formatting.None));
    }

    public string ExportCode(SculptShape shape)
    {
        var o = new JObject
        {
            ["n"] = shape.Name, ["h"] = Parse(shape.Hand), ["s"] = JObject.FromObject(shape.Sliders), ["o"] = Parse(shape.Output),
        };
        return CodePrefix + Pack(o.ToString(Newtonsoft.Json.Formatting.None));
    }

    /// <summary> Adds a shape from such a line of text to your kept shapes; null with a reason in Note when it is not one. </summary>
    public SculptShape? ImportCode(string text)
    {
        text = text.Trim();
        if (!text.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            Note = Loc.T("The clipboard does not hold a Sculpt+ shape (it starts with SCULPT1:).");
            return null;
        }

        try
        {
            var o    = JObject.Parse(Unpack(text[CodePrefix.Length..]));
            var name = (o["n"]?.Value<string>() ?? "Shape").Trim();
            if (name.Length is 0 or > 48)
                name = "Shape";
            var free = name;
            for (var i = 2; Config.Shapes.Any(s => s.Name.Equals(free, StringComparison.OrdinalIgnoreCase)); ++i)
                free = $"{name} {i}";
            var shape = new SculptShape
            {
                Name    = free,
                Hand    = Bones(o["h"]).ToString(Newtonsoft.Json.Formatting.None),
                Output  = Bones(o["o"]).ToString(Newtonsoft.Json.Formatting.None),
                Sliders = (o["s"] as JObject)?.Properties().Where(p => Array.Exists(Sliders, x => x.Key == p.Name))
                    .ToDictionary(p => p.Name, p => Math.Clamp(p.Value.Value<float>(), -2f, 2f)) ?? new Dictionary<string, float>(),
            };
            Config.Shapes.Add(shape);
            _plugin.SaveConfig();
            Note = Loc.F("Added the shape \"{0}\".", shape.Name);
            return shape;
        }
        catch
        {
            Note = Loc.T("That shape text is damaged.");
            return null;
        }

        // only bones with numbers come in: nothing else from a stranger's text reaches Customize+
        static JObject Bones(JToken? t)
        {
            var result = new JObject();
            if (t is not JObject o)
                return result;
            foreach (var p in o.Properties().Take(600))
                if (p.Name.Length is > 0 and <= 64 && p.Value is JObject b)
                {
                    var (m, r, s) = Read(b);
                    Write(result, p.Name, m, r, s);
                }

            return result;
        }
    }

    private static string Pack(string json)
        => SculptMath.Pack(json);

    private static string Unpack(string code)
        => SculptMath.Unpack(code);

    private void TryDelete(string picture)
    {
        if (picture.Length == 0)
            return;
        try
        {
            File.Delete(Path.Combine(_plugin.DataDirectory, picture));
        }
        catch
        {
            // a leftover file harms nobody
        }
    }

    private void Roll(LiveBone bone, Vector3 wideHighDeep, Vector3 modelMove, bool fromCurrent)
    {
        var (m, t, s) = fromCurrent ? Get(bone.Name) : Read(_base[bone.Name]);
        Set(bone.Name, m + ToLocal(bone, modelMove), t, s * LocalSize(bone, wideHighDeep));
    }

    private float Gauss()
    {
        // two uniform numbers make one bell-shaped one, cut at 2.5 so nothing goes wild
        var u = 1.0 - _random.NextDouble();
        var v = _random.NextDouble();
        return (float)Math.Clamp(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v), -2.5, 2.5);
    }

    // ---- blending ----

    /// <summary> Starts a blend from how you look now towards a target (null: how you started; else a kept shape). </summary>
    public void BeginBlend(SculptShape? target)
    {
        Checkpoint();
        _blendFrom = Compose();
        _blendTo   = target is null ? (JObject)_base.DeepClone() : Parse(target.Output);
        _sliders.Clear(); // they are part of "now" from here on
        _hand  = (JObject)_blendFrom.DeepClone();
        _dirty = true;
    }

    public bool Blending => _blendFrom is not null && _blendTo is not null;

    /// <summary> 0: how you looked when the blend began; 1: the target; beyond 1 or below 0 goes further that way. </summary>
    public void SetBlend(float t)
    {
        if (_blendFrom is null || _blendTo is null)
            return;
        var result = new JObject();
        foreach (var name in _blendFrom.Properties().Select(p => p.Name).Union(_blendTo.Properties().Select(p => p.Name)))
        {
            var a = Read(_blendFrom[name]);
            var b = Read(_blendTo[name]);
            var size = new Vector3(Mix(a.Size.X, b.Size.X), Mix(a.Size.Y, b.Size.Y), Mix(a.Size.Z, b.Size.Z));
            if ((_blendFrom[name] ?? _blendTo[name]) is JObject keep)
                result[name] = keep.DeepClone();
            Write(result, name, Vector3.Lerp(a.Move, b.Move, t), Vector3.Lerp(a.Turn, b.Turn, t), size);
        }

        _hand  = result;
        _dirty = true;

        float Mix(float x, float y)
            => MathF.Exp(MathF.Log(MathF.Max(x, 0.01f)) * (1 - t) + MathF.Log(MathF.Max(y, 0.01f)) * t);
    }

    public void EndBlend()
        => _blendFrom = _blendTo = null;

    // ---- kept shapes ----

    public SculptShape? WornShape()
        => Config.Worn.Length == 0 ? null : Config.Shapes.FirstOrDefault(s => s.Name == Config.Worn);

    /// <summary> Keeps how you look now under a name (an existing one is replaced). </summary>
    public SculptShape Save(string name)
    {
        var shape = Config.Shapes.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (shape is null)
            Config.Shapes.Add(shape = new SculptShape { Name = name });
        shape.Hand     = _hand.ToString(Newtonsoft.Json.Formatting.None);
        shape.Sliders  = new Dictionary<string, float>(_sliders);
        shape.Output   = Compose().ToString(Newtonsoft.Json.Formatting.None);
        shape.SavedUtc = DateTime.UtcNow;
        _kept = Snapshot();
        _plugin.SaveConfig();
        return shape;
    }

    private string _kept = string.Empty;

    /// <summary> True while the sculpt has changes that are in no kept shape (asked when you stop, not every frame). </summary>
    public bool Unkept
        => Active && HasChanges && Snapshot() != _kept;

    /// <summary> Starts sculpting and puts the last sculpt back (the one you stopped without keeping). </summary>
    public bool ContinueLast()
    {
        if (!Start())
            return false;
        LoadLast();
        Config.LastUnkept = false;
        return true;
    }

    /// <summary> While sculpting: continue from a kept shape. </summary>
    public void Load(SculptShape shape)
    {
        _hand = Parse(shape.Hand);
        _sliders.Clear();
        foreach (var (k, v) in shape.Sliders)
            _sliders[k] = v;
        _blendFrom = _blendTo = null;
        _dirty = true;
        _kept  = Snapshot();
    }

    /// <summary> Puts a kept shape on you and keeps it on (through area changes and logins) until you take it off. </summary>
    public bool Wear(SculptShape shape)
    {
        if (!_ipc.Available)
        {
            Note = Loc.T("Customize+ is not running (it does the actual reshaping).");
            return false;
        }

        Config.Worn = shape.Name;
        _plugin.SaveConfig();
        if (Active)
        {
            Load(shape);
            return true;
        }

        _wornFor = _plugin.Objects.LocalPlayer?.Address ?? nint.Zero;
        return Send(shape.Output);
    }

    public bool WearByName(string? name)
        => !string.IsNullOrEmpty(name) && Config.Shapes.FirstOrDefault(s => s.Name == name) is { } shape && Wear(shape);

    /// <summary> Something else may have removed the temporary profile: put the worn shape back on the next tick. </summary>
    public void Reapply()
    {
        _wornFor   = nint.Zero;
        _wornRetry = DateTime.MinValue;
    }

    public void TakeShapeOff()
    {
        Config.Worn = string.Empty;
        _plugin.SaveConfig();
        if (!Active)
            _ipc.DeleteTemporary(0);
    }

    public void Delete(SculptShape shape)
    {
        if (Config.Worn == shape.Name)
            TakeShapeOff();
        Config.Shapes.Remove(shape);
        TryDelete(shape.Picture);
        _plugin.SaveConfig();
    }

    /// <summary> A worn shape is a temporary profile: it is put back when you log in or Customize+ lost it. </summary>
    private void KeepWorn()
    {
        if (Config.Worn.Length == 0 || _plugin.Zoning)
            return;
        var address = _plugin.Objects.LocalPlayer?.Address ?? nint.Zero;
        if (address == nint.Zero)
        {
            _wornFor = nint.Zero;
            return;
        }

        if (address == _wornFor || DateTime.UtcNow < _wornRetry)
            return;
        _wornRetry = DateTime.UtcNow.AddSeconds(5);
        if (WornShape() is { } shape && _ipc.Available && Send(shape.Output))
            _wornFor = address;
    }

    // ---- out to Customize+ ----

    /// <summary> The whole shape as a Customize+ template for its "import from clipboard" button. </summary>
    public string ExportTemplate(string name)
    {
        var bones = new JObject();
        foreach (var p in Compose().Properties())
        {
            if (p.Value is not JObject o)
                continue;
            var b = new JObject
            {
                ["Translation"] = o["Translation"] ?? Json(Vector3.Zero),
                ["Rotation"]    = o["Rotation"] ?? Json(Vector3.Zero),
                ["Scaling"]     = o["Scaling"] ?? Json(Vector3.One),
            };
            var independent = o["ChildScaleIndependent"]?.Value<bool>() ?? false;
            if (independent)
                b["ChildScaling"] = o["ChildScaling"] ?? Json(Vector3.One);
            b["ChildScalingIndependent"] = independent;
            b["PropagateTranslation"]    = o["PropagateTranslation"]?.Value<bool>() ?? false;
            b["PropagateRotation"]       = o["PropagateRotation"]?.Value<bool>() ?? false;
            b["PropagateScale"]          = o["PropagateScale"]?.Value<bool>() ?? false;
            bones[p.Name] = b;
        }

        var now = DateTimeOffset.UtcNow;
        var template = new JObject
        {
            ["Version"] = 7, ["UniqueId"] = Guid.NewGuid(), ["CreationDate"] = now, ["ModifiedDate"] = now, ["Name"] = name, ["Bones"] = bones,
            ["IsWriteProtected"] = false, ["Source"] = 0,
        };
        var bytes = Encoding.UTF8.GetBytes(template.ToString(Newtonsoft.Json.Formatting.None));
        using var compressed = new MemoryStream();
        using (var zip = new GZipStream(compressed, CompressionMode.Compress))
        {
            zip.WriteByte(7);
            zip.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(compressed.ToArray());
    }

    private static JObject Parse(string json)
    {
        try
        {
            return JObject.Parse(json);
        }
        catch
        {
            return new JObject();
        }
    }

    private static Vector3 Vec(JToken? t, float fallback)
        => t is JObject o
            ? new Vector3(o["X"]?.Value<float>() ?? fallback, o["Y"]?.Value<float>() ?? fallback, o["Z"]?.Value<float>() ?? fallback)
            : new Vector3(fallback);

    private static JObject Json(Vector3 v)
        => new() { ["X"] = v.X, ["Y"] = v.Y, ["Z"] = v.Z };

    private static Vector3 Clamp(Vector3 v, float min, float max)
        => new(Math.Clamp(v.X, min, max), Math.Clamp(v.Y, min, max), Math.Clamp(v.Z, min, max));

    public void Dispose()
    {
        if (Active)
            Stop();
    }
}
