using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UskEffect.App;
using UskEffect.App.Controls.Ui;
using UskEffect.Core.Export;
using UskEffect.Core.Project;
using UskEffect.Graphics;
using UskEffect.Graphics.Effects;

namespace UskEffect.Tests;

/// <summary>画面を使わずに本物の App とウィンドウを動かす。利用者の画面とマウスに触らない。</summary>
public sealed class UiSession : IDisposable
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<UskEffect.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(typeof(UiSession));

    public Task Run(Action action) => _session.Dispatch(action, CancellationToken.None);

    // セッションの破棄を待つとテストのプロセスが終わらなくなるので破棄しない (プロセスの終了で片付く)
    public void Dispose() { }
}

public sealed class UiTests(UiSession ui) : IClassFixture<UiSession>
{
    private static MainWindow Open(double width = 1280, double height = 800)
    {
        var window = new MainWindow { WindowState = WindowState.Normal, Width = width, Height = height };
        window.Show();
        Settle();
        return window;
    }

    private static void CloseWindow(Window window)
    {
        if (window is MainWindow main)
            main.CloseWithoutPrompt();
        else
            window.Close();
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();
    }

    private static string CapturePath(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "uskfx-ui");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".png");
    }

    private static void Capture(Window window, string name)
    {
        Settle();
        window.CaptureRenderedFrame()?.Save(CapturePath(name));
    }

    /// <summary>見えている文字のうち、自分の領域やウィンドウからはみ出しているものを返す。</summary>
    private static List<string> FindCutText(Window window)
    {
        var problems = new List<string>();
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible || string.IsNullOrEmpty(text.Text) || text.TextTrimming != Avalonia.Media.TextTrimming.None)
                continue;

            // 親の制約に縮められていない本来の文字幅で比べる
            double needed = NaturalWidth(text);
            if (needed > text.Bounds.Width + 0.5 && text.TextWrapping == Avalonia.Media.TextWrapping.NoWrap)
                problems.Add($"切れている: 「{text.Text}」 必要 {needed:0} / 実際 {text.Bounds.Width:0}");

            var box = text.TransformToVisual(window) is { } m ? new Rect(text.Bounds.Size).TransformToAABB(m) : default;
            if (box.Right > window.Bounds.Width + 0.5 || box.Bottom > window.Bounds.Height + 0.5)
            {
                // スクロール領域の中はスクロールすれば見えるので除く
                if (text.FindAncestorOfType<ScrollViewer>() is null)
                    problems.Add($"窓からはみ出している: 「{text.Text}」 {box}");
            }
        }
        return problems;
    }

    private static double NaturalWidth(TextBlock text)
    {
        var probe = new TextBlock
        {
            // アクセスキーの印 "_" は画面に出ない
            Text = text is AccessText ? text.Text?.Replace("_", "") : text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
        };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    /// <summary>数値欄の入力部に、値の文字が収まる幅があること。</summary>
    private static List<string> FindCrushedNumberFields(Window window)
    {
        var problems = new List<string>();
        foreach (var field in window.GetVisualDescendants().OfType<NumberField>())
        {
            if (!field.IsEffectivelyVisible)
                continue;
            var text = field.GetVisualDescendants().OfType<TextBox>().Single();
            var probe = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize };
            probe.Measure(Size.Infinity);
            if (text.Bounds.Width < probe.DesiredSize.Width)
                problems.Add($"数値欄がつぶれている: 入力部 {text.Bounds.Width:0} / 必要 {probe.DesiredSize.Width:0}");
        }
        return problems;
    }

    /// <summary>調整欄の項目名と値が重なっていないこと。</summary>
    private static List<string> FindOverlappingFields(Window window)
    {
        var problems = new List<string>();
        foreach (var field in window.GetVisualDescendants().OfType<SliderField>())
        {
            if (!field.IsEffectivelyVisible || field.Bounds.Width <= 0)
                continue;
            var texts = field.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).ToList();
            double used = texts.Sum(NaturalWidth) + 16;
            if (used > field.Bounds.Width + 0.5)
                problems.Add($"「{field.Label}」 必要 {used:0} / 欄 {field.Bounds.Width:0}");
        }
        return problems;
    }

    private static void AssertLayoutIsClean(Window window, string context)
    {
        var problems = FindCutText(window).Concat(FindOverlappingFields(window)).Concat(FindCrushedNumberFields(window)).ToList();
        Assert.True(problems.Count == 0, $"{context}:\n{string.Join("\n", problems)}");
    }

    private static void ShowEveryTab(MainWindow window, Action<int> check)
    {
        for (int tab = 0; tab < 4; tab++)
        {
            window.TabSegment.SelectedIndex = tab;
            Settle();
            check(tab);
        }
    }

    [Theory]
    [InlineData(1280, 800, "1280x800")]
    [InlineData(800, 500, "800x500")]
    public Task EveryTab_HasNoCutOrOverlappingText(double width, double height, string name) => ui.Run(() =>
    {
        var window = Open(width, height);
        ShowEveryTab(window, tab =>
        {
            AssertLayoutIsClean(window, $"{name} タブ {tab}");
            Capture(window, $"{name}-tab{tab}");
        });
        CloseWindow(window);
    });

    [Fact]
    public Task LongText_StillFits() => ui.Run(() =>
    {
        var resources = Avalonia.Application.Current!.Resources;
        resources["S.Noise.Warp"] = "ドメインワープ強度（UV 単位）";
        resources["S.Noise.Resolution"] = "解像度（px）";
        try
        {
            var window = Open(800, 500);
            window.TabSegment.SelectedIndex = 0;
            Settle();
            AssertLayoutIsClean(window, "長い文言 800x500");
            Capture(window, "long-800x500");
            CloseWindow(window);
        }
        finally
        {
            resources.Remove("S.Noise.Warp");
            resources.Remove("S.Noise.Resolution");
        }
    });

    [Fact]
    public Task RightPanel_NarrowsBelow1000() => ui.Run(() =>
    {
        var wide = Open(1280, 800);
        Assert.Equal(320, wide.RightPanel.Width);
        wide.CloseWithoutPrompt();

        var narrow = Open(800, 500);
        Assert.Equal(264, narrow.RightPanel.Width);
        narrow.CloseWithoutPrompt();
    });

    [Fact]
    public Task AllResourceKeys_AreDefined_NoRawKeysOnScreen() => ui.Run(() =>
    {
        var window = Open();
        ShowEveryTab(window, _ =>
        {
            var raw = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text is { } s && s.StartsWith("S.", StringComparison.Ordinal))
                .Select(t => t.Text).ToList();
            Assert.Empty(raw);
        });
        CloseWindow(window);
    });

    // 既存の機能を減らしていないことの確認: 全設定が新しい部品を通って往復する
    [Fact]
    public Task EverySetting_RoundTripsThroughTheControls() => ui.Run(() =>
    {
        var document = new ProjectDocument
        {
            Noise = new NoiseSettings
            {
                Resolution = 1024, Type = NoiseType.Worley, Seed = 42, Frequency = 7, Octaves = 3, Persistence = 0.35f,
                Seam = SeamMode.X, Lacunarity = 3, Fractal = FractalMode.Ridged, Worley = WorleyMode.F2MinusF1,
                WarpStrength = 0.2f, Contrast = 1.7f, Brightness = -0.1f, Invert = true,
            },
            Preview = new PreviewSettings
            {
                Mode = PreviewMode.Effect, ScrollSpeedU = 0.3f, ScrollSpeedV = -0.2f,
                Polar = new PolarSettings(0.1f, 0.9f, 45f, FlipAngular: true, FlipRadial: true),
                Heatmap = HeatmapMode.Gradient, HeatThreshold = 7f,
            },
            Effect = new EffectSettings
            {
                Mapping = EffectMapping.Polar, Blend = EffectBlend.Alpha, Background = EffectBackground.Gray,
                Mask = EffectMask.None, Rotation = new(10f, 200f), Scale = new(0.5f, 1.5f), Dissolve = new(0.1f, 0.9f),
                DissolveEdgeWidth = 0.2f, DissolveEdgeColor = new Vector3(1f, 0f, 0f), Alpha = new(0.8f, 0.2f),
                Color = new(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f)), TextureAsAlpha = false,
            },
            Timeline = new TimelineSettings { Duration = 3.5, Loop = false },
            Export = new ExportSettings { Channels = ExportChannels.RgbaWhiteAlpha },
        };

        var window = Open();
        window.ApplyDocument(document);
        Settle();
        var read = window.BuildDocument();

        Assert.Equal(document.Noise, read.Noise);
        Assert.Equal(document.Preview, read.Preview);
        Assert.Equal(document.Effect, read.Effect);
        Assert.Equal(document.Timeline, read.Timeline);
        Assert.Equal(document.Export, read.Export);
        CloseWindow(window);
    });

    [Fact]
    public Task SliderField_DragSetsSnappedValue_AndReportsChange() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.FrequencyField;
        int changes = 0;
        window.NoiseTab.Changed += () => changes++;

        var center = field.TranslatePoint(new Point(field.Bounds.Width / 2, field.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Settle();

        // 1〜32 の中央付近で、整数に丸められる
        Assert.InRange(field.Value, 16, 17);
        Assert.Equal(Math.Round(field.Value), field.Value);
        Assert.True(changes > 0);
        Assert.Equal((int)field.Value, window.NoiseTab.Read().Frequency);
        CloseWindow(window);
    });

    [Fact]
    public Task SliderField_KeyboardStepsAndClamps() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.OctavesField;
        field.Focus();
        double before = field.Value;
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(before + 1, field.Value);
        window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.End, RawInputModifiers.None);
        Assert.Equal(8, field.Value);
        CloseWindow(window);
    });

    [Fact]
    public Task Segment_ClickSelects_AndThumbFollows() => ui.Run(() =>
    {
        var window = Open();
        var segment = window.Strip.ViewSegment;
        var third = segment.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(2);
        var center = third.TranslatePoint(new Point(third.Bounds.Width / 2, third.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Settle();

        Assert.Equal(PreviewMode.Polar, window.Strip.Mode);
        Assert.True(window.Strip.PolarButton.IsVisible, "極座標のときは設定ボタンが出る");
        Assert.True(window.PolarThumb.IsActive);
        CloseWindow(window);
    });

    [Fact]
    public Task NumberField_RoundsOnCommit_AndStepsWithinRange() => ui.Run(() =>
    {
        var field = new NumberField { Minimum = 0, Maximum = 10, Increment = 1, Value = 5 };
        var window = new Window { Content = field, Width = 200, Height = 60 };
        window.Show();
        Settle();

        var text = field.GetVisualDescendants().OfType<TextBox>().Single();
        void PressEnter() => text.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        text.Text = "999";
        PressEnter();
        Assert.Equal(10, field.Value);
        Assert.Equal("10", text.Text);

        text.Text = "abc";
        PressEnter();
        Assert.Equal(10, field.Value);
        Assert.Equal("10", text.Text);
        CloseWindow(window);
    });

    [Fact]
    public Task UnavailableFeatures_LookDisabled_ButKeepTheirPlace() => ui.Run(() =>
    {
        var window = Open();
        var rail = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tool")).ToList();
        Assert.Equal(7, rail.Count);
        Assert.All(rail, b => Assert.False(b.IsEffectivelyEnabled));
        CloseWindow(window);
    });

    [Fact]
    public Task EveryIcon_HasAVisibleStroke() => ui.Run(() =>
    {
        var window = Open();
        var icons = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p => p.Classes.Contains("icon")).ToList();
        Assert.NotEmpty(icons);
        Assert.All(icons, icon =>
        {
            Assert.NotNull(icon.Stroke);
            Assert.NotNull(icon.Data);
        });
        CloseWindow(window);
    });

    [Fact]
    public Task Duration_ShowsConfiguredFormat() => ui.Run(() =>
    {
        var window = Open();
        var text = window.Timeline.DurationField.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal("2.0", text.Text);
        CloseWindow(window);
    });

    [Fact]
    public Task WorleyDistance_IsEnabledOnlyForWorley() => ui.Run(() =>
    {
        var window = Open();
        Assert.False(window.NoiseTab.WorleySegment.IsEnabled);
        window.NoiseTab.TypeSegment.SelectedIndex = (int)NoiseType.Worley;
        Assert.True(window.NoiseTab.WorleySegment.IsEnabled);
        CloseWindow(window);
    });
}
