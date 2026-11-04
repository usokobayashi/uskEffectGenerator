namespace UskEffect.Core.Export;

public enum ImageFileFormat
{
    Png,
    Tga,
}

/// <summary>書き出しのときに色を乗せる方法。</summary>
public enum ExportTint
{
    None,
    Multiply,
    Add,
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

    /// <summary>黒を透明にする。明るさを A へ移し、色は明るさで割って保つので、加算合成と同じ見た目で重ねられる。</summary>
    RgbaBlackToAlpha,
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
            else if (channels == ExportChannels.RgbaBlackToAlpha)
                WriteRgba(stream, format, image.Width, image.Height, BlackToAlpha(image.Rgba));
            else
                Write(stream, format, image.ToLuminanceForChannels(channels), image.Width, image.Height, channels, image);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// アルファへ変換した画像に色を乗せる。透明なところは透明のまま。
    /// 乗せた後の一番明るい成分をアルファにするので、乗算で暗くなったところは透けていく。
    /// </summary>
    public static UskEffect.Graphics.ImageData ApplyTint(UskEffect.Graphics.ImageData image, ExportTint tint, float r, float g, float b)
    {
        if (tint == ExportTint.None)
            return image;

        var source = image.Rgba;
        var result = new byte[source.Length];
        for (int i = 0; i < source.Length; i += 4)
        {
            float alpha = source[i + 3] / 255f;
            if (alpha <= 0f)
                continue;

            // アルファへ移した明るさを戻してから色を乗せる
            float pr = source[i] / 255f * alpha, pg = source[i + 1] / 255f * alpha, pb = source[i + 2] / 255f * alpha;
            if (tint == ExportTint.Multiply)
            {
                pr *= r;
                pg *= g;
                pb *= b;
            }
            else
            {
                pr = MathF.Min(pr + r, 1f);
                pg = MathF.Min(pg + g, 1f);
                pb = MathF.Min(pb + b, 1f);
            }

            float peak = MathF.Max(pr, MathF.Max(pg, pb));
            if (peak <= 0f)
                continue;
            result[i] = ToByte(pr / peak);
            result[i + 1] = ToByte(pg / peak);
            result[i + 2] = ToByte(pb / peak);
            result[i + 3] = ToByte(peak);
        }
        return new UskEffect.Graphics.ImageData(image.Width, image.Height, result);
    }

    /// <summary>黒を透明にする。元のアルファも引き継ぐ。</summary>
    public static byte[] BlackToAlpha(byte[] rgba)
    {
        var result = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int peak = Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2]));
            if (peak == 0)
                continue;
            result[i] = (byte)MathF.Round(rgba[i] * 255f / peak);
            result[i + 1] = (byte)MathF.Round(rgba[i + 1] * 255f / peak);
            result[i + 2] = (byte)MathF.Round(rgba[i + 2] * 255f / peak);
            result[i + 3] = (byte)MathF.Round(peak * rgba[i + 3] / 255f);
        }
        return result;
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
                    // グレースケールでは、黒の透明化は白 + アルファと同じ結果になる
                    rgba = ToRgba(pixels, channels is ExportChannels.RgbaWhiteAlpha or ExportChannels.RgbaBlackToAlpha);
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
