using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace UskEffect.App.Controls.Ui;

/// <summary>色の見本。押すと色選択の吹き出しを開く。</summary>
public sealed class ColorSwatch : UserControl
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorSwatch, Color>(nameof(Color), Colors.White);

    private readonly Button _button;
    private readonly ColorView _view;
    private bool _syncing;

    /// <summary>ユーザーの操作でも設定でも、色が変わると発生する。</summary>
    public event Action? ColorChanged;

    public ColorSwatch()
    {
        _view = new ColorView { Color = Color, IsAlphaEnabled = false };
        _view.ColorChanged += (_, e) =>
        {
            if (!_syncing)
                Color = e.NewColor;
        };

        _button = new Button { Background = new SolidColorBrush(Color), Flyout = new Flyout { Content = _view } };
        _button.Classes.Add("swatch");
        Content = _button;
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
        _view.Color = Color;
        _syncing = false;
        ColorChanged?.Invoke();
    }
}
