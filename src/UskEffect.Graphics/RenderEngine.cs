using System.Diagnostics;
using UskEffect.Graphics.Analysis;
using UskEffect.Graphics.Effects;

namespace UskEffect.Graphics;

public readonly record struct RenderStats(
    string AdapterName,
    int TextureWidth,
    int TextureHeight,
    int ViewportWidth,
    int ViewportHeight,
    double ComputeMilliseconds,
    double FrameMilliseconds,
    string? ShaderErrors,
    SeamAnalysis? Seam,
    bool SeamAnalysisPending);

/// <summary>
/// 描画エンジン。ノイズ生成 → プレビュー描画 → Present をレンダースレッドで回す。
/// ノイズを作り直したら、操作が落ち着いてから GPU から読み戻して継ぎ目を解析する (FR-04)。
/// D3D11 の即時コンテキストはスレッドセーフでないため、D3D を触る公開メソッドは _sync で直列化する。
/// 設定はイミュータブルな record の差し替えで受け取るので、UI スレッドからロックなしで設定できる。
/// HLSL ファイルの変更を監視し、次フレームで再コンパイルする。
/// </summary>
public sealed class RenderEngine : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly NoiseGenerator _noise;
    private readonly PreviewRenderer _preview;
    private readonly EffectRenderer _effect;
    private readonly GpuTimer _timer;
    private readonly FileSystemWatcher? _watcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _noiseShaderPath;
    private readonly string _previewShaderPath;
    private readonly string _effectShaderPath;
    private TimeSpan _lastFrameTime;

    private readonly object _sync = new();

    private SwapChainTarget? _target;
    private volatile NoiseSettings _noiseSettings = new();
    private volatile bool _noiseDirty = true;
    private volatile bool _shadersDirty = true;
    private string? _noiseErrors;
    private string? _previewErrors;
    private string? _effectErrors;

    // スライダー操作中に毎フレーム読み戻すと描画が詰まるので、最後の生成から少し待って 1 回だけ解析する
    private static readonly TimeSpan AnalysisDelay = TimeSpan.FromMilliseconds(150);
    private bool _analysisPending;
    private TimeSpan _lastNoiseGenerated;
    private Task? _analysisTask;
    private volatile SeamAnalysis? _seamAnalysis;

    public volatile PreviewSettings Preview = new();
    public volatile EffectSettings Effect = new();

    /// <summary>エフェクトプレビューの再生位置。UI スレッドから再生操作できる。</summary>
    public EffectTimeline Timeline { get; } = new();
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
        _effectShaderPath = Path.Combine(shaderDirectory, "Effect.hlsl");

#if DEBUG
        _gpu = new GpuDevice(enableDebugLayer: true);
#else
        _gpu = new GpuDevice();
#endif
        _noise = new NoiseGenerator(_gpu);
        _preview = new PreviewRenderer(_gpu);
        _effect = new EffectRenderer(_gpu);
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
            _effectErrors = _effect.LoadShader(_effectShaderPath);
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
                _analysisPending = true;
                _lastNoiseGenerated = _clock.Elapsed;
            }
            var now = _clock.Elapsed;
            Timeline.Advance((now - _lastFrameTime).TotalSeconds);
            _lastFrameTime = now;

            var preview = Preview;
            if (preview.Mode == PreviewMode.Effect)
            {
                var effect = Effect;
                var frame = EffectEvaluator.Evaluate(effect, preview, Timeline.Time, Timeline.NormalizedTime);
                _effect.Render(_target.RenderTargetView, _target.Width, _target.Height, _noise.ShaderResourceView,
                    effect, preview.Polar, frame);
            }
            else
            {
                _preview.Render(_target, _noise.ShaderResourceView, _noise.Width, _noise.Height, preview,
                    (float)now.TotalSeconds, _seamAnalysis);
            }
            _timer.EndFrame();
            _target.Present(VSync);
            StartSeamAnalysisIfIdle();
        }

        var errorList = new[] { _noiseErrors, _previewErrors, _effectErrors }.Where(e => e is not null).ToList();
        string? errors = errorList.Count == 0 ? null : string.Join(Environment.NewLine, errorList);
        stats = new RenderStats(_gpu.AdapterName, _noise.Width, _noise.Height,
            _target?.Width ?? 0, _target?.Height ?? 0,
            _timer.ComputeMilliseconds, _timer.FrameMilliseconds, errors,
            _seamAnalysis, _analysisPending || _analysisTask is { IsCompleted: false });
        return rendered;
    }

    private void StartSeamAnalysisIfIdle()
    {
        if (!_analysisPending || _clock.Elapsed - _lastNoiseGenerated < AnalysisDelay)
            return;
        if (_analysisTask is { IsCompleted: false })
            return; // 前の解析が終わってから。終わった時点でまだ pending なら次フレームで再解析する

        _analysisPending = false;
        float[] pixels = _noise.ReadPixels();
        int width = _noise.Width, height = _noise.Height;
        if (pixels.Length == 0)
            return;
        _analysisTask = Task.Run(() => _seamAnalysis = SeamAnalyzer.Analyze(pixels, width, height));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _analysisTask?.Wait();
        DetachWindow();
        lock (_sync)
        {
            _timer.Dispose();
            _effect.Dispose();
            _preview.Dispose();
            _noise.Dispose();
            _gpu.Dispose();
        }
    }
}
