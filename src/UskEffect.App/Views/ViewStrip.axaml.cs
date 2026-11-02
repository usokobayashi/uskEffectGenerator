using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using UskEffect.App.Locale;
using UskEffect.Graphics;

namespace UskEffect.App.Views;

public partial class ViewStrip : UserControl
{
    // 表示の切り替えの並び
    private static readonly PreviewMode[] Modes = [PreviewMode.Rect, PreviewMode.Polar, PreviewMode.SeamCheck, PreviewMode.Effect];

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
