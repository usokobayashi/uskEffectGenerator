namespace UskEffect.Core.Export;

/// <summary>非圧縮 TGA のエンコーダー (8bit グレースケール、32bit BGRA)。原点は左上。</summary>
public static class TgaEncoder
{
    private const byte ImageTypeTrueColor = 2;
    private const byte ImageTypeGrayscale = 3;
    private const byte DescriptorTopLeft = 0x20;

    /// <summary>行優先・上から下のグレースケール 8bit。</summary>
    public static void WriteGrayscale(Stream output, int width, int height, ReadOnlySpan<byte> gray)
    {
        if (gray.Length != width * height)
            throw new ArgumentException("サンプル数が合わない");
        WriteHeader(output, width, height, ImageTypeGrayscale, bitsPerPixel: 8, alphaBits: 0);
        output.Write(gray);
    }

    /// <summary>行優先・上から下の RGBA 8bit。TGA の並びは BGRA なので入れ替えて書く。</summary>
    public static void WriteRgba(Stream output, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("サンプル数が合わない");
        WriteHeader(output, width, height, ImageTypeTrueColor, bitsPerPixel: 32, alphaBits: 8);

        var bgra = rgba.ToArray();
        for (int i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        output.Write(bgra);
    }

    private static void WriteHeader(Stream output, int width, int height, byte imageType, byte bitsPerPixel, byte alphaBits)
    {
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "TGA の幅・高さは 1〜65535");

        Span<byte> header = stackalloc byte[18];
        header.Clear();
        header[2] = imageType;
        // 3..11: カラーマップなし、原点 (0, 0)
        header[12] = (byte)width;
        header[13] = (byte)(width >> 8);
        header[14] = (byte)height;
        header[15] = (byte)(height >> 8);
        header[16] = bitsPerPixel;
        header[17] = (byte)(DescriptorTopLeft | alphaBits);
        output.Write(header);
    }
}
