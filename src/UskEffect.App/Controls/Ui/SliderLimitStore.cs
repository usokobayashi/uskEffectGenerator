using System.Text.Json;

namespace UskEffect.App.Controls.Ui;

/// <summary>
/// 広げたスライダーの最大値を次回起動へ引き継ぐ。項目ごとに「終了時の値」を保存し、
/// 次回はそれを最大値にする。既定の最大値は下回らない。
/// 例: 既定 32 で 100 まで広げ、60 で終了 → 次回の最大値は 60。10 で終了 → 次回は 32。
/// </summary>
public sealed class SliderLimitStore
{
    /// <summary>テストで利用者の設定を書き換えないための差し替え口。</summary>
    internal static string? PathOverride;

    private readonly string _path = PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "uskEffect", "slider-limits.json");

    public Dictionary<string, double> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return []; // 壊れていれば既定の最大値で始める
        }
    }

    public void Save(Dictionary<string, double> limits)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(limits));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 使い勝手のための記録なので、保存できなくても終了は止めない
        }
    }
}
