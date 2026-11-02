namespace UskEffect.Tests;

/// <summary>
/// 境界をまたぐ差分と内部の差分の比で継ぎ目を評価する (要件 §4: 値だけでなく勾配の不連続も検査する)。
/// 継ぎ目がなければ比はおおよそ 1、継ぎ目があると大きくなる。
/// </summary>
public readonly record struct SeamReport(double ValueRatio, double GradientRatio)
{
    public override string ToString() => $"value {ValueRatio:0.00}, gradient {GradientRatio:0.00}";
}

public static class SeamMetrics
{
    public static SeamReport Horizontal(float[] pixels, int width, int height) =>
        Measure(pixels, width, height, (x, y) => pixels[y * width + x], width, height);

    public static SeamReport Vertical(float[] pixels, int width, int height) =>
        Measure(pixels, width, height, (x, y) => pixels[x * width + y], height, width);

    // at(i, line): i は評価する軸方向の位置、line はそれと直交する行
    private static SeamReport Measure(float[] pixels, int width, int height, Func<int, int, float> at, int length, int lines)
    {
        double seamValue = 0, interiorValue = 0, seamGradient = 0, interiorGradient = 0;

        for (int line = 0; line < lines; line++)
        {
            float first = at(0, line);
            float last = at(length - 1, line);
            seamValue += Math.Abs(first - last);
            seamGradient += Math.Abs(at(1, line) - 2 * first + last);
            seamGradient += Math.Abs(first - 2 * last + at(length - 2, line));

            for (int i = 1; i < length; i++)
                interiorValue += Math.Abs(at(i, line) - at(i - 1, line));
            for (int i = 1; i < length - 1; i++)
                interiorGradient += Math.Abs(at(i + 1, line) - 2 * at(i, line) + at(i - 1, line));
        }

        seamValue /= lines;
        seamGradient /= 2.0 * lines;
        interiorValue /= (double)lines * (length - 1);
        interiorGradient /= (double)lines * (length - 2);

        return new SeamReport(
            seamValue / Math.Max(interiorValue, 1e-9),
            seamGradient / Math.Max(interiorGradient, 1e-9));
    }
}
