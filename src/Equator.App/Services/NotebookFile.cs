using System.Text.Json;
using System.Text.Json.Serialization;

namespace Equator.App.Services;

/// <summary>The .eqnb notebook format: JSON holding the cell inputs (outputs are recomputed).</summary>
public static class NotebookFile
{
    private const int MaxNotebookChars = 2 * 1024 * 1024;
    private const int MaxCells = 10_000;
    private const int MaxCellChars = 100_000;

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
        if (string.IsNullOrWhiteSpace(json)) throw new JsonException("Empty notebook file.");
        if (json.Length > MaxNotebookChars) throw new JsonException("Notebook exceeds the 2 MB size limit.");

        var model = JsonSerializer.Deserialize<Model>(json, new JsonSerializerOptions { MaxDepth = 32 })
            ?? throw new JsonException("Empty notebook file.");

        if (model.Format != "equator-notebook") throw new JsonException("Not an Equator notebook.");
        if (model.Cells.Count > MaxCells) throw new JsonException("Notebook has too many cells.");

        var cells = new List<string>(model.Cells.Count);
        foreach (var cell in model.Cells)
        {
            var input = cell.Input ?? string.Empty;
            if (input.Length > MaxCellChars) throw new JsonException("Notebook cell exceeds the 100 KB size limit.");
            cells.Add(input);
        }

        return cells;
    }
}
