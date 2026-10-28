# NatureFX (KSL mod)

Repository: https://github.com/nec093/CarX-Drifting-NatureFX

CarX Drift Racing Online (Unity 2023.2.22f1 / HDRP) 向けのシェーダー・ライティング総合Mod。

| 機能 | 仕組み | 追加アセット |
|---|---|---|
| ライティング刷新 (SSGI / SSR / AO / コンタクトシャドウ / ボリュメトリックフォグ / ACES / ブルーム) | 優先度 10000 のグローバル HDRP Volume を実行時に生成。ON にした項目だけ上書きし、残りはトラック側の Volume がそのまま効く | 不要 |
| レイトレーシング (RTGI / RTR / RTAO, Mixed モード) | HDRP の Ray Casting Mode = Mixed。画面内はレイマーチ、画面外だけ DXR | 不要 (DX12 + ゲーム側 HDRP 設定が必要) |
| フォトモード・パストレーシング (Ctrl+P) | HDRP Path Tracing ボリューム。カメラ静止中にサンプル蓄積 | 不要 (同上) |
| 濡れた路面 | トラックの HDRP/Lit マテリアルのアルベドを暗く・スムースネスを上げる | 不要 |
| 草・葉の揺れ / 突風 / 葉のはためき / 車が草を押し分ける | `NFX_Wind.hlsl` + グローバル変数。SpeedTree 用に WindZone も生成 | シェーダーバンドル |
| 雪・落ち葉・砂ぼこり・水たまりの蓄積 (時間経過で積もる) | `NFX_Accumulation.hlsl` + グローバル変数 | シェーダーバンドル |

## フォルダ構成

```
NatureFX/
├─ lib/                     KSL.API.dll, 0Harmony.dll (ksl_sdk から)
├─ src/NatureFX/         Mod 本体 (C#, net472)
│  ├─ NatureFXMod.cs     エントリポイント (BaseMod)
│  ├─ Settings.cs / Presets.cs / SettingsUI.cs
│  ├─ Rendering/            Capabilities, VolumeOverhaul
│  ├─ Nature/               Wind, Accumulation, ShaderBundle, MaterialReplacer
│  └─ Tools/                SceneShaderDumper
└─ unity/Assets/NatureFX/  Unity 2023.2.22f1 HDRP プロジェクトにコピーする部分
   ├─ ShaderLibrary/        NFX_Common / NFX_Wind / NFX_Accumulation (.hlsl)
   ├─ Shaders/              ← ここに Shader Graph を作る
   └─ Editor/               BuildNatureFXBundle.cs
```

## 1. Mod 本体のビルド

事前に [ksl_sdk](https://github.com/trbflxr/ksl_sdk) の `sdk/API/` から `KSL.API.dll` と `0Harmony.dll` を `lib/` にコピーしてください (詳細は `lib/README.md`)。
Unity / HDRP の DLL はインストール済みのゲームから参照します。

```bash
cd src/NatureFX
dotnet build -c Release
```

ゲームが別の場所にある場合は `-p:GameDir="D:\SteamLibrary\steamapps\common\CarX Drift Racing Online"`。
`-p:DeployToGame=true` を付けると `kino/mods` に生 .dll をコピーします (KSL の開発者モードが必要)。

一般配布用の `.ksm` は KSL の開発者権限を取ってから maykr でビルドします
(`ksl/doc/guide/dev/developer_rights.md`, `maykr.md` を参照)。

## 2. シェーダーバンドルの作成 (草の揺れ・蓄積に必要)

1. Unity Hub で **Unity 2023.2.22f1** をインストールし、HDRP テンプレートで新規プロジェクトを作る
   (バージョンが違うとバンドルが読み込めない / シェーダーが壊れます)
2. `unity/Assets/NatureFX` をプロジェクトの `Assets/` にコピー
3. `Assets/NatureFX/Shaders/` に下記 3 つの **HDRP Lit Shader Graph** を作る
   (Graph Inspector で Precision = **Single** にすること)
4. メニュー **NatureFX > Set Game Folder...** → **NatureFX > Build And Copy To Game**
   → `kino/mods/NatureFX/naturefx.bundle` が作られる

### Shader Graph の作り方

Blackboard のプロパティは **HDRP/Lit と同じ Reference 名** にしてください。
こうすることで、シェーダーを差し替えてもゲームのテクスチャ・色がそのまま引き継がれます。

| Reference | 型 |
|---|---|
| `_BaseColorMap` | Texture2D |
| `_BaseColor` | Color |
| `_NormalMap` | Texture2D (Normal) |
| `_NormalScale` | Float |
| `_MaskMap` | Texture2D (R=Metallic, G=AO, A=Smoothness) |
| `_Metallic`, `_Smoothness` | Float |
| `_AlphaCutoff` | Float (Foliage のみ) |

`_NFX_*` のグローバル変数は **Blackboard に追加しないでください** (HLSL 側で宣言済み)。

**NatureFX/Foliage** (Graph の名前は Graph Inspector のパスで `NatureFX/Foliage` に)
- Surface: Opaque, Alpha Clipping ON, Double Sided ON (Normal Mode: Flip or Mirror)
- Vertex:
  - Custom Function (File = `NFX_Wind.hlsl`, Name = `NFX_Wind`)
  - 入力: Position(Absolute World), Position(Object), Weight = Vertex Color.R (グラスカードなら UV0.y), Stiffness = 0
  - 出力 `OutPositionOS` → Vertex **Position**
- Fragment: 下の Accumulation ノードを通して BaseColor / Smoothness / Metallic に接続 (Exposure = 1, Porosity = 0.3)
- Transmission / Thickness を少し入れると逆光の葉がきれいになります (Material Type: Translucent)

**NatureFX/LitAccumulation** (地面・岩・道路・屋根用)
- 通常の Lit 構成に Custom Function (File = `NFX_Accumulation.hlsl`, Name = `NFX_Accumulation`) を挟む
- 入力: Position(Absolute World), Normal Vector(World), BaseColor, Smoothness, Metallic, Exposure = 1, Porosity = 1
- 出力: `OutBaseColor` / `OutSmoothness` / `OutMetallic` → 各スロット
- `NormalFlatten` → Lerp(サンプルした法線, (0,0,1), NormalFlatten) → Normal (Tangent)

## 3. 使い方

- KSL メニュー > **NatureFX**
  - プリセット: Performance / Balanced / Quality / Ray Traced
  - Lighting / Ray tracing / Foliage & wind / Weather & accumulation / Tools
- **Ctrl+P**: フォトモード (パストレーシング) の切り替え
- レイトレーシングを使うには Steam の起動オプションに `-force-d3d12` を追加
  (Ray tracing 画面に「GPU / Pipeline が対応しているか」が表示されます)

### シェーダー差し替えルール

初回起動時に `kino/config/NatureFX.rules.txt` が作られます。

```
<ゲームのシェーダー名 正規表現> | <名前 正規表現> => <NFX シェーダー名>
```

- 区切りは前後に空白のある ` | ` です。空白なしの `|` は正規表現の「または」として扱われます
- `<名前 正規表現>` は **マテリアル名** と、そのマテリアルを使う各オブジェクトの **階層パス** (最大 3 階層、例: `Track/Road/main_LOD0`) の両方に当てはめます
- CarX のマテリアル名はほとんどが `0x4C6A925E` のような番号に置き換えられているので、オブジェクト名で書くのが確実です
- マテリアルは共有されるため、1 つでも当てはまるオブジェクトがあれば、そのマテリアルを使う全オブジェクトが変わります

例 (道路と柵に雪・濡れを足す):

```
(?i)^HDRP/Lit$ | (?i)(road|asphalt|concrete_barrier) => NatureFX/LitAccumulation
```

**Tools > Dump scene shaders** で、いま読み込まれているシーンのシェーダーごとに、プロパティ一覧と
マテリアルごとの使用数・オブジェクトの階層パスが `kino/config/NatureFX_dump_*.txt` に出力されます。
それを見てルールを調整し、**Reload replacement rules** で再適用します。

## マップ作者向け

KN マップ側から揺れ方・雪の積もり方・区域ごとの天候を指定する仕様（草案）は [docs/map-authoring.md](docs/map-authoring.md) にあります。

## 既知の制限 / 未検証事項

- このコードは実機のゲーム DLL に対してコンパイル済みですが、**ゲーム内での動作はまだ未検証** です
- ゲームの HDRP アセットで Ray Tracing / SSGI / Volumetrics が無効化されてビルドされている場合、その機能は使えません
  (Mod は自動で検出してスクリーンスペース版にフォールバックします)
- パストレーシングはカメラが動くとリセットされるため、走行中ではなくスクリーンショット用です
- ゲームのアップデートでシェーダー名が変わると差し替えルールの更新が必要です
- HLSL ファイルは Unity 上でまだコンパイルしていません。Shader Graph 作成時にエラーが出たら知らせてください
