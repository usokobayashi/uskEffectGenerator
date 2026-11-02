using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace UskEffect.App.Controls.Ui;

/// <summary>色相 (0..360)・彩度 (0..1)・明度 (0..1)。</summary>
public readonly record struct Hsv(double H, double S, double V)
{
    public Color ToColor()
    {
        double c = V * S;
        double x = c * (1 - Math.Abs(H / 60 % 2 - 1));
        double m = V - c;
        (double r, double g, double b) = (H % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    public static Hsv FromColor(Color color, double fallbackHue = 0)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double delta = max - Math.Min(r, Math.Min(g, b));
        double h = fallbackHue; // 無彩色には色相がないので、今の色相を保つ
        if (delta > 0)
        {
            h = max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
            if (h < 0)
                h += 360;
        }
        return new Hsv(h, max > 0 ? delta / max : 0, max);
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}

/// <summary>
/// 三角形の中の位置と彩度・明度の対応。頂点は 純色・白・黒 で、純色の頂点がリング上の色相を指す。
/// 座標は中心原点・y 下向き。
/// </summary>
public static class HsvTriangle
{
    private static (Point Pure, Point White, Point Black) Vertices(double hue, double radius)
    {
        Point At(double degrees) => new(Math.Cos(degrees * Math.PI / 180) * radius, Math.Sin(degrees * Math.PI / 180) * radius);
        return (At(hue), At(hue + 120), At(hue + 240));
    }

    /// <summary>点の重み (純色・白・黒)。三角形の内側なら全て 0 以上で合計 1。</summary>
    public static (double Pure, double White, double Black) Weights(Point p, double hue, double radius)
    {
        var (a, b, c) = Vertices(hue, radius);
        double det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        double pure = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / det;
        double white = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / det;
        return (pure, white, 1 - pure - white);
    }

    public static Point ToPoint(Hsv hsv, double radius)
    {
        var (a, b, c) = Vertices(hsv.H, radius);
        double pure = hsv.S * hsv.V, white = (1 - hsv.S) * hsv.V, black = 1 - hsv.V;
        return new Point(pure * a.X + white * b.X + black * c.X, pure * a.Y + white * b.Y + black * c.Y);
    }

    /// <summary>点を彩度・明度にする。三角形の外なら内側へ寄せる。</summary>
    public static (double S, double V) Pick(Point p, double hue, double radius)
    {
        var (pure, white, black) = Weights(p, hue, radius);
        pure = Math.Max(pure, 0);
        white = Math.Max(white, 0);
        black = Math.Max(black, 0);
        double total = pure + white + black;
        pure /= total;
        white /= total;
        double v = pure + white;
        return (v > 0 ? pure / v : 0, v);
    }
}

/// <summary>三角のカラーピッカー。外側のリングで色相、内側の三角形で彩度と明度を選ぶ。</summary>
public sealed class TriangleColorPicker : Control
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<TriangleColorPicker, Color>(nameof(Color), Colors.White);

    // 外径に対するリングの内径の割合と、リングと三角形の間の余白 [DIP]
    private const double RingInnerRatio = 0.84;
    private const double TriangleGap = 3;
    private const double HandleRadius = 5;

    private static readonly Pen HandleOuter = new(Brushes.Black, 2.5);
    private static readonly Pen HandleInner = new(Brushes.White, 1.2);

    private enum Drag { None, Ring, Triangle }

    private Hsv _hsv = new(0, 0, 1);
    private WriteableBitmap? _bitmap;
    private double _bitmapHue = double.NaN;
    private Drag _drag;
    private bool _settingFromPointer;

    /// <summary>テスト用。Headless では表示倍率を変えられないので、倍率の扱いをここで確かめる。</summary>
    internal double? ScalingOverride { get; set; }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>ユーザーの操作で色が変わったときに発生する。</summary>
    public event Action? ColorPicked;

    private double OuterRadius => Math.Min(Bounds.Width, Bounds.Height) / 2;
    private double RingInnerRadius => OuterRadius * RingInnerRatio;
    private double TriangleRadius => RingInnerRadius - TriangleGap;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorProperty && !_settingFromPointer)
        {
            _hsv = Hsv.FromColor(Color, _hsv.H);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double outer = OuterRadius;
        if (outer <= 0)
            return;

        double scaling = ScalingOverride ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        EnsureBitmap(scaling);
        // ビットマップは 96 DPI で作るので、元の範囲は画素の大きさそのもの
        context.DrawImage(_bitmap!, new Rect(_bitmap!.Size), new Rect(0, 0, outer * 2, outer * 2));

        var center = new Point(outer, outer);
        double ringMiddle = (outer + RingInnerRadius) / 2;
        double radians = _hsv.H * Math.PI / 180;
        DrawHandle(context, center + new Point(Math.Cos(radians), Math.Sin(radians)) * ringMiddle);
        DrawHandle(context, center + HsvTriangle.ToPoint(_hsv, TriangleRadius));
    }

    private static void DrawHandle(DrawingContext context, Point at)
    {
        context.DrawEllipse(null, HandleOuter, at, HandleRadius, HandleRadius);
        context.DrawEllipse(null, HandleInner, at, HandleRadius, HandleRadius);
    }

    // 色相が変わったときだけ描き直す (三角形の色が色相で決まるため)
    private void EnsureBitmap(double scaling)
    {
        int size = (int)Math.Ceiling(OuterRadius * 2 * scaling);
        if (_bitmap is not null && _bitmap.PixelSize.Width == size && _bitmapHue == _hsv.H)
            return;

        if (_bitmap is null || _bitmap.PixelSize.Width != size)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        // 三角形の余白も画素の大きさに合わせる
        var pixels = RenderPixels(size, _hsv.H, RingInnerRatio, TriangleGap * size / (OuterRadius * 2));
        using var frame = _bitmap.Lock();
        for (int y = 0; y < size; y++)
            Marshal.Copy(pixels, y * size * 4, frame.Address + y * frame.RowBytes, size * 4);
        _bitmapHue = _hsv.H;
    }

    private static byte[] RenderPixels(int size, double hue, double ringInnerRatio, double gap)
    {
        var pixels = new byte[size * size * 4];
        double outer = size / 2d;
        double inner = outer * ringInnerRatio;
        double triangle = inner - gap;
        // 三角形の高さ。重みを画素の距離に直して縁をなめらかにする
        double height = triangle * 1.5;
        var pureColor = new Hsv(hue, 1, 1).ToColor();

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double px = x + 0.5 - outer, py = y + 0.5 - outer;
                double distance = Math.Sqrt(px * px + py * py);
                int i = (y * size + x) * 4;

                double ringAlpha = Math.Clamp(Math.Min(outer - distance, distance - inner) + 0.5, 0, 1);
                if (ringAlpha > 0)
                {
                    double angle = Math.Atan2(py, px) * 180 / Math.PI;
                    Write(pixels, i, new Hsv(angle < 0 ? angle + 360 : angle, 1, 1).ToColor(), ringAlpha);
                    continue;
                }

                if (distance >= inner)
                    continue;
                var (a, b, c) = HsvTriangle.Weights(new Point(px, py), hue, triangle);
                double alpha = Math.Clamp(Math.Min(a, Math.Min(b, c)) * height + 0.5, 0, 1);
                if (alpha <= 0)
                    continue;

                double pure = Math.Max(a, 0), white = Math.Max(b, 0);
                Write(pixels, i, Color.FromRgb(
                    (byte)Math.Min(pure * pureColor.R + white * 255, 255),
                    (byte)Math.Min(pure * pureColor.G + white * 255, 255),
                    (byte)Math.Min(pure * pureColor.B + white * 255, 255)), alpha);
            }
        }
        return pixels;
    }

    // BGRA の乗算済みアルファで書く
    private static void Write(byte[] pixels, int i, Color color, double alpha)
    {
        pixels[i] = (byte)(color.B * alpha);
        pixels[i + 1] = (byte)(color.G * alpha);
        pixels[i + 2] = (byte)(color.R * alpha);
        pixels[i + 3] = (byte)(255 * alpha);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = FromCenter(e.GetPosition(this));
        double distance = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        _drag = distance >= RingInnerRadius && distance <= OuterRadius ? Drag.Ring : distance < RingInnerRadius ? Drag.Triangle : Drag.None;
        if (_drag == Drag.None)
            return;
        e.Pointer.Capture(this);
        PickAt(p);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag != Drag.None)
            PickAt(FromCenter(e.GetPosition(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    private Point FromCenter(Point p) => new(p.X - OuterRadius, p.Y - OuterRadius);

    private void PickAt(Point p)
    {
        if (_drag == Drag.Ring)
        {
            double angle = Math.Atan2(p.Y, p.X) * 180 / Math.PI;
            _hsv = _hsv with { H = angle < 0 ? angle + 360 : angle };
        }
        else
        {
            var (s, v) = HsvTriangle.Pick(p, _hsv.H, TriangleRadius);
            _hsv = _hsv with { S = s, V = v };
        }

        _settingFromPointer = true;
        Color = _hsv.ToColor();
        _settingFromPointer = false;
        InvalidateVisual();
        ColorPicked?.Invoke();
    }
}
