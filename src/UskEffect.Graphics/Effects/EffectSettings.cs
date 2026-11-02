using System.Numerics;

namespace UskEffect.Graphics.Effects;

public enum EffectMapping : uint
{
    /// <summary>テクスチャをそのまま四角形に貼る。</summary>
    Rect = 0,

    /// <summary>プレビューの極座標設定でリング・円盤に貼る。</summary>
    Polar = 1,
}

public enum EffectBlend
{
    Alpha,
    Additive,
}

public enum EffectBackground : uint
{
    Checker = 0,
    Black = 1,
    Gray = 2,
}

public enum EffectMask : uint
{
    None = 0,

    /// <summary>四角形の端をぼかして円形にする (矩形貼りのときだけ有効)。</summary>
    SoftCircle = 1,
}

/// <summary>開始値から終了値へ再生位置に応じて線形補間する値。</summary>
public readonly record struct Animated<T>(T Start, T End);

/// <summary>
/// エフェクトプレビューの設定 (FR-05 / FR-06)。UV 移動は <see cref="PreviewSettings"/> のスクロール速度を使う。
/// </summary>
public sealed record EffectSettings
{
    public EffectMapping Mapping { get; init; } = EffectMapping.Rect;
    public EffectBlend Blend { get; init; } = EffectBlend.Additive;
    public EffectBackground Background { get; init; } = EffectBackground.Black;
    public EffectMask Mask { get; init; } = EffectMask.SoftCircle;

    /// <summary>回転 [度]。表示を反時計回りに回す。</summary>
    public Animated<float> Rotation { get; init; } = new(0f, 0f);

    public Animated<float> Scale { get; init; } = new(1f, 1f);

    /// <summary>0 で全表示、1 で全消失。</summary>
    public Animated<float> Dissolve { get; init; } = new(0f, 0f);

    /// <summary>ディゾルブ境界の発光帯の幅 (テクスチャ値の単位)。0 で発光なし。</summary>
    public float DissolveEdgeWidth { get; init; } = 0.08f;

    public Vector3 DissolveEdgeColor { get; init; } = new(1f, 0.55f, 0.15f);

    public Animated<float> Alpha { get; init; } = new(1f, 1f);

    public Animated<Vector3> Color { get; init; } = new(Vector3.One, Vector3.One);

    /// <summary>テクスチャの値をアルファに掛ける。オフならテクスチャはディゾルブの判定だけに使う。</summary>
    public bool TextureAsAlpha { get; init; } = true;
}

/// <summary>ある時刻のエフェクトの状態。シェーダーへ渡す値になる。</summary>
public readonly record struct EffectFrame(
    float RotationRadians,
    float Scale,
    float DissolveThreshold,
    float Alpha,
    Vector3 Color,
    Vector2 ScrollOffset);

public static class EffectEvaluator
{
    /// <summary>ディゾルブ境界のぼかし幅 (テクスチャ値の単位)。</summary>
    public const float DissolveSoftness = 0.02f;

    public static EffectFrame Evaluate(EffectSettings effect, PreviewSettings preview, double time, double normalizedTime)
    {
        float t = (float)Math.Clamp(normalizedTime, 0, 1);

        // 時刻が大きくなっても float の精度が落ちないよう、スクロール量は 1 周 (UV 1.0) で折り返す
        var scroll = new Vector2(Wrap(preview.ScrollSpeedU * time), Wrap(preview.ScrollSpeedV * time));

        return new EffectFrame(
            Lerp(effect.Rotation, t) * (MathF.PI / 180f),
            MathF.Max(Lerp(effect.Scale, t), 1e-4f),
            DissolveThreshold(Lerp(effect.Dissolve, t), effect.DissolveEdgeWidth),
            Math.Clamp(Lerp(effect.Alpha, t), 0f, 1f),
            Vector3.Lerp(effect.Color.Start, effect.Color.End, t),
            scroll);
    }

    /// <summary>
    /// ディゾルブ量 (0..1) を、テクスチャ値と比べるしきい値に変換する。テクスチャ値がしきい値以上の画素が残る。
    /// 量 0 では発光帯とぼかしを含めて全画素が残り、量 1 では全画素が消えるように範囲を広げる。
    /// </summary>
    public static float DissolveThreshold(float amount, float edgeWidth)
    {
        float margin = MathF.Max(edgeWidth, 0f) + DissolveSoftness;
        return Math.Clamp(amount, 0f, 1f) * (1f + margin) - margin;
    }

    private static float Lerp(Animated<float> value, float t) => value.Start + (value.End - value.Start) * t;

    private static float Wrap(double x) => (float)(x - Math.Floor(x));
}
