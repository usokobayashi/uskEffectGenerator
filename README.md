# uskEffect

ゲームエフェクト向けテクスチャ制作補助ソフト（Windows スタンドアロン）。
要件は「エフェクトテクスチャ制作補助ソフト 要件定義書 v0.4」を参照。

## 現状

| Phase | 内容 | 状態 |
|---|---|---|
| 1 | GPU 描画・UI 連携の技術検証 | 完了 |
| 2 | ノイズ生成・調整 | 完了 |
| 3 | シームレス生成・極座標検証 | 完了 |

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
| プレビュー | 矩形 / 3×3 タイル / 極座標 / シーム検証、UV スクロール |
| 極座標 | 内径・外径 (内径 0 で円盤)、回転、角度方向・内外の反転。変換規約は `PolarMapping` に一本化 (+x が 0°、反時計回りに u 増加、内径で v = 0) |
| シーム検証 | 境界をまたぐ値差分・勾配 (2 階) 差分を境界に隣接する差分と比べて判定し、数値と判定を常時表示。半タイルずらし表示に、内部平均比のヒートマップを重ねる |
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
- テスト 54 件: 周期性 (全ノイズ種別 × フラクタル、X のみ、Lacunarity 3/4、ドメインワープ、Worley 内部の不連続、値域、Seed の決定性)、継ぎ目解析 (正弦波・ランプ・長方形・端での折れ)、極座標変換 (往復、規約、回転、0°/360° の連続、リング外)
- シーム検証ビュー: シーム X のみで上下の継ぎ目が中央の線として出る (比 値 19.6 / 勾配 68.4)。XY では比 0.6〜1.3

### 構成

```text
Shaders/Noise/PeriodicNoise.hlsl  周期ノイズ (Compute)
Shaders/Preview.hlsl              矩形・タイル・極座標プレビュー (後で Polar/Effect に分割)
src/UskEffect.Graphics/           D3D11 デバイス・スワップチェーン・ノイズ生成・プレビュー・GPU タイマー、PolarMapping
src/UskEffect.Graphics/Analysis/  継ぎ目解析 (SeamAnalyzer)
src/UskEffect.App/                Avalonia UI、D3DHost (NativeControlHost)、プリセット
tests/UskEffect.Tests/            周期性・継ぎ目解析・極座標変換のテスト (xunit v3 / Microsoft.Testing.Platform)
```

### 既知の制約

- 子 HWND は Avalonia の描画より常に手前に出る（airspace 問題）。キャンバス上に Avalonia のコントロールを重ねることはできない。キャンバス上のオーバーレイ（ブラシカーソル等）は D3D 側で描く前提。
- 子 HWND 上のマウス・ペン入力は Avalonia に届かない。Phase 6 以降で WndProc から `WM_POINTER` を受けて処理する（筆圧取得にも必要）。
- デバイスロストからの復帰処理は未実装。
- 極座標で内径 0 (円盤) のとき、中心 r = 0 は角度が不定なので 1 点に集まる (要件 §3.1 の「中心近傍の専用処理」は直接ペイントの Phase 8 で扱う)。
- 継ぎ目解析は生成のたびに GPU から読み戻して CPU で行う (操作が 150ms 止まってから実行)。解析は別スレッドなので描画は止まらないが、高解像度での所要時間は未計測。
- Vortice の `Compiler.Compile(string)` は非 ASCII 文字があるとソース末尾を切り捨てるため、コンパイル前に非 ASCII 文字を空白へ置換している (HLSL のコメントは日本語可)。
