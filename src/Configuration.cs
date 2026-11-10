using Dalamud.Configuration;

namespace SculptPlus;

/// <summary> Everything Sculpt+ remembers between sessions. </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary> "auto" follows Dalamud's language; otherwise a code from Loc.Languages. </summary>
    public string Language { get; set; } = "auto";

    /// <summary> The last sculpt (hand layer and sliders), to put back on. </summary>
    public string Last { get; set; } = string.Empty;

    /// <summary> Shapes you kept, and the one you wear (kept on through logins). </summary>
    public List<SculptShape> Shapes { get; set; } = new();
    public string Worn { get; set; } = string.Empty;

    /// <summary> Soft brush radius in metres (0: off). </summary>
    public float Brush { get; set; }

    /// <summary> How fast the character moves while you sculpt: 0 frozen, 1 normal. </summary>
    public float Speed { get; set; }

    /// <summary> The parts whose dots show while sculpting. </summary>
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public HashSet<string> Dots { get; set; } = new() { "lips", "eyes", "brows", "nose", "cheeks", "jaw", "face", "ears", "chest", "torso", "arms", "legs", "feet", "tail" };

    /// <summary> Face dots on the side of the head that is turned away from you are not drawn. </summary>
    public bool HideFarSide { get; set; } = true;

    /// <summary> Size of the dots (1: normal). </summary>
    public float DotSize { get; set; } = 1f;

    /// <summary> Keep it natural: no part goes further from where it started than a sensible range. </summary>
    public bool Natural { get; set; } = true;

    /// <summary> Arrows and rings on the part you hold, for moving and turning it along one direction only. </summary>
    public bool Arrows { get; set; } = true;

    /// <summary> The kept shape that A/B compares the sculpt with (empty: none chosen). </summary>
    public string CompareShape { get; set; } = string.Empty;

    /// <summary> A line with what the mouse sees, for reporting problems. </summary>
    public bool TestLine { get; set; }
}
