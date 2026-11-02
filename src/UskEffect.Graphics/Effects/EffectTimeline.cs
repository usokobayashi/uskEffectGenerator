namespace UskEffect.Graphics.Effects;

/// <summary>
/// エフェクトプレビューの再生位置 (FR-05: 再生・停止・スクラブ・ループ)。
/// レンダースレッドが <see cref="Advance"/> で進め、UI スレッドが再生操作とシークを行うのでロックで保護する。
/// </summary>
public sealed class EffectTimeline
{
    private readonly Lock _sync = new();
    private double _time;
    private double _duration = 2.0;
    private bool _isPlaying;
    private bool _loop = true;

    public const double MinDuration = 0.05;

    public double Time
    {
        get { lock (_sync) return _time; }
    }

    /// <summary>0..1 の再生位置。</summary>
    public double NormalizedTime
    {
        get { lock (_sync) return _time / _duration; }
    }

    public double Duration
    {
        get { lock (_sync) return _duration; }
        set
        {
            lock (_sync)
            {
                _duration = Math.Max(MinDuration, value);
                _time = Math.Min(_time, _duration);
            }
        }
    }

    public bool IsPlaying
    {
        get { lock (_sync) return _isPlaying; }
    }

    public bool Loop
    {
        get { lock (_sync) return _loop; }
        set { lock (_sync) _loop = value; }
    }

    /// <summary>末尾で止まっている状態から再生したときは先頭から再生し直す。</summary>
    public void Play()
    {
        lock (_sync)
        {
            if (!_loop && _time >= _duration)
                _time = 0;
            _isPlaying = true;
        }
    }

    public void Pause()
    {
        lock (_sync) _isPlaying = false;
    }

    public void TogglePlay()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    /// <summary>停止して先頭へ戻す。</summary>
    public void Stop()
    {
        lock (_sync)
        {
            _isPlaying = false;
            _time = 0;
        }
    }

    /// <summary>スクラブ。再生中ならその位置から再生を続ける。</summary>
    public void Seek(double time)
    {
        lock (_sync) _time = Math.Clamp(time, 0, _duration);
    }

    public void SeekNormalized(double normalized)
    {
        lock (_sync) _time = Math.Clamp(normalized, 0, 1) * _duration;
    }

    /// <summary>経過時間ぶん進める。ループしないときは末尾で止まる。現在の時刻を返す。</summary>
    public double Advance(double deltaSeconds)
    {
        lock (_sync)
        {
            if (!_isPlaying || deltaSeconds <= 0)
                return _time;

            _time += deltaSeconds;
            if (_time >= _duration)
            {
                if (_loop)
                {
                    _time %= _duration;
                }
                else
                {
                    _time = _duration;
                    _isPlaying = false;
                }
            }
            return _time;
        }
    }
}
