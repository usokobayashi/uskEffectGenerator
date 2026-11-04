using System.Runtime.InteropServices;

namespace UskEffect.Graphics;

public enum NoiseType : uint
{
    Perlin = 0,
    Worley = 1,
    Simplex = 2,

    /// <summary>帯。周波数が帯の本数になる。</summary>
    Stripe = 3,

    // ここから下はノイズではなく形。周期を持たないので、シームレスや合成は効かない

    GradientLinear = 4,
    GradientRadial = 5,
    GradientAngular = 6,
    GradientConical = 7,
    GradientMultiStop = 8,

    ShapeCircle = 9,
    ShapeRing = 10,
    ShapeArc = 11,
    ShapeRectangle = 12,
    ShapePolygon = 13,
    ShapeStar = 14,

    Rays = 15,
    Spokes = 16,
    RadialStripes = 17,
    Burst = 18,

    Spiral = 19,
    BezierCurve = 20,
    SineWave = 21,
    Ribbon = 22,

    /// <summary>単色。色はレイヤーの合成のときに付ける。</summary>
    Solid = 23,
}

/// <summary>プレビューと書き出しの形。</summary>
public enum CompositeOutput
{
    /// <summary>色とアルファをそのまま。色のあるテクスチャの編集向け。</summary>
    Color = 0,

    /// <summary>黒を透明へ変換する。色は保つ。透過素材向け。</summary>
    BlackToAlpha = 1,

    /// <summary>明るさだけの白黒。透明は黒。不透明度マスク向け。</summary>
    Mask = 2,
}

public static class NoiseTypeExtensions
{
    /// <summary>周期を持つノイズか。false なら形で、シームレスや合成は効かない。</summary>
    public static bool IsNoise(this NoiseType type) => type <= NoiseType.Stripe;
}

public enum StripeDirection : uint
{
    Vertical = 0,
    Horizontal = 1,
    Diagonal = 2,
}

public enum FractalMode : uint
{
    Fbm = 0,
    Ridged = 1,
    Turbulence = 2,
}

public enum WorleyMode : uint
{
    F1 = 0,
    F2 = 1,
    F2MinusF1 = 2,
}

[Flags]
public enum SeamMode : uint
{
    Off = 0,
    X = 1,
    Y = 2,
    XY = X | Y,
}

public enum PreviewMode : uint
{
    Rect = 0,

    /// <summary>廃止。古いプロジェクトを読むためだけに残す (Tiling 3×3 の矩形として扱う)。</summary>
    Tile3x3 = 1,
    Polar = 2,

    /// <summary>廃止。古いプロジェクトを読むためだけに残す (矩形として扱う)。</summary>
    SeamCheck = 3,

    /// <summary>廃止。古いプロジェクトを読むためだけに残す (矩形として扱う)。</summary>
    Effect = 4,

    /// <summary>4×4 に並べて、タイルしたときのつながりを見る。書き出しは 1 枚のまま。</summary>
    Tile4x4 = 5,
}

/// <summary>形の設定。座標は UV (0..1) で、大きさは幅に対する割合。</summary>
public sealed record ShapeSettings
{
    public float CenterX { get; init; } = 0.5f;
    public float CenterY { get; init; } = 0.5f;
    public float AngleDegrees { get; init; }
    public float Size { get; init; } = 0.4f;
    public float Thickness { get; init; } = 0.08f;
    public float Softness { get; init; } = 0.01f;

    /// <summary>光線・辺・腕・波の数。</summary>
    public int Count { get; init; } = 8;
    public float Falloff { get; init; } = 1f;

    /// <summary>種類ごとの比率。弧の長さ、星の内径、帯の幅、波の振幅。</summary>
    public float Amount { get; init; } = 0.5f;
    public float Aspect { get; init; } = 1f;
    public float Twist { get; init; } = 2f;

    public float Stop0 { get; init; }
    public float Stop1 { get; init; } = 1f;
    public float Stop2 { get; init; } = 0.3f;
    public float Stop3 { get; init; } = 1f;

    public float P0X { get; init; } = 0.1f;
    public float P0Y { get; init; } = 0.8f;
    public float P1X { get; init; } = 0.5f;
    public float P1Y { get; init; } = 0.1f;
    public float P2X { get; init; } = 0.9f;
    public float P2Y { get; init; } = 0.8f;

    public float SolidR { get; init; } = 1f;
    public float SolidG { get; init; } = 1f;
    public float SolidB { get; init; } = 1f;
}

public sealed record NoiseSettings
{
    public int Resolution { get; init; } = 512;
    public NoiseType Type { get; init; } = NoiseType.Perlin;
    public uint Seed { get; init; } = 1;
    public int Frequency { get; init; } = 4;
    public int Octaves { get; init; } = 5;
    public float Persistence { get; init; } = 0.5f;
    public SeamMode Seam { get; init; } = SeamMode.XY;

    /// <summary>オクターブごとの周波数倍率。周期を保つため整数。</summary>
    public int Lacunarity { get; init; } = 2;
    public FractalMode Fractal { get; init; } = FractalMode.Fbm;
    public WorleyMode Worley { get; init; } = WorleyMode.F1;

    public StripeDirection StripeDirection { get; init; } = StripeDirection.Vertical;

    /// <summary>帯の幅 (周期に対する割合)。</summary>
    public float StripeWidth { get; init; } = 0.5f;

    /// <summary>帯の縁のぼかし (周期に対する割合)。</summary>
    public float StripeSoftness { get; init; } = 0.1f;

    public ShapeSettings Shape { get; init; } = new();

    /// <summary>ドメインワープ量 (UV 単位)。0 で無効。</summary>
    public float WarpStrength { get; init; }

    public float Contrast { get; init; } = 1f;
    public float Brightness { get; init; }
    public bool Invert { get; init; }
}

public sealed record PreviewSettings
{
    /// <summary>タイリングの表示で並べる数。</summary>
    public const int TilePreviewCount = 4;

    public PreviewMode Mode { get; init; } = PreviewMode.Rect;
    public PolarSettings Polar { get; init; } = new();

    /// <summary>UV の繰り返し数とずらし量 (Unity のマテリアルの Tiling / Offset)。書き出す画像には影響しない。</summary>
    public float TilingX { get; init; } = 1f;
    public float TilingY { get; init; } = 1f;
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
}

// cbuffer NoiseParams (Shaders/Noise/PeriodicNoise.hlsl) と同じレイアウト
[StructLayout(LayoutKind.Sequential)]
internal struct NoiseConstants
{
    public uint Width;
    public uint Height;
    public uint Seed;
    public uint Octaves;
    public float Frequency;
    public float Persistence;
    public uint SeamMode;
    public uint NoiseType;

    public float Lacunarity;
    public uint FractalMode;
    public uint WorleyMode;
    public float WarpStrength;

    public float Contrast;
    public float Brightness;
    public uint Invert;
    public float Padding;

    public uint StripeDirection;
    public float StripeWidth;
    public float StripeSoftness;
    public float Padding2;

    public float ShapeCenterX;
    public float ShapeCenterY;
    public float ShapeAngle;
    public float ShapeSize;

    public float ShapeThickness;
    public float ShapeSoftness;
    public float ShapeFalloff;
    public float ShapeAmount;

    public float ShapeAspect;
    public float ShapeTwist;
    public uint ShapeCount;
    public float Padding3;

    public float ShapeStop0;
    public float ShapeStop1;
    public float ShapeStop2;
    public float ShapeStop3;

    public float ShapeP0X;
    public float ShapeP0Y;
    public float ShapeP1X;
    public float ShapeP1Y;

    public float ShapeP2X;
    public float ShapeP2Y;
    public float Padding4;
    public float Padding5;
}

// cbuffer PreviewParams (Shaders/Preview.hlsl) と同じレイアウト
[StructLayout(LayoutKind.Sequential)]
internal struct PreviewConstants
{
    public float ViewportWidth;
    public float ViewportHeight;
    public float TextureWidth;
    public float TextureHeight;

    public uint ViewMode;
    public float ViewModePadding0;
    public float ViewModePadding1;
    public float ViewModePadding2;

    public float InnerRadius;
    public float OuterRadius;
    public float RotationRadians;
    public uint PolarFlags; // bit0: 角度方向反転, bit1: 内外反転


    public float StageR;
    public float StageG;
    public float StageB;
    public float ContentMargin;

    public float ViewPanX;
    public float ViewPanY;
    public float ViewZoom;
    public float ViewRotation;

    public uint SourceIsColor;
    public float Padding0;
    public float Padding1;
    public float Padding2;

    public float TilingX;
    public float TilingY;
    public float OffsetX;
    public float OffsetY;
}
