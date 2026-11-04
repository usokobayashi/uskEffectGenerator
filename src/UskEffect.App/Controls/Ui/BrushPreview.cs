using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Animation.Easings;
using Avalonia.Media;

namespace UskEffect.App.Controls.Ui;

/// <summary>ブラシの色と硬さの見本。大きさは実寸ではなく、枠に収まるよう正規化して描く。</summary>
public sealed class BrushPreview : Control
{
    public static readonly StyledProperty<Color> BrushColorProperty =
        AvaloniaProperty.Register<BrushPreview, Color>(nameof(BrushColor), Colors.White);

    public static readonly StyledProperty<double> HardnessProperty =
        AvaloniaProperty.Register<BrushPreview, double>(nameof(Hardness), 0.8);

    public static readonly StyledProperty<double> SizeRatioProperty =
        AvaloniaProperty.Register<BrushPreview, double>(nameof(SizeRatio), 1);

    public static readonly StyledProperty<double> FlowProperty =
        AvaloniaProperty.Register<BrushPreview, double>(nameof(Flow), 1);

    /// <summary>暗い背景を敷くか。ボタンの中のアイコンは透過する。</summary>
    public static readonly StyledProperty<bool> ShowBackdropProperty =
        AvaloniaProperty.Register<BrushPreview, bool>(nameof(ShowBackdrop));

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(16, 19, 22));

    static BrushPreview() =>
        AffectsRender<BrushPreview>(BrushColorProperty, HardnessProperty, SizeRatioProperty, FlowProperty);

    public BrushPreview()
    {
        if (Application.Current is not { } app || !UskEffect.App.Locale.Motion.Enabled
            || !app.TryGetResource("Ue.Motion.Toggle", null, out var value) || value is not TimeSpan duration)
            return;
        Easing? easing = app.TryGetResource("Ue.Ease", null, out var ease) ? ease as Easing : null;
        Transitions =
        [
            new ColorTransition { Property = BrushColorProperty, Duration = duration, Easing = easing ?? new CubicEaseOut() },
            new DoubleTransition { Property = HardnessProperty, Duration = duration, Easing = easing ?? new CubicEaseOut() },
            new DoubleTransition { Property = SizeRatioProperty, Duration = duration, Easing = easing ?? new CubicEaseOut() },
            new DoubleTransition { Property = FlowProperty, Duration = duration, Easing = easing ?? new CubicEaseOut() },
        ];
    }

    public bool ShowBackdrop
    {
        get => GetValue(ShowBackdropProperty);
        set => SetValue(ShowBackdropProperty, value);
    }

    public Color BrushColor
    {
        get => GetValue(BrushColorProperty);
        set => SetValue(BrushColorProperty, value);
    }

    public double Hardness
    {
        get => GetValue(HardnessProperty);
        set => SetValue(HardnessProperty, value);
    }

    /// <summary>0..1。1 で枠いっぱい。</summary>
    public double SizeRatio
    {
        get => GetValue(SizeRatioProperty);
        set => SetValue(SizeRatioProperty, value);
    }

    public double Flow
    {
        get => GetValue(FlowProperty);
        set => SetValue(FlowProperty, value);
    }

    /// <summary>ブラシの大きさを、枠に収まる比率にする。差が見えるよう対数で扱う。</summary>
    public static double NormalizeSize(double size, double minimum, double maximum)
    {
        const double smallest = 0.12;
        double t = (Math.Log(Math.Max(size, minimum)) - Math.Log(minimum)) / (Math.Log(maximum) - Math.Log(minimum));
        return smallest + (1 - smallest) * Math.Clamp(t, 0, 1);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (ShowBackdrop)
            context.DrawRectangle(Backdrop, null, bounds, 3, 3);

        double radius = Math.Min(bounds.Width, bounds.Height) / 2 * Math.Clamp(SizeRatio, 0, 1);
        if (radius <= 0)
            return;

        var color = BrushColor;
        byte alpha = (byte)Math.Round(255 * Math.Clamp(Flow, 0, 1));
        var solid = Color.FromArgb(alpha, color.R, color.G, color.B);
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(solid, Math.Clamp(Hardness, 0, 0.995)),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        context.DrawEllipse(brush, null, bounds.Center, radius, radius);
    }
}
