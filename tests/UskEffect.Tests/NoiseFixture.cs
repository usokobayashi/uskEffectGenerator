using UskEffect.Graphics;

namespace UskEffect.Tests;

/// <summary>D3D11 デバイスとノイズ生成器をテストクラス間で共有する (ウィンドウなし、GPU がなければ WARP)。</summary>
public sealed class NoiseFixture : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly NoiseGenerator _generator;
    private readonly Lock _sync = new();

    public NoiseFixture()
    {
        _gpu = new GpuDevice();
        _generator = new NoiseGenerator(_gpu);

        string? errors = _generator.LoadShader(Path.Combine(FindRepositoryRoot(), "Shaders", "Noise", "PeriodicNoise.hlsl"));
        if (errors is not null)
            throw new InvalidOperationException(errors);
    }

    public string AdapterName => _gpu.AdapterName;

    public float[] Generate(NoiseSettings settings)
    {
        lock (_sync)
        {
            _generator.Generate(settings);
            return _generator.ReadPixels();
        }
    }

    internal static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UskEffect.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("UskEffect.slnx が見つからない");
    }

    public void Dispose()
    {
        _generator.Dispose();
        _gpu.Dispose();
    }
}
