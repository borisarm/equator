using System.Security.Cryptography;

namespace Equator.Recognition;

/// <summary>
/// Locates the handwriting model on disk and downloads it on demand. The files come from a pinned revision of
/// breezedeus/pix2text-mfr-1.5 on Hugging Face (MIT licence) and are checked against known SHA-256 hashes,
/// so a download is only ever accepted when it is exactly the expected model. Recognition itself runs locally.
/// </summary>
public static class ModelStore
{
    public const string ModelName = "pix2text-mfr-1.5";
    private const string Revision = "1cef9f0bdcd6a4c63df7de1311fb0894593340cc";
    private const string BaseUrl = "https://huggingface.co/breezedeus/pix2text-mfr-1.5/resolve/" + Revision + "/";

    public static readonly (string Name, string Sha256, long Size)[] Files =
    [
        ("encoder_model.onnx", "080a3f660f08bc9ebcacdd96e34be6b6400f8c7e62d7cd0dd8251badc37f610b", 87510770),
        ("decoder_model.onnx", "917deb98e91a0453c5f234f58a0f32f9fb037de8527c7eb4ed394daf9e692f2a", 32026253),
        ("tokenizer.json", "4ffbeb2143e6a38324bb6111b7a8109530d38a076a8439aa5777535f0a32758a", 113168),
    ];

    public static long DownloadSize => Files.Sum(f => f.Size);

    /// <summary>Where a downloaded model is stored: %LOCALAPPDATA%\Equator\models\pix2text-mfr-1.5.</summary>
    public static string UserDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Equator", "models", ModelName);

    /// <summary>Candidate locations, in order: EQUATOR_MODELS, next to the application, the user folder.</summary>
    public static IEnumerable<string> Candidates()
    {
        var env = Environment.GetEnvironmentVariable("EQUATOR_MODELS");
        if (!string.IsNullOrEmpty(env)) yield return Path.Combine(env, ModelName);
        yield return Path.Combine(AppContext.BaseDirectory, "models", ModelName);
        yield return UserDirectory;
        // Development: a models folder in an enclosing directory (the repository root).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "models", ModelName);
    }

    /// <summary>A directory containing all model files (sizes checked), or null.</summary>
    public static string? Find() =>
        Candidates().FirstOrDefault(dir => Files.All(f => File.Exists(Path.Combine(dir, f.Name)) && new FileInfo(Path.Combine(dir, f.Name)).Length == f.Size));

    /// <summary>Downloads the model into the user folder, verifying every file. Progress is reported in [0, 1].</summary>
    public static async Task<string> DownloadAsync(IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var dir = UserDirectory;
        Directory.CreateDirectory(dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        long done = 0, total = DownloadSize;
        foreach (var (name, sha, size) in Files)
        {
            var target = Path.Combine(dir, name);
            if (File.Exists(target) && new FileInfo(target).Length == size && await HashAsync(target, cancellation) == sha)
            {
                done += size;
                progress?.Report((double)done / total);
                continue;
            }
            var temp = target + ".part";
            using (var response = await http.GetAsync(BaseUrl + name, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(cancellation);
                await using var dst = File.Create(temp);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await src.ReadAsync(buffer, cancellation)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    done += read;
                    progress?.Report(Math.Min(1, (double)done / total));
                }
            }
            if (await HashAsync(temp, cancellation) != sha)
            {
                File.Delete(temp);
                throw new InvalidDataException($"The downloaded file {name} does not match the expected checksum.");
            }
            File.Move(temp, target, overwrite: true);
        }
        return dir;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellation)
    {
        await using var s = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(s, cancellation);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
