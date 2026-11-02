using System.Numerics;
using UskEffect.Core.Export;
using UskEffect.Core.Imaging;
using UskEffect.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UskEffect.Tests;

public sealed class ImageLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uskfx-image").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // 2×2: 赤 / 緑 (半透明) / 青 / 白
    private static readonly byte[] Pixels = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 255, 255, 255, 255, 255];

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Encode(Action<Stream> write)
    {
        using var stream = new MemoryStream();
        write(stream);
        return stream.ToArray();
    }

    private static byte[] TgaHeader(int imageType, int bits, byte descriptor) =>
        [0, 0, (byte)imageType, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2, 0, (byte)bits, descriptor];

    [Fact]
    public void Png_RoundTripsThroughWic()
    {
        var path = Write("a.png", Encode(s => PngEncoder.Write(s, 2, 2, PngColorType.Rgba, 8, Pixels)));

        var image = ImageLoader.Load(path);

        Assert.Equal((2, 2), (image.Width, image.Height));
        Assert.Equal(Pixels, image.Rgba);
    }

    [Fact]
    public void Bmp_IsReadBottomUp()
    {
        // 2×1 の 24bit BMP: 赤, 青 (BGR、行は 4 バイト境界)
        byte[] bmp =
        [
            (byte)'B', (byte)'M', 62, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
            40, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 255, 255, 0, 0, 0, 0,
        ];

        var image = ImageLoader.Load(Write("a.bmp", bmp));

        Assert.Equal((2, 1), (image.Width, image.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, image.Rgba);
    }

    [Fact]
    public void Tga_FromOurEncoder_IsReadBack()
    {
        var path = Write("a.tga", Encode(s => TgaEncoder.WriteRgba(s, 2, 2, Pixels)));

        Assert.Equal(Pixels, ImageLoader.Load(path).Rgba);
    }

    [Fact]
    public void Tga_BottomLeftOrigin_IsFlipped()
    {
        // 既定の原点は左下: ファイルの先頭行が画面の下の行
        byte[] file = [.. TgaHeader(3, 8, 0), 10, 20, 30, 40];

        var image = ImageLoader.Load(Write("a.tga", file));

        Assert.Equal(30, image.Rgba[0]);
        Assert.Equal(10, image.Rgba[2 * 4]);
    }

    [Fact]
    public void Tga_Rle_ExpandsRunsAndRawPackets()
    {
        // RLE 24bit: 2 画素の繰り返し (赤) + 2 画素の生データ (緑, 青)。原点は左上
        byte[] file = [.. TgaHeader(10, 24, 0x20), 0x81, 0, 0, 255, 0x01, 0, 255, 0, 255, 0, 0];

        var image = ImageLoader.Load(Write("a.tga", file));

        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255 }, image.Rgba);
    }

    [Theory]
    [InlineData("a.png")]
    [InlineData("a.jpg")]
    [InlineData("a.tga")]
    public void BrokenFile_RaisesLoadError(string name)
    {
        Assert.Throws<ImageLoadException>(() => ImageLoader.Load(Write(name, [1, 2, 3])));
    }

    [Fact]
    public void TruncatedTga_RaisesLoadError()
    {
        byte[] file = [.. TgaHeader(3, 8, 0x20), 10];
        Assert.Throws<ImageLoadException>(() => ImageLoader.Load(Write("a.tga", file)));
    }

    [Fact]
    public void UnsupportedExtension_RaisesLoadError()
    {
        Assert.Throws<ImageLoadException>(() => ImageLoader.Load(Write("a.gif", [1])));
    }

    [Fact]
    public void SaveImage_KeepsColorOrUsesLuminance()
    {
        var image = new ImageData(2, 2, Pixels);
        string source = Path.Combine(_dir, "source.png");
        string opaque = Path.Combine(_dir, "opaque.png");

        TextureExporter.SaveImage(source, image, ExportChannels.RgbaSource);
        TextureExporter.SaveImage(opaque, image, ExportChannels.RgbaOpaque);

        Assert.Equal(Pixels, ImageLoader.Load(source).Rgba);
        var saved = ImageLoader.Load(opaque).Rgba;
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, saved[4..8]); // RGB を保ち A は 255
    }

    [Fact]
    public void ImageSeam_IsDetectedOnLuminance()
    {
        // 左右で黒から白へ増える画像は左右に継ぎ目がある
        const int size = 64;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte v = (byte)(x * 255 / (size - 1));
                var i = (y * size + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = v;
                rgba[i + 3] = 255;
            }
        }

        var image = new ImageData(size, size, rgba);
        var analysis = SeamAnalyzer.Analyze(image.ToLuminance(), size, size);

        Assert.False(analysis.Horizontal.IsSeamless);
    }
}

public sealed class CanvasViewTests
{
    private const double Tolerance = 1e-9;

    private static (double X, double Y) ContentAt(CanvasView view, double x, double y) => view.ScreenToContent(x, y, 2);

    [Fact]
    public void ZoomAt_KeepsThePointUnderTheCursor()
    {
        var view = new CanvasView { PanX = 10, PanY = -20, RotationDegrees = 30 };
        var before = ContentAt(view, 100, 50);

        var zoomed = view.ZoomAt(100, 50, 2);

        var after = ContentAt(zoomed, 100, 50);
        Assert.Equal(2, zoomed.Zoom, Tolerance);
        Assert.Equal(before.X, after.X, 1e-6);
        Assert.Equal(before.Y, after.Y, 1e-6);
    }

    [Fact]
    public void Zoom_IsClamped_AndPointStaysPutAtTheLimit()
    {
        var view = new CanvasView { Zoom = CanvasView.MaxZoom, PanX = 5 };

        var zoomed = view.ZoomAt(100, 0, 10);

        Assert.Equal(CanvasView.MaxZoom, zoomed.Zoom);
        Assert.Equal(5, zoomed.PanX, Tolerance);
    }

    [Fact]
    public void Rotated_TurnsPanAroundTheCenter_AndWrapsAngle()
    {
        var view = new CanvasView { PanX = 10, RotationDegrees = 350 }.Rotated(20);

        Assert.Equal(10, view.RotationDegrees, 1e-6);
        Assert.Equal(10 * Math.Cos(20 * Math.PI / 180), view.PanX, 1e-6);
        Assert.Equal(10 * Math.Sin(20 * Math.PI / 180), view.PanY, 1e-6);
    }

    [Fact]
    public void ScreenToContent_UndoesPanZoomRotation()
    {
        var view = new CanvasView { Zoom = 2, PanX = 30, PanY = 40, RotationDegrees = 90 };

        // 回転 90° (時計回り): 内容の +x は画面の +y へ行く。倍率 2 × 全体表示 2 = 4
        var content = view.ScreenToContent(30, 40 + 8, 2);

        Assert.Equal(2, content.X, 1e-6);
        Assert.Equal(0, content.Y, 1e-6);
    }
}

/// <summary>プレビューの実際の画素で Tiling・表示変換・カラー画像を確かめる。</summary>
public sealed unsafe class PreviewRenderFixture : IDisposable
{
    public const int Size = 64;

    private readonly GpuDevice _gpu = new();
    private readonly PreviewRenderer _renderer;
    private readonly ID3D11Texture2D _target;
    private readonly ID3D11RenderTargetView _targetView;
    private readonly ID3D11Texture2D _staging;
    private readonly ID3D11Texture2D _source;
    private readonly ID3D11ShaderResourceView _sourceView;
    private readonly Lock _sync = new();

    public PreviewRenderFixture()
    {
        _renderer = new PreviewRenderer(_gpu);
        string? errors = _renderer.LoadShader(Path.Combine(NoiseFixture.FindRepositoryRoot(), "Shaders", "Preview.hlsl"));
        if (errors is not null)
            throw new InvalidOperationException(errors);

        var desc = new Texture2DDescription
        {
            Width = Size,
            Height = Size,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        };
        _target = _gpu.Device.CreateTexture2D(desc);
        _targetView = _gpu.Device.CreateRenderTargetView(_target);
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        _staging = _gpu.Device.CreateTexture2D(desc);

        // 2×2 の画像: 左が赤、右が青
        byte[] pixels = [255, 0, 0, 255, 0, 0, 255, 255, 255, 0, 0, 255, 0, 0, 255, 255];
        fixed (byte* data = pixels)
        {
            _source = _gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = 2,
                Height = 2,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            }, new SubresourceData((nint)data, 8));
        }
        _sourceView = _gpu.Device.CreateShaderResourceView(_source);
    }

    /// <summary>テクスチャを 32×32 として描く (内容はビューポートいっぱい)。</summary>
    public Func<int, int, Vector3> Render(PreviewSettings settings, CanvasView? view = null)
    {
        lock (_sync)
        {
            _renderer.Render(_targetView, Size, Size, _sourceView, 2, 2, sourceIsColor: true, settings, 0f, null, 0f, view);
            _gpu.Context.CopyResource(_staging, _target);
            var mapped = _gpu.Context.Map(_staging, 0, MapMode.Read);
            var bytes = new byte[Size * Size * 4];
            try
            {
                for (int y = 0; y < Size; y++)
                    new ReadOnlySpan<byte>((void*)(mapped.DataPointer + (nint)(y * mapped.RowPitch)), Size * 4).CopyTo(bytes.AsSpan(y * Size * 4));
            }
            finally
            {
                _gpu.Context.Unmap(_staging, 0);
            }
            return (x, y) =>
            {
                int i = (y * Size + x) * 4;
                return new Vector3(bytes[i + 2], bytes[i + 1], bytes[i]) / 255f;
            };
        }
    }

    public void Dispose()
    {
        _sourceView.Dispose();
        _source.Dispose();
        _staging.Dispose();
        _targetView.Dispose();
        _target.Dispose();
        _renderer.Dispose();
        _gpu.Dispose();
    }
}

public sealed class PreviewRenderTests(PreviewRenderFixture fixture) : IClassFixture<PreviewRenderFixture>
{
    private const int Size = PreviewRenderFixture.Size;

    private static bool IsRed(Vector3 c) => c.X > 0.6f && c.Z < 0.4f;
    private static bool IsBlue(Vector3 c) => c.Z > 0.6f && c.X < 0.4f;

    [Fact]
    public void ColorImage_ShowsItsColors()
    {
        var pixel = fixture.Render(new PreviewSettings());

        Assert.True(IsRed(pixel(4, Size / 2)), pixel(4, Size / 2).ToString());
        Assert.True(IsBlue(pixel(Size - 5, Size / 2)), pixel(Size - 5, Size / 2).ToString());
    }

    [Fact]
    public void Tiling_RepeatsTheTexture()
    {
        // Tiling X = 2: 赤青赤青。U = 0.3 は 2 枚目の左側でなく 1 枚目の右側に当たる
        var pixel = fixture.Render(new PreviewSettings { TilingX = 2f });

        Assert.True(IsRed(pixel(Size / 8, Size / 2)));
        Assert.True(IsBlue(pixel(Size * 3 / 8, Size / 2)));
        Assert.True(IsRed(pixel(Size * 5 / 8, Size / 2)));
        Assert.True(IsBlue(pixel(Size * 7 / 8, Size / 2)));
    }

    [Fact]
    public void Offset_ShiftsTheTexture()
    {
        // Offset X = 0.5 で左右が入れ替わる
        var pixel = fixture.Render(new PreviewSettings { OffsetX = 0.5f });

        Assert.True(IsBlue(pixel(4, Size / 2)));
        Assert.True(IsRed(pixel(Size - 5, Size / 2)));
    }

    [Fact]
    public void Pan_MovesContent_LeavingStageColor()
    {
        var pixel = fixture.Render(new PreviewSettings(), new CanvasView { PanX = Size });

        // 全体が右へ出て、左側はステージの色になる
        Assert.False(IsRed(pixel(Size / 4, Size / 2)));
        Assert.True(pixel(Size / 4, Size / 2).Length() < 0.2f);
    }

    [Fact]
    public void Zoom_EnlargesAroundTheCenter()
    {
        // 2 倍にすると、左端付近に左の色の内側が来て、元の左端は画面外へ出る
        var pixel = fixture.Render(new PreviewSettings(), new CanvasView { Zoom = 2 });

        Assert.True(IsRed(pixel(Size / 4, Size / 2)));
        Assert.True(IsBlue(pixel(Size * 3 / 4, Size / 2)));
    }

    [Fact]
    public void Rotation180_SwapsLeftAndRight()
    {
        var pixel = fixture.Render(new PreviewSettings(), new CanvasView { RotationDegrees = 180 });

        Assert.True(IsBlue(pixel(4, Size / 2)));
        Assert.True(IsRed(pixel(Size - 5, Size / 2)));
    }
}
