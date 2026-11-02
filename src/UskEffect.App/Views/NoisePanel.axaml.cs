using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using UskEffect.App.Locale;
using UskEffect.App.Presets;
using UskEffect.Graphics;

namespace UskEffect.App.Views;

public partial class NoisePanel : UserControl
{
    private static readonly int[] Resolutions = [256, 512, 1024, 2048];

    private readonly NoisePresetStore _presets = new();
    private bool _suppress;

    /// <summary>ノイズの設定が変わったときに発生する。</summary>
    public event Action? Changed;

    public NoisePanel()
    {
        InitializeComponent();

        foreach (RangeBase field in new RangeBase[] { FrequencyField, OctavesField, PersistenceField, LacunarityField, WarpField, ContrastField, BrightnessField })
            field.ValueChanged += (_, _) => RaiseChanged();
        foreach (SelectingItemsControl selector in new SelectingItemsControl[] { TypeSegment, FractalSegment, WorleySegment, SeamSegment, ResolutionBox })
            selector.SelectionChanged += (_, _) => RaiseChanged();
        SeedField.ValueChanged += RaiseChanged;
        InvertSwitch.IsCheckedChanged += (_, _) => RaiseChanged();

        PresetBox.SelectionChanged += (_, _) => OnPresetSelected();
        SavePresetButton.Click += (_, _) => SavePreset();
        DeletePresetButton.Click += (_, _) => DeletePreset();
        RefreshPresetList(null);
        UpdateEnabled();
    }

    public NoiseSettings Read() => new()
    {
        Type = (NoiseType)Math.Max(0, TypeSegment.SelectedIndex),
        Resolution = Resolutions[Math.Clamp(ResolutionBox.SelectedIndex, 0, Resolutions.Length - 1)],
        Seed = (uint)SeedField.Value,
        Frequency = (int)FrequencyField.Value,
        Octaves = (int)OctavesField.Value,
        Persistence = (float)PersistenceField.Value,
        Seam = (SeamMode)Math.Max(0, SeamSegment.SelectedIndex),
        Lacunarity = (int)LacunarityField.Value,
        Fractal = (FractalMode)Math.Max(0, FractalSegment.SelectedIndex),
        Worley = (WorleyMode)Math.Max(0, WorleySegment.SelectedIndex),
        WarpStrength = (float)WarpField.Value,
        Contrast = (float)ContrastField.Value,
        Brightness = (float)BrightnessField.Value,
        Invert = InvertSwitch.IsChecked == true,
    };

    /// <summary>設定を反映する。解像度は出力設定なので、プリセットでは変えない (includeResolution = false)。</summary>
    public void Write(NoiseSettings s, bool includeResolution)
    {
        _suppress = true;
        try
        {
            TypeSegment.SelectedIndex = (int)s.Type;
            SeedField.Value = s.Seed;
            FrequencyField.Value = s.Frequency;
            OctavesField.Value = s.Octaves;
            PersistenceField.Value = s.Persistence;
            SeamSegment.SelectedIndex = (int)s.Seam;
            LacunarityField.Value = s.Lacunarity;
            FractalSegment.SelectedIndex = (int)s.Fractal;
            WorleySegment.SelectedIndex = (int)s.Worley;
            WarpField.Value = s.WarpStrength;
            ContrastField.Value = s.Contrast;
            BrightnessField.Value = s.Brightness;
            InvertSwitch.IsChecked = s.Invert;
            if (includeResolution)
            {
                int index = Array.IndexOf(Resolutions, s.Resolution);
                ResolutionBox.SelectedIndex = index >= 0 ? index : Array.IndexOf(Resolutions, 512);
                PresetBox.SelectedIndex = -1;
            }
        }
        finally
        {
            _suppress = false;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_suppress)
            return;
        UpdateEnabled();
        Changed?.Invoke();
    }

    private void UpdateEnabled() => WorleySegment.IsEnabled = TypeSegment.SelectedIndex == (int)NoiseType.Worley;

    private void RefreshPresetList(string? selectName)
    {
        var presets = _presets.All;
        _suppress = true;
        try
        {
            PresetBox.ItemsSource = presets.Select(p => p.DisplayName).ToList();
            PresetBox.SelectedIndex = selectName is null ? -1 : presets.ToList().FindIndex(p => p.Name == selectName);
        }
        finally
        {
            _suppress = false;
        }
        UpdatePresetButtons();
    }

    private NoisePreset? SelectedPreset =>
        PresetBox.SelectedIndex is int i && i >= 0 && i < _presets.All.Count ? _presets.All[i] : null;

    private void OnPresetSelected()
    {
        UpdatePresetButtons();
        if (_suppress || SelectedPreset is not { } preset)
            return;
        PresetNameBox.Text = preset.IsBuiltIn ? "" : preset.Name;
        Write(preset.Settings, includeResolution: false);
    }

    private void UpdatePresetButtons() => DeletePresetButton.IsEnabled = SelectedPreset is { IsBuiltIn: false };

    private void ShowPresetMessage(string message)
    {
        PresetMessage.Text = message;
        PresetMessage.IsVisible = true;
    }

    private void SavePreset()
    {
        string name = (PresetNameBox.Text ?? "").Trim();
        try
        {
            _presets.Save(name, Read());
            RefreshPresetList(name);
            ShowPresetMessage(Loc.F("S.Noise.PresetSaved", name));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowPresetMessage(Loc.F("S.Noise.PresetSaveFailed", ex.Message));
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
            ShowPresetMessage(Loc.F("S.Noise.PresetDeleted", preset.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowPresetMessage(Loc.F("S.Noise.PresetDeleteFailed", ex.Message));
        }
    }
}
