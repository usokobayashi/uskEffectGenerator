using System.Diagnostics;

namespace UskEffect.Graphics;

public readonly record struct RenderStats(
    string AdapterName,
    int TextureWidth,
    int TextureHeight,
    int ViewportWidth,
    int ViewportHeight,
    double ComputeMilliseconds,
    double FrameMilliseconds,
    string? ShaderErrors);

/// <summary>
/// Phase 1 検証用の描画エンジン。ノイズ生成 → プレビュー描画 → Present をレンダースレッドで回す。
/// D3D11 の即時コンテキストはスレッドセーフでないため、D3D を触る公開メソッドは _sync で直列化する。
/// 設定はイミュータブルな record の差し替えで受け取るので、UI スレッドからロックなしで設定できる。
/// HLSL ファイルの変更を監視し、次フレームで再コンパイルする。
/// </summary>
public sealed class RenderEngine : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly NoiseGenerator _noise;
    private readonly PreviewRenderer _preview;
    private readonly GpuTimer _timer;
    private readonly FileSystemWatcher? _watcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _noiseShaderPath;
    private readonly string _previewShaderPath;

    private readonly object _sync = new();

    private SwapChainTarget? _target;
    private volatile NoiseSettings _noiseSettings = new();
    private volatile bool _noiseDirty = true;
    private volatile bool _shadersDirty = true;
    private string? _noiseErrors;
    private string? _previewErrors;

    public volatile PreviewSettings Preview = new();
    public volatile bool VSync = true;

    public NoiseSettings Noise
    {
        get => _noiseSettings;
        set
        {
            if (value == _noiseSettings)
                return;
            _noiseSettings = value;
            _noiseDirty = true;
        }
    }

    public string ShaderDirectory { get; }

    public RenderEngine(string shaderDirectory)
    {
        ShaderDirectory = shaderDirectory;
        _noiseShaderPath = Path.Combine(shaderDirectory, "Noise", "PeriodicNoise.hlsl");
        _previewShaderPath = Path.Combine(shaderDirectory, "Preview.hlsl");

#if DEBUG
        _gpu = new GpuDevice(enableDebugLayer: true);
#else
        _gpu = new GpuDevice();
#endif
        _noise = new NoiseGenerator(_gpu);
        _preview = new PreviewRenderer(_gpu);
        _timer = new GpuTimer(_gpu);

        if (Directory.Exists(shaderDirectory))
        {
            _watcher = new FileSystemWatcher(shaderDirectory, "*.hlsl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => _shadersDirty = true;
            _watcher.Created += (_, _) => _shadersDirty = true;
            _watcher.Renamed += (_, _) => _shadersDirty = true;
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void AttachWindow(IntPtr hwnd, int width, int height)
    {
        lock (_sync)
        {
            DetachWindowCore();
            _target = new SwapChainTarget(_gpu, hwnd, width, height);
        }
    }

    public void DetachWindow()
    {
        lock (_sync)
            DetachWindowCore();
    }

    private void DetachWindowCore()
    {
        if (_target is null)
            return;
        _gpu.Context.ClearState();
        _gpu.Context.Flush();
        _target.Dispose();
        _target = null;
    }

    public void ReloadShaders() => _shadersDirty = true;

    /// <summary>1 フレーム描画する。描画先がなければ何もせず false を返す。</summary>
    public bool RenderFrame(int viewportWidth, int viewportHeight, out RenderStats stats)
    {
        lock (_sync)
            return RenderFrameCore(viewportWidth, viewportHeight, out stats);
    }

    private bool RenderFrameCore(int viewportWidth, int viewportHeight, out RenderStats stats)
    {
        if (_shadersDirty)
        {
            _shadersDirty = false;
            _noiseErrors = _noise.LoadShader(_noiseShaderPath);
            _previewErrors = _preview.LoadShader(_previewShaderPath);
            _noiseDirty = true;
        }

        bool rendered = _target is not null && viewportWidth > 0 && viewportHeight > 0;
        if (rendered)
        {
            _target!.Resize(viewportWidth, viewportHeight);

            _timer.BeginFrame();
            if (_noiseDirty)
            {
                _noise.Generate(_noiseSettings);
                _timer.MarkComputeDone();
                _noiseDirty = false;
            }
            _preview.Render(_target, _noise.ShaderResourceView, _noise.Width, _noise.Height, Preview,
                (float)_clock.Elapsed.TotalSeconds);
            _timer.EndFrame();
            _target.Present(VSync);
        }

        string? errors = (_noiseErrors, _previewErrors) switch
        {
            (null, null) => null,
            var (a, b) => string.Join(Environment.NewLine, new[] { a, b }.Where(e => e is not null)),
        };
        stats = new RenderStats(_gpu.AdapterName, _noise.Width, _noise.Height,
            _target?.Width ?? 0, _target?.Height ?? 0,
            _timer.ComputeMilliseconds, _timer.FrameMilliseconds, errors);
        return rendered;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        DetachWindow();
        lock (_sync)
        {
            _timer.Dispose();
            _preview.Dispose();
            _noise.Dispose();
            _gpu.Dispose();
        }
    }
}
