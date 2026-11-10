using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace SculptPlus;

/// <summary> FontAwesome icons (the default font has no ★ or ✕). </summary>
public static class Icons
{
    public static void Text(FontAwesomeIcon icon, Vector4 color)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color, icon.ToIconString());
        ImGui.PopFont();
    }

    public static bool Button(FontAwesomeIcon icon, string id, Vector4? color = null)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        if (color is { } c)
            ImGui.PushStyleColor(ImGuiCol.Text, c);
        var clicked = ImGui.Button($"{icon.ToIconString()}##{id}");
        if (color is not null)
            ImGui.PopStyleColor();
        ImGui.PopFont();
        return clicked;
    }

    public static bool Small(FontAwesomeIcon icon, string id)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var clicked = ImGui.SmallButton($"{icon.ToIconString()}##{id}");
        ImGui.PopFont();
        return clicked;
    }

    public static void Draw(ImDrawListPtr draw, Vector2 pos, uint color, FontAwesomeIcon icon)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        draw.AddText(pos, color, icon.ToIconString());
        ImGui.PopFont();
    }

    public static Vector2 Size(FontAwesomeIcon icon)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var size = ImGui.CalcTextSize(icon.ToIconString());
        ImGui.PopFont();
        return size;
    }

    /// <summary> A heading with a line under it. </summary>
    public static void Section(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(Theme.Accent, text);
        ImGui.Separator();
    }

    /// <summary> Coloured text that wraps at the edge of the window. </summary>
    public static void Wrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
