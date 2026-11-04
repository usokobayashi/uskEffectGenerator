using UskEffect.Core.Export;
using UskEffect.Graphics;

namespace UskEffect.Core.Project;

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

    /// <summary>透過ボタン。黒を透明へ変換して RGBA で書き出す。</summary>
    public bool Transparent { get; init; }

    /// <summary>プレビューと書き出しの形。null は古いプロジェクトで、Channels と Transparent から決める。</summary>
    public CompositeOutput? Output { get; init; }

    /// <summary>白黒マスクを 16bit で書き出す (PNG のみ)。</summary>
    public bool Bit16 { get; init; }

    /// <summary>書き出しのときに色を乗せる方法。アルファのときだけ効く。</summary>
    public ExportTint Tint { get; init; }
    public float TintR { get; init; } = 1f;
    public float TintG { get; init; } = 1f;
    public float TintB { get; init; } = 1f;

    /// <summary>出力先のフォルダー。null なら「ダウンロード」。</summary>
    public string? Folder { get; init; }

    /// <summary>出力する名前 (拡張子なし)。null ならプロジェクトの名前。</summary>
    public string? Name { get; init; }

    public ImageFileFormat Format { get; init; } = ImageFileFormat.Png;

    /// <summary>同じ名前のファイルがあっても、確認せず上書きする。</summary>
    public bool AllowOverwrite { get; init; }
}

/// <summary>
/// プロジェクトの内容 (FR-08)。生成設定・プレビュー設定・描いた内容を保存・復元する。
/// 画像データは同じ .uskfx の ZIP 内に置く。
/// </summary>
public sealed record ProjectDocument
{
    /// <summary>解像度。ノイズはレイヤーが持つ。古いプロジェクトでは一番下のノイズの設定でもある。</summary>
    public NoiseSettings Noise { get; init; } = new();

    /// <summary>キャンバスの大きさ。null なら Noise の解像度の正方形。読み込んだ画像に合わせたときに入る。</summary>
    public int? CanvasWidth { get; init; }
    public int? CanvasHeight { get; init; }

    /// <summary>ノイズをレイヤーとして持つ形式か。false なら開くときに Noise を一番下のレイヤーにする。</summary>
    public bool NoiseIsLayer { get; init; }
    public PreviewSettings Preview { get; init; } = new();
    public ExportSettings Export { get; init; } = new();
    public SourceSettings Source { get; init; } = new();

    /// <summary>古い形式の読み込んだ画像。今はレイヤーが持つので、開くときにレイヤーへ移す。ZIP 内の PNG に保存する。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ImageData? Image { get; init; }

    /// <summary>レイヤー (先頭が一番下)。何もしていなければ null。画素は ZIP 内の PNG に保存する。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UskEffect.Graphics.Painting.LayerData>? Layers { get; init; }
}
