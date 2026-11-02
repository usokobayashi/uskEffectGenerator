# uskEffect

ゲームエフェクト向けテクスチャ制作補助ソフト（Windows スタンドアロン）。
要件は「エフェクトテクスチャ制作補助ソフト 要件定義書 v0.4」を参照。

## 現状

| Phase | 内容 | 状態 |
|---|---|---|
| 1 | GPU 描画・UI 連携の技術検証 | 完了 |
| 2 | ノイズ生成・調整 | 完了 |

| 項目 | 内容 |
|---|---|
| UI | C# / .NET 10 / Avalonia 12 |
| GPU | Direct3D 11 (Vortice.Windows 3.8) |
| 統合方式 | `NativeControlHost` で子 HWND を埋め込み、フリップモデルのスワップチェーンを作成 |
| 描画ループ | 専用レンダースレッド + `Present` の VSync。`RequestAnimationFrame` 駆動では 60Hz 環境でも約 42fps で頭打ちだった |
| シェーダー | `Shaders/*.hlsl` を実行時コンパイル。保存するとホットリロードし、エラーは画面下部に表示 |
| ノイズ | Compute Shader で生成。Perlin / Worley (F1・F2・F2−F1) / Simplex、フラクタル (FBM・Ridged・Turbulence)、Seed / Frequency / Octaves / Persistence / Lacunarity、ドメインワープ、コントラスト・明るさ・反転 |
| シームレス | OFF・X・Y・XY。Perlin / Worley は格子を周期で折り返し、Simplex は 4D Simplex をトーラス上でサンプリングする。周期を保つため Frequency と Lacunarity は整数 |
| プリセット | 標準 7 種 + ユーザー保存 (`%APPDATA%\uskEffect\noise-presets.json`) |
| プレビュー | 矩形 / 3×3 タイル / 極座標、UV スクロール |
| 計測 | タイムスタンプクエリによる GPU 時間、FPS |

### ビルド・実行・テスト

```bash
dotnet run --project src/UskEffect.App
```

```bash
dotnet test --project tests/UskEffect.Tests
```

テストは GPU でノイズを生成して読み戻し、境界をまたぐ値・勾配の差を内部の差と比べて継ぎ目を検査する（GPU がなければ WARP で動く）。

プロキシ環境で NuGet が 407 を返す場合は、並列ダウンロードを止めると通ることがある。

```bash
dotnet restore --disable-parallel
```

### 確認済み (2026-11-02, RTX 3060 Laptop)

- 512×512 で Noise 生成 約 0.2ms、144Hz パネルで 144fps
- リサイズ、最小化からの復帰、DPI 120→144 のモニター移動に追従
- 3×3 タイルでシーム XY は継ぎ目なし、OFF は継ぎ目が出る。極座標で 0°/360° が連続
- 周期性テスト 38 件 (全ノイズ種別 × フラクタル、X のみ、Lacunarity 3/4、ドメインワープ、Worley 内部の不連続、値域、Seed の決定性)

### 構成

```text
Shaders/Noise/PeriodicNoise.hlsl  周期ノイズ (Compute)
Shaders/Preview.hlsl              矩形・タイル・極座標プレビュー (後で Polar/Effect に分割)
src/UskEffect.Graphics/           D3D11 デバイス・スワップチェーン・ノイズ生成・プレビュー・GPU タイマー
src/UskEffect.App/                Avalonia UI、D3DHost (NativeControlHost)、プリセット
tests/UskEffect.Tests/            周期性テスト (xunit v3 / Microsoft.Testing.Platform)
```

### 既知の制約

- 子 HWND は Avalonia の描画より常に手前に出る（airspace 問題）。キャンバス上に Avalonia のコントロールを重ねることはできない。キャンバス上のオーバーレイ（ブラシカーソル等）は D3D 側で描く前提。
- 子 HWND 上のマウス・ペン入力は Avalonia に届かない。Phase 6 以降で WndProc から `WM_POINTER` を受けて処理する（筆圧取得にも必要）。
- デバイスロストからの復帰処理は未実装。
- Vortice の `Compiler.Compile(string)` は非 ASCII 文字があるとソース末尾を切り捨てるため、コンパイル前に非 ASCII 文字を空白へ置換している (HLSL のコメントは日本語可)。
