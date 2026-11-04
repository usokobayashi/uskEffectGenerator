using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace UskEffect.App.Locale;

/// <summary>コードから文言を引く。XAML は DynamicResource で同じキーを参照する。</summary>
public static class Loc
{
    /// <summary>キーの文言を返す。見つからないときはキーをそのまま返す (漏れが画面で分かるように)。</summary>
    public static string T(string key) =>
        Application.Current is { } app && app.TryGetResource(key, null, out var value) && value is string text ? text : key;

    public static string F(string key, params object?[] args) => string.Format(T(key), args);
}

/// <summary>動きの設定。OS のアニメーション効果がオフのときは全ての動きを止めて、最終状態をすぐ見せる。</summary>
public static class Motion
{
    private const string RevealClass = "reveal";

    public static bool Enabled { get; private set; } = true;

    public static void Apply(Application app, bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
        {
            // ControlTheme より Application.Styles の方が優先されるので、テーマの Transitions を消せる
            app.Styles.Add(new Style(x => x.Is<Control>())
            {
                Setters = { new Setter(Animatable.TransitionsProperty, null) },
            });
            return;
        }

        // ポップアップは開くときに、欄は表示されるときに動かす
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, e) =>
        {
            if (e.NewValue is true && popup.Child is { } child)
                RevealPopup(child);
        });
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true && control.Classes.Contains(RevealClass))
                Reveal(control);
        });
    }

    /// <summary>少し下から上がって現れる。</summary>
    public static void Reveal(Visual target, double fromOpacity = 0) => Run(target, translateY: 4, scale: 1, fromOpacity);

    /// <summary>前の位置から今の位置へ滑る。並びが変わったときに使う。</summary>
    public static void SlideFrom(Visual target, double offsetY) => Run(target, offsetY, scale: 1, fromOpacity: 1);

    /// <summary>少し小さい状態から現れる。ポップアップ用。</summary>
    public static void RevealPopup(Visual target) => Run(target, translateY: 0, scale: 0.97, fromOpacity: 0);

    private static void Run(Visual target, double translateY, double scale, double fromOpacity)
    {
        if (!Enabled || Application.Current is not { } app)
            return;

        var from = new KeyFrame { Cue = new Cue(0) };
        var to = new KeyFrame { Cue = new Cue(1) };
        from.Setters.Add(new Setter(Visual.OpacityProperty, fromOpacity));
        to.Setters.Add(new Setter(Visual.OpacityProperty, 1d));

        // ボタンなど、押したときの動きで別の変形を持つものは色の濃さだけで見せる
        if (target.RenderTransform is null or TransformGroup)
        {
            from.Setters.Add(new Setter(TranslateTransform.YProperty, translateY));
            to.Setters.Add(new Setter(TranslateTransform.YProperty, 0d));
            from.Setters.Add(new Setter(ScaleTransform.ScaleXProperty, scale));
            to.Setters.Add(new Setter(ScaleTransform.ScaleXProperty, 1d));
            from.Setters.Add(new Setter(ScaleTransform.ScaleYProperty, scale));
            to.Setters.Add(new Setter(ScaleTransform.ScaleYProperty, 1d));
        }

        var animation = new Animation
        {
            Duration = app.TryGetResource("Ue.Motion.Popup", null, out var duration) && duration is TimeSpan time ? time : TimeSpan.FromSeconds(0.15),
            Easing = app.TryGetResource("Ue.Ease", null, out var ease) && ease is Easing easing ? easing : new CubicEaseOut(),
            Children = { from, to },
        };
        _ = animation.RunAsync(target);
    }
}
