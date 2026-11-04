using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Platform;
using UskEffect.App.Locale;
using UskEffect.Graphics.Painting;

namespace UskEffect.App.Views;

/// <summary>レイヤーの一覧と操作 (FR-15)。状態は持たず、エンジンの状態を表示する。</summary>
public partial class LayerPanel : UserControl
{
    private const double HiddenOpacity = 0.35;

    private readonly Dictionary<int, Bitmap> _thumbnails = [];
    private readonly Dictionary<int, LayerRow> _rows = [];
    private readonly Dictionary<int, LayerState> _states = [];
    private bool _rowsShown;
    private int _activeId;
    private bool _showing;

    public event Action<int>? LayerSelected;
    public event Action<int, bool>? VisibleToggled;
    public event Action<int, float>? OpacityChanged;

    public event Action<int, LayerBlendMode>? BlendChanged;
    public event Action? AddPaintRequested;
    public event Action? AddNoiseRequested;
    public event Action? AddImageRequested;
    public event Action<int, bool>? InvertToggled;
    public event Action<int, bool>? MaskToggled;
    public event Action<int>? RemoveRequested;

    /// <summary>引数は (レイヤー, 動かす向き)。正なら上へ。</summary>
    public event Action<int, int>? MoveRequested;

    public LayerPanel()
    {
        InitializeComponent();
        // 追加はノイズとペイントから選ぶ
        AddButton.Click += (_, _) => ShowAddChoice(!AddChoiceRow.IsVisible);
        AddPaintButton.Click += (_, _) =>
        {
            HideAddChoice();
            AddPaintRequested?.Invoke();
        };
        AddImageButton.Click += (_, _) =>
        {
            HideAddChoice();
            AddImageRequested?.Invoke();
        };
        AddNoiseButton.Click += (_, _) =>
        {
            HideAddChoice();
            AddNoiseRequested?.Invoke();
        };
        DeleteButton.Click += (_, _) => RemoveRequested?.Invoke(_activeId);
        MoveUpButton.Click += (_, _) => MoveRequested?.Invoke(_activeId, 1);
        MoveDownButton.Click += (_, _) => MoveRequested?.Invoke(_activeId, -1);
        OpacityField.ValueChanged += (_, _) =>
        {
            if (!_showing)
                OpacityChanged?.Invoke(_activeId, (float)OpacityField.Value);
        };
        BlendBox.SelectionChanged += (_, _) =>
        {
            if (!_showing)
                BlendChanged?.Invoke(_activeId, (LayerBlendMode)Math.Max(0, BlendBox.SelectedIndex));
        };
    }

    private void ShowRows(IReadOnlyList<LayerState> layers, int activeId)
    {
        _states.Clear();
        foreach (var layer in layers)
            _states[layer.Id] = layer;
        foreach (int id in _rows.Keys.Where(id => !_states.ContainsKey(id)).ToArray())
            _rows.Remove(id);

        // 並びが変わった行は、前の位置から滑らせる
        var before = _rowsShown ? _rows.Values.ToDictionary(r => r, r => r.Root.Bounds.Y) : [];

        var added = new List<LayerRow>();
        var order = new List<Border>();
        foreach (var layer in layers.Reverse())
        {
            if (!_rows.TryGetValue(layer.Id, out var row))
            {
                row = CreateRow(layer.Id);
                _rows[layer.Id] = row;
                added.Add(row);
            }
            UpdateRow(row, layer, layer.Id == activeId);
            order.Add(row.Root);
        }

        // 並びが変わったときだけ入れ替える。外すとホバーの状態が消える
        if (!LayerList.Children.SequenceEqual(order))
        {
            LayerList.Children.Clear();
            LayerList.Children.AddRange(order);
        }
        if (_rowsShown)
        {
            added.ForEach(row => Motion.Reveal(row.Root));
            // 新しい位置は配置の後で分かる
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var (row, oldY) in before)
                {
                    double offset = oldY - row.Root.Bounds.Y;
                    if (Math.Abs(offset) >= 1 && row.Root.IsAttachedToVisualTree())
                        Motion.SlideFrom(row.Root, offset);
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        _rowsShown = true;
    }

    public void HideAddChoice() => ShowAddChoice(false);

    private void ShowAddChoice(bool show)
    {
        AddChoiceRow.IsVisible = show;
        MoveButtons.IsVisible = !show;
    }

    /// <summary>縮小画像を入れ替える。</summary>
    public void ShowThumbnails(IReadOnlyList<LayerThumbnail> thumbnails)
    {
        foreach (var thumbnail in thumbnails)
        {
            var bitmap = ToBitmap(thumbnail);
            _thumbnails[thumbnail.LayerId] = bitmap;
            if (_rows.TryGetValue(thumbnail.LayerId, out var row))
                row.Thumb.Source = bitmap;
        }
    }

    public Bitmap? ThumbnailOf(int layerId) => _thumbnails.GetValueOrDefault(layerId);

    internal static WriteableBitmap ToBitmap(LayerThumbnail thumbnail)
    {
        int size = thumbnail.Size;
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        for (int y = 0; y < size; y++)
            System.Runtime.InteropServices.Marshal.Copy(thumbnail.Rgba, y * size * 4, frame.Address + y * frame.RowBytes, size * 4);
        return bitmap;
    }

    /// <summary>一覧を作り直す。layers は先頭が一番下なので、逆順に並べる。</summary>
    public void Show(IReadOnlyList<LayerState> layers, int activeId)
    {
        _showing = true;
        _activeId = activeId;
        ShowRows(layers, activeId);

        int index = layers.ToList().FindIndex(l => l.Id == activeId);
        if (index >= 0)
        {
            OpacityField.Value = layers[index].Opacity;
            BlendBox.SelectedIndex = (int)layers[index].Blend;
            // マスクは合成モードを使わない
            BlendBox.IsEnabled = !layers[index].IsMask;
        }
        DeleteButton.IsEnabled = layers.Count > 1;
        MoveUpButton.IsEnabled = index >= 0 && index < layers.Count - 1;
        MoveDownButton.IsEnabled = index > 0;
        _showing = false;
    }

    // 行は作り直さない。選択やホバーの色の遷移が途切れるため
    private sealed class LayerRow
    {
        public required Border Root { get; init; }
        public required Button Eye { get; init; }
        public required Image Thumb { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Opacity { get; init; }
        public required Button Invert { get; init; }
        public required Button Mask { get; init; }
    }

    private LayerRow CreateRow(int id)
    {
        var icon = new Avalonia.Controls.Shapes.Path { Data = Resource<Geometry>("Icon.Eye") };
        icon.Classes.AddRange(["stroke", "icon"]);
        var eye = new Button { Content = icon };
        eye.Classes.Add("ico");
        AutomationProperties.SetName(eye, Loc.T("S.Layer.ToggleVisible"));
        eye.Click += (_, _) => VisibleToggled?.Invoke(id, !_states[id].Visible);

        var thumb = new Image
        {
            Width = Resource<double>("Ue.Size.LayerThumb"),
            Height = Resource<double>("Ue.Size.LayerThumb"),
            VerticalAlignment = VerticalAlignment.Center,
            Source = ThumbnailOf(id),
        };
        Grid.SetColumn(thumb, 1);

        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 2);
        var opacity = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        opacity.Classes.AddRange(["muted", "mono"]);
        Grid.SetColumn(opacity, 5);

        var invert = CreateToggle("Icon.Invert", "S.Layer.Invert", () => InvertToggled?.Invoke(id, !_states[id].Invert));
        Grid.SetColumn(invert, 3);
        var mask = CreateToggle("Icon.Mask", "S.Layer.Mask", () => MaskToggled?.Invoke(id, !_states[id].IsMask));
        Grid.SetColumn(mask, 4);

        var root = new Border
        {
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto"),
                ColumnSpacing = Resource<double>("Ue.Gap.Line"),
                Children = { eye, name, opacity, thumb, invert, mask },
            },
        };
        root.Classes.Add("layer");
        root.PointerPressed += (_, _) => LayerSelected?.Invoke(id);
        return new LayerRow { Root = root, Eye = eye, Thumb = thumb, Name = name, Opacity = opacity, Invert = invert, Mask = mask };
    }

    private void UpdateRow(LayerRow row, LayerState layer, bool active)
    {
        row.Eye.Opacity = layer.Visible ? 1 : HiddenOpacity;
        row.Name.Text = layer.Name;
        row.Opacity.Text = $"{layer.Opacity * 100:0}%";
        row.Invert.Classes.Set("on", layer.Invert);
        row.Mask.Classes.Set("on", layer.IsMask);
        if (active)
            row.Root.Background = Resource<IBrush>("Ue.Fill");
        else
            row.Root.ClearValue(BackgroundProperty);
    }

    private static Button CreateToggle(string icon, string label, Action toggled)
    {
        var path = new Avalonia.Controls.Shapes.Path { Data = Resource<Geometry>(icon) };
        path.Classes.AddRange(["stroke", "icon"]);
        var button = new Button { Content = path };
        button.Classes.Add("ico");
        ToolTip.SetTip(button, Loc.T(label));
        AutomationProperties.SetName(button, Loc.T(label));
        button.Click += (_, _) => toggled();
        return button;
    }

    private static T Resource<T>(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is T typed
            ? typed
            : throw new InvalidOperationException($"リソースがありません: {key}");
}
