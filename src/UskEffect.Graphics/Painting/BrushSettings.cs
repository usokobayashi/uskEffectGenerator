using System.Numerics;

namespace UskEffect.Graphics.Painting;

public enum PaintTool
{
    Brush,

    /// <summary>編集レイヤーを消す。元のノイズや画像は残る。</summary>
    Eraser,
    Blur,
}

/// <summary>ブラシの設定。大きさはテクスチャの画素。</summary>
public sealed record BrushSettings
{
    public const double MinSize = 1;
    public const double MaxSize = 1024;

    public PaintTool Tool { get; init; } = PaintTool.Brush;

    /// <summary>直径 [画素]。</summary>
    public double Size { get; init; } = 32;

    /// <summary>0: 中心から縁までぼける, 1: 縁がくっきりする。</summary>
    public double Hardness { get; init; } = 0.8;

    /// <summary>不透明度。ぼかしでは強さ。</summary>
    public double Opacity { get; init; } = 1;

    public Vector3 Color { get; init; } = Vector3.One;

    /// <summary>テクスチャの端をまたいで反対側へつなげて描く (FR-23)。極座標の 0°/360° は常につなぐ。</summary>
    public bool Wrap { get; init; } = true;

    /// <summary>筆圧で大きさを変える (FR-18)。マウスは常に筆圧 1 として扱う。</summary>
    public bool PressureSize { get; init; } = true;

    /// <summary>筆圧で濃さを変える (FR-18)。</summary>
    public bool PressureOpacity { get; init; }

    /// <summary>筆圧に対する大きさの倍率。ごく弱い筆圧でも線が消えないよう下限を置く。</summary>
    public double SizeAt(double pressure) =>
        PressureSize ? 0.1 + 0.9 * Math.Clamp(pressure, 0, 1) : 1;

    public double FlowAt(double pressure) =>
        PressureOpacity ? Flow * Math.Clamp(pressure, 0, 1) : Flow;

    /// <summary>スタンプの間隔 [画素]。</summary>
    public double Spacing => Math.Max(1, Size * 0.1);

    /// <summary>
    /// 1 スタンプの濃さ。直線を引いたとき、スタンプが重なった中心で Opacity に届く値にする。
    /// </summary>
    public double Flow
    {
        get
        {
            double opacity = Math.Clamp(Opacity, 0, 1);
            double overlaps = Math.Max(1, Size / Spacing);
            return 1 - Math.Pow(1 - opacity, 1 / overlaps);
        }
    }
}

/// <summary>
/// スタンプの中心。テクスチャの画素で、テクスチャ内に折り返した値。
/// 極座標で描くときは、外接円を 1 とした中心の半径も持つ。
/// </summary>
public readonly record struct PaintDab(double X, double Y, double PolarRadius = 0, double Pressure = 1);

/// <summary>ストロークの通り道の点。</summary>
public readonly record struct StrokePoint(double X, double Y, double Pressure = 1);

/// <summary>プロジェクトに保存するレイヤー (FR-15)。Image は乗算なし。画素は JSON ではなく ZIP 内の PNG に置く。</summary>
public sealed record LayerData
{
    public string Name { get; init; } = "";
    public bool Visible { get; init; } = true;
    public float Opacity { get; init; } = 1f;

    [System.Text.Json.Serialization.JsonIgnore]
    public ImageData? Image { get; init; }
}

/// <summary>極座標キャンバス上で円になるスタンプの設定 (FR-19 / FR-21)。</summary>
public sealed record PolarStamp(double InnerRadius, double OuterRadius, bool FlipRadial, double TilingX, double TilingY, double ContentSize);

/// <summary>継ぎ目の自動ブレンド (FR-13)。Width は境界から片側の幅で、テクスチャに対する割合。</summary>
public sealed record SeamBlendSettings(SeamMode Axes = SeamMode.XY, double Width = 0.1, double Strength = 1);

/// <summary>画面とテクスチャの対応。ストロークは表示の種類ごとの座標で補間してから、スタンプへ直す。</summary>
public interface ICanvasMapping
{
    /// <summary>テクスチャまたはブラシの 1 画素が画面で何画素か。</summary>
    double PixelScale { get; }

    /// <summary>極座標で描くときの設定。矩形では null。</summary>
    PolarStamp? Polar { get; }

    (double X, double Y) ToStrokeSpace(double screenX, double screenY);

    /// <summary>ストロークを始められる場所か。</summary>
    bool CanStartAt(double strokeX, double strokeY);

    /// <summary>スタンプにする。置けない場所なら null。</summary>
    PaintDab? ToDab(double strokeX, double strokeY, BrushSettings brush);
}

/// <summary>ポインターの通り道に、一定の間隔でスタンプを置く。</summary>
public sealed class StrokeInterpolator(double spacing)
{
    private double _lastX, _lastY, _lastPressure;
    private double _untilNext;

    /// <summary>最初の点。ここにスタンプを 1 つ置く。</summary>
    public StrokePoint Begin(double x, double y, double pressure = 1)
    {
        _lastX = x;
        _lastY = y;
        _lastPressure = pressure;
        _untilNext = spacing;
        return new StrokePoint(x, y, pressure);
    }

    /// <summary>次の点までに置くスタンプを output へ足す。筆圧も間を補間する。</summary>
    public void MoveTo(double x, double y, List<StrokePoint> output, double pressure = 1)
    {
        double dx = x - _lastX, dy = y - _lastY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0)
        {
            _lastPressure = pressure;
            return;
        }

        double along = _untilNext;
        while (along <= length)
        {
            double t = along / length;
            output.Add(new StrokePoint(_lastX + dx * t, _lastY + dy * t, _lastPressure + (pressure - _lastPressure) * t));
            along += spacing;
        }
        _untilNext = along - length;
        _lastX = x;
        _lastY = y;
        _lastPressure = pressure;
    }
}

/// <summary>
/// キャンバスの画面座標とテクスチャの画素の対応。Shaders/Preview.hlsl の矩形表示と同じ式。
/// Tiling をまたいだストロークが途切れないよう、テクスチャの座標は折り返さずに返す。
/// </summary>
public sealed record CanvasMapping(
    int ViewportWidth, int ViewportHeight, int TextureWidth, int TextureHeight, double Margin, CanvasView View,
    double TilingX, double TilingY, double OffsetX, double OffsetY) : ICanvasMapping
{
    public PolarStamp? Polar => null;

    public (double X, double Y) ToStrokeSpace(double screenX, double screenY) => ScreenToTexture(screenX, screenY);

    public bool CanStartAt(double strokeX, double strokeY) => IsInsideCanvas(strokeX, strokeY);

    public PaintDab? ToDab(double strokeX, double strokeY, BrushSettings brush) =>
        IsInsideCanvas(strokeX, strokeY) ? Wrap(strokeX, strokeY) : null;

    /// <summary>全体表示の倍率。</summary>
    public double FitScale => Math.Min(
        Math.Max(ViewportWidth - Margin * 2, 1) / TextureWidth,
        Math.Max(ViewportHeight - Margin * 2, 1) / TextureHeight);

    /// <summary>テクスチャの 1 画素が画面で何画素か。</summary>
    public double PixelScale => FitScale * View.Zoom / Math.Max(TilingX, TilingY);

    public (double X, double Y) ScreenToTexture(double screenX, double screenY)
    {
        var (x, y) = View.ScreenToContent(screenX - ViewportWidth / 2.0, screenY - ViewportHeight / 2.0, FitScale);
        double localX = x / TextureWidth + 0.5, localY = y / TextureHeight + 0.5;
        return ((localX * TilingX + OffsetX) * TextureWidth, (localY * TilingY + OffsetY) * TextureHeight);
    }

    /// <summary>キャンバスの枠の内側か。外ではスタンプを置かない。</summary>
    public bool IsInsideCanvas(double textureX, double textureY)
    {
        double localX = (textureX / TextureWidth - OffsetX) / TilingX;
        double localY = (textureY / TextureHeight - OffsetY) / TilingY;
        return localX is >= 0 and <= 1 && localY is >= 0 and <= 1;
    }

    public PaintDab Wrap(double textureX, double textureY) =>
        new(WrapAxis(textureX, TextureWidth), WrapAxis(textureY, TextureHeight));

    private static double WrapAxis(double value, int size)
    {
        double wrapped = value % size;
        return wrapped < 0 ? wrapped + size : wrapped;
    }
}

/// <summary>
/// 極座標キャンバスの対応 (FR-19)。Shaders/Preview.hlsl の極座標表示と同じ式。
/// ストロークはキャンバスの画素で補間するので、0°/360° をまたいでも途切れず、見た目どおりの間隔になる。
/// </summary>
public sealed record PolarCanvasMapping(
    int ViewportWidth, int ViewportHeight, int TextureWidth, int TextureHeight, double Margin, CanvasView View,
    PolarSettings Settings, double TilingX, double TilingY, double OffsetX, double OffsetY) : ICanvasMapping
{
    private double ContentSize => Math.Max(TextureWidth, TextureHeight);

    private double FitScale => Math.Min(
        Math.Max(ViewportWidth - Margin * 2, 1) / ContentSize,
        Math.Max(ViewportHeight - Margin * 2, 1) / ContentSize);

    public double PixelScale => FitScale * View.Zoom;

    public PolarStamp Polar => new(Settings.InnerRadius, Settings.OuterRadius, Settings.FlipRadial, TilingX, TilingY, ContentSize);

    PolarStamp? ICanvasMapping.Polar => Polar;

    /// <summary>キャンバスの中心を原点、y 上向き、単位は内容の画素。</summary>
    public (double X, double Y) ToStrokeSpace(double screenX, double screenY)
    {
        var (x, y) = View.ScreenToContent(screenX - ViewportWidth / 2.0, screenY - ViewportHeight / 2.0, FitScale);
        return (x, -y);
    }

    private double RadiusAt(double x, double y) => Math.Sqrt(x * x + y * y) / (ContentSize / 2);

    public bool CanStartAt(double strokeX, double strokeY)
    {
        double r = RadiusAt(strokeX, strokeY);
        return r >= Settings.InnerRadius && r <= Settings.OuterRadius;
    }

    public PaintDab? ToDab(double strokeX, double strokeY, BrushSettings brush)
    {
        // 中心がリングの外でも、ブラシがリングにかかっていれば描く
        double r = RadiusAt(strokeX, strokeY);
        double brushRadius = brush.Size / ContentSize;
        double inner = Settings.InnerRadius, outer = Settings.OuterRadius;
        if (r < inner - brushRadius || r > outer + brushRadius)
            return null;

        double u = (Math.Atan2(strokeY, strokeX) - Settings.RotationDegrees * Math.PI / 180) / (2 * Math.PI);
        double v = (r - inner) / Math.Max(outer - inner, 1e-5);
        if (Settings.FlipAngular)
            u = 1 - u;
        if (Settings.FlipRadial)
            v = 1 - v;
        return new PaintDab(Fract(u * TilingX + OffsetX) * TextureWidth, Fract(v * TilingY + OffsetY) * TextureHeight, r);
    }

    private static double Fract(double value) => value - Math.Floor(value);
}
