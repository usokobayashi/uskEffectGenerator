using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace UskEffect.App.Controls.Ui;

/// <summary>幅に合わせて正方形にする。</summary>
public sealed class SquareDecorator : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double side = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        Child?.Measure(new Size(side, side));
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double side = finalSize.Width;
        Child?.Arrange(new Rect(0, 0, side, side));
        return new Size(side, side);
    }
}

/// <summary>
/// 右上のプレビュー 1 つ。絵は子 HWND なので、マウスは D3DHost の通知で受ける。
/// 下の名前はボタンなのでキーボードでも押せる。
/// </summary>
public sealed class PreviewThumb : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<PreviewThumb, string?>(nameof(Text));

    private readonly Border _ring;
    private readonly Button _label;
    private bool _isActive;

    public D3DHost Host { get; } = new();

    /// <summary>プレビューか名前が押されたときに発生する。</summary>
    public event Action? Clicked;

    public PreviewThumb()
    {
        _ring = new Border { Child = Host };
        _ring.Classes.Add("thumbRing");

        _label = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };
        _label.Classes.Add("thumbLabel");
        _label.Click += (_, _) => Clicked?.Invoke();

        Host.HostClicked += () => Clicked?.Invoke();
        Host.HostPointerEntered += () => _ring.Classes.Set("hover", true);
        Host.HostPointerExited += () => _ring.Classes.Set("hover", false);

        Content = new StackPanel
        {
            Spacing = 4,
            Children = { new SquareDecorator { Child = _ring }, _label },
        };
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            _ring.Classes.Set("on", value);
            _label.Classes.Set("on", value);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && _label is not null)
            _label.Content = Text;
    }
}
