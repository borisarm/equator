using System.Text.Json;
using System.Text.Json.Serialization;

namespace Equator.App.Services;

/// <summary>The .eqnb notebook format: JSON holding the cell inputs (outputs are recomputed).</summary>
public static class NotebookFile
{
    private sealed class Model
    {
        [JsonPropertyName("format")] public string Format { get; set; } = "equator-notebook";
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("cells")] public List<CellModel> Cells { get; set; } = [];
    }

    private sealed class CellModel
    {
        [JsonPropertyName("input")] public string Input { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(IEnumerable<string> inputs)
    {
        var model = new Model { Cells = inputs.Where(i => i.Trim().Length > 0).Select(i => new CellModel { Input = i }).ToList() };
        return JsonSerializer.Serialize(model, Options);
    }

    public static List<string> Parse(string json)
    {
        var model = JsonSerializer.Deserialize<Model>(json) ?? throw new JsonException("Empty notebook file.");
        if (model.Format != "equator-notebook") throw new JsonException("Not an Equator notebook.");
        return model.Cells.Select(c => c.Input).ToList();
    }
}
