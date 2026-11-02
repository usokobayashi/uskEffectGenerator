using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
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

/// <summary>OS のアニメーション効果がオフのとき、全ての動きを止めて最終状態を即時に表示する。</summary>
public static class Motion
{
    public static void Apply(Application app, bool enabled)
    {
        if (enabled)
            return;
        // ControlTheme より Application.Styles の方が優先されるので、テーマの Transitions をまとめて無効にできる
        app.Styles.Add(new Style(x => x.Is<Control>())
        {
            Setters = { new Setter(Animatable.TransitionsProperty, null) },
        });
    }
}
