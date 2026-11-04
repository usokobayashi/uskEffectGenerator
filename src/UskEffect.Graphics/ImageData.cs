namespace UskEffect.Graphics;

/// <summary>読み込んだ画像。行優先・上から下の RGBA 8bit (乗算なし)。</summary>
public sealed class ImageData
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public ImageData(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "画像の大きさが不正です。");
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("画素数が大きさと合いません。", nameof(rgba));
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    /// <summary>輝度 (0..1)。継ぎ目の解析とグレースケールの書き出しに使う。</summary>
    /// <summary>
    /// 縦横比を保ったまま、指定の大きさの中央に収める。余白は透明。同じ大きさならそのまま返す。
    /// 色が縁へにじまないよう、アルファを掛けた状態で補間する。
    /// </summary>
    public ImageData FitInto(int width, int height)
    {
        if (width == Width && height == Height)
            return this;

        double scale = Math.Min((double)width / Width, (double)height / Height);
        double fittedWidth = Width * scale, fittedHeight = Height * scale;
        double left = (width - fittedWidth) / 2, top = (height - fittedHeight) / 2;
        var result = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            double sy = (y + 0.5 - top) / scale - 0.5;
            if (y + 0.5 < top || y + 0.5 > top + fittedHeight)
                continue;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, Height - 1), y1 = Math.Min(y0 + 1, Height - 1);
            double ty = Math.Clamp(sy - y0, 0, 1);
            for (int x = 0; x < width; x++)
            {
                if (x + 0.5 < left || x + 0.5 > left + fittedWidth)
                    continue;
                double sx = (x + 0.5 - left) / scale - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, Width - 1), x1 = Math.Min(x0 + 1, Width - 1);
                double tx = Math.Clamp(sx - x0, 0, 1);

                double r = 0, g = 0, b = 0, alpha = 0;
                void Add(int px, int py, double weight)
                {
                    int i = (py * Width + px) * 4;
                    double a = Rgba[i + 3] * weight;
                    r += Rgba[i] * a;
                    g += Rgba[i + 1] * a;
                    b += Rgba[i + 2] * a;
                    alpha += a;
                }
                Add(x0, y0, (1 - tx) * (1 - ty));
                Add(x1, y0, tx * (1 - ty));
                Add(x0, y1, (1 - tx) * ty);
                Add(x1, y1, tx * ty);
                if (alpha <= 0)
                    continue;

                int o = (y * width + x) * 4;
                result[o] = (byte)Math.Round(r / alpha);
                result[o + 1] = (byte)Math.Round(g / alpha);
                result[o + 2] = (byte)Math.Round(b / alpha);
                result[o + 3] = (byte)Math.Round(alpha);
            }
        }
        return new ImageData(width, height, result);
    }

    public float[] ToLuminance()
    {
        var luminance = new float[Width * Height];
        for (int i = 0; i < luminance.Length; i++)
        {
            int p = i * 4;
            // 透明なところは黒として扱う (マスクや透過で抜けた部分)
            luminance[i] = Rgba[p + 3] / 255f * (0.299f * Rgba[p] + 0.587f * Rgba[p + 1] + 0.114f * Rgba[p + 2]) / 255f;
        }
        return luminance;
    }
}

/// <summary>
/// キャンバスの表示状態 (拡大率・移動・回転)。画面の状態なので保存も元に戻すもしない。
/// 画面座標はビューポート中央を原点、y 下向きの画素。画面上の点 S と内容の点 C の関係は
/// S = Pan + R(回転) × C × (全体表示の倍率 × Zoom)。Shaders/Preview.hlsl と同じ式。
/// </summary>
public sealed record CanvasView
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 64;

    /// <summary>全体表示を 1 とした拡大率。</summary>
    public double Zoom { get; init; } = 1;
    public double PanX { get; init; }
    public double PanY { get; init; }

    /// <summary>回転 [度]。画面上で時計回り。</summary>
    public double RotationDegrees { get; init; }

    /// <summary>画面上の点を動かさずに拡大縮小する。</summary>
    public CanvasView ZoomAt(double screenX, double screenY, double factor)
    {
        double zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        double applied = zoom / Zoom;
        return this with
        {
            Zoom = zoom,
            PanX = screenX - applied * (screenX - PanX),
            PanY = screenY - applied * (screenY - PanY),
        };
    }

    public CanvasView Panned(double dx, double dy) => this with { PanX = PanX + dx, PanY = PanY + dy };

    /// <summary>ビューポートの中央を軸に回す。</summary>
    public CanvasView Rotated(double degrees)
    {
        double radians = degrees * Math.PI / 180;
        double c = Math.Cos(radians), s = Math.Sin(radians);
        double rotation = (RotationDegrees + degrees) % 360;
        return this with
        {
            RotationDegrees = rotation < 0 ? rotation + 360 : rotation,
            PanX = c * PanX - s * PanY,
            PanY = s * PanX + c * PanY,
        };
    }

    /// <summary>画面の点を内容の座標 (全体表示の倍率で割る前の、内容の中央原点) へ戻す。</summary>
    public (double X, double Y) ScreenToContent(double screenX, double screenY, double fitScale)
    {
        double radians = -RotationDegrees * Math.PI / 180;
        double c = Math.Cos(radians), s = Math.Sin(radians);
        double x = screenX - PanX, y = screenY - PanY;
        double scale = fitScale * Zoom;
        return ((c * x - s * y) / scale, (s * x + c * y) / scale);
    }
}
