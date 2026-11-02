namespace UskEffect.Graphics.Analysis;

/// <summary>
/// 1 軸ぶんの継ぎ目評価。境界をまたぐ差分と、境界に隣接する内部の差分の比で表す (要件 §4: 値だけでなく勾配の不連続も検査する)。
/// 継ぎ目がなければ比はおおよそ 1、継ぎ目があると大きくなる。
/// </summary>
public readonly record struct SeamReport(double ValueRatio, double GradientRatio, double InteriorValue, double InteriorGradient)
{
    /// <summary>これ未満なら値の継ぎ目なしと判定する。</summary>
    public const double SeamlessValueLimit = 1.5;

    /// <summary>これ未満なら勾配の継ぎ目なしと判定する (2 階差分は画素ごとのばらつきが大きいので緩め)。</summary>
    public const double SeamlessGradientLimit = 2.0;

    public bool IsSeamless => ValueRatio < SeamlessValueLimit && GradientRatio < SeamlessGradientLimit;

    public override string ToString() => $"value {ValueRatio:0.00}, gradient {GradientRatio:0.00}";
}

/// <summary>横 (左右の端) と縦 (上下の端) の継ぎ目評価。</summary>
public sealed record SeamAnalysis(SeamReport Horizontal, SeamReport Vertical)
{
    /// <summary>内部の隣接画素差の平均。ヒートマップの正規化に使う。</summary>
    public double MeanValueDiff => (Horizontal.InteriorValue + Vertical.InteriorValue) * 0.5;

    /// <summary>内部の 2 階差分の平均。ヒートマップの正規化に使う。</summary>
    public double MeanGradientDiff => (Horizontal.InteriorGradient + Vertical.InteriorGradient) * 0.5;
}

public static class SeamAnalyzer
{
    public static SeamAnalysis Analyze(float[] pixels, int width, int height) =>
        new(Horizontal(pixels, width, height), Vertical(pixels, width, height));

    /// <summary>左端と右端の継ぎ目。</summary>
    public static SeamReport Horizontal(float[] pixels, int width, int height) =>
        Measure((i, line) => pixels[line * width + i], width, height);

    /// <summary>上端と下端の継ぎ目。</summary>
    public static SeamReport Vertical(float[] pixels, int width, int height) =>
        Measure((i, line) => pixels[i * width + line], height, width);

    // 境界の差は、全体平均ではなく境界に隣接する差と比べる。
    // 正弦波のように構造のある画像では、境界がたまたま傾きの急な位置に来ると全体平均との比が π/2 まで膨らむため。
    private const int NeighborPairs = 2;

    // 隣接差がほぼ 0 の平坦な場所で比が暴れないよう、全体平均のこの割合を分母の下限にする
    private const double DenominatorFloor = 0.25;

    // at(i, line): i は評価する軸方向の位置、line はそれと直交する行
    private static SeamReport Measure(Func<int, int, float> at, int length, int lines)
    {
        double seamValue = 0, nearValue = 0, interiorValue = 0;
        double seamGradient = 0, nearGradient = 0, interiorGradient = 0;
        int last = length - 1;

        for (int line = 0; line < lines; line++)
        {
            float Wrap(int i) => at((i % length + length) % length, line);
            double Diff(int i) => Math.Abs(Wrap(i) - Wrap(i - 1));
            double SecondDiff(int i) => Math.Abs(Wrap(i + 1) - 2 * Wrap(i) + Wrap(i - 1));

            // 境界をまたぐのは (0, last) の差と、0 と last を中心にした 2 階差分
            seamValue += Diff(0);
            seamGradient += SecondDiff(0) + SecondDiff(last);

            for (int k = 1; k <= NeighborPairs; k++)
            {
                nearValue += Diff(k) + Diff(last - k + 1);
                nearGradient += SecondDiff(k) + SecondDiff(last - k);
            }

            for (int i = 1; i < length; i++)
                interiorValue += Diff(i);
            for (int i = 1; i < last; i++)
                interiorGradient += SecondDiff(i);
        }

        seamValue /= lines;
        seamGradient /= 2.0 * lines;
        nearValue /= 2.0 * NeighborPairs * lines;
        nearGradient /= 2.0 * NeighborPairs * lines;
        interiorValue /= (double)lines * (length - 1);
        interiorGradient /= (double)lines * (length - 2);

        return new SeamReport(
            seamValue / Math.Max(Math.Max(nearValue, interiorValue * DenominatorFloor), 1e-9),
            seamGradient / Math.Max(Math.Max(nearGradient, interiorGradient * DenominatorFloor), 1e-9),
            interiorValue,
            interiorGradient);
    }
}
