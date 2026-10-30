# CarX シェーダー・VFX 全面作り直し：設計と段階（レビュー用）

ゲーム（CarX Drift Racing Online）が使っているシェーダーと VFX を、すべて NatureFX 自前のものに置き換える計画です。
この文書は「何を、どの順番で、どういう作りで」作り直すかをまとめたもので、訂正・追加・判断が必要な点を洗い出すためのものです。

> 2026-10-29 時点。まだ何も実装していません。棚卸しの数字は駐車場シーン（`NatureFX_dump_20261028_161505.txt`）とゲームのログから取ったものです。

---

## 1. 目的と前提

- **目的**：見た目をすべて NatureFX の管理下に置く。素材ごとの見え方（塗装・アスファルト・葉・ガラス）と VFX（煙・火花・排気炎）を自前で作り、ゲーム側の古い作りや制限に縛られないようにする
- **一番の狙い**：レイトレーシングを「本物の素材」で計算すること。今の RT は、ゲームの素材ごとに近い色を推測した代理素材（`NFX_RTProxy`）で計算しています。自前のシェーダーならレイトレーシング用のパスを持たせられるので、反射・影・照り返しが画面の見た目と一致します
- **方針（決定済み）**：ゲームのシェーダーは**完全に置き換え**ます。対応表にない素材も汎用シェーダーへ強制的に置き換えます
- **守る制約**
  - ゲームの exe には手を加えない。置き換えは実行時に行う（今の `MaterialReplacer` と同じやり方を広げる）
  - ゲームのシェーダーグラフはコピーしない。プロパティ名と見た目を手がかりに、一から書く
  - マルチプレイには影響させない（見た目だけ。他の人の画面は変わらない）
  - Kino の配布マップでも動くこと。マップ作者の独自シェーダーは汎用シェーダーで受ける
  - NatureFX をオフにすれば元の見た目に戻ること（元の素材はメモリに残しておく）

---

## 2. 棚卸し：今ゲームが使っているもの

### 2.1 シェーダー

| ゲームのシェーダー | 使用数（描画物 / 素材） | 何に使われているか | 主なプロパティ |
|---|---|---|---|
| HDRP/Lit | 4171 / 69 | マップの大半（木の幹、街灯、柵、建物） | `_BaseColorMap` `_MaskMap` `_NormalMap` `_Metallic` `_Smoothness` |
| Shader Graphs/CarXFoliageSimple | 2578 / 29 | 葉・枝・遠景の木 | `_BaseColorMap` 拡散プロファイル、アルファ抜き、両面 |
| Shader Graphs/Colormask | 651 / 11 | 駐車車両・小物（マスクで色分けする素材）。別マップでは 166 素材 | `_BaseColorMap` `_NormalMap` `_MaskMap` `_BaseColor` |
| Shader Graphs/CarGeneric | 100 / 13 | 車の部品（キャリパー、ローター、エンジン、内装、ドライバー） | `_BaseMap` `_PackMap` `_ColormaskMap` 3色（`_BaseColor` `_SecondaryColor` `_ThirdColor`）`_EmissionMap` |
| Shader Graphs/CarPaint | 9 / 2 | 車体の塗装 | `_Roughness` `_Metalness` `_Clearcoat` 2色、`_Spectrum`（偏光）`_MaterialProperties` `_TextureMaterial` |
| CarPaintStitches / CarPaintRim | 8 / 3 | 塗装の縫い目・ホイール | CarPaint に準じる |
| CarGlass / CarGlassNoVinyls | 8 / 2 | 窓ガラス（ステッカーあり / なし） | 透過 |
| CarTireCustomization | 4 / 1 | タイヤ（サイドウォールの文字） | — |
| CarLights | 4 / 2 | ヘッドライト・テール | 発光 |
| CarBrakeRotorBuiltin | 12（ログ） | ブレーキローター | — |
| mirror_shader | 2（ログ） | ミラー | — |
| HDRP/Custom/PhysicsMotionVectors | 4 / 1 | モーションベクター用 | 置き換え対象外 |
| CarBlobshadow | 1 / 1 | 車の下の偽の影 | RT 影があるので**廃止** |
| Shader Graphs/Smoke, Mobile/Particles/Additive | 4 / 4 | 煙・パーティクル | VFX で作り直す |
| Sprites/Default, Sprites/Mask | 12 / 2 | UI・2D 表示 | **対象外**（UI は触らない） |

### 2.2 VFX（ゲームのコードにあるもの）

| ゲームのクラス | 内容 |
|---|---|
| `TireSmokeGpuEmitter` / `WheelBurnout` / `BaseWheelSmoke` | タイヤスモーク（GPU）、バーンアウト |
| `WheelsParticles`（`WheelsParticlesData`） | 路面ごとの土・砂利・草の跳ね上げ |
| `CarCollideEffects` | 衝突時の火花・破片 |
| `ExhaustSmoke` / `CarPopExhaust` / `ExternalFlameSetup` | 排気煙、アフターファイア |
| `StopLightFx` | ブレーキランプの演出 |
| `MapParticlesActivator` | マップ側のパーティクル（滝、葉など） |
| `CheckpointsSmokeSettings` | チェックポイントの煙 |
| ブロブシャドウ（`CarBlobshadow`） | 車の下の偽の影 |

NatureFX 側にはすでに、雨（`NFX_CarRain`）、DX12 用のタイヤスモーク修正（`TireSmokeDx12`）、ポストエフェクト、空、天候、RT 一式があります。

---

## 3. 作りの全体像

```
ゲームの素材 ──(翻訳器)──▶ NFX シェーダー ──▶ HDRP の G バッファ／ライティング
                               └─ NFXRayTracing パス ──▶ RT（反射・影・照り返し）
ゲームの VFX ──(停止)        NFX VFX ──▶ 自前のパーティクル描画（RT の影・光を受ける）
```

### 3.1 シェーダーの家族

すべて共通の HLSL の芯（今の `GenerateNatureFXShaders.cs` が HDRP/Lit を土台に生成している仕組み）から作ります。HDRP の照明・影・GI・デカールにそのまま乗るので、ライティングを自前で書き直す必要はありません。素材の「面の作り方」だけを自前にします。

| NFX シェーダー | 置き換えるもの | 自前で入れる表現 |
|---|---|---|
| `NatureFX/World` | HDRP/Lit、Colormask | マップ素材の共通形。経年・濡れ・積もり（今の `NFX_MaterialProfile` と `LitAccumulation`）、色マスクによる色分け |
| `NatureFX/Foliage` | CarXFoliageSimple、葉の HDRP/Lit | アルファ抜き、両面、透過光（RT と一致）、風、遠景のディザ |
| `NatureFX/Terrain` | Unity Terrain（使っているマップのみ） | レイヤーの混ぜ合わせ、濡れ |
| `NatureFX/CarPaint` | CarPaint、Stitches、Rim | クリア層、メタリックフレーク、偏光（`_Spectrum` を自前で再現）、ステッカー、汚れ・濡れ |
| `NatureFX/CarPart` | CarGeneric、BrakeRotor | 3色マスク、パックマップ、発光、熱（ローターの赤熱は将来） |
| `NatureFX/CarGlass` | CarGlass、NoVinyls | 屈折なしの透過、ステッカー、雨粒・曇り |
| `NatureFX/Tire` | CarTireCustomization | ゴムの質感、サイドウォール文字、摩耗・熱・汚れ |
| `NatureFX/CarLight` | CarLights | レンズとリフレクターの質感、点灯状態ごとの発光 |
| `NatureFX/Mirror` | mirror_shader | ミラー（RT 反射で描くのが理想） |
| `NatureFX/Generic` | 対応表にないすべて | 色テクスチャ・色・法線・金属感をプロパティ名から推測して描く受け皿 |

**全シェーダーに RT 用のパス（`NFXRayTracing`）を持たせます。**置き換え後の素材はそのまま RT のシーンに入り、代理素材は「まだ置き換えていない素材」だけに残ります（最終的には不要になります）。

### 3.2 翻訳器（素材の変換）

- ゲームのシェーダーごとに「翻訳器」を1つ作ります。ゲームのプロパティを読み、NFX のプロパティへ書き写します（例：CarGeneric の `_BaseMap` → `_BaseColorMap`、`_PackMap` の各チャンネル → 金属・粗さ・AO）
- 車の色はゲームが描画物ごと（MaterialPropertyBlock）に設定しているので、それも NFX のプロパティへ写します（RT ではすでにやっている処理）
- 対応表にない素材は `Generic` へ。プロパティ名の推測は、今 RT 用に使っている `FindProperty` の仕組みを育てて使います
- 置き換えの時機
  - マップ読み込み時
  - 車が出てきたとき、パーツや塗装を変えたとき（ゲームの `EnableCarModel` などにフック）
  - 1秒ごとの見回り（RT のシーン同期と同じもの）
- 元の素材は保持し、NatureFX をオフにすれば戻せるようにします

### 3.3 VFX

- ゲームの VFX は、該当クラスの処理を Harmony で止めるか、パーティクルを無効にします（exe は触らない）
- 発生のきっかけ（タイヤの滑り量、路面の種類、衝突、アフターファイア）は、ゲームのクラスから読んで自前の発生器に渡します
- 描画は自前のパーティクル用シェーダーで行います。RT の影・照り返し・光の筋と一致させ、煙が太陽に照らされたり、影の中で暗くなったりするようにします
- 段階1は Unity のパーティクルシステムに自前のシェーダーを載せます（作りやすく安全）。段階2で、タイヤスモークを GPU（compute）の体積的な煙へ発展させます

---

## 4. 段階

| 段階 | 内容 | できあがりの確認 |
|---|---|---|
| **0. 基盤** | 共通の芯、翻訳器の枠組み、全マップ・全車の素材を書き出す棚卸しツール（プロパティ値まで）、NFX 全シェーダーの RT パス | 棚卸しの一覧が出る。テストマップで `Generic` に置き換わる |
| **1. マップ** | `World` `Foliage` `Terrain`。HDRP/Lit・Colormask・CarXFoliageSimple を置き換え、代理素材から卒業 | 公式マップと Kino マップ数本で崩れがない。RT の反射に映る物が画面と一致する |
| **2. 車** | `CarPaint` `CarPart` `CarGlass` `Tire` `CarLight` `Mirror`。塗装の色・ステッカー・偏光を再現 | ガレージで、全塗装タイプ・ステッカー・ホイールが元と同じ色で出る |
| **3. VFX** | タイヤスモーク、跳ね上げ、火花、排気煙・炎、マップのパーティクル。ブロブシャドウ廃止 | 走行・ドリフト・衝突で全部出る。重さは計測ボタンで確認 |
| **4. 完全置換** | 残った素材をすべて `Generic` へ。置き換え漏れの警告をログに出す | 棚卸しで「元のシェーダーのまま」が 0（UI を除く） |
| **5. 仕上げ** | 重さ、バリアント削減、DX11 での見え方 | 主要マップで fps を計測し、元と比べる |

各段階の終わりに、テストマップ（`test_map`）へ確認用の物を足します。

---

## 5. 決める必要があること・リスク

1. **UI は対象外でよいか**：`Sprites/Default` などは HUD・メニュー用なので、触らない前提にしています
2. **DX11 を続けるか**：シェーダー自体は DX11 でも動かせますが、RT は DX12 専用です。DX11 の確認を省けば、開発もビルドも速くなります
3. **車の塗装の再現度**：`_Spectrum`（偏光）、ステッカー、ゲーム内の塗装エディターの全組み合わせを、元と同じ色で出す必要があります。ここが一番手間がかかり、ずれが目立つ部分です
4. **シェーダーのビルド時間**：シェーダーが増えるので、バリアントの記録と削減（`RecordedVariantStripper`）を最初から前提にします
5. **ゲームのアップデート**：プロパティ名やクラス名が変わると翻訳器が外れます。外れたら `Generic` に落ちてログを出すようにし、真っ白・真っ黒にはならないようにします
6. **マップ作者の独自シェーダー**：`Generic` の推測が外れることがあります。マップ拡張（`nfx_profile`）で素材の指定を上書きできるようにするのが安全です
7. **Harmony で止める VFX**：ゲームの処理の中で、見た目以外（音・物理）とつながっている部分は止めないようにします（例：`CarPopExhaust` は音とも連動）

---

## 6. 段階0の実装（2026-10-30）

**実装済み**（ビルドが通った状態で、ゲームでの確認はまだ）：

- **RT パス**：`NatureFX/LitAccumulation` と `NatureFX/Foliage` に `NFXRayTracing` パス（`ShaderLibrary/NFX_RTLitHit.hlsl`）を付けました。生成時に HDRP の RT 用 SubShader を差し替えます。素材の値はラスター描画と同じ `UnityPerMaterial` から読むので、SRP Batcher の一貫性も保たれます。マスクマップの金属・ツヤ、アルファ抜きもそのまま反映されます
- **RT シーン**：`NFXRayTracing` パスを持つ素材は代理素材を使わず、**その素材のまま** RT のシーンに入ります（ログの「instances with NatureFX materials」）
- **翻訳の枠組み**：差し替え前に全プロパティを `MaterialSnapshot` に保存します。`GenericTranslator` が、よくある名前（`_BaseMap` `_MainTex` `_BumpMap` `_Roughness` など）から HDRP/Lit の名前へ書き写します。NatureFX をオフにすると、スナップショットから元の値に戻ります
- **完全置換**：ルールに当たらない素材も、すべて Generic の受け皿へ回します。後の段階で作り直すもの（車、VFX、透明、Unlit）と、面でないもの（UI、空、デカール）は理由を付けて残し、F8 > Settings > Shader rebuild に件数を出します
- **棚卸し**：マップを読み込むたびに `kino/config/NatureFX/inventory/<シーン名>.txt` を書きます。全素材について、元のシェーダー、全プロパティの値、キーワード、使われている場所、置き換え先（または残した理由）を記録します
- **RT 専用モードでも置き換える**ようにしました（これからの作り直しは RT 前提のため）

**設計からの変更**：Generic 用に新しい HDRP/Lit のコピーを作ると、全バリアントのビルドに約1時間かかります。そこで段階0では、既にビルド・記録済みの `NatureFX/LitAccumulation`（地面・建物）と `NatureFX/Foliage`（葉）を受け皿に使っています。どちらも天候の積もりや濡れに対応した Lit なので、マップの面の受け皿としてそのまま使えます。

**注意**：新しく置き換わる素材のキーワードの組み合わせは、まだバリアントの記録にありません。記録にない組み合わせは、実行時に近いバリアントで代用されます。F8 > Tools > Shader variant recording で記録してからバンドルを作り直すと、正しい見た目になります。

## 7. 最初に着手するもの（当初の案）

段階0の「棚卸しツール」と「共通の芯＋`Generic`」から始めるのがおすすめです。全素材の正確な一覧があれば、段階1〜4の対応表を数字で決められます。また `Generic` があれば、「完全置換」の方針どおり、未対応の素材も最初から置き換えた状態で開発を進められます。
