using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UskEffect.App.Locale;
using UskEffect.Core.Export;
using UskEffect.Core.Project;
using UskEffect.Graphics;

namespace UskEffect.App.Views;

/// <summary>書き出しウィンドウで選んだ内容。</summary>
public sealed record ExportChoice(ExportSettings Settings, int Resolution, (int Width, int Height)? CustomSize);

/// <summary>書き出しのプレビューと調整。出力ボタンは、開いたときに書き出しボタンと同じ位置に来る。</summary>
public partial class ExportWindow : Window
{
    private static readonly int[] Resolutions = [256, 512, 1024, 2048];
    private const int CustomSizeIndex = 4;

    // 枠の線 + 余白 + 出力ボタンの半分。XAML と同じ値
    private const double ConfirmCenterX = 1 + 12 + 72 / 2.0;
    private const double ConfirmCenterY = 1 + 12 + 28 / 2.0;

    private readonly Func<ExportChoice, ImageData?> _render;
    private readonly Func<ExportChoice, Task<bool>> _confirm;
    private readonly (int Width, int Height)? _customSize;
    private readonly ExportSettings _base;
    private bool _loading = true;
    private bool _previewPending;

    /// <summary>出力まで終えて閉じたか。</summary>
    public bool Confirmed { get; private set; }

    /// <summary>XAML の読み込み用。実際には引数付きを使う。</summary>
    public ExportWindow() : this(new ExportChoice(new ExportSettings(), 512, null), _ => null, _ => Task.FromResult(true))
    {
    }

    public ExportWindow(ExportChoice initial, Func<ExportChoice, ImageData?> render, Func<ExportChoice, Task<bool>> confirm)
    {
        InitializeComponent();
        _render = render;
        _confirm = confirm;
        _base = initial.Settings;
        _customSize = initial.CustomSize;
        Load(initial);

        foreach (var selector in new Avalonia.Controls.Primitives.SelectingItemsControl[] { OutputSegment, ResolutionBox, TintSegment })
            selector.SelectionChanged += (_, _) => OnChanged();
        TintSwatch.ColorChanged += OnChanged;
        Bit16Switch.IsCheckedChanged += (_, _) => OnChanged();
        FormatSegment.SelectionChanged += (_, _) => UpdateVisibility();
        NameBox.TextChanged += (_, _) => UpdateVisibility();
        FolderBox.TextChanged += (_, _) => UpdateVisibility();

        ConfirmButton.Click += async (_, _) => await ConfirmAsync();
        CancelButton.Click += (_, _) => Close();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        Opened += (_, _) =>
        {
            Motion.Reveal(Frame);
            SchedulePreview();
        };

        _loading = false;
        UpdateVisibility();
    }

    /// <summary>出力ボタンの中心を画面上の target に合わせるときの、窓の左上の位置。</summary>
    public static PixelPoint PositionFor(PixelPoint target, double scaling) =>
        new(target.X - (int)Math.Round(ConfirmCenterX * scaling), target.Y - (int)Math.Round(ConfirmCenterY * scaling));

    private void Load(ExportChoice choice)
    {
        var s = choice.Settings;
        OutputSegment.SelectedIndex = s.Output == CompositeOutput.Mask ? 1 : 0;
        if (choice.CustomSize is { } size)
        {
            CustomSizeItem.Content = $"{size.Width} × {size.Height}";
            CustomSizeItem.IsVisible = true;
            ResolutionBox.SelectedIndex = CustomSizeIndex;
        }
        else
        {
            int index = Array.IndexOf(Resolutions, choice.Resolution);
            ResolutionBox.SelectedIndex = index >= 0 ? index : 1;
        }
        Bit16Switch.IsChecked = s.Bit16;
        TintSegment.SelectedIndex = (int)s.Tint;
        TintSwatch.Color = Color.FromRgb(ToByte(s.TintR), ToByte(s.TintG), ToByte(s.TintB));
        NameBox.Text = s.Name;
        FolderBox.Text = s.Folder;
        FormatSegment.SelectedIndex = s.Format == ImageFileFormat.Tga ? 1 : 0;
        OverwriteSwitch.IsChecked = s.AllowOverwrite;
    }

    private static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255);

    /// <summary>今の選択を読む。</summary>
    public ExportChoice Read()
    {
        bool mask = OutputSegment.SelectedIndex == 1;
        bool bit16 = mask && Bit16Switch.IsChecked == true;
        int index = ResolutionBox.SelectedIndex;
        var color = TintSwatch.Color;
        var settings = _base with
        {
            Output = mask ? CompositeOutput.Mask : CompositeOutput.BlackToAlpha,
            Bit16 = Bit16Switch.IsChecked == true,
            Tint = (ExportTint)Math.Max(0, TintSegment.SelectedIndex),
            TintR = color.R / 255f,
            TintG = color.G / 255f,
            TintB = color.B / 255f,
            Name = NameBox.Text?.Trim(),
            Folder = FolderBox.Text?.Trim(),
            // 16bit は PNG だけ
            Format = !bit16 && FormatSegment.SelectedIndex == 1 ? ImageFileFormat.Tga : ImageFileFormat.Png,
            AllowOverwrite = OverwriteSwitch.IsChecked == true,
        };
        return index == CustomSizeIndex
            ? new ExportChoice(settings, 512, _customSize)
            : new ExportChoice(settings, Resolutions[Math.Clamp(index, 0, Resolutions.Length - 1)], null);
    }

    private void OnChanged()
    {
        if (_loading)
            return;
        UpdateVisibility();
        SchedulePreview();
    }

    private void UpdateVisibility()
    {
        bool mask = OutputSegment.SelectedIndex == 1;
        Bit16Row.IsVisible = mask;
        TintRow.IsVisible = !mask;
        TintSwatch.IsVisible = TintSegment.SelectedIndex != 0;
        bool bit16 = mask && Bit16Switch.IsChecked == true;
        TgaItem.IsEnabled = !bit16;
        if (bit16)
            FormatSegment.SelectedIndex = 0;
        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text) && !string.IsNullOrWhiteSpace(FolderBox.Text);
    }

    // スライダーのように続けて変わっても、描くのは最後の 1 回だけ
    private void SchedulePreview()
    {
        if (_previewPending)
            return;
        _previewPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _previewPending = false;
            if (_render(Read()) is { } image)
                PreviewImage.Source = ToBitmap(image);
        }, DispatcherPriority.Background);
    }

    /// <summary>乗算なしの画像を表示用にする。</summary>
    internal static WriteableBitmap ToBitmap(ImageData image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        var row = new byte[image.Width * 4];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int i = (y * image.Width + x) * 4, o = x * 4;
                int a = image.Rgba[i + 3];
                row[o] = (byte)(image.Rgba[i] * a / 255);
                row[o + 1] = (byte)(image.Rgba[i + 1] * a / 255);
                row[o + 2] = (byte)(image.Rgba[i + 2] * a / 255);
                row[o + 3] = (byte)a;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, row.Length);
        }
        return bitmap;
    }

    private async Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("S.Export.Folder"), AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            FolderBox.Text = path;
    }

    private async Task ConfirmAsync()
    {
        ConfirmButton.IsEnabled = false;
        try
        {
            if (await _confirm(Read()))
            {
                Confirmed = true;
                Close();
            }
        }
        finally
        {
            UpdateVisibility();
        }
    }
}
