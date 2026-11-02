using System.Numerics;
using UskEffect.Graphics;
using UskEffect.Graphics.Effects;

namespace UskEffect.Tests;

public sealed class EffectTimelineTests
{
    [Fact]
    public void Paused_DoesNotAdvance()
    {
        var timeline = new EffectTimeline();
        Assert.Equal(0, timeline.Advance(0.5));
    }

    [Fact]
    public void Loop_WrapsAroundDuration()
    {
        var timeline = new EffectTimeline { Duration = 2, Loop = true };
        timeline.Play();

        Assert.Equal(1.5, timeline.Advance(1.5), 9);
        Assert.Equal(0.25, timeline.Advance(0.75), 9);
        Assert.True(timeline.IsPlaying);
    }

    [Fact]
    public void NoLoop_StopsAtEnd_AndPlayRestartsFromBeginning()
    {
        var timeline = new EffectTimeline { Duration = 2, Loop = false };
        timeline.Play();

        Assert.Equal(2, timeline.Advance(5), 9);
        Assert.False(timeline.IsPlaying);

        timeline.Play();
        Assert.Equal(0, timeline.Time);
        Assert.True(timeline.IsPlaying);
    }

    [Fact]
    public void Seek_ClampsAndKeepsPlayState()
    {
        var timeline = new EffectTimeline { Duration = 2 };
        timeline.Play();

        timeline.SeekNormalized(0.5);
        Assert.Equal(1, timeline.Time, 9);
        Assert.True(timeline.IsPlaying);

        timeline.Seek(-3);
        Assert.Equal(0, timeline.Time);
        timeline.Seek(10);
        Assert.Equal(2, timeline.Time);
    }

    [Fact]
    public void Stop_ResetsToBeginning()
    {
        var timeline = new EffectTimeline { Duration = 2 };
        timeline.Play();
        timeline.Advance(1.2);

        timeline.Stop();

        Assert.Equal(0, timeline.Time);
        Assert.False(timeline.IsPlaying);
    }

    [Fact]
    public void ShorteningDuration_ClampsTime()
    {
        var timeline = new EffectTimeline { Duration = 4 };
        timeline.Seek(3);

        timeline.Duration = 1;

        Assert.Equal(1, timeline.Time);
        Assert.Equal(1, timeline.NormalizedTime);
    }
}

public sealed class EffectEvaluatorTests
{
    private static readonly EffectSettings Animated = new()
    {
        Rotation = new(0f, 90f),
        Scale = new(1f, 2f),
        Alpha = new(1f, 0f),
        Color = new(new Vector3(1, 0, 0), new Vector3(0, 0, 1)),
    };

    [Fact]
    public void Interpolates_LinearlyOverNormalizedTime()
    {
        var mid = EffectEvaluator.Evaluate(Animated, new PreviewSettings(), 1, 0.5);

        Assert.Equal(MathF.PI / 4, mid.RotationRadians, 1e-5f);
        Assert.Equal(1.5f, mid.Scale, 1e-5f);
        Assert.Equal(0.5f, mid.Alpha, 1e-5f);
        Assert.Equal(new Vector3(0.5f, 0, 0.5f), mid.Color);
    }

    [Fact]
    public void Endpoints_MatchStartAndEnd()
    {
        var start = EffectEvaluator.Evaluate(Animated, new PreviewSettings(), 0, 0);
        var end = EffectEvaluator.Evaluate(Animated, new PreviewSettings(), 2, 1);

        Assert.Equal(1f, start.Scale);
        Assert.Equal(2f, end.Scale);
        Assert.Equal(new Vector3(0, 0, 1), end.Color);
    }

    [Fact]
    public void Scroll_IsSpeedTimesTime_WrappedToOneTile()
    {
        var preview = new PreviewSettings { ScrollSpeedU = 0.3f, ScrollSpeedV = -0.25f };

        var frame = EffectEvaluator.Evaluate(new EffectSettings(), preview, 10.5, 0.5);

        // 0.3 × 10.5 = 3.15 → 0.15、-0.25 × 10.5 = -2.625 → 0.375
        Assert.Equal(0.15f, frame.ScrollOffset.X, 1e-4f);
        Assert.Equal(0.375f, frame.ScrollOffset.Y, 1e-4f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.08f)]
    [InlineData(0.3f)]
    public void DissolveThreshold_ZeroKeepsEverything_OneRemovesEverything(float edge)
    {
        // テクスチャ値 0 でも発光帯・ぼかしの外 (完全に残る) になる
        float none = EffectEvaluator.DissolveThreshold(0, edge);
        Assert.True(none + edge + EffectEvaluator.DissolveSoftness <= 1e-6f);

        // テクスチャ値 1 でもしきい値以下 (消える) になる
        Assert.True(EffectEvaluator.DissolveThreshold(1, edge) >= 1f - 1e-6f);
    }
}
