using UskEffect.Graphics;

namespace UskEffect.Tests;

/// <summary>形の生成 (グラデーション・シェイプ・放射・曲線)。実際の GPU で画素を確かめる。</summary>
public sealed class ShapeGeneratorTests(NoiseFixture fixture) : IClassFixture<NoiseFixture>
{
    private const int Resolution = 128;

    private float[] Generate(NoiseType type, ShapeSettings? shape = null) =>
        fixture.Generate(new NoiseSettings { Resolution = Resolution, Type = type, Shape = shape ?? new ShapeSettings() });

    // UV の位置の値
    private static float At(float[] pixels, double u, double v)
    {
        int x = Math.Clamp((int)(u * Resolution), 0, Resolution - 1);
        int y = Math.Clamp((int)(v * Resolution), 0, Resolution - 1);
        return pixels[y * Resolution + x];
    }

    // 中心の周りを一周して、明るい部分に入る回数を数える
    private static int CountLobes(float[] pixels, double radius)
    {
        const int steps = 1440;
        int lobes = 0;
        bool previous = false;
        for (int i = 0; i <= steps; i++)
        {
            double angle = i * 2 * Math.PI / steps;
            bool bright = At(pixels, 0.5 + Math.Cos(angle) * radius, 0.5 + Math.Sin(angle) * radius) > 0.5f;
            if (bright && !previous && i > 0)
                lobes++;
            previous = bright;
        }
        return lobes;
    }

    public static TheoryData<NoiseType> AllShapes()
    {
        var data = new TheoryData<NoiseType>();
        foreach (var type in Enum.GetValues<NoiseType>().Where(t => !t.IsNoise() && t != NoiseType.Solid))
            data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void EveryShape_IsInRange_NotFlat_AndRepeatable(NoiseType type)
    {
        var pixels = Generate(type);

        Assert.All(pixels, p => Assert.InRange(p, 0f, 1f));
        Assert.True(pixels.Max() - pixels.Min() > 0.3f, $"{type} の値の幅 {pixels.Max() - pixels.Min()}");
        Assert.Equal(pixels, Generate(type));
    }

    [Fact]
    public void Linear_RisesAlongX_AndAngleRotatesIt()
    {
        var shape = new ShapeSettings { Size = 0.5f };
        var pixels = Generate(NoiseType.GradientLinear, shape);
        Assert.True(At(pixels, 0.1, 0.5) < At(pixels, 0.5, 0.5));
        Assert.True(At(pixels, 0.5, 0.5) < At(pixels, 0.9, 0.5));

        var rotated = Generate(NoiseType.GradientLinear, shape with { AngleDegrees = 90 });
        Assert.True(At(rotated, 0.5, 0.1) < At(rotated, 0.5, 0.9));
        Assert.Equal(At(rotated, 0.1, 0.5), At(rotated, 0.9, 0.5), 2);
    }

    [Fact]
    public void Radial_IsBrightestAtTheCenter_AndFollowsIt()
    {
        var pixels = Generate(NoiseType.GradientRadial, new ShapeSettings { Size = 0.5f });
        Assert.True(At(pixels, 0.5, 0.5) > 0.95f);
        Assert.True(At(pixels, 0.02, 0.02) < 0.01f);

        var moved = Generate(NoiseType.GradientRadial, new ShapeSettings { Size = 0.2f, CenterX = 0.25f, CenterY = 0.75f });
        Assert.True(At(moved, 0.25, 0.75) > 0.9f);
        Assert.True(At(moved, 0.75, 0.25) < 0.01f);
    }

    [Fact]
    public void Angular_GoesAroundOnce_AndConicalIsSymmetric()
    {
        var angular = Generate(NoiseType.GradientAngular);
        Assert.True(At(angular, 0.75, 0.5) > 0.45f && At(angular, 0.75, 0.5) < 0.55f);
        Assert.True(At(angular, 0.5, 0.75) > 0.7f);
        Assert.True(At(angular, 0.5, 0.25) < 0.3f);

        var conical = Generate(NoiseType.GradientConical);
        Assert.True(At(conical, 0.9, 0.5) > 0.95f);
        Assert.True(At(conical, 0.1, 0.5) < 0.05f);
        Assert.Equal(At(conical, 0.5, 0.2), At(conical, 0.5, 0.8), 1);
    }

    [Fact]
    public void MultiStop_PassesThroughEachStop()
    {
        var shape = new ShapeSettings { Size = 0.5f, Stop0 = 0f, Stop1 = 1f, Stop2 = 0.2f, Stop3 = 0.8f };
        var pixels = Generate(NoiseType.GradientMultiStop, shape);
        Assert.Equal(0f, At(pixels, 0.003, 0.5), 1);
        Assert.Equal(1f, At(pixels, 1 / 3.0, 0.5), 1);
        Assert.Equal(0.2f, At(pixels, 2 / 3.0, 0.5), 1);
        Assert.Equal(0.8f, At(pixels, 0.997, 0.5), 1);
    }

    [Fact]
    public void Circle_And_Ring_FillTheRightRadius()
    {
        var circle = Generate(NoiseType.ShapeCircle, new ShapeSettings { Size = 0.3f });
        Assert.True(At(circle, 0.5, 0.5) > 0.99f);
        Assert.True(At(circle, 0.75, 0.5) > 0.99f);
        Assert.True(At(circle, 0.85, 0.5) < 0.01f);

        var ring = Generate(NoiseType.ShapeRing, new ShapeSettings { Size = 0.3f, Thickness = 0.1f });
        Assert.True(At(ring, 0.5, 0.5) < 0.01f);
        Assert.True(At(ring, 0.8, 0.5) > 0.99f);
        Assert.True(At(ring, 0.95, 0.5) < 0.01f);
    }

    [Fact]
    public void Arc_CoversOnlyItsShare()
    {
        var pixels = Generate(NoiseType.ShapeArc, new ShapeSettings { Size = 0.3f, Thickness = 0.1f, Amount = 0.25f });
        Assert.True(At(pixels, 0.8, 0.5) > 0.99f);
        Assert.True(At(pixels, 0.2, 0.5) < 0.01f);
        Assert.True(At(pixels, 0.5, 0.8) < 0.01f);
    }

    [Fact]
    public void Rectangle_UsesItsAspect()
    {
        var pixels = Generate(NoiseType.ShapeRectangle, new ShapeSettings { Size = 0.4f, Aspect = 0.25f });
        Assert.True(At(pixels, 0.85, 0.5) > 0.99f);
        Assert.True(At(pixels, 0.5, 0.65) < 0.01f);
        Assert.True(At(pixels, 0.5, 0.55) > 0.99f);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Polygon_HasFlatSides_ThatCutInsideTheCircle(int sides)
    {
        var pixels = Generate(NoiseType.ShapePolygon, new ShapeSettings { Size = 0.4f, Count = sides });
        double apothem = 0.4 * Math.Cos(Math.PI / sides);
        // 辺の中央が +x を向く。頂点は外接円の近くまで届き、辺の中央はそれより内側で切れる
        double vertex = Math.PI / sides;
        double between = (apothem + 0.4) / 2;
        Assert.True(At(pixels, 0.5 + Math.Cos(vertex) * between, 0.5 + Math.Sin(vertex) * between) > 0.9f);
        Assert.True(At(pixels, 0.5 + between, 0.5) < 0.1f);
        Assert.True(At(pixels, 0.5 + apothem - 0.03, 0.5) > 0.9f);
        Assert.Equal(sides, CountLobes(pixels, (apothem + 0.4) / 2));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void Star_HasTheRightNumberOfPoints(int points)
    {
        var pixels = Generate(NoiseType.ShapeStar, new ShapeSettings { Size = 0.45f, Count = points, Amount = 0.4f });
        Assert.True(At(pixels, 0.5, 0.5) > 0.99f);
        Assert.Equal(points, CountLobes(pixels, 0.3));
    }

    [Theory]
    [InlineData(NoiseType.Rays, 8)]
    [InlineData(NoiseType.Rays, 16)]
    [InlineData(NoiseType.Spokes, 12)]
    [InlineData(NoiseType.RadialStripes, 10)]
    public void Radial_HasTheRightNumberOfLines(NoiseType type, int count)
    {
        var shape = new ShapeSettings { Size = 0.5f, Count = count, Thickness = type == NoiseType.Spokes ? 0.03f : 0.5f, Falloff = 0.1f, Amount = 0.5f };
        Assert.Equal(count, CountLobes(Generate(type, shape), 0.3));
    }

    [Fact]
    public void Burst_ChangesWithTheSeed()
    {
        var settings = new NoiseSettings { Resolution = Resolution, Type = NoiseType.Burst };
        Assert.NotEqual(fixture.Generate(settings), fixture.Generate(settings with { Seed = 9 }));
    }

    [Fact]
    public void Spiral_HasArms_ThatTurnWithTheRadius()
    {
        var shape = new ShapeSettings { Size = 0.5f, Count = 4, Thickness = 0.5f, Twist = 2 };
        var pixels = Generate(NoiseType.Spiral, shape);
        Assert.Equal(4, CountLobes(pixels, 0.2));
        // ねじれ 2、大きさ 0.5 なので半径 0.25 で一周期。半周期ずらすと明暗が逆になる
        Assert.NotEqual(At(pixels, 0.7, 0.5) > 0.5f, At(pixels, 0.7 + 0.125, 0.5) > 0.5f);
    }

    [Fact]
    public void Bezier_PassesThroughItsEndsAndMiddle()
    {
        var pixels = Generate(NoiseType.BezierCurve, new ShapeSettings { Thickness = 0.04f, P0X = 0.1f, P0Y = 0.8f, P1X = 0.5f, P1Y = 0f, P2X = 0.9f, P2Y = 0.8f });
        Assert.True(At(pixels, 0.1, 0.8) > 0.9f);
        Assert.True(At(pixels, 0.9, 0.8) > 0.9f);
        Assert.True(At(pixels, 0.5, 0.4) > 0.9f); // t = 0.5 の点
        Assert.True(At(pixels, 0.5, 0.8) < 0.01f);
    }

    [Fact]
    public void Bezier_WithStraightControlPoints_IsALine()
    {
        var pixels = Generate(NoiseType.BezierCurve, new ShapeSettings { Thickness = 0.04f, P0X = 0.1f, P0Y = 0.5f, P1X = 0.5f, P1Y = 0.5f, P2X = 0.9f, P2Y = 0.5f });
        Assert.True(At(pixels, 0.5, 0.5) > 0.9f);
        Assert.True(At(pixels, 0.5, 0.7) < 0.01f);
    }

    [Theory]
    [InlineData(NoiseType.SineWave)]
    [InlineData(NoiseType.Ribbon)]
    public void Waves_FollowTheSine(NoiseType type)
    {
        var pixels = Generate(type, new ShapeSettings { Count = 1, Amount = 0.5f, Thickness = 0.06f });
        Assert.True(At(pixels, 0.75, 0.75) > 0.5f); // sin の山
        Assert.True(At(pixels, 0.25, 0.25) > 0.5f); // sin の谷
        Assert.True(At(pixels, 0.75, 0.25) < 0.01f);
    }

    // 画面に欄を出さない設定で、形や帯が変わらないこと
    [Fact]
    public void HiddenSettings_DoNotChangeTheResult()
    {
        var circle = new NoiseSettings { Resolution = Resolution, Type = NoiseType.ShapeCircle };
        Assert.Equal(fixture.Generate(circle), fixture.Generate(circle with { WarpStrength = 0.3f, Octaves = 2, Frequency = 9 }));

        var stripe = new NoiseSettings { Resolution = Resolution, Type = NoiseType.Stripe };
        Assert.Equal(fixture.Generate(stripe with { Octaves = 1 }),
            fixture.Generate(stripe with { Octaves = 6, Fractal = FractalMode.Ridged }));
    }

    [Fact]
    public void Invert_And_Contrast_StillApplyToShapes()
    {
        var settings = new NoiseSettings { Resolution = Resolution, Type = NoiseType.ShapeCircle };
        Assert.True(At(fixture.Generate(settings with { Invert = true }), 0.5, 0.5) < 0.01f);
        Assert.True(At(fixture.Generate(settings with { Brightness = -0.5f }), 0.5, 0.5) < 0.6f);
    }
}
