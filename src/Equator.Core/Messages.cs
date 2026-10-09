using System.Globalization;

namespace Equator.Core;

/// <summary>
/// Text the engine shows to people: error messages, check() reports, help(). Each message is written in English
/// as a composite-format template ("Index {0} is out of range 1..{1}.") and passed through <see cref="T"/>, so a
/// front end can supply translations keyed by the English template. Without a translator the English is used.
/// </summary>
public static class Messages
{
    /// <summary>Maps an English template to a translated one (null when there is no translation).</summary>
    public static Func<string, string?>? Translate { get; set; }

    /// <summary>Maps a built-in's name to a translated description (null when there is none).</summary>
    public static Func<string, string?>? TranslateDescription { get; set; }

    public static string T(string template, params object?[] args)
    {
        var text = Translate?.Invoke(template) ?? template;
        if (args.Length == 0) return text;
        try
        {
            return string.Format(CultureInfo.InvariantCulture, text, args);
        }
        catch (FormatException)
        {
            // A damaged translation must not hide the message itself.
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
    }

    public static string Description(Engine.Builtin b) => TranslateDescription?.Invoke(b.Name) ?? b.Description;
}
