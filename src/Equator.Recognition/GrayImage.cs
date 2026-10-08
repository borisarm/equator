namespace Equator.Recognition;

/// <summary>Rectangle in pixel coordinates (inclusive left/top, exclusive right/bottom).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Grayscale image with values in [0, 1] (0 = black ink, 1 = white paper), plus the image operations the
/// recognizer needs: cleaning photos, finding the written area, splitting it into lines, resizing.
/// </summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public float[] Pixels { get; }

    public GrayImage(int width, int height, float[] pixels)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Pixel count does not match the size.");
        Width = width; Height = height; Pixels = pixels;
    }

    public static GrayImage Blank(int width, int height) => new(width, height, Enumerable.Repeat(1f, width * height).ToArray());

    public float this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    /// <summary>From 8-bit BGRA (Windows imaging) or RGBA pixels; transparent pixels count as paper.</summary>
    public static GrayImage FromBgra(byte[] bgra, int width, int height, bool rgbaOrder = false)
    {
        var px = new float[width * height];
        for (int i = 0; i < px.Length; i++)
        {
            int o = 4 * i;
            float r = bgra[o + (rgbaOrder ? 0 : 2)], g = bgra[o + 1], b = bgra[o + (rgbaOrder ? 2 : 0)], a = bgra[o + 3] / 255f;
            float lum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            px[i] = lum * a + (1 - a); // composite on white
        }
        return new GrayImage(width, height, px);
    }

    /// <summary>
    /// Turns a photo or scan into clean black ink on white: divides by a local estimate of the paper brightness
    /// (removes shadows and uneven lighting), then thresholds with Otsu's method.
    /// </summary>
    public GrayImage Clean()
    {
        var background = PaperBrightness();
        var flat = new float[Pixels.Length];
        for (int i = 0; i < flat.Length; i++)
            flat[i] = Math.Clamp(Pixels[i] / Math.Max(background[i], 1e-3f), 0, 1);
        // Ink must be clearly darker than the paper around it, whatever Otsu finds on nearly blank pages.
        float t = Math.Min(Otsu(flat), 0.78f);
        float contrast = flat.Max() - flat.Min();
        var result = new float[flat.Length];
        for (int i = 0; i < flat.Length; i++) result[i] = contrast < 0.15f || flat[i] > t ? 1f : 0f;
        return new GrayImage(Width, Height, result).RemoveSpecks();
    }

    /// <summary>
    /// Paper brightness at every pixel: the 90th percentile of each block (ink is sparse, so that is paper),
    /// interpolated bilinearly between block centres.
    /// </summary>
    private float[] PaperBrightness()
    {
        int block = Math.Max(16, Math.Max(Width, Height) / 24);
        int bx = (Width + block - 1) / block, by = (Height + block - 1) / block;
        var grid = new float[bx * by];
        var values = new List<float>(block * block);
        for (int j = 0; j < by; j++)
            for (int i = 0; i < bx; i++)
            {
                values.Clear();
                for (int y = j * block; y < Math.Min(Height, (j + 1) * block); y++)
                    for (int x = i * block; x < Math.Min(Width, (i + 1) * block); x++)
                        values.Add(this[x, y]);
                values.Sort();
                grid[j * bx + i] = values[(int)(values.Count * 0.9)];
            }
        var result = new float[Pixels.Length];
        for (int y = 0; y < Height; y++)
        {
            double gy = Math.Clamp((y + 0.5) / block - 0.5, 0, by - 1);
            int j0 = (int)gy, j1 = Math.Min(j0 + 1, by - 1);
            double fy = gy - j0;
            for (int x = 0; x < Width; x++)
            {
                double gx = Math.Clamp((x + 0.5) / block - 0.5, 0, bx - 1);
                int i0 = (int)gx, i1 = Math.Min(i0 + 1, bx - 1);
                double fx = gx - i0;
                result[y * Width + x] = (float)((1 - fy) * ((1 - fx) * grid[j0 * bx + i0] + fx * grid[j0 * bx + i1])
                                              + fy * ((1 - fx) * grid[j1 * bx + i0] + fx * grid[j1 * bx + i1]));
            }
        }
        return result;
    }

    /// <summary>Otsu's threshold for values in [0, 1].</summary>
    private static float Otsu(float[] values)
    {
        var hist = new int[256];
        foreach (var v in values) hist[(int)(Math.Clamp(v, 0, 1) * 255)]++;
        long total = values.Length;
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
        double sumB = 0, best = -1;
        long wB = 0;
        int threshold = 128;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += i * (double)hist[i];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = i; }
        }
        // Ink is the dark minority: never put the threshold above light gray.
        return Math.Min(threshold / 255f, 0.85f);
    }

    /// <summary>Removes isolated dark pixels (sensor noise, paper texture) that have almost no dark neighbours.</summary>
    private GrayImage RemoveSpecks()
    {
        var res = (float[])Pixels.Clone();
        for (int y = 1; y < Height - 1; y++)
            for (int x = 1; x < Width - 1; x++)
            {
                if (this[x, y] > 0.5f) continue;
                int dark = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dy != 0) && this[x + dx, y + dy] < 0.5f) dark++;
                if (dark == 0) res[y * Width + x] = 1f;
            }
        return new GrayImage(Width, Height, res);
    }

    public bool IsInk(int x, int y) => this[x, y] < 0.5f;

    /// <summary>Bounding box of the ink, or an empty rectangle.</summary>
    public PixelRect InkBounds(PixelRect? within = null)
    {
        var r = within ?? new PixelRect(0, 0, Width, Height);
        int l = int.MaxValue, t = int.MaxValue, rr = -1, b = -1;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                if (IsInk(x, y))
                {
                    if (x < l) l = x;
                    if (x > rr) rr = x;
                    if (y < t) t = y;
                    if (y > b) b = y;
                }
        return rr < 0 ? new PixelRect(0, 0, 0, 0) : new PixelRect(l, t, rr + 1, b + 1);
    }

    /// <summary>
    /// Splits the written area into lines using the horizontal ink profile. Gaps narrower than half the
    /// typical line height do not split (exponents, fraction bars), and thin bands (a lone fraction bar,
    /// a stray mark) are merged into a neighbour.
    /// </summary>
    public List<PixelRect> SplitLines()
    {
        var all = InkBounds();
        if (all.IsEmpty) return [];
        var rowInk = new int[Height];
        for (int y = all.Top; y < all.Bottom; y++)
            for (int x = all.Left; x < all.Right; x++)
                if (IsInk(x, y)) rowInk[y]++;

        // Raw bands of consecutive rows containing ink.
        var bands = new List<(int Top, int Bottom)>();
        int start = -1;
        for (int y = all.Top; y <= all.Bottom; y++)
        {
            bool ink = y < all.Bottom && rowInk[y] > 0;
            if (ink && start < 0) start = y;
            if (!ink && start >= 0) { bands.Add((start, y)); start = -1; }
        }
        if (bands.Count == 0) return [];

        var heights = bands.Select(b => b.Bottom - b.Top).OrderBy(h => h).ToList();
        double typical = Math.Max(heights[heights.Count * 3 / 4], 4);

        // Merge across small gaps.
        var merged = new List<(int Top, int Bottom)> { bands[0] };
        for (int i = 1; i < bands.Count; i++)
        {
            var last = merged[^1];
            int gap = bands[i].Top - last.Bottom;
            if (gap < 0.45 * typical) merged[^1] = (last.Top, bands[i].Bottom);
            else merged.Add(bands[i]);
        }
        // Absorb thin bands into the nearer neighbour.
        bool changed = true;
        while (changed && merged.Count > 1)
        {
            changed = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var b = merged[i];
                if (b.Bottom - b.Top >= 0.3 * typical) continue;
                int target;
                if (i == 0) target = 1;
                else if (i == merged.Count - 1) target = i - 1;
                else target = b.Top - merged[i - 1].Bottom <= merged[i + 1].Top - b.Bottom ? i - 1 : i + 1;
                var t = merged[target];
                merged[target] = (Math.Min(t.Top, b.Top), Math.Max(t.Bottom, b.Bottom));
                merged.RemoveAt(i);
                changed = true;
                break;
            }
        }
        return merged
            .Select(b => InkBounds(new PixelRect(all.Left, b.Top, all.Right, b.Bottom)))
            .Where(r => !r.IsEmpty)
            .ToList();
    }

    public GrayImage Crop(PixelRect r)
    {
        var px = new float[r.Width * r.Height];
        for (int y = 0; y < r.Height; y++)
            Array.Copy(Pixels, (r.Top + y) * Width + r.Left, px, y * r.Width, r.Width);
        return new GrayImage(r.Width, r.Height, px);
    }

    /// <summary>Adds a white margin around the image.</summary>
    public GrayImage Pad(int left, int top, int right, int bottom)
    {
        var res = Blank(Width + left + right, Height + top + bottom);
        for (int y = 0; y < Height; y++)
            Array.Copy(Pixels, y * Width, res.Pixels, (y + top) * res.Width + left, Width);
        return res;
    }

    /// <summary>Resize by area averaging when shrinking and bilinear interpolation when enlarging.</summary>
    public GrayImage Resize(int width, int height)
    {
        var res = new float[width * height];
        double sx = (double)Width / width, sy = (double)Height / height;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (sx > 1 || sy > 1)
                {
                    int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, (int)Math.Ceiling((x + 1) * sx));
                    int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, (int)Math.Ceiling((y + 1) * sy));
                    double acc = 0; int n = 0;
                    for (int yy = y0; yy < Math.Min(y1, Height); yy++)
                        for (int xx = x0; xx < Math.Min(x1, Width); xx++) { acc += this[xx, yy]; n++; }
                    res[y * width + x] = (float)(acc / Math.Max(n, 1));
                }
                else
                {
                    double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, Width - 1), fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, Height - 1);
                    int ix = (int)fx, iy = (int)fy;
                    int jx = Math.Min(ix + 1, Width - 1), jy = Math.Min(iy + 1, Height - 1);
                    double ax = fx - ix, ay = fy - iy;
                    res[y * width + x] = (float)((1 - ay) * ((1 - ax) * this[ix, iy] + ax * this[jx, iy]) + ay * ((1 - ax) * this[ix, jy] + ax * this[jx, jy]));
                }
            }
        return new GrayImage(width, height, res);
    }

    /// <summary>Thickens strokes by a radius (thin pen strokes shrink to nothing at the model's input size).</summary>
    public GrayImage Dilate(int radius)
    {
        if (radius <= 0) return this;
        var res = (float[])Pixels.Clone();
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (!IsInk(x, y)) continue;
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < Width && yy < Height && dx * dx + dy * dy <= radius * radius)
                            res[yy * Width + xx] = Math.Min(res[yy * Width + xx], this[x, y]);
                    }
            }
        return new GrayImage(Width, Height, res);
    }

    /// <summary>Estimated stroke width: ink pixels divided by the length of the ink boundary, times two.</summary>
    public double StrokeWidth()
    {
        int ink = 0, edge = 0;
        for (int y = 1; y < Height - 1; y++)
            for (int x = 1; x < Width - 1; x++)
            {
                if (!IsInk(x, y)) continue;
                ink++;
                if (!IsInk(x + 1, y) || !IsInk(x - 1, y) || !IsInk(x, y + 1) || !IsInk(x, y - 1)) edge++;
            }
        return edge == 0 ? 0 : 2.0 * ink / edge;
    }
}
