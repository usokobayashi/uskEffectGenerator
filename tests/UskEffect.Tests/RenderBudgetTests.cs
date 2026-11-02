using System.Diagnostics;
using System.Runtime.InteropServices;
using UskEffect.Graphics;

namespace UskEffect.Tests;

/// <summary>
/// キャンバスの描画負荷を計測する。表示しない窓に描くので利用者の画面に出ない。
/// DWM の合成は含まないので、実画面の fps の代わりにはならない。実行: --filter-method で指定し、Explicit を許可する。
/// </summary>
public sealed class RenderBudgetTests(ITestOutputHelper output)
{
    private const uint WS_POPUP = 0x80000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string? name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [Fact(Explicit = true)]
    public void MeasureFrameCost()
    {
        string shaders = Path.Combine(NoiseFixture.FindRepositoryRoot(), "Shaders");
        using var engine = new RenderEngine(shaders) { VSync = false };

        const int width = 916, height = 672;
        var window = CreateWindowEx(0, "STATIC", null, WS_POPUP, 0, 0, width, height, 0, 0, 0, 0);
        try
        {
            engine.AttachSurface(window, width, height);

            double Median(int frames, Action<int> before)
            {
                var samples = new double[frames];
                for (int f = 0; f < frames; f++)
                {
                    before(f);
                    long start = Stopwatch.GetTimestamp();
                    engine.RenderFrame(width, height, out _);
                    samples[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                Array.Sort(samples);
                return samples[frames / 2];
            }

            Median(30, _ => { }); // シェーダーのコンパイルを除く
            double idle = Median(300, _ => { });
            double dragging = Median(300, f => engine.Noise = new NoiseSettings { Resolution = 512, Seed = (uint)f, Octaves = 8 });
            engine.Preview = new PreviewSettings { Mode = PreviewMode.Effect };
            engine.Timeline.Play();
            double effect = Median(300, _ => { });

            output.WriteLine($"操作なし {idle:0.00} ms / スライダー操作相当 (毎フレーム生成・8 オクターブ) {dragging:0.00} ms / エフェクト再生 {effect:0.00} ms");
            Assert.True(dragging < 33.3, $"512×512 の操作時 30fps を満たさない ({dragging:0.00} ms)");
            Assert.True(effect < 16.7, $"アニメーション 60fps を満たさない ({effect:0.00} ms)");
        }
        finally
        {
            engine.DetachSurface();
            DestroyWindow(window);
        }
    }
}
