using System.Text;
using System.Text.Json;

namespace Equator.Recognition;

/// <summary>
/// Decoder for a byte-level BPE vocabulary (Hugging Face tokenizer.json, GPT-2 style): token strings are
/// concatenated, mapped back from the printable byte alphabet to raw bytes, and decoded as UTF-8.
/// </summary>
public sealed class ByteLevelBpe
{
    private readonly string[] _tokens;
    private readonly HashSet<int> _special;
    private static readonly Dictionary<char, byte> ByteDecoder = BuildByteDecoder();

    public ByteLevelBpe(string tokenizerJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath, Encoding.UTF8));
        var vocab = doc.RootElement.GetProperty("model").GetProperty("vocab");
        var pairs = vocab.EnumerateObject().Select(p => (Token: p.Name, Id: p.Value.GetInt32())).ToList();
        _tokens = new string[pairs.Max(p => p.Id) + 1];
        foreach (var (t, id) in pairs) _tokens[id] = t;
        _special = [];
        if (doc.RootElement.TryGetProperty("added_tokens", out var added))
            foreach (var a in added.EnumerateArray())
                if (a.TryGetProperty("special", out var sp) && sp.GetBoolean()) _special.Add(a.GetProperty("id").GetInt32());
    }

    public int VocabularySize => _tokens.Length;

    public string Decode(IEnumerable<long> ids)
    {
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (id < 0 || id >= _tokens.Length || _special.Contains((int)id) || _tokens[id] is null) continue;
            foreach (var ch in _tokens[id])
            {
                if (ByteDecoder.TryGetValue(ch, out var b)) bytes.Add(b);
                else bytes.AddRange(Encoding.UTF8.GetBytes(ch.ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    /// <summary>Inverse of GPT-2's bytes_to_unicode: printable bytes map to themselves, the rest to U+0100 onwards.</summary>
    private static Dictionary<char, byte> BuildByteDecoder()
    {
        var printable = new List<int>();
        for (int b = '!'; b <= '~'; b++) printable.Add(b);
        for (int b = 0xA1; b <= 0xAC; b++) printable.Add(b);
        for (int b = 0xAE; b <= 0xFF; b++) printable.Add(b);
        var map = new Dictionary<char, byte>();
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (printable.Contains(b)) map[(char)b] = (byte)b;
            else map[(char)(256 + n++)] = (byte)b;
        }
        return map;
    }
}
