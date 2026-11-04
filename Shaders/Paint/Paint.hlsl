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

    float  InnerRadius;    // 極座標: 外接円を 1 とした比率。DabRadius と BlurRadius も同じ単位になる
    float  OuterRadius;
    float  CenterRadius;   // 極座標: スタンプの中心の半径
    float  RadialSign;     // 極座標: 内外を反転していると -1

    float2 PolarTiling;
    float  BlendWidth;     // 継ぎ目のブレンド: 境界から片側の幅 (テクスチャに対する割合)
    float  BlendStrength;

    uint   BlendAxis;      // 0: 左右の継ぎ目, 1: 上下の継ぎ目
    float3 _Padding;
};

Texture2D<float4> Source : register(t0);
Texture2D<float4> Layer : register(t1);
Texture2D<float4> Composite : register(t2);
SamplerState LinearSampler : register(s0); // スタンプでは Wrap か Clamp を選ぶ

static const float TwoPi = 6.28318530718;
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
        float4 s = Composite.SampleLevel(LinearSampler, (input.Pixel + offset) / CanvasSize, 0);
        sum += float4(s.rgb * s.a, s.a);
    }
    float4 blurred = sum / BlurTaps;
    return blurred * Coverage(input.Pixel) * Flow;
}

// テクスチャの点を、スタンプの中心を角度 0 に置いた極座標へ移す。周期で最も近い点を使う。
// リングの外 (編集不可領域) なら false (要件 §3.2)
bool ToPolarFrame(float2 uv, out float r, out float theta)
{
    float2 d = uv - DabCenter / CanvasSize;
    d -= round(d);
    theta = d.x / PolarTiling.x * TwoPi;
    r = CenterRadius + d.y / PolarTiling.y * (OuterRadius - InnerRadius) * RadialSign;
    return r >= InnerRadius && r <= OuterRadius;
}

// 極座標キャンバス上で円になるよう、中心からの距離を余弦定理で求める (FR-21)。
// 中心 r = 0 でも角度に依存しない
float PolarDistance(float r, float theta)
{
    return sqrt(max(CenterRadius * CenterRadius + r * r - 2.0 * CenterRadius * r * cos(theta), 0.0));
}

float PolarCoverage(float distanceToCenter)
{
    // 比率の単位なので、なじませる幅は微分から取る
    float smooth = clamp(fwidth(distanceToCenter), 1e-4, 0.02);
    return 1.0 - smoothstep(DabRadius * Hardness - smooth, DabRadius + smooth, distanceToCenter);
}

float4 PSPolarBrush(FullOutput input) : SV_Target
{
    float r, theta;
    bool inside = ToPolarFrame(input.Uv, r, theta);
    float alpha = PolarCoverage(PolarDistance(r, theta)) * Flow;
    return inside ? float4(Color * alpha, alpha) : float4(0, 0, 0, 0);
}

// 近傍を極座標キャンバス上で求め、逆写像した UV から読む (FR-20)。
// 読み込みは Wrap なので 0°/360° をまたいでも連続する
float4 PSPolarBlur(FullOutput input) : SV_Target
{
    float r, theta;
    bool inside = ToPolarFrame(input.Uv, r, theta);
    float coverage = PolarCoverage(PolarDistance(r, theta));
    float2 p = float2(cos(theta), sin(theta)) * r;
    float2 center = DabCenter / CanvasSize;

    float4 sum = 0;
    for (int i = 0; i < BlurTaps; i++)
    {
        float radius = BlurRadius * sqrt((i + 0.5) / BlurTaps);
        float angle = i * GoldenAngle;
        float2 q = p + float2(cos(angle), sin(angle)) * radius;
        float qr = clamp(length(q), InnerRadius, OuterRadius);
        float2 uv = center + float2(atan2(q.y, q.x) / TwoPi * PolarTiling.x,
            (qr - CenterRadius) / (OuterRadius - InnerRadius) * PolarTiling.y * RadialSign);
        float4 s = Composite.SampleLevel(LinearSampler, uv, 0);
        sum += float4(s.rgb * s.a, s.a);
    }
    return inside ? sum / BlurTaps * coverage * Flow : float4(0, 0, 0, 0);
}

// 継ぎ目の両側を、境界で折り返した向かい側と混ぜる。境界で同じ値になり、幅の端で元の値に戻る
float4 PSSeamBlend(FullOutput input) : SV_Target
{
    int2 texel = int2(input.Position.xy);
    int size = BlendAxis == 0 ? (int)CanvasSize.x : (int)CanvasSize.y;
    int position = BlendAxis == 0 ? texel.x : texel.y;
    float distanceToEdge = min(position + 0.5, size - position - 0.5);
    float mixAmount = 0.5 * saturate(1.0 - distanceToEdge / max(BlendWidth * size, 1.0)) * BlendStrength;
    if (mixAmount <= 0.0)
        return 0;

    int2 mirrored = BlendAxis == 0 ? int2(size - 1 - texel.x, texel.y) : int2(texel.x, size - 1 - texel.y);
    float4 a = Composite.Load(int3(texel, 0));
    float4 b = Composite.Load(int3(mirrored, 0));
    return lerp(float4(a.rgb * a.a, a.a), float4(b.rgb * b.a, b.a), mixAmount);
}

// 合成の 1 段目: 元の画像を乗算済みにして敷く
float4 PSSourcePremultiplied(FullOutput input) : SV_Target
{
    float4 source = Source.Load(int3(input.Position.xy, 0));
    if (SourceIsColor == 0)
        return float4(source.rrr, 1);
    return float4(source.rgb * source.a, source.a);
}

// 合成の 2 段目: レイヤーを下から順に重ねる。Flow にレイヤーの不透明度が入る
float4 PSLayerOver(FullOutput input) : SV_Target
{
    return Layer.Load(int3(input.Position.xy, 0)) * Flow;
}

// 合成の 3 段目: 乗算なしへ戻す (読み込んだ画像と同じ扱いにする)
float4 PSStraighten(FullOutput input) : SV_Target
{
    float4 c = Layer.Load(int3(input.Position.xy, 0));
    return c.a > 0.0 ? float4(c.rgb / c.a, c.a) : float4(0, 0, 0, 0);
}

// 大きさが変わったレイヤーの引き継ぎ
float4 PSResample(FullOutput input) : SV_Target
{
    return Layer.SampleLevel(LinearSampler, input.Uv, 0);
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
