using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UskEffect.App.Controls;
using UskEffect.App.Locale;
using UskEffect.Core.Export;
using UskEffect.Core.Imaging;
using UskEffect.App.Views;
using UskEffect.Core.Project;
using UskEffect.Graphics;
using UskEffect.Graphics.Painting;

namespace UskEffect.App;

/// <summary>プロジェクトの新規・開く・保存 (FR-08) と画像の書き出し (FR-07)。</summary>
public partial class MainWindow
{
    private static FilePickerFileType ProjectFileType => new(Loc.T("S.FileType.Project")) { Patterns = ["*" + ProjectFile.Extension] };
    private static FilePickerFileType PngFileType => new(Loc.T("S.FileType.Png")) { Patterns = ["*.png"] };
    private static FilePickerFileType TgaFileType => new(Loc.T("S.FileType.Tga")) { Patterns = ["*.tga"] };

    private string? _projectPath;
    private ExportSettings _exportSettings = new();
    private ExportWindow? _exportWindow;
    private bool _exportWindowOpen;
    private Avalonia.Threading.DispatcherTimer? _bannerTimer;
    private bool _dirty;
    private bool _closeConfirmed;
    private bool _closePromptOpen;

    private string DocumentName => _projectPath is null ? Loc.T("S.App.Untitled") : Path.GetFileName(_projectPath);

    private void SetUpProjectCommands()
    {
        Top.NewMenuItem.Click += async (_, _) => await NewProjectAsync();
        Top.OpenMenuItem.Click += async (_, _) => await OpenAsync();
        Top.OpenButton.Click += async (_, _) => await OpenAsync();
        Top.SaveMenuItem.Click += async (_, _) => await SaveProjectAsync(saveAs: false);
        Top.SaveButton.Click += async (_, _) => await SaveProjectAsync(saveAs: false);
        Top.SaveAsMenuItem.Click += async (_, _) => await SaveProjectAsync(saveAs: true);
        Top.ExportMenuItem.Click += async (_, _) => await ShowExportWindowAsync();
        Top.ExportButton.Click += async (_, _) => await ShowExportWindowAsync();
        ExportBanner.PointerPressed += (_, _) => ExportBanner.IsVisible = false;
        Top.ExitMenuItem.Click += (_, _) => Close();
        Top.AboutMenuItem.Click += async (_, _) =>
            await Dialogs.ShowMessageAsync(this, Loc.T("S.App.Name"), Loc.T("S.About.Body"));

        Closing += async (_, e) =>
        {
            if (_closeConfirmed || !_dirty)
                return;

            // 確認ダイアログは非同期なので、いったん閉じるのを取り消して答えを待つ。
            // 確認中に閉じる要求が重なっても (× の二度押しなど) ダイアログを増やさない
            e.Cancel = true;
            if (_closePromptOpen)
                return;

            _closePromptOpen = true;
            try
            {
                if (!await ConfirmDiscardOrSaveAsync())
                    return;
            }
            finally
            {
                _closePromptOpen = false;
            }
            _closeConfirmed = true;
            Close();
        };

        _dirty = false;
        UpdateTitle();
    }

    /// <summary>未保存の確認を出さずに閉じる。</summary>
    internal void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        Close();
    }

    private void MarkDirty()
    {
        if (_suppressApply || _dirty)
            return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string name = $"{DocumentName}{(_dirty ? " *" : "")}";
        Title = $"{name} - {Loc.T("S.App.Name")}";
        Top.ProjectNameText.Text = name;
        ToolTip.SetTip(Top.ProjectNameText, _projectPath ?? name);
    }

    /// <summary>未保存の変更があれば保存するか尋ねる。続けてよければ true。</summary>
    private async Task<bool> ConfirmDiscardOrSaveAsync()
    {
        if (!_dirty)
            return true;

        return await Dialogs.AskSaveChangesAsync(this, DocumentName) switch
        {
            UnsavedChoice.Save => await SaveProjectAsync(saveAs: false),
            UnsavedChoice.Discard => true,
            _ => false,
        };
    }

    private static FilePickerFileType ImageFileType =>
        new(Loc.T("S.FileType.Images")) { Patterns = ImageLoader.Extensions.Select(e => "*" + e).ToArray() };

    private static FilePickerFileType OpenableFileType =>
        new(Loc.T("S.FileType.Openable")) { Patterns = [.. ProjectFileType.Patterns!, .. ImageFileType.Patterns!] };

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("S.Project.OpenTitle"),
            AllowMultiple = false,
            FileTypeFilter = [OpenableFileType, ProjectFileType, ImageFileType],
        });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
            return;

        await OpenFileAsync(path);
    }

    /// <summary>プロジェクトは今の内容と置き換え、画像は今のプロジェクトのレイヤーにする。</summary>
    internal async Task OpenFileAsync(string path)
    {
        if (ProjectFile.IsProjectPath(path))
        {
            // 画像なら今の内容を残すので、確認はプロジェクトを開くときだけ出す
            if (await ConfirmDiscardOrSaveAsync())
                await OpenProjectFileAsync(path);
            return;
        }

        await LoadImageFileAsync(path);
    }

    private async Task PickImageLayerAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("S.Project.OpenTitle"),
            AllowMultiple = false,
            FileTypeFilter = [ImageFileType],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await LoadImageFileAsync(path);
    }

    // 画像をレイヤーとして追加する。始めたばかりならキャンバスを画像に合わせ、そうでなければ縦横比を保って収める
    private async Task LoadImageFileAsync(string path)
    {
        if (_engine is null)
            return;
        try
        {
            // 大きな画像でも画面を止めない
            var image = await Task.Run(() => ImageLoader.Load(path));
            if ((_engine.IsPristine || _engine.Layers is [{ Noise: not null }]) && NoiseTab.CustomSize is null)
                FitCanvasTo(image);

            // 白黒のままだと画像の色が見えない
            if (Strip.Output == CompositeOutput.Mask)
                Strip.Output = CompositeOutput.BlackToAlpha;

            var (width, height) = NoiseTab.CustomSize ?? (NoiseTab.Resolution, NoiseTab.Resolution);
            var fitted = await Task.Run(() => image.FitInto(width, height));
            ChangeLayers(() => _engine.AddImageLayer(fitted, Path.GetFileName(path)));
        }
        catch (ImageLoadException ex)
        {
            await Dialogs.ShowMessageAsync(this, Loc.T("S.Source.LoadFailed"), $"{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}");
        }
    }

    private void FitCanvasTo(ImageData image)
    {
        if (image.Width == image.Height && image.Width is 256 or 512 or 1024 or 2048)
            NoiseTab.Resolution = image.Width;
        else
            NoiseTab.SetCustomSize(image.Width, image.Height);
        ApplySettings();
    }

    private async Task NewProjectAsync()
    {
        if (!await ConfirmDiscardOrSaveAsync())
            return;

        ApplyDocument(new ProjectDocument { NoiseIsLayer = true });
        ShowStartTypeMenu();
        _projectPath = null;
        _dirty = false;
        UpdateTitle();
    }

    private async Task OpenProjectFileAsync(string path)
    {
        ProjectDocument document;
        try
        {
            document = ProjectFile.Load(path);
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, Loc.T("S.Project.OpenFailed"), $"{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return;
        }

        ApplyDocument(document);
        _projectPath = path;
        _dirty = false;
        UpdateTitle();
    }

    /// <summary>保存できたら true。名前を付けて保存でキャンセルしたら false。</summary>
    private async Task<bool> SaveProjectAsync(bool saveAs)
    {
        string? path = _projectPath;
        if (saveAs || path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T("S.Project.SaveTitle"),
                SuggestedFileName = Path.GetFileNameWithoutExtension(_projectPath) ?? Loc.T("S.App.Untitled"),
                DefaultExtension = ProjectFile.Extension.TrimStart('.'),
                FileTypeChoices = [ProjectFileType],
                ShowOverwritePrompt = true,
            });
            path = file?.TryGetLocalPath();
            if (path is null)
                return false;
        }

        try
        {
            ProjectFile.Save(path, BuildDocument());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, Loc.T("S.Project.SaveFailed"), $"{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return false;
        }

        _projectPath = path;
        _dirty = false;
        UpdateTitle();
        Status.ShowMessage(Loc.F("S.Project.Saved", path));
        return true;
    }

    private async Task ShowExportWindowAsync()
    {
        if (_engine is null || _exportWindowOpen)
            return;

        var before = (Settings: CurrentExportSettings(), NoiseTab.Resolution, NoiseTab.CustomSize);
        var folder = before.Settings.Folder;
        if (string.IsNullOrWhiteSpace(folder))
            folder = (await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads))?.TryGetLocalPath()
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string name = string.IsNullOrWhiteSpace(before.Settings.Name)
            ? Path.GetFileNameWithoutExtension(_projectPath) ?? Loc.T("S.Export.DefaultName")
            : before.Settings.Name;

        var window = new ExportWindow(
            new ExportChoice(before.Settings with { Folder = folder, Name = name }, before.Resolution, before.CustomSize),
            RenderExportPreview, choice => RunExportAsync(choice, path => Dialogs.AskOverwriteAsync(_exportWindow!, path)));

        // 慣れた人が同じ位置をもう一度押すだけで出力できるよう、出力ボタンを書き出しボタンに重ねる
        var button = Top.ExportButton;
        var center = button.PointToScreen(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2));
        window.Position = ExportWindow.PositionFor(center, RenderScaling);

        _exportWindow = window;
        _exportWindowOpen = true;
        try
        {
            await window.ShowDialog(this);
        }
        finally
        {
            _exportWindowOpen = false;
            _exportWindow = null;
        }

        // 取り消したら、プレビューのために変えたものを戻す
        if (!window.Confirmed)
            ApplyExportChoice(new ExportChoice(before.Settings, before.Resolution, before.CustomSize));
    }

    private ExportSettings CurrentExportSettings() => _exportSettings with { Output = Strip.Output };

    private void ApplyExportChoice(ExportChoice choice)
    {
        _exportSettings = choice.Settings;
        if (choice.Settings.Output is { } output)
            Strip.Output = output;
        if (choice.CustomSize is { } size)
            NoiseTab.SetCustomSize(size.Width, size.Height);
        else
            NoiseTab.Resolution = choice.Resolution;
        ApplySettings();
    }

    /// <summary>書き出す画像を作る。プレビューと書き出しで同じ結果にする。</summary>
    internal ImageData? RenderExportPreview(ExportChoice choice)
    {
        ApplyExportChoice(choice);
        if (_engine?.ReadComposite() is not { } image)
            return null;
        var s = choice.Settings;
        return s.Output == CompositeOutput.Mask ? image : TextureExporter.ApplyTint(image, s.Tint, s.TintR, s.TintG, s.TintB);
    }

    /// <summary>出力する。やめたときや失敗したときは false で、ウィンドウは閉じない。</summary>
    internal async Task<bool> RunExportAsync(ExportChoice choice, Func<string, Task<bool>> askOverwrite)
    {
        if (_engine is null)
            return false;
        var s = choice.Settings;
        string name = s.Name ?? "";
        string extension = s.Format == ImageFileFormat.Tga ? ".tga" : ".png";
        // 拡張子まで書かれていても二重にしない
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length];

        try
        {
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException(Loc.T("S.Export.BadName"));

            string path = Path.Combine(s.Folder ?? "", name + extension);
            if (File.Exists(path) && !s.AllowOverwrite && !await askOverwrite(path))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var image = RenderExportPreview(choice);
            if (image is null)
                throw new InvalidOperationException(Loc.T("S.Export.Failed"));

            if (s.Output == CompositeOutput.Mask && s.Bit16)
                TextureExporter.Save(path, _engine.ReadCompositeLuminance(), image.Width, image.Height, ExportChannels.Gray16);
            else
                TextureExporter.SaveImage(path, image, s.Output == CompositeOutput.Mask ? ExportChannels.Gray8 : ExportChannels.RgbaSource);

            MarkDirty();
            ShowExportBanner(path);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            await Dialogs.ShowMessageAsync((Window?)_exportWindow ?? this, Loc.T("S.Export.Failed"), ex.Message);
            return false;
        }
    }

    private void ShowExportBanner(string path)
    {
        ExportBannerText.Text = Loc.F("S.Export.Done", path);
        ToolTip.SetTip(ExportBanner, path);
        ExportBanner.IsVisible = true;
        _bannerTimer?.Stop();
        _bannerTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer?.Stop();
            ExportBanner.IsVisible = false;
        };
        _bannerTimer.Start();
    }

    internal ProjectDocument BuildDocument() => new()
    {
        Noise = _canvasNoise with { Resolution = NoiseTab.Resolution },
        NoiseIsLayer = true,
        Preview = ReadPreviewSettings(),
        Export = CurrentExportSettings(),
        CanvasWidth = NoiseTab.CustomSize?.Width,
        CanvasHeight = NoiseTab.CustomSize?.Height,
        Layers = _engine?.ReadLayers(),
    };

    // 反転はレイヤーの設定へ移した。古いプロジェクトは、ノイズと読み込んだ画像を一番下のレイヤーにする
    private IReadOnlyList<LayerData>? LayersForEditor(ProjectDocument document)
    {
        var layers = (document.Layers ?? [])
            .Select(l => l.Noise is { Invert: true } ? l with { Invert = !l.Invert, Noise = l.Noise with { Invert = false } } : l)
            .ToList();
        bool usesImage = document.Source.Kind == SourceKind.Image && document.Image is not null;
        if (!document.NoiseIsLayer && document.Image is { } image)
        {
            // 使っていなかった画像も捨てず、隠して残す
            layers.Insert(0, new LayerData { Name = document.Source.ImageName ?? Loc.T("S.Layer.AddImage"), Image = image, Visible = usesImage });
        }
        if (!document.NoiseIsLayer && !usesImage)
        {
            layers.Insert(0, new LayerData
            {
                Name = Loc.T("S.Layer.FirstNoiseName"),
                Noise = document.Noise with { Invert = false },
                Invert = document.Noise.Invert,
            });
        }
        // 古いプロジェクトの Tiling は表示の設定だった。今はレイヤーが持つ
        if (!document.NoiseIsLayer)
        {
            bool oldTile = document.Preview.Mode == PreviewMode.Tile3x3;
            float tilingX = oldTile ? 3 : document.Preview.TilingX, tilingY = oldTile ? 3 : document.Preview.TilingY;
            layers = layers.Select(l => l with { TilingX = tilingX, TilingY = tilingY, OffsetX = document.Preview.OffsetX, OffsetY = document.Preview.OffsetY }).ToList();
        }
        return layers.Count > 0 ? layers : null;
    }

    // 古いプロジェクトの書き出し設定を、今のプレビューと書き出しの形へ直す
    private static (CompositeOutput Output, bool Bit16) ReadExport(ExportSettings export)
    {
        // カラーは廃止した。残るのはアルファと白黒
        if (export.Output is { } output)
            return (output == CompositeOutput.Color ? CompositeOutput.BlackToAlpha : output, export.Bit16);
        if (export.Transparent)
            return (CompositeOutput.BlackToAlpha, false);
        return export.Channels switch
        {
            ExportChannels.Gray16 => (CompositeOutput.Mask, true),
            ExportChannels.RgbaWhiteAlpha or ExportChannels.RgbaBlackToAlpha => (CompositeOutput.BlackToAlpha, false),
            ExportChannels.RgbaOpaque or ExportChannels.RgbaSource => (CompositeOutput.BlackToAlpha, false),
            _ => (CompositeOutput.Mask, false),
        };
    }

    internal void ApplyDocument(ProjectDocument document)
    {
        _suppressApply = true;
        try
        {
            _shownNoiseTarget = -1;
            _canvasNoise = document.Noise;
            NoiseTab.Resolution = document.Noise.Resolution;
            // 古いプロジェクトは、読み込んだ画像がキャンバスの大きさを決めていた
            if (!document.NoiseIsLayer && document.Source.Kind == SourceKind.Image && document.Image is { } oldImage)
                NoiseTab.SetCustomSize(oldImage.Width, oldImage.Height);
            else if (document is { CanvasWidth: { } canvasWidth, CanvasHeight: { } canvasHeight })
                NoiseTab.SetCustomSize(canvasWidth, canvasHeight);
            Strip.Mode = document.Preview.Mode;
            Strip.Polar = document.Preview.Polar;
            var (output, bit16) = ReadExport(document.Export);
            Strip.Output = output;
            _exportSettings = document.Export with { Output = output, Bit16 = bit16 };
        }
        finally
        {
            _suppressApply = false;
        }

        if (_engine is not null)
            _engine.View = new CanvasView();
        _engine?.CanvasSize = NoiseTab.CustomSize is { } size ? new CanvasPixelSize(size.Width, size.Height) : null;
        _engine?.SetLayers(LayersForEditor(document));
        ApplySettings();
        RefreshLayers();
    }
}
