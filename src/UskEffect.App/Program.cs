using Avalonia;
using UskEffect.App.Views;
using UskEffect.Graphics;

namespace UskEffect.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--self-check", var logPath])
            return SelfCheck(logPath);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();

    // 配布する実行ファイルの確認用。窓を出さずに、UI の土台と GPU とシェーダーが動くかを調べて結果をファイルへ書く
    private static int SelfCheck(string logPath)
    {
        try
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            string shaders = MainWindow.LocateShaderDirectory();
            using var engine = new RenderEngine(shaders) { TransparentBase = true };
            var noise = new NoiseSettings { Resolution = 64 };
            engine.Noise = noise;
            engine.SetLayerNoise(engine.ActiveLayerId, noise);
            var image = engine.ReadComposite() ?? throw new InvalidOperationException("合成できない");
            var samples = engine.RenderSamples(NoiseCatalog.Create().Where(e => !e.IsHeader).Select(NoiseCatalog.SampleSettings).ToArray());
            engine.RenderFrame(0, 0, out var stats);
            if (samples.Count == 0 || !string.IsNullOrEmpty(stats.ShaderErrors))
                throw new InvalidOperationException($"シェーダーのエラー: {stats.ShaderErrors}");

            File.WriteAllText(logPath, $"OK gpu={stats.AdapterName} shaders={shaders} image={image.Width}x{image.Height} samples={samples.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"NG {ex}");
            return 1;
        }
    }
}
