using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UskEffect.App.Locale;
using UskEffect.Graphics;

namespace UskEffect.App.Views;

public partial class NoisePanel : UserControl
{

    // 種類の一覧のサンプルの大きさ
    private const double ListSampleSize = 32;
    private const double ListRowHeight = ListSampleSize + 4;

    private readonly IReadOnlyList<NoiseCatalogEntry> _catalog = NoiseCatalog.Create();
    private readonly Dictionary<NoiseType, Button> _typeItems = [];
    private NoiseCatalogEntry? _selectedEntry;
    private bool _suppress;
    private bool _loading;
    private int _lastResolution = 512;
    private bool _layerIsNoise = true;
    private bool _pickingNewLayer;

    /// <summary>テクスチャ生成で種類を選んだときに発生する。</summary>
    public event Action<NoiseType>? NewLayerTypeChosen;

    /// <summary>ノイズの設定が変わったときに発生する。</summary>
    public event Action? Changed;

    /// <summary>Tiling / Offset が変わったときに発生する。</summary>
    public event Action? TextureChanged;

    public NoisePanel()
    {
        InitializeComponent();
        SetUpTypeMenu();
        P0Row.Write(0.1, 0.8);
        P1Row.Write(0.5, 0.1);
        P2Row.Write(0.9, 0.8);

        foreach (RangeBase field in new RangeBase[]
                 {
                     FrequencyField, OctavesField, PersistenceField, LacunarityField, WarpField, ContrastField, BrightnessField,
                     StripeWidthField, StripeSoftnessField,
                     AngleField, SizeField, ThicknessField, SoftnessField, CountField, FalloffField, AmountField, AspectField, TwistField,
                     Stop0Field, Stop1Field, Stop2Field, Stop3Field,
                 })
            field.ValueChanged += (_, _) => RaiseChanged();
        foreach (var row in new[] { CenterRow, P0Row, P1Row, P2Row })
            row.Changed += RaiseChanged;
        SolidColorSwatch.ColorChanged += RaiseChanged;
        foreach (SelectingItemsControl selector in new SelectingItemsControl[]
                 {
                     FractalSegment, WorleySegment, SeamSegment, StripeDirectionSegment,
                 })
            selector.SelectionChanged += (_, _) => RaiseChanged();
        SeedField.ValueChanged += RaiseChanged;
        RandomSeedButton.Click += (_, _) => SeedField.Value = Random.Shared.Next(0, (int)SeedField.Maximum + 1);
        TilingRow.Changed += RaiseTextureChanged;
        OffsetRow.Changed += RaiseTextureChanged;
        UpdateVisibility();
    }

    private void SetUpTypeMenu()
    {
        _selectedEntry = _catalog.First(e => !e.IsHeader);
        StackPanel? column = null;
        foreach (var entry in _catalog)
        {
            if (entry.IsHeader)
            {
                column = new StackPanel { Width = ResourceDouble("Ue.Size.TypeColumn"), Spacing = 1 };
                var header = new TextBlock { Text = Loc.T(entry.Name), Margin = new Thickness(4, 0, 0, 4) };
                header.Classes.Add("muted");
                column.Children.Add(header);
                TypeMenu.Children.Add(column);
                continue;
            }
            column!.Children.Add(CreateTypeItem(entry));
        }

        TypeButton.Flyout!.Opened += (_, _) => ShowSelectedType();
        TypeButton.Flyout.Closed += (_, _) =>
        {
            _pickingNewLayer = false;
            ((PopupFlyoutBase)TypeButton.Flyout).Placement = PlacementMode.LeftEdgeAlignedTop;
        };
        GenerateButton.Click += (_, _) => PickTypeForNewLayer(GenerateButton, PlacementMode.LeftEdgeAlignedTop);
        TypeMenu.AddHandler(KeyDownEvent, (_, e) => CloseTypeMenuOnEscape(e), RoutingStrategies.Tunnel);
        ShowSelectedType();
    }

    private Button CreateTypeItem(NoiseCatalogEntry entry)
    {
        // 大きさを固定して左に置くので、名前の開始位置がそろう
        var sample = new Image { Width = ListSampleSize, Height = ListSampleSize, Stretch = Stretch.Fill };
        sample.Bind(Image.SourceProperty, new Binding(nameof(NoiseCatalogEntry.Sample)) { Source = entry });
        RenderOptions.SetBitmapInterpolationMode(sample, BitmapInterpolationMode.HighQuality);
        var name = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        var item = new Button
        {
            Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Children = { sample, name } },
            Height = ListRowHeight,
        };
        item.Classes.Add("typeItem");
        Avalonia.Automation.AutomationProperties.SetName(item, entry.Name);
        item.Click += (_, _) =>
        {
            // 閉じると選び直しの状態が消えるので、先に受け取る
            bool pickingNewLayer = _pickingNewLayer;
            CloseTypeMenu();
            if (pickingNewLayer)
                NewLayerTypeChosen?.Invoke(entry.Type!.Value);
            else
                SelectedType = entry.Type!.Value;
        };
        _typeItems[entry.Type!.Value] = item;
        return item;
    }

    private void ShowSelectedType()
    {
        foreach (var (type, item) in _typeItems)
            item.Classes.Set("selected", !_pickingNewLayer && type == _selectedEntry?.Type);
        TypeNameText.Text = _selectedEntry?.Name;
        TypeSample.Source = _selectedEntry?.Sample;
    }

    /// <summary>種類の一覧の、その種類のボタン。</summary>
    internal Button TypeItem(NoiseType type) => _typeItems[type];

    public bool IsTypeMenuOpen => TypeButton.Flyout!.IsOpen;

    public void CloseTypeMenu() => TypeButton.Flyout!.Hide();

    public void CloseTypeMenuOnEscape(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsTypeMenuOpen)
            return;
        CloseTypeMenu();
        e.Handled = true;
    }

    private static double ResourceDouble(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is double d ? d : 0;

    /// <summary>選択欄のサンプルに渡す設定。見出しは含まない。SetSamples と同じ順。</summary>
    public IReadOnlyList<NoiseSettings> SampleSettings() =>
        _catalog.Where(e => !e.IsHeader).Select(NoiseCatalog.SampleSettings).ToArray();

    /// <summary>サンプルを選択欄へ入れる。画素は 0..1 の正方形。数が合わなければ入れない。</summary>
    public void SetSamples(IReadOnlyList<float[]> samples)
    {
        var entries = _catalog.Where(e => !e.IsHeader).ToArray();
        if (samples.Count != entries.Length)
            return;
        for (int i = 0; i < entries.Length; i++)
            entries[i].Sample = ToBitmap(samples[i]);
        ShowSelectedType();
    }

    // 周りをぼかした円形のアイコンにする
    internal static WriteableBitmap ToBitmap(float[] pixels)
    {
        const double fadeStart = 0.6;
        int size = (int)Math.Sqrt(pixels.Length);
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        var row = new byte[size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = (x + 0.5) / size * 2 - 1, dy = (y + 0.5) / size * 2 - 1;
                double t = Math.Clamp((Math.Sqrt(dx * dx + dy * dy) - fadeStart) / (1 - fadeStart), 0, 1);
                double alpha = 1 - t * t * (3 - 2 * t);
                byte gray = (byte)Math.Round(Math.Clamp(pixels[y * size + x], 0f, 1f) * alpha * 255);
                row[x * 4] = gray;
                row[x * 4 + 1] = gray;
                row[x * 4 + 2] = gray;
                row[x * 4 + 3] = (byte)Math.Round(alpha * 255);
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, row.Length);
        }
        return bitmap;
    }

    /// <summary>選んでいる種類。</summary>
    public NoiseType SelectedType
    {
        get => _selectedEntry?.Type ?? NoiseType.Perlin;
        set
        {
            _selectedEntry = _catalog.First(e => e.Type == value);
            ShowSelectedType();
            RaiseChanged();
        }
    }

    /// <summary>解像度。プロジェクト全体の設定で、書き出しのウィンドウから変える。設定すると正方形に戻る。</summary>
    public int Resolution
    {
        get => _lastResolution;
        set
        {
            _lastResolution = value;
            CustomSize = null;
        }
    }

    /// <summary>画像に合わせたキャンバスの大きさ。null なら解像度の正方形。</summary>
    public (int Width, int Height)? CustomSize { get; private set; }

    public void SetCustomSize(int width, int height) => CustomSize = (width, height);

    /// <summary>選んだレイヤーを出す。noise が null ならペイントのレイヤーで、種類の欄を出さない。</summary>
    public void ShowLayer(string name, NoiseSettings? noise, float tilingX, float tilingY, float offsetX, float offsetY)
    {
        _layerIsNoise = noise is not null;
        _loading = true;
        if (noise is not null)
            Write(noise with { Resolution = Resolution });
        WriteTexture(tilingX, tilingY, offsetX, offsetY);
        _loading = false;
        TargetText.Text = Loc.F("S.Noise.Target", name);
        UpdateVisibility();
        if (NoiseControls.IsVisible)
            Motion.Reveal(NoiseControls, fromOpacity: 0.4); // 消えると、切り替えのたびにちらつく
    }

    /// <summary>選んだレイヤーがノイズか。</summary>
    public bool IsNoiseLayer => _layerIsNoise;

    /// <summary>種類の一覧を開いて、選んだら新しいノイズのレイヤーを作る。別の場所のクリックや Esc で取り消せる。</summary>
    public void PickTypeForNewLayer(Control anchor, PlacementMode placement)
    {
        var flyout = (PopupFlyoutBase)TypeButton.Flyout!;
        _pickingNewLayer = true;
        flyout.Placement = placement;
        flyout.ShowAt(anchor);
    }

    public (float TilingX, float TilingY, float OffsetX, float OffsetY) ReadTexture()
    {
        var (tilingX, tilingY) = TilingRow.Read();
        var (offsetX, offsetY) = OffsetRow.Read();
        return (tilingX, tilingY, offsetX, offsetY);
    }

    public void WriteTexture(float tilingX, float tilingY, float offsetX, float offsetY)
    {
        TilingRow.Write(tilingX, tilingY);
        OffsetRow.Write(offsetX, offsetY);
    }

    public NoiseSettings Read() => new()
    {
        Type = SelectedType,
        Resolution = Resolution,
        Seed = (uint)SeedField.Value,
        Frequency = (int)FrequencyField.Value,
        Octaves = (int)OctavesField.Value,
        Persistence = (float)PersistenceField.Value,
        Seam = (SeamMode)Math.Max(0, SeamSegment.SelectedIndex),
        Lacunarity = (int)LacunarityField.Value,
        Fractal = (FractalMode)Math.Max(0, FractalSegment.SelectedIndex),
        Worley = (WorleyMode)Math.Max(0, WorleySegment.SelectedIndex),
        StripeDirection = (StripeDirection)Math.Max(0, StripeDirectionSegment.SelectedIndex),
        StripeWidth = (float)StripeWidthField.Value,
        StripeSoftness = (float)StripeSoftnessField.Value,
        Shape = ReadShape(),
        WarpStrength = (float)WarpField.Value,
        Contrast = (float)ContrastField.Value,
        Brightness = (float)BrightnessField.Value,
    };

    private ShapeSettings ReadShape()
    {
        var (centerX, centerY) = CenterRow.Read();
        var (p0X, p0Y) = P0Row.Read();
        var (p1X, p1Y) = P1Row.Read();
        var (p2X, p2Y) = P2Row.Read();
        return new ShapeSettings
        {
            CenterX = centerX,
            CenterY = centerY,
            AngleDegrees = (float)AngleField.Value,
            Size = (float)SizeField.Value,
            Thickness = (float)ThicknessField.Value,
            Softness = (float)SoftnessField.Value,
            Count = (int)CountField.Value,
            Falloff = (float)FalloffField.Value,
            Amount = (float)AmountField.Value,
            Aspect = (float)AspectField.Value,
            Twist = (float)TwistField.Value,
            Stop0 = (float)Stop0Field.Value,
            Stop1 = (float)Stop1Field.Value,
            Stop2 = (float)Stop2Field.Value,
            Stop3 = (float)Stop3Field.Value,
            P0X = p0X,
            P0Y = p0Y,
            P1X = p1X,
            P1Y = p1Y,
            P2X = p2X,
            P2Y = p2Y,
            SolidR = SolidColorSwatch.Color.R / 255f,
            SolidG = SolidColorSwatch.Color.G / 255f,
            SolidB = SolidColorSwatch.Color.B / 255f,
        };
    }

    private void WriteShape(ShapeSettings shape)
    {
        CenterRow.Write(shape.CenterX, shape.CenterY);
        AngleField.Value = shape.AngleDegrees;
        SizeField.Value = shape.Size;
        ThicknessField.Value = shape.Thickness;
        SoftnessField.Value = shape.Softness;
        CountField.Value = shape.Count;
        FalloffField.Value = shape.Falloff;
        AmountField.Value = shape.Amount;
        AspectField.Value = shape.Aspect;
        TwistField.Value = shape.Twist;
        Stop0Field.Value = shape.Stop0;
        Stop1Field.Value = shape.Stop1;
        Stop2Field.Value = shape.Stop2;
        Stop3Field.Value = shape.Stop3;
        P0Row.Write(shape.P0X, shape.P0Y);
        P1Row.Write(shape.P1X, shape.P1Y);
        P2Row.Write(shape.P2X, shape.P2Y);
        SolidColorSwatch.Color = Color.FromRgb(
            (byte)Math.Round(Math.Clamp(shape.SolidR, 0f, 1f) * 255),
            (byte)Math.Round(Math.Clamp(shape.SolidG, 0f, 1f) * 255),
            (byte)Math.Round(Math.Clamp(shape.SolidB, 0f, 1f) * 255));
    }

    public void Write(NoiseSettings s)
    {
        _suppress = true;
        try
        {
            SelectedType = s.Type;
            WriteShape(s.Shape);
            SeedField.Value = s.Seed;
            FrequencyField.SetValueExpanding(s.Frequency);
            OctavesField.Value = s.Octaves;
            PersistenceField.Value = s.Persistence;
            SeamSegment.SelectedIndex = (int)s.Seam;
            LacunarityField.Value = s.Lacunarity;
            FractalSegment.SelectedIndex = (int)s.Fractal;
            WorleySegment.SelectedIndex = (int)s.Worley;
            StripeDirectionSegment.SelectedIndex = (int)s.StripeDirection;
            StripeWidthField.Value = s.StripeWidth;
            StripeSoftnessField.Value = s.StripeSoftness;
            WarpField.SetValueExpanding(s.WarpStrength);
            ContrastField.SetValueExpanding(s.Contrast);
            BrightnessField.Value = s.Brightness;
            // 画像に合わせた大きさは、レイヤーの設定を出しても変えない
            if (CustomSize is null)
                Resolution = s.Resolution;
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
        UpdateVisibility();
        if (!_loading)
            Changed?.Invoke();
    }

    private void RaiseTextureChanged()
    {
        if (!_suppress && !_loading)
            TextureChanged?.Invoke();
    }

    // 選んだ項目に関係のない欄は出さない
    private void UpdateVisibility()
    {
        NoiseControls.IsVisible = _layerIsNoise;
        GenerateButton.IsVisible = !_layerIsNoise;

        var type = SelectedType;
        bool noise = type.IsNoise();
        var parameters = _selectedEntry?.Parameters ?? ShapeParam.None;

        bool stripe = type == NoiseType.Stripe;

        // 帯は周波数だけで周期が決まり、重ねない。形にはシームレスや合成がない
        bool layered = noise && !stripe;
        FractalRow.IsVisible = layered;
        WorleyRow.IsVisible = type == NoiseType.Worley;
        StripeControls.IsVisible = stripe;
        SeamRow.IsVisible = layered;
        SeedRow.IsVisible = layered || parameters.HasFlag(ShapeParam.Seed);
        ShapeSection.IsVisible = noise;
        // 単色に階調はない
        ToneSection.IsVisible = type != NoiseType.Solid;
        OctavesField.IsVisible = layered;
        PersistenceField.IsVisible = layered && OctavesField.Value > 1;
        LacunarityField.IsVisible = layered && OctavesField.Value > 1;
        PatternSection.IsVisible = !noise;

        SolidColorRow.IsVisible = parameters.HasFlag(ShapeParam.Color);
        CenterRow.IsVisible = parameters.HasFlag(ShapeParam.Center);
        AngleField.IsVisible = parameters.HasFlag(ShapeParam.Angle);
        SizeField.IsVisible = parameters.HasFlag(ShapeParam.Size);
        ThicknessField.IsVisible = parameters.HasFlag(ShapeParam.Thickness);
        SoftnessField.IsVisible = parameters.HasFlag(ShapeParam.Softness);
        CountField.IsVisible = parameters.HasFlag(ShapeParam.Count);
        FalloffField.IsVisible = parameters.HasFlag(ShapeParam.Falloff);
        AmountField.IsVisible = parameters.HasFlag(ShapeParam.Amount);
        AspectField.IsVisible = parameters.HasFlag(ShapeParam.Aspect);
        TwistField.IsVisible = parameters.HasFlag(ShapeParam.Twist);
        foreach (var stop in new[] { Stop0Field, Stop1Field, Stop2Field, Stop3Field })
            stop.IsVisible = parameters.HasFlag(ShapeParam.Stops);
        foreach (var point in new[] { P0Row, P1Row, P2Row })
            point.IsVisible = parameters.HasFlag(ShapeParam.Points);
        if (_selectedEntry?.AmountLabel is { } amountLabel)
            AmountField.Label = Loc.T(amountLabel);
    }
}
