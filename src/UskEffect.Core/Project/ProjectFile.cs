using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using UskEffect.Core.Export;
using UskEffect.Core.Imaging;

namespace UskEffect.Core.Project;

public sealed class ProjectFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// .uskfx プロジェクトファイル。中身は ZIP で、設定を project.json に保存する (要件 §5: JSON + 画像データ)。
/// Phase 6 以降のレイヤー画像も同じ ZIP に追加できるよう、最初から ZIP にしている。
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".uskfx";

    public static bool IsProjectPath(string path) =>
        string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>形式を変えたら上げる。読込時、これより新しい形式は拒否する。</summary>
    /// 1: 設定のみ。2: 読み込んだ画像を images/source.png に保存。
    public const int CurrentFormatVersion = 2;

    private const string JsonEntryName = "project.json";
    private const string ImageEntryName = "images/source.png";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Vector3 の X/Y/Z はフィールドなので含める
        IncludeFields = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, ProjectDocument document)
    {
        var json = JsonSerializer.SerializeToNode(document, JsonOptions)!.AsObject();
        json.Insert(0, "formatVersion", CurrentFormatVersion);
        json.Insert(1, "application", "uskEffect");

        // 途中で失敗しても既存のプロジェクトを壊さないよう、一時ファイルに書いてから置き換える
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(JsonEntryName, CompressionLevel.Optimal);
            using var writer = entry.Open();
            JsonSerializer.Serialize(writer, json, JsonOptions);
            writer.Dispose();

            if (document.Image is { } image)
            {
                // PNG 自体が圧縮済みなので ZIP では圧縮しない
                var imageEntry = zip.CreateEntry(ImageEntryName, CompressionLevel.NoCompression);
                using var imageStream = imageEntry.Open();
                PngEncoder.Write(imageStream, image.Width, image.Height, PngColorType.Rgba, 8, image.Rgba);
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    public static ProjectDocument Load(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(JsonEntryName)
                        ?? throw new ProjectFormatException($"{JsonEntryName} がありません。uskEffect のプロジェクトではない可能性があります。");
            using var reader = entry.Open();
            var json = JsonNode.Parse(reader)?.AsObject() ?? throw new ProjectFormatException("project.json が空です。");

            int version = json["formatVersion"]?.GetValue<int>() ?? 0;
            if (version < 1 || version > CurrentFormatVersion)
                throw new ProjectFormatException($"対応していない形式のバージョンです ({version})。新しい uskEffect で保存された可能性があります。");

            json.Remove("formatVersion");
            json.Remove("application");
            var document = json.Deserialize<ProjectDocument>(JsonOptions) ?? new ProjectDocument();

            if (zip.GetEntry(ImageEntryName) is { } imageEntry)
            {
                using var imageStream = imageEntry.Open();
                using var buffer = new MemoryStream();
                imageStream.CopyTo(buffer);
                document = document with { Image = ImageLoader.DecodeWithWic(buffer.ToArray()) };
            }
            return document;
        }
        catch (InvalidDataException ex)
        {
            throw new ProjectFormatException("ZIP として読めません。ファイルが壊れているか、別の形式です。", ex);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException($"project.json を読めません: {ex.Message}", ex);
        }
        catch (ImageLoadException ex)
        {
            throw new ProjectFormatException($"保存された画像を読めません: {ex.Message}", ex);
        }
    }
}
