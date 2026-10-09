using System.Text.Json;
using System.Text.RegularExpressions;
using Equator.Core.Engine;

namespace Equator.Core.Tests;

// Installing a translator changes every engine message, so these tests must not run alongside the others.
[CollectionDefinition("Messages", DisableParallelization = true)]
public class MessagesCollection;

[Collection("Messages")]
public class MessagesTests
{
    [Fact]
    public void TranslatorReplacesTemplateAndKeepsArguments()
    {
        var previous = Messages.Translate;
        try
        {
            Messages.Translate = t => t == "Index {0} is out of range 1..{1}." ? "Índice {0} fuera de 1..{1}." : null;
            var error = new Kernel().Evaluate("[1, 2, 3][7]").Outputs.Single(o => o.Kind == OutputKind.Error);
            Assert.Contains("Índice 7 fuera de 1..3.", error.Text);
        }
        finally
        {
            Messages.Translate = previous;
        }
    }

    [Fact]
    public void DamagedTranslationFallsBackToEnglish()
    {
        var previous = Messages.Translate;
        try
        {
            Messages.Translate = _ => "broken {5}";
            Assert.Equal("Cannot differentiate x.", Messages.T("Cannot differentiate {0}.", "x"));
        }
        finally
        {
            Messages.Translate = previous;
        }
    }
}

/// <summary>
/// Every engine message and interface string must have a translation in every language, with the same
/// placeholders: adding a message without translating it fails here rather than showing English to a reader.
/// </summary>
public class TranslationTableTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Strings = Path.Combine(Root, "src", "Equator.App", "Strings");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Equator.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, string> Load(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

    private static string Placeholders(string s) =>
        string.Join(",", Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Distinct().Order());

    /// <summary>The English templates passed to Messages.T in the engine, plus the argument labels it translates.</summary>
    private static HashSet<string> EngineTemplates()
    {
        var found = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src", "Equator.Core"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"Messages\.T\(""((?:[^""\\]|\\.)*)"""))
                found.Add(Regex.Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(text, @"(?:Int|Dbl)\([^()"";]*, ""([A-Za-z][^""]+)""\)"))
                if (m.Groups[1].Value is not ("x" or "y")) found.Add(m.Groups[1].Value);
        }
        return found;
    }

    public static TheoryData<string> Languages() => ["zh-Hans", "hi", "es", "fr", "ar", "bn", "pt-BR"];

    [Theory]
    [MemberData(nameof(Languages))]
    public void EngineMessagesAreTranslated(string language)
    {
        var table = Load(Path.Combine(Strings, "Engine", language + ".json"));
        var templates = EngineTemplates();
        Assert.True(templates.Count > 200, $"only {templates.Count} templates found");
        Assert.DoesNotContain(templates, t => !table.ContainsKey(t));
        Assert.DoesNotContain(table.Keys, k => !templates.Contains(k));
        Assert.Empty(table.Where(kv => Placeholders(kv.Key) != Placeholders(kv.Value)).Select(kv => kv.Key));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void InterfaceStringsAreTranslated(string language)
    {
        var english = Load(Path.Combine(Strings, "en.json"));
        var table = Load(Path.Combine(Strings, language + ".json"));
        Assert.DoesNotContain(english.Keys, k => !table.ContainsKey(k));
        Assert.DoesNotContain(table.Keys, k => !english.ContainsKey(k));
        Assert.Empty(english.Where(kv => Placeholders(kv.Value) != Placeholders(table[kv.Key])).Select(kv => kv.Key));
    }

    [Fact]
    public void EveryBuiltinHasADescriptionKey()
    {
        var english = Load(Path.Combine(Strings, "en.json"));
        Assert.Empty(Builtins.All.Where(b => !english.ContainsKey("fn." + b.Name)).Select(b => b.Name));
        Assert.DoesNotContain(Builtins.All.Select(b => b.Category).Distinct(), c => !english.ContainsKey("cat." + c));
    }
}
