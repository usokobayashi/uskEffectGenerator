using System.Runtime.InteropServices;

namespace UskEffect.Graphics;

public enum NoiseType : uint
{
    Perlin = 0,
    Worley = 1,
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
}

public sealed record PreviewSettings
{
    public PreviewMode Mode { get; init; } = PreviewMode.Rect;
    public float ScrollSpeedU { get; init; }
    public float ScrollSpeedV { get; init; }
    public float InnerRadius { get; init; } = 0.3f;
    public float OuterRadius { get; init; } = 1.0f;
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
    public float Padding0;
    public float Padding1;
}
