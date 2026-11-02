using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using UskEffect.App.Controls.Ui;
using UskEffect.Graphics.Effects;

namespace UskEffect.App.Views;

/// <summary>エフェクトの設定 (FR-05 / FR-06)。UV 移動はプレビュー全体の設定だがここに置く。</summary>
public partial class EffectPanel : UserControl
{
    private bool _suppress;

    public event Action? Changed;

    public EffectPanel()
    {
        InitializeComponent();

        foreach (SegmentedControl segment in new[] { BlendSegment, MappingSegment, BackgroundSegment })
            segment.SelectionChanged += (_, _) => RaiseChanged();
        foreach (ToggleButton toggle in new ToggleButton[] { SoftMaskSwitch, TextureAlphaSwitch })
            toggle.IsCheckedChanged += (_, _) => RaiseChanged();
        foreach (RangeBase field in new RangeBase[]
                 {
                     RotationStartField, RotationEndField, ScaleStartField, ScaleEndField,
                     AlphaStartField, AlphaEndField, DissolveStartField, DissolveEndField, EdgeWidthField,
                 })
            field.ValueChanged += (_, _) => RaiseChanged();
        ScrollRow.Changed += RaiseChanged;
        foreach (ColorSwatch swatch in new[] { ColorStartSwatch, ColorEndSwatch, EdgeColorSwatch })
            swatch.ColorChanged += RaiseChanged;
    }

    public float ScrollU => ScrollRow.Read().X;
    public float ScrollV => ScrollRow.Read().Y;

    public EffectSettings Read() => new()
    {
        Mapping = (EffectMapping)Math.Max(0, MappingSegment.SelectedIndex),
        Blend = BlendSegment.SelectedIndex == 0 ? EffectBlend.Alpha : EffectBlend.Additive,
        Background = (EffectBackground)Math.Max(0, BackgroundSegment.SelectedIndex),
        Mask = SoftMaskSwitch.IsChecked == true ? EffectMask.SoftCircle : EffectMask.None,
        TextureAsAlpha = TextureAlphaSwitch.IsChecked == true,
        Rotation = new((float)RotationStartField.Value, (float)RotationEndField.Value),
        Scale = new((float)ScaleStartField.Value, (float)ScaleEndField.Value),
        Alpha = new((float)AlphaStartField.Value, (float)AlphaEndField.Value),
        Dissolve = new((float)DissolveStartField.Value, (float)DissolveEndField.Value),
        DissolveEdgeWidth = (float)EdgeWidthField.Value,
        DissolveEdgeColor = ToVector(EdgeColorSwatch.Color),
        Color = new(ToVector(ColorStartSwatch.Color), ToVector(ColorEndSwatch.Color)),
    };

    public void Write(EffectSettings e, float scrollU, float scrollV)
    {
        _suppress = true;
        try
        {
            MappingSegment.SelectedIndex = (int)e.Mapping;
            BlendSegment.SelectedIndex = e.Blend == EffectBlend.Alpha ? 0 : 1;
            BackgroundSegment.SelectedIndex = (int)e.Background;
            SoftMaskSwitch.IsChecked = e.Mask == EffectMask.SoftCircle;
            TextureAlphaSwitch.IsChecked = e.TextureAsAlpha;
            ScrollRow.Write(scrollU, scrollV);
            RotationStartField.Value = e.Rotation.Start;
            RotationEndField.Value = e.Rotation.End;
            ScaleStartField.SetValueExpanding(e.Scale.Start);
            ScaleEndField.SetValueExpanding(e.Scale.End);
            AlphaStartField.Value = e.Alpha.Start;
            AlphaEndField.Value = e.Alpha.End;
            DissolveStartField.Value = e.Dissolve.Start;
            DissolveEndField.Value = e.Dissolve.End;
            EdgeWidthField.Value = e.DissolveEdgeWidth;
            EdgeColorSwatch.Color = ToColor(e.DissolveEdgeColor);
            ColorStartSwatch.Color = ToColor(e.Color.Start);
            ColorEndSwatch.Color = ToColor(e.Color.End);
        }
        finally
        {
            _suppress = false;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (!_suppress)
            Changed?.Invoke();
    }

    private static Vector3 ToVector(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    private static Color ToColor(Vector3 v) => Color.FromRgb(ToByte(v.X), ToByte(v.Y), ToByte(v.Z));

    private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
