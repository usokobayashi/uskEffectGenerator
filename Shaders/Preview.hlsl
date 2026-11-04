// プレビュー描画: 矩形 / 極座標 (タイリングの表示は矩形を繰り返す)
// 同じソーステクスチャを Wrap サンプラーで読む。

cbuffer PreviewParams : register(b0)
{
    float2 ViewportSize;
    float2 TextureSize;

    uint   ViewMode;          // 0: Rect, 2: Polar
    float3 _ViewModePadding;

    float  InnerRadius;       // 外接円を 1 とした比率
    float  OuterRadius;
    float  Rotation;          // ラジアン。表示を反時計回りに回す
    uint   PolarFlags;        // bit0: 角度方向反転, bit1: 内外反転

    float3 StageColor;        // 内容の外側の色
    float  ContentMargin;     // 内容の周りの余白 [px]

    float2 ViewPan;           // キャンバスの移動 [px]。src/UskEffect.Graphics/ImageData.cs の CanvasView と同じ式
    float  ViewZoom;          // 全体表示を 1 とした拡大率
    float  ViewRotation;      // ラジアン。画面上で時計回り

    uint   SourceIsColor;     // 0: R チャンネルだけ使う (ノイズ), 1: RGBA (読み込んだ画像)
    float3 _Padding;

    float2 Tiling;            // UV の繰り返し数 (Unity のマテリアルと同じ)
    float2 UvOffset;          // UV のずらし量
};

Texture2D<float4> Source : register(t0);
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

// 透明を表す市松模様 (リングの外側)
float3 Checker(float2 pixel)
{
    float2 cell = floor(pixel / 8.0);
    return lerp(26.0 / 255.0, 32.0 / 255.0, fmod(cell.x + cell.y, 2.0)).xxx;
}

// 内容の外側: 内容に接する 1px は枠線、それより外はステージの色
float3 Outside(float2 local, float2 contentPixels)
{
    float2 overflow = max(abs(local - 0.5) - 0.5, 0.0) * contentPixels;
    return ContentMargin > 0.0 && max(overflow.x, overflow.y) <= 1.0
        ? float3(52.0, 58.0, 65.0) / 255.0
        : StageColor;
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

float4 LoadRaw(int2 texel)
{
    int2 size = (int2)TextureSize;
    return Source.Load(int3((texel % size + size) % size, 0));
}

// 色にする。画像の透明部分には市松模様を透かす
float3 Shade(float4 s, float2 pixel)
{
    return SourceIsColor != 0 ? lerp(Checker(pixel), s.rgb, s.a) : s.rrr;
}

float4 PSMain(VSOutput input) : SV_Target
{
    float2 pixel = input.ScreenUV * ViewportSize;
    float2 content = ViewMode == 2 ? max(TextureSize.x, TextureSize.y).xx : TextureSize;
    float2 available = max(ViewportSize - ContentMargin * 2.0, 1.0);
    float scale = min(available.x / content.x, available.y / content.y) * ViewZoom;

    // 画面 → 内容 (移動と回転の逆変換)
    float2 p = pixel - ViewportSize * 0.5 - ViewPan;
    float s = sin(-ViewRotation);
    float c = cos(-ViewRotation);
    float2 local = float2(c * p.x - s * p.y, s * p.x + c * p.y) / (content * scale) + 0.5;

    float2 uv;

    if (ViewMode == 2)
    {
        if (any(local < 0.0) || any(local > 1.0))
            return float4(Outside(local, content * scale), 1);
        float2 polar = local * 2.0 - 1.0;
        polar.y = -polar.y;
        if (!PolarToUv(polar, uv))
            return float4(Checker(pixel), 1);
        uv = uv * Tiling + UvOffset;
    }
    else
    {
        if (any(local < 0.0) || any(local > 1.0))
            return float4(Outside(local, content * scale), 1);

        uv = local * Tiling + UvOffset;

        // 大きく拡大したら画素の境界が見えるようにする
        if (scale / max(Tiling.x, Tiling.y) >= 4.0)
            return float4(Shade(LoadRaw((int2)floor(frac(uv) * TextureSize)), pixel), 1);
    }

    // atan2 の不連続でミップ選択が破綻しないよう LOD 0 固定
    return float4(Shade(Source.SampleLevel(WrapSampler, uv, 0), pixel), 1);
}
