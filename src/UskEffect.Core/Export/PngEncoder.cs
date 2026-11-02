using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace UskEffect.Core.Export;

public enum PngColorType : byte
{
    Grayscale = 0,
    Rgba = 6,
}

/// <summary>
/// 最小限の PNG エンコーダー (8/16bit グレースケール、8bit RGBA、非インターレース)。
/// 外部ライブラリのライセンス条件を避けるため、圧縮は .NET 標準の ZLibStream を使う。
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 行優先・上から下のサンプル列を PNG にする。16bit のサンプルはビッグエンディアンで並べること (PNG の仕様)。
    /// </summary>
    public static void Write(Stream output, int width, int height, PngColorType colorType, int bitDepth, ReadOnlySpan<byte> samples)
    {
        int channels = colorType == PngColorType.Rgba ? 4 : 1;
        int bytesPerPixel = channels * bitDepth / 8;
        int rowBytes = width * bytesPerPixel;
        if (samples.Length != rowBytes * height)
            throw new ArgumentException($"サンプル数が合わない (期待 {rowBytes * height} バイト、実際 {samples.Length})");
        if (colorType == PngColorType.Rgba && bitDepth != 8)
            throw new ArgumentException("RGBA は 8bit のみ対応");

        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        header[10] = 0; // 圧縮方式: deflate
        header[11] = 0; // フィルタ方式: 適応フィルタ
        header[12] = 0; // インターレースなし
        WriteChunk(output, "IHDR", header);

        WriteChunk(output, "IDAT", Compress(samples, rowBytes, height, bytesPerPixel));
        WriteChunk(output, "IEND", []);
    }

    // 各行に Sub フィルタを掛けて deflate する。ノイズのような連続的な画像では None より小さくなりやすい
    private static byte[] Compress(ReadOnlySpan<byte> samples, int rowBytes, int height, int bytesPerPixel)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[rowBytes + 1];
            row[0] = 1; // フィルタ種別: Sub
            for (int y = 0; y < height; y++)
            {
                var source = samples.Slice(y * rowBytes, rowBytes);
                for (int i = 0; i < rowBytes; i++)
                {
                    byte left = i >= bytesPerPixel ? source[i - bytesPerPixel] : (byte)0;
                    row[i + 1] = (byte)(source[i] - left);
                }
                zlib.Write(row);
            }
        }
        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    /// <summary>PNG のチャンクに使う CRC-32 (多項式 0xEDB88320)。</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
