using System.Buffers.Binary;
using UskEffect.Core.Export;
using Vortice.WIC;

namespace UskEffect.Tests;

public sealed class ExportTests
{
    // 端数のある大きさで行の扱いを確かめる
    private const int Width = 37;
    private const int Height = 19;

    [Theory]
    [InlineData(0, 0, 0, 255, 0, 0, 0, 0)]             // 黒は透明
    [InlineData(255, 255, 255, 255, 255, 255, 255, 255)] // 白は不透明の白
    [InlineData(128, 128, 128, 255, 255, 255, 255, 128)] // 灰は半透明の白
    [InlineData(200, 0, 0, 255, 255, 0, 0, 200)]       // 色は保つ
    [InlineData(255, 255, 255, 128, 255, 255, 255, 128)] // 元のアルファも引き継ぐ
    public void BlackToAlpha_MovesBrightnessIntoAlpha(byte r, byte g, byte b, byte a, byte outR, byte outG, byte outB, byte outA)
    {
        var result = TextureExporter.BlackToAlpha([r, g, b, a]);
        Assert.Equal([outR, outG, outB, outA], result);
    }

    [Fact]
    public void BlackToAlpha_OnNoise_IsTransparentWhereBlack_InAFile()
    {
        var pixels = Ramp();
        foreach (var format in new[] { ImageFileFormat.Png, ImageFileFormat.Tga })
        {
            string path = Path.Combine(Path.GetTempPath(), $"usk-black-{Guid.NewGuid():N}.{(format == ImageFileFormat.Png ? "png" : "tga")}");
            try
            {
                TextureExporter.Save(path, pixels, Width, Height, ExportChannels.RgbaBlackToAlpha);
                var image = UskEffect.Core.Imaging.ImageLoader.Load(path);
                Assert.Equal(0, image.Rgba[3]);
                Assert.Equal(255, image.Rgba[^1]);
                Assert.Equal(255, image.Rgba[^4]);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Gray_FromAnImage_TreatsTransparentAsBlack()
    {
        var image = new UskEffect.Graphics.ImageData(1, 1, [255, 255, 255, 128]);
        Assert.InRange(image.ToLuminance()[0], 0.49f, 0.51f);
    }

    [Theory]
    [InlineData(ExportTint.None, 255, 255, 255, 128, 255, 255, 255, 128)]      // 変えない
    [InlineData(ExportTint.Multiply, 255, 255, 255, 128, 255, 0, 0, 128)]      // 半透明の白に赤を掛ける
    [InlineData(ExportTint.Add, 255, 255, 255, 128, 255, 128, 128, 255)]       // 灰に赤を足すと明るくなる
    [InlineData(ExportTint.Multiply, 0, 0, 0, 0, 0, 0, 0, 0)]                  // 透明は透明のまま
    [InlineData(ExportTint.Add, 0, 0, 0, 0, 0, 0, 0, 0)]                       // 足しても穴を埋めない
    [InlineData(ExportTint.Multiply, 0, 255, 0, 255, 0, 0, 0, 0)]              // 緑に赤を掛けると何も残らない
    public void ApplyTint_PutsColorOnTheAlphaImage(ExportTint tint, byte r, byte g, byte b, byte a, byte outR, byte outG, byte outB, byte outA)
    {
        var image = new UskEffect.Graphics.ImageData(1, 1, [r, g, b, a]);
        var result = TextureExporter.ApplyTint(image, tint, 1, 0, 0).Rgba;
        Assert.InRange(result[0], outR - 1, outR + 1);
        Assert.InRange(result[1], outG - 1, outG + 1);
        Assert.InRange(result[2], outB - 1, outB + 1);
        Assert.InRange(result[3], outA - 1, outA + 1);
    }

    private static float[] Ramp()
    {
        var pixels = new float[Width * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                pixels[y * Width + x] = (x + y * Width) / (float)(Width * Height - 1);
        return pixels;
    }

    private static byte[] Encode(ImageFileFormat format, ExportChannels channels, float[] pixels)
    {
        using var stream = new MemoryStream();
        TextureExporter.Write(stream, format, pixels, Width, Height, channels);
        return stream.ToArray();
    }

    /// <summary>Windows 標準のデコーダー (WIC) で読み戻す。targetFormat を指定すればその形式へ変換する。</summary>
    private static (int Width, int Height, Guid Format, byte[] Pixels, int Stride) DecodeWithWic(byte[] file, Guid? targetFormat = null)
    {
        using var factory = new IWICImagingFactory();
        using var stream = new MemoryStream(file);
        using var decoder = factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        var size = frame.Size;

        IWICBitmapSource source = frame;
        IWICFormatConverter? converter = null;
        if (targetFormat is { } target)
        {
            converter = factory.CreateFormatConverter();
            converter.Initialize(frame, target, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            source = converter;
        }

        try
        {
            var format = source.PixelFormat;
            int bitsPerPixel = format == PixelFormat.Format16bppGray ? 16 : format == PixelFormat.Format8bppGray ? 8 : 32;
            int stride = (size.Width * bitsPerPixel + 7) / 8;
            var pixels = new byte[stride * size.Height];
            source.CopyPixels((uint)stride, pixels);
            return (size.Width, size.Height, format, pixels, stride);
        }
        finally
        {
            converter?.Dispose();
        }
    }

    [Fact]
    public void PngGray8_DecodesWithWic_ToSameValues()
    {
        var pixels = Ramp();
        var decoded = DecodeWithWic(Encode(ImageFileFormat.Png, ExportChannels.Gray8, pixels));

        Assert.Equal((Width, Height), (decoded.Width, decoded.Height));
        Assert.Equal(PixelFormat.Format8bppGray, decoded.Format);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                Assert.Equal(TextureExporter.ToByte(pixels[y * Width + x]), decoded.Pixels[y * decoded.Stride + x]);
    }

    [Fact]
    public void PngGray16_DecodesWithWic_KeepsSixteenBits()
    {
        var pixels = Ramp();
        var decoded = DecodeWithWic(Encode(ImageFileFormat.Png, ExportChannels.Gray16, pixels));

        Assert.Equal(PixelFormat.Format16bppGray, decoded.Format);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                // WIC のメモリ上の 16bit はリトルエンディアン
                ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(decoded.Pixels.AsSpan(y * decoded.Stride + x * 2));
                Assert.Equal(TextureExporter.ToUInt16(pixels[y * Width + x]), actual);
            }
        }
    }

    [Theory]
    [InlineData(ExportChannels.RgbaOpaque)]
    [InlineData(ExportChannels.RgbaWhiteAlpha)]
    public void PngRgba_DecodesWithWic_ToExpectedChannels(ExportChannels channels)
    {
        var pixels = Ramp();
        var decoded = DecodeWithWic(Encode(ImageFileFormat.Png, channels, pixels), PixelFormat.Format32bppRGBA);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte v = TextureExporter.ToByte(pixels[y * Width + x]);
                var px = decoded.Pixels.AsSpan(y * decoded.Stride + x * 4, 4);
                if (channels == ExportChannels.RgbaOpaque)
                    Assert.Equal(new byte[] { v, v, v, 255 }, px.ToArray());
                else
                    Assert.Equal(new byte[] { 255, 255, 255, v }, px.ToArray());
            }
        }
    }

    [Fact]
    public void TgaGray8_HeaderAndPixelsFollowSpec()
    {
        var pixels = Ramp();
        var file = Encode(ImageFileFormat.Tga, ExportChannels.Gray8, pixels);

        Assert.Equal(18 + Width * Height, file.Length);
        Assert.Equal(3, file[2]);   // 非圧縮グレースケール
        Assert.Equal(Width, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12)));
        Assert.Equal(Height, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(14)));
        Assert.Equal(8, file[16]);
        Assert.Equal(0x20, file[17]); // 原点左上、アルファなし
        Assert.Equal(TextureExporter.ToByte(pixels[Width + 3]), file[18 + Width + 3]);
    }

    [Fact]
    public void TgaRgba_IsBgraWithEightAlphaBits()
    {
        var pixels = Ramp();
        var file = Encode(ImageFileFormat.Tga, ExportChannels.RgbaWhiteAlpha, pixels);

        Assert.Equal(18 + Width * Height * 4, file.Length);
        Assert.Equal(2, file[2]);    // 非圧縮トゥルーカラー
        Assert.Equal(32, file[16]);
        Assert.Equal(0x28, file[17]); // 原点左上 + アルファ 8bit

        int i = 18 + (Width * 2 + 5) * 4;
        byte v = TextureExporter.ToByte(pixels[Width * 2 + 5]);
        Assert.Equal(new byte[] { 255, 255, 255, v }, file.AsSpan(i, 4).ToArray());
    }

    [Fact]
    public void Gray16_IsRejectedForTga()
    {
        Assert.False(TextureExporter.IsSupported(ImageFileFormat.Tga, ExportChannels.Gray16));
        Assert.Throws<NotSupportedException>(() => Encode(ImageFileFormat.Tga, ExportChannels.Gray16, Ramp()));
    }

    [Theory]
    [InlineData("a.png", ImageFileFormat.Png)]
    [InlineData("B.TGA", ImageFileFormat.Tga)]
    public void FormatFromPath_UsesExtension(string path, ImageFileFormat expected)
    {
        Assert.Equal(expected, TextureExporter.FormatFromPath(path));
        Assert.Throws<NotSupportedException>(() => TextureExporter.FormatFromPath("c.jpg"));
    }

    [Fact]
    public void Save_ReplacesFile_WithoutLeavingTemp()
    {
        string dir = Directory.CreateTempSubdirectory("uskfx-test").FullName;
        try
        {
            string path = Path.Combine(dir, "out.png");
            File.WriteAllText(path, "old");

            TextureExporter.Save(path, Ramp(), Width, Height, ExportChannels.Gray8);

            Assert.Equal(PixelFormat.Format8bppGray, DecodeWithWic(File.ReadAllBytes(path)).Format);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
