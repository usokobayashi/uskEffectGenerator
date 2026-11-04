using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UskEffect.App.Locale;
using UskEffect.Graphics.Painting;

namespace UskEffect.App.Views;

/// <summary>レイヤーの一覧と操作 (FR-15)。状態は持たず、エンジンの状態を表示する。</summary>
public partial class LayerPanel : UserControl
{
    private const double HiddenOpacity = 0.35;

    private int _activeId;
    private bool _showing;

    public event Action<int>? LayerSelected;
    public event Action<int, bool>? VisibleToggled;
    public event Action<int, float>? OpacityChanged;
    public event Action? AddRequested;
    public event Action<int>? RemoveRequested;

    /// <summary>引数は (レイヤー, 動かす向き)。正なら上へ。</summary>
    public event Action<int, int>? MoveRequested;

    public LayerPanel()
    {
        InitializeComponent();
        AddButton.Click += (_, _) => AddRequested?.Invoke();
        DeleteButton.Click += (_, _) => RemoveRequested?.Invoke(_activeId);
        MoveUpButton.Click += (_, _) => MoveRequested?.Invoke(_activeId, 1);
        MoveDownButton.Click += (_, _) => MoveRequested?.Invoke(_activeId, -1);
        OpacityField.ValueChanged += (_, _) =>
        {
            if (!_showing)
                OpacityChanged?.Invoke(_activeId, (float)OpacityField.Value);
        };
    }

    /// <summary>一覧を作り直す。layers は先頭が一番下なので、逆順に並べる。</summary>
    public void Show(IReadOnlyList<LayerState> layers, int activeId)
    {
        _showing = true;
        _activeId = activeId;
        LayerList.Children.Clear();
        foreach (var layer in layers.Reverse())
            LayerList.Children.Add(CreateRow(layer, layer.Id == activeId));

        int index = layers.ToList().FindIndex(l => l.Id == activeId);
        if (index >= 0)
            OpacityField.Value = layers[index].Opacity;
        DeleteButton.IsEnabled = layers.Count > 1;
        MoveUpButton.IsEnabled = index >= 0 && index < layers.Count - 1;
        MoveDownButton.IsEnabled = index > 0;
        _showing = false;
    }

    private Border CreateRow(LayerState layer, bool active)
    {
        var icon = new Avalonia.Controls.Shapes.Path { Data = Resource<Geometry>("Icon.Eye") };
        icon.Classes.AddRange(["stroke", "icon"]);
        var eye = new Button { Content = icon, Opacity = layer.Visible ? 1 : HiddenOpacity };
        eye.Classes.Add("ico");
        AutomationProperties.SetName(eye, Loc.T("S.Layer.ToggleVisible"));
        eye.Click += (_, _) => VisibleToggled?.Invoke(layer.Id, !layer.Visible);

        var name = new TextBlock { Text = layer.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        var opacity = new TextBlock { Text = $"{layer.Opacity * 100:0}%", VerticalAlignment = VerticalAlignment.Center };
        opacity.Classes.AddRange(["muted", "mono"]);
        Grid.SetColumn(opacity, 2);

        var row = new Border
        {
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = Resource<double>("Ue.Gap.Line"),
                Children = { eye, name, opacity },
            },
        };
        row.Classes.Add("layer");
        if (active)
            row.Background = Resource<IBrush>("Ue.Fill");
        row.PointerPressed += (_, _) => LayerSelected?.Invoke(layer.Id);
        return row;
    }

    private static T Resource<T>(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is T typed
            ? typed
            : throw new InvalidOperationException($"リソースがありません: {key}");
}
