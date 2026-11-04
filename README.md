# uskEffect

ゲームエフェクト向けテクスチャ制作補助ソフト（Windows スタンドアロン）。
要件は「エフェクトテクスチャ制作補助ソフト 要件定義書 v0.4」を参照。

## 現状

| Phase | 内容 | 状態 |
|---|---|---|
| 1 | GPU 描画・UI 連携の技術検証 | 完了 |
| 2 | ノイズ生成・調整 | 完了 |
| 3 | シームレス生成・極座標検証 | 完了 |
| 4 | エフェクトプレビュー・タイムライン | 完了 |
| 5 | 画像出力・プロジェクト保存 | 完了 |
| - | UI の作り直し (採用モックアップ) | 実装済み |
| 6 | 画像読込・キャンバス操作 | 実装済み・実画面は未確認 |
| 7 | GPU ブラシ・消しゴム・ぼかし | 実装済み・実画面は未確認 |

| 項目 | 内容 |
|---|---|
| UI | C# / .NET 10 / Avalonia 12 |
| GPU | Direct3D 11 (Vortice.Windows 3.8) |
| 統合方式 | `NativeControlHost` で子 HWND を埋め込み、フリップモデルのスワップチェーンを作成 |
| 描画ループ | 専用レンダースレッド + `Present` の VSync。`RequestAnimationFrame` 駆動では 60Hz 環境でも約 42fps で頭打ちだった |
| シェーダー | `Shaders/*.hlsl` を実行時コンパイル。保存するとホットリロードし、エラーは画面下部に表示 |
| ノイズ | Compute Shader で生成。Perlin / Worley (F1・F2・F2−F1) / Simplex / Stripe (向き・幅・ぼかし)、フラクタル (FBM・Ridged・Turbulence)、Seed / Frequency / Octaves / Persistence / Lacunarity、ドメインワープ、コントラスト・明るさ・反転 |
| シームレス | OFF・X・Y・XY。Perlin / Worley は格子を周期で折り返し、Simplex は 4D Simplex をトーラス上でサンプリングする。周期を保つため Frequency と Lacunarity は整数 |
| プレビュー | 矩形 / 極座標 / シーム検証、UV スクロール、Tiling / Offset (表示のみ。書き出す画像には影響しない) |
| 画像読込 | PNG・JPG・BMP は WIC、TGA は自前 (8/16/24/32bit、RLE)。継ぎ目は輝度で解析。プロジェクトの ZIP 内に保存 |
| キャンバス | ホイール: カーソル位置でズーム。中ボタン or Space + ドラッグ: 移動。Shift + Space + ドラッグ: 回転。Ctrl + Space + ドラッグ: ズーム。Ctrl+0 全体表示、Ctrl+1 100% |
| 調整欄 | 「項目名 / スライダー / 数値」の行。数値はクリックかダブルクリックで入力し、外をクリックで確定、Esc で取り消し。負から正にまたがる範囲は中央へ吸着。Frequency など上限のない項目は、大きな値の入力で最大値が広がる (999 まで。次回の最大値は終了時の値、既定は下回らない。`%APPDATA%\uskEffect\slider-limits.json`) |
| Vector2 | Tiling・Offset・UV 移動は X と Y の 2 列 + リンクボタン (オンで同じ値) |
| 極座標 | 内径・外径 (内径 0 で円盤)、回転、角度方向・内外の反転。変換規約は `PolarMapping` に一本化 (+x が 0°、反時計回りに u 増加、内径で v = 0) |
| シーム検証 | 境界をまたぐ値差分・勾配 (2 階) 差分を境界に隣接する差分と比べて判定し、数値と判定を常時表示。半タイルずらし表示に、内部平均比のヒートマップを重ねる |
| エフェクト | 再生・一時停止・停止・スクラブ・ループ・長さ (下部タイムライン)。UV 移動、回転・拡縮・アルファ・色・ディゾルブを開始値→終了値で補間。ディゾルブの発光帯 (幅・色)、Alpha / Additive 合成、矩形 / 極座標で貼る、背景 (チェッカー・黒・灰)、円形ソフトマスク |
| 画像出力 | PNG / TGA。グレースケール 8bit・16bit (PNG のみ)・RGBA (RGB = 値)・RGBA (白 + A = 値)。外部ライブラリを使わない自前エンコーダー (PNG の圧縮は .NET 標準の ZLibStream) |
| プロジェクト | `.uskfx` (ZIP 内の project.json、formatVersion 付き)。ノイズ・プレビュー・エフェクト・タイムライン・書き出し設定と、読み込んだ画像 (images/source.png)、描いた内容 (images/paint.png) を保存 |
| ペイント | 上部で「ペイント」に切り替え、矩形の表示で左ドラッグ。ブラシ (B)・消しゴム (E)・ぼかし (J)、大きさは [ ]。編集レイヤーは元の画像と別に持つので、消しゴムで元に戻せる。全表示と書き出しは合成結果を使う。極座標への直接描画・周期境界をまたぐ描画は Phase 8、筆圧・元に戻す・複数レイヤーは Phase 9 |
| メニュー | 新規 (Ctrl+N)・開く (Ctrl+O)・保存 (Ctrl+S)・名前を付けて保存 (Ctrl+Shift+S)・画像を書き出し (Ctrl+E)。未保存の変更はタイトルに * を付け、閉じる・新規・開くの前に確認する |
| 画面 | 上部 (メニュー・ファイル操作・生成/ペイント)、左 (道具)、中央 (表示の切り替え + キャンバス)、右 (ノイズ・シーム・エフェクト・レイヤーのタブ)、下部 (タイムライン・ステータス)。未実装の道具・レイヤー・シーム修正は無効の見た目で場所を確保 |
| 部品 | `Controls/Ui/`: 調整欄・滑るボタン群・数値欄・スイッチ・折りたたみ見出し・色見本・三角のカラーピッカー。色と寸法と動きは `Theme/Tokens.axaml`、文言は `Locale/ja.axaml` |
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
- タイル表示でシーム XY は継ぎ目なし、OFF は継ぎ目が出る。極座標で 0°/360° が連続
- テスト 54 件: 周期性 (全ノイズ種別 × フラクタル、X のみ、Lacunarity 3/4、ドメインワープ、Worley 内部の不連続、値域、Seed の決定性)、継ぎ目解析 (正弦波・ランプ・長方形・端での折れ)、極座標変換 (往復、規約、回転、0°/360° の連続、リング外)
- シーム検証ビュー: シーム X のみで上下の継ぎ目が中央の線として出る (比 値 19.6 / 勾配 68.4)。XY では比 0.6〜1.3
- エフェクト再生中も 144fps
- テスト 88 件。Phase 5 で PNG を Windows 標準のデコーダー (WIC) で読み戻す検証、TGA のヘッダー検証、プロジェクトの保存→読込で全項目が一致すること、壊れたファイル・新しい形式の拒否を追加
- 実機: メニューから PNG 書き出し、Ctrl+S で保存、Ctrl+N の未保存確認、Ctrl+O で開いて設定が戻ること、閉じるときの確認 (二重の閉じる要求でもダイアログは 1 つ) を確認

### 構成

```text
Shaders/Noise/PeriodicNoise.hlsl  周期ノイズ (Compute)
Shaders/Preview.hlsl              矩形・タイル・極座標・シーム検証プレビュー
Shaders/Effect.hlsl               エフェクトプレビュー (背景 + 乗算済みアルファで合成)
Shaders/Paint/Paint.hlsl          ブラシ・消しゴム・ぼかしのスタンプと、元の画像への合成
src/UskEffect.Graphics/           D3D11 デバイス・スワップチェーン・ノイズ生成・プレビュー・GPU タイマー、PolarMapping
src/UskEffect.Graphics/Analysis/  継ぎ目解析 (SeamAnalyzer)
src/UskEffect.Graphics/Effects/   エフェクト設定・時間補間・タイムライン・描画
src/UskEffect.Core/Export/        PNG / TGA エンコーダーとテクスチャの書き出し
src/UskEffect.Core/Project/       プロジェクトファイル (.uskfx)
src/UskEffect.App/                Avalonia UI、D3DHost (NativeControlHost)、プリセット
src/UskEffect.App/Views/          画面の部品 (TopBar・ViewStrip・NoisePanel など)
src/UskEffect.App/Theme/          共通の定義と部品の見た目
tests/UskEffect.Tests/            周期性・継ぎ目解析・極座標変換・エフェクト・書き出し・プロジェクトのテスト (xunit v3 / Microsoft.Testing.Platform)
```

UI テストは Avalonia.Headless で画面を使わずに動く。描画負荷の計測は手動で実行する (表示しない窓に描く)。

```bash
tests/UskEffect.Tests/bin/Debug/net10.0-windows/UskEffect.Tests.exe -class UskEffect.Tests.RenderBudgetTests -explicit only -showLiveOutput
```

### 既知の制約

- 子 HWND は Avalonia の描画より常に手前に出る（airspace 問題）。キャンバス上に Avalonia のコントロールを重ねることはできない。キャンバス上のオーバーレイ（ブラシカーソル等）は D3D 側で描く前提。
- 子 HWND 上のマウス・ペン入力は Avalonia に届かない。Phase 6 以降で WndProc から `WM_POINTER` を受けて処理する（筆圧取得にも必要）。
- デバイスロストからの復帰処理は未実装。
- ホイールは Windows の「非アクティブウィンドウのスクロール」設定がオフだと子 HWND に届かないことがある。
- 16bit の PNG を読み込むと 8bit になる。
- 極座標で内径 0 (円盤) のとき、中心 r = 0 は角度が不定なので 1 点に集まる (要件 §3.1 の「中心近傍の専用処理」は直接ペイントの Phase 8 で扱う)。
- 継ぎ目解析は生成のたびに GPU から読み戻して CPU で行う (操作が 150ms 止まってから実行)。解析は別スレッドなので描画は止まらないが、高解像度での所要時間は未計測。
- HLSL の `#include` に未対応のため、極座標変換 (`PolarToUv`) は Preview.hlsl と Effect.hlsl に重複している。変更時は `PolarMapping.cs` と 3 か所をそろえる。
- Vortice の `Compiler.Compile(string)` は非 ASCII 文字があるとソース末尾を切り捨てるため、コンパイル前に非 ASCII 文字を空白へ置換している (HLSL のコメントは日本語可)。
