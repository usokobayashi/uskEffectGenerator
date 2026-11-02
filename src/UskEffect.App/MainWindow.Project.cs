using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UskEffect.App.Controls;
using UskEffect.App.Locale;
using UskEffect.Core.Export;
using UskEffect.Core.Project;
using UskEffect.Graphics;

namespace UskEffect.App;

/// <summary>プロジェクトの新規・開く・保存 (FR-08) と画像の書き出し (FR-07)。</summary>
public partial class MainWindow
{
    private static FilePickerFileType ProjectFileType => new(Loc.T("S.FileType.Project")) { Patterns = ["*" + ProjectFile.Extension] };
    private static FilePickerFileType PngFileType => new(Loc.T("S.FileType.Png")) { Patterns = ["*.png"] };
    private static FilePickerFileType TgaFileType => new(Loc.T("S.FileType.Tga")) { Patterns = ["*.tga"] };

    private string? _projectPath;
    private bool _dirty;
    private bool _closeConfirmed;
    private bool _closePromptOpen;

    private string DocumentName => _projectPath is null ? Loc.T("S.App.Untitled") : Path.GetFileName(_projectPath);

    private void SetUpProjectCommands()
    {
        Top.NewMenuItem.Click += async (_, _) => await NewProjectAsync();
        Top.OpenMenuItem.Click += async (_, _) => await OpenProjectAsync();
        Top.OpenButton.Click += async (_, _) => await OpenProjectAsync();
        Top.SaveMenuItem.Click += async (_, _) => await SaveProjectAsync(saveAs: false);
        Top.SaveButton.Click += async (_, _) => await SaveProjectAsync(saveAs: false);
        Top.SaveAsMenuItem.Click += async (_, _) => await SaveProjectAsync(saveAs: true);
        Top.ExportMenuItem.Click += async (_, _) => await ExportImageAsync();
        Top.ExportRunButton.Click += async (_, _) =>
        {
            Top.ExportButton.Flyout?.Hide();
            await ExportImageAsync();
        };
        Top.ExitMenuItem.Click += (_, _) => Close();
        Top.AboutMenuItem.Click += async (_, _) =>
            await Dialogs.ShowMessageAsync(this, Loc.T("S.App.Name"), Loc.T("S.About.Body"));
        Top.ExportChannelsBox.SelectionChanged += (_, _) => MarkDirty();

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

    private async Task NewProjectAsync()
    {
        if (!await ConfirmDiscardOrSaveAsync())
            return;

        ApplyDocument(new ProjectDocument());
        _projectPath = null;
        _dirty = false;
        UpdateTitle();
    }

    private async Task OpenProjectAsync()
    {
        if (!await ConfirmDiscardOrSaveAsync())
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("S.Project.OpenTitle"),
            AllowMultiple = false,
            FileTypeFilter = [ProjectFileType],
        });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
            return;

        await OpenProjectFileAsync(path);
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

    private async Task ExportImageAsync()
    {
        if (_engine is null)
            return;

        var channels = (ExportChannels)Math.Max(0, Top.ExportChannelsBox.SelectedIndex);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("S.Export.Title"),
            SuggestedFileName = Path.GetFileNameWithoutExtension(_projectPath) ?? Loc.T("S.Export.DefaultName"),
            DefaultExtension = "png",
            // 16bit は PNG だけなので TGA を候補に出さない
            FileTypeChoices = channels == ExportChannels.Gray16 ? [PngFileType] : [PngFileType, TgaFileType],
            ShowOverwritePrompt = true,
        });
        string? path = file?.TryGetLocalPath();
        if (path is null)
            return;

        try
        {
            var format = TextureExporter.FormatFromPath(path);
            if (!TextureExporter.IsSupported(format, channels))
                throw new NotSupportedException(Loc.T("S.Export.Gray16PngOnly"));

            var (pixels, width, height) = _engine.ReadNoisePixels();
            TextureExporter.Save(path, pixels, width, height, channels);
            Status.ShowMessage(Loc.F("S.Export.Done", path));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, Loc.T("S.Export.Failed"), ex.Message);
        }
    }

    internal ProjectDocument BuildDocument() => new()
    {
        Noise = NoiseTab.Read(),
        Preview = ReadPreviewSettings(),
        Effect = EffectTab.Read(),
        Timeline = Timeline.Read(),
        Export = new ExportSettings { Channels = (ExportChannels)Math.Max(0, Top.ExportChannelsBox.SelectedIndex) },
    };

    internal void ApplyDocument(ProjectDocument document)
    {
        _suppressApply = true;
        try
        {
            NoiseTab.Write(document.Noise, includeResolution: true);
            Strip.Mode = document.Preview.Mode;
            Strip.Polar = document.Preview.Polar;
            SeamTab.Heatmap = document.Preview.Heatmap;
            SeamTab.Threshold = document.Preview.HeatThreshold;
            EffectTab.Write(document.Effect, document.Preview.ScrollSpeedU, document.Preview.ScrollSpeedV);
            Timeline.Write(document.Timeline);
            Top.ExportChannelsBox.SelectedIndex = (int)document.Export.Channels;
        }
        finally
        {
            _suppressApply = false;
        }

        _engine?.Timeline.Stop();
        ApplySettings();
    }
}
