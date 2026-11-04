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
using UskEffect.App.Views;
using UskEffect.Core.Export;
using UskEffect.Core.Project;
using UskEffect.Graphics;

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
        // 新規で始めると種類の一覧が開く。テストは Perlin を選んだ状態から始める
        window.NoiseTab.CloseTypeMenu();
        window.AddNoiseLayer(NoiseType.Perlin);
        Settle();
        return window;
    }

    private static MainWindow OpenWithoutChoosing(double width = 1280, double height = 800)
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
        Settle();
        check(0);
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
                Mode = PreviewMode.Polar,
                Polar = new PolarSettings(0.1f, 0.9f, 45f, FlipAngular: true, FlipRadial: true),
            },
            Export = new ExportSettings { Output = CompositeOutput.BlackToAlpha },
        };

        var window = Open();
        window.ApplyDocument(document);
        Settle();
        var read = window.BuildDocument();

        Assert.Equal(document.Noise, read.Noise);
        Assert.Equal(document.Preview, read.Preview);
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
        // レイヤーのアイコンはペイントモードでなくても使える
        var rail = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("tool") && b != window.Rail.LayerButton).ToList();
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
        Assert.Equal(3, window.Strip.ViewSegment.ItemCount);
        window.ApplyDocument(new ProjectDocument { Preview = new PreviewSettings { Mode = PreviewMode.Tile3x3 } });
        Settle();
        // 古いプロジェクトの 3×3 は Tiling 3×3 の矩形として開く
        Assert.Equal(PreviewMode.Rect, window.Strip.Mode);
        Assert.Equal((3f, 3f, 0f, 0f), window.NoiseTab.ReadTexture());
        CloseWindow(window);
    });

    [Fact]
    public Task OldProject_WithAnUnusedImage_KeepsItHidden() => ui.Run(() =>
    {
        var window = Open();
        window.ApplyDocument(new ProjectDocument
        {
            Image = new ImageData(1, 1, [1, 2, 3, 255]),
            Source = new SourceSettings { Kind = SourceKind.Noise, ImageName = "a.png" },
        });
        Settle();

        var layers = window.BuildDocument().Layers!;
        Assert.Equal(2, layers.Count);
        Assert.NotNull(layers[0].Noise);
        Assert.False(layers[1].Visible);
        Assert.Null(window.BuildDocument().CanvasWidth);
        CloseWindow(window);
    });

    [Fact]
    public Task CustomCanvasSize_RoundTrips_AndChoosingAResolutionClearsIt() => ui.Run(() =>
    {
        var window = Open();
        window.NoiseTab.SetCustomSize(300, 200);
        var document = window.BuildDocument();
        Assert.Equal(300, document.CanvasWidth);

        window.ApplyDocument(new ProjectDocument { NoiseIsLayer = true });
        Settle();
        Assert.Null(window.NoiseTab.CustomSize);
        window.ApplyDocument(document);
        Settle();
        Assert.Equal((300, 200), window.NoiseTab.CustomSize);

        window.NoiseTab.Resolution = 1024;
        Assert.Null(window.NoiseTab.CustomSize);
        Assert.Equal(1024, window.BuildDocument().Noise.Resolution);
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
            Assert.Single(window.BuildDocument().Layers!);

            // 画像はレイヤーになる。始めたばかりなので、キャンバスは画像の大きさになる
            WaitFor(window.OpenFileAsync(imagePath));
            var document = window.BuildDocument();
            Assert.Equal(2, document.Layers!.Count);
            Assert.Equal("b.tga", document.Layers[^1].Name);
            Assert.Null(document.Layers[^1].Noise);
            Assert.Equal(1, document.CanvasWidth);
            Assert.Equal(1234u, document.Noise.Seed);
            Assert.StartsWith("a.USKFX", window.Title);

            // 未保存の変更があっても、画像なら保存の確認を出さずに読み込む
            Assert.Contains("*", window.Title);
            WaitFor(window.OpenFileAsync(imagePath));
            Assert.Equal(3, window.BuildDocument().Layers!.Count);
            CloseWindow(window);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task LayerPanel_ShowsAndChangesTheLayers_AndUndoRestores() => ui.Run(() =>
    {
        var window = Open();
        Assert.Single(window.LayerTab.LayerList.Children);
        Assert.False(window.LayerTab.DeleteButton.IsEnabled);
        Assert.False(window.Top.UndoButton.IsEnabled);

        window.LayerTab.AddButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        window.LayerTab.AddPaintButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.Equal(2, window.LayerTab.LayerList.Children.Count);
        Assert.True(window.LayerTab.DeleteButton.IsEnabled);
        Assert.True(window.Top.UndoButton.IsEnabled);
        Assert.True(window.LayerTab.MoveDownButton.IsEnabled);
        Assert.False(window.LayerTab.MoveUpButton.IsEnabled);
        Assert.Contains("*", window.Title);

        window.LayerTab.OpacityField.Value = 0.5;
        Settle();
        Assert.Contains("50%", ((TextBlock)((Grid)((Border)window.LayerTab.LayerList.Children[0]).Child!).Children[2]).Text);

        window.Top.UndoButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.Equal(1, window.LayerTab.OpacityField.Value);

        window.Top.UndoButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.Single(window.LayerTab.LayerList.Children);
        Assert.False(window.Top.UndoButton.IsEnabled);
        Assert.True(window.Top.RedoButton.IsEnabled);

        window.Top.RedoButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.Equal(2, window.LayerTab.LayerList.Children.Count);
        CloseWindow(window);
    });

    [Fact]
    public Task Document_RestoresLayers_AndNewProjectResets() => ui.Run(() =>
    {
        var window = Open();
        window.ApplyDocument(new ProjectDocument
        {
            NoiseIsLayer = true,
            Layers =
            [
                new UskEffect.Graphics.Painting.LayerData { Name = "下" },
                new UskEffect.Graphics.Painting.LayerData { Name = "上", Visible = false, Opacity = 0.25f },
            ],
        });
        Settle();
        Assert.Equal(2, window.LayerTab.LayerList.Children.Count);

        window.ApplyDocument(new ProjectDocument());
        Settle();
        Assert.Single(window.LayerTab.LayerList.Children);
        Assert.NotNull(window.BuildDocument().Layers!.Single().Noise);
        CloseWindow(window);
    });

    [Fact]
    public void PenPressure_IsNormalized_AndMissingPressureCountsAsFull()
    {
        Assert.Equal(0.5, UskEffect.App.Interop.Win32.NormalizePenPressure(1, 512), 6);
        Assert.Equal(1, UskEffect.App.Interop.Win32.NormalizePenPressure(1, 4096), 6);
        Assert.Equal(1, UskEffect.App.Interop.Win32.NormalizePenPressure(0, 0), 6);
    }

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
    public Task EveryType_CanBeSelected_AndShapesHideNoiseOnlyRows() => ui.Run(() =>
    {
        var window = Open();
        var tab = window.NoiseTab;
        foreach (var type in Enum.GetValues<NoiseType>())
        {
            tab.SelectedType = type;
            Settle();
            Assert.Equal(type, tab.Read().Type);
            Assert.Equal(type.IsNoise() && type != NoiseType.Stripe, tab.SeamRow.IsVisible);
            Assert.Equal(type.IsNoise(), tab.ShapeSection.IsVisible);
            Assert.Equal(type == NoiseType.Worley, tab.WorleyRow.IsVisible);
            Assert.Equal(type == NoiseType.Stripe, tab.StripeControls.IsVisible);
            Assert.Equal(!type.IsNoise(), tab.PatternSection.IsVisible);
        }

        tab.SelectedType = NoiseType.ShapeStar;
        Settle();
        Assert.True(tab.CountField.IsVisible);
        Assert.False(tab.AspectField.IsVisible);
        Assert.False(tab.SeedRow.IsVisible);
        Assert.Equal("内側の比率", tab.AmountField.Label);

        tab.SelectedType = NoiseType.Burst;
        Settle();
        Assert.True(tab.SeedRow.IsVisible);
        CloseWindow(window);
    });

    [Fact]
    public Task ShapeSettings_SurviveWriteAndRead() => ui.Run(() =>
    {
        var window = Open();
        var settings = new NoiseSettings
        {
            Type = NoiseType.BezierCurve,
            Shape = new ShapeSettings
            {
                CenterX = 0.2f, CenterY = 0.7f, AngleDegrees = 45, Size = 0.3f, Thickness = 0.2f, Softness = 0.05f, Count = 9,
                Falloff = 2, Amount = 0.25f, Aspect = 1.5f, Twist = -3, Stop0 = 0.1f, Stop1 = 0.2f, Stop2 = 0.3f, Stop3 = 0.4f,
                P0X = 0.11f, P0Y = 0.12f, P1X = 0.13f, P1Y = 0.14f, P2X = 0.15f, P2Y = 0.16f,
            },
        };
        window.NoiseTab.Write(settings);
        var read = window.NoiseTab.Read();
        Assert.Equal(settings.Type, read.Type);
        Assert.Equal(settings.Shape, read.Shape);
        CloseWindow(window);
    });

    [Fact]
    public Task TypeMenu_OpensColumnsByGroup_WithASampleLeftOfEachName() => ui.Run(() =>
    {
        var window = Open(1280, 900);
        var tab = window.NoiseTab;
        using var engine = new RenderEngine(Path.Combine(NoiseFixture.FindRepositoryRoot(), "Shaders"));
        var settings = tab.SampleSettings();
        Assert.Equal(Enum.GetValues<NoiseType>().Length, settings.Count);
        tab.SetSamples(engine.RenderSamples(settings));

        ClickAt(window, CenterOf(tab.TypeButton, window));
        Assert.True(tab.IsTypeMenuOpen);

        // グループごとの列が横に並び、列の中は縦に並ぶ
        Assert.Equal(5, tab.TypeMenu.Children.Count);
        var left = tab.TypeMenu.Children.Select(c => c.TranslatePoint(default, window)!.Value.X).ToArray();
        Assert.Equal(left.OrderBy(x => x), left);
        Assert.Equal(left.Length, left.Distinct().Count());

        var circle = tab.TypeItem(NoiseType.ShapeCircle);
        var ring = tab.TypeItem(NoiseType.ShapeRing);
        var circleAt = circle.TranslatePoint(default, window)!.Value;
        var ringAt = ring.TranslatePoint(default, window)!.Value;
        Assert.Equal(circleAt.X, ringAt.X);
        Assert.True(ringAt.Y > circleAt.Y);

        // サンプルは名前の左で、正方形。名前の開始位置はそろう
        var sample = circle.GetVisualDescendants().OfType<Image>().Single();
        var name = circle.GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.NotNull(sample.Source);
        Assert.Equal(sample.Bounds.Width, sample.Bounds.Height);
        Assert.True(circle.Bounds.Height >= sample.Bounds.Height);
        var nameLeft = name.TranslatePoint(default, window)!.Value.X;
        Assert.True(sample.TranslatePoint(new Point(sample.Bounds.Width, 0), window)!.Value.X <= nameLeft);
        Assert.Equal(nameLeft, ring.GetVisualDescendants().OfType<TextBlock>().Single().TranslatePoint(default, window)!.Value.X);

        // 選ぶと閉じて、欄の表示が変わる
        ring.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.False(tab.IsTypeMenuOpen);
        Assert.Equal(NoiseType.ShapeRing, tab.SelectedType);
        Assert.Equal("Ring", tab.TypeNameText.Text);
        Assert.NotNull(tab.TypeSample.Source);

        // Esc でも閉じる
        ClickAt(window, CenterOf(tab.TypeButton, window));
        Assert.True(tab.IsTypeMenuOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle();
        Assert.False(tab.IsTypeMenuOpen);
        CloseWindow(window);
    });

    [Fact]
    public Task Stripe_ShowsOnlyWhatStripesUse() => ui.Run(() =>
    {
        var window = Open();
        var tab = window.NoiseTab;
        tab.SelectedType = NoiseType.Stripe;
        Settle();
        Assert.True(tab.FrequencyField.IsVisible);
        Assert.False(tab.FractalRow.IsVisible);
        Assert.False(tab.OctavesField.IsVisible);
        Assert.False(tab.SeedRow.IsVisible);
        CloseWindow(window);
    });

    [Fact]
    public Task Persistence_AndLacunarity_ShowOnlyWhenLayered() => ui.Run(() =>
    {
        var window = Open();
        var tab = window.NoiseTab;
        tab.OctavesField.Value = 3;
        Settle();
        Assert.True(tab.PersistenceField.IsVisible);
        tab.OctavesField.Value = 1;
        Settle();
        Assert.False(tab.PersistenceField.IsVisible);
        Assert.False(tab.LacunarityField.IsVisible);
        CloseWindow(window);
    });

    [Fact]
    public Task EffectFeature_IsGone() => ui.Run(() =>
    {
        var window = Open();
        Assert.Equal(3, window.Strip.ViewSegment.ItemCount);
        CloseWindow(window);
    });

    private static void ClickAt(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle();
    }

    [Fact]
    public Task LayerIcon_OpensBesideIt_AndClosesOnEscapeOrOutsideClick() => ui.Run(() =>
    {
        var window = Open();
        var rail = window.Rail;
        var icon = CenterOf(rail.LayerButton, window);
        Assert.False(rail.IsLayerOpen);
        Assert.Equal("1", rail.LayerNumberText.Text);

        // ペイントモードでなくても開ける
        ClickAt(window, icon);
        Assert.True(rail.IsLayerOpen);
        Assert.True(rail.LayerButton.Classes.Contains("selected"));

        // アイコンの隣で、短く開く
        var popup = window.LayerTab;
        var left = popup.TranslatePoint(default, window)!.Value;
        var iconRight = rail.LayerButton.TranslatePoint(new Point(rail.LayerButton.Bounds.Width, 0), window)!.Value.X;
        Assert.True(left.X >= iconRight, $"レイヤーの左端 {left.X}、アイコンの右端 {iconRight}");
        Assert.True(left.X - iconRight < 40, $"アイコンから離れている {left.X - iconRight}");
        Assert.True(popup.Bounds.Width <= 300);
        Assert.True(popup.Bounds.Height < window.Bounds.Height / 2, $"高さ {popup.Bounds.Height}");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle();
        Assert.False(rail.IsLayerOpen);
        Assert.False(rail.LayerButton.Classes.Contains("selected"));

        // 別のところをクリックすると閉じる
        ClickAt(window, icon);
        Assert.True(rail.IsLayerOpen);
        ClickAt(window, CenterOf(window.Status, window));
        Assert.False(rail.IsLayerOpen);

        // もう一度アイコンを押しても閉じる
        ClickAt(window, icon);
        Assert.True(rail.IsLayerOpen);
        ClickAt(window, icon);
        Assert.False(rail.IsLayerOpen);
        CloseWindow(window);
    });

    [Fact]
    public Task CanvasClick_ClosesLayers_WithoutShowingAPaintHint() => ui.Run(() =>
    {
        var window = Open();
        var input = (UskEffect.App.Interop.IHostWindowInput)window.Viewport;
        ClickAt(window, CenterOf(window.Rail.LayerButton, window));
        Assert.True(window.Rail.IsLayerOpen);
        window.Status.MessageText.Text = "";

        input.OnPointerDown(0, 10, 10);
        input.OnPointerUp(0, 10, 10);
        Settle();

        Assert.False(window.Rail.IsLayerOpen);
        Assert.Equal("", window.Status.MessageText.Text);

        // 閉じた後のクリックは普段どおり
        input.OnPointerDown(0, 10, 10);
        input.OnPointerUp(0, 10, 10);
        Assert.Contains("ペイント", window.Status.MessageText.Text);
        CloseWindow(window);
    });

    private static readonly Avalonia.Interactivity.RoutedEventArgs ClickArgs = new(Button.ClickEvent);

    [Fact]
    public Task NewStart_OpensTheTypeMenu_AndChoosingMakesTheFirstLayer() => ui.Run(() =>
    {
        var window = OpenWithoutChoosing();
        Assert.True(window.NoiseTab.IsTypeMenuOpen);

        window.NoiseTab.TypeItem(NoiseType.Solid).RaiseEvent(ClickArgs);
        Settle();
        Assert.False(window.NoiseTab.IsTypeMenuOpen);
        var layer = window.BuildDocument().Layers!.Single();
        Assert.Equal(NoiseType.Solid, layer.Noise!.Type);
        Assert.True(window.NoiseTab.SolidColorRow.IsVisible);
        Assert.DoesNotContain("*", window.Title);
        Assert.False(window.Top.UndoButton.IsEnabled);
        CloseWindow(window);
    });

    [Fact]
    public Task NewStart_Cancelled_LeavesAnEmptyPaintLayer() => ui.Run(() =>
    {
        var window = OpenWithoutChoosing();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle();
        Assert.False(window.NoiseTab.IsTypeMenuOpen);
        Assert.True(window.NoiseTab.GenerateButton.IsVisible);
        Assert.True(window.Rail.BrushToolButton.IsEnabled);

        // 後から選ぶと、空のレイヤーを置き換える
        window.AddNoiseLayer(NoiseType.Worley);
        Settle();
        Assert.Equal(NoiseType.Worley, window.BuildDocument().Layers!.Single().Noise!.Type);
        CloseWindow(window);
    });

    [Fact]
    public Task SolidColor_IsSavedInTheLayer() => ui.Run(() =>
    {
        var window = Open();
        window.NoiseTab.SelectedType = NoiseType.Solid;
        window.NoiseTab.SolidColorSwatch.Color = Avalonia.Media.Colors.Red;
        Settle();
        var shape = window.BuildDocument().Layers!.Single().Noise!.Shape;
        Assert.Equal((1f, 0f, 0f), (shape.SolidR, shape.SolidG, shape.SolidB));
        Assert.False(window.NoiseTab.SizeField.IsVisible);
        Assert.False(window.NoiseTab.ToneSection.IsVisible);
        window.NoiseTab.SelectedType = NoiseType.Perlin;
        Settle();
        Assert.True(window.NoiseTab.ToneSection.IsVisible);
        CloseWindow(window);
    });

    [Fact]
    public Task ToolsAndBrush_FollowTheLayerKind_WithoutAModeSwitch() => ui.Run(() =>
    {
        var window = Open();
        Assert.False(window.Rail.BrushToolButton.IsEnabled);
        Assert.False(window.Strip.BrushButton.IsVisible);
        window.ExplainWhyNotPainting();
        Assert.Contains("ノイズのレイヤー", window.Status.MessageText.Text);

        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.True(window.Rail.BrushToolButton.IsEnabled);
        Assert.True(window.Strip.BrushButton.IsVisible);
        window.Status.MessageText.Text = "";
        window.ExplainWhyNotPainting();
        Assert.Equal("", window.Status.MessageText.Text);

        window.Rail.EraserToolButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.Equal(UskEffect.Graphics.Painting.PaintTool.Eraser, window.CurrentBrush.Tool);
        Assert.False(window.Strip.BrushColorRow.IsVisible);
        window.Strip.BrushSize = 100000;
        Assert.Equal(UskEffect.Graphics.Painting.BrushSettings.MaxSize, window.CurrentBrush.Size);

        // ノイズのレイヤーへ戻ると、道具は使えない
        var noiseRow = (Border)window.LayerTab.LayerList.Children[1];
        noiseRow.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
            noiseRow, new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true), noiseRow,
            default, 0, new Avalonia.Input.PointerPointProperties(RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        Settle();
        Assert.False(window.Rail.BrushToolButton.IsEnabled);
        CloseWindow(window);
    });

    [Fact]
    public Task Tiling_BelongsToEachLayer() => ui.Run(() =>
    {
        var window = Open();
        var tab = window.NoiseTab;
        tab.TilingRow.X.Value = 3;
        Settle();
        var first = window.BuildDocument().Layers![0];
        Assert.Equal(3f, first.TilingX);

        // 別のレイヤーは別の値を持つ
        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.Equal((1f, 1f, 0f, 0f), tab.ReadTexture());
        tab.TilingRow.X.Value = 5;
        Settle();
        var layers = window.BuildDocument().Layers!;
        Assert.Equal(3f, layers[0].TilingX);
        Assert.Equal(5f, layers[1].TilingX);

        // 表示の設定には入らない
        Assert.Equal(1f, window.BuildDocument().Preview.TilingX);
        CloseWindow(window);
    });

    [Fact]
    public Task ViewStrip_HasTileView_AndOutputToggle_ThatFollowsTheExport() => ui.Run(() =>
    {
        var window = Open();
        var strip = window.Strip;
        strip.ViewSegment.SelectedIndex = 2;
        Settle();
        Assert.Equal(PreviewMode.Tile4x4, strip.Mode);

        // カラーは廃止。アルファと白黒の 2 つで、白黒が既定
        Assert.Equal(2, strip.OutputSegment.ItemCount);
        Assert.Equal(CompositeOutput.Mask, strip.Output);

        strip.Output = CompositeOutput.BlackToAlpha;
        Settle();
        Assert.Equal(CompositeOutput.BlackToAlpha, window.BuildDocument().Export.Output);

        // 保存から戻る
        var saved = window.BuildDocument();
        strip.Output = CompositeOutput.Mask;
        window.ApplyDocument(saved);
        Settle();
        Assert.Equal(CompositeOutput.BlackToAlpha, strip.Output);
        CloseWindow(window);
    });

    [Theory]
    [InlineData(ExportChannels.Gray8, false, CompositeOutput.Mask)]
    [InlineData(ExportChannels.Gray16, false, CompositeOutput.Mask)]
    [InlineData(ExportChannels.RgbaOpaque, false, CompositeOutput.BlackToAlpha)]
    [InlineData(ExportChannels.RgbaSource, false, CompositeOutput.BlackToAlpha)]
    [InlineData(ExportChannels.RgbaWhiteAlpha, false, CompositeOutput.BlackToAlpha)]
    [InlineData(ExportChannels.Gray8, true, CompositeOutput.BlackToAlpha)]
    public Task OldProject_ExportSettings_BecomeTheOutputToggle(ExportChannels channels, bool transparent, CompositeOutput expected) => ui.Run(() =>
    {
        var window = Open();
        window.ApplyDocument(new ProjectDocument { Export = new ExportSettings { Channels = channels, Transparent = transparent } });
        Settle();
        Assert.Equal(expected, window.Strip.Output);
        Assert.Equal(channels == ExportChannels.Gray16 && !transparent, window.BuildDocument().Export.Bit16);
        CloseWindow(window);
    });

    [Fact]
    public Task OldProject_WithAnImage_OpensItAsALayer_AtItsOwnSize() => ui.Run(() =>
    {
        var window = Open();
        window.ApplyDocument(new ProjectDocument
        {
            Image = new ImageData(2, 1, [1, 2, 3, 255, 4, 5, 6, 255]),
            Source = new SourceSettings { Kind = SourceKind.Image, ImageName = "a.png" },
        });
        Settle();

        var document = window.BuildDocument();
        var layer = document.Layers!.Single();
        Assert.Equal("a.png", layer.Name);
        Assert.Null(layer.Noise);
        Assert.True(layer.Visible);
        Assert.Equal(1, layer.Image!.Rgba[0]);
        Assert.Equal(2, document.CanvasWidth);
        Assert.Equal(1, document.CanvasHeight);
        Assert.Null(document.Image);
        CloseWindow(window);
    });

    [Fact]
    public Task Motion_Sections_AreMarkedToAppearSmoothly_AndLayerRowsStayInPlace() => ui.Run(() =>
    {
        var window = Open();
        Assert.True(window.NoiseTab.NoiseControls.Classes.Contains("reveal"));
        Assert.True(window.NoiseTab.PatternSection.Classes.Contains("reveal"));
        Assert.True(window.LayerTab.AddChoiceRow.Classes.Contains("reveal"));

        // 選択を変えても行を作り直さないので、色の遷移が途切れない
        var row = window.LayerTab.LayerList.Children[0];
        window.LayerTab.OpacityField.Value = 0.5;
        Settle();
        Assert.Same(row, window.LayerTab.LayerList.Children[0]);

        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.Same(row, window.LayerTab.LayerList.Children[1]);
        CloseWindow(window);
    });

    private static bool ExportResult(Task<bool> task)
    {
        WaitFor(task);
        return task.Result;
    }

    private static ExportChoice Choice(string folder, string name, CompositeOutput output = CompositeOutput.Mask, bool overwrite = false, bool bit16 = false,
        ExportTint tint = ExportTint.None, ImageFileFormat format = ImageFileFormat.Png) =>
        new(new ExportSettings { Folder = folder, Name = name, Output = output, AllowOverwrite = overwrite, Bit16 = bit16, Tint = tint, TintR = 1, TintG = 0, TintB = 0, Format = format }, 64, null);

    [Fact]
    public Task Export_WritesTheFile_AndShowsABannerWithThePath() => ui.Run(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), $"usk-export-{Guid.NewGuid():N}");
        try
        {
            var window = Open();
            Assert.True(ExportResult(window.RunExportAsync(Choice(dir, "a"), _ => throw new InvalidOperationException("確認は出ないはず"))));
            string path = Path.Combine(dir, "a.png");
            Assert.True(File.Exists(path));
            Assert.Equal(64, UskEffect.Core.Imaging.ImageLoader.Load(path).Width);
            Assert.True(window.ExportBanner.IsVisible);
            Assert.Contains("出力しました", window.ExportBannerText.Text);
            Assert.Contains(path, window.ExportBannerText.Text);
            CloseWindow(window);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Export_AsksBeforeOverwriting_UnlessAllowed() => ui.Run(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), $"usk-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "a.png");
            File.WriteAllBytes(path, [1, 2, 3]);
            var window = Open();
            int asked = 0;

            // 断れば書かない
            Assert.False(ExportResult(window.RunExportAsync(Choice(dir, "a"), _ => { asked++; return Task.FromResult(false); })));
            Assert.Equal(1, asked);
            Assert.Equal(3, new FileInfo(path).Length);

            // 認めれば書く
            Assert.True(ExportResult(window.RunExportAsync(Choice(dir, "a"), _ => { asked++; return Task.FromResult(true); })));
            Assert.Equal(2, asked);
            Assert.True(new FileInfo(path).Length > 3);

            // 上書きを許可していれば聞かない
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.True(ExportResult(window.RunExportAsync(Choice(dir, "a", overwrite: true), _ => { asked++; return Task.FromResult(false); })));
            Assert.Equal(2, asked);
            Assert.True(new FileInfo(path).Length > 3);
            CloseWindow(window);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Export_FollowsTheChosenFormat() => ui.Run(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), $"usk-export-{Guid.NewGuid():N}");
        try
        {
            var window = Open();
            window.NoiseTab.SelectedType = NoiseType.ShapeCircle;
            Settle();

            // 拡張子まで書かれても二重にしない。アルファは RGBA で、黒は透明
            Assert.True(ExportResult(window.RunExportAsync(Choice(dir, "alpha.png", CompositeOutput.BlackToAlpha), _ => Task.FromResult(true))));
            var alpha = UskEffect.Core.Imaging.ImageLoader.Load(Path.Combine(dir, "alpha.png"));
            Assert.Equal(0, alpha.Rgba[3]);
            Assert.Equal(255, alpha.Rgba[(32 * 64 + 32) * 4 + 3]);

            // 色を乗せる
            Assert.True(ExportResult(window.RunExportAsync(Choice(dir, "red", CompositeOutput.BlackToAlpha, tint: ExportTint.Multiply), _ => Task.FromResult(true))));
            var red = UskEffect.Core.Imaging.ImageLoader.Load(Path.Combine(dir, "red.png"));
            int center = (32 * 64 + 32) * 4;
            Assert.Equal(255, red.Rgba[center]);
            Assert.Equal(0, red.Rgba[center + 1]);

            // 白黒は透明にしない。16bit と TGA も出せる
            Assert.True(ExportResult(window.RunExportAsync(Choice(dir, "mask"), _ => Task.FromResult(true))));
            Assert.Equal(255, UskEffect.Core.Imaging.ImageLoader.Load(Path.Combine(dir, "mask.png")).Rgba[3]);
            Assert.True(ExportResult(window.RunExportAsync(Choice(dir, "m16", bit16: true), _ => Task.FromResult(true))));
            Assert.True(File.Exists(Path.Combine(dir, "m16.png")));
            Assert.True(ExportResult(window.RunExportAsync(Choice(dir, "t", format: ImageFileFormat.Tga), _ => Task.FromResult(true))));
            Assert.True(File.Exists(Path.Combine(dir, "t.tga")));
            CloseWindow(window);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task ExportWindow_PutsConfirmWhereTheExportButtonIs_AndShowsOnlyWhatApplies() => ui.Run(() =>
    {
        var initial = new ExportChoice(new ExportSettings { Output = CompositeOutput.Mask, Name = "a", Folder = "C:/x" }, 512, null);
        var rendered = new List<ExportChoice>();
        var window = new ExportWindow(initial, c => { rendered.Add(c); return new ImageData(1, 1, [255, 255, 255, 255]); }, _ => Task.FromResult(true));
        window.Show();
        Settle();

        // 計算した位置と、実際のボタンの中心が一致する
        var button = window.ConfirmButton;
        // 現れる動きの途中でも狂わないよう、配置の値だけで求める
        var center = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
        for (Control? c = button; c is not null and not Window; c = c.Parent as Control)
            center += c.Bounds.Position;
        var origin = ExportWindow.PositionFor(new PixelPoint(1000, 500), 1);
        Assert.Equal(1000, origin.X + (int)Math.Round(center.X));
        Assert.Equal(500, origin.Y + (int)Math.Round(center.Y));

        Assert.NotEmpty(rendered);
        Assert.True(window.Bit16Row.IsVisible);
        Assert.False(window.TintRow.IsVisible);

        window.OutputSegment.SelectedIndex = 0;
        Settle();
        Assert.False(window.Bit16Row.IsVisible);
        Assert.True(window.TintRow.IsVisible);
        Assert.False(window.TintSwatch.IsVisible);
        window.TintSegment.SelectedIndex = 1;
        Settle();
        Assert.True(window.TintSwatch.IsVisible);
        Assert.Equal(ExportTint.Multiply, window.Read().Settings.Tint);

        window.ResolutionBox.SelectedIndex = 2;
        Settle();
        Assert.Equal(1024, rendered[^1].Resolution);

        // 16bit は PNG だけ。名前がなければ出力できない
        window.OutputSegment.SelectedIndex = 1;
        window.FormatSegment.SelectedIndex = 1;
        Assert.Equal(ImageFileFormat.Tga, window.Read().Settings.Format);
        window.Bit16Switch.IsChecked = true;
        Settle();
        Assert.Equal(ImageFileFormat.Png, window.Read().Settings.Format);
        Assert.False(window.TgaItem.IsEnabled);
        window.NameBox.Text = " ";
        Settle();
        Assert.False(window.ConfirmButton.IsEnabled);
        window.Close();
    });

    [Fact]
    public Task ExportWindow_ConfirmClosesOnlyWhenTheExportSucceeds() => ui.Run(() =>
    {
        bool succeed = false;
        var initial = new ExportChoice(new ExportSettings { Name = "a", Folder = "C:/x", Output = CompositeOutput.Mask }, 512, null);
        var window = new ExportWindow(initial, _ => null, _ => Task.FromResult(succeed));
        window.Show();
        Settle();
        window.ConfirmButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.False(window.Confirmed);
        Assert.True(window.IsVisible);

        succeed = true;
        window.ConfirmButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.True(window.Confirmed);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public Task BrushSettings_CloseOnCanvasClick_AndNoClearButton() => ui.Run(() =>
    {
        var window = Open();
        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        ClickAt(window, CenterOf(window.Strip.BrushButton, window));
        Assert.True(window.Strip.IsPopupOpen);

        var input = (UskEffect.App.Interop.IHostWindowInput)window.Viewport;
        input.OnPointerDown(0, 10, 10);
        input.OnPointerUp(0, 10, 10);
        Settle();
        Assert.False(window.Strip.IsPopupOpen);

        // 背景を透過し、大きいプレビューだけ背景を持つ
        Assert.False(window.Strip.BrushButtonPreview.ShowBackdrop);
        Assert.True(window.Strip.BrushLargePreview.ShowBackdrop);
        CloseWindow(window);
    });

    [Fact]
    public Task BrushSettings_CloseOnEscape() => ui.Run(() =>
    {
        var window = Open();
        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        ClickAt(window, CenterOf(window.Strip.BrushButton, window));
        Assert.True(window.Strip.IsPopupOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle();
        Assert.False(window.Strip.IsPopupOpen);
        CloseWindow(window);
    });

    // 閉じたポップアップの入力欄にフォーカスが残っていても、元に戻すは効く
    [Fact]
    public Task Undo_WorksWhilePopupIsOpen_AndAfterItCloses() => ui.Run(() =>
    {
        var window = Open();
        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.Equal(3, window.BuildDocument().Layers!.Count);

        // 開いたまま
        ClickAt(window, CenterOf(window.Strip.BrushButton, window));
        Assert.True(window.Strip.IsPopupOpen);
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Settle();
        Assert.Equal(2, window.BuildDocument().Layers!.Count);

        // 入力欄にフォーカスを残して閉じた後
        window.Strip.BrushSizeField.BeginEdit();
        Settle();
        window.Strip.BrushSizeField.EndEdit(commit: false);
        window.Strip.ClosePopups();
        Settle();
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Settle();
        Assert.Single(window.BuildDocument().Layers!);
        CloseWindow(window);
    });

    [Fact]
    public Task SliderDragging_TurnsOffItsTransitions() => ui.Run(() =>
    {
        var window = Open();
        var field = window.NoiseTab.PersistenceField;
        var center = CenterOf(field, window);
        Assert.False(field.Classes.Contains("dragging"));

        window.MouseDown(center, MouseButton.Left);
        window.MouseMove(center + new Point(20, 0));
        Settle();
        Assert.True(field.Classes.Contains("dragging"));

        window.MouseUp(center + new Point(20, 0), MouseButton.Left);
        Settle();
        Assert.False(field.Classes.Contains("dragging"));
        CloseWindow(window);
    });

    [Fact]
    public Task LayerPopup_KeepsItsHeight_WhenChoosingAndAdding() => ui.Run(() =>
    {
        var window = Open();
        ClickAt(window, CenterOf(window.Rail.LayerButton, window));
        var popup = window.LayerTab;
        double height = popup.Bounds.Height;
        Assert.True(height > 0);

        popup.AddButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.True(popup.AddChoiceRow.IsVisible);
        Assert.False(popup.MoveButtons.IsVisible);
        Assert.Equal(height, popup.Bounds.Height);

        popup.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.True(popup.MoveButtons.IsVisible);
        Assert.Equal(height, popup.Bounds.Height);
        window.Rail.CloseLayers();
        CloseWindow(window);
    });

    [Fact]
    public Task FirstLayer_IsAnOrdinaryNoiseLayer_ThatCanBeEditedAndDeleted() => ui.Run(() =>
    {
        var window = Open();
        var tab = window.NoiseTab;
        var first = window.BuildDocument().Layers!.Single();
        Assert.NotNull(first.Noise);
        Assert.True(tab.NoiseControls.IsVisible);
        Assert.False(tab.GenerateButton.IsVisible);

        tab.FrequencyField.Value = 11;
        Settle();
        Assert.Equal(11, window.BuildDocument().Layers![0].Noise!.Frequency);

        // ほかのレイヤーと同じように動かして消せる
        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.True(window.LayerTab.MoveDownButton.IsEnabled);
        window.LayerTab.MoveDownButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.Null(window.BuildDocument().Layers![0].Noise);
        Assert.NotNull(window.BuildDocument().Layers![1].Noise);
        window.LayerTab.DeleteButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.NotNull(window.BuildDocument().Layers!.Single().Noise);
        CloseWindow(window);
    });

    [Fact]
    public Task AddButton_OffersNoiseOrPaint_AndPaintLayerHasNoNoiseControls() => ui.Run(() =>
    {
        var window = Open();
        var tab = window.NoiseTab;
        Assert.False(window.LayerTab.AddChoiceRow.IsVisible);
        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.True(window.LayerTab.AddChoiceRow.IsVisible);

        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.False(window.LayerTab.AddChoiceRow.IsVisible);
        Assert.Equal("2", window.Rail.LayerNumberText.Text);
        Assert.Null(window.BuildDocument().Layers![^1].Noise);
        Assert.False(tab.NoiseControls.IsVisible);
        Assert.True(tab.GenerateButton.IsVisible);
        CloseWindow(window);
    });

    [Fact]
    public Task TextureGenerate_PicksATypeThenCreates_AndEscapeCancels() => ui.Run(() =>
    {
        var window = Open(1280, 900);
        var tab = window.NoiseTab;
        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddPaintButton.RaiseEvent(ClickArgs);
        Settle();
        int before = window.BuildDocument().Layers!.Count;

        // Esc で取り消すと、レイヤーは増えない
        ClickAt(window, CenterOf(tab.GenerateButton, window));
        Assert.True(tab.IsTypeMenuOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle();
        Assert.False(tab.IsTypeMenuOpen);
        Assert.Equal(before, window.BuildDocument().Layers!.Count);

        // 別の場所のクリックでも取り消す
        ClickAt(window, CenterOf(tab.GenerateButton, window));
        Assert.True(tab.IsTypeMenuOpen);
        ClickAt(window, CenterOf(window.Status, window));
        Assert.False(tab.IsTypeMenuOpen);
        Assert.Equal(before, window.BuildDocument().Layers!.Count);

        // 種類を選ぶと生成する
        ClickAt(window, CenterOf(tab.GenerateButton, window));
        tab.TypeItem(NoiseType.ShapeRing).RaiseEvent(ClickArgs);
        Settle();
        var layers = window.BuildDocument().Layers!;
        Assert.Equal(before + 1, layers.Count);
        Assert.Equal(NoiseType.ShapeRing, layers[^1].Noise!.Type);
        Assert.True(tab.NoiseControls.IsVisible);
        Assert.Equal(NoiseType.ShapeRing, tab.SelectedType);

        // 生成の後の種類の変更は、そのレイヤーを変えるだけ
        tab.SelectedType = NoiseType.ShapeCircle;
        Settle();
        Assert.Equal(before + 1, window.BuildDocument().Layers!.Count);
        Assert.Equal(NoiseType.ShapeCircle, window.BuildDocument().Layers![^1].Noise!.Type);
        CloseWindow(window);
    });

    [Fact]
    public Task AddNoiseFromLayerPopup_OpensTheTypeMenu() => ui.Run(() =>
    {
        var window = Open(1280, 900);
        ClickAt(window, CenterOf(window.Rail.LayerButton, window));
        window.LayerTab.AddButton.RaiseEvent(ClickArgs);
        window.LayerTab.AddNoiseButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.False(window.Rail.IsLayerOpen);
        Assert.True(window.NoiseTab.IsTypeMenuOpen);
        window.NoiseTab.CloseTypeMenu();
        Settle();
        Assert.Single(window.BuildDocument().Layers!);
        CloseWindow(window);
    });

    [Fact]
    public Task LayerToggles_SetInvertAndMask_AndBlendIsSaved() => ui.Run(() =>
    {
        var window = Open();
        var row = (Grid)((Border)window.LayerTab.LayerList.Children[0]).Child!;
        var toggles = row.Children.OfType<Button>().Where(b => Grid.GetColumn(b) >= 3).OrderBy(Grid.GetColumn).ToArray();
        Assert.Equal(2, toggles.Length);

        toggles[0].RaiseEvent(ClickArgs);
        Settle();
        Assert.True(window.BuildDocument().Layers![0].Invert);

        row = (Grid)((Border)window.LayerTab.LayerList.Children[0]).Child!;
        toggles = row.Children.OfType<Button>().Where(b => Grid.GetColumn(b) >= 3).OrderBy(Grid.GetColumn).ToArray();
        Assert.True(toggles[0].Classes.Contains("on"));
        toggles[1].RaiseEvent(ClickArgs);
        Settle();
        Assert.True(window.BuildDocument().Layers![0].IsMask);
        Assert.False(window.LayerTab.BlendBox.IsEnabled);

        window.Top.UndoButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.False(window.BuildDocument().Layers![0].IsMask);
        Assert.True(window.LayerTab.BlendBox.IsEnabled);

        window.LayerTab.BlendBox.SelectedIndex = (int)UskEffect.Graphics.Painting.LayerBlendMode.Multiply;
        Settle();
        Assert.Equal(UskEffect.Graphics.Painting.LayerBlendMode.Multiply, window.BuildDocument().Layers![0].Blend);
        CloseWindow(window);
    });

    [Fact]
    public Task LayerRows_ShowThumbnails() => ui.Run(() =>
    {
        var window = Open();
        int id = window.BuildDocument().Layers!.Count; // 最初のレイヤーの番号は 1
        window.LayerTab.ShowThumbnails([new UskEffect.Graphics.Painting.LayerThumbnail(id, 2, new byte[16])]);
        Settle();

        var row = (Border)window.LayerTab.LayerList.Children[0];
        Assert.NotNull(((Grid)row.Child!).Children.OfType<Image>().Single().Source);
        CloseWindow(window);
    });

    [Fact]
    public Task OldProject_GetsItsNoiseAsTheBottomLayer_WithInvertOnTheLayer() => ui.Run(() =>
    {
        var window = Open();
        window.ApplyDocument(new ProjectDocument
        {
            Noise = new NoiseSettings { Type = NoiseType.Worley, Frequency = 7, Invert = true },
            Layers = [new UskEffect.Graphics.Painting.LayerData { Name = "上" }],
        });
        Settle();

        var layers = window.BuildDocument().Layers!;
        Assert.Equal(2, layers.Count);
        Assert.Equal(NoiseType.Worley, layers[0].Noise!.Type);
        Assert.Equal(7, layers[0].Noise!.Frequency);
        Assert.False(layers[0].Noise!.Invert);
        Assert.True(layers[0].Invert);
        Assert.Null(layers[1].Noise);

        // 新しい形式で保存したものは、そのまま開く
        window.ApplyDocument(window.BuildDocument());
        Settle();
        Assert.Equal(2, window.BuildDocument().Layers!.Count);
        CloseWindow(window);
    });

    [Fact]
    public Task BrushButton_ShowsAPreview_AndThePopupScalesItsSize() => ui.Run(() =>
    {
        var window = Open();
        var strip = window.Strip;
        strip.BrushHardnessField.Value = 0.3;
        strip.BrushColorSwatch.Color = Avalonia.Media.Colors.Red;
        Settle();
        // 動きの途中の値ではなく、行き先を確かめる
        Assert.Equal(0.3, strip.BrushButtonPreview.GetBaseValue(BrushPreview.HardnessProperty).GetValueOrDefault(), 3);
        Assert.Equal(Avalonia.Media.Colors.Red, strip.BrushButtonPreview.GetBaseValue(BrushPreview.BrushColorProperty).GetValueOrDefault());
        Assert.Equal(Avalonia.Media.Colors.Red, strip.BrushLargePreview.GetBaseValue(BrushPreview.BrushColorProperty).GetValueOrDefault());

        // 大きさは枠に収まるよう正規化する。実寸ではない
        strip.BrushSizeField.Value = strip.BrushSizeField.Minimum;
        double smallest = strip.BrushLargePreview.GetBaseValue(BrushPreview.SizeRatioProperty).GetValueOrDefault();
        strip.BrushSizeField.Value = strip.BrushSizeField.Maximum;
        Assert.Equal(1, strip.BrushLargePreview.GetBaseValue(BrushPreview.SizeRatioProperty).GetValueOrDefault(), 6);
        Assert.InRange(smallest, 0.01, 0.3);

        // 1 回で反転する
        strip.BrushColorSwatch.Color = Avalonia.Media.Colors.White;
        strip.BrushInvertButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.Equal(Avalonia.Media.Colors.Black, strip.BrushColorSwatch.Color);
        strip.BrushInvertButton.RaiseEvent(ClickArgs);
        Settle();
        Assert.Equal(Avalonia.Media.Colors.White, strip.BrushColorSwatch.Color);
        CloseWindow(window);
    });

    [Fact]
    public Task TypeIcon_IsCircular_WithABlurredEdge() => ui.Run(() =>
    {
        var bitmap = UskEffect.App.Views.NoisePanel.ToBitmap(Enumerable.Repeat(1f, 64 * 64).ToArray());
        Assert.Equal(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        using var frame = bitmap.Lock();
        byte Alpha(int x, int y) => System.Runtime.InteropServices.Marshal.ReadByte(frame.Address, y * frame.RowBytes + x * 4 + 3);
        Assert.Equal(255, Alpha(32, 32));
        Assert.Equal(0, Alpha(0, 0));
        Assert.Equal(0, Alpha(63, 63));
        Assert.InRange(Alpha(32, 7), 1, 254);
        Assert.Equal(Alpha(32, 7), Alpha(7, 32));
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
    public Task WorleyDistance_ShowsOnlyForWorley() => ui.Run(() =>
    {
        var window = Open();
        Assert.False(window.NoiseTab.WorleyRow.IsVisible);
        window.NoiseTab.SelectedType = NoiseType.Worley;
        Assert.True(window.NoiseTab.WorleyRow.IsVisible);
        CloseWindow(window);
    });
}
