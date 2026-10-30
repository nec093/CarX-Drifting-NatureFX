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
