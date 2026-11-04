using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using UskEffect.App.Locale;
using UskEffect.Graphics;
using UskEffect.Graphics.Painting;

namespace UskEffect.App.Views;

public partial class ViewStrip : UserControl
{
    // 表示の切り替えの並び
    private static readonly PreviewMode[] Modes = [PreviewMode.Rect, PreviewMode.Polar, PreviewMode.Tile4x4];

    /// <summary>表示か極座標の設定が変わったときに発生する。</summary>
    public event Action? Changed;

    private PaintTool _tool = PaintTool.Brush;

    public ViewStrip()
    {
        InitializeComponent();

        foreach (RangeBase field in new RangeBase[] { BrushSizeField, BrushHardnessField, BrushOpacityField })
            field.ValueChanged += (_, _) => UpdateBrushPreview();
        BrushColorSwatch.ColorChanged += UpdateBrushPreview;
        BrushInvertButton.Click += (_, _) => InvertBrushColor();
        UpdateBrushPreview();

        ViewSegment.SelectionChanged += (_, _) => Changed?.Invoke();
        OutputSegment.SelectionChanged += (_, _) => Changed?.Invoke();
        foreach (RangeBase field in new RangeBase[] { InnerRadiusField, OuterRadiusField, RotationField })
            field.ValueChanged += (_, _) => OnPolarChanged();
        foreach (ToggleButton toggle in new ToggleButton[] { FlipAngularSwitch, FlipRadialSwitch })
            toggle.IsCheckedChanged += (_, _) => Changed?.Invoke();
        UpdateSummary();
    }

    /// <summary>プレビューと書き出しの形。</summary>
    public CompositeOutput Output
    {
        get => OutputSegment.SelectedIndex == 1 ? CompositeOutput.Mask : CompositeOutput.BlackToAlpha;
        set => OutputSegment.SelectedIndex = value == CompositeOutput.Mask ? 1 : 0;
    }

    public bool IsPopupOpen => BrushButton.Flyout!.IsOpen || PolarButton.Flyout!.IsOpen;

    public void ClosePopups()
    {
        BrushButton.Flyout!.Hide();
        PolarButton.Flyout!.Hide();
    }

    public PreviewMode Mode
    {
        get => Modes[Math.Clamp(ViewSegment.SelectedIndex, 0, Modes.Length - 1)];
        set => ViewSegment.SelectedIndex = Math.Max(Array.IndexOf(Modes, value), 0);
    }

    public PolarSettings Polar
    {
        get
        {
            // 外径は内径より外に保つ
            float inner = (float)InnerRadiusField.Value;
            float outer = Math.Max((float)OuterRadiusField.Value, inner + 0.01f);
            return new PolarSettings(inner, outer, (float)RotationField.Value,
                FlipAngularSwitch.IsChecked == true, FlipRadialSwitch.IsChecked == true);
        }
        set
        {
            InnerRadiusField.Value = value.InnerRadius;
            OuterRadiusField.Value = value.OuterRadius;
            RotationField.Value = value.RotationDegrees;
            FlipAngularSwitch.IsChecked = value.FlipAngular;
            FlipRadialSwitch.IsChecked = value.FlipRadial;
        }
    }

    /// <summary>極座標の設定ボタンを出すか。</summary>
    public bool ShowPolarSettings
    {
        set => PolarButton.IsVisible = value;
    }

    /// <summary>ブラシの設定ボタンを出すか。</summary>
    public bool ShowBrushSettings
    {
        set => BrushButton.IsVisible = value;
    }

    /// <summary>選んでいる道具。色の欄はブラシのときだけ出す。</summary>
    public PaintTool Tool
    {
        set
        {
            _tool = value;
            BrushColorRow.IsVisible = value == PaintTool.Brush;
            UpdateBrushPreview();
        }
    }

    public double BrushSize
    {
        get => BrushSizeField.Value;
        set => BrushSizeField.Value = Math.Round(Math.Clamp(value, BrushSettings.MinSize, BrushSettings.MaxSize));
    }

    public BrushSettings ReadBrush(PaintTool tool)
    {
        Color color = BrushColorSwatch.Color;
        return new BrushSettings
        {
            Tool = tool,
            Size = BrushSizeField.Value,
            Hardness = BrushHardnessField.Value,
            Opacity = BrushOpacityField.Value,
            Color = new System.Numerics.Vector3(color.R / 255f, color.G / 255f, color.B / 255f),
            Wrap = BrushWrapSwitch.IsChecked == true,
            PressureSize = PressureSizeSwitch.IsChecked == true,
            PressureOpacity = PressureOpacitySwitch.IsChecked == true,
        };
    }

    private void UpdateBrushPreview()
    {
        // 消しゴムとぼかしに色はないので白で見せる
        Color color = _tool == PaintTool.Brush ? BrushColorSwatch.Color : Colors.White;
        double size = UskEffect.App.Controls.Ui.BrushPreview.NormalizeSize(BrushSizeField.Value, BrushSizeField.Minimum, BrushSizeField.Maximum);
        foreach (var preview in new[] { BrushButtonPreview, BrushLargePreview })
        {
            preview.BrushColor = color;
            preview.Hardness = BrushHardnessField.Value;
        }
        BrushLargePreview.SizeRatio = size;
        BrushLargePreview.Flow = BrushOpacityField.Value;
    }

    /// <summary>色を反転する。白と黒をすぐ入れ替えられる。</summary>
    private void InvertBrushColor()
    {
        Color color = BrushColorSwatch.Color;
        BrushColorSwatch.Color = Color.FromRgb((byte)(255 - color.R), (byte)(255 - color.G), (byte)(255 - color.B));
    }

    public void SetZoom(double? ratio, double rotationDegrees)
    {
        string zoom = ratio is { } r ? $"{r * 100:0}%" : "";
        ZoomText.Text = ratio is not null && Math.Abs(rotationDegrees) >= 0.5 ? $"{zoom} {rotationDegrees:0}°" : zoom;
    }

    private void OnPolarChanged()
    {
        UpdateSummary();
        Changed?.Invoke();
    }

    private void UpdateSummary() =>
        PolarSummaryText.Text = Loc.F("S.Polar.Summary", InnerRadiusField.Value, OuterRadiusField.Value);
}
