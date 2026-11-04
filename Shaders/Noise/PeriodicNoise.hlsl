// 周期ノイズ生成 (FR-01 / FR-02)
// Perlin / Worley は格子座標を周期で折り返し、Simplex は 4D Simplex をトーラス上でサンプリングして、
// 有効な軸方向にシームレスなテクスチャを生成する。
// 周期を保つため、周波数と Lacunarity は整数に丸める。
// NoiseType が 4 以上は形 (グラデーション・シェイプ・放射・曲線)。こちらは周期を持たない。

cbuffer NoiseParams : register(b0)
{
    uint2 OutputSize;
    uint  Seed;
    uint  Octaves;

    float BaseFrequency;
    float Persistence;
    uint  SeamMode;      // bit0: X, bit1: Y
    uint  NoiseType;     // 0: Perlin, 1: Worley, 2: Simplex, 3: Stripe, 4 以上: 形 (Parameters.cs の NoiseType)

    float Lacunarity;
    uint  FractalMode;   // 0: FBM, 1: Ridged, 2: Turbulence
    uint  WorleyMode;    // 0: F1, 1: F2, 2: F2 - F1
    float WarpStrength;  // UV 単位

    float Contrast;
    float Brightness;
    uint  Invert;
    float _Padding;

    uint  StripeDirection;  // 0: 縦の帯, 1: 横の帯, 2: 斜めの帯
    float StripeWidth;      // 帯の幅 (周期に対する割合)
    float StripeSoftness;   // 帯の縁のぼかし (周期に対する割合)
    float _Padding2;

    float2 ShapeCenter;
    float ShapeAngle;       // ラジアン
    float ShapeSize;

    float ShapeThickness;
    float ShapeSoftness;
    float ShapeFalloff;
    float ShapeAmount;      // 弧の長さ、星の内径、帯の幅、波の振幅

    float ShapeAspect;
    float ShapeTwist;
    uint  ShapeCount;
    float _Padding3;

    float4 ShapeStops;

    float2 ShapeP0;
    float2 ShapeP1;

    float2 ShapeP2;
    float2 _Padding4;
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
// Stripe (帯)。周波数が整数なので、どの方向でも上下左右がつながる。戻り値は [0, 1]
// ---------------------------------------------------------------------------

float Stripe(float2 uv, float frequency, uint seed)
{
    float2 direction = StripeDirection == 0 ? float2(1, 0) : StripeDirection == 1 ? float2(0, 1) : float2(1, 1);
    float phase = dot(uv, direction) * frequency + (Hash(seed) & 0xFFFFu) / 65536.0;
    float halfWidth = StripeWidth * 0.5;
    float soft = max(StripeSoftness * 0.5, 1e-3);
    // 周期を折り返した、帯の中心からの距離
    float distance = abs(frac(phase) - 0.5);
    return 1.0 - smoothstep(halfWidth - soft, halfWidth + soft, distance);
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
    if (NoiseType == 3)
        return Stripe(uv, frequency, seed);

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

// ---------------------------------------------------------------------------
// 形。戻り値は [0, 1]。p は中心からの位置で、回転済み
// ---------------------------------------------------------------------------

float EdgeMask(float distance)
{
    return 1.0 - smoothstep(-ShapeSoftness, ShapeSoftness, distance);
}

float DistanceToSegment(float2 p, float2 a, float2 b)
{
    float2 pa = p - a;
    float2 ba = b - a;
    float t = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-8));
    return length(pa - ba * t);
}

// 2 次ベジェ曲線までの距離 (Inigo Quilez の解析解)
float DistanceToBezier(float2 p, float2 a0, float2 a1, float2 a2)
{
    float2 a = a1 - a0;
    float2 b = a0 - 2.0 * a1 + a2;
    if (dot(b, b) < 1e-6)
        return DistanceToSegment(p, a0, a2);

    float2 c = a * 2.0;
    float2 d = a0 - p;
    float kk = 1.0 / dot(b, b);
    float kx = kk * dot(a, b);
    float ky = kk * (2.0 * dot(a, a) + dot(d, b)) / 3.0;
    float kz = kk * dot(d, a);
    float q0 = ky - kx * kx;
    float q1 = kx * (2.0 * kx * kx - 3.0 * ky) + kz;
    float h = q1 * q1 + 4.0 * q0 * q0 * q0;
    if (h >= 0.0)
    {
        h = sqrt(h);
        float2 x = (float2(h, -h) - q1) * 0.5;
        float2 root = sign(x) * pow(abs(x), 1.0 / 3.0);
        float t = saturate(root.x + root.y - kx);
        float2 v = d + (c + b * t) * t;
        return length(v);
    }
    float z = sqrt(-q0);
    float angle = acos(q1 / (q0 * z * 2.0)) / 3.0;
    float m = cos(angle);
    float n = sin(angle) * 1.732050808;
    float2 t2 = saturate(float2(m + m, -n - m) * z - kx);
    float2 v0 = d + (c + b * t2.x) * t2.x;
    float2 v1 = d + (c + b * t2.y) * t2.y;
    return sqrt(min(dot(v0, v0), dot(v1, v1)));
}

float Burst(float angle, float radius)
{
    float count = (float)ShapeCount;
    float sector = (angle / TwoPi + 0.5) * count;
    uint index = (uint)floor(sector) % ShapeCount;
    uint h = Hash(index ^ Hash(Seed));
    float lengthScale = 0.35 + 0.65 * (h & 0xFFFFu) / 65535.0;
    float widthScale = 0.4 + 0.6 * (h >> 16) / 65535.0;
    float across = abs(frac(sector) - 0.5) * 2.0;
    float ray = saturate(1.0 - across / max(ShapeThickness * widthScale, 1e-3));
    float fade = pow(saturate(1.0 - radius / (ShapeSize * lengthScale)), ShapeFalloff);
    float core = saturate(1.0 - radius / (ShapeSize * 0.2));
    return max(ray * fade, core * core);
}

float ShapeValue(float2 uv)
{
    float2 offset = uv - ShapeCenter;
    float c = cos(ShapeAngle);
    float s = sin(ShapeAngle);
    float2 p = float2(c * offset.x + s * offset.y, -s * offset.x + c * offset.y);
    float radius = length(p);
    float angle = atan2(p.y, p.x); // -π..π
    float count = (float)ShapeCount;
    float sector = TwoPi / count;

    switch (NoiseType)
    {
    case 4: // Linear
        return pow(saturate(p.x / (2.0 * ShapeSize) + 0.5), ShapeFalloff);
    case 5: // Radial
        return pow(saturate(1.0 - radius / ShapeSize), ShapeFalloff);
    case 6: // Angular
        return pow(angle / TwoPi + 0.5, ShapeFalloff);
    case 7: // Conical。正面が明るく、真後ろが暗い
        return pow(1.0 - abs(angle) / (TwoPi * 0.5), ShapeFalloff);
    case 8: // Multi-stop。4 段を等間隔に置く
    {
        float t = saturate(p.x / (2.0 * ShapeSize) + 0.5) * 3.0;
        if (t < 1.0)
            return lerp(ShapeStops.x, ShapeStops.y, t);
        if (t < 2.0)
            return lerp(ShapeStops.y, ShapeStops.z, t - 1.0);
        return lerp(ShapeStops.z, ShapeStops.w, t - 2.0);
    }
    case 9: // Circle
        return EdgeMask(radius - ShapeSize);
    case 10: // Ring
        return EdgeMask(abs(radius - ShapeSize) - ShapeThickness * 0.5);
    case 11: // Arc。+x を中心に、Amount (0..1) の分だけ円周を覆う
    {
        float halfSweep = saturate(ShapeAmount) * TwoPi * 0.5;
        float distance = abs(radius - ShapeSize);
        if (abs(angle) > halfSweep)
        {
            float2 tip = float2(cos(halfSweep), sin(halfSweep)) * ShapeSize;
            distance = length(float2(p.x, abs(p.y)) - tip);
        }
        return EdgeMask(distance - ShapeThickness * 0.5);
    }
    case 12: // Rectangle
    {
        float2 q = abs(p) - float2(ShapeSize, ShapeSize * ShapeAspect);
        return EdgeMask(length(max(q, 0.0)) + min(max(q.x, q.y), 0.0));
    }
    case 13: // Polygon
    {
        float sides = max(count, 3.0);
        float k = TwoPi / sides;
        return EdgeMask(cos(floor(0.5 + angle / k) * k - angle) * radius - ShapeSize * cos(k * 0.5));
    }
    case 14: // Star。頂点が +x を向く
    {
        float points = max(count, 2.0);
        float k = TwoPi / points;
        float folded = abs(angle - floor(angle / k + 0.5) * k);
        float2 q = float2(cos(folded), sin(folded)) * radius;
        float2 tip = float2(ShapeSize, 0.0);
        float2 valley = float2(cos(k * 0.5), sin(k * 0.5)) * ShapeSize * saturate(ShapeAmount);
        float2 edge = valley - tip;
        float side = edge.x * (q.y - tip.y) - edge.y * (q.x - tip.x);
        return EdgeMask(DistanceToSegment(q, tip, valley) * (side > 0.0 ? -1.0 : 1.0));
    }
    case 15: // Rays。光線が中心から減衰する
    {
        float across = abs(frac(angle / sector) - 0.5) * 2.0;
        float ray = 1.0 - saturate((1.0 - across) / max(ShapeThickness, 1e-3));
        float fade = pow(saturate(1.0 - radius / ShapeSize), ShapeFalloff);
        float core = saturate(1.0 - radius / (ShapeSize * 0.2));
        return max(ray * fade, core * core);
    }
    case 16: // Spokes。幅が一定の線
    {
        float local = (frac(angle / sector + 0.5) - 0.5) * sector;
        float distance = abs(sin(local)) * radius;
        if (abs(local) > TwoPi * 0.25)
            distance = radius;
        return min(EdgeMask(distance - ShapeThickness * 0.5), EdgeMask(radius - ShapeSize));
    }
    case 17: // Radial Stripes。全面に広がる放射状の帯
    {
        float across = abs(frac(angle / sector) - 0.5);
        float halfWidth = saturate(ShapeAmount) * 0.5;
        return 1.0 - smoothstep(halfWidth - ShapeSoftness, halfWidth + ShapeSoftness, across);
    }
    case 18:
        return Burst(angle, radius);
    case 19: // Spiral
    {
        float t = angle / TwoPi * count + ShapeTwist * radius / ShapeSize;
        float across = abs(frac(t) - 0.5);
        float halfWidth = saturate(ShapeThickness) * 0.5;
        float arm = 1.0 - smoothstep(halfWidth - ShapeSoftness, halfWidth + ShapeSoftness, across);
        return arm * EdgeMask(radius - ShapeSize);
    }
    case 23: // 単色。色は合成のときに付ける
        return 1.0;
    case 20: // Bezier Curve。控えの点は回転させない
        return EdgeMask(DistanceToBezier(uv, ShapeP0, ShapeP1, ShapeP2) - ShapeThickness * 0.5);
    case 21: // Sine Wave。距離は傾きで補正する
    {
        float amplitude = ShapeAmount * 0.5;
        float w = TwoPi * count;
        float slope = amplitude * w * cos(w * p.x);
        float distance = abs(p.y - amplitude * sin(w * p.x)) / sqrt(1.0 + slope * slope);
        return EdgeMask(distance - ShapeThickness * 0.5);
    }
    default: // Ribbon。波に沿って幅がねじれるように変わる
    {
        float amplitude = ShapeAmount * 0.5;
        float w = TwoPi * count;
        float slope = amplitude * w * cos(w * p.x);
        float distance = abs(p.y - amplitude * sin(w * p.x)) / sqrt(1.0 + slope * slope);
        float halfWidth = ShapeThickness * (0.15 + 0.85 * abs(cos(w * 0.5 * p.x)));
        float shade = 0.6 + 0.4 * cos(saturate(distance / max(halfWidth, 1e-3)) * TwoPi * 0.25);
        return EdgeMask(distance - halfWidth) * shade;
    }
    }
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= OutputSize))
        return;

    float2 uv = (id.xy + 0.5) / (float2)OutputSize;

    // ドメインワープ: ずらし量も同じ周期のノイズなので、ワープ後も周期性は保たれる
    // 形には効かせない (画面に欄を出さないので、見えない設定で形を変えない)
    if (WarpStrength > 0.0 && NoiseType < 4)
    {
        float frequency = max(1.0, round(BaseFrequency));
        float2 offset = float2(BaseNoise(uv, frequency, Seed ^ 0xA511E9B3u),
                               BaseNoise(uv, frequency, Seed ^ 0x63D83595u)) * 2.0 - 1.0;
        uv += offset * WarpStrength;
    }

    // 反転してからコントラスト・明るさを掛ける (UI 上の調整が見たままの方向に効くように)
    float value;
    if (NoiseType >= 4)
        value = ShapeValue(uv);
    else
        value = Fractal(uv, Seed);
    if (Invert != 0)
        value = 1.0 - value;
    value = (value - 0.5) * Contrast + 0.5 + Brightness;

    Output[id.xy] = saturate(value);
}
