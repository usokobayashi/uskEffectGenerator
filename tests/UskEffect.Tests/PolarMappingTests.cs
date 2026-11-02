using System.Numerics;
using UskEffect.Graphics;

namespace UskEffect.Tests;

public sealed class PolarMappingTests
{
    private const float Tolerance = 1e-4f;

    public static TheoryData<PolarSettings> Settings() => new()
    {
        new PolarSettings(0.3f, 1f),
        new PolarSettings(0f, 1f),
        new PolarSettings(0.2f, 0.8f, 37f),
        new PolarSettings(0.3f, 1f, 0f, FlipAngular: true),
        new PolarSettings(0.3f, 1f, 200f, FlipAngular: true, FlipRadial: true),
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void UvToCanvas_ThenBack_ReturnsSameUv(PolarSettings settings)
    {
        for (float u = 0.05f; u < 1f; u += 0.1f)
        {
            for (float v = 0.05f; v < 1f; v += 0.1f)
            {
                var canvas = PolarMapping.UvToCanvas(new Vector2(u, v), settings);
                Assert.True(PolarMapping.TryCanvasToUv(canvas, settings, out var uv), $"({u}, {v}) がリング外になった");
                Assert.Equal(u, uv.X, Tolerance);
                Assert.Equal(v, uv.Y, Tolerance);
            }
        }
    }

    [Fact]
    public void Convention_ZeroAngleIsPlusX_CounterClockwiseIncreasesU_InnerIsV0()
    {
        var s = new PolarSettings(0.25f, 1f);

        Assert.True(PolarMapping.TryCanvasToUv(new Vector2(0.25f, 0f), s, out var right));
        Assert.Equal(0f, right.X, Tolerance);
        Assert.Equal(0f, right.Y, Tolerance);

        Assert.True(PolarMapping.TryCanvasToUv(new Vector2(0f, 1f), s, out var top));
        Assert.Equal(0.25f, top.X, Tolerance);
        Assert.Equal(1f, top.Y, Tolerance);
    }

    [Fact]
    public void Rotation_RotatesDisplayCounterClockwise()
    {
        // 90° 回すと u = 0 は上 (+y) に来る
        var canvas = PolarMapping.UvToCanvas(new Vector2(0f, 1f), new PolarSettings(0.3f, 1f, 90f));
        Assert.Equal(0f, canvas.X, Tolerance);
        Assert.Equal(1f, canvas.Y, Tolerance);
    }

    // 0°/360° をまたいでも UV が連続している (u は 0 付近と 1 付近で Repeat 上は隣接) (FR-23 の前提)
    [Theory]
    [InlineData(0f, false)]
    [InlineData(123f, false)]
    [InlineData(0f, true)]
    public void AcrossSeam_UvIsAdjacentUnderWrap(float rotation, bool flipAngular)
    {
        var s = new PolarSettings(0.3f, 1f, rotation, flipAngular);
        var seamPoint = PolarMapping.UvToCanvas(new Vector2(0f, 0.5f), s);
        var tangent = Vector2.Normalize(new Vector2(-seamPoint.Y, seamPoint.X)) * 1e-3f;

        Assert.True(PolarMapping.TryCanvasToUv(seamPoint + tangent, s, out var a));
        Assert.True(PolarMapping.TryCanvasToUv(seamPoint - tangent, s, out var b));

        float wrappedDistance = MathF.Min(MathF.Abs(a.X - b.X), 1f - MathF.Abs(a.X - b.X));
        Assert.True(wrappedDistance < 1e-2f, $"u が不連続 ({a.X} と {b.X})");
        Assert.Equal(a.Y, b.Y, 1e-3f);
    }

    [Theory]
    [InlineData(0.1f, 0f)]   // 内径より内側
    [InlineData(0.9f, 0.9f)] // 外接円より外側
    public void OutsideRing_IsRejected(float x, float y)
    {
        Assert.False(PolarMapping.TryCanvasToUv(new Vector2(x, y), new PolarSettings(0.3f, 1f), out _));
    }
}
