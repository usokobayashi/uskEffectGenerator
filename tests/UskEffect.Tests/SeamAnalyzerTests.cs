namespace UskEffect.Tests;

/// <summary>答えがわかっている合成画像で継ぎ目解析を検証する。</summary>
public sealed class SeamAnalyzerTests
{
    private static float[] Image(int width, int height, Func<double, double, double> f)
    {
        var pixels = new float[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = (float)f((double)x / width, (double)y / height);
        return pixels;
    }

    [Fact]
    public void PeriodicSine_IsSeamlessOnBothAxes()
    {
        var pixels = Image(128, 128, (u, v) => 0.5 + 0.25 * Math.Sin(2 * Math.PI * 3 * u) * Math.Cos(2 * Math.PI * 2 * v));

        var analysis = SeamAnalyzer.Analyze(pixels, 128, 128);

        Assert.True(analysis.Horizontal.IsSeamless, analysis.Horizontal.ToString());
        Assert.True(analysis.Vertical.IsSeamless, analysis.Vertical.ToString());
    }

    [Fact]
    public void RampAlongX_HasSeamOnlyHorizontally()
    {
        // 左右で 0 → 1 に増えるので左右の端が食い違う。上下方向には変化しない
        var pixels = Image(128, 64, (u, v) => u + 0.1 * Math.Sin(2 * Math.PI * v));

        var analysis = SeamAnalyzer.Analyze(pixels, 128, 64);

        Assert.False(analysis.Horizontal.IsSeamless, analysis.Horizontal.ToString());
        Assert.True(analysis.Vertical.IsSeamless, analysis.Vertical.ToString());
    }

    [Fact]
    public void NonSquareImage_UsesCorrectAxes()
    {
        // 上下だけ食い違う長方形 (幅 ≠ 高さ) で軸の取り違えがないこと
        var pixels = Image(96, 40, (u, v) => v + 0.1 * Math.Sin(2 * Math.PI * u));

        var analysis = SeamAnalyzer.Analyze(pixels, 96, 40);

        Assert.True(analysis.Horizontal.IsSeamless, analysis.Horizontal.ToString());
        Assert.False(analysis.Vertical.IsSeamless, analysis.Vertical.ToString());
    }

    [Fact]
    public void KinkAtBoundary_IsCaughtByGradientOnly()
    {
        // 三角波: 値は端でつながるが、傾きの向きが端で反転する
        var pixels = Image(128, 32, (u, v) => u < 0.5 ? u : 1 - u);

        var report = SeamAnalyzer.Horizontal(pixels, 128, 32);

        Assert.True(report.ValueRatio < SeamReport.SeamlessValueLimit, report.ToString());
        Assert.True(report.GradientRatio >= SeamReport.SeamlessGradientLimit, report.ToString());
    }
}
