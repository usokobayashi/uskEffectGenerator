using System.Numerics;

namespace UskEffect.Graphics;

/// <summary>極座標表示の設定 (FR-03)。半径は外接円を 1 とした比率。</summary>
public readonly record struct PolarSettings(
    float InnerRadius = 0f,
    float OuterRadius = 1f,
    float RotationDegrees = 0f,
    bool FlipAngular = false,
    bool FlipRadial = false)
{
    public PolarSettings() : this(0f) { }
}

/// <summary>
/// 極座標キャンバスと矩形テクスチャ UV の対応 (要件 §3.1)。Shaders/Preview.hlsl の極座標表示と同じ規約で、
/// 極座標上の直接ペイント (FR-19) もこの変換を使う。
/// <list type="bullet">
/// <item>キャンバス座標は中心原点・y 上向き、外接円の半径を 1 とする。</item>
/// <item>角度は +x 方向を 0 とし、反時計回りに u が増える。回転は表示を反時計回りに回す。</item>
/// <item>v は内径で 0、外径で 1。</item>
/// <item>中心 r = 0 では角度が不定なので、内径 0 の円盤では中心付近が 1 点に集まる。</item>
/// </list>
/// </summary>
public static class PolarMapping
{
    private const float TwoPi = MathF.PI * 2f;

    /// <summary>キャンバス上の点を UV へ逆変換する。リングの外 (編集不可領域) なら false。</summary>
    public static bool TryCanvasToUv(Vector2 canvas, PolarSettings settings, out Vector2 uv)
    {
        float r = canvas.Length();
        if (r < settings.InnerRadius || r > settings.OuterRadius)
        {
            uv = default;
            return false;
        }

        float theta = MathF.Atan2(canvas.Y, canvas.X) - settings.RotationDegrees * (MathF.PI / 180f);
        float u = Fract(theta / TwoPi);
        float v = (r - settings.InnerRadius) / MathF.Max(settings.OuterRadius - settings.InnerRadius, 1e-5f);

        if (settings.FlipAngular)
            u = Fract(1f - u);
        if (settings.FlipRadial)
            v = 1f - v;

        uv = new Vector2(u, v);
        return true;
    }

    /// <summary>UV をキャンバス上の点へ変換する。</summary>
    public static Vector2 UvToCanvas(Vector2 uv, PolarSettings settings)
    {
        float u = settings.FlipAngular ? 1f - uv.X : uv.X;
        float v = settings.FlipRadial ? 1f - uv.Y : uv.Y;
        float theta = u * TwoPi + settings.RotationDegrees * (MathF.PI / 180f);
        float r = settings.InnerRadius + v * (settings.OuterRadius - settings.InnerRadius);
        return new Vector2(MathF.Cos(theta), MathF.Sin(theta)) * r;
    }

    private static float Fract(float x) => x - MathF.Floor(x);
}
