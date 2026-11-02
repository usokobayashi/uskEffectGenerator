// プレビュー描画 (Phase 1 検証用): 矩形 / 3x3 タイル / 極座標
// 同じソーステクスチャを Wrap サンプラーで読み、UV スクロールでアニメーションさせる。

cbuffer PreviewParams : register(b0)
{
    float2 ViewportSize;
    float2 TextureSize;
    uint   ViewMode;     // 0: Rect, 1: Tile3x3, 2: Polar
    float  Time;
    float2 ScrollSpeed;  // UV / 秒
    float  InnerRadius;  // 0..1
    float  OuterRadius;  // 0..1
    float2 _Padding;
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
        // 3.1 座標変換: theta=0 を右方向、反時計回りを +u、内径側を v=0 とする
        float2 c = local * 2.0 - 1.0;
        c.y = -c.y;
        float r = length(c);
        if (r < InnerRadius || r > OuterRadius)
            return float4(Background(pixel), 1);
        float u = frac(atan2(c.y, c.x) / TwoPi);
        float v = (r - InnerRadius) / max(OuterRadius - InnerRadius, 1e-5);
        uv = float2(u, v) + scroll;
    }
    else
    {
        if (any(local < 0.0) || any(local > 1.0))
            return float4(Background(pixel), 1);
        uv = local * tiles + scroll;
    }

    // atan2 の不連続でミップ選択が破綻しないよう LOD 0 固定
    float n = Source.SampleLevel(WrapSampler, uv, 0);
    return float4(n.xxx, 1);
}
