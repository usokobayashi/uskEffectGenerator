# uskEffect

ゲームエフェクト向けテクスチャ制作補助ソフト（Windows スタンドアロン）。
要件は「エフェクトテクスチャ制作補助ソフト 要件定義書 v0.4」を参照。

## 現状: Phase 1 GPU 描画・UI 連携の技術検証

要件定義書 §5 の「Avalonia と Direct3D 11 の描画サーフェス統合は最初に実証する」に対応するスパイク。

| 項目 | 内容 |
|---|---|
| UI | C# / .NET 10 / Avalonia 12 |
| GPU | Direct3D 11 (Vortice.Windows 3.8) |
| 統合方式 | `NativeControlHost` で子 HWND を埋め込み、フリップモデルのスワップチェーンを作成 |
| 描画ループ | 専用レンダースレッド + `Present` の VSync。`RequestAnimationFrame` 駆動では 60Hz 環境でも約 42fps で頭打ちだった |
| シェーダー | `Shaders/*.hlsl` を実行時コンパイル。保存するとホットリロードし、エラーは画面下部に表示 |
| ノイズ | Compute Shader で周期 Perlin / Worley + FBM を生成 (Seed / Frequency / Octaves / Persistence / シーム OFF・X・Y・XY) |
| プレビュー | 矩形 / 3×3 タイル / 極座標、UV スクロール |
| 計測 | タイムスタンプクエリによる GPU 時間、FPS |

### ビルド・実行

```bash
dotnet run --project src/UskEffect.App
```

プロキシ環境で NuGet が 407 を返す場合は、並列ダウンロードを止めると通ることがある。

```bash
dotnet restore --disable-parallel
```

### 確認済み (2026-11-02, RTX 3060 Laptop)

- 512×512 で Noise 生成 約 0.18ms、144Hz パネルで 144fps
- リサイズ、最小化からの復帰、DPI 120→144 のモニター移動に追従

### 構成

```text
Shaders/Noise/PeriodicNoise.hlsl  周期ノイズ (Compute)
Shaders/Preview.hlsl              矩形・タイル・極座標プレビュー (Phase 1 用。後で Polar/Effect に分割)
src/UskEffect.Graphics/           D3D11 デバイス・スワップチェーン・ノイズ生成・プレビュー・GPU タイマー
src/UskEffect.App/                Avalonia UI、D3DHost (NativeControlHost)
```

### 検証で確認すべき点

- リサイズ、DPI 変更、最小化・復帰でスワップチェーンが追従するか
- 512×512 で 60fps を維持できるか（下部ステータスに表示）
- 3×3 タイル表示でシーム OFF のときだけ継ぎ目が出ること
- ComboBox のドロップダウンなど、ポップアップが HWND の上に正しく表示されるか

### 既知の制約（この方式の性質）

- 子 HWND は Avalonia の描画より常に手前に出る（airspace 問題）。キャンバス上に Avalonia のコントロールを重ねることはできない。キャンバス上のオーバーレイ（ブラシカーソル等）は D3D 側で描く前提。
- 子 HWND 上のマウス・ペン入力は Avalonia に届かない。Phase 6 以降で WndProc から `WM_POINTER` を受けて処理する（筆圧取得にも必要）。
- デバイスロストからの復帰処理は未実装。
- Simplex ノイズは周期化の扱いが異なるため未実装。
