using System.Runtime.InteropServices;

namespace UskEffect.Graphics;

public enum NoiseType : uint
{
    Perlin = 0,
    Worley = 1,
    Simplex = 2,
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

    /// <summary>内部平均の何倍の差分から色を付けるか。</summary>
    public float HeatThreshold { get; init; } = 5f;
}

/// <summary>同時に描く描画先。中央のキャンバスと、右上の 3 つのプレビュー。</summary>
public enum SurfaceSlot
{
    Main = 0,
    Tile = 1,
    Polar = 2,
    Effect = 3,
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
}
