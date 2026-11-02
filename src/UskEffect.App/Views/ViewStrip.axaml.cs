using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using UskEffect.App.Locale;
using UskEffect.Graphics;

namespace UskEffect.App.Views;

public partial class ViewStrip : UserControl
{
    /// <summary>表示か極座標の設定が変わったときに発生する。</summary>
    public event Action? Changed;

    public ViewStrip()
    {
        InitializeComponent();

        ViewSegment.SelectionChanged += (_, _) => Changed?.Invoke();
        foreach (RangeBase field in new RangeBase[] { InnerRadiusField, OuterRadiusField, RotationField })
            field.ValueChanged += (_, _) => OnPolarChanged();
        foreach (ToggleButton toggle in new ToggleButton[] { FlipAngularSwitch, FlipRadialSwitch })
            toggle.IsCheckedChanged += (_, _) => Changed?.Invoke();
        UpdateSummary();
    }

    public PreviewMode Mode
    {
        get => (PreviewMode)Math.Max(0, ViewSegment.SelectedIndex);
        set => ViewSegment.SelectedIndex = (int)value;
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

    public void SetZoom(double? ratio) => ZoomText.Text = ratio is { } r ? $"{r * 100:0}%" : "";

    private void OnPolarChanged()
    {
        UpdateSummary();
        Changed?.Invoke();
    }

    private void UpdateSummary() =>
        PolarSummaryText.Text = Loc.F("S.Polar.Summary", InnerRadiusField.Value, OuterRadiusField.Value);
}
