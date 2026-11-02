// プレビュー描画: 矩形 / 3x3 タイル / 極座標 / シーム検証
// 同じソーステクスチャを Wrap サンプラーで読み、UV スクロールでアニメーションさせる。

cbuffer PreviewParams : register(b0)
{
    float2 ViewportSize;
    float2 TextureSize;

    uint   ViewMode;          // 0: Rect, 1: Tile3x3, 2: Polar, 3: SeamCheck
    float  Time;
    float2 ScrollSpeed;       // UV / 秒

    float  InnerRadius;       // 外接円を 1 とした比率
    float  OuterRadius;
    float  Rotation;          // ラジアン。表示を反時計回りに回す
    uint   PolarFlags;        // bit0: 角度方向反転, bit1: 内外反転

    uint   HeatmapMode;       // 0: Off, 1: 値差分, 2: 勾配差分
    float  MeanValueDiff;     // 内部の隣接画素差の平均 (CPU の継ぎ目解析から)
    float  MeanGradientDiff;  // 内部の 2 階差分の平均
    float  HeatThreshold;     // 平均の何倍から色を付けるか
};

Texture2D<float> Source : register(t0);
SamplerState WrapSampler : register(s0);

static const float TwoPi = 6.28318530718;

struct VSOutput
{
    float4 Position : SV_Position;
    float2 ScreenUV : TEXCOORD0;
};

// 頂点バッファなしのフルスクリーン三角形
VSOutput VSMain(uint vertexId : SV_VertexID)
{
    VSOutput o;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.ScreenUV = uv;
    return o;
}

float3 Background(float2 pixel)
{
    float2 cell = floor(pixel / 16.0);
    float checker = fmod(cell.x + cell.y, 2.0);
    return lerp(0.13, 0.17, checker).xxx;
}

// 極座標キャンバス → UV。規約は src/UskEffect.Graphics/PolarMapping.cs と同じ。
// c は中心原点・y 上向き・外接円半径 1。リング外なら false
bool PolarToUv(float2 c, out float2 uv)
{
    float r = length(c);
    uv = 0;
    if (r < InnerRadius || r > OuterRadius)
        return false;

    float u = frac((atan2(c.y, c.x) - Rotation) / TwoPi);
    float v = (r - InnerRadius) / max(OuterRadius - InnerRadius, 1e-5);
    if ((PolarFlags & 1u) != 0)
        u = frac(1.0 - u);
    if ((PolarFlags & 2u) != 0)
        v = 1.0 - v;
    uv = float2(u, v);
    return true;
}

float LoadWrapped(int2 texel)
{
    int2 size = (int2)TextureSize;
    return Source.Load(int3((texel % size + size) % size, 0));
}

// 周期境界を含めた局所差分を内部平均で割った比。継ぎ目があると境界の行・列で大きくなる
float SeamHeat(float2 uv)
{
    int2 p = (int2)floor(frac(uv) * TextureSize);
    float c = LoadWrapped(p);
    float l = LoadWrapped(p - int2(1, 0));
    float d = LoadWrapped(p - int2(0, 1));

    if (HeatmapMode == 1)
        return max(abs(c - l), abs(c - d)) / max(MeanValueDiff, 1e-6);

    float r = LoadWrapped(p + int2(1, 0));
    float u = LoadWrapped(p + int2(0, 1));
    return max(abs(r - 2.0 * c + l), abs(u - 2.0 * c + d)) / max(MeanGradientDiff, 1e-6);
}

float4 PSMain(VSOutput input) : SV_Target
{
    float2 pixel = input.ScreenUV * ViewportSize;
    float tiles = ViewMode == 1 ? 3.0 : 1.0;
    float2 content = ViewMode == 2 ? max(TextureSize.x, TextureSize.y).xx : TextureSize * tiles;
    float scale = min(ViewportSize.x / content.x, ViewportSize.y / content.y) * 0.95;
    float2 local = (pixel - ViewportSize * 0.5) / (content * scale) + 0.5;

    float2 scroll = ScrollSpeed * Time;
    float2 uv;

    if (ViewMode == 2)
    {
        float2 c = local * 2.0 - 1.0;
        c.y = -c.y;
        if (!PolarToUv(c, uv))
            return float4(Background(pixel), 1);
        uv += scroll;
    }
    else
    {
        if (any(local < 0.0) || any(local > 1.0))
            return float4(Background(pixel), 1);

        if (ViewMode == 3)
        {
            // 半タイルずらして、左右・上下の端を中央の十字に持ってくる
            uv = local + 0.5;
            float n = Source.SampleLevel(WrapSampler, uv, 0);
            float3 color = n.xxx * 0.75;
            if (HeatmapMode != 0)
            {
                float heat = SeamHeat(uv);
                float alpha = saturate((heat - HeatThreshold) / HeatThreshold);
                float3 heatColor = lerp(float3(1.0, 0.85, 0.1), float3(1.0, 0.1, 0.05), alpha);
                color = lerp(color, heatColor, alpha * 0.9);
            }
            return float4(color, 1);
        }

        uv = local * tiles + scroll;
    }

    // atan2 の不連続でミップ選択が破綻しないよう LOD 0 固定
    float n = Source.SampleLevel(WrapSampler, uv, 0);
    return float4(n.xxx, 1);
}
