using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace UskEffect.App.Controls.Ui;

/// <summary>
/// 一体型の調整欄。欄全体がスライダーで、塗りが値を、文字が項目名と値を示す。
/// RangeBase を継承するので値の範囲処理とアクセシビリティは標準のものを使う。
/// </summary>
[TemplatePart("PART_Fill", typeof(Border))]
[TemplatePart("PART_Value", typeof(TextBlock))]
public sealed class SliderField : RangeBase
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SliderField, string?>(nameof(Label));

    public static readonly StyledProperty<string?> HelpTextProperty =
        AvaloniaProperty.Register<SliderField, string?>(nameof(HelpText));

    /// <summary>値の表示形式 (double.ToString の書式)。</summary>
    public static readonly StyledProperty<string> ValueFormatProperty =
        AvaloniaProperty.Register<SliderField, string>(nameof(ValueFormat), "0.00");

    /// <summary>値の刻み。0 なら刻まない。</summary>
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<SliderField, double>(nameof(Step));

    private Border? _fill;
    private TextBlock? _valueText;
    private bool _dragging;

    static SliderField()
    {
        LabelProperty.Changed.AddClassHandler<SliderField>((field, e) =>
            AutomationProperties.SetName(field, e.NewValue as string));
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? HelpText
    {
        get => GetValue(HelpTextProperty);
        set => SetValue(HelpTextProperty, value);
    }

    public string ValueFormat
    {
        get => GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SliderFieldAutomationPeer(this);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _fill = e.NameScope.Find<Border>("PART_Fill");
        _valueText = e.NameScope.Find<TextBlock>("PART_Value");
        UpdateVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MinimumProperty || change.Property == MaximumProperty
            || change.Property == ValueFormatProperty || change.Property == BoundsProperty)
        {
            UpdateVisuals();
        }
    }

    private void UpdateVisuals()
    {
        double range = Maximum - Minimum;
        double ratio = range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
        if (_fill is not null)
            _fill.Width = Bounds.Width * ratio;
        if (_valueText is not null)
            _valueText.Text = Value.ToString(ValueFormat);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        e.Pointer.Capture(this);
        SetFromPointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            SetFromPointer(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = Step > 0 ? Step : (Maximum - Minimum) / 100;
        switch (e.Key)
        {
            case Key.Left or Key.Down:
                SetSnapped(Value - step);
                e.Handled = true;
                break;
            case Key.Right or Key.Up:
                SetSnapped(Value + step);
                e.Handled = true;
                break;
            case Key.Home:
                SetSnapped(Minimum);
                e.Handled = true;
                break;
            case Key.End:
                SetSnapped(Maximum);
                e.Handled = true;
                break;
        }
    }

    private void SetFromPointer(double x)
    {
        if (Bounds.Width <= 0)
            return;
        SetSnapped(Minimum + Math.Clamp(x / Bounds.Width, 0, 1) * (Maximum - Minimum));
    }

    private void SetSnapped(double value)
    {
        if (Step > 0)
            value = Minimum + Math.Round((value - Minimum) / Step) * Step;
        SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }

    private sealed class SliderFieldAutomationPeer(SliderField owner) : RangeBaseAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    }
}
