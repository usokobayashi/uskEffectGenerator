using System.Collections.Concurrent;
using System.Diagnostics;
using UskEffect.Graphics.Analysis;
using UskEffect.Graphics.Effects;
using UskEffect.Graphics.Painting;
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
    bool SeamAnalysisPending,
    int DeviceRecoveries = 0);

/// <summary>ブラシの大きさを示す輪の位置。画面の画素と、テクスチャの画素での直径。</summary>
public sealed record PaintCursor(int X, int Y, double Diameter);

/// <summary>
/// 描画エンジン。ノイズ生成 → プレビュー描画 → Present をレンダースレッドで回す。
/// ノイズを作り直したら、操作が落ち着いてから GPU から読み戻して継ぎ目を解析する (FR-04)。
/// D3D11 の即時コンテキストはスレッドセーフでないため、D3D を触る公開メソッドは _sync で直列化する。
/// 設定はイミュータブルな record の差し替えで受け取るので、UI スレッドからロックなしで設定できる。
/// HLSL ファイルの変更を監視し、次フレームで再コンパイルする。
/// GPU を失ったら (デバイスロスト) 作り直す。GPU 上のレイヤーは読めなくなるので、控えを CPU 側に持って戻す。
/// </summary>
public sealed class RenderEngine : IDisposable
{
    private GpuDevice _gpu;
    private NoiseGenerator _noise;
    private PreviewRenderer _preview;
    private EffectRenderer _effect;
    private PaintRenderer _paint;
    private GpuTimer _timer;
    private readonly FileSystemWatcher? _watcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _noiseShaderPath;
    private readonly string _previewShaderPath;
    private readonly string _effectShaderPath;
    private readonly string _paintShaderPath;
    private TimeSpan _lastFrameTime;

    // 中央のキャンバスの周りの余白 [px]
    private const float MainContentMargin = 12f;

    // UI スレッドの入力を止めないよう、ペイントの操作は溜めておいてレンダースレッドで処理する
    private readonly ConcurrentQueue<Action<ID3D11ShaderResourceView, bool>> _paintCommands = new();

    private readonly object _sync = new();

    private SwapChainTarget? _target;
    private IntPtr _surfaceHwnd;
    private int _surfaceWidth, _surfaceHeight;

    // デバイスロストからの復帰。作り直せない間 (GPU のリセット中) は少し待ってやり直す
    private volatile bool _deviceLost;
    private bool _deviceObjectsReleased;
    private TimeSpan _nextRecovery;
    private int _deviceRecoveries;

    // 復帰に使うレイヤーの控え。GPU が読めなくなった後の保存にも使う
    private IReadOnlyList<LayerData>? _layerBackup;
    private bool _backupPending;

    /// <summary>GPU を失って、まだ復帰していないか。</summary>
    public bool IsDeviceLost => _deviceLost;

    internal TimeSpan RecoveryRetryInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>テスト用。デバイスの作り直しを差し替える。</summary>
    internal Func<GpuDevice>? DeviceFactory { get; set; }

    private volatile NoiseSettings _noiseSettings = new();
    private volatile bool _noiseDirty = true;
    private volatile bool _shadersDirty = true;
    private string? _noiseErrors;
    private string? _previewErrors;
    private string? _effectErrors;
    private string? _paintErrors;

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

    /// <summary>ブラシの大きさを示す輪。null で出さない。エフェクトの表示では描かない。</summary>
    public volatile PaintCursor? Cursor;

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
        _paintShaderPath = Path.Combine(shaderDirectory, "Paint", "Paint.hlsl");

        _gpu = CreateDevice();
        _noise = new NoiseGenerator(_gpu);
        _preview = new PreviewRenderer(_gpu);
        _effect = new EffectRenderer(_gpu);
        _paint = new PaintRenderer(_gpu);
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

    private GpuDevice CreateDevice()
    {
        if (DeviceFactory is { } factory)
            return factory();
#if DEBUG
        return new GpuDevice(enableDebugLayer: true);
#else
        return new GpuDevice();
#endif
    }

    public void AttachSurface(IntPtr hwnd, int width, int height)
    {
        lock (_sync)
        {
            _surfaceHwnd = hwnd;
            _surfaceWidth = width;
            _surfaceHeight = height;
            // GPU を失っている間は覚えておくだけ。復帰したときに作る
            if (!EnsureDevice())
                return;
            DetachSurfaceCore();
            _target = new SwapChainTarget(_gpu, hwnd, width, height);
        }
    }

    public void DetachSurface()
    {
        lock (_sync)
        {
            _surfaceHwnd = IntPtr.Zero;
            DetachSurfaceCore();
        }
    }

    // GPU が使えるか確かめ、失っていたら作り直す。作り直せなければ false
    private bool EnsureDevice()
    {
        if (IsDeviceRemoved())
            _deviceLost = true;
        return !_deviceLost || TryRecoverDevice();
    }

    private bool IsDeviceRemoved() => !_deviceLost && _gpu.Device.DeviceRemovedReason.Failure;

    /// <summary>テスト用。実際の GPU のリセットは起こせないので、失った状態だけを作る。</summary>
    internal void SimulateDeviceLoss()
    {
        lock (_sync)
            _deviceLost = true;
    }

    private bool TryRecoverDevice()
    {
        if (_clock.Elapsed < _nextRecovery)
            return false;
        _nextRecovery = _clock.Elapsed + RecoveryRetryInterval;

        // フリップモデルは古いスワップチェーンを消してからでないと同じ窓に作れない
        ReleaseDeviceObjects();

        var created = new List<IDisposable>();
        T Track<T>(T item) where T : IDisposable
        {
            created.Add(item);
            return item;
        }

        try
        {
            var gpu = Track(CreateDevice());
            var noise = Track(new NoiseGenerator(gpu));
            var preview = Track(new PreviewRenderer(gpu));
            var effect = Track(new EffectRenderer(gpu));
            var paint = Track(new PaintRenderer(gpu));
            var timer = Track(new GpuTimer(gpu));
            paint.SetLayers(_layerBackup);

            var image = _image is { } source ? CreateImageTexture(gpu, source) : default;
            if (image.Texture is not null)
            {
                Track(image.Texture);
                Track(image.View!);
            }
            var target = _surfaceHwnd != IntPtr.Zero ? Track(new SwapChainTarget(gpu, _surfaceHwnd, _surfaceWidth, _surfaceHeight)) : null;

            (_gpu, _noise, _preview, _effect, _paint, _timer) = (gpu, noise, preview, effect, paint, timer);
            (_imageTexture, _imageView) = image;
            _target = target;
        }
        catch (Exception)
        {
            created.Reverse();
            created.ForEach(DisposeQuietly);
            return false;
        }

        _deviceObjectsReleased = false;
        _deviceLost = false;
        _deviceRecoveries++;
        _shadersDirty = true;
        _noiseDirty = true;
        _analysisPending = true;
        _lastNoiseGenerated = _clock.Elapsed;
        return true;
    }

    // 失ったデバイスの後始末で例外が出ても、復帰は止めない
    private static void DisposeQuietly(IDisposable item)
    {
        try
        {
            item.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void ReleaseDeviceObjects()
    {
        if (_deviceObjectsReleased)
            return;
        _deviceObjectsReleased = true;

        try
        {
            DetachSurfaceCore();
        }
        catch (Exception)
        {
            _target = null;
        }
        foreach (var item in new IDisposable?[] { _imageView, _imageTexture, _timer, _paint, _effect, _preview, _noise, _gpu })
        {
            if (item is not null)
                DisposeQuietly(item);
        }
        _imageView = null;
        _imageTexture = null;
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

            // GPU を失っている間は覚えておくだけ。復帰したときに作る
            if (!EnsureDevice())
            {
                _image = image;
                return;
            }

            _gpu.Context.PSSetShaderResource(0, null!);
            _imageView?.Dispose();
            _imageTexture?.Dispose();
            _imageView = null;
            _imageTexture = null;
            _image = image;

            if (image is not null)
                (_imageTexture, _imageView) = CreateImageTexture(_gpu, image);

            // 継ぎ目の解析をやり直す
            _paint.MarkAllDirty();
            _analysisPending = true;
            _lastNoiseGenerated = _clock.Elapsed;
        }
    }

    private static (ID3D11Texture2D? Texture, ID3D11ShaderResourceView? View) CreateImageTexture(GpuDevice gpu, ImageData image)
    {
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(image.Rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var texture = gpu.Device.CreateTexture2D(new Texture2DDescription
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
            return (texture, gpu.Device.CreateShaderResourceView(texture));
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>スタンプを置く。UI スレッドから呼んでよい。次のフレームで処理する。</summary>
    public void Paint(BrushSettings brush, IReadOnlyList<PaintDab> dabs, PolarStamp? polar = null)
    {
        if (dabs.Count > 0)
            _paintCommands.Enqueue((source, isColor) => _paint.Stamp(brush, dabs, polar, source, isColor));
    }

    /// <summary>継ぎ目の自動ブレンドのプレビューを作り直す (FR-13)。UI スレッドから呼んでよい。</summary>
    public void PreviewSeamBlend(SeamBlendSettings settings) =>
        _paintCommands.Enqueue((source, isColor) => _paint.PreviewSeamBlend(settings, source, isColor));

    /// <summary>プレビューを確定する。UI スレッドから呼んでよい。</summary>
    public void CommitSeamBlend() => _paintCommands.Enqueue((_, _) => _paint.CommitSeamBlend());

    /// <summary>プレビューを取り消す。UI スレッドから呼んでよい。</summary>
    public void CancelSeamBlend() => _paintCommands.Enqueue((_, _) => _paint.CancelSeamBlend());

    /// <summary>ストロークの終わり。ここまでを 1 回の「元に戻す」にする。UI スレッドから呼んでよい。</summary>
    public void EndStroke() => _paintCommands.Enqueue((_, _) => _paint.CommitEdit());

    /// <summary>選択中のレイヤーを消す。元に戻せる。UI スレッドから呼んでよい。</summary>
    public void ClearActiveLayer() => _paintCommands.Enqueue((_, _) => _paint.ClearActiveLayer());

    /// <summary>レイヤーの一覧 (先頭が一番下)。</summary>
    public IReadOnlyList<LayerState> Layers => _paint.Layers;

    public int ActiveLayerId => _paint.ActiveLayerId;
    public bool CanUndo => _paint.CanUndo || !_paintCommands.IsEmpty;
    public bool CanRedo => _paint.CanRedo;

    public void AddLayer() => WithPaint(_paint.AddLayer);
    public void RemoveLayer(int id) => WithPaint(() => _paint.RemoveLayer(id));
    public void MoveLayer(int id, int delta) => WithPaint(() => _paint.MoveLayer(id, delta));
    public void SetLayerVisible(int id, bool visible) => WithPaint(() => _paint.SetLayerVisible(id, visible));
    public void SetLayerOpacity(int id, float opacity) => WithPaint(() => _paint.SetLayerOpacity(id, opacity));
    public void SetActiveLayer(int id) => WithPaint(() => _paint.SetActiveLayer(id));
    public void Undo() => WithPaint(() => _paint.Undo());
    public void Redo() => WithPaint(() => _paint.Redo());

    // レイヤーと履歴の操作は、溜まったストロークを済ませてからすぐ行う。結果を UI がすぐ読めるようにするため
    private void WithPaint(Action action)
    {
        lock (_sync)
        {
            if (!EnsureDevice())
                return;
            EnsureSourceReady();
            action();
            _analysisPending = true;
            _backupPending = true;
            _lastNoiseGenerated = _clock.Elapsed;
        }
    }

    /// <summary>保存されていたレイヤーに置き換える。null で最初の状態へ戻す。履歴も消える。</summary>
    public void SetLayers(IReadOnlyList<LayerData>? layers)
    {
        lock (_sync)
        {
            while (_paintCommands.TryDequeue(out _)) { }
            _layerBackup = layers;
            _backupPending = false;
            if (!EnsureDevice())
                return;
            // 描いていないプロジェクトを開くだけで GPU に触らない
            if (layers is null && _paint.IsPristine)
                return;
            _paint.SetLayers(layers);
        }
    }

    /// <summary>元の画像に描いた内容を重ねた結果を読み戻す。何も描いていなければ null。</summary>
    public ImageData? ReadComposite()
    {
        lock (_sync)
        {
            RequireDevice();
            EnsureSourceReady();
            return _paint.HasPaint ? _paint.ReadComposite() : null;
        }
    }

    // 書き出しは GPU がないと作れない。黙って描いた内容のない画像を出さないよう止める
    private void RequireDevice()
    {
        if (!EnsureDevice())
            throw new InvalidOperationException("GPU が復帰していないため読み取れません。少し待ってからやり直してください。");
    }

    /// <summary>レイヤーを読み戻す (保存用)。何もしていなければ null。GPU を失っている間は最後の控えを返す。</summary>
    public IReadOnlyList<LayerData>? ReadLayers()
    {
        lock (_sync)
        {
            if (!EnsureDevice())
                return _layerBackup;
            // 何もしていないプロジェクトの保存では GPU に触らない
            if (_paint.IsPristine && _paintCommands.IsEmpty)
                return null;
            EnsureSourceReady();
            _paint.CommitEdit();
            return _paint.IsPristine ? null : _paint.ReadLayers();
        }
    }

    /// <summary>今の表示で、画面の画素とテクスチャの画素を対応付ける。エフェクトの表示では使わない。</summary>
    public ICanvasMapping? CreateCanvasMapping(int viewportWidth, int viewportHeight)
    {
        var image = _image;
        int width = image?.Width ?? _noise.Width;
        int height = image?.Height ?? _noise.Height;
        if (viewportWidth <= 0 || viewportHeight <= 0 || width <= 0 || height <= 0)
            return null;
        return BuildMapping(viewportWidth, viewportHeight, width, height, Preview, View, _clock.Elapsed.TotalSeconds);
    }

    private static ICanvasMapping BuildMapping(int viewportWidth, int viewportHeight, int width, int height,
        PreviewSettings preview, CanvasView view, double time)
    {
        double offsetX = preview.OffsetX + preview.ScrollSpeedU * time;
        double offsetY = preview.OffsetY + preview.ScrollSpeedV * time;
        return preview.Mode switch
        {
            PreviewMode.Polar => new PolarCanvasMapping(viewportWidth, viewportHeight, width, height, MainContentMargin, view,
                preview.Polar, preview.TilingX, preview.TilingY, offsetX, offsetY),

            // シームの表示は半タイルずらしただけで、Tiling もスクロールも効かない
            PreviewMode.SeamCheck => new CanvasMapping(viewportWidth, viewportHeight, width, height, MainContentMargin, view, 1, 1, 0.5, 0.5),

            _ => new CanvasMapping(viewportWidth, viewportHeight, width, height, MainContentMargin, view,
                preview.TilingX, preview.TilingY, offsetX, offsetY),
        };
    }

    private void LoadShaders()
    {
        _shadersDirty = false;
        _noiseErrors = _noise.LoadShader(_noiseShaderPath);
        _previewErrors = _preview.LoadShader(_previewShaderPath);
        _effectErrors = _effect.LoadShader(_effectShaderPath);
        _paintErrors = _paint.LoadShader(_paintShaderPath);
        _noiseDirty = true;
    }

    // シェーダーと元の画像を用意して、溜まったペイントの操作を処理する
    private void EnsureSourceReady()
    {
        if (_shadersDirty)
            LoadShaders();
        if (_image is null && (_noiseDirty || _noise.ShaderResourceView is null))
        {
            _noise.Generate(_noiseSettings);
            _noiseDirty = false;
            _paint.MarkAllDirty();
            _analysisPending = true;
            _lastNoiseGenerated = _clock.Elapsed;
        }
        SyncPaint();
    }

    private void SyncPaint()
    {
        var source = _imageView ?? _noise.ShaderResourceView;
        if (source is null || (!_paint.HasPaint && _paintCommands.IsEmpty))
            return;

        bool isColor = _image is not null;
        _paint.EnsureSize(_image?.Width ?? _noise.Width, _image?.Height ?? _noise.Height);

        bool changed = false;
        while (_paintCommands.TryDequeue(out var command))
        {
            command(source, isColor);
            // 保存されていた大きさのレイヤーを読み込んだときに、元の画像の大きさへ戻す
            _paint.EnsureSize(_image?.Width ?? _noise.Width, _image?.Height ?? _noise.Height);
            changed = true;
        }
        if (changed)
        {
            _analysisPending = true;
            _backupPending = true;
            _lastNoiseGenerated = _clock.Elapsed;
        }
        if (_paint.HasPaint)
            _paint.UpdateComposite(source, isColor);
    }

    /// <summary>1 フレーム描く。描画先がなければ何もせず false を返す。</summary>
    public bool RenderFrame(int width, int height, out RenderStats stats)
    {
        lock (_sync)
        {
            stats = default;
            if (!EnsureDevice())
                return false;
            try
            {
                return RenderFrameCore(width, height, out stats);
            }
            catch (Exception) when (IsDeviceRemoved())
            {
                _deviceLost = true;
                return false;
            }
        }
    }

    private bool RenderFrameCore(int width, int height, out RenderStats stats)
    {
        if (_shadersDirty)
            LoadShaders();

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
                _paint.MarkAllDirty();
                _analysisPending = true;
                _lastNoiseGenerated = _clock.Elapsed;
            }
            SyncPaint();

            // 描いた内容があれば、どの表示も合成結果を読む (要件 §3.4)
            bool painted = _paint.HasPaint && _paint.CompositeView is not null;
            bool imageIsColor = _image is not null;
            bool isColor = imageIsColor || painted;
            var sourceView = painted ? _paint.CompositeView : _imageView ?? _noise.ShaderResourceView;
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
                    effect, preview.Polar, frame, imageIsColor, sourceIsPaintedNoise: painted && !imageIsColor);
            }
            else
            {
                var view = View;
                _preview.Render(_target.RenderTargetView, _target.Width, _target.Height, sourceView,
                    sourceWidth, sourceHeight, isColor, preview, time, _seamAnalysis, MainContentMargin, view);

                if (Cursor is { } cursor && sourceWidth > 0 && sourceHeight > 0)
                {
                    var mapping = BuildMapping(_target.Width, _target.Height, sourceWidth, sourceHeight, preview, view, time);
                    _paint.DrawCursor(_target.RenderTargetView, _target.Width, _target.Height, cursor.X, cursor.Y,
                        (float)(cursor.Diameter / 2 * mapping.PixelScale));
                }
            }
            _timer.EndFrame();
            _target.Present(VSync);
            StartSeamAnalysisIfIdle();
        }

        var errorList = new[] { _noiseErrors, _previewErrors, _effectErrors, _paintErrors }.Where(e => e is not null).ToList();
        string? errors = errorList.Count == 0 ? null : string.Join(Environment.NewLine, errorList);
        stats = new RenderStats(_gpu.AdapterName, _image?.Width ?? _noise.Width, _image?.Height ?? _noise.Height,
            _target?.Width ?? 0, _target?.Height ?? 0,
            _timer.ComputeMilliseconds, _timer.FrameMilliseconds, errors,
            _seamAnalysis, _analysisPending || _analysisTask is { IsCompleted: false }, _deviceRecoveries);
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
            RequireDevice();
            if (_shadersDirty)
                LoadShaders();
            if (_noiseErrors is not null)
                throw new InvalidOperationException($"ノイズのシェーダーにエラーがあるため書き出せません。{Environment.NewLine}{_noiseErrors}");

            if (_noiseDirty || _noise.ShaderResourceView is null)
            {
                _noise.Generate(_noiseSettings);
                _noiseDirty = false;
                _paint.MarkAllDirty();
                _analysisPending = true;
                _lastNoiseGenerated = _clock.Elapsed;
            }
            return (_noise.ReadPixels(), _noise.Width, _noise.Height);
        }
    }

    // 操作が落ち着いたときに取る。自動ブレンドの確認中はまだ確定していないので取らない
    private void BackupLayers()
    {
        if (!_backupPending || _paint.IsPreviewingBlend)
            return;
        _backupPending = false;
        _layerBackup = _paint.IsPristine ? null : _paint.ReadLayers();
    }

    /// <summary>テスト用。溜まった操作を済ませて控えを取る。</summary>
    internal void BackupLayersNow()
    {
        lock (_sync)
        {
            EnsureSourceReady();
            BackupLayers();
        }
    }

    private void StartSeamAnalysisIfIdle()
    {
        if (!_analysisPending || _clock.Elapsed - _lastNoiseGenerated < AnalysisDelay)
            return;
        if (_analysisTask is { IsCompleted: false })
            return; // 前の解析が終わってから。終わった時点でまだ pending なら次フレームで再解析する

        _analysisPending = false;
        BackupLayers();
        if (_paint.HasPaint && _paint.ReadComposite() is { } painted)
        {
            _analysisTask = Task.Run(() => _seamAnalysis = SeamAnalyzer.Analyze(painted.ToLuminance(), painted.Width, painted.Height));
            return;
        }
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
            ReleaseDeviceObjects();
    }
}
