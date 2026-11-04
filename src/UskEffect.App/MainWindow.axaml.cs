using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using UskEffect.App.Controls;
using UskEffect.App.Controls.Ui;
using UskEffect.App.Locale;
using UskEffect.Graphics;

namespace UskEffect.App;

public partial class MainWindow : Window
{
    // 右パネルを狭くする窓の幅
    private const double NarrowWindowWidth = 1000;

    private readonly RenderEngine? _engine;
    private readonly CanvasNavigator? _navigator;
    private Thread? _renderThread;
    private RenderStats _lastStats;
    private int _shownRecoveries;

    // 解像度を持つ。ノイズの中身は各レイヤーが持つ
    private NoiseSettings _canvasNoise = new();
    private int _shownNoiseTarget = -1;
    private IReadOnlyList<UskEffect.Graphics.Painting.LayerThumbnail>? _shownThumbnails;

    // レイヤーは左のバーのアイコンの隣に開く
    internal UskEffect.App.Views.LayerPanel LayerTab => Rail.LayerPopup;
    private bool _suppressApply;
    private volatile bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _engine = new RenderEngine(LocateShaderDirectory());
        }
        catch (Exception ex)
        {
            Status.ShowFailure(Loc.F("S.Status.GpuFailed", ex.Message));
            RootPanel.Opacity = 1;
            return;
        }

        Viewport.HwndCreated += hwnd =>
        {
            var (w, h) = Viewport.PixelSize;
            _engine.AttachSurface(hwnd, w, h);
        };
        Viewport.HwndDestroying += () => _engine.DetachSurface();

        // キャンバス (子 HWND) のクリックは Avalonia を通らないので、ここで数値入力を終える
        Viewport.HostPointerDown += (_, _, _) => TextEditDismiss.EndActive();

        NoiseTab.Changed += OnNoiseTabChanged;
        NoiseTab.TextureChanged += OnLayerTextureChanged;
        Strip.Changed += ApplySettings;

        Top.ViewRectMenuItem.Click += (_, _) => Strip.Mode = PreviewMode.Rect;
        Top.ZoomFitMenuItem.Click += (_, _) => _engine.View = new CanvasView { RotationDegrees = _engine.View.RotationDegrees };
        Top.Zoom100MenuItem.Click += (_, _) => ZoomToActualSize();
        Top.ResetRotationMenuItem.Click += (_, _) => _engine.View = _engine.View with { RotationDegrees = 0 };

        _navigator = new CanvasNavigator(Viewport, () => _engine.View, view => _engine.View = view);

        // キャンバスの上で Space を押している間は、フォーカス中のボタンを押さない (Space + ドラッグが移動のため)
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Space && Viewport.IsPointerOverHost)
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Top.ViewPolarMenuItem.Click += (_, _) => Strip.Mode = PreviewMode.Polar;
        Top.ViewTileMenuItem.Click += (_, _) => Strip.Mode = PreviewMode.Tile4x4;

        Status.ReloadButton.Click += (_, _) => _engine.ReloadShaders();
        ToolTip.SetTip(Status.ReloadButton, Loc.F("S.Status.ShaderFolder", _engine.ShaderDirectory));
        Status.VSyncChanged += vsync => _engine.VSync = vsync;

        SizeChanged += (_, e) => RightPanel.Width = ResourceDouble(
            e.NewSize.Width < NarrowWindowWidth ? "Ue.Size.RightPanelNarrow" : "Ue.Size.RightPanel");

        ApplySliderLimits();
        ApplySettings();
        SetUpProjectCommands();
        SetUpPaint();
        Opened += (_, _) =>
        {
            RootPanel.Opacity = 1;
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "uskEffect Render" };
            _renderThread.Start();
            LoadTypeSamples();
            // 新規で始めるときは、まず種類を選ぶ
            Dispatcher.UIThread.Post(ShowStartTypeMenu, DispatcherPriority.Loaded);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            SaveSliderLimits();
            _renderThread?.Join();
            _engine.Dispose();
        };
    }

    // 広げた最大値の引き継ぎ。吹き出しの中の欄は対象にしない
    private IEnumerable<SliderField> ExpandableFields() =>
        this.GetLogicalDescendants().OfType<SliderField>().Where(f => f.IsExpandable);

    // Vector2 の X・Y は名前を持たないので、親の行の名前から作る
    private static string LimitKey(SliderField field) => field.Parent?.Parent is Vector2Row row
        ? $"{row.Name}.{(ReferenceEquals(row.X, field) ? "X" : "Y")}"
        : field.Name ?? "";

    private void ApplySliderLimits()
    {
        var limits = new SliderLimitStore().Load();
        foreach (var field in ExpandableFields())
        {
            if (limits.TryGetValue(LimitKey(field), out double stored))
                field.ApplyStoredMaximum(stored);
        }
    }

    private void SaveSliderLimits()
    {
        var limits = new Dictionary<string, double>();
        foreach (var field in ExpandableFields())
        {
            // 次回の最大値は終了時の値。既定の最大値以下なら記録しない
            if (field.Value > field.DefaultMaximum)
                limits[LimitKey(field)] = field.Value;
        }
        new SliderLimitStore().Save(limits);
    }

    private static double ResourceDouble(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is double d ? d : 0;

    private void ApplySettings()
    {
        if (_engine is null || _suppressApply)
            return;

        var mode = Strip.Mode;
        _canvasNoise = _canvasNoise with { Resolution = NoiseTab.Resolution };
        _engine.Noise = _canvasNoise;
        _engine.CanvasSize = NoiseTab.CustomSize is { } size ? new CanvasPixelSize(size.Width, size.Height) : null;
        _engine.Output = Strip.Output;
        _engine.Preview = ReadPreviewSettings();

        Strip.ShowPolarSettings = mode == PreviewMode.Polar;

        MarkDirty();
    }

    private PreviewSettings ReadPreviewSettings()
    {
        return new PreviewSettings
        {
            Mode = Strip.Mode,
            Polar = Strip.Polar,
        };
    }

    // サンプルは見本なので、描けなくても操作は止めない
    private void LoadTypeSamples()
    {
        var engine = _engine!;
        var settings = NoiseTab.SampleSettings();
        Task.Run(() =>
        {
            try
            {
                var samples = engine.RenderSamples(settings);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_closed)
                        NoiseTab.SetSamples(samples);
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debug.WriteLine(ex);
            }
        });
    }

    // Avalonia の RequestAnimationFrame では 60Hz 環境でも 40fps 程度しか回らなかったため、
    // 描画は専用スレッドで回し Present の VSync 待ちでペースを取る。UI スレッドには統計だけ渡す。
    private void RenderLoop()
    {
        var engine = _engine!;
        var fpsClock = Stopwatch.StartNew();
        int frames = 0;
        bool lostShown = false;

        while (!_closed)
        {
            var (width, height) = Viewport.PixelSize;

            RenderStats stats;
            try
            {
                if (!engine.RenderFrame(width, height, out stats))
                {
                    if (engine.IsDeviceLost && !lostShown)
                    {
                        lostShown = true;
                        Dispatcher.UIThread.Post(() => Status.ShowMessage(Loc.T("S.Status.DeviceLost")));
                    }
                    Thread.Sleep(16); // 描画先がない (最小化・HWND 作成前)、または GPU の復帰待ち
                    continue;
                }
                lostShown = false;
            }
            catch (Exception ex)
            {
                // デバイスロストはエンジンが復帰する。ここに来るのはそれ以外の失敗
                Dispatcher.UIThread.Post(() => Status.ShowFailure(Loc.F("S.Status.RenderFailed", ex.Message)));
                return;
            }

            if (!engine.VSync)
                Thread.Sleep(1);

            frames++;
            double elapsed = fpsClock.Elapsed.TotalSeconds;
            if (elapsed >= 0.25)
            {
                double fps = frames / elapsed;
                frames = 0;
                fpsClock.Restart();
                Dispatcher.UIThread.Post(() => ShowStats(stats, fps));
            }
        }
    }

    private void ShowStats(RenderStats stats, double fps)
    {
        if (_closed)
            return;

        if (stats.LayerThumbnails is { } thumbnails && !ReferenceEquals(thumbnails, _shownThumbnails))
        {
            _shownThumbnails = thumbnails;
            LayerTab.ShowThumbnails(thumbnails);
            ShowCurrentLayerOnRail();
        }

        if (stats.DeviceRecoveries != _shownRecoveries)
        {
            _shownRecoveries = stats.DeviceRecoveries;
            RefreshLayers();
            Status.ShowMessage(Loc.T("S.Status.DeviceRecovered"));
        }

        Status.ShowStats(stats, fps);
        RefreshUndoButtons();
        _lastStats = stats;
        Strip.SetZoom(ZoomRatio(stats), _engine?.View.RotationDegrees ?? 0);
    }


    // Shaders/Preview.hlsl の全体表示の倍率と同じ計算 (余白は RenderEngine.MainContentMargin の両側)
    private static double FitScale(RenderStats stats)
    {
        const double margin = 24;
        return Math.Min((stats.ViewportWidth - margin) / stats.TextureWidth, (stats.ViewportHeight - margin) / stats.TextureHeight);
    }

    private double? ZoomRatio(RenderStats stats)
    {
        if (stats.TextureWidth <= 0)
            return null;
        return FitScale(stats) * (_engine?.View.Zoom ?? 1);
    }

    private void ZoomToActualSize()
    {
        if (_engine is null || _lastStats.TextureWidth <= 0)
            return;
        _engine.View = _engine.View with { Zoom = Math.Clamp(1 / FitScale(_lastStats), CanvasView.MinZoom, CanvasView.MaxZoom) };
    }

    // 開発中はリポジトリ直下の Shaders/ を直接監視してホットリロードする
    internal static string LocateShaderDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UskEffect.slnx")) && Directory.Exists(Path.Combine(dir.FullName, "Shaders")))
                return Path.Combine(dir.FullName, "Shaders");
        }
        return Path.Combine(AppContext.BaseDirectory, "Shaders");
    }
}
