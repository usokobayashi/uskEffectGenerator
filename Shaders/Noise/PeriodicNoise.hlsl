// 周期ノイズ生成 (FR-01 / FR-02)
// 格子座標を周期で折り返すことで、有効な軸方向にシームレスなテクスチャを生成する。
// 周期を保つため、周波数は整数に丸め、オクターブごとに 2 倍する (lacunarity = 2 固定)。

cbuffer NoiseParams : register(b0)
{
    uint2 OutputSize;
    uint  Seed;
    uint  Octaves;
    float BaseFrequency;
    float Persistence;
    uint  SeamMode;   // bit0: X, bit1: Y
    uint  NoiseType;  // 0: Perlin, 1: Worley
};

RWTexture2D<float> Output : register(u0);

static const float TwoPi = 6.28318530718;

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

int2 WrapCell(int2 cell, int2 period)
{
    int2 wrapped = ((cell % period) + period) % period;
    return int2((SeamMode & 1u) != 0 ? wrapped.x : cell.x,
                (SeamMode & 2u) != 0 ? wrapped.y : cell.y);
}

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

// F1 距離。戻り値はおおよそ [0, 1]
float Worley(float2 p, int2 period, uint seed)
{
    int2 i = (int2)floor(p);
    float2 f = p - floor(p);
    float minDistSq = 8.0;

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
            minDistSq = min(minDistSq, dot(d, d));
        }
    }
    return sqrt(minDistSq);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= OutputSize))
        return;

    float2 uv = (id.xy + 0.5) / (float2)OutputSize;
    float frequency = max(1.0, round(BaseFrequency));
    float amplitude = 1.0;
    float sum = 0.0;
    float norm = 0.0;

    for (uint octave = 0; octave < Octaves; ++octave)
    {
        int2 period = int2((int)frequency, (int)frequency);
        uint seed = Seed + octave * 1013u;
        float n;
        if (NoiseType == 0)
            n = Perlin(uv * frequency, period, seed) * 0.5 + 0.5;
        else
            n = Worley(uv * frequency, period, seed);

        sum += n * amplitude;
        norm += amplitude;
        amplitude *= Persistence;
        frequency *= 2.0;
    }

    Output[id.xy] = saturate(sum / max(norm, 1e-5));
}
