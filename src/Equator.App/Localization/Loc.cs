using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Equator.App.Localization;

/// <summary>
/// Interface strings. Each language is a flat JSON table (Strings/&lt;code&gt;.json, embedded in the assembly);
/// a key missing from a translation falls back to English, then to the key itself. Command names, maths and
/// notebook contents are never translated: worksheets have to run the same in every language.
/// </summary>
public static class Loc
{
    /// <summary>Supported languages: code and name in that language (the most widely spoken ones).</summary>
    public static readonly (string Code, string NativeName)[] Languages =
    [
        ("en", "English"),
        ("zh-Hans", "中文（简体）"),
        ("hi", "हिन्दी"),
        ("es", "Español"),
        ("fr", "Français"),
        ("ar", "العربية"),
        ("bn", "বাংলা"),
        ("pt-BR", "Português (Brasil)"),
    ];

    private static readonly Dictionary<string, string> English = Load("en");
    private static Dictionary<string, string> _table = English;

    /// <summary>The language in use.</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>The user's choice: a language code, or "" to follow Windows.</summary>
    public static string Preference { get; private set; } = "";

    public static bool IsRightToLeft => Current == "ar";

    /// <summary>Raised on the UI thread after the language changes.</summary>
    public static event EventHandler? Changed;

    /// <summary>Picks the saved language (or EQUATOR_LANG, or the Windows display language) at startup.</summary>
    public static void Initialize()
    {
        Preference = Settings.Language;
        var forced = Environment.GetEnvironmentVariable("EQUATOR_LANG");
        Apply(Resolve(forced is { Length: > 0 } ? forced : Preference));
    }

    /// <summary>Switches language ("" follows Windows), remembers the choice and refreshes the interface.</summary>
    public static void SetPreference(string code)
    {
        Preference = code;
        Settings.Language = code;
        var resolved = Resolve(code);
        if (resolved == Current) return;
        Apply(resolved);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key) =>
        _table.TryGetValue(key, out var s) ? s : English.TryGetValue(key, out var e) ? e : key;

    /// <summary>A translated string, or <paramref name="fallback"/> when no table has the key.</summary>
    public static string GetOr(string key, string fallback) =>
        _table.TryGetValue(key, out var s) ? s : English.TryGetValue(key, out var e) ? e : fallback;

    public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Maps a language tag (en-GB, zh-CN, pt-PT, ...) to the closest supported language.</summary>
    public static string Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) tag = CultureInfo.CurrentUICulture.Name;
        foreach (var (code, _) in Languages)
            if (code.Equals(tag, StringComparison.OrdinalIgnoreCase)) return code;
        var lower = tag.ToLowerInvariant();
        if (lower.StartsWith("zh")) return "zh-Hans"; // Traditional Chinese readers get Simplified rather than English.
        if (lower.StartsWith("pt")) return "pt-BR";
        var primary = lower.Split('-')[0];
        foreach (var (code, _) in Languages)
            if (code.Equals(primary, StringComparison.OrdinalIgnoreCase)) return code;
        return "en";
    }

    private static void Apply(string code)
    {
        Current = code;
        _table = code == "en" ? English : Load(code);
    }

    private static Dictionary<string, string> Load(string code)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Strings.{code}.json");
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}

/// <summary>Small per-user settings file (%LOCALAPPDATA%\Equator\settings.json).</summary>
internal static class Settings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Equator", "settings.json");

    public static string Language
    {
        get => Read().GetValueOrDefault("language", "");
        set { var all = Read(); all["language"] = value; Write(all); }
    }

    private static Dictionary<string, string> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Write(Dictionary<string, string> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log("could not save settings: " + ex.Message);
        }
    }
}
