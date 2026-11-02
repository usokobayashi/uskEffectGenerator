using UskEffect.Graphics;

namespace UskEffect.Tests;

public sealed class PeriodicNoiseTests(NoiseFixture fixture) : IClassFixture<NoiseFixture>
{
    private const int Size = 256;

    // 継ぎ目なしと判定する上限。内部の差分と同程度 (比 ≈ 1) なら継ぎ目はない
    private const double SeamlessValueLimit = 1.5;
    private const double SeamlessGradientLimit = 2.0;

    // 継ぎ目ありと判定する下限。メトリクスが継ぎ目を検出できることの確認に使う
    private const double SeamDetectedLimit = 3.0;

    private static NoiseSettings Base(NoiseType type, FractalMode fractal = FractalMode.Fbm) => new()
    {
        Resolution = Size,
        Type = type,
        Seed = 7,
        Frequency = 4,
        Octaves = 4,
        Persistence = 0.5f,
        Fractal = fractal,
    };

    public static TheoryData<NoiseType, FractalMode> AllTypes()
    {
        var data = new TheoryData<NoiseType, FractalMode>();
        foreach (var type in Enum.GetValues<NoiseType>())
            foreach (var fractal in Enum.GetValues<FractalMode>())
                data.Add(type, fractal);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void SeamXY_IsSeamlessOnBothAxes(NoiseType type, FractalMode fractal)
    {
        var pixels = fixture.Generate(Base(type, fractal) with { Seam = SeamMode.XY });

        AssertSeamless(SeamMetrics.Horizontal(pixels, Size, Size), "X");
        AssertSeamless(SeamMetrics.Vertical(pixels, Size, Size), "Y");
    }

    [Theory]
    [InlineData(NoiseType.Perlin)]
    [InlineData(NoiseType.Worley)]
    [InlineData(NoiseType.Simplex)]
    public void SeamX_IsSeamlessOnlyOnX(NoiseType type)
    {
        var pixels = fixture.Generate(Base(type) with { Seam = SeamMode.X });

        AssertSeamless(SeamMetrics.Horizontal(pixels, Size, Size), "X");
        AssertSeamDetected(SeamMetrics.Vertical(pixels, Size, Size), "Y");
    }

    [Theory]
    [InlineData(NoiseType.Perlin)]
    [InlineData(NoiseType.Worley)]
    [InlineData(NoiseType.Simplex)]
    public void SeamOff_HasDetectableSeam(NoiseType type)
    {
        var pixels = fixture.Generate(Base(type) with { Seam = SeamMode.Off });

        AssertSeamDetected(SeamMetrics.Horizontal(pixels, Size, Size), "X");
        AssertSeamDetected(SeamMetrics.Vertical(pixels, Size, Size), "Y");
    }

    [Theory]
    [InlineData(WorleyMode.F1)]
    [InlineData(WorleyMode.F2)]
    [InlineData(WorleyMode.F2MinusF1)]
    public void WorleyModes_AreSeamless(WorleyMode mode)
    {
        var pixels = fixture.Generate(Base(NoiseType.Worley) with { Worley = mode, Seam = SeamMode.XY });

        AssertSeamless(SeamMetrics.Horizontal(pixels, Size, Size), "X");
        AssertSeamless(SeamMetrics.Vertical(pixels, Size, Size), "Y");
    }

    // 1 オクターブなら、隣接画素の差は勾配 × 画素幅で抑えられる。セル探索範囲が足りないと値が飛ぶ
    [Theory]
    [InlineData(WorleyMode.F1)]
    [InlineData(WorleyMode.F2)]
    [InlineData(WorleyMode.F2MinusF1)]
    public void WorleyModes_HaveNoInteriorJumps(WorleyMode mode)
    {
        var settings = Base(NoiseType.Worley) with { Worley = mode, Octaves = 1, Frequency = 6, Seam = SeamMode.XY };
        var pixels = fixture.Generate(settings);

        // 距離の勾配は最大 1 (F2 − F1 は 2)、BaseNoise の倍率は最大 1.5、1 画素は 6/256 セル
        double maxExpected = 2.0 * 1.5 * settings.Frequency / Size;
        double maxJump = 0;
        for (int y = 0; y < Size; y++)
            for (int x = 1; x < Size; x++)
                maxJump = Math.Max(maxJump, Math.Abs(pixels[y * Size + x] - pixels[y * Size + x - 1]));

        Assert.True(maxJump <= maxExpected * 1.5, $"内部で値が不連続に飛んでいる (最大差 {maxJump:0.000}, 許容 {maxExpected * 1.5:0.000})");
    }

    [Theory]
    [InlineData(NoiseType.Perlin, 3)]
    [InlineData(NoiseType.Simplex, 3)]
    [InlineData(NoiseType.Worley, 4)]
    public void IntegerLacunarity_KeepsPeriod(NoiseType type, int lacunarity)
    {
        var pixels = fixture.Generate(Base(type) with { Lacunarity = lacunarity, Octaves = 3, Seam = SeamMode.XY });

        AssertSeamless(SeamMetrics.Horizontal(pixels, Size, Size), "X");
        AssertSeamless(SeamMetrics.Vertical(pixels, Size, Size), "Y");
    }

    [Theory]
    [InlineData(NoiseType.Perlin)]
    [InlineData(NoiseType.Worley)]
    [InlineData(NoiseType.Simplex)]
    public void DomainWarp_KeepsPeriod(NoiseType type)
    {
        var pixels = fixture.Generate(Base(type) with { WarpStrength = 0.15f, Seam = SeamMode.XY });

        AssertSeamless(SeamMetrics.Horizontal(pixels, Size, Size), "X");
        AssertSeamless(SeamMetrics.Vertical(pixels, Size, Size), "Y");
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Output_IsInRangeAndNotFlat(NoiseType type, FractalMode fractal)
    {
        var pixels = fixture.Generate(Base(type, fractal) with { Seam = SeamMode.XY });

        Assert.All(pixels, p => Assert.InRange(p, 0f, 1f));
        double mean = pixels.Average();
        double std = Math.Sqrt(pixels.Average(p => (p - mean) * (p - mean)));
        Assert.True(std > 0.03, $"出力がほぼ一定 (std {std:0.000})");
    }

    [Fact]
    public void SameSeed_IsDeterministic_DifferentSeed_Differs()
    {
        var a = fixture.Generate(Base(NoiseType.Simplex));
        var b = fixture.Generate(Base(NoiseType.Simplex));
        var c = fixture.Generate(Base(NoiseType.Simplex) with { Seed = 8 });

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Invert_FlipsValues()
    {
        var normal = fixture.Generate(Base(NoiseType.Perlin));
        var inverted = fixture.Generate(Base(NoiseType.Perlin) with { Invert = true });

        for (int i = 0; i < normal.Length; i += 97)
            Assert.Equal(1f - normal[i], inverted[i], 1e-5f);
    }

    private static void AssertSeamless(SeamReport report, string axis)
    {
        Assert.True(report.ValueRatio < SeamlessValueLimit && report.GradientRatio < SeamlessGradientLimit,
            $"{axis} 方向に継ぎ目がある ({report})");
    }

    private static void AssertSeamDetected(SeamReport report, string axis)
    {
        Assert.True(report.ValueRatio > SeamDetectedLimit, $"{axis} 方向の継ぎ目を検出できない ({report})");
    }
}
