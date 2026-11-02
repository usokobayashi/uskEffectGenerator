using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using UskEffect.App.Locale;
using UskEffect.Core.Project;
using UskEffect.Graphics.Effects;

namespace UskEffect.App.Views;

public partial class TimelineBar : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool _updating;
    private bool? _shownPlaying;
    private EffectTimeline? _timeline;

    /// <summary>保存対象 (ループ・長さ) が変わったときに発生する。</summary>
    public event Action? Changed;

    public TimelineBar() => InitializeComponent();

    public void Attach(EffectTimeline timeline)
    {
        _timeline = timeline;
        PlayButton.Click += (_, _) => timeline.TogglePlay();
        StopButton.Click += (_, _) => timeline.Stop();
        LoopSwitch.IsCheckedChanged += (_, _) =>
        {
            timeline.Loop = LoopSwitch.IsChecked == true;
            Changed?.Invoke();
        };
        DurationField.ValueChanged += () =>
        {
            timeline.Duration = (double)DurationField.Value;
            Changed?.Invoke();
        };
        ScrubField.ValueChanged += (_, e) =>
        {
            if (!_updating)
                timeline.SeekNormalized(e.NewValue);
        };

        // 再生位置の表示は 30Hz で更新する (統計表示の 4Hz ではスライダーの動きがカクつく)
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public void Stop() => _timer.Stop();

    public TimelineSettings Read() => new()
    {
        Duration = (double)DurationField.Value,
        Loop = LoopSwitch.IsChecked == true,
    };

    public void Write(TimelineSettings settings)
    {
        DurationField.Value = (decimal)settings.Duration;
        LoopSwitch.IsChecked = settings.Loop;
    }

    private void Refresh()
    {
        if (_timeline is not { } timeline)
            return;

        _updating = true;
        try
        {
            ScrubField.Value = timeline.NormalizedTime;
        }
        finally
        {
            _updating = false;
        }
        TimeText.Text = Loc.F("S.Timeline.Time", timeline.Time, timeline.Duration);

        bool playing = timeline.IsPlaying;
        if (playing != _shownPlaying)
        {
            _shownPlaying = playing;
            string label = Loc.T(playing ? "S.Timeline.Pause" : "S.Timeline.Play");
            ToolTip.SetTip(PlayButton, label);
            AutomationProperties.SetName(PlayButton, label);
            if (Application.Current!.TryGetResource(playing ? "Icon.Pause" : "Icon.Play", null, out var icon))
                PlayIcon.Data = icon as Geometry;
        }
    }
}
