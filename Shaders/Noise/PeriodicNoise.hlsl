// 周期ノイズ生成 (FR-01 / FR-02)
// Perlin / Worley は格子座標を周期で折り返し、Simplex は 4D Simplex をトーラス上でサンプリングして、
// 有効な軸方向にシームレスなテクスチャを生成する。
// 周期を保つため、周波数と Lacunarity は整数に丸める。

cbuffer NoiseParams : register(b0)
{
    uint2 OutputSize;
    uint  Seed;
    uint  Octaves;

    float BaseFrequency;
    float Persistence;
    uint  SeamMode;      // bit0: X, bit1: Y
    uint  NoiseType;     // 0: Perlin, 1: Worley, 2: Simplex

    float Lacunarity;
    uint  FractalMode;   // 0: FBM, 1: Ridged, 2: Turbulence
    uint  WorleyMode;    // 0: F1, 1: F2, 2: F2 - F1
    float WarpStrength;  // UV 単位

    float Contrast;
    float Brightness;
    uint  Invert;
    float _Padding;
};

RWTexture2D<float> Output : register(u0);

static const float TwoPi = 6.28318530718;

// ---------------------------------------------------------------------------
// Hash
// ---------------------------------------------------------------------------

// PCG hash
uint Hash(uint x)
{
    uint state = x * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

uint HashCell(int2 cell, uint seed)
{
    return Hash(asuint(cell.x) ^ Hash(asuint(cell.y) ^ Hash(seed)));
}

uint HashCell4(int4 cell, uint seed)
{
    return Hash(asuint(cell.x) ^ Hash(asuint(cell.y) ^ Hash(asuint(cell.z) ^ Hash(asuint(cell.w) ^ Hash(seed)))));
}

int2 WrapCell(int2 cell, int2 period)
{
    int2 wrapped = ((cell % period) + period) % period;
    return int2((SeamMode & 1u) != 0 ? wrapped.x : cell.x,
                (SeamMode & 2u) != 0 ? wrapped.y : cell.y);
}

// ---------------------------------------------------------------------------
// Perlin (周期格子)
// ---------------------------------------------------------------------------

float2 Gradient(int2 cell, int2 period, uint seed)
{
    uint h = HashCell(WrapCell(cell, period), seed);
    float angle = (h & 0xFFFFu) * (TwoPi / 65536.0);
    return float2(cos(angle), sin(angle));
}

// 戻り値はおおよそ [-1, 1]
float Perlin(float2 p, int2 period, uint seed)
{
    int2 i = (int2)floor(p);
    float2 f = p - floor(p);

    float n00 = dot(Gradient(i,              period, seed), f);
    float n10 = dot(Gradient(i + int2(1, 0), period, seed), f - float2(1, 0));
    float n01 = dot(Gradient(i + int2(0, 1), period, seed), f - float2(0, 1));
    float n11 = dot(Gradient(i + int2(1, 1), period, seed), f - float2(1, 1));

    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return lerp(lerp(n00, n10, u.x), lerp(n01, n11, u.x), u.y) * 1.41421356;
}

// ---------------------------------------------------------------------------
// Worley (周期格子)。戻り値は (F1, F2)
// ---------------------------------------------------------------------------

float2 Worley(float2 p, int2 period, uint seed)
{
    int2 i = (int2)floor(p);
    float2 f = p - floor(p);
    float f1 = 8.0;
    float f2 = 8.0;

    [unroll]
    for (int y = -1; y <= 1; ++y)
    {
        [unroll]
        for (int x = -1; x <= 1; ++x)
        {
            int2 offset = int2(x, y);
            uint h = HashCell(WrapCell(i + offset, period), seed);
            float2 jitter = float2(h & 0xFFFFu, h >> 16) / 65535.0;
            float2 d = offset + jitter - f;
            float distSq = dot(d, d);
            f2 = min(f2, max(f1, distSq));
            f1 = min(f1, distSq);
        }
    }
    return sqrt(float2(f1, f2));
}

// ---------------------------------------------------------------------------
// Simplex 4D (Gustavson の 4D Simplex。順位付けは Ashima webgl-noise と同じ方式)
// 勾配は (0,±1,±1,±1) の 32 方向をハッシュで選ぶ。戻り値はおおよそ [-1, 1]
// ---------------------------------------------------------------------------

float4 Gradient4(uint h)
{
    float3 s = float3((h & 4u) != 0 ? -1.0 : 1.0,
                      (h & 8u) != 0 ? -1.0 : 1.0,
                      (h & 16u) != 0 ? -1.0 : 1.0);
    uint zeroAxis = h & 3u;
    if (zeroAxis == 0) return float4(0.0, s);
    if (zeroAxis == 1) return float4(s.x, 0.0, s.yz);
    if (zeroAxis == 2) return float4(s.xy, 0.0, s.z);
    return float4(s, 0.0);
}

float SimplexCorner(float4 x, int4 cell, uint seed)
{
    float t = max(0.6 - dot(x, x), 0.0);
    t *= t;
    return t * t * dot(Gradient4(HashCell4(cell, seed)), x);
}

float Simplex4D(float4 p, uint seed)
{
    const float F4 = 0.309016994374947451; // (sqrt(5) - 1) / 4
    const float G4 = 0.138196601125010504; // (5 - sqrt(5)) / 20

    float4 i = floor(p + dot(p, F4.xxxx));
    float4 x0 = p - i + dot(i, G4.xxxx);

    // 各成分の大小順位 (0..3) から、通過する単体の頂点オフセットを求める
    float4 rank;
    float3 isX = step(x0.yzw, x0.xxx);
    float3 isYZ = step(x0.zww, x0.yyz);
    rank.x = isX.x + isX.y + isX.z;
    rank.yzw = 1.0 - isX;
    rank.y += isYZ.x + isYZ.y;
    rank.zw += 1.0 - isYZ.xy;
    rank.z += isYZ.z;
    rank.w += 1.0 - isYZ.z;

    float4 i3 = saturate(rank);
    float4 i2 = saturate(rank - 1.0);
    float4 i1 = saturate(rank - 2.0);

    float4 x1 = x0 - i1 + G4;
    float4 x2 = x0 - i2 + 2.0 * G4;
    float4 x3 = x0 - i3 + 3.0 * G4;
    float4 x4 = x0 - 1.0 + 4.0 * G4;

    int4 c = (int4)i;
    float n = SimplexCorner(x0, c, seed)
            + SimplexCorner(x1, c + (int4)i1, seed)
            + SimplexCorner(x2, c + (int4)i2, seed)
            + SimplexCorner(x3, c + (int4)i3, seed)
            + SimplexCorner(x4, c + 1, seed);
    return n * 27.0;
}

// 周期化する軸は円周 (半径 = 周波数 / 2π で円周長が周波数と一致) に、しない軸は直線に写す
float4 TorusCoord(float2 uv, float frequency)
{
    float radius = frequency / TwoPi;
    float2 angle = uv * TwoPi;
    float2 xPair = (SeamMode & 1u) != 0 ? float2(cos(angle.x), sin(angle.x)) * radius : float2(uv.x * frequency, 17.31);
    float2 yPair = (SeamMode & 2u) != 0 ? float2(cos(angle.y), sin(angle.y)) * radius : float2(uv.y * frequency, 41.73);
    return float4(xPair, yPair);
}

// ---------------------------------------------------------------------------
// 合成
// ---------------------------------------------------------------------------

// 基本ノイズを 1 オクターブ分評価する。戻り値はおおよそ [0, 1]
float BaseNoise(float2 uv, float frequency, uint seed)
{
    int2 period = int2((int)frequency, (int)frequency);

    if (NoiseType == 1)
    {
        float2 f = Worley(uv * frequency, period, seed);
        if (WorleyMode == 1)
            return saturate(f.y * 0.75);
        if (WorleyMode == 2)
            return saturate((f.y - f.x) * 1.5);
        return saturate(f.x);
    }
    if (NoiseType == 2)
        return Simplex4D(TorusCoord(uv, frequency), seed) * 0.5 + 0.5;

    return Perlin(uv * frequency, period, seed) * 0.5 + 0.5;
}

float ShapeOctave(float n)
{
    if (FractalMode == 1)
    {
        float ridge = 1.0 - abs(n * 2.0 - 1.0);
        return ridge * ridge;
    }
    if (FractalMode == 2)
        return abs(n * 2.0 - 1.0);
    return n;
}

float Fractal(float2 uv, uint seed)
{
    float frequency = max(1.0, round(BaseFrequency));
    float lacunarity = max(1.0, round(Lacunarity));
    float amplitude = 1.0;
    float sum = 0.0;
    float norm = 0.0;

    for (uint octave = 0; octave < Octaves; ++octave)
    {
        float n = BaseNoise(uv, frequency, seed + octave * 1013u);
        sum += ShapeOctave(n) * amplitude;
        norm += amplitude;
        amplitude *= Persistence;
        frequency *= lacunarity;
    }
    return sum / max(norm, 1e-5);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= OutputSize))
        return;

    float2 uv = (id.xy + 0.5) / (float2)OutputSize;

    // ドメインワープ: ずらし量も同じ周期のノイズなので、ワープ後も周期性は保たれる
    if (WarpStrength > 0.0)
    {
        float frequency = max(1.0, round(BaseFrequency));
        float2 offset = float2(BaseNoise(uv, frequency, Seed ^ 0xA511E9B3u),
                               BaseNoise(uv, frequency, Seed ^ 0x63D83595u)) * 2.0 - 1.0;
        uv += offset * WarpStrength;
    }

    // 反転してからコントラスト・明るさを掛ける (UI 上の調整が見たままの方向に効くように)
    float value = Fractal(uv, Seed);
    if (Invert != 0)
        value = 1.0 - value;
    value = (value - 0.5) * Contrast + 0.5 + Brightness;

    Output[id.xy] = saturate(value);
}
