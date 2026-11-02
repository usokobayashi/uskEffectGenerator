using System.Diagnostics;
using UskEffect.Graphics.Analysis;
using UskEffect.Graphics.Effects;
using Vortice.Direct3D11;
using Vortice.DXGI;

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

    // 読み込んだ画像。null ならノイズを表示する
    private ImageData? _image;
    private ID3D11Texture2D? _imageTexture;
    private ID3D11ShaderResourceView? _imageView;

    public volatile PreviewSettings Preview = new();
    public volatile EffectSettings Effect = new();

    /// <summary>中央のキャンバスの表示状態。UI スレッドから差し替える。</summary>
    public volatile CanvasView View = new();

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

    public void AttachSurface(IntPtr hwnd, int width, int height)
    {
        lock (_sync)
        {
            DetachSurfaceCore();
            _target = new SwapChainTarget(_gpu, hwnd, width, height);
        }
    }

    public void DetachSurface()
    {
        lock (_sync)
            DetachSurfaceCore();
    }

    private void DetachSurfaceCore()
    {
        if (_target is null)
            return;
        _gpu.Context.ClearState();
        _gpu.Context.Flush();
        _target.Dispose();
        _target = null;
    }

    public void ReloadShaders() => _shadersDirty = true;

    /// <summary>表示する画像を差し替える。null でノイズに戻す。</summary>
    public void SetImageSource(ImageData? image)
    {
        lock (_sync)
        {
            if (ReferenceEquals(image, _image))
                return;

            _gpu.Context.PSSetShaderResource(0, null!);
            _imageView?.Dispose();
            _imageTexture?.Dispose();
            _imageView = null;
            _imageTexture = null;
            _image = image;

            if (image is not null)
            {
                var pinned = System.Runtime.InteropServices.GCHandle.Alloc(image.Rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    _imageTexture = _gpu.Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)image.Width,
                        Height = (uint)image.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.R8G8B8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Immutable,
                        BindFlags = BindFlags.ShaderResource,
                    }, new SubresourceData(pinned.AddrOfPinnedObject(), (uint)(image.Width * 4)));
                }
                finally
                {
                    pinned.Free();
                }
                _imageView = _gpu.Device.CreateShaderResourceView(_imageTexture);
            }

            // 継ぎ目の解析をやり直す
            _analysisPending = true;
            _lastNoiseGenerated = _clock.Elapsed;
        }
    }

    /// <summary>1 フレーム描く。描画先がなければ何もせず false を返す。</summary>
    public bool RenderFrame(int width, int height, out RenderStats stats)
    {
        lock (_sync)
            return RenderFrameCore(width, height, out stats);
    }

    private bool RenderFrameCore(int width, int height, out RenderStats stats)
    {
        if (_shadersDirty)
        {
            _shadersDirty = false;
            _noiseErrors = _noise.LoadShader(_noiseShaderPath);
            _previewErrors = _preview.LoadShader(_previewShaderPath);
            _effectErrors = _effect.LoadShader(_effectShaderPath);
            _noiseDirty = true;
        }

        bool rendered = _target is not null && width > 0 && height > 0;
        if (rendered)
        {
            _target!.Resize(width, height);
            _timer.BeginFrame();
            // 画像を表示している間はノイズを作らない。ノイズに戻したときに作り直す
            if (_noiseDirty && _image is null)
            {
                _noise.Generate(_noiseSettings);
                _timer.MarkComputeDone();
                _noiseDirty = false;
                _analysisPending = true;
                _lastNoiseGenerated = _clock.Elapsed;
            }

            bool isColor = _image is not null;
            var sourceView = _imageView ?? _noise.ShaderResourceView;
            int sourceWidth = _image?.Width ?? _noise.Width;
            int sourceHeight = _image?.Height ?? _noise.Height;

            var now = _clock.Elapsed;
            Timeline.Advance((now - _lastFrameTime).TotalSeconds);
            _lastFrameTime = now;

            var preview = Preview;
            var effect = Effect;
            var frame = EffectEvaluator.Evaluate(effect, preview, Timeline.Time, Timeline.NormalizedTime);
            float time = (float)now.TotalSeconds;

            if (preview.Mode == PreviewMode.Effect)
            {
                _effect.Render(_target.RenderTargetView, _target.Width, _target.Height, sourceView,
                    effect, preview.Polar, frame, isColor);
            }
            else
            {
                _preview.Render(_target.RenderTargetView, _target.Width, _target.Height, sourceView,
                    sourceWidth, sourceHeight, isColor, preview, time, _seamAnalysis, MainContentMargin, View);
            }
            _timer.EndFrame();
            _target.Present(VSync);
            StartSeamAnalysisIfIdle();
        }

        var errorList = new[] { _noiseErrors, _previewErrors, _effectErrors }.Where(e => e is not null).ToList();
        string? errors = errorList.Count == 0 ? null : string.Join(Environment.NewLine, errorList);
        stats = new RenderStats(_gpu.AdapterName, _image?.Width ?? _noise.Width, _image?.Height ?? _noise.Height,
            _target?.Width ?? 0, _target?.Height ?? 0,
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
        if (_image is { } image)
        {
            _analysisTask = Task.Run(() => _seamAnalysis = SeamAnalyzer.Analyze(image.ToLuminance(), image.Width, image.Height));
            return;
        }

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
            DetachSurfaceCore();
            _imageView?.Dispose();
            _imageTexture?.Dispose();
            _timer.Dispose();
            _effect.Dispose();
            _preview.Dispose();
            _noise.Dispose();
            _gpu.Dispose();
        }
    }
}
