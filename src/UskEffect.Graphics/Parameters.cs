using System.Runtime.InteropServices;

namespace UskEffect.Graphics;

public enum NoiseType : uint
{
    Perlin = 0,
    Worley = 1,
    Simplex = 2,

    /// <summary>帯。周波数が帯の本数になる。</summary>
    Stripe = 3,
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

    /// <summary>半タイルずらして継ぎ目を中央に集め、差分をヒートマップで重ねる (FR-04)。</summary>
    SeamCheck = 3,

    /// <summary>タイムラインで再生するエフェクトプレビュー (FR-05 / FR-06)。</summary>
    Effect = 4,
}

public enum HeatmapMode : uint
{
    Off = 0,
    Value = 1,
    Gradient = 2,
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

    /// <summary>ドメインワープ量 (UV 単位)。0 で無効。</summary>
    public float WarpStrength { get; init; }

    public float Contrast { get; init; } = 1f;
    public float Brightness { get; init; }
    public bool Invert { get; init; }
}

public sealed record PreviewSettings
{
    public PreviewMode Mode { get; init; } = PreviewMode.Rect;
    public float ScrollSpeedU { get; init; }
    public float ScrollSpeedV { get; init; }
    public PolarSettings Polar { get; init; } = new();
    public HeatmapMode Heatmap { get; init; } = HeatmapMode.Value;

    /// <summary>UV の繰り返し数とずらし量 (Unity のマテリアルの Tiling / Offset)。書き出す画像には影響しない。</summary>
    public float TilingX { get; init; } = 1f;
    public float TilingY { get; init; } = 1f;
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }

    /// <summary>内部平均の何倍の差分から色を付けるか。</summary>
    public float HeatThreshold { get; init; } = 5f;
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
    public float Time;
    public float ScrollSpeedU;
    public float ScrollSpeedV;

    public float InnerRadius;
    public float OuterRadius;
    public float RotationRadians;
    public uint PolarFlags; // bit0: 角度方向反転, bit1: 内外反転

    public uint HeatmapMode;
    public float MeanValueDiff;
    public float MeanGradientDiff;
    public float HeatThreshold;

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
