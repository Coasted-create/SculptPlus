using System.Reflection;
using System.Text.Json;

namespace SculptPlus;

/// <summary>
/// Translations. Every text the user sees goes through <see cref="T"/> (fixed text) or <see cref="F"/> (text with values,
/// "{0}" placeholders like string.Format). The English text is the key; Lang/&lt;code&gt;.json (embedded) maps it to the
/// translation. Missing entries fall back to English. An ImGui id after "##" is kept as it is.
/// No Dalamud types here, so the tests can use it too.
/// </summary>
public static class Loc
{
    /// <summary> Languages to choose from (code, name in that language). </summary>
    public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("de", "Deutsch") };

    private static Dictionary<string, string> _table = new(StringComparer.Ordinal);

    /// <summary> The language in use ("en", "de"). </summary>
    public static string Language { get; private set; } = "en";

    /// <summary> Bumped when the language changes (for caches of translated text). </summary>
    public static int Version { get; private set; }

    /// <summary> Number of translated texts (for the settings line). </summary>
    public static int Count
        => _table.Count;

    /// <summary> Switches the language; unknown codes and missing files mean English. </summary>
    public static void SetLanguage(string? code)
    {
        code = Languages.Any(l => l.Code == code) ? code! : "en";
        if (code == Language && (code == "en" || _table.Count > 0))
            return;

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        if (code != "en")
        {
            try
            {
                using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"SculptPlus.Lang.{code}.json");
                if (stream is not null)
                    foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new())
                        if (key.Length > 0 && !string.IsNullOrEmpty(value))
                            table[key] = value;
            }
            catch
            {
                // a broken table: stay in English rather than break the window
                table.Clear();
            }
        }

        _table   = table;
        Language = table.Count > 0 ? code : "en";
        Version++;
    }

    /// <summary> The translation of a fixed text ("Wear", "Try on##try3"). </summary>
    public static string T(string text)
    {
        var table = _table;
        if (table.Count == 0 || string.IsNullOrEmpty(text))
            return text;

        var cut = text.IndexOf("##", StringComparison.Ordinal);
        if (cut < 0)
            return table.TryGetValue(text, out var t) ? t : text;
        if (cut == 0)
            return text;
        return table.TryGetValue(text[..cut], out var tr) ? tr + text[cut..] : text;
    }

    /// <summary> The translation of a text with values: F("Wearing {0}", name). </summary>
    public static string F(string format, params object?[] args)
    {
        var translated = T(format);
        try
        {
            return string.Format(translated, args);
        }
        catch (FormatException)
        {
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }
    }

    /// <summary> A display name for an enum value (its English name is the key). </summary>
    public static string Name<TEnum>(TEnum value) where TEnum : struct, Enum
        => T(value.ToString());
}
