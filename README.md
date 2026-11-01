# NatureFX (KSL mod)

Repository: https://github.com/nec093/CarX-Drifting-NatureFX

CarX Drift Racing Online (Unity 2023.2.22f1 / HDRP) 向けのレイトレーシング・シェーダー総合Mod。

| 機能 | 仕組み | 追加アセット |
|---|---|---|
| レイトレーシング (太陽の影 / 環境光とバウンス光 / 接地影 / 反射 / 街灯の影 / 光の筋) | NatureFX が集めたシーンに独自エンジン `NFXNative.dll` (DX12、DXR 1.0 のパイプライン / DXR 1.1 のインライン) でレイを撃ち、カスタムパスで合成 | シェーダーバンドル、独自エンジンは `NFXNative.dll` (DX12) |
| レイトレーシングのみモード | ゲーム自身の画面空間エフェクトを切り、露出とトーンだけを NatureFX が受け持つグローバル HDRP Volume | 不要 |
| シェーダーリビルド | ゲームのマテリアルを NatureFX のシェーダーに置き換え (路面の摩耗・タイヤ痕、ぎらつきや透過の修正) | シェーダーバンドル |
| 濡れた路面 | トラックの HDRP/Lit マテリアルのアルベドを暗く・スムースネスを上げる | 不要 |
| 草・葉の揺れ / 突風 / 葉のはためき / 車が草を押し分ける | `NFX_Wind.hlsl` + グローバル変数。画面の揺れはマップ設定 `foliageWind = true` のときだけ、RT の影は常に揺れる。SpeedTree 用に WindZone も生成 | シェーダーバンドル |
| 雪・落ち葉・砂ぼこり・水たまりの蓄積 (時間経過で積もる) | `NFX_Accumulation.hlsl` + グローバル変数 | シェーダーバンドル |

## フォルダ構成

```
NatureFX/
├─ lib/                     KSL.API.dll, 0Harmony.dll (ksl_sdk から)
├─ src/NatureFX/         Mod 本体 (C#, net472)
│  ├─ NatureFXMod.cs     エントリポイント (BaseMod)
│  ├─ Settings.cs / Presets.cs / SettingsUI.cs
│  ├─ Rendering/            RayTracingEffects (RT パス), RTShadowPass, RTReflectionPass, RayTracingScene, NativeEngine, VolumeOverhaul
│  ├─ Nature/               Wind, Accumulation, ShaderBundle, MaterialReplacer
│  └─ Tools/                SceneShaderDumper
├─ unity/Assets/NatureFX/   Unity 側のソース (tools/build_bundle.sh が Unity プロジェクトへコピー)
│  ├─ ShaderLibrary/        NFX_Common / NFX_Wind / NFX_Accumulation / NFX_LitData (.hlsl)
│  └─ Editor/               GenerateNatureFXShaders.cs, BuildNatureFXBundle.cs
├─ tools/                   build_bundle.sh, maykr.exe (gitignore)
└─ docs/                    map-authoring.md
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

NatureFX のシェーダーは、インストールされている **HDRP/Lit をコピーして風と蓄積の処理を差し込んだもの** です
(`Editor/GenerateNatureFXShaders.cs` がビルドのたびに生成)。プロパティ名・キーワード・パスが HDRP/Lit と同じなので、
ゲームのマテリアルのシェーダーだけを差し替えても、テクスチャや設定はそのまま使われます。

| シェーダー | 中身 |
|---|---|
| `NatureFX/Foliage` | HDRP/Lit ＋ 風 (`NFX_WIND`) ＋ 蓄積 (`NFX_ACCUMULATION`) |
| `NatureFX/LitAccumulation` | HDRP/Lit ＋ 蓄積 |

差し込み方：`NFX_LitData.hlsl` が HDRP の `LitData.hlsl` を別名で読み込み、`ApplyVertexModification`
(頂点) と `GetSurfaceAndBuiltinData` (表面) を包んでいます。レイトレーシング用の SubShader は外しています。

### 手順

1. Unity **2023.2.x** (Kino Content SDK と同じ 2023.2.20f1 で確認) で空のプロジェクトを作り、
   `Packages/manifest.json` に `"com.unity.render-pipelines.high-definition": "16.0.6"` を追加する
   - Windows の「コントロールされたフォルダーアクセス」が有効なら、Documents やデスクトップの外に作る (既定は `~/NatureFX-Unity`)
2. ビルドする

   ```bash
   tools/build_bundle.sh ~/NatureFX-Unity
   ```

   `unity/Assets/NatureFX` をプロジェクトへコピーし、Unity をバッチモードで起動して
   HDRP アセットの用意 → シェーダー生成 → バンドル作成 → `kino/mods/NatureFX/naturefx.bundle` へのコピーまで行います。
   初回は HDRP の読み込みとシェーダーのコンパイルで時間がかかります。ログは `<プロジェクト>/Logs/naturefx_build.log`。

Unity エディタで開いた場合は、メニュー **NatureFX > Generate Shaders From HDRP Lit** → **NatureFX > Build And Copy To Game** でも同じことができます。

### Shader Graph で作る場合

`NFX_Wind.hlsl` / `NFX_Accumulation.hlsl` には Shader Graph の Custom Function 用の関数 (`NFX_Wind_float`, `NFX_Accumulation_float`) も入っています。
`Assets/NatureFX/Shaders/` に置いた Shader Graph もバンドルに含まれます。プロパティの Reference 名は HDRP/Lit と同じ (`_BaseColorMap` など) にしてください。

## 3. 使い方

- KSL メニュー > **NatureFX**
  - プリセット: Performance / Balanced / Quality / Ray Traced
  - Lighting / Ray tracing / Foliage & wind / Weather & accumulation / Tools
- レイトレーシングを使うには Steam の起動オプションに `-force-d3d12` を追加
  (Ray tracing 画面に「GPU / Pipeline が対応しているか」が表示されます)
- **F9**：写真モード。カメラを止めている間、レイトレーシングの効果を最高品質で積み上げ続け、ノイズのない 1 枚に近づけます
- F8 の Weather > **Season / Sky / Eye and lens**：季節の色、ボリュームの雲と雲の影・遠くの霞、暗所の見え方・レンズフレア・被写界深度
- F8 の Weather > **Rain and snow**：カメラのまわりに降る雨（着地点の水しぶき、ヘッドライトや街灯の光の中で光る）と雪。Precipitation で量（0 で消える）
- F8 の Road and cars > **Smoke and light from the cars**：光を受けるボリュームのタイヤスモークと水しぶき、ブレーキの赤熱とアフターファイア、車の泥・ほこり・雪

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

## 使用しているサードパーティ SDK

- **NVIDIA Real-Time Denoisers (NRD)**: レイトレースの影のノイズ除去（「NRD SIGMA (sun)」）に NVIDIA の NRD を使用しています。
  NVIDIA, the NVIDIA logo and NRD are trademarks and/or registered trademarks of NVIDIA Corporation.
  NRD は NVIDIA RTX SDKs License に従って配布されます（NatureFX の独自エンジン `NFXNative.dll` にオブジェクトコードとして組み込み）。
- **NVIDIA DLSS (DLAA)**: アンチエイリアス（Own post-processing の「DLAA」）に NVIDIA DLSS を使用しています。
  NVIDIA, the NVIDIA logo and DLSS are trademarks and/or registered trademarks of NVIDIA Corporation.
  DLSS は NVIDIA RTX SDKs License に従って配布されます（NGX ライブラリを `NFXNative.dll` に組み込み、実行時の `nvngx_dlss.dll` / `nvngx_dlssd.dll` を同梱。ライセンス文は `DLSS-LICENSE.txt`）。
