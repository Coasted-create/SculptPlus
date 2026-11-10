using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace SculptPlus;

/// <summary>
/// The look: a dark plum "boutique" palette with a rose accent, rounded glassy panels, the game's own
/// Trump Gothic font for titles, and hand-drawn pills, buttons and chips.
/// </summary>
public sealed class Theme : IDisposable
{
    // ---- palette ----
    public static readonly Vector4 Bg         = Rgb(0x16101B, 0.96f);
    public static readonly Vector4 Panel      = Rgb(0x1E1724, 0.92f);
    public static readonly Vector4 Card       = Rgb(0x261D2D);
    public static readonly Vector4 Hover      = Rgb(0x33273D);
    public static readonly Vector4 Line       = Rgb(0x3D2F49);
    public static readonly Vector4 Accent     = Rgb(0xF0A6BE);
    public static readonly Vector4 AccentDeep = Rgb(0xC8668A);
    public static readonly Vector4 Gold       = Rgb(0xEBC98E);
    public static readonly Vector4 Text       = Rgb(0xF4ECF2);
    public static readonly Vector4 Muted      = Rgb(0xA394AB);
    public static readonly Vector4 Faint      = Rgb(0x6F6277);
    public static readonly Vector4 Good       = Rgb(0x92DDB6);
    public static readonly Vector4 Bad        = Rgb(0xF29393);
    public static readonly Vector4 Warn       = Rgb(0xF2C27E);

    public enum Kind { Primary, Secondary, Ghost, Danger }

    private int _colors;
    private int _vars;

    public IFontHandle Title { get; }
    public IFontHandle Heading { get; }

    public Theme(IUiBuilder ui)
    {
        Title   = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.TrumpGothic34));
        Heading = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.TrumpGothic23));
    }

    public static float S
        => ImGuiHelpers.GlobalScale;

    public static Vector4 Rgb(uint rgb, float alpha = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    public static uint U32(Vector4 c, float alphaMul = 1f)
        => ImGui.GetColorU32(c with { W = c.W * alphaMul });

    // ---- window styling ----

    /// <summary> Pushes the palette and rounded style; call before the window begins (Window.PreDraw). </summary>
    public void Push()
    {
        Var(ImGuiStyleVar.WindowRounding, 14f);
        Var(ImGuiStyleVar.ChildRounding, 12f);
        Var(ImGuiStyleVar.FrameRounding, 8f);
        Var(ImGuiStyleVar.PopupRounding, 10f);
        Var(ImGuiStyleVar.GrabRounding, 8f);
        Var(ImGuiStyleVar.TabRounding, 8f);
        Var(ImGuiStyleVar.ScrollbarRounding, 8f);
        Var(ImGuiStyleVar.ScrollbarSize, 10f * S);
        Var(ImGuiStyleVar.WindowBorderSize, 1f);
        Var(ImGuiStyleVar.ChildBorderSize, 0f);
        Var(ImGuiStyleVar.FrameBorderSize, 0f);
        Var(ImGuiStyleVar.PopupBorderSize, 1f);
        Var(ImGuiStyleVar.WindowPadding, new Vector2(16, 12) * S);
        Var(ImGuiStyleVar.FramePadding, new Vector2(10, 6) * S);
        Var(ImGuiStyleVar.ItemSpacing, new Vector2(10, 8) * S);
        Var(ImGuiStyleVar.ItemInnerSpacing, new Vector2(8, 6) * S);
        Var(ImGuiStyleVar.WindowTitleAlign, new Vector2(0.5f, 0.5f));

        Col(ImGuiCol.WindowBg, Bg);
        Col(ImGuiCol.ChildBg, Vector4.Zero);
        Col(ImGuiCol.PopupBg, Rgb(0x1E1724, 0.98f));
        Col(ImGuiCol.Border, Line with { W = 0.8f });
        Col(ImGuiCol.Text, Text);
        Col(ImGuiCol.TextDisabled, Muted);
        Col(ImGuiCol.TitleBg, Rgb(0x120D16, 0.95f));
        Col(ImGuiCol.TitleBgActive, Rgb(0x1B1220, 0.98f));
        Col(ImGuiCol.TitleBgCollapsed, Rgb(0x120D16, 0.8f));
        Col(ImGuiCol.FrameBg, Card);
        Col(ImGuiCol.FrameBgHovered, Hover);
        Col(ImGuiCol.FrameBgActive, Rgb(0x3B2C46));
        Col(ImGuiCol.Button, Card);
        Col(ImGuiCol.ButtonHovered, Hover);
        Col(ImGuiCol.ButtonActive, AccentDeep with { W = 0.6f });
        Col(ImGuiCol.Header, Card);
        Col(ImGuiCol.HeaderHovered, Hover);
        Col(ImGuiCol.HeaderActive, AccentDeep with { W = 0.45f });
        Col(ImGuiCol.CheckMark, Accent);
        Col(ImGuiCol.SliderGrab, AccentDeep);
        Col(ImGuiCol.SliderGrabActive, Accent);
        Col(ImGuiCol.Separator, Line);
        Col(ImGuiCol.SeparatorHovered, AccentDeep);
        Col(ImGuiCol.SeparatorActive, Accent);
        Col(ImGuiCol.ScrollbarBg, Vector4.Zero);
        Col(ImGuiCol.ScrollbarGrab, Line);
        Col(ImGuiCol.ScrollbarGrabHovered, Faint);
        Col(ImGuiCol.ScrollbarGrabActive, AccentDeep);
        Col(ImGuiCol.Tab, Card);
        Col(ImGuiCol.TabHovered, Hover);
        Col(ImGuiCol.TabActive, AccentDeep with { W = 0.7f });
        Col(ImGuiCol.TableHeaderBg, Card);
        Col(ImGuiCol.TableRowBg, Vector4.Zero);
        Col(ImGuiCol.TableRowBgAlt, Rgb(0xFFFFFF, 0.025f));
        Col(ImGuiCol.TableBorderLight, Line with { W = 0.5f });
        Col(ImGuiCol.TableBorderStrong, Line);
        Col(ImGuiCol.TextSelectedBg, AccentDeep with { W = 0.45f });
        Col(ImGuiCol.ResizeGrip, Vector4.Zero);
        Col(ImGuiCol.ResizeGripHovered, AccentDeep with { W = 0.6f });
        Col(ImGuiCol.ResizeGripActive, Accent);
    }

    /// <summary> Pops everything <see cref="Push"/> pushed (Window.PostDraw). </summary>
    public void Pop()
    {
        if (_colors > 0)
            ImGui.PopStyleColor(_colors);
        if (_vars > 0)
            ImGui.PopStyleVar(_vars);
        _colors = 0;
        _vars   = 0;
    }

    private void Var(ImGuiStyleVar v, float value)
    {
        ImGui.PushStyleVar(v, value);
        _vars++;
    }

    private void Var(ImGuiStyleVar v, Vector2 value)
    {
        ImGui.PushStyleVar(v, value);
        _vars++;
    }

    private void Col(ImGuiCol c, Vector4 value)
    {
        ImGui.PushStyleColor(c, value);
        _colors++;
    }

    // ---- text ----

    public void TitleText(string text, Vector4? color = null)
    {
        using (Title.Push())
            ImGui.TextColored(color ?? Text, text);
    }

    public void HeadingText(string text, Vector4? color = null)
    {
        using (Heading.Push())
            ImGui.TextColored(color ?? Text, text);
    }

    /// <summary> A section heading in the title font with a soft rose line under it. </summary>
    public void Section(string text)
    {
        ImGui.Spacing();
        using (Heading.Push())
            ImGui.TextColored(Accent, text);
        var min = ImGui.GetItemRectMin();
        var w   = ImGui.GetContentRegionAvail().X;
        var y   = ImGui.GetItemRectMax().Y + 2;
        ImGui.GetWindowDrawList().AddRectFilledMultiColor(new Vector2(min.X, y), new Vector2(min.X + w, y + 1.5f),
            U32(Accent, 0.7f), U32(Accent, 0f), U32(Accent, 0f), U32(Accent, 0.7f));
        ImGui.Dummy(new Vector2(1, 4 * S));
    }

    // ---- controls ----

    /// <summary> A rounded, hand-drawn button with an optional icon. </summary>
    public static bool Button(string label, Vector2 size, Kind kind = Kind.Secondary, FontAwesomeIcon icon = FontAwesomeIcon.None, bool enabled = true, string? id = null)
    {
        var iconText  = icon == FontAwesomeIcon.None ? string.Empty : icon.ToIconString();
        var iconSize  = iconText.Length > 0 ? Icons.Size(icon) : Vector2.Zero;
        var textSize  = ImGui.CalcTextSize(label);
        var gap       = iconText.Length > 0 && label.Length > 0 ? 7 * S : 0;
        var content   = new Vector2(iconSize.X + gap + textSize.X, Math.Max(iconSize.Y, textSize.Y));
        if (size.X <= 0)
            size.X = content.X + 28 * S;
        if (size.Y <= 0)
            size.Y = ImGui.GetFrameHeight() + 6 * S;

        var clicked = ImGui.InvisibleButton(id ?? $"##btn{label}{kind}", size) && enabled;
        var hovered = enabled && ImGui.IsItemHovered();
        var active  = enabled && ImGui.IsItemActive();
        var min     = ImGui.GetItemRectMin();
        var max     = ImGui.GetItemRectMax();
        var draw    = ImGui.GetWindowDrawList();
        var r       = size.Y / 2.6f;

        Vector4 fill, border, fg;
        switch (kind)
        {
            case Kind.Primary:
                fill   = active ? AccentDeep * 0.9f : hovered ? Rgb(0xD97798) : AccentDeep;
                border = Accent with { W = hovered ? 0.9f : 0.5f };
                fg     = Rgb(0xFFF6FA);
                break;
            case Kind.Danger:
                fill   = hovered ? Rgb(0x6E2B36) : Rgb(0x4A2029);
                border = Bad with { W = 0.5f };
                fg     = Rgb(0xFFD9DD);
                break;
            case Kind.Ghost:
                fill   = hovered ? Hover : Vector4.Zero;
                border = Line with { W = hovered ? 1f : 0.7f };
                fg     = hovered ? Text : Muted;
                break;
            default:
                fill   = active ? Rgb(0x3B2C46) : hovered ? Hover : Card;
                border = hovered ? Accent with { W = 0.45f } : Line with { W = 0.6f };
                fg     = Text;
                break;
        }

        if (!enabled)
        {
            fill   = fill with { W = fill.W * 0.4f };
            border = border with { W = border.W * 0.4f };
            fg     = Faint;
        }

        if (kind == Kind.Primary && enabled && hovered)
            Glow(draw, min, max, r, Accent, 0.18f);

        draw.AddRectFilled(min, max, U32(fill), r);
        if (kind == Kind.Primary && enabled)
            draw.AddRectFilled(min + new Vector2(1, 1), new Vector2(max.X - 1, min.Y + size.Y * 0.5f), U32(Rgb(0xFFFFFF), 0.08f), r, ImDrawFlags.RoundCornersTop);
        draw.AddRect(min, max, U32(border), r, ImDrawFlags.None, 1f);

        var pos = min + (size - content) / 2;
        if (iconText.Length > 0)
        {
            Icons.Draw(draw, new Vector2(pos.X, min.Y + (size.Y - iconSize.Y) / 2), U32(fg), icon);
            pos.X += iconSize.X + gap;
        }

        draw.AddText(new Vector2(pos.X, min.Y + (size.Y - textSize.Y) / 2), U32(fg), label);
        return clicked;
    }

    /// <summary> A navigation pill (icon + label); returns true when clicked. </summary>
    public static bool NavPill(string label, FontAwesomeIcon icon, bool active, string? badge = null)
    {
        var iconSize = Icons.Size(icon);
        var text     = badge is null ? label : $"{label}";
        var textSize = ImGui.CalcTextSize(text);
        var badgeW   = badge is null ? 0 : ImGui.CalcTextSize(badge).X + 12 * S;
        var gapIcon  = label.Length > 0 ? 8 * S : 0;
        var size     = new Vector2(iconSize.X + gapIcon + textSize.X + badgeW + 26 * S, ImGui.GetFrameHeight() + 8 * S);
        var clicked  = ImGui.InvisibleButton($"##nav{label}{(int)icon}", size);
        var hovered  = ImGui.IsItemHovered();
        var min      = ImGui.GetItemRectMin();
        var max      = ImGui.GetItemRectMax();
        var draw     = ImGui.GetWindowDrawList();
        var r        = size.Y / 2;

        if (active)
        {
            Glow(draw, min, max, r, Accent, 0.12f);
            draw.AddRectFilled(min, max, U32(AccentDeep, 0.85f), r);
            draw.AddRectFilled(min + new Vector2(1, 1), new Vector2(max.X - 1, min.Y + size.Y / 2), U32(Rgb(0xFFFFFF), 0.07f), r, ImDrawFlags.RoundCornersTop);
        }
        else if (hovered)
        {
            draw.AddRectFilled(min, max, U32(Hover), r);
        }

        var fg  = active ? Rgb(0xFFF6FA) : hovered ? Text : Muted;
        var pos = new Vector2(min.X + 13 * S, min.Y);
        Icons.Draw(draw, new Vector2(pos.X, min.Y + (size.Y - iconSize.Y) / 2), U32(active ? fg : hovered ? Accent : Faint), icon);
        pos.X += iconSize.X + gapIcon;
        if (text.Length > 0)
            draw.AddText(new Vector2(pos.X, min.Y + (size.Y - textSize.Y) / 2), U32(fg), text);
        if (badge is not null)
        {
            pos.X += textSize.X + 6 * S;
            var bs   = ImGui.CalcTextSize(badge);
            var bmin = new Vector2(pos.X, min.Y + (size.Y - bs.Y - 2 * S) / 2);
            draw.AddRectFilled(bmin, bmin + new Vector2(bs.X + 8 * S, bs.Y + 2 * S), U32(active ? Rgb(0xFFFFFF) : Line, active ? 0.25f : 1f), 8 * S);
            draw.AddText(bmin + new Vector2(4 * S, 1 * S), U32(fg), badge);
        }

        return clicked;
    }

    /// <summary> A filter chip with a count; returns true when clicked. </summary>
    public static bool Chip(string label, int count, bool active, string id)
    {
        var countText = count < 0 ? string.Empty : count.ToString(); // below 0: a chip without a count
        var ts        = ImGui.CalcTextSize(label);
        var cs        = ImGui.CalcTextSize(countText);
        var size      = new Vector2(ts.X + cs.X + (count < 0 ? 22 : 30) * S, ts.Y + 10 * S);
        var clicked   = ImGui.InvisibleButton($"##chip{id}", size);
        var hovered   = ImGui.IsItemHovered();
        var min       = ImGui.GetItemRectMin();
        var max       = ImGui.GetItemRectMax();
        var draw      = ImGui.GetWindowDrawList();
        var r         = size.Y / 2;
        draw.AddRectFilled(min, max, U32(active ? AccentDeep : hovered ? Hover : Card, active ? 0.9f : 1f), r);
        if (!active)
            draw.AddRect(min, max, U32(Line, 0.8f), r);
        draw.AddText(min + new Vector2(11 * S, 5 * S), U32(active ? Rgb(0xFFF6FA) : hovered ? Text : Muted), label);
        draw.AddText(min + new Vector2(11 * S + ts.X + 7 * S, 5 * S), U32(active ? Rgb(0xFFFFFF) : Faint, active ? 0.75f : 1f), countText);
        return clicked;
    }

    /// <summary> A small status pill drawn at a position (dot + text). Returns its size. </summary>
    public static Vector2 StatusPill(ImDrawListPtr draw, Vector2 pos, string text, Vector4 color)
    {
        var ts   = ImGui.CalcTextSize(text);
        var size = new Vector2(ts.X + 22 * S, ts.Y + 4 * S);
        draw.AddRectFilled(pos, pos + size, U32(Rgb(0x0E0A11), 0.78f), size.Y / 2);
        draw.AddCircleFilled(pos + new Vector2(9 * S, size.Y / 2), 3.2f * S, U32(color));
        draw.AddText(pos + new Vector2(16 * S, 2 * S), U32(Text), text);
        return size;
    }

    /// <summary> A small rounded tag (e.g. "OUTFIT") at a position. Returns its size. </summary>
    public static Vector2 Tag(ImDrawListPtr draw, Vector2 pos, string text, Vector4 color)
    {
        var ts   = ImGui.CalcTextSize(text);
        var size = new Vector2(ts.X + 12 * S, ts.Y + 2 * S);
        draw.AddRectFilled(pos, pos + size, U32(Rgb(0x0E0A11), 0.7f), 6 * S);
        draw.AddText(pos + new Vector2(6 * S, 1 * S), U32(color), text);
        return size;
    }

    /// <summary> Inline chip in the text flow (author, version, category…). </summary>
    public static void InlineChip(string text, Vector4 color)
    {
        var ts   = ImGui.CalcTextSize(text);
        var size = new Vector2(ts.X + 14 * S, ts.Y + 4 * S);
        ImGui.Dummy(size);
        var min  = ImGui.GetItemRectMin();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, U32(Card), size.Y / 2);
        draw.AddRect(min, min + size, U32(Line, 0.7f), size.Y / 2);
        draw.AddText(min + new Vector2(7 * S, 2 * S), U32(color), text);
    }

    // ---- decoration ----

    /// <summary> A soft glow / shadow around a rectangle. </summary>
    public static void Glow(ImDrawListPtr draw, Vector2 min, Vector2 max, float rounding, Vector4 color, float strength, int layers = 5, Vector2 offset = default)
    {
        for (var i = layers; i >= 1; --i)
        {
            var grow = i * 2f * S;
            draw.AddRectFilled(min - new Vector2(grow) + offset, max + new Vector2(grow) + offset, U32(color, strength / layers * (layers - i + 1) / 2f), rounding + grow);
        }
    }

    /// <summary> Dark gradient over the bottom of a picture so text on it stays readable (respects rounded corners). </summary>
    public static void BottomFade(ImDrawListPtr draw, Vector2 min, Vector2 max, float height, float rounding)
    {
        var top    = Math.Max(min.Y, max.Y - height);
        var cut    = max.Y - rounding;
        var dark   = U32(Rgb(0x0B0710), 0.92f);
        var clear  = U32(Rgb(0x0B0710), 0f);
        if (cut > top)
            draw.AddRectFilledMultiColor(new Vector2(min.X, top), new Vector2(max.X, cut), clear, clear, dark, dark);
        draw.AddRectFilled(new Vector2(min.X, cut), max, dark, rounding, ImDrawFlags.RoundCornersBottom);
    }

    /// <summary> A thin fading divider line across the available width. </summary>
    public static void Divider()
    {
        var pos = ImGui.GetCursorScreenPos();
        var w   = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddRectFilledMultiColor(pos, pos + new Vector2(w, 1), U32(Line, 0f), U32(Line), U32(Line), U32(Line, 0f));
        ImGui.Dummy(new Vector2(w, 4 * S));
    }

    /// <summary> Begins a rounded panel (child) with a soft background. </summary>
    public static bool BeginPanel(string id, Vector2 size, bool border = false)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Panel);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10) * S);
        var open = ImGui.BeginChild(id, size, border, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        return open;
    }

    public void Dispose()
    {
        Title.Dispose();
        Heading.Dispose();
    }
}
