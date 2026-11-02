namespace UskEffect.Core.Export;

public enum ImageFileFormat
{
    Png,
    Tga,
}

/// <summary>書き出すチャンネル構成 (FR-07)。</summary>
public enum ExportChannels
{
    /// <summary>グレースケール 8bit。</summary>
    Gray8,

    /// <summary>グレースケール 16bit (PNG のみ)。高さマップなど階調が要る用途向け。</summary>
    Gray16,

    /// <summary>RGB に値、A は 255。</summary>
    RgbaOpaque,

    /// <summary>RGB は白、A に値。エフェクト用のアルファマスク。</summary>
    RgbaWhiteAlpha,

    /// <summary>読み込んだ画像の RGBA をそのまま。ノイズでは RgbaOpaque と同じ。</summary>
    RgbaSource,
}

/// <summary>0..1 の float テクスチャを PNG / TGA に書き出す。</summary>
public static class TextureExporter
{
    public static ImageFileFormat FormatFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImageFileFormat.Png,
            ".tga" => ImageFileFormat.Tga,
            var ext => throw new NotSupportedException($"未対応の拡張子: {ext}（.png / .tga）"),
        };

    public static bool IsSupported(ImageFileFormat format, ExportChannels channels) =>
        !(format == ImageFileFormat.Tga && channels == ExportChannels.Gray16);

    public static void Save(string path, float[] pixels, int width, int height, ExportChannels channels)
    {
        var format = FormatFromPath(path);
        // 途中で失敗しても既存ファイルを壊さないよう、一時ファイルに書いてから置き換える
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
            Write(stream, format, pixels, width, height, channels);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>読み込んだ画像を書き出す。グレースケールと白 + アルファは輝度を使う。</summary>
    public static void SaveImage(string path, UskEffect.Graphics.ImageData image, ExportChannels channels)
    {
        var format = FormatFromPath(path);
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            if (channels == ExportChannels.RgbaSource)
                WriteRgba(stream, format, image.Width, image.Height, image.Rgba);
            else
                Write(stream, format, image.ToLuminanceForChannels(channels), image.Width, image.Height, channels, image);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteRgba(Stream output, ImageFileFormat format, int width, int height, byte[] rgba)
    {
        if (format == ImageFileFormat.Png)
            PngEncoder.Write(output, width, height, PngColorType.Rgba, 8, rgba);
        else
            TgaEncoder.WriteRgba(output, width, height, rgba);
    }

    private static float[] ToLuminanceForChannels(this UskEffect.Graphics.ImageData image, ExportChannels channels) => image.ToLuminance();

    public static void Write(Stream output, ImageFileFormat format, float[] pixels, int width, int height, ExportChannels channels) =>
        Write(output, format, pixels, width, height, channels, null);

    private static void Write(Stream output, ImageFileFormat format, float[] pixels, int width, int height, ExportChannels channels, UskEffect.Graphics.ImageData? image)
    {
        if (pixels.Length != width * height)
            throw new ArgumentException("画素数が合わない");
        if (!IsSupported(format, channels))
            throw new NotSupportedException("16bit グレースケールは PNG のみ対応");

        switch (channels)
        {
            case ExportChannels.Gray8:
            {
                var gray = ToGray8(pixels);
                if (format == ImageFileFormat.Png)
                    PngEncoder.Write(output, width, height, PngColorType.Grayscale, 8, gray);
                else
                    TgaEncoder.WriteGrayscale(output, width, height, gray);
                break;
            }
            case ExportChannels.Gray16:
                PngEncoder.Write(output, width, height, PngColorType.Grayscale, 16, ToGray16BigEndian(pixels));
                break;
            default:
            {
                byte[] rgba;
                if (image is not null && channels == ExportChannels.RgbaOpaque)
                {
                    // 画像の RGB を保ち、A だけ 255 にする
                    rgba = (byte[])image.Rgba.Clone();
                    for (int i = 3; i < rgba.Length; i += 4)
                        rgba[i] = 255;
                }
                else
                {
                    rgba = ToRgba(pixels, channels == ExportChannels.RgbaWhiteAlpha);
                }
                WriteRgba(output, format, width, height, rgba);
                break;
            }
        }
    }

    public static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    public static ushort ToUInt16(float value) => (ushort)MathF.Round(Math.Clamp(value, 0f, 1f) * 65535f);

    private static byte[] ToGray8(float[] pixels)
    {
        var gray = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
            gray[i] = ToByte(pixels[i]);
        return gray;
    }

    private static byte[] ToGray16BigEndian(float[] pixels)
    {
        var bytes = new byte[pixels.Length * 2];
        for (int i = 0; i < pixels.Length; i++)
        {
            ushort v = ToUInt16(pixels[i]);
            bytes[i * 2] = (byte)(v >> 8);
            bytes[i * 2 + 1] = (byte)v;
        }
        return bytes;
    }

    private static byte[] ToRgba(float[] pixels, bool whiteWithAlpha)
    {
        var rgba = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            byte v = ToByte(pixels[i]);
            byte rgb = whiteWithAlpha ? (byte)255 : v;
            rgba[i * 4] = rgb;
            rgba[i * 4 + 1] = rgb;
            rgba[i * 4 + 2] = rgb;
            rgba[i * 4 + 3] = whiteWithAlpha ? v : (byte)255;
        }
        return rgba;
    }
}
