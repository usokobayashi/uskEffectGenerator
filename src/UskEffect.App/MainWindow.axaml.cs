using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using UskEffect.App.Presets;
using UskEffect.Graphics;

namespace UskEffect.App;

public partial class MainWindow : Window
{
    private static readonly int[] Resolutions = [256, 512, 1024, 2048];

    private readonly RenderEngine? _engine;
    private readonly NoisePresetStore _presets = new();
    private Thread? _renderThread;
    private bool _suppressApply;
    private string? _shownErrors;
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
            StatusText.Text = $"GPU 初期化失敗: {ex.Message}";
            return;
        }

        ShaderDirText.Text = _engine.ShaderDirectory;

        Viewport.HwndCreated += hwnd =>
        {
            var (w, h) = Viewport.PixelSize;
            _engine.AttachWindow(hwnd, w, h);
        };
        Viewport.HwndDestroying += () => _engine.DetachWindow();

        foreach (var slider in new RangeBase[]
                 {
                     FrequencySlider, OctavesSlider, PersistenceSlider, LacunaritySlider, WarpSlider, ContrastSlider,
                     BrightnessSlider, InnerRadiusSlider, ScrollUSlider, ScrollVSlider,
                 })
            slider.ValueChanged += (_, _) => ApplySettings();
        foreach (var combo in new[] { NoiseTypeBox, WorleyModeBox, FractalBox, ResolutionBox, SeamBox, ViewModeBox })
            combo.SelectionChanged += (_, _) => ApplySettings();
        SeedBox.ValueChanged += (_, _) => ApplySettings();
        InvertBox.IsCheckedChanged += (_, _) => ApplySettings();
        VSyncBox.IsCheckedChanged += (_, _) => ApplySettings();
        ReloadButton.Click += (_, _) => _engine.ReloadShaders();

        PresetBox.SelectionChanged += (_, _) => OnPresetSelected();
        SavePresetButton.Click += (_, _) => SavePreset();
        DeletePresetButton.Click += (_, _) => DeletePreset();
        RefreshPresetList(null);

        ApplySettings();
        Opened += (_, _) =>
        {
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "uskEffect Render" };
            _renderThread.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _renderThread?.Join();
            _engine.Dispose();
        };
    }

    private void ApplySettings()
    {
        if (_engine is null || _suppressApply)
            return;

        int frequency = (int)FrequencySlider.Value;
        int octaves = (int)OctavesSlider.Value;
        FrequencyLabel.Text = $"Frequency: {frequency}";
        OctavesLabel.Text = $"Octaves: {octaves}";
        PersistenceLabel.Text = $"Persistence: {PersistenceSlider.Value:0.00}";
        InnerRadiusLabel.Text = $"極座標 内径: {InnerRadiusSlider.Value:0.00}";
        ScrollULabel.Text = $"UV スクロール U: {ScrollUSlider.Value:0.00} /s";
        ScrollVLabel.Text = $"UV スクロール V: {ScrollVSlider.Value:0.00} /s";
        LacunarityLabel.Text = $"Lacunarity: {(int)LacunaritySlider.Value}";
        WarpLabel.Text = $"ドメインワープ: {WarpSlider.Value:0.00}";
        ContrastLabel.Text = $"コントラスト: {ContrastSlider.Value:0.00}";
        BrightnessLabel.Text = $"明るさ: {BrightnessSlider.Value:+0.00;-0.00;0.00}";

        var noise = ReadNoiseSettings();
        WorleyModeBox.IsEnabled = noise.Type == NoiseType.Worley;
        _engine.Noise = noise;
        _engine.Preview = new PreviewSettings
        {
            Mode = (PreviewMode)Math.Max(0, ViewModeBox.SelectedIndex),
            InnerRadius = (float)InnerRadiusSlider.Value,
            ScrollSpeedU = (float)ScrollUSlider.Value,
            ScrollSpeedV = (float)ScrollVSlider.Value,
        };
        _engine.VSync = VSyncBox.IsChecked == true;
    }

    private NoiseSettings ReadNoiseSettings() => new()
    {
        Type = (NoiseType)Math.Max(0, NoiseTypeBox.SelectedIndex),
        Resolution = Resolutions[Math.Clamp(ResolutionBox.SelectedIndex, 0, Resolutions.Length - 1)],
        Seed = (uint)(SeedBox.Value ?? 0),
        Frequency = (int)FrequencySlider.Value,
        Octaves = (int)OctavesSlider.Value,
        Persistence = (float)PersistenceSlider.Value,
        Seam = (SeamMode)Math.Max(0, SeamBox.SelectedIndex),
        Lacunarity = (int)LacunaritySlider.Value,
        Fractal = (FractalMode)Math.Max(0, FractalBox.SelectedIndex),
        Worley = (WorleyMode)Math.Max(0, WorleyModeBox.SelectedIndex),
        WarpStrength = (float)WarpSlider.Value,
        Contrast = (float)ContrastSlider.Value,
        Brightness = (float)BrightnessSlider.Value,
        Invert = InvertBox.IsChecked == true,
    };

    // 解像度は出力設定なのでプリセットでは変えない
    private void WriteNoiseSettings(NoiseSettings s)
    {
        _suppressApply = true;
        try
        {
            NoiseTypeBox.SelectedIndex = (int)s.Type;
            SeedBox.Value = s.Seed;
            FrequencySlider.Value = s.Frequency;
            OctavesSlider.Value = s.Octaves;
            PersistenceSlider.Value = s.Persistence;
            SeamBox.SelectedIndex = (int)s.Seam;
            LacunaritySlider.Value = s.Lacunarity;
            FractalBox.SelectedIndex = (int)s.Fractal;
            WorleyModeBox.SelectedIndex = (int)s.Worley;
            WarpSlider.Value = s.WarpStrength;
            ContrastSlider.Value = s.Contrast;
            BrightnessSlider.Value = s.Brightness;
            InvertBox.IsChecked = s.Invert;
        }
        finally
        {
            _suppressApply = false;
        }
        ApplySettings();
    }

    private void RefreshPresetList(string? selectName)
    {
        var presets = _presets.All;
        _suppressApply = true;
        try
        {
            PresetBox.ItemsSource = presets.Select(p => p.DisplayName).ToList();
            PresetBox.SelectedIndex = selectName is null ? -1 : presets.ToList().FindIndex(p => p.Name == selectName);
        }
        finally
        {
            _suppressApply = false;
        }
        UpdatePresetButtons();
    }

    private NoisePreset? SelectedPreset =>
        PresetBox.SelectedIndex is int i && i >= 0 && i < _presets.All.Count ? _presets.All[i] : null;

    private void OnPresetSelected()
    {
        UpdatePresetButtons();
        if (_suppressApply || SelectedPreset is not { } preset)
            return;
        PresetNameBox.Text = preset.IsBuiltIn ? "" : preset.Name;
        WriteNoiseSettings(preset.Settings);
    }

    private void ShowPresetMessage(string message)
    {
        PresetMessage.Text = message;
        PresetMessage.IsVisible = true;
    }

    private void UpdatePresetButtons() => DeletePresetButton.IsEnabled = SelectedPreset is { IsBuiltIn: false };

    private void SavePreset()
    {
        string name = PresetNameBox.Text ?? "";
        try
        {
            _presets.Save(name, ReadNoiseSettings());
            RefreshPresetList(name.Trim());
            ShowPresetMessage($"「{name.Trim()}」を保存しました。");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowPresetMessage($"保存できません: {ex.Message}");
        }
    }

    private void DeletePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset)
            return;
        try
        {
            _presets.Delete(preset.Name);
            RefreshPresetList(null);
            PresetNameBox.Text = "";
            ShowPresetMessage($"「{preset.Name}」を削除しました。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowPresetMessage($"削除できません: {ex.Message}");
        }
    }

    // Avalonia の RequestAnimationFrame では 60Hz 環境でも 40fps 程度しか回らなかったため、
    // 描画は専用スレッドで回し Present の VSync 待ちでペースを取る。UI スレッドには統計だけ渡す。
    private void RenderLoop()
    {
        var engine = _engine!;
        var fpsClock = Stopwatch.StartNew();
        int frames = 0;

        while (!_closed)
        {
            var (w, h) = Viewport.PixelSize;
            RenderStats stats;
            try
            {
                if (!engine.RenderFrame(w, h, out stats))
                {
                    Thread.Sleep(16); // 描画先がない (最小化・HWND 作成前)
                    continue;
                }
            }
            catch (Exception ex)
            {
                // デバイスロスト等。Phase 1 では復帰処理は行わない
                Dispatcher.UIThread.Post(() => StatusText.Text = $"描画エラー: {ex.Message}");
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

        if (stats.ShaderErrors != _shownErrors)
        {
            _shownErrors = stats.ShaderErrors;
            ErrorText.Text = _shownErrors;
            ErrorPanel.IsVisible = _shownErrors is not null;
        }

        StatusText.Text =
            $"{stats.AdapterName}  |  Texture {stats.TextureWidth}×{stats.TextureHeight}  |  " +
            $"Viewport {stats.ViewportWidth}×{stats.ViewportHeight}  |  " +
            $"Noise GPU {stats.ComputeMilliseconds:0.000} ms  |  Frame GPU {stats.FrameMilliseconds:0.000} ms  |  {fps:0.0} fps";
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
