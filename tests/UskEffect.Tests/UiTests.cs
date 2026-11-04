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

    private readonly HeadlessUnitTestSession _session = StartSession();

    private static HeadlessUnitTestSession StartSession()
    {
        // 利用者の設定 (%APPDATA%) を書き換えない
        SliderLimitStore.PathOverride = Path.Combine(Path.GetTempPath(), $"uskfx-limits-{Guid.NewGuid():N}.json");
        return HeadlessUnitTestSession.StartNew(typeof(UiSession));
    }

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
            if (field.Classes.Contains("compact") && used > field.Bounds.Width + 0.5)
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
                StripeDirection = StripeDirection.Diagonal, StripeWidth = 0.3f, StripeSoftness = 0.25f,
                WarpStrength = 0.2f, Contrast = 1.7f, Brightness = -0.1f, Invert = true,
            },
            Preview = new PreviewSettings
            {
                Mode = PreviewMode.Effect, ScrollSpeedU = 0.3f, ScrollSpeedV = -0.2f,
                Polar = new PolarSettings(0.1f, 0.9f, 45f, FlipAngular: true, FlipRadial: true),
                Heatmap = HeatmapMode.Gradient, HeatThreshold = 7f,
                TilingX = 2.5f, TilingY = 4f, OffsetX = 0.25f, OffsetY = -0.5f,
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

        var track = field.GetVisualDescendants().OfType<Panel>().First(c => c.Name == "PART_Track");
        var center = track.TranslatePoint(new Point(track.Bounds.Width / 2, track.Bounds.Height / 2), window)!.Value;
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
        var third = segment.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(1);
        var center = third.TranslatePoint(new Point(third.Bounds.Width / 2, third.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Settle();

        Assert.Equal(PreviewMode.Polar, window.Strip.Mode);
        Assert.True(window.Strip.PolarButton.IsVisible, "極座標のときは設定ボタンが出る");
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

    [Theory]
    [InlineData(0.01, 0)]   // 中央の近くは 0 に吸着する
    [InlineData(-0.09, 0)]
    [InlineData(0.2, 0.2)]  // 離れていれば吸着しない
    public Task NegativeToPositiveRange_SnapsToMiddleWhileDragging(double value, double expected) => ui.Run(() =>
    {
        var field = new SliderField { Minimum = -1, Maximum = 1, Step = 0.01 };
        field.SetSnapped(value, snapToMiddle: true);
        Assert.Equal(expected, field.Value, 6);
    });

    [Fact]
    public Task PositiveOnlyRange_DoesNotSnapToMiddle() => ui.Run(() =>
    {
        var field = new SliderField { Minimum = 0, Maximum = 1, Step = 0.01 };
        field.SetSnapped(0.51, snapToMiddle: true);
        Assert.Equal(0.51, field.Value, 6);
    });

    [Fact]
    public Task DoubleClickEditor_CommitsClampsAndCancels() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.PersistenceField;
        var editor = field.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_Editor");

        field.BeginEdit();
        Assert.True(field.IsEditing);
        editor.Text = "0.25";
        field.EndEdit(commit: true);
        Assert.False(field.IsEditing);
        Assert.Equal(0.25, field.Value, 6);

        field.BeginEdit();
        editor.Text = "99";
        field.EndEdit(commit: true);
        Assert.Equal(1, field.Value);

        field.BeginEdit();
        editor.Text = "0.5";
        field.EndEdit(commit: false);
        Assert.Equal(1, field.Value);
        CloseWindow(window);
    });

    [Fact]
    public Task SeedButton_SetsRandomValue_AndHasNoSteppers() => ui.Run(() =>
    {
        var window = Open();
        Assert.False(window.NoiseTab.SeedField.ShowSteppers);
        var seen = new HashSet<decimal>();
        for (int i = 0; i < 20; i++)
        {
            window.NoiseTab.RandomSeedButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            seen.Add(window.NoiseTab.SeedField.Value);
            Assert.InRange(window.NoiseTab.SeedField.Value, 0, 999999);
        }
        Assert.True(seen.Count > 1, "Seed が変わらない");
        CloseWindow(window);
    });

    [Fact]
    public Task TileView_IsGone_FromSwitcherAndPreviews() => ui.Run(() =>
    {
        var window = Open();
        Assert.Equal(4, window.Strip.ViewSegment.ItemCount);
        window.ApplyDocument(new ProjectDocument { Preview = new PreviewSettings { Mode = PreviewMode.Tile3x3 } });
        Settle();
        // 古いプロジェクトの 3×3 は Tiling 3×3 の矩形として開く
        Assert.Equal(PreviewMode.Rect, window.Strip.Mode);
        Assert.Equal((3f, 3f, 0f, 0f), window.NoiseTab.ReadTexture());
        CloseWindow(window);
    });

    [Fact]
    public Task ImageSource_DisablesNoiseControls_AndCannotBeChosenWithoutImage() => ui.Run(() =>
    {
        var window = Open();
        Assert.False(window.NoiseTab.SourceImageItem.IsEnabled);
        Assert.True(window.NoiseTab.NoiseControls.IsEnabled);

        window.ApplyDocument(new ProjectDocument
        {
            Image = new ImageData(1, 1, [1, 2, 3, 255]),
            Source = new SourceSettings { Kind = SourceKind.Image, ImageName = "a.png" },
        });
        Settle();

        Assert.True(window.NoiseTab.UseImage);
        Assert.False(window.NoiseTab.NoiseControls.IsEnabled);
        Assert.Equal("a.png", window.BuildDocument().Source.ImageName);
        CloseWindow(window);
    });

    [Fact]
    public Task OpenFile_LoadsProject_ThenAddsImageWithoutReplacingProject() => ui.Run(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), $"uskfx-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string projectPath = Path.Combine(dir, "a.USKFX");
            ProjectFile.Save(projectPath, new ProjectDocument { Noise = new NoiseSettings { Seed = 1234 } });
            string imagePath = Path.Combine(dir, "b.tga");
            File.WriteAllBytes(imagePath, Tga1x1());

            var window = Open();
            WaitFor(window.OpenFileAsync(projectPath));
            Assert.Equal(1234u, window.BuildDocument().Noise.Seed);
            Assert.False(window.NoiseTab.UseImage);

            WaitFor(window.OpenFileAsync(imagePath));
            var document = window.BuildDocument();
            Assert.True(window.NoiseTab.UseImage);
            Assert.Equal("b.tga", document.Source.ImageName);
            Assert.Equal(1234u, document.Noise.Seed);
            Assert.StartsWith("a.USKFX", window.Title);

            // 未保存の変更があっても、画像なら保存の確認を出さずに読み込む
            Assert.Contains("*", window.Title);
            WaitFor(window.OpenFileAsync(imagePath));
            CloseWindow(window);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task PaintMode_EnablesTools_AndShowsBrushSettings() => ui.Run(() =>
    {
        var window = Open();
        Assert.False(window.Rail.BrushToolButton.IsEnabled);
        Assert.False(window.Strip.BrushButton.IsVisible);

        window.Strip.Mode = UskEffect.Graphics.PreviewMode.Effect;
        window.Top.ModeSegment.SelectedIndex = 1;
        Settle();

        Assert.True(window.Rail.BrushToolButton.IsEnabled);
        Assert.True(window.Strip.BrushButton.IsVisible);
        Assert.Equal(UskEffect.Graphics.PreviewMode.Rect, window.Strip.Mode);

        // 極座標とシームの表示のまま描ける
        window.Strip.Mode = UskEffect.Graphics.PreviewMode.Polar;
        Settle();
        Assert.Equal(UskEffect.Graphics.PreviewMode.Polar, window.Strip.Mode);

        window.Rail.EraserToolButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.Equal(UskEffect.Graphics.Painting.PaintTool.Eraser, window.CurrentBrush.Tool);
        Assert.False(window.Strip.BrushColorRow.IsVisible);

        window.Strip.BrushSize = 100000;
        Assert.Equal(UskEffect.Graphics.Painting.BrushSettings.MaxSize, window.CurrentBrush.Size);

        window.Top.ModeSegment.SelectedIndex = 0;
        Settle();
        Assert.False(window.Rail.BrushToolButton.IsEnabled);
        CloseWindow(window);
    });

    [Fact]
    public Task SeamFix_Buttons_SwitchToOffsetView_AndBlendLocksFileOperations() => ui.Run(() =>
    {
        var window = Open();
        window.TabSegment.SelectedIndex = 1;
        Settle();

        window.SeamTab.ManualFixButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.Equal(1, window.Top.ModeSegment.SelectedIndex);
        Assert.Equal(UskEffect.Graphics.PreviewMode.SeamCheck, window.Strip.Mode);

        window.SeamTab.BlendButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.True(window.SeamTab.BlendControls.IsVisible);
        Assert.False(window.SeamTab.FixButtons.IsVisible);
        Assert.False(window.Top.IsEnabled);
        Assert.False(window.NoiseTab.IsEnabled);

        window.SeamTab.BlendCancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.False(window.SeamTab.BlendControls.IsVisible);
        Assert.True(window.Top.IsEnabled);
        Assert.True(window.NoiseTab.IsEnabled);
        CloseWindow(window);
    });

    private static void WaitFor(Task task)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < limit, "開く処理が終わりません");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
        Settle();
    }

    // 1×1、24bit、無圧縮の TGA
    private static byte[] Tga1x1() => [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 24, 0, 10, 20, 30];

    [Fact]
    public Task Vector2Link_KeepsBothValuesTheSame() => ui.Run(() =>
    {
        var window = Open();
        var row = window.NoiseTab.TilingRow;
        row.X.Value = 3;
        Assert.Equal(1, row.Y.Value);

        row.IsLinked = true;
        Assert.Equal(3, row.Y.Value);

        row.Y.Value = 5;
        Assert.Equal(5, row.X.Value);
        Assert.Equal((5f, 5f, 0f, 0f), window.NoiseTab.ReadTexture());

        row.IsLinked = false;
        row.X.Value = 2;
        Assert.Equal(5, row.Y.Value);
        CloseWindow(window);
    });

    [Fact]
    public Task CompactField_ClickStartsEditing_AndHidesOldValue() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.OffsetRow.X;
        double before = field.Value;
        var center = field.TranslatePoint(new Point(field.Bounds.Width * 0.8, field.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Settle();

        Assert.True(field.IsEditing, "クリックで入力が始まる");
        Assert.Equal(before, field.Value); // クリックだけでは値は動かない
        var value = field.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Value");
        Assert.False(value.IsVisible, "入力中は前の値を隠す");

        field.EndEdit(commit: false);
        Assert.True(value.IsVisible);
        CloseWindow(window);
    });

    [Fact]
    public Task CompactField_DragChangesValue() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.OffsetRow.X;
        var start = field.TranslatePoint(new Point(field.Bounds.Width * 0.5, field.Bounds.Height / 2), window)!.Value;
        var end = field.TranslatePoint(new Point(field.Bounds.Width * 0.9, field.Bounds.Height / 2), window)!.Value;

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Settle();

        Assert.False(field.IsEditing);
        Assert.InRange(field.Value, 0.5, 1);
        CloseWindow(window);
    });

    [Fact]
    public Task InspectorField_ValueBoxClickStartsEditing() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.PersistenceField;
        var box = field.GetVisualDescendants().OfType<Panel>().Single(c => c.Name == "PART_ValueBox");
        var center = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Settle();

        Assert.True(field.IsEditing);
        field.EndEdit(commit: false);
        CloseWindow(window);
    });

    [Fact]
    public Task ExpandableField_RaisesMaximum_UpToTheDigitLimit() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.FrequencyField;
        var editor = field.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_Editor");
        Assert.Equal(32, field.Maximum);

        field.BeginEdit();
        editor.Text = "100";
        field.EndEdit(commit: true);
        Assert.Equal(100, field.Maximum);
        Assert.Equal(100, field.Value);

        field.BeginEdit();
        editor.Text = "5000";
        field.EndEdit(commit: true);
        Assert.Equal(999, field.Maximum);
        Assert.Equal(999, field.Value);
        CloseWindow(window);
    });

    [Fact]
    public Task FixedField_DoesNotExpand() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.OctavesField;
        var editor = field.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_Editor");

        field.BeginEdit();
        editor.Text = "100";
        field.EndEdit(commit: true);

        Assert.Equal(8, field.Maximum);
        Assert.Equal(8, field.Value);
        CloseWindow(window);
    });

    [Theory]
    [InlineData(60, 60)] // 広げて 60 で終了 → 次回の最大値は 60
    [InlineData(10, 32)] // 既定の最大値は下回らない
    public Task NextStart_UsesExitValueAsMaximum(double exitValue, double expectedMaximum) => ui.Run(() =>
    {
        var first = Open();
        first.NoiseTab.FrequencyField.SetValueExpanding(100);
        first.NoiseTab.FrequencyField.Value = exitValue;
        first.CloseWithoutPrompt();
        Settle();

        var second = Open();
        Assert.Equal(expectedMaximum, second.NoiseTab.FrequencyField.Maximum);
        second.NoiseTab.FrequencyField.Value = 4;
        second.CloseWithoutPrompt();
        Settle();
    });

    [Fact]
    public Task BrokenLimitFile_FallsBackToDefaults() => ui.Run(() =>
    {
        File.WriteAllText(SliderLimitStore.PathOverride!, "{ not json");
        var window = Open();
        Assert.Equal(32, window.NoiseTab.FrequencyField.Maximum);
        window.NoiseTab.FrequencyField.Value = 4;
        CloseWindow(window);
    });

    [Fact]
    public Task ValueText_HasNoPlusSign_AndFitsItsBox() => ui.Run(() =>
    {
        var window = Open();
        var brightness = window.NoiseTab.BrightnessField;
        brightness.Value = 0.25;
        var text = brightness.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Value");
        Assert.Equal("0.25", text.Text);

        // 桁の上限まで広げても値がボックスからはみ出さない
        var frequency = window.NoiseTab.FrequencyField;
        frequency.SetValueExpanding(999);
        Settle();
        var box = frequency.GetVisualDescendants().OfType<Panel>().Single(c => c.Name == "PART_ValueBox");
        var value = frequency.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Value");
        Assert.True(NaturalWidth(value) <= box.Bounds.Width, $"{NaturalWidth(value)} > {box.Bounds.Width}");
        frequency.Value = 4;
        CloseWindow(window);
    });

    [Fact]
    public Task StripeControls_AreEnabledOnlyForStripe() => ui.Run(() =>
    {
        var window = Open();
        Assert.False(window.NoiseTab.StripeControls.IsEnabled);
        window.NoiseTab.TypeBox.SelectedIndex = (int)NoiseType.Stripe;
        Assert.True(window.NoiseTab.StripeControls.IsEnabled);
        CloseWindow(window);
    });

    private static Point CenterOf(Visual visual, Window window) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    [Fact]
    public Task Editing_EndsWithCommit_WhenClickingElsewhere() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.PersistenceField;
        var editor = field.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_Editor");
        field.BeginEdit();
        Settle();
        editor.Text = "0.25";

        // フォーカスを持てない場所 (項目名) をクリックする
        var label = window.NoiseTab.OctavesField.GetVisualDescendants().OfType<TextBlock>().First();
        var at = CenterOf(label, window);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle();

        Assert.False(field.IsEditing);
        Assert.Equal(0.25, field.Value, 6);
        CloseWindow(window);
    });

    [Fact]
    public Task Editing_CancelsWithEscape() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.PersistenceField;
        var editor = field.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_Editor");
        field.BeginEdit();
        Settle();
        editor.Text = "0.25";

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle();

        Assert.False(field.IsEditing);
        Assert.Equal(0.5, field.Value, 6);
        CloseWindow(window);
    });

    [Fact]
    public Task Canvas_ClickEndsEditing_ThroughEndActive() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.PersistenceField;
        field.BeginEdit();
        Settle();

        TextEditDismiss.EndActive();

        Assert.False(field.IsEditing);
        CloseWindow(window);
    });

    [Fact]
    public Task NumberField_EndsEditing_OnClickElsewhere_AndEscapeReverts() => ui.Run(() =>
    {
        var window = Open();
        var seed = window.NoiseTab.SeedField;
        var text = seed.GetVisualDescendants().OfType<TextBox>().Single();

        text.Focus();
        Settle();
        text.Text = "123";
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle();
        Assert.Equal(1, seed.Value);
        Assert.Equal("1", text.Text);
        Assert.False(text.IsFocused);

        text.Focus();
        Settle();
        text.Text = "456";
        var label = window.NoiseTab.OctavesField.GetVisualDescendants().OfType<TextBlock>().First();
        var at = CenterOf(label, window);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle();
        Assert.Equal(456, seed.Value);
        Assert.False(text.IsFocused);
        CloseWindow(window);
    });

    [Fact]
    public Task RightPanel_HasNoPreviewPanes_OnlyTheCanvasHost() => ui.Run(() =>
    {
        var window = Open();
        Assert.Single(window.GetVisualDescendants().OfType<UskEffect.App.Controls.D3DHost>());
        CloseWindow(window);
    });

    [Fact]
    public Task CompactField_HasNoMiddleTick() => ui.Run(() =>
    {
        var window = Open();
        var ticks = window.NoiseTab.OffsetRow.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PART_Middle");
        Assert.Empty(ticks);
        CloseWindow(window);
    });

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 128, 255)]
    [InlineData(255, 140, 38)]
    [InlineData(10, 20, 30)]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    public void Hsv_RoundTripsColors(byte r, byte g, byte b)
    {
        var color = Avalonia.Media.Color.FromRgb(r, g, b);
        Assert.Equal(color, Hsv.FromColor(color).ToColor());
    }

    [Fact]
    public void Hsv_KeepsHue_ForGray()
    {
        Assert.Equal(200, Hsv.FromColor(Avalonia.Media.Colors.Gray, fallbackHue: 200).H);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(120, 0.5, 0.8)]
    [InlineData(300, 0.25, 0.3)]
    [InlineData(45, 0, 1)]
    public void Triangle_PointAndPick_AreInverse(double h, double s, double v)
    {
        var point = HsvTriangle.ToPoint(new Hsv(h, s, v), 100);

        var (pickedS, pickedV) = HsvTriangle.Pick(point, h, 100);

        Assert.Equal(s, pickedS, 6);
        Assert.Equal(v, pickedV, 6);
    }

    [Fact]
    public void Triangle_Vertices_ArePureWhiteAndBlack()
    {
        // 純色の頂点はリング上の色相の向き
        var pure = HsvTriangle.ToPoint(new Hsv(0, 1, 1), 100);
        Assert.Equal(100, pure.X, 6);
        Assert.Equal(0, pure.Y, 6);

        var black = HsvTriangle.ToPoint(new Hsv(0, 0, 0), 100);
        var white = HsvTriangle.ToPoint(new Hsv(0, 0, 1), 100);
        Assert.Equal(0, HsvTriangle.Pick(black, 0, 100).V, 6);
        Assert.Equal(1, HsvTriangle.Pick(white, 0, 100).V, 6);
        Assert.Equal(0, HsvTriangle.Pick(white, 0, 100).S, 6);
    }

    [Fact]
    public void Triangle_Pick_ClampsOutsidePoints()
    {
        var (s, v) = HsvTriangle.Pick(new Point(-500, 0), 0, 100);
        Assert.InRange(s, 0, 1);
        Assert.InRange(v, 0, 1);
    }

    [Fact]
    public Task Picker_PicksColorFromRingAndTriangle() => ui.Run(() =>
    {
        var picker = new TriangleColorPicker { Width = 180, Height = 180 };
        var window = new Window { Content = picker, Width = 200, Height = 200 };
        window.Show();
        Settle();
        var center = CenterOf(picker, window);

        // リングの右端 = 色相 0 (赤) を押してから、三角形の純色の頂点付近を押す
        var ring = center + new Point(84, 0);
        window.MouseDown(ring, MouseButton.Left);
        window.MouseUp(ring, MouseButton.Left);
        var pure = center + new Point(70, 0);
        window.MouseDown(pure, MouseButton.Left);
        window.MouseUp(pure, MouseButton.Left);
        Settle();

        Assert.True(picker.Color.R > 200 && picker.Color.G < 60 && picker.Color.B < 60, picker.Color.ToString());
        window.CaptureRenderedFrame()?.Save(CapturePath("color-picker"));
        window.Close();
    });

    // 表示倍率が 100% でなくても、絵が拡大されず枠に収まること
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public Task Picker_DrawsAtTheRightSize_AtAnyDisplayScale(double scaling) => ui.Run(() =>
    {
        var picker = new TriangleColorPicker { Width = 180, Height = 180, ScalingOverride = scaling };
        var window = new Window { Content = picker, Width = 200, Height = 200 };
        window.Show();
        Settle();
        var center = CenterOf(picker, window);

        var frame = window.CaptureRenderedFrame()!;
        using var locked = frame.Lock();
        Avalonia.Media.Color At(Point p)
        {
            int offset = (int)p.Y * locked.RowBytes + (int)p.X * 4;
            byte first = System.Runtime.InteropServices.Marshal.ReadByte(locked.Address, offset);
            byte g = System.Runtime.InteropServices.Marshal.ReadByte(locked.Address, offset + 1);
            byte third = System.Runtime.InteropServices.Marshal.ReadByte(locked.Address, offset + 2);
            return locked.Format == Avalonia.Platform.PixelFormat.Rgba8888
                ? Avalonia.Media.Color.FromRgb(first, g, third)
                : Avalonia.Media.Color.FromRgb(third, g, first);
        }

        // リングは半径 75.6〜90。各方向の色相がその位置に出ていれば、大きさも位置も合っている
        var right = At(center + new Point(83, 0));   // 色相 0: 赤
        var left = At(center + new Point(-83, 0));   // 色相 180: シアン
        var down = At(center + new Point(0, 83));    // 色相 90: 黄緑
        var up = At(center + new Point(0, -83));     // 色相 270: 紫
        Assert.True(right.R > 200 && right.G < 80 && right.B < 80, $"右 {right}");
        Assert.True(left.R < 80 && left.G > 200 && left.B > 200, $"左 {left}");
        Assert.True(down.G > 200 && down.B < 80, $"下 {down}");
        Assert.True(up.B > 200 && up.G < 80, $"上 {up}");
        window.Close();
    });

    [Fact]
    public Task Swatch_FiresColorChanged() => ui.Run(() =>
    {
        var swatch = new ColorSwatch();
        int changes = 0;
        swatch.ColorChanged += () => changes++;
        swatch.Color = Avalonia.Media.Colors.Red;
        Assert.Equal(1, changes);
    });

    [Fact]
    public Task WorleyDistance_IsEnabledOnlyForWorley() => ui.Run(() =>
    {
        var window = Open();
        Assert.False(window.NoiseTab.WorleySegment.IsEnabled);
        window.NoiseTab.TypeBox.SelectedIndex = (int)NoiseType.Worley;
        Assert.True(window.NoiseTab.WorleySegment.IsEnabled);
        CloseWindow(window);
    });
}
