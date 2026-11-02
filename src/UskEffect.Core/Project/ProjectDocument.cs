using UskEffect.Core.Export;
using UskEffect.Graphics;
using UskEffect.Graphics.Effects;

namespace UskEffect.Core.Project;

public sealed record TimelineSettings
{
    public double Duration { get; init; } = 2.0;
    public bool Loop { get; init; } = true;
}

public enum SourceKind
{
    Noise,
    Image,
}

public sealed record SourceSettings
{
    public SourceKind Kind { get; init; } = SourceKind.Noise;

    /// <summary>読み込んだ画像の元のファイル名 (表示用)。画像そのものは ZIP 内に保存する。</summary>
    public string? ImageName { get; init; }
}

public sealed record ExportSettings
{
    public ExportChannels Channels { get; init; } = ExportChannels.Gray8;
}

/// <summary>
/// プロジェクトの内容 (FR-08)。生成設定・プレビュー設定・エフェクト設定を保存・復元する。
/// レイヤーと編集内容はペイントの Phase で追加する (画像データは同じ .uskfx の ZIP 内に置く)。
/// </summary>
public sealed record ProjectDocument
{
    public NoiseSettings Noise { get; init; } = new();
    public PreviewSettings Preview { get; init; } = new();
    public EffectSettings Effect { get; init; } = new();
    public TimelineSettings Timeline { get; init; } = new();
    public ExportSettings Export { get; init; } = new();
    public SourceSettings Source { get; init; } = new();

    /// <summary>読み込んだ画像。project.json ではなく ZIP 内の PNG に保存する。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ImageData? Image { get; init; }
}
