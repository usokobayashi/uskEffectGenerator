using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using UskEffect.App.Locale;

namespace UskEffect.App.Controls.Ui;

/// <summary>リンクボタン。オンのとき X と Y を同じ値にする。</summary>
public sealed class LinkToggle : ToggleButton;

/// <summary>
/// Vector2 の行 (項目名 | X | Y | リンク)。Unity のインスペクターと同じく、リンクがオンなら X と Y は常に同じ値。
/// </summary>
public sealed class Vector2Row : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<Vector2Row, string?>(nameof(Label));

    public static readonly StyledProperty<string?> LabelXProperty =
        AvaloniaProperty.Register<Vector2Row, string?>(nameof(LabelX));

    public static readonly StyledProperty<string?> LabelYProperty =
        AvaloniaProperty.Register<Vector2Row, string?>(nameof(LabelY));

    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly LinkToggle _link = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool _syncing;

    public SliderField X { get; } = new();
    public SliderField Y { get; } = new();

    /// <summary>X か Y が変わったときに発生する。リンク中でも 1 回の操作で 1 回。</summary>
    public event Action? Changed;

    public Vector2Row()
    {
        _label.Classes.Add("rowLabel");
        X.Classes.Add("compact");
        Y.Classes.Add("compact");
        ToolTip.SetTip(_link, Loc.T("S.Texture.Link"));
        AutomationProperties.SetName(_link, Loc.T("S.Texture.Link"));

        X.ValueChanged += (_, _) => OnFieldChanged(X, Y);
        Y.ValueChanged += (_, _) => OnFieldChanged(Y, X);
        _link.IsCheckedChanged += (_, _) =>
        {
            // リンクしたら Y を X にそろえる
            if (_link.IsChecked == true && Y.Value != X.Value)
                OnFieldChanged(X, Y);
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,*,Auto"), ColumnSpacing = 6 };
        Grid.SetColumn(X, 1);
        Grid.SetColumn(Y, 2);
        Grid.SetColumn(_link, 3);
        grid.Children.Add(_label);
        grid.Children.Add(X);
        grid.Children.Add(Y);
        grid.Children.Add(_link);
        Content = grid;
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? LabelX
    {
        get => GetValue(LabelXProperty);
        set => SetValue(LabelXProperty, value);
    }

    public string? LabelY
    {
        get => GetValue(LabelYProperty);
        set => SetValue(LabelYProperty, value);
    }

    public double Minimum
    {
        set
        {
            X.Minimum = value;
            Y.Minimum = value;
        }
    }

    public double Maximum
    {
        set
        {
            X.Maximum = value;
            Y.Maximum = value;
        }
    }

    public double Step
    {
        set
        {
            X.Step = value;
            Y.Step = value;
        }
    }

    public string ValueFormat
    {
        set
        {
            X.ValueFormat = value;
            Y.ValueFormat = value;
        }
    }

    public bool IsExpandable
    {
        set
        {
            X.IsExpandable = value;
            Y.IsExpandable = value;
        }
    }

    /// <summary>両方の初期値。</summary>
    public double Value
    {
        set => Write(value, value);
    }

    public bool IsLinked
    {
        get => _link.IsChecked == true;
        set => _link.IsChecked = value;
    }

    public (float X, float Y) Read() => ((float)X.Value, (float)Y.Value);

    public void Write(double x, double y)
    {
        _syncing = true;
        X.SetValueExpanding(x);
        Y.SetValueExpanding(y);
        _syncing = false;
    }

    private void OnFieldChanged(SliderField source, SliderField other)
    {
        if (_syncing)
            return;
        if (IsLinked)
        {
            _syncing = true;
            other.SetValueExpanding(source.Value);
            _syncing = false;
        }
        Changed?.Invoke();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty)
            _label.Text = Label;
        else if (change.Property == LabelXProperty)
            X.Label = LabelX;
        else if (change.Property == LabelYProperty)
            Y.Label = LabelY;
    }
}
