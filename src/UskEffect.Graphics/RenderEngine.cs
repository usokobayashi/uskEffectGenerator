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

    // 中央のキャンバスの周りの余白 [px]
    private const float MainContentMargin = 12f;

    private readonly object _sync = new();

    private readonly SwapChainTarget?[] _targets = new SwapChainTarget?[Enum.GetValues<SurfaceSlot>().Length];
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

    public void AttachSurface(SurfaceSlot slot, IntPtr hwnd, int width, int height)
    {
        lock (_sync)
        {
            DetachSurfaceCore(slot);
            _targets[(int)slot] = new SwapChainTarget(_gpu, hwnd, width, height);
        }
    }

    public void DetachSurface(SurfaceSlot slot)
    {
        lock (_sync)
            DetachSurfaceCore(slot);
    }

    private void DetachSurfaceCore(SurfaceSlot slot)
    {
        if (_targets[(int)slot] is not { } target)
            return;
        _gpu.Context.ClearState();
        _gpu.Context.Flush();
        target.Dispose();
        _targets[(int)slot] = null;
    }

    public void ReloadShaders() => _shadersDirty = true;

    /// <summary>
    /// 全ての描画先を 1 フレーム描く。sizes は SurfaceSlot の順の画素サイズ。
    /// 描けた描画先がなければ false を返す。
    /// </summary>
    public bool RenderFrame(ReadOnlySpan<(int Width, int Height)> sizes, out RenderStats stats)
    {
        lock (_sync)
            return RenderFrameCore(sizes, out stats);
    }

    private bool RenderFrameCore(ReadOnlySpan<(int Width, int Height)> sizes, out RenderStats stats)
    {
        if (_shadersDirty)
        {
            _shadersDirty = false;
            _noiseErrors = _noise.LoadShader(_noiseShaderPath);
            _previewErrors = _preview.LoadShader(_previewShaderPath);
            _effectErrors = _effect.LoadShader(_effectShaderPath);
            _noiseDirty = true;
        }

        bool rendered = false;
        for (int i = 0; i < _targets.Length; i++)
            rendered |= _targets[i] is not null && sizes[i].Width > 0 && sizes[i].Height > 0;

        if (rendered)
        {
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
            var effect = Effect;
            var frame = EffectEvaluator.Evaluate(effect, preview, Timeline.Time, Timeline.NormalizedTime);
            float time = (float)now.TotalSeconds;

            for (int i = 0; i < _targets.Length; i++)
            {
                if (_targets[i] is not { } target || sizes[i].Width <= 0 || sizes[i].Height <= 0)
                    continue;
                target.Resize(sizes[i].Width, sizes[i].Height);

                var slot = (SurfaceSlot)i;
                var mode = slot switch
                {
                    SurfaceSlot.Tile => PreviewMode.Tile3x3,
                    SurfaceSlot.Polar => PreviewMode.Polar,
                    SurfaceSlot.Effect => PreviewMode.Effect,
                    _ => preview.Mode,
                };
                if (mode == PreviewMode.Effect)
                {
                    _effect.Render(target.RenderTargetView, target.Width, target.Height, _noise.ShaderResourceView,
                        effect, preview.Polar, frame);
                }
                else
                {
                    _preview.Render(target, _noise.ShaderResourceView, _noise.Width, _noise.Height,
                        preview with { Mode = mode }, time, _seamAnalysis,
                        slot == SurfaceSlot.Main ? MainContentMargin : 0f);
                }
            }
            _timer.EndFrame();

            // 同期待ちは最後の 1 回だけにする (描画先の数だけ待つと fps が落ちる)
            for (int i = 1; i < _targets.Length; i++)
            {
                if (_targets[i] is { } thumbnail && sizes[i].Width > 0 && sizes[i].Height > 0)
                    thumbnail.Present(false);
            }
            if (_targets[0] is { } main && sizes[0].Width > 0 && sizes[0].Height > 0)
                main.Present(VSync);
            StartSeamAnalysisIfIdle();
        }

        var errorList = new[] { _noiseErrors, _previewErrors, _effectErrors }.Where(e => e is not null).ToList();
        string? errors = errorList.Count == 0 ? null : string.Join(Environment.NewLine, errorList);
        stats = new RenderStats(_gpu.AdapterName, _noise.Width, _noise.Height,
            _targets[0]?.Width ?? 0, _targets[0]?.Height ?? 0,
            _timer.ComputeMilliseconds, _timer.FrameMilliseconds, errors,
            _seamAnalysis, _analysisPending || _analysisTask is { IsCompleted: false });
        return rendered;
    }

    /// <summary>
    /// 現在の設定で生成したノイズを読み戻す (画像出力用)。UI スレッドから呼んでよい。
    /// 設定変更がまだ描画に反映されていなくても、ここで生成し直してから読む。
    /// </summary>
    public (float[] Pixels, int Width, int Height) ReadNoisePixels()
    {
        lock (_sync)
        {
            if (_shadersDirty)
            {
                _shadersDirty = false;
                _noiseErrors = _noise.LoadShader(_noiseShaderPath);
                _previewErrors = _preview.LoadShader(_previewShaderPath);
                _effectErrors = _effect.LoadShader(_effectShaderPath);
                _noiseDirty = true;
            }
            if (_noiseErrors is not null)
                throw new InvalidOperationException($"ノイズのシェーダーにエラーがあるため書き出せません。{Environment.NewLine}{_noiseErrors}");

            if (_noiseDirty || _noise.ShaderResourceView is null)
            {
                _noise.Generate(_noiseSettings);
                _noiseDirty = false;
                _analysisPending = true;
                _lastNoiseGenerated = _clock.Elapsed;
            }
            return (_noise.ReadPixels(), _noise.Width, _noise.Height);
        }
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
        lock (_sync)
        {
            foreach (var slot in Enum.GetValues<SurfaceSlot>())
                DetachSurfaceCore(slot);
            _timer.Dispose();
            _effect.Dispose();
            _preview.Dispose();
            _noise.Dispose();
            _gpu.Dispose();
        }
    }
}
