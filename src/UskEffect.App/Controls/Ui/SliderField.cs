using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Transformation;

namespace UskEffect.App.Controls.Ui;

/// <summary>
/// 調整欄。既定は「項目名 | スライダー | 数値」の 1 行、.compact は欄全体がスライダー。
/// 数値はクリックかダブルクリックで入力する。負から正にまたがる範囲は、ドラッグ中に中央へ吸着する。
/// IsExpandable なら、最大値より大きな値を入力するとスライダーの最大値が広がる (Maya と同じ。桁の上限は HardMaximum)。
/// RangeBase を継承するので値の範囲処理とアクセシビリティは標準のものを使う。
/// </summary>
[TemplatePart("PART_Track", typeof(Control))]
[TemplatePart("PART_Fill", typeof(Border))]
[TemplatePart("PART_Value", typeof(TextBlock))]
[TemplatePart("PART_Editor", typeof(TextBox))]
public sealed class SliderField : RangeBase
{
    // 中央への吸着幅 (範囲に対する割合)
    private const double MiddleSnapRange = 0.05;

    // ボックスに収まる入力の文字数
    private const int EditorMaxLength = 7;

    // これより動かなければクリックとして扱う [px]
    private const double DragThreshold = 3;

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

    /// <summary>ダブルクリックで数値を入力できるか。</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<SliderField, bool>(nameof(IsEditable), true);

    /// <summary>大きな値の入力で最大値を広げられるか。</summary>
    public static readonly StyledProperty<bool> IsExpandableProperty =
        AvaloniaProperty.Register<SliderField, bool>(nameof(IsExpandable));

    /// <summary>広げられる最大値の上限。ボックスから値がはみ出さない桁に抑える。</summary>
    public static readonly StyledProperty<double> HardMaximumProperty =
        AvaloniaProperty.Register<SliderField, double>(nameof(HardMaximum), 999);

    private Control? _track;
    private Control? _valueBox;
    private Border? _middle;
    private bool _clickPending;
    private double _pressX;
    private bool _pressOnValueBox;
    private double? _defaultMaximum;
    private Border? _fill;
    private Border? _knob;
    private TextBlock? _valueText;
    private TextBox? _editor;
    private bool _dragging;
    private double _valueBeforePress;

    static SliderField()
    {
        LabelProperty.Changed.AddClassHandler<SliderField>((field, e) =>
            AutomationProperties.SetName(field, e.NewValue as string));
    }

    public SliderField()
    {
        DoubleTapped += (_, e) =>
        {
            if (!IsEditable || IsEditing)
                return;
            // 1 回目の押下で動いた値を戻してから入力を始める
            SetCurrentValue(ValueProperty, _valueBeforePress);
            BeginEdit();
            e.Handled = true;
        };
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

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public bool IsExpandable
    {
        get => GetValue(IsExpandableProperty);
        set => SetValue(IsExpandableProperty, value);
    }

    public double HardMaximum
    {
        get => GetValue(HardMaximumProperty);
        set => SetValue(HardMaximumProperty, value);
    }

    public bool IsEditing => _editor?.IsVisible == true;

    /// <summary>既定の最大値。最初に記録した Maximum。</summary>
    public double DefaultMaximum => _defaultMaximum ?? Maximum;

    /// <summary>次回起動へ引き継ぐ最大値を反映する。既定の最大値は下回らない。</summary>
    internal void ApplyStoredMaximum(double stored)
    {
        _defaultMaximum ??= Maximum;
        Maximum = Math.Min(Math.Max(stored, _defaultMaximum.Value), HardMaximum);
    }

    /// <summary>値を設定する。広げられる欄なら、最大値を超える値に合わせて最大値を広げる。</summary>
    public void SetValueExpanding(double value)
    {
        if (IsExpandable && value > Maximum)
        {
            _defaultMaximum ??= Maximum;
            Maximum = Math.Min(value, HardMaximum);
        }
        Value = value;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SliderFieldAutomationPeer(this);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_track is not null)
            _track.SizeChanged -= OnTrackSizeChanged;

        _track = e.NameScope.Find<Control>("PART_Track");
        _fill = e.NameScope.Find<Border>("PART_Fill");
        _knob = e.NameScope.Find<Border>("PART_Knob");
        _middle = e.NameScope.Find<Border>("PART_Middle");
        _valueBox = e.NameScope.Find<Control>("PART_ValueBox");
        _valueText = e.NameScope.Find<TextBlock>("PART_Value");
        _editor = e.NameScope.Find<TextBox>("PART_Editor");

        if (_track is not null)
            _track.SizeChanged += OnTrackSizeChanged;
        if (_editor is not null)
        {
            _editor.MaxLength = EditorMaxLength;
            // TextBox より先に受ける (Esc を確実に取るため)
            _editor.AddHandler(KeyDownEvent, OnEditorKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _editor.LostFocus += (_, _) => EndEdit(commit: true);
            TextEditDismiss.Attach(_editor, () => EndEdit(commit: true));
        }
        UpdateVisuals();
    }

    private void OnTrackSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateVisuals();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MinimumProperty || change.Property == MaximumProperty
            || change.Property == ValueFormatProperty)
        {
            UpdateVisuals();
        }
    }

    private void UpdateVisuals()
    {
        double range = Maximum - Minimum;
        double ratio = range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
        double width = _track?.Bounds.Width ?? 0;
        if (_fill is not null)
            _fill.Width = width * ratio;
        if (_knob is not null)
        {
            var builder = TransformOperations.CreateBuilder(1);
            builder.AppendTranslate(Math.Max(width - _knob.Bounds.Width, 0) * ratio, 0);
            _knob.RenderTransform = builder.Build();
        }
        if (_valueText is not null)
            _valueText.Text = Value.ToString(ValueFormat);
        if (_middle is not null)
            _middle.IsVisible = Minimum < 0 && Maximum > 0;
    }

    private double KnobWidth => _knob?.Bounds.Width ?? 0;

    /// <summary>数値の入力を始める。</summary>
    internal void BeginEdit()
    {
        if (_editor is null)
            return;
        _editor.Text = Value.ToString("0.###", CultureInfo.InvariantCulture);
        _editor.IsVisible = true;
        // 前の値が入力欄の下に残らないようにする
        if (_valueText is not null)
            _valueText.IsVisible = false;
        _editor.Focus();
        _editor.SelectAll();
    }

    /// <summary>入力を終える。範囲外は範囲へ丸め、読めない文字なら元の値のまま。</summary>
    internal void EndEdit(bool commit)
    {
        if (_editor is not { IsVisible: true })
            return;
        _editor.IsVisible = false;
        if (_valueText is not null)
            _valueText.IsVisible = true;
        if (commit && double.TryParse(_editor.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double typed))
        {
            if (IsExpandable && typed > Maximum)
            {
                _defaultMaximum ??= Maximum;
                Maximum = Math.Min(typed, HardMaximum);
            }
            SetSnapped(typed, snapToMiddle: false);
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EndEdit(commit: true);
            Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndEdit(commit: false);
            Focus();
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsEditing || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // ダブルクリックの 2 回目では値を動かさない
        if (e.ClickCount == 1)
            _valueBeforePress = Value;
        Focus();

        // 数値のボックスはクリックで入力する
        _pressOnValueBox = _valueBox is not null && _valueBox.Bounds.Contains(e.GetPosition(_valueBox.Parent as Visual ?? this));
        if (_pressOnValueBox && IsEditable)
        {
            _clickPending = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(_track ?? this);
        var bounds = _track?.Bounds.Size ?? Bounds.Size;
        if (position.X < 0 || position.X > bounds.Width || e.ClickCount > 1)
            return; // スライダーの外 (項目名) はドラッグしない

        e.Pointer.Capture(this);
        e.Handled = true;

        // 一体型は欄全体がスライダーなので、動かさなければクリック (数値入力)、動かせばドラッグ
        if (IsEditable && Classes.Contains("compact"))
        {
            _clickPending = true;
            _pressX = position.X;
            return;
        }

        _dragging = true;
        SetFromPointer(position.X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double x = e.GetPosition(_track ?? this).X;
        if (_clickPending && !_pressOnValueBox && Math.Abs(x - _pressX) > DragThreshold)
        {
            _clickPending = false;
            _dragging = true;
        }
        if (_dragging)
            SetFromPointer(x);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // キャプチャの解除で OnPointerCaptureLost が走るので、先に状態を取っておく
        bool clicked = _clickPending;
        _clickPending = false;
        _dragging = false;
        e.Pointer.Capture(null);
        if (clicked)
            BeginEdit();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
        _clickPending = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || IsEditing)
            return;

        double step = Step > 0 ? Step : (Maximum - Minimum) / 100;
        switch (e.Key)
        {
            case Key.Left or Key.Down:
                SetSnapped(Value - step, snapToMiddle: false);
                e.Handled = true;
                break;
            case Key.Right or Key.Up:
                SetSnapped(Value + step, snapToMiddle: false);
                e.Handled = true;
                break;
            case Key.Home:
                SetSnapped(Minimum, snapToMiddle: false);
                e.Handled = true;
                break;
            case Key.End:
                SetSnapped(Maximum, snapToMiddle: false);
                e.Handled = true;
                break;
            case Key.Enter or Key.F2 when IsEditable:
                BeginEdit();
                e.Handled = true;
                break;
        }
    }

    private void SetFromPointer(double x)
    {
        // つまみの中心がカーソルに来るようにする
        double travel = (_track?.Bounds.Width ?? Bounds.Width) - KnobWidth;
        if (travel <= 0)
            return;
        SetSnapped(Minimum + Math.Clamp((x - KnobWidth / 2) / travel, 0, 1) * (Maximum - Minimum), snapToMiddle: true);
    }

    /// <summary>値を刻みと範囲に合わせて設定する。負から正にまたがる範囲は、ドラッグ中だけ中央へ吸着する。</summary>
    internal void SetSnapped(double value, bool snapToMiddle)
    {
        if (snapToMiddle && Minimum < 0 && Maximum > 0)
        {
            double middle = (Minimum + Maximum) / 2;
            if (Math.Abs(value - middle) <= (Maximum - Minimum) * MiddleSnapRange)
            {
                SetCurrentValue(ValueProperty, middle);
                return;
            }
        }
        if (Step > 0)
            value = Minimum + Math.Round((value - Minimum) / Step) * Step;
        SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }

    private sealed class SliderFieldAutomationPeer(SliderField owner) : RangeBaseAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    }
}
