using System.ComponentModel;
using Avalonia.Media.Imaging;
using UskEffect.Graphics;

namespace UskEffect.App.Views;

/// <summary>種類ごとに使う設定欄。</summary>
[Flags]
public enum ShapeParam
{
    None = 0,
    Center = 1 << 0,
    Angle = 1 << 1,
    Size = 1 << 2,
    Thickness = 1 << 3,
    Softness = 1 << 4,
    Count = 1 << 5,
    Falloff = 1 << 6,
    Amount = 1 << 7,
    Aspect = 1 << 8,
    Twist = 1 << 9,
    Stops = 1 << 10,
    Points = 1 << 11,
    Seed = 1 << 12,
    Color = 1 << 13,
}

/// <summary>種類の選択欄の 1 行。Type が null なら選べない見出し。</summary>
public sealed class NoiseCatalogEntry : INotifyPropertyChanged
{
    private Bitmap? _sample;

    public NoiseCatalogEntry(string name, NoiseType? type = null, ShapeParam parameters = ShapeParam.None,
        string? amountLabel = null, ShapeSettings? sampleShape = null)
    {
        Name = name;
        Type = type;
        Parameters = parameters;
        AmountLabel = amountLabel;
        SampleShape = sampleShape ?? new ShapeSettings();
    }

    public string Name { get; }
    public NoiseType? Type { get; }
    public ShapeParam Parameters { get; }
    public string? AmountLabel { get; }
    public ShapeSettings SampleShape { get; }
    public bool IsHeader => Type is null;

    public Bitmap? Sample
    {
        get => _sample;
        set
        {
            _sample = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sample)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>選べる種類の一覧と、選択欄のサンプルの設定。</summary>
public static class NoiseCatalog
{
    private const int SampleResolution = 64;

    private const ShapeParam Placed = ShapeParam.Center | ShapeParam.Angle;

    public static IReadOnlyList<NoiseCatalogEntry> Create() =>
    [
        new("S.Generator.Noise"),
        new("Perlin", NoiseType.Perlin),
        new("Worley", NoiseType.Worley),
        new("Simplex", NoiseType.Simplex),
        new("Stripe", NoiseType.Stripe),

        new("S.Generator.Gradient"),
        new("単色", NoiseType.Solid, ShapeParam.Color),
        new("Linear", NoiseType.GradientLinear, Placed | ShapeParam.Size | ShapeParam.Falloff, sampleShape: new() { Size = 0.5f }),
        new("Radial", NoiseType.GradientRadial, ShapeParam.Center | ShapeParam.Size | ShapeParam.Falloff, sampleShape: new() { Size = 0.5f }),
        new("Angular", NoiseType.GradientAngular, Placed | ShapeParam.Falloff),
        new("Conical", NoiseType.GradientConical, Placed | ShapeParam.Falloff),
        new("Multi-stop", NoiseType.GradientMultiStop, Placed | ShapeParam.Size | ShapeParam.Stops, sampleShape: new() { Size = 0.5f }),

        new("S.Generator.Shape"),
        new("Circle", NoiseType.ShapeCircle, ShapeParam.Center | ShapeParam.Size | ShapeParam.Softness),
        new("Ring", NoiseType.ShapeRing, ShapeParam.Center | ShapeParam.Size | ShapeParam.Thickness | ShapeParam.Softness,
            sampleShape: new() { Thickness = 0.12f }),
        new("Arc", NoiseType.ShapeArc, Placed | ShapeParam.Size | ShapeParam.Thickness | ShapeParam.Softness | ShapeParam.Amount,
            "S.Shape.ArcLength", new() { Thickness = 0.12f, Amount = 0.65f }),
        new("Rectangle", NoiseType.ShapeRectangle, Placed | ShapeParam.Size | ShapeParam.Aspect | ShapeParam.Softness,
            sampleShape: new() { Size = 0.38f, Aspect = 0.6f }),
        new("Polygon", NoiseType.ShapePolygon, Placed | ShapeParam.Size | ShapeParam.Count | ShapeParam.Softness,
            sampleShape: new() { Count = 6 }),
        new("Star", NoiseType.ShapeStar, Placed | ShapeParam.Size | ShapeParam.Count | ShapeParam.Amount | ShapeParam.Softness,
            "S.Shape.StarInner", new() { Count = 5, Size = 0.46f, Amount = 0.45f, AngleDegrees = 270f }),

        new("S.Generator.Radial"),
        new("Rays", NoiseType.Rays, Placed | ShapeParam.Size | ShapeParam.Count | ShapeParam.Thickness | ShapeParam.Falloff,
            sampleShape: new() { Count = 16, Size = 0.5f, Thickness = 0.5f, Falloff = 0.7f }),
        new("Spokes", NoiseType.Spokes, Placed | ShapeParam.Size | ShapeParam.Count | ShapeParam.Thickness | ShapeParam.Softness,
            sampleShape: new() { Count = 12, Size = 0.45f, Thickness = 0.03f }),
        new("Radial Stripes", NoiseType.RadialStripes, Placed | ShapeParam.Count | ShapeParam.Amount | ShapeParam.Softness,
            "S.Shape.StripeWidth", new() { Count = 12, Amount = 0.5f }),
        new("Burst", NoiseType.Burst, Placed | ShapeParam.Size | ShapeParam.Count | ShapeParam.Thickness | ShapeParam.Falloff | ShapeParam.Seed,
            sampleShape: new() { Count = 24, Size = 0.5f, Thickness = 0.7f, Falloff = 0.6f }),

        new("S.Generator.Curve"),
        new("Spiral", NoiseType.Spiral, Placed | ShapeParam.Size | ShapeParam.Count | ShapeParam.Thickness | ShapeParam.Twist | ShapeParam.Softness,
            sampleShape: new() { Count = 6, Size = 0.48f, Thickness = 0.4f, Twist = 1.5f }),
        new("Bezier Curve", NoiseType.BezierCurve, ShapeParam.Points | ShapeParam.Thickness | ShapeParam.Softness,
            sampleShape: new() { Thickness = 0.1f }),
        new("Sine Wave", NoiseType.SineWave, Placed | ShapeParam.Count | ShapeParam.Amount | ShapeParam.Thickness | ShapeParam.Softness,
            "S.Shape.Amplitude", new() { Count = 2, Amount = 0.45f, Thickness = 0.07f }),
        new("Ribbon", NoiseType.Ribbon, Placed | ShapeParam.Count | ShapeParam.Amount | ShapeParam.Thickness | ShapeParam.Softness,
            "S.Shape.Amplitude", new() { Count = 1, Amount = 0.45f, Thickness = 0.14f }),
    ];

    /// <summary>サンプルを描く設定。見出しの分は含まない。</summary>
    public static NoiseSettings SampleSettings(NoiseCatalogEntry entry) => new()
    {
        Resolution = SampleResolution,
        Type = entry.Type!.Value,
        Seed = 3,
        Frequency = 4,
        // 小さくても見分けが付くよう、重ねすぎない
        Octaves = entry.Type is NoiseType.Stripe or NoiseType.Worley ? 1 : 3,
        Shape = entry.SampleShape,
    };
}
