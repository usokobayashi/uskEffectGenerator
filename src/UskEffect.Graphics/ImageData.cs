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
    public float[] ToLuminance()
    {
        var luminance = new float[Width * Height];
        for (int i = 0; i < luminance.Length; i++)
        {
            int p = i * 4;
            luminance[i] = (0.299f * Rgba[p] + 0.587f * Rgba[p + 1] + 0.114f * Rgba[p + 2]) / 255f;
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
