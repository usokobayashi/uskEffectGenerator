using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UskEffect.App.Controls;
using UskEffect.Core.Export;
using UskEffect.Core.Project;
using UskEffect.Graphics;

namespace UskEffect.App;

/// <summary>プロジェクトの新規・開く・保存 (FR-08) と画像の書き出し (FR-07)。</summary>
public partial class MainWindow
{
    private static readonly FilePickerFileType ProjectFileType = new("uskEffect プロジェクト") { Patterns = ["*" + ProjectFile.Extension] };
    private static readonly FilePickerFileType PngFileType = new("PNG 画像") { Patterns = ["*.png"] };
    private static readonly FilePickerFileType TgaFileType = new("TGA 画像") { Patterns = ["*.tga"] };

    private string? _projectPath;
    private bool _dirty;
    private bool _closeConfirmed;
    private bool _closePromptOpen;

    private string DocumentName => _projectPath is null ? "無題" : Path.GetFileName(_projectPath);

    private void SetUpProjectCommands()
    {
        NewMenuItem.Click += async (_, _) => await NewProjectAsync();
        OpenMenuItem.Click += async (_, _) => await OpenProjectAsync();
        SaveMenuItem.Click += async (_, _) => await SaveProjectAsync(saveAs: false);
        SaveAsMenuItem.Click += async (_, _) => await SaveProjectAsync(saveAs: true);
        ExportMenuItem.Click += async (_, _) => await ExportImageAsync();
        ExitMenuItem.Click += (_, _) => Close();
        ExportChannelsBox.SelectionChanged += (_, _) => MarkDirty();

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

    private void MarkDirty()
    {
        if (_suppressApply || _dirty)
            return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle() => Title = $"{DocumentName}{(_dirty ? " *" : "")} - uskEffect";

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
            Title = "プロジェクトを開く",
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
            await Dialogs.ShowMessageAsync(this, "開けません", $"{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}");
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
                Title = "プロジェクトを保存",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_projectPath) ?? "無題",
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
            await Dialogs.ShowMessageAsync(this, "保存できません", $"{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return false;
        }

        _projectPath = path;
        _dirty = false;
        UpdateTitle();
        return true;
    }

    private async Task ExportImageAsync()
    {
        if (_engine is null)
            return;

        var channels = (ExportChannels)Math.Max(0, ExportChannelsBox.SelectedIndex);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "画像を書き出し",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_projectPath) ?? "noise",
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
                throw new NotSupportedException("グレースケール 16bit は PNG でのみ書き出せます。");

            var (pixels, width, height) = _engine.ReadNoisePixels();
            TextureExporter.Save(path, pixels, width, height, channels);
            StatusText.Text = $"書き出しました: {path}";
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, "書き出せません", ex.Message);
        }
    }

    private ProjectDocument BuildDocument() => new()
    {
        Noise = ReadNoiseSettings(),
        Preview = _engine?.Preview ?? new PreviewSettings(),
        Effect = EffectSettingsPanel.Read(),
        Timeline = new TimelineSettings
        {
            Duration = (double)(DurationBox.Value ?? 2m),
            Loop = LoopBox.IsChecked == true,
        },
        Export = new ExportSettings { Channels = (ExportChannels)Math.Max(0, ExportChannelsBox.SelectedIndex) },
    };

    private void ApplyDocument(ProjectDocument document)
    {
        _suppressApply = true;
        try
        {
            WriteNoiseControls(document.Noise);
            int resolution = Array.IndexOf(Resolutions, document.Noise.Resolution);
            ResolutionBox.SelectedIndex = resolution >= 0 ? resolution : Array.IndexOf(Resolutions, 512);
            WritePreviewControls(document.Preview);
            EffectSettingsPanel.Write(document.Effect);
            DurationBox.Value = (decimal)document.Timeline.Duration;
            LoopBox.IsChecked = document.Timeline.Loop;
            ExportChannelsBox.SelectedIndex = (int)document.Export.Channels;
            PresetBox.SelectedIndex = -1;
        }
        finally
        {
            _suppressApply = false;
        }

        _engine?.Timeline.Stop();
        ApplySettings();
    }

    private void WritePreviewControls(PreviewSettings p)
    {
        ViewModeBox.SelectedIndex = (int)p.Mode;
        ScrollUSlider.Value = p.ScrollSpeedU;
        ScrollVSlider.Value = p.ScrollSpeedV;
        InnerRadiusSlider.Value = p.Polar.InnerRadius;
        OuterRadiusSlider.Value = p.Polar.OuterRadius;
        RotationSlider.Value = p.Polar.RotationDegrees;
        FlipAngularBox.IsChecked = p.Polar.FlipAngular;
        FlipRadialBox.IsChecked = p.Polar.FlipRadial;
        HeatmapBox.SelectedIndex = (int)p.Heatmap;
        HeatThresholdSlider.Value = p.HeatThreshold;
    }
}
