using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using UskEffect.App.Controls;
using UskEffect.App.Locale;
using UskEffect.Graphics;
using UskEffect.Graphics.Analysis;
using UskEffect.Graphics.Effects;

namespace UskEffect.App;

public partial class MainWindow : Window
{
    // 右パネルを狭くする窓の幅
    private const double NarrowWindowWidth = 1000;

    private readonly RenderEngine? _engine;
    private readonly D3DHost[] _hosts;
    private Thread? _renderThread;
    private bool _suppressApply;
    private volatile bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        // SurfaceSlot の順
        _hosts = [Viewport, TileThumb.Host, PolarThumb.Host, EffectThumb.Host];

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

        for (int i = 0; i < _hosts.Length; i++)
        {
            var slot = (SurfaceSlot)i;
            var host = _hosts[i];
            host.HwndCreated += hwnd =>
            {
                var (w, h) = host.PixelSize;
                _engine.AttachSurface(slot, hwnd, w, h);
            };
            host.HwndDestroying += () => _engine.DetachSurface(slot);
        }

        NoiseTab.Changed += ApplySettings;
        SeamTab.Changed += ApplySettings;
        EffectTab.Changed += ApplySettings;
        Strip.Changed += ApplySettings;
        Timeline.Attach(_engine.Timeline);
        Timeline.Changed += MarkDirty;

        TabSegment.SelectionChanged += (_, _) => ShowTab(TabSegment.SelectedIndex);
        TileThumb.Clicked += () => Strip.Mode = PreviewMode.Tile3x3;
        PolarThumb.Clicked += () => Strip.Mode = PreviewMode.Polar;
        EffectThumb.Clicked += () => Strip.Mode = PreviewMode.Effect;
        Top.ViewRectMenuItem.Click += (_, _) => Strip.Mode = PreviewMode.Rect;
        Top.ViewTileMenuItem.Click += (_, _) => Strip.Mode = PreviewMode.Tile3x3;
        Top.ViewPolarMenuItem.Click += (_, _) => Strip.Mode = PreviewMode.Polar;
        Top.ViewSeamMenuItem.Click += (_, _) => Strip.Mode = PreviewMode.SeamCheck;
        Top.ViewEffectMenuItem.Click += (_, _) => Strip.Mode = PreviewMode.Effect;

        Status.ReloadButton.Click += (_, _) => _engine.ReloadShaders();
        ToolTip.SetTip(Status.ReloadButton, Loc.F("S.Status.ShaderFolder", _engine.ShaderDirectory));
        Status.VSyncChanged += vsync => _engine.VSync = vsync;

        SizeChanged += (_, e) => RightPanel.Width = ResourceDouble(
            e.NewSize.Width < NarrowWindowWidth ? "Ue.Size.RightPanelNarrow" : "Ue.Size.RightPanel");

        ApplySettings();
        SetUpProjectCommands();
        Opened += (_, _) =>
        {
            RootPanel.Opacity = 1;
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "uskEffect Render" };
            _renderThread.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            Timeline.Stop();
            _renderThread?.Join();
            _engine.Dispose();
        };
    }

    private static double ResourceDouble(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is double d ? d : 0;

    private void ShowTab(int index)
    {
        NoiseTab.IsVisible = index == 0;
        SeamTab.IsVisible = index == 1;
        EffectTab.IsVisible = index == 2;
        LayerTab.IsVisible = index == 3;
    }

    private void ApplySettings()
    {
        if (_engine is null || _suppressApply)
            return;

        var mode = Strip.Mode;
        var effect = EffectTab.Read();
        _engine.Noise = NoiseTab.Read();
        _engine.Effect = effect;
        _engine.Preview = ReadPreviewSettings();

        // 極座標の設定はエフェクトを極座標で貼るときにも使う
        Strip.ShowPolarSettings = mode == PreviewMode.Polar
            || (mode == PreviewMode.Effect && effect.Mapping == EffectMapping.Polar);
        TileThumb.IsActive = mode == PreviewMode.Tile3x3;
        PolarThumb.IsActive = mode == PreviewMode.Polar;
        EffectThumb.IsActive = mode == PreviewMode.Effect;

        MarkDirty();
    }

    private PreviewSettings ReadPreviewSettings() => new()
    {
        Mode = Strip.Mode,
        ScrollSpeedU = EffectTab.ScrollU,
        ScrollSpeedV = EffectTab.ScrollV,
        Polar = Strip.Polar,
        Heatmap = SeamTab.Heatmap,
        HeatThreshold = SeamTab.Threshold,
    };

    // Avalonia の RequestAnimationFrame では 60Hz 環境でも 40fps 程度しか回らなかったため、
    // 描画は専用スレッドで回し Present の VSync 待ちでペースを取る。UI スレッドには統計だけ渡す。
    private void RenderLoop()
    {
        var engine = _engine!;
        var fpsClock = Stopwatch.StartNew();
        int frames = 0;
        var sizes = new (int Width, int Height)[_hosts.Length];

        while (!_closed)
        {
            for (int i = 0; i < _hosts.Length; i++)
                sizes[i] = _hosts[i].PixelSize;

            RenderStats stats;
            try
            {
                if (!engine.RenderFrame(sizes, out stats))
                {
                    Thread.Sleep(16); // 描画先がない (最小化・HWND 作成前)
                    continue;
                }
            }
            catch (Exception ex)
            {
                // デバイスロスト等。復帰処理は未実装
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

        Status.ShowStats(stats, fps);
        SeamTab.ShowAnalysis(stats.Seam, stats.SeamAnalysisPending);
        ShowSeamMark(stats.Seam);
        Strip.SetZoom(ZoomRatio(stats));
    }

    // シームのタブに判定を常に出す
    private void ShowSeamMark(SeamAnalysis? seam)
    {
        bool seamless = seam is { Horizontal.IsSeamless: true, Vertical.IsSeamless: true };
        SeamMarkText.Text = seam is null ? "" : seamless ? "✓" : "!";
        SeamMarkText.Classes.Set("ok", seam is not null && seamless);
        SeamMarkText.Classes.Set("bad", seam is not null && !seamless);
    }

    private double? ZoomRatio(RenderStats stats)
    {
        if (Strip.Mode == PreviewMode.Effect || stats.TextureWidth <= 0)
            return null;
        // Shaders/Preview.hlsl の内容の大きさと同じ計算 (余白は RenderEngine.MainContentMargin の両側)
        const double margin = 24;
        double tiles = Strip.Mode == PreviewMode.Tile3x3 ? 3 : 1;
        return Math.Min(stats.ViewportWidth - margin, stats.ViewportHeight - margin) / (stats.TextureWidth * tiles);
    }

    // 開発中はリポジトリ直下の Shaders/ を直接監視してホットリロードする
    private static string LocateShaderDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UskEffect.slnx")) && Directory.Exists(Path.Combine(dir.FullName, "Shaders")))
                return Path.Combine(dir.FullName, "Shaders");
        }
        return Path.Combine(AppContext.BaseDirectory, "Shaders");
    }
}
