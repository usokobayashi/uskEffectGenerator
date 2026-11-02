using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using UskEffect.App.Locale;
using UskEffect.Graphics;

namespace UskEffect.App.Views;

public partial class NoisePanel : UserControl
{
    private static readonly int[] Resolutions = [256, 512, 1024, 2048];

    private bool _suppress;

    /// <summary>ノイズの設定が変わったときに発生する。</summary>
    public event Action? Changed;

    /// <summary>元画像の切り替え (ノイズ／画像) が変わったときに発生する。</summary>
    public event Action? SourceChanged;

    /// <summary>Tiling / Offset が変わったときに発生する。</summary>
    public event Action? TextureChanged;

    public NoisePanel()
    {
        InitializeComponent();

        foreach (RangeBase field in new RangeBase[]
                 {
                     FrequencyField, OctavesField, PersistenceField, LacunarityField, WarpField, ContrastField, BrightnessField,
                     StripeWidthField, StripeSoftnessField,
                 })
            field.ValueChanged += (_, _) => RaiseChanged();
        foreach (SelectingItemsControl selector in new SelectingItemsControl[]
                 {
                     TypeBox, FractalSegment, WorleySegment, SeamSegment, ResolutionBox, StripeDirectionSegment,
                 })
            selector.SelectionChanged += (_, _) => RaiseChanged();
        SeedField.ValueChanged += RaiseChanged;
        RandomSeedButton.Click += (_, _) => SeedField.Value = Random.Shared.Next(0, (int)SeedField.Maximum + 1);
        SourceSegment.SelectionChanged += (_, _) =>
        {
            NoiseControls.IsEnabled = !UseImage;
            if (!_suppress)
                SourceChanged?.Invoke();
        };
        TilingRow.Changed += RaiseTextureChanged;
        OffsetRow.Changed += RaiseTextureChanged;
        SetImageInfo(null);
        InvertSwitch.IsCheckedChanged += (_, _) => RaiseChanged();
        UpdateEnabled();
    }

    /// <summary>読み込んだ画像を表示するか。</summary>
    public bool UseImage
    {
        get => SourceSegment.SelectedIndex == 1;
        set => SourceSegment.SelectedIndex = value ? 1 : 0;
    }

    /// <summary>画像の情報を出す。null なら画像なし。</summary>
    public void SetImageInfo(string? info)
    {
        ImageInfoText.Text = info ?? Loc.T("S.Source.None");
        ToolTip.SetTip(ImageInfoText, info);
        SourceImageItem.IsEnabled = info is not null;
        if (info is null)
            UseImage = false;
    }

    public (float TilingX, float TilingY, float OffsetX, float OffsetY) ReadTexture()
    {
        var (tilingX, tilingY) = TilingRow.Read();
        var (offsetX, offsetY) = OffsetRow.Read();
        return (tilingX, tilingY, offsetX, offsetY);
    }

    public void WriteTexture(float tilingX, float tilingY, float offsetX, float offsetY)
    {
        TilingRow.Write(tilingX, tilingY);
        OffsetRow.Write(offsetX, offsetY);
    }

    public NoiseSettings Read() => new()
    {
        Type = (NoiseType)Math.Max(0, TypeBox.SelectedIndex),
        Resolution = Resolutions[Math.Clamp(ResolutionBox.SelectedIndex, 0, Resolutions.Length - 1)],
        Seed = (uint)SeedField.Value,
        Frequency = (int)FrequencyField.Value,
        Octaves = (int)OctavesField.Value,
        Persistence = (float)PersistenceField.Value,
        Seam = (SeamMode)Math.Max(0, SeamSegment.SelectedIndex),
        Lacunarity = (int)LacunarityField.Value,
        Fractal = (FractalMode)Math.Max(0, FractalSegment.SelectedIndex),
        Worley = (WorleyMode)Math.Max(0, WorleySegment.SelectedIndex),
        StripeDirection = (StripeDirection)Math.Max(0, StripeDirectionSegment.SelectedIndex),
        StripeWidth = (float)StripeWidthField.Value,
        StripeSoftness = (float)StripeSoftnessField.Value,
        WarpStrength = (float)WarpField.Value,
        Contrast = (float)ContrastField.Value,
        Brightness = (float)BrightnessField.Value,
        Invert = InvertSwitch.IsChecked == true,
    };

    public void Write(NoiseSettings s)
    {
        _suppress = true;
        try
        {
            TypeBox.SelectedIndex = (int)s.Type;
            SeedField.Value = s.Seed;
            FrequencyField.SetValueExpanding(s.Frequency);
            OctavesField.Value = s.Octaves;
            PersistenceField.Value = s.Persistence;
            SeamSegment.SelectedIndex = (int)s.Seam;
            LacunarityField.Value = s.Lacunarity;
            FractalSegment.SelectedIndex = (int)s.Fractal;
            WorleySegment.SelectedIndex = (int)s.Worley;
            StripeDirectionSegment.SelectedIndex = (int)s.StripeDirection;
            StripeWidthField.Value = s.StripeWidth;
            StripeSoftnessField.Value = s.StripeSoftness;
            WarpField.SetValueExpanding(s.WarpStrength);
            ContrastField.SetValueExpanding(s.Contrast);
            BrightnessField.Value = s.Brightness;
            InvertSwitch.IsChecked = s.Invert;
            int index = Array.IndexOf(Resolutions, s.Resolution);
            ResolutionBox.SelectedIndex = index >= 0 ? index : Array.IndexOf(Resolutions, 512);
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

    private void RaiseTextureChanged()
    {
        if (!_suppress)
            TextureChanged?.Invoke();
    }

    private void UpdateEnabled()
    {
        WorleySegment.IsEnabled = TypeBox.SelectedIndex == (int)NoiseType.Worley;
        StripeControls.IsEnabled = TypeBox.SelectedIndex == (int)NoiseType.Stripe;
    }
}
