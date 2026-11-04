# uskEffect

ゲームエフェクト向けのノイズテクスチャと、グラデーションや形のテクスチャを作る Windows アプリです。
レイヤーで重ねて形を作り、極座標での確認と書き込みができ、白黒マスクや透過素材として書き出せます。

開発中のプレビュー版です。

## ダウンロード

[Releases](../../releases) から `uskEffect-*-win-x64.exe` をダウンロードして実行します。インストールと .NET の準備は不要です。

- 対応環境: Windows 10 / 11 (64bit)、Direct3D 11
- 初回の起動は、ファイルを展開するので少し時間がかかります。

## できること

| 機能 | 内容 |
|---|---|
| ノイズ | Perlin、Worley、Simplex、Stripe。重ね方は FBM、Ridged、Turbulence。シームレスに生成できる |
| 形 | 単色、グラデーション (Linear、Radial、Angular、Conical、Multi-stop)、シェイプ (Circle、Ring、Arc、Rectangle、Polygon、Star)、放射 (Rays、Spokes、Radial Stripes、Burst)、曲線 (Spiral、Bezier Curve、Sine Wave、Ribbon) |
| レイヤー | ノイズ、ペイント、画像の 3 種類。合成モード (乗算、加算、減算、スクリーン、オーバーレイ)、マスク、色の反転、不透明度、レイヤーごとの Tiling と Offset |
| ペイント | ブラシ、消しゴム、ぼかし。筆圧と、端をまたぐ描画に対応 |
| 表示 | 矩形、極座標、タイリング (4×4)。極座標の表示にも直接描ける |
| 書き出し | PNG、TGA。白黒 (8bit / 16bit) とアルファ (黒を透明に変換、RGBA)。書き出しの画面で解像度の変更と色乗せができる |
| 保存 | プロジェクトを `.uskfx` で保存して、続きから編集できる |

### 使い方の例: ノイズの中心に穴をあける

1. 新規で始めて、種類から Perlin を選びます。
2. 左下のレイヤーのアイコンを押し、「+」から「ノイズ」を選んで Circle を選びます。
3. レイヤーの効果を「減算」にします。
4. 書き出しの画面で形式を「アルファ」にすると、穴の部分は透明になります。

### 主な操作

| 操作 | 内容 |
|---|---|
| ホイール | カーソルの位置でズーム |
| 中ボタン、または Space + ドラッグ | 移動 |
| Shift + Space + ドラッグ | 回転 |
| B / E / J | ブラシ / 消しゴム / ぼかし |
| `[` / `]` | ブラシの大きさ |
| Ctrl+Z / Ctrl+Y | 元に戻す / やり直す |
| Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+E | 新規 / 開く / 保存 / 書き出し |

## 既知の制約

- ノイズのレイヤーには描けません。ペイントのレイヤーに描きます。
- 形は周期を持たないので、シームレスとドメインワープは効きません。
- Multi-stop は 4 段を等間隔に置くだけで、段の位置は動かせません。
- 解像度を変えると、描いた内容は拡大縮小され、元に戻す履歴は消えます。
- GPU のリセットからは自動で復帰しますが、元に戻す履歴は消え、最後に操作が落ち着いた後の描画は失われます。
- 16bit の PNG を読み込むと 8bit になります。

## 開発

C# / .NET 10 / Avalonia 12 / Direct3D 11 (Vortice.Windows)。シェーダーは `Shaders/` の HLSL を実行時にコンパイルします。

```bash
dotnet run --project src/UskEffect.App
```

```bash
dotnet test --project tests/UskEffect.Tests
```

単体で動く exe を作ります (`.artifacts/publish` に出力)。

```bash
dotnet publish src/UskEffect.App -c Release -p:PublishProfile=win-x64
```

プロキシ環境で NuGet が 407 を返す場合は、`dotnet restore --disable-parallel` で通ることがあります。

## ライセンス

[MIT License](LICENSE)

配布する実行ファイルには、次のオープンソースソフトウェアが含まれます。いずれも MIT License です。

- [.NET](https://github.com/dotnet/runtime)
- [Avalonia](https://github.com/AvaloniaUI/Avalonia)
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)
