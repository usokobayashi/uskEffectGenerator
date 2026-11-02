using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using UskEffect.App.Locale;

namespace UskEffect.App.Controls.Ui;

/// <summary>
/// 数値欄 (入力 + 「−」「＋」)。入力中は自由にして、確定時 (Enter・フォーカスを外す・±) に範囲と形へ丸める。
/// フォーカスしたら全選択する。
/// </summary>
public sealed class NumberField : UserControl
{
    public static readonly StyledProperty<decimal> ValueProperty =
        AvaloniaProperty.Register<NumberField, decimal>(nameof(Value));

    private readonly TextBox _text;
    private readonly Button _down;
    private readonly Button _up;
    private string _formatString = "0";
    private bool _writingText;

    public decimal Minimum { get; set; }
    public decimal Maximum { get; set; } = 999999;
    public decimal Increment { get; set; } = 1;

    public string FormatString
    {
        get => _formatString;
        set
        {
            _formatString = value;
            WriteText();
        }
    }

    /// <summary>「−」「＋」を出すか。</summary>
    public bool ShowSteppers
    {
        get => _down.IsVisible;
        set
        {
            _down.IsVisible = value;
            _up.IsVisible = value;
        }
    }

    /// <summary>値が変わったとき (確定時) に発生する。</summary>
    public event Action? ValueChanged;

    public NumberField()
    {
        _text = new TextBox
        {
            TextAlignment = TextAlignment.Center,
        };
        _text.Classes.Add("bare");
        _text.GotFocus += (_, _) => _text.SelectAll();
        _text.LostFocus += (_, _) => Commit();
        TextEditDismiss.Attach(_text, EndEditing);
        _text.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                _text.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // 入力を取り消して抜ける
                WriteText();
                Focus(); // 入力欄からフォーカスを外す
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _down = MakeButton("−", "S.Common.Decrease", () => Step(-1));
        _up = MakeButton("＋", "S.Common.Increase", () => Step(1));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(_text, 1);
        Grid.SetColumn(_up, 2);
        grid.Children.Add(_down);
        grid.Children.Add(_text);
        grid.Children.Add(_up);

        Focusable = true;
        IsTabStop = false;

        var frame = new Border { ClipToBounds = true, Child = grid };
        frame.Classes.Add("numberField");
        Content = frame;
        WriteText();
    }

    public decimal Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AutomationProperties.SetName(_text, AutomationProperties.GetName(this));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            WriteText();
            ValueChanged?.Invoke();
        }
    }

    private void EndEditing()
    {
        Commit();
        Focus(); // 入力欄からフォーカスを外す
    }

    private Button MakeButton(string glyph, string nameKey, Action click)
    {
        var button = new Button { Content = glyph, Focusable = false };
        button.Classes.Add("num");
        AutomationProperties.SetName(button, Loc.T(nameKey));
        button.Click += (_, _) => click();
        return button;
    }

    private void Step(int direction)
    {
        Commit();
        Value = Clamp(Value + direction * Increment);
    }

    private void Commit()
    {
        if (_writingText)
            return;
        if (decimal.TryParse(_text.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            Value = Clamp(parsed);
        WriteText();
    }

    private decimal Clamp(decimal value) => Math.Clamp(value, Minimum, Maximum);

    private void WriteText()
    {
        _writingText = true;
        _text.Text = Value.ToString(FormatString, CultureInfo.InvariantCulture);
        _writingText = false;
    }
}
