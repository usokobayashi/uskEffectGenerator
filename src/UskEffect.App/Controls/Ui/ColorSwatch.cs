using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using UskEffect.App.Locale;

namespace UskEffect.App.Controls.Ui;

/// <summary>色の見本。押すと三角のカラーピッカーと 16 進の入力欄を開く。</summary>
public sealed class ColorSwatch : UserControl
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorSwatch, Color>(nameof(Color), Colors.White);

    private readonly Button _button;
    private readonly TriangleColorPicker _picker = new();
    private readonly TextBox _hex = new();
    private bool _syncing;

    /// <summary>ユーザーの操作でも設定でも、色が変わると発生する。</summary>
    public event Action? ColorChanged;

    public ColorSwatch()
    {
        _picker.Classes.Add("colorWheel");
        _picker.ColorPicked += () =>
        {
            if (!_syncing)
                Color = _picker.Color;
        };

        _hex.Classes.Add("hexEditor");
        _hex.MaxLength = 7;
        AutomationProperties.SetName(_hex, Loc.T("S.Color.Hex"));
        _hex.GotFocus += (_, _) => _hex.SelectAll();
        _hex.LostFocus += (_, _) => CommitHex();
        TextEditDismiss.Attach(_hex, CommitHex);
        _hex.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitHex();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                WriteHex();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var flyoutContent = new StackPanel { Children = { _picker, _hex } };
        flyoutContent.Classes.Add("colorFlyout");

        _button = new Button { Background = new SolidColorBrush(Color), Flyout = new Flyout { Content = flyoutContent } };
        _button.Classes.Add("swatch");
        Content = _button;
        WriteHex();
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AutomationProperties.SetName(_button, AutomationProperties.GetName(this));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ColorProperty || _button is null)
            return;

        _button.Background = new SolidColorBrush(Color);
        _syncing = true;
        _picker.Color = Color;
        _syncing = false;
        WriteHex();
        ColorChanged?.Invoke();
    }

    private void WriteHex() => _hex.Text = $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";

    // 読めない入力なら元の色のまま
    private void CommitHex()
    {
        string text = (_hex.Text ?? "").Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;
        if (text.Length == 7 && Color.TryParse(text, out var color))
            Color = color;
        WriteHex();
    }
}
