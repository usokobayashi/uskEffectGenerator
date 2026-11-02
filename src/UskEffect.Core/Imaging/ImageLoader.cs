using SharpGen.Runtime;
using UskEffect.Graphics;
using Vortice.WIC;

namespace UskEffect.Core.Imaging;

public sealed class ImageLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 画像の読み込み (FR-11)。PNG・JPG・BMP は Windows 標準の WIC、TGA は WIC が対応しないので自前で読む。
/// 16bit の PNG は 8bit へ落ちる。
/// </summary>
public static class ImageLoader
{
    /// <summary>GPU テクスチャの上限 (Direct3D 11)。</summary>
    public const int MaxDimension = 16384;

    public static readonly string[] Extensions = [".png", ".tga", ".jpg", ".jpeg", ".bmp"];

    public static ImageData Load(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(extension))
            throw new ImageLoadException($"未対応の形式です: {extension}（PNG・TGA・JPG・BMP）");

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImageLoadException(ex.Message, ex);
        }
        return extension == ".tga" ? TgaDecoder.Decode(bytes) : DecodeWithWic(bytes);
    }

    /// <summary>WIC が読める形式 (PNG・JPG・BMP) のバイト列を読む。</summary>
    public static ImageData DecodeWithWic(byte[] bytes)
    {
        try
        {
            using var factory = new IWICImagingFactory();
            using var stream = new MemoryStream(bytes);
            using var decoder = factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            var size = frame.Size;
            CheckSize(size.Width, size.Height);

            using var converter = factory.CreateFormatConverter();
            converter.Initialize(frame, PixelFormat.Format32bppRGBA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);

            int stride = size.Width * 4;
            var rgba = new byte[stride * size.Height];
            converter.CopyPixels((uint)stride, rgba);
            return new ImageData(size.Width, size.Height, rgba);
        }
        catch (SharpGenException ex)
        {
            throw new ImageLoadException("画像を読めません。ファイルが壊れているか、別の形式です。", ex);
        }
    }

    internal static void CheckSize(int width, int height)
    {
        if (width > MaxDimension || height > MaxDimension)
            throw new ImageLoadException($"画像が大きすぎます ({width}×{height})。上限は {MaxDimension}×{MaxDimension} です。");
    }
}

/// <summary>TGA の読み込み。カラーマップなしの 8・16・24・32bit と RLE 圧縮に対応する。</summary>
public static class TgaDecoder
{
    private const int HeaderSize = 18;

    public static ImageData Decode(byte[] file)
    {
        if (file.Length < HeaderSize)
            throw new ImageLoadException("TGA のヘッダーが足りません。");

        int idLength = file[0];
        int colorMapType = file[1];
        int imageType = file[2];
        int width = file[12] | file[13] << 8;
        int height = file[14] | file[15] << 8;
        int bits = file[16];
        bool topToBottom = (file[17] & 0x20) != 0;
        bool rightToLeft = (file[17] & 0x10) != 0;

        bool rle = imageType is 10 or 11;
        bool gray = imageType is 3 or 11;
        if (colorMapType != 0 || imageType is not (2 or 3 or 10 or 11))
            throw new ImageLoadException($"未対応の TGA です (種類 {imageType}、カラーマップ {colorMapType})。");
        if (gray ? bits != 8 : bits is not (16 or 24 or 32))
            throw new ImageLoadException($"未対応の TGA です ({bits}bit)。");
        if (width == 0 || height == 0)
            throw new ImageLoadException("TGA の大きさが 0 です。");
        ImageLoader.CheckSize(width, height);

        int bytesPerPixel = bits / 8;
        int position = HeaderSize + idLength;
        int pixelCount = width * height;
        var rgba = new byte[pixelCount * 4];

        int written = 0;
        void Put(ReadOnlySpan<byte> source)
        {
            int row = written / width;
            int column = written % width;
            int y = topToBottom ? row : height - 1 - row;
            int x = rightToLeft ? width - 1 - column : column;
            WritePixel(source, bytesPerPixel, rgba.AsSpan((y * width + x) * 4, 4));
            written++;
        }

        ReadOnlySpan<byte> Take(int count, byte[] data, ref int at)
        {
            if (at + count > data.Length)
                throw new ImageLoadException("TGA のデータが途中で切れています。");
            var span = data.AsSpan(at, count);
            at += count;
            return span;
        }

        while (written < pixelCount)
        {
            if (!rle)
            {
                Put(Take(bytesPerPixel, file, ref position));
                continue;
            }

            byte packet = Take(1, file, ref position)[0];
            int run = (packet & 0x7F) + 1;
            if (written + run > pixelCount)
                throw new ImageLoadException("TGA の RLE データが不正です。");
            if ((packet & 0x80) != 0)
            {
                var pixel = Take(bytesPerPixel, file, ref position);
                for (int i = 0; i < run; i++)
                    Put(pixel);
            }
            else
            {
                for (int i = 0; i < run; i++)
                    Put(Take(bytesPerPixel, file, ref position));
            }
        }
        return new ImageData(width, height, rgba);
    }

    private static void WritePixel(ReadOnlySpan<byte> source, int bytesPerPixel, Span<byte> rgba)
    {
        switch (bytesPerPixel)
        {
            case 1:
                rgba[0] = rgba[1] = rgba[2] = source[0];
                rgba[3] = 255;
                break;
            case 2:
            {
                // ARRRRRGG GGGBBBBB (リトルエンディアン)
                int v = source[0] | source[1] << 8;
                rgba[0] = Expand5((v >> 10) & 31);
                rgba[1] = Expand5((v >> 5) & 31);
                rgba[2] = Expand5(v & 31);
                rgba[3] = 255;
                break;
            }
            default:
                rgba[0] = source[2];
                rgba[1] = source[1];
                rgba[2] = source[0];
                rgba[3] = bytesPerPixel == 4 ? source[3] : (byte)255;
                break;
        }
    }

    private static byte Expand5(int v) => (byte)(v << 3 | v >> 2);
}
