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

/// <summary>スタンプの中心。テクスチャの画素で、テクスチャ内に折り返した値。</summary>
public readonly record struct PaintDab(double X, double Y);

/// <summary>ポインターの通り道に、一定の間隔でスタンプを置く。</summary>
public sealed class StrokeInterpolator(double spacing)
{
    private double _lastX, _lastY;
    private double _untilNext;

    /// <summary>最初の点。ここにスタンプを 1 つ置く。</summary>
    public (double X, double Y) Begin(double x, double y)
    {
        _lastX = x;
        _lastY = y;
        _untilNext = spacing;
        return (x, y);
    }

    /// <summary>次の点までに置くスタンプを output へ足す。</summary>
    public void MoveTo(double x, double y, List<(double X, double Y)> output)
    {
        double dx = x - _lastX, dy = y - _lastY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0)
            return;

        double along = _untilNext;
        while (along <= length)
        {
            double t = along / length;
            output.Add((_lastX + dx * t, _lastY + dy * t));
            along += spacing;
        }
        _untilNext = along - length;
        _lastX = x;
        _lastY = y;
    }
}

/// <summary>
/// キャンバスの画面座標とテクスチャの画素の対応。Shaders/Preview.hlsl の矩形表示と同じ式。
/// Tiling をまたいだストロークが途切れないよう、テクスチャの座標は折り返さずに返す。
/// </summary>
public sealed record CanvasMapping(
    int ViewportWidth, int ViewportHeight, int TextureWidth, int TextureHeight, double Margin, CanvasView View,
    double TilingX, double TilingY, double OffsetX, double OffsetY)
{
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
