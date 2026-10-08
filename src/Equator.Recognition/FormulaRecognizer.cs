using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Equator.Recognition;

/// <summary>
/// Image-to-LaTeX for a single formula with a TrOCR-style encoder/decoder (Pix2Text MFR) on ONNX Runtime.
/// The encoder runs once per image; the decoder is applied greedily, one token at a time.
/// Thread-safe for concurrent Recognize calls.
/// </summary>
public sealed class FormulaRecognizer : IDisposable
{
    public const int InputSize = 384;
    private const int Bos = 1, Eos = 2, MaxTokens = 400;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly ByteLevelBpe _tokenizer;

    public FormulaRecognizer(string modelDirectory)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        _encoder = new InferenceSession(Path.Combine(modelDirectory, "encoder_model.onnx"), options);
        _decoder = new InferenceSession(Path.Combine(modelDirectory, "decoder_model.onnx"), options);
        _tokenizer = new ByteLevelBpe(Path.Combine(modelDirectory, "tokenizer.json"));
    }

    /// <summary>LaTeX for an image containing one formula (ink dark on light background).</summary>
    public string Recognize(GrayImage formula, CancellationToken cancellation = default)
    {
        var input = Preprocess(formula);
        using var encoded = _encoder.Run([NamedOnnxValue.CreateFromTensor("pixel_values", input)]);
        var hidden = encoded.First().AsTensor<float>().ToDenseTensor();

        var ids = new List<long> { Bos };
        for (int step = 0; step < MaxTokens; step++)
        {
            cancellation.ThrowIfCancellationRequested();
            var idTensor = new DenseTensor<long>(ids.ToArray(), [1, ids.Count]);
            using var output = _decoder.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", idTensor),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
            ]);
            var logits = output.First().AsTensor<float>();
            int vocab = logits.Dimensions[2];
            int last = ids.Count - 1;
            int best = 0;
            float bestScore = float.NegativeInfinity;
            for (int v = 0; v < vocab; v++)
            {
                float s = logits[0, last, v];
                if (s > bestScore) { bestScore = s; best = v; }
            }
            if (best == Eos) break;
            ids.Add(best);
            if (IsDegenerateRepeat(ids)) break;
        }
        return _tokenizer.Decode(ids.Skip(1));
    }

    /// <summary>Stops runaway decoding that keeps repeating the same short pattern.</summary>
    private static bool IsDegenerateRepeat(List<long> ids)
    {
        for (int period = 1; period <= 8; period++)
        {
            int reps = 12 / period + 2;
            if (ids.Count < period * reps + 1) continue;
            bool repeating = true;
            for (int i = 0; i < period * (reps - 1) && repeating; i++)
                repeating = ids[ids.Count - 1 - i] == ids[ids.Count - 1 - i - period];
            if (repeating) return true;
        }
        return false;
    }

    /// <summary>
    /// The model's input: the formula with a white margin, resized (without keeping the aspect ratio, as in
    /// training) to 384×384, as RGB normalised to [−1, 1].
    /// </summary>
    public static DenseTensor<float> Preprocess(GrayImage formula)
    {
        var bounds = formula.InkBounds();
        var crop = bounds.IsEmpty ? formula : formula.Crop(bounds);
        int margin = Math.Max(4, Math.Max(crop.Width, crop.Height) / 16);
        var padded = crop.Pad(margin, margin, margin, margin);
        // Keep strokes visible after downscaling: about 3 px wide at the model's resolution.
        double scale = (double)InputSize / Math.Max(padded.Width, padded.Height);
        double stroke = padded.StrokeWidth() * scale;
        if (stroke > 0 && stroke < 2.5) padded = padded.Dilate((int)Math.Ceiling((2.5 - stroke) / scale / 2));
        var img = padded.Resize(InputSize, InputSize);
        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        for (int y = 0; y < InputSize; y++)
            for (int x = 0; x < InputSize; x++)
            {
                float v = img[x, y] * 2 - 1;
                tensor[0, 0, y, x] = v;
                tensor[0, 1, y, x] = v;
                tensor[0, 2, y, x] = v;
            }
        return tensor;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
