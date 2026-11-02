using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UskEffect.Graphics.Effects;

namespace UskEffect.App.Controls;

/// <summary>
/// エフェクトプレビューの設定パネル (FR-05 / FR-06)。開始値・終了値の組が多いのでコードで組み立てる。
/// </summary>
public sealed class EffectPanel : UserControl
{
    private readonly ComboBox _mapping = Combo("矩形", "極座標（プレビューの極座標設定）");
    private readonly ComboBox _blend = Combo("Additive（加算）", "Alpha（通常）");
    private readonly ComboBox _background = Combo("チェッカー", "黒", "灰");
    private readonly CheckBox _softMask = new() { Content = "円形ソフトマスク（矩形のみ）", IsChecked = true };
    private readonly CheckBox _textureAsAlpha = new() { Content = "テクスチャの値をアルファに使う", IsChecked = true };

    private readonly RangePair _rotation;
    private readonly RangePair _scale;
    private readonly RangePair _dissolve;
    private readonly RangePair _alpha;
    private readonly Slider _edgeWidth = new() { Minimum = 0, Maximum = 0.3, Value = 0.08 };
    private readonly TextBlock _edgeWidthLabel = Label("");
    private readonly ColorPicker _edgeColor = Picker(Color.FromRgb(255, 140, 38));
    private readonly ColorPicker _colorStart = Picker(Colors.White);
    private readonly ColorPicker _colorEnd = Picker(Colors.White);

    private bool _suppress;

    public event Action? Changed;

    public EffectPanel()
    {
        _mapping.SelectedIndex = 0;
        _blend.SelectedIndex = 0;
        _background.SelectedIndex = 1;

        _rotation = new RangePair("Rotation", "回転", -360, 360, 0, 0, "0°");
        _scale = new RangePair("Scale", "拡縮", 0.1, 2, 1, 1, "0.00");
        _dissolve = new RangePair("Dissolve", "ディゾルブ", 0, 1, 0, 0, "0.00");
        _alpha = new RangePair("Alpha", "アルファ", 0, 1, 1, 1, "0.00");

        // UI オートメーション (アクセシビリティ・自動操作) から識別できるよう名前を付ける
        _mapping.Name = "EffectMappingBox";
        _blend.Name = "EffectBlendBox";
        _background.Name = "EffectBackgroundBox";
        _softMask.Name = "EffectSoftMaskBox";
        _textureAsAlpha.Name = "EffectTextureAsAlphaBox";
        _edgeWidth.Name = "EffectEdgeWidthSlider";

        var panel = new StackPanel();
        panel.Children.Add(Label("貼り方"));
        panel.Children.Add(_mapping);
        panel.Children.Add(Label("合成"));
        panel.Children.Add(_blend);
        panel.Children.Add(Label("背景"));
        panel.Children.Add(_background);
        panel.Children.Add(_softMask);
        panel.Children.Add(_textureAsAlpha);
        panel.Children.Add(Label("UV 移動は下の「UV スクロール」で設定").With(t => t.Opacity = 0.6));

        foreach (var pair in new[] { _rotation, _scale, _alpha, _dissolve })
            panel.Children.Add(pair.View);

        panel.Children.Add(_edgeWidthLabel);
        panel.Children.Add(_edgeWidth);
        panel.Children.Add(Row(Label("発光色"), _edgeColor));

        panel.Children.Add(Label("色（開始 → 終了）"));
        panel.Children.Add(Row(_colorStart, new TextBlock { Text = "→", Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center }, _colorEnd));

        Content = panel;

        foreach (var combo in new[] { _mapping, _blend, _background })
            combo.SelectionChanged += (_, _) => RaiseChanged();
        foreach (var check in new[] { _softMask, _textureAsAlpha })
            check.IsCheckedChanged += (_, _) => RaiseChanged();
        foreach (var pair in new[] { _rotation, _scale, _dissolve, _alpha })
            pair.Changed += RaiseChanged;
        _edgeWidth.ValueChanged += (_, _) => RaiseChanged();
        foreach (var picker in new[] { _edgeColor, _colorStart, _colorEnd })
            picker.ColorChanged += (_, _) => RaiseChanged();

        UpdateLabels();
    }

    public EffectSettings Read() => new()
    {
        Mapping = (EffectMapping)Math.Max(0, _mapping.SelectedIndex),
        Blend = _blend.SelectedIndex == 1 ? EffectBlend.Alpha : EffectBlend.Additive,
        Background = (EffectBackground)Math.Max(0, _background.SelectedIndex),
        Mask = _softMask.IsChecked == true ? EffectMask.SoftCircle : EffectMask.None,
        TextureAsAlpha = _textureAsAlpha.IsChecked == true,
        Rotation = _rotation.Read(),
        Scale = _scale.Read(),
        Dissolve = _dissolve.Read(),
        DissolveEdgeWidth = (float)_edgeWidth.Value,
        DissolveEdgeColor = ToVector(_edgeColor.Color),
        Alpha = _alpha.Read(),
        Color = new Animated<Vector3>(ToVector(_colorStart.Color), ToVector(_colorEnd.Color)),
    };

    /// <summary>設定をコントロールに反映する (プロジェクト読込用)。Changed は 1 回だけ発生する。</summary>
    public void Write(EffectSettings e)
    {
        _suppress = true;
        try
        {
            _mapping.SelectedIndex = (int)e.Mapping;
            _blend.SelectedIndex = e.Blend == EffectBlend.Alpha ? 1 : 0;
            _background.SelectedIndex = (int)e.Background;
            _softMask.IsChecked = e.Mask == EffectMask.SoftCircle;
            _textureAsAlpha.IsChecked = e.TextureAsAlpha;
            _rotation.Write(e.Rotation);
            _scale.Write(e.Scale);
            _dissolve.Write(e.Dissolve);
            _alpha.Write(e.Alpha);
            _edgeWidth.Value = e.DissolveEdgeWidth;
            _edgeColor.Color = ToColor(e.DissolveEdgeColor);
            _colorStart.Color = ToColor(e.Color.Start);
            _colorEnd.Color = ToColor(e.Color.End);
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
        UpdateLabels();
        Changed?.Invoke();
    }

    private void UpdateLabels()
    {
        _edgeWidthLabel.Text = $"発光帯の幅: {_edgeWidth.Value:0.00}";
        foreach (var pair in new[] { _rotation, _scale, _dissolve, _alpha })
            pair.UpdateLabel();
    }

    private static Vector3 ToVector(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    private static Color ToColor(Vector3 v) => Color.FromRgb(
        (byte)MathF.Round(Math.Clamp(v.X, 0f, 1f) * 255f),
        (byte)MathF.Round(Math.Clamp(v.Y, 0f, 1f) * 255f),
        (byte)MathF.Round(Math.Clamp(v.Z, 0f, 1f) * 255f));

    private static ComboBox Combo(params string[] items)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in items)
            combo.Items.Add(new ComboBoxItem { Content = item });
        return combo;
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 2), Opacity = 0.8 };

    private static ColorPicker Picker(Color color) => new() { Color = color, IsAlphaEnabled = false };

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var child in children)
        {
            child.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(child);
        }
        return row;
    }

    /// <summary>開始値と終了値のスライダー 2 本。</summary>
    private sealed class RangePair
    {
        private readonly string _title;
        private readonly string _format;
        private readonly TextBlock _label = Label("");
        private readonly Slider _start;
        private readonly Slider _end;

        public event Action? Changed;

        public RangePair(string id, string title, double min, double max, double start, double end, string format)
        {
            _title = title;
            _format = format;
            _start = new Slider { Name = $"Effect{id}StartSlider", Minimum = min, Maximum = max, Value = start };
            _end = new Slider { Name = $"Effect{id}EndSlider", Minimum = min, Maximum = max, Value = end };
            _start.ValueChanged += (_, _) => Changed?.Invoke();
            _end.ValueChanged += (_, _) => Changed?.Invoke();

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            AddRow(grid, 0, "開始", _start);
            AddRow(grid, 1, "終了", _end);

            View = new StackPanel { Children = { _label, grid } };
        }

        public Control View { get; }

        public Animated<float> Read() => new((float)_start.Value, (float)_end.Value);

        public void Write(Animated<float> value)
        {
            _start.Value = value.Start;
            _end.Value = value.End;
        }

        public void UpdateLabel() =>
            _label.Text = $"{_title}: {_start.Value.ToString(_format)} → {_end.Value.ToString(_format)}";

        private static void AddRow(Grid grid, int row, string caption, Slider slider)
        {
            var text = new TextBlock { Text = caption, FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(text, row);
            Grid.SetRow(slider, row);
            Grid.SetColumn(slider, 1);
            grid.Children.Add(text);
            grid.Children.Add(slider);
        }
    }
}

internal static class ControlExtensions
{
    public static T With<T>(this T control, Action<T> configure) where T : Control
    {
        configure(control);
        return control;
    }
}
