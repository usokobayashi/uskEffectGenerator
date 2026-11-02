using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using UskEffect.Graphics;

namespace UskEffect.App.Presets;

public sealed record NoisePreset(string Name, NoiseSettings Settings, bool IsBuiltIn = false)
{
    public string DisplayName => IsBuiltIn ? $"{Name}（標準）" : Name;
}

/// <summary>
/// ノイズ設定のプリセット (FR-09)。組み込みプリセットはコードに持ち、
/// ユーザープリセットは %APPDATA%\uskEffect\noise-presets.json に保存する。
/// </summary>
public sealed class NoisePresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 手で編集できるよう日本語をエスケープしない
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly NoisePreset[] BuiltIns =
    [
        new("雲", new NoiseSettings { Type = NoiseType.Perlin, Frequency = 4, Octaves = 6, Persistence = 0.5f }, true),
        new("煙・流体", new NoiseSettings { Type = NoiseType.Simplex, Frequency = 3, Octaves = 5, Persistence = 0.5f, WarpStrength = 0.12f, Contrast = 1.6f }, true),
        new("ディゾルブ", new NoiseSettings { Type = NoiseType.Simplex, Frequency = 8, Octaves = 4, Persistence = 0.55f, Contrast = 1.2f }, true),
        new("乱流", new NoiseSettings { Type = NoiseType.Perlin, Frequency = 4, Octaves = 6, Persistence = 0.55f, Fractal = FractalMode.Turbulence }, true),
        new("リッジ・稲妻", new NoiseSettings { Type = NoiseType.Simplex, Frequency = 4, Octaves = 5, Persistence = 0.5f, Fractal = FractalMode.Ridged, Contrast = 1.3f }, true),
        new("セル", new NoiseSettings { Type = NoiseType.Worley, Frequency = 8, Octaves = 1, Worley = WorleyMode.F1, Invert = true }, true),
        new("コースティクス", new NoiseSettings { Type = NoiseType.Worley, Frequency = 6, Octaves = 1, Worley = WorleyMode.F2MinusF1, Invert = true, Contrast = 3f, Brightness = -0.9f, WarpStrength = 0.04f }, true),
    ];

    private readonly string _path;
    private readonly List<NoisePreset> _userPresets = [];

    public NoisePresetStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "uskEffect", "noise-presets.json");
        Load();
    }

    public IReadOnlyList<NoisePreset> All => [.. BuiltIns, .. _userPresets];

    /// <summary>同名のユーザープリセットがあれば上書きする。組み込みと同名は不可。</summary>
    public void Save(string name, NoiseSettings settings)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new ArgumentException("プリセット名が空です。");
        if (BuiltIns.Any(p => p.Name == name))
            throw new ArgumentException($"「{name}」は標準プリセットの名前です。別の名前にしてください。");

        _userPresets.RemoveAll(p => p.Name == name);
        _userPresets.Add(new NoisePreset(name, settings));
        Persist();
    }

    public bool Delete(string name)
    {
        if (_userPresets.RemoveAll(p => p.Name == name) == 0)
            return false;
        Persist();
        return true;
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredPreset>>(File.ReadAllText(_path), JsonOptions);
            if (stored is not null)
                _userPresets.AddRange(stored.Select(p => new NoisePreset(p.Name, p.Settings)));
        }
        catch (JsonException)
        {
            // 壊れたファイルは読み飛ばす。保存時に上書きされる
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var stored = _userPresets.Select(p => new StoredPreset(p.Name, p.Settings)).ToList();
        File.WriteAllText(_path, JsonSerializer.Serialize(stored, JsonOptions));
    }

    private sealed record StoredPreset(string Name, NoiseSettings Settings);
}
