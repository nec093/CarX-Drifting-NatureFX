# variants

このフォルダのファイルは、バンドルのビルド時に `tools/build_bundle.sh` が Unity プロジェクトの `Assets/NatureFX/Variants/` にコピーします。役割の違う 2 種類があります。

| ファイル | 役割 |
|---|---|
| `NatureFX_variants.txt` など | マテリアルのキーワードの組み合わせ。見本マテリアルを作り、必要な組み合わせが削られないようにする |
| `recorded_variants.txt` | ゲームで実際に使われたシェーダーバリアント。これ以外をビルドしないことで、ビルドを速くする |
| `game_hdrp_*.json` `game_hdrp_levels.txt` | ゲームの HDRP 設定。ビルドの HDRP アセットをゲームと同じにして、ゲームが使わない機能のバリアントを作らない（3 章） |

## 1. マテリアルのキーワードの組み合わせ

HDRP はバンドルに入っているマテリアルが使うキーワードの組み合わせ（シェーダーバリアント）だけを残します。
NatureFX のバンドルはシェーダーだけなので、そのままだと葉の切り抜き (`_ALPHATEST_ON`) などが削られてしまいます。
そこで、ゲーム内で実際に使われている組み合わせをここに置き、ビルド時にその組み合わせの見本マテリアルをバンドルへ入れます。

### 更新のしかた

1. ゲームで各コース・KN マップを走り、NatureFX のメニュー **Tools > Dump scene shaders** を押す
   （押すたびに `kino/config/NatureFX_variants.txt` に追記されます）
2. そのファイルをこのフォルダにコピーする
3. `tools/build_bundle.sh` でバンドルを作り直す

### 書式

```
<NatureFX のシェーダー名> | <キーワード（空白区切り）>
NatureFX/Foliage | _ALPHATEST_ON _DOUBLESIDED_ON
```

ここに無い組み合わせも、`BuildNatureFXBundle.cs` の既定の組み合わせ（法線マップ・マスクマップ・切り抜き・両面の主な組み合わせ）は常に入ります。

## 2. 使われたバリアントの記録（ビルドを速くする）

草木と路面のシェーダー（`NatureFX/Foliage` `NatureFX/LitAccumulation`）は HDRP/Lit のコピーなので、HDRP の機能の組み合わせだけで数千のバリアントがあり、全部ビルドすると 1 時間前後かかります。
ゲームで実際に使われたものだけをビルドすれば、数十まで減らせます。

### 記録のしかた

1. F8 > **Tools > Shader variant recording** の **Record** をオンにする（Unity が、GPU に送ったバリアントを Player.log に書くようになる）
2. いろいろな状況で走る：DX11 と DX12、昼と夜、雨と雪、公式コースと KN マップ、写真モード、レイトレーシングのオンとオフ
3. **Save recorded variants** を押す（マップの読み込み時とゲームの終了時にも自動で保存）。`kino/config/NatureFX/recorded_variants.txt` に、前回までの記録と合わせて書かれる
4. そのファイルをこのフォルダにコピーする

記録は、**全部入りのバンドル**（`NFX_VARIANTS=full` でビルドしたもの）で行ってください。削ったバンドルで記録すると、削られたバリアントの代わりに使われた近いバリアントが記録され、足りないものに気付けません。

### ビルド

- `recorded_variants.txt` がこのフォルダにあれば、記録にあるバリアントだけをビルドします（`unity/Assets/NatureFX/Editor/RecordedVariantStripper.cs`）
- 全部ビルドするとき（配布用、記録を取り直すとき）は `NFX_VARIANTS=full tools/build_bundle.sh`
- ビルドのログに、パスごとの残した数・削った数が `[NatureFX] Recorded variants:` の行で出ます

### 注意

記録にないバリアントが必要になると、Unity は一番近いバリアントで描きます。見た目が崩れたら（影が出ない、切り抜きがおかしいなど）、その状況で記録を取り直してください。

## 3. ゲームの HDRP 設定に合わせる

HDRP は、HDRP アセットの設定（影のフィルター品質、画面空間の影、デカール、light layers など）に合わないバリアントをビルド時に削ります。
ゲームと同じ設定の HDRP アセットでビルドすれば、ゲームが使わない機能のバリアントは作られません。記録（2 章）と違い、ゲームの設定で起こりうる組み合わせはすべて残るので、配布用のビルドでも安全です。

### 更新のしかた

1. NatureFX を入れてゲームを起動する（起動時に自動で書き出します）
2. `kino/config/NatureFX/game_hdrp/` の中身（`game_hdrp_<アセット名>.json` と `game_hdrp_levels.txt`）をこのフォルダにコピーする
3. `tools/build_bundle.sh` でバンドルを作り直す。ログに `[NatureFX] Mirroring game HDRP asset` の行が出る

- `game_hdrp_levels.txt` に、ゲームの画質の段階ごとにどの HDRP アセットを使うかが書かれます。使わない段階のアセットの `.json` を消せば、その分も作りません
- ゲームの更新で HDRP の設定が変わったら、書き出し直してください
- 全部の機能を入れたいとき（NatureFX が HDRP の設定を上書きする機能を試すときなど）は `NFX_HDRP=all tools/build_bundle.sh`

このほか、ビルドではリアルタイム GI のライトマップ（`DYNAMICLIGHTMAP_ON`）と Subtractive のバリアントを常に外します。CarX では使われていません。

## 4. ゲームの描き方の事実で削る（`game_variant_profile.txt`）

2 章の記録は、製品ビルドのゲームでは Unity が「どのバリアントを使ったか」をログに書かないため、**使えない**（`Player.log` に 1 行も出ない）。
代わりに Mod が、マップを読み込むたびに **描き方の事実**を `kino/config/NatureFX/game_hdrp/game_variant_profile.txt` に書く（`src/NatureFX/Tools/VariantProfileDump.cs`）。
ビルドはそれを読んで、その事実では決して使われないバリアントをまるごと外す（`unity/Assets/NatureFX/Editor/VariantProfileStripper.cs`）。
記録と違い「次元ごと」に外すので、配布用のビルドでも安全（事実がすべてのマップを覆っている限り）。

| 行 | 意味 | 外れるもの |
|---|---|---|
| `litShaderMode=Deferred` | カメラが deferred で描く | 不透明マテリアルの Forward 系パス（Forward, TransparentDepthPrepass/Postpass, TransparentBackface, DistortionVectors）、透明の GBuffer、Forward の `USE_FPTL_LIGHTLIST`（透明はクラスタ型） |
| `directionalLightmaps=no` | 方向付きライトマップのマップが無い | `DIRLIGHTMAP_COMBINED` |
| `shadowmask=no` | シャドウマスク付きライトマップが無い | `SHADOWS_SHADOWMASK` |
| `lodCrossfade=no` | クロスフェードする LODGroup が無い | `LOD_FADE_CROSSFADE` |
| `msaa=no` | カメラが MSAA を使わない | `WRITE_MSAA_DEPTH` |

- 一度 `yes`（または `Forward`）が出た行はそのまま残る（バンドルは全マップで使うため）。行が無ければその次元は残す。
- 更新のしかた: ゲームでいろいろなマップを読み込む → `kino/config/NatureFX/game_hdrp/` の中身（json と `game_variant_profile.txt`）をこのフォルダにコピー → ビルド。ログに `[NatureFX] Variant profile:` の行で、残した数・外した数が出る。
- 全部ビルドするときは 2 章と同じ `NFX_VARIANTS=full`。
- 最初のコピー（2026-11-01）は手書きで、確実な 2 行（`litShaderMode=Deferred`、`msaa=no`）だけ。Mod を一度動かせば残りの行も埋まる。

### 1 バリアントのコンパイル時間

2026-11-01 のフルビルド（6.4 時間）では、1 バリアントの fp のコンパイルに 90〜190 CPU 秒かかっていた（HDRP 素の Lit は数秒）。
原因はハッシュ式の手続きノイズを展開ループの中で大量にインラインしていたこと。`fxc` で単体計測した結果:

| 変種 | 前 | 後 |
|---|---|---|
| LitAccumulation GBuffer fp | 11.0 s | 2.2 s |
| LitAccumulation Forward fp | 24.1 s | 8.5 s |
| Foliage Forward fp（重い組み合わせ） | 31.6 s | 11.1 s |

対策は `NFX_Common.hlsl` のノイズ（`NFX_ValueNoise`, `NFX_Hash12`）を Mod が起動時に作るテクスチャ `_NFX_NoiseTex`（`ShaderGlobals.EnsureNoiseTexture`）の参照にしたことと、落ち葉・ひび・リップル・再帰反射の `[unroll]` を `[loop]` にしたこと。
計測のやり方: HDRP パッケージへのジャンクションと `Assets/NatureFX` のコピーを短いパスに置き、`Lit.shader` の `HLSLINCLUDE` とパスの本体を 1 つの hlsl に結合して `fxc /T ps_5_0 /E Frag /O3 /I .` でコンパイルする（Unity を起動しなくてよい）。
