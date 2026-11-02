// エフェクトプレビュー (FR-05 / FR-06)
// 背景を不透明で描いたあと、エフェクトを乗算済みアルファで出力し、ブレンド状態で Alpha / Additive を切り替える。
// 時間による補間は CPU (EffectEvaluator) で済ませ、ここには現在のフレームの値だけが届く。

cbuffer EffectParams : register(b0)
{
    float2 ViewportSize;
    float  Rotation;           // ラジアン。表示を反時計回りに回す
    float  Scale;

    float2 ScrollOffset;       // UV
    float  DissolveThreshold;  // テクスチャ値がこれ以上の画素が残る
    float  DissolveEdge;       // 発光帯の幅

    float3 Color;
    float  Alpha;

    float3 EdgeColor;
    uint   Flags;              // bit0: 極座標で貼る, bit1: テクスチャをアルファに使う, bit2: 円形ソフトマスク, bit3: カラー画像

    float  InnerRadius;
    float  OuterRadius;
    float  PolarRotation;
    uint   PolarFlags;         // bit0: 角度方向反転, bit1: 内外反転

    uint   BackgroundMode;     // 0: チェッカー, 1: 黒, 2: 灰
    float  DissolveSoftness;
    float2 Tiling;             // UV の繰り返し数
};

Texture2D<float4> Source : register(t0);
SamplerState WrapSampler : register(s0);

static const float TwoPi = 6.28318530718;

struct VSOutput
{
    float4 Position : SV_Position;
    float2 ScreenUV : TEXCOORD0;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    VSOutput o;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.ScreenUV = uv;
    return o;
}

float4 PSBackground(VSOutput input) : SV_Target
{
    if (BackgroundMode == 1)
        return float4(0, 0, 0, 1);
    if (BackgroundMode == 2)
        return float4(0.5, 0.5, 0.5, 1);

    float2 cell = floor(input.ScreenUV * ViewportSize / 16.0);
    return float4(lerp(0.13, 0.17, fmod(cell.x + cell.y, 2.0)).xxx, 1);
}

// 規約は src/UskEffect.Graphics/PolarMapping.cs と Shaders/Preview.hlsl の PolarToUv と同じ
bool PolarToUv(float2 c, out float2 uv)
{
    float r = length(c);
    uv = 0;
    if (r < InnerRadius || r > OuterRadius)
        return false;

    float u = frac((atan2(c.y, c.x) - PolarRotation) / TwoPi);
    float v = (r - InnerRadius) / max(OuterRadius - InnerRadius, 1e-5);
    if ((PolarFlags & 1u) != 0)
        u = frac(1.0 - u);
    if ((PolarFlags & 2u) != 0)
        v = 1.0 - v;
    uv = float2(u, v);
    return true;
}

float4 PSEffect(VSOutput input) : SV_Target
{
    // ビューポート中央の正方形 (短辺の 90%) を [-1, 1]、y 上向きの座標にする
    float2 pixel = input.ScreenUV * ViewportSize;
    float size = min(ViewportSize.x, ViewportSize.y) * 0.9;
    float2 p = (pixel - ViewportSize * 0.5) / (size * 0.5);
    p.y = -p.y;

    // 回転・拡縮の逆変換でスプライト上の座標 q を求める
    float s = sin(-Rotation);
    float c = cos(-Rotation);
    float2 q = float2(c * p.x - s * p.y, s * p.x + c * p.y) / Scale;

    float2 uv;
    float mask = 1.0;
    if ((Flags & 1u) != 0)
    {
        if (!PolarToUv(q, uv))
            return 0;
    }
    else
    {
        if (any(abs(q) > 1.0))
            return 0;
        uv = float2(q.x, -q.y) * 0.5 + 0.5;
        if ((Flags & 4u) != 0)
            mask = 1.0 - smoothstep(0.75, 1.0, length(q));
    }

    // 極座標の 0°/360° で atan2 が不連続なので LOD 0 固定
    // カラー画像は輝度 × アルファを値として扱い、色はエフェクトの色に掛ける
    float4 texel = Source.SampleLevel(WrapSampler, uv * Tiling + ScrollOffset, 0);
    bool isColor = (Flags & 8u) != 0;
    float n = isColor ? dot(texel.rgb, float3(0.299, 0.587, 0.114)) * texel.a : texel.r;

    float visible = smoothstep(DissolveThreshold, DissolveThreshold + DissolveSoftness, n);
    float edge = DissolveEdge > 0.0 ? 1.0 - saturate((n - DissolveThreshold) / DissolveEdge) : 0.0;

    float textureAlpha = (Flags & 2u) != 0 ? n : 1.0;
    float alpha = Alpha * visible * mask * max(textureAlpha, edge);
    float3 color = lerp(isColor ? Color * texel.rgb : Color, EdgeColor, edge);

    return float4(color * alpha, alpha);
}
