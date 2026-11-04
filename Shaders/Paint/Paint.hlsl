// ペイント: ブラシ・消しゴム・ぼかしのスタンプと、元の画像への合成
// 編集レイヤーは乗算済みアルファの RGBA。合成結果は乗算なしの RGBA (読み込んだ画像と同じ扱い)。
// 座標はテクスチャの画素。

cbuffer PaintParams : register(b0)
{
    float2 CanvasSize;
    float2 DabCenter;

    float  DabRadius;
    float  Hardness;       // 0: 中心から縁までぼける, 1: 縁だけ 1px なじませる
    float  Flow;           // 1 スタンプの濃さ
    float  BlurRadius;

    float3 Color;
    uint   SourceIsColor;  // 0: R チャンネルだけ使う (ノイズ), 1: RGBA (読み込んだ画像)
};

Texture2D<float4> Source : register(t0);
Texture2D<float4> Layer : register(t1);
Texture2D<float4> Composite : register(t2);
SamplerState ClampSampler : register(s0);

static const float GoldenAngle = 2.39996323;
static const int BlurTaps = 24;

struct FullOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

struct DabOutput
{
    float4 Position : SV_Position;
    float2 Pixel : TEXCOORD0;
};

// 頂点バッファなしのフルスクリーン三角形
FullOutput VSFull(uint vertexId : SV_VertexID)
{
    FullOutput o;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

// スタンプを囲む四角形 (トライアングルストリップ)
DabOutput VSDab(uint vertexId : SV_VertexID)
{
    DabOutput o;
    float2 corner = float2(vertexId & 1, (vertexId >> 1) & 1) * 2.0 - 1.0;
    float2 pixel = DabCenter + corner * (DabRadius + 2.0);
    o.Pixel = pixel;
    o.Position = float4(pixel / CanvasSize * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float Coverage(float2 pixel)
{
    float inner = DabRadius * Hardness;
    return 1.0 - smoothstep(inner - 0.5, DabRadius + 0.5, distance(pixel, DabCenter));
}

// 消しゴムも同じ出力で、ブレンド状態だけが違う
float4 PSBrush(DabOutput input) : SV_Target
{
    float alpha = Coverage(input.Pixel) * Flow;
    return float4(Color * alpha, alpha);
}

// 合成結果を円盤状に読んで平均し、レイヤーへ書く。読み込み元と書き込み先は別のテクスチャ
float4 PSBlur(DabOutput input) : SV_Target
{
    float4 sum = 0;
    for (int i = 0; i < BlurTaps; i++)
    {
        float radius = BlurRadius * sqrt((i + 0.5) / BlurTaps);
        float angle = i * GoldenAngle;
        float2 offset = float2(cos(angle), sin(angle)) * radius;
        float4 s = Composite.SampleLevel(ClampSampler, (input.Pixel + offset) / CanvasSize, 0);
        sum += float4(s.rgb * s.a, s.a);
    }
    float4 blurred = sum / BlurTaps;
    return blurred * Coverage(input.Pixel) * Flow;
}

float4 PSComposite(FullOutput input) : SV_Target
{
    int3 texel = int3(input.Position.xy, 0);
    float4 layer = Layer.Load(texel);
    float4 source = Source.Load(texel);
    if (SourceIsColor == 0)
        source = float4(source.rrr, 1);

    float below = source.a * (1.0 - layer.a);
    float alpha = layer.a + below;
    if (alpha <= 0.0)
        return 0;
    return float4((layer.rgb + source.rgb * below) / alpha, alpha);
}

// 大きさが変わったレイヤーの引き継ぎ
float4 PSResample(FullOutput input) : SV_Target
{
    return Layer.SampleLevel(ClampSampler, input.Uv, 0);
}

// ブラシの大きさを示す輪。どの背景でも見えるよう、白い線の外側を暗くする
float4 PSCursor(DabOutput input) : SV_Target
{
    float d = abs(distance(input.Pixel, DabCenter) - DabRadius);
    float white = 1.0 - smoothstep(0.5, 1.0, d);
    float dark = (1.0 - smoothstep(1.0, 2.0, d)) * 0.6;
    float alpha = max(white, dark);
    return float4(white.xxx, alpha);
}
