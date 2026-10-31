# 引き継ぎ資料（2026-11-01 時点）

次のセッションがこのファイルと `AGENT.md` を読めば作業を続けられるようにまとめたもの。
基本ルール（日本語で返答、署名キー、push、ダウンロード許可、ビルド方法）は `AGENT.md` にある。ここには **今の状態と次にやること** だけを書く。

---

## 0. 最初に確認すること

1. **シェーダーバンドルのビルドが終わったか** → **11-01 01:51 に完了・エラーなし・インストール済み**（以下は次にビルドしたときの手順として残す）
   - ログ: `C:\Users\kppn1223\NatureFX-Unity\Logs\naturefx_build.log`
   - 2026-11-01 時点で `NatureFX/LitAccumulation` の `Forward` (fp) を 2136 バリアント中 1879 までコンパイル中（約 0.2 バリアント/秒、エラーなし）。この後に Foliage など他のシェーダーが続く。バックグラウンドのタスク名は bxlf17x13（出力の最後に grep の結果が出る）。全 hlsl 共通の変更（アンチタイリング）でキャッシュが無効になったので非常に長い（数時間）。
   - 終わったら次を確認する:
     ```bash
     L=/c/Users/kppn1223/NatureFX-Unity/Logs/naturefx_build.log
     grep -a "Shader error" $L | sort | uniq -c
     grep -a "lost keywords\|variant materials" $L
     ```
   - 前回の失敗: `invalid subscript 'tangentWS'`（LitDataIndividualLayer.hlsl:79、Foliage と LitAccumulation）。`NFX_AntiTiling.hlsl` の `UVMapping` に `tangentWS` / `bitangentWS` を常に持たせて直した。これが再発していないかを見る。
   - `variant_054 (NatureFX/LitAccumulation) lost keywords: _NORMALMAP` は前回も出ていた既知の警告（1 件）。
2. **ゲームが起動中か**
   - 起動中ならバンドルは `kino/mods/NatureFX/reload/naturefx-<日時>.bundle` に置かれる → Developer > Reload shaders で読める。
   - 閉じたら `C:\Users\kppn1223\NatureFX-Unity\Build\NatureFX\naturefx.bundle` を `C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online\kino\mods\NatureFX\` にコピーする（起動中は上書き禁止。壊れたバンドルを読んでしまう）。
3. **git の状態**: 最後のコミットは `0093a94`（2026-10-31 02:22）。それ以降の作業（41 ファイル変更 + 新規ファイル多数）は **すべて未コミット**。ユーザーの方針は「ゲームで確認してからコミット」で、コミット前に必ずユーザーに聞く。push はしない。

---

## 0.5 コードレビュー（2026-11-01）の結果

`docs/review-2026-11-01.md` に不具合 41 件・性能 20 件の一覧（レベル付き、修正済み/未修正）。重要な点:

- **ビルド中のバンドルは法線が壊れる**（B1: `NFX_AntiTiling.hlsl` が `SURFACE_GRADIENT` 未定義で HDRP の法線サンプラーをコンパイルしていた）。修正は入れたが共通 hlsl なので **もう一度フルビルド**が要る。同じビルドで B16（雪の影パスの法線）、B26（ヘックスの歪み）、P12 も入る。
- C# 側の修正（反射の `ViewProj` 転置、暗視/F9 の初期化順、Sun override 解除時の月、Tire marks オフ時の例外、遠景ヘイズのトラック霧上書き、UI 2 件、NRE 2 件、RT シーンの VRAM 増加、RT 全オフ時のシーン構築、DX11 の煙パッチ）は Debug ビルドが通った状態。**Release ビルドと .ksm の入れ替えはまだ**。
- ネイティブ側（解放済み BLAS を次のビルドまで保持、GI キャッシュの解放漏れ、記述子ヒープ枯渇の表示）は `cl /Zs` の構文チェックのみ。**DLL は未ビルド**（`build_native.ps1` はゲームフォルダへコピーするので、ゲームを閉じてから）。
- 未修正で判断がいるもの: マップ読み込みごとの PNG 書き出し（P1）、`ClearScene` を呼ばない記述子ヒープのリーク（B19）、NRD の行列の利き手（B21）、`CheckScene` の同期待ち（P3）。

## 0.6 シェーダービルドの時間（2026-11-01）

11-01 のフルビルドは **6.4 時間**（ログの `finished in` を合計）。内訳: LitAccumulation Forward fp 45%、同 GBuffer fp 25%、Foliage Forward fp 14%、同 GBuffer fp 7%。1 バリアントの fp に 90〜190 CPU 秒（HDRP 素の Lit は数秒）。二つの対策を入れた（`unity/variants/README.md` 4 章）:

1. **1 バリアントを速く**: `NFX_Common.hlsl` のノイズをテクスチャ参照にした（Mod が起動時に `_NFX_NoiseTex` を作る、`ShaderGlobals.EnsureNoiseTexture`）。落ち葉・ひび・リップル・再帰反射の `[unroll]` を `[loop]` に。fxc 単体計測で GBuffer 11→2.2 秒、Forward 24→8.5 秒、Foliage Forward 32→11 秒。
   **新しいバンドルは新しい .ksm が必要**（テクスチャが無いとノイズが一定値になり、雪・濡れ・落ち葉の模様が消える）。両方そろえて入れ替える。
2. **変種を減らす**: 2 章の「記録」は製品ビルドのゲームでは Unity がログを書かず使えない（`Player.log` に 0 行、`recorded_variants.txt` は空）。代わりに Mod が描き方の事実を `kino/config/NatureFX/game_hdrp/game_variant_profile.txt` に書き（`Tools/VariantProfileDump.cs`）、`Editor/VariantProfileStripper.cs` がそれで削る。`unity/variants/game_variant_profile.txt` は手書きの初版（`litShaderMode=Deferred`、`msaa=no`）で、Forward 系パスは不透明の組み合わせから外れる（前回の 60% 分）。**次にゲームを動かしたら `game_hdrp/` の中身を `unity/variants/` にコピーする**と、方向付きライトマップ・シャドウマスク・LOD クロスフェードの事実も入り、さらに減る。
   `litShaderMode=Deferred` が間違っていれば、置換した不透明マテリアルが Forward 変種を見つけられず崩れて見える（その場合はその行を消す）。見込みのフルビルドは 30〜60 分。

**今ゲームに入っているバンドル（11-01 01:51）は B1 の法線のバグを含む。** 次のビルドで直る。
→ 11-01 02:46 に B1・B16・B26・P12 とビルド時間対策の入ったフルビルドを開始（ログは `NatureFX-Unity/Logs/naturefx_build.log`）。ゲームが閉じていればそのままゲームのフォルダにコピーされる。終わったら `Shader error` と所要時間（`finished in`）を確認する。
→ **03:13 に完了、27 分**（6.4 時間から）、シェーダーエラーなし、既知の `variant_054 lost _NORMALMAP` のみ。バンドルは 168 MB → 33 MB。ゲームのフォルダにインストール済み（.ksm・DLL とそろった）。ゲームで未確認: 法線（B1）、雪・濡れ・落ち葉の模様（ノイズのテクスチャ化）、不透明の置換マテリアルが崩れないか（`litShaderMode=Deferred` の前提）。

## 0.7 11-01 の新しいバンドルでのゲーム確認（ユーザー）

- 全体は問題なし（不透明の置換マテリアルの崩れなし → `litShaderMode=Deferred` の前提は当面 OK）。
- **未解決 1: 雲が赤い**（11 時のつもりで夕焼けのような赤い空と霞。「CarX の雲が表示されている」とも）。原因未特定。候補: 太陽の色温度（`SunController` は高度 0〜30° で 2200 K から上げる。`SunMaxElevation` が低い、または時刻がゲーム側のまま）、雨の霧に赤い太陽光が散乱。ユーザーに F8 の時刻・Map カードの Sky type・Clouds のオンオフを確認してもらう。
- **未解決 2: 雨 + ヘッドライトで車の前に線（画面の上下いっぱいの白い筋、曲がったものも）**。ヘッドライトを消すと消える。有力: 自前の雨（`Precipitation.cs`、`NFX_Particle.shader` の mode 1）がランプの光（`ParticleLighting`、`facing * 0.15`）で明るくなりすぎ。CarX のヘッドライトの強さ（cd）や点光源の有無は未確認。F8 の Rain and snow を 0 にして消えるかで切り分ける。前回の「謎のフィルタ」と同じ現象の可能性。
- **修正済み（未インストール）: `No more space in Reflection Probe Atlas` が 5000 回以上**。`MapProbes` の自動プローブ 16 個がゲームの解像度で HDRP のアトラスを埋めていた → 128 に固定（`ProbeSettings.cubeResolution` は internal なのでリフレクション）。11-01 03:40 に Release の .ksm をインストール済み。
- 切り分け結果（ユーザー）: 時刻は NatureFX の 11 時。Sky type は `HDRISky, day`（テストマップなのに。ログでは parking の Volume が sky type 1 を選んでいて、テストマップ読み込み後に Track sky type の変化が出ていない）。**Clouds をオフにしても雲は消えない → 雲は HDRI の写真**。**Rain and snow を 0 にしても線は消えない → 自前の雨ではない**。次の候補: HDRP の体積フォグ（ヘッドライトの volumetricDimmer）か RT の Light shafts。案: 時刻を NatureFX が動かすときは、昼も物理ベースの空（＋自前の体積雲）に切り替える（ユーザーの判断待ち）。
- **修正（11-01 03:54 に Release の .ksm をインストール済み、ゲームで未確認）**:
  - 空: ユーザーの方針「常に物理ベースの空、基本的にすべて物理ベースの演算に」。`VolumeOverhaul.UpdateSky` は NatureFX が有効な間ずっと sky type を PhysicallyBased に上書き（トラックの PBS の設定値は上書きしないので通る）。写真の空を夜に暗くする処理（DimSky・間接光の倍率）は無効化（`night = 0`）。星もどのマップでも出るようになる。
  - ヘッドライトの線: ユーザーの切り分けで「Cars in the rain」をオフにすると消えた。`CarRain` が車の全レンダラーを水滴シェーダーで描き直していて、ヘッドライトのビーム（車の前に伸びる半透明のコーン）に水の筋が描かれていた → サブメッシュ単位で、半透明かつ加算／名前が light・beam・flare・glow 等のマテリアルを除外（`IsLightEffect`）。窓ガラスは残す。→ **ゲームで直っていなかった**（空・プローブは確認済み OK）。03:59 に追加: 車のコライダーの範囲（+1.2 m）からはみ出すレンダラーを除外し、除外したものを `Car rain leaves out ...` としてログに出す。ゲームで未確認（ログで何が除外されたかを見る）。
- 11-01 ユーザー確認: 空（常に物理ベース）OK、ヘッドライトの線 OK（コライダー範囲での除外が効いた）、プローブのエラーなし・落ちていない。
- **体積雲のリアル化と風（11-01 04:07 に .ksm インストール、ゲームで未確認）**: `CloudController` を HDRP の Advanced モードに。Mod が起動時にノイズから雲の種類のマップ（積雲・高層雲・積乱雲・雨、256²、R8）を作り、天候の雲量と雨で各倍率を決める（倍率は 0.02 刻み: 変わるたびに HDRP がクラウドマップを焼き直すため）。Worley の侵食・微細侵食・多重散乱・粉効果・ステップ増。風は `_NFX_Wind`（WindController）の向きと強さから km/h（地上の約 2.5 倍、最低 15 km/h）、向きは +x から +z への角度（HDRP は向きの方向へマップを動かす。逆向きならここ）。雲海は従来どおり Simple モード。Advanced の生成シェーダーは HDRP が体積雲の初期化で必ず読むので、雲が出ているゲームには入っている。
- 体積雲がゲームで描かれていなかった（ユーザー）。有力: ゲームのカメラのフレーム設定で VolumetricClouds がオフ（HDRP は `frameSettings.IsEnabled(VolumetricClouds)` がないと描かない。以前の Simple モードの雲も実は出ていなかった可能性）。`CameraVolumetrics` をフィールド指定の汎用クラスにして、`CameraClouds` で雲がオンの間だけ上書き（F8 の Map タブに「Volumetric clouds:」の行）。HDRP アセット側で非対応なら Sky の行に `NOT DRAWN` と出す。04:2x に .ksm インストール、ゲームで未確認。
- カメラの VolumetricClouds をオンにしても雲は出なかった（F8: `VolumetricClouds on (switched on by NatureFX)`、NOT DRAWN なし、cover 80%）。調べたこと: 雲のコンピュートシェーダーはバンドルに全変種あり（DX12 fix で差し替え済み、IsSupported の警告なし）。Unity プロジェクトの HDRP アセットも supportVolumetricClouds 1。合成シェーダー `VolumetricCloudsCombine`（ピクセルシェーダー、ゲーム側のまま）は未確認。次の候補: 霞・雨の霧（`MapFog` の haze は 3.5〜40 km × FogScale、高さ +1500 m まで）が km 先の雲を隠している。Sky の行に `[HDRP: <モード>, fog <平均自由行程> m up to <高さ> m]` を追加（04:28 .ksm）。晴れで出るか確認してもらう。
- 雲は晴れでも出なかった（ユーザー、11-01）。**方針転換: ユーザーの決定で、直接光・空・体積雲・霧を独自エンジンへ移す**。計画は `docs/native-atmosphere-lighting-plan.md`（段階 1a 空 → 1b 雲 → 1c 環境光 → 2 霧 → 3 直接光、各段階に F8 のトグル）。HDRP の雲（`CloudController` の Advanced モード化とカメラ設定の上書き）はこの段階 1b で置き換える。
- **段階 1a（独自エンジンの空）実装済み・ゲームで未確認（11-01 04:47 DLL、04:50 .ksm、04:52 バンドル（エラーなし）、すべてインストール済み）**:
  - 独自エンジン: `shaders/Sky.hlsli`（大気の定数・媒質・位相関数・Bruneton と Hillaire のパラメータ化・生バッファの双線形 `NFX_LUT_SAMPLE`）、`SkyTransmittance.hlsl`（256×64）、`SkyMultiScatter.hlsl`（32×32、64 方向）、`SkyView.hlsl`（192×108、32 ステップ）。`TraceKind` 9〜11（光線は使わない。トレースの仕組みだけ借りる）。`Frame.ShaftParams`（未使用だった）を `SkyParams`（xyz 大気の外の太陽 lux、w カメラの高さ）に改名、`LightCount.y` がエアロゾル、`.z` がオゾン。
  - C#: `NativeEngine.TraceSky`（透過率・多重散乱はエアロゾルが変わったときと Reload shaders の後だけ、スカイビューは毎フレーム。Unpack4 で RTHandle へ）。`RTShadowPass` が BeginFrame の後に呼ぶ（影のパスが動いているとき＝RT の影・AO・光の筋のどれかがオン）。太陽の値は `SunController.TopOfAtmosphereLux`（トラックの元の太陽の強さ × Sun intensity）、エアロゾルは 1 + 2×雲 + 2×雨。
  - 合成: `NFX_RTShadow.shader` の `CompositeSurface` で空の画素を `NativeSky()` に（スカイビューを方向で引く＋太陽の円盤（周辺減光つき）×透過率）。夜（太陽 -2〜-7°）は HDRP の空（星・月）を足す。パラメータ化は Sky.hlsli と同じものを複製しているので、変えるときは両方。
  - 設定: `NativeSky`（移行 42 でオン）、F8 の Sky カードに「Native sky」スイッチと状態の行。
- 段階 1a: ユーザー確認「かなりいい感じ」。
- **段階 1b（独自エンジンの体積雲）実装中（11-01 05:00）**: `CloudNoise.hlsl`（種類 12: 64³ Perlin-Worley、32³ Worley、256² 雲の種類マップを、透過率の表と同じ「空のデータ」バッファの後ろに作る。オフセットは Sky.hlsli）、`Clouds.hlsl`（種類 13: 1/4 解像度、雲層 1.5〜5.5 km、64 ステップ＋太陽へ 6 ステップ、Wrenninge の多重散乱 3 オクターブ、HG 2 ローブ、粉効果、環境光はスカイビューから、遠くは霞に溶ける）。定数は `Issue` の kindParams（AmbientParams＝雲量: 積雲・高層雲・積乱雲・密度、ReflParams＝風のずれ・経過時間・霞の距離）、Probe にスカイビュー、Screen に空のデータ。`PrepareClouds`（ブリッジ）が方向と空かどうかを作る。`CloudController.UpdateNative` が雲量と風（m/s、地上の 2.5 倍、最低 15 km/h）を計算、Native sky オンのとき HDRP の雲とカメラの雲設定は使わない。合成は `NativeSky(dir, hdrp, uv)` で空×透過率＋雲の光、太陽の円盤も雲で隠れる、星も雲で隠れる。**時間方向の蓄積はまだない**（ジッタは画素ごとに固定）。雲の影（地面）もまだ。
  - インストール: .ksm 05:00、DLL とバンドル 05:02（エラーなし）。**ユーザー確認「めっちゃいい感じ」（11-01）**。残り: 時間方向の蓄積（縁のざらつき）、雲の影（`SunShadow.hlsl` の可視度に雲層の透過率を掛ける）、段階 1c（独自の空から環境光）。
- **未解決 3: ときどきゲームが落ちる**。`Player.log`（LocalLow/CarX Technologies/Drift Racing Online、前回は `Player-prev.log`）では 2 回とも `CreateCommittedResource ... failed (887a0005)`、`Device Remove Reason 0x887a0006`（DXGI_ERROR_DEVICE_HUNG = GPU のタイムアウト）。VRAM は 7.0/11.2 GB で不足ではない。マップを読み込んで十数分走った後で、直前のログはプローブのエラーで埋まっていて手がかりなし。RT のパスは `IsScreenCamera` でプローブのカメラを飛ばしている。プローブ修正後も落ちるなら、独自エンジン（NRD・GI キャッシュ・揺れる葉）を一つずつ切って切り分ける。

## 1. インストール済みのもの（ゲームに入っている）

| 物 | 状態 |
|---|---|
| Mod 本体 `NatureFX.ksm` | 11-01 02:46 の Release ビルド（レビューの C# 修正と `_NFX_NoiseTex` の生成を含む）。インストール済み |
| `NFXNative.dll` | 11-01 02:47 のビルド（レビューのネイティブ修正を含む）。インストール済み |
| テストマップ `test_map.knmap` | 苗木の小枝・街灯のガラス修正版（10-31 19:28、`KN_Base/maps`） |
| シェーダーバンドル | アンチタイリング入り（11-01 01:51、シェーダーエラーなし、`variant_054 lost _NORMALMAP` の既知警告 1 件のみ）。ゲームのフォルダにコピー済み。**法線マップが傾くバグ（レビュー B1）を含む**: 次のフルビルドで直る。そのビルドのバンドルは `_NFX_NoiseTex` を作る新しい .ksm とセットで入れる |

---

## 2. 直近の作業と、ゲームでの確認状況

### 確認済み（ユーザーが確認）
- Light shafts を有効にすると夜空が明るくなる問題 → 直った。

### 実装済み・ゲームで未確認（ユーザーに確認してもらう項目）
| 項目 | 何を見るか | 主なファイル |
|---|---|---|
| **アンチタイリング（ヘックス・タイリング）** | テストマップの道路・路肩・林床・苔の岩・U 字溝で模様の繰り返しや継ぎ目が消えているか。F8 > Shader rebuild の「Anti-tiling」スライダー（0 で全体オフ） | `unity/.../ShaderLibrary/NFX_AntiTiling.hlsl`、`NFX_LitData.hlsl`、`GenerateNatureFXShaders.cs`（`_NFX_Surface`）、`Map/MaterialProfiles.cs`（`antitiling=` `variation=` `variation_size=`）、`Nature/AccumulationController.cs`（`_NFX_AntiTiling`） |
| 広い範囲の色むら（`variation=`） | アスファルト・コンクリート・岩・土で数十 m 単位の明るさむら | 同上 |
| 星 | 明るさとサイズが現実的か（等級から照度、9000 個、天の川・大気光） | `Map/MapNight.cs` の `Stars()` |
| フォグ | 霧のライトがブロック状に見えないか（品質 2、スライス分布 0.55、デノイズ Both） | `Map/MapFog.cs` |
| ヘッドライトの「謎のフィルタ」 | 車の前のもや（volumetricDimmer を ×4 から ×1.5 に） | `Rendering/HeadlightController.cs` |
| 光芒の合成 | 空の画素を飛ばし、透過率を掛けて足す | `Shaders/NFX_RTShadow.shader`、`Rendering/RTShadowPass.cs` |
| マーカー表示 | Map Markers の表示が崩れないか（ラベルの重なり回避、近い面でのクリップ） | `Map/MarkerOverlay.cs` |
| Foliage too の FPS | 揺れる葉 BLAS の更新を 64 個/フレームに制限した効果 | `native/NFXNative/src/Wind.inl` |
| 紫色のパーティクル | マップの ParticleSystem で HDRP のパスがないマテリアルを差し替え | `Map/MapParticles.cs` |
| 夜の空が白い | カメラごとのボリュームスタックで空の種類を読み、深夜は PhysicallyBasedSky に | `Rendering/VolumeOverhaul.cs`（`Stack()`、`SkyState`） |
| 苗木（fir_sapling / pine_sapling_small） | 小枝が描かれているか、街灯のガラス | `tools/kino_sdk/NfxDataTestMap.cs`、`tools/kino_sdk/polyhaven_import.py` |

その他、`docs/map-extension-plan.md` の段階 1.5〜5（天候ゾーン、光源マーカー、葉の色、立体の草、デカール・水・煙など）も実装済み・ゲームで未確認。

---

## 3. 次にやること

### 3.1 バンドルの確認と報告（すぐ）
0 章のとおりエラーを確認し、ユーザーに「再起動（またはReload shaders）して 2 章の項目を見てください」と伝える。

### 3.2 シェル法の苔・草（ユーザー承認済み、アンチタイリングの確認後に着手）
ユーザーの依頼: 「大量のコケや草をパフォーマンスを落とさずに使える技術（髪の毛等）」。ユーザーに説明した設計:

- **仕組み**: 地面のメッシュを法線方向に少しずつ浮かせて 8〜24 枚重ねて描く。各層はノイズで決めた「毛の断面」以外を clip する。
- **シェーダー**: 生成している Lit の複製として `NatureFX/Shell` の変種を作る（`NFX_SHELL` の define）。HDRP の通常マテリアルの複製なので影・照明・RT・雪や濡れがそのまま効く。
  - 層の番号は MaterialPropertyBlock の `_NFX_ShellLayer` で渡し、同じメッシュを層の数だけ描く（`Graphics.RenderMesh` 系、`ForestRenderer` / `GrassRenderer` のやり方を参考に）。
  - 根元ほど暗く（毛の間の陰影）。草は上の層ほど風で揺らす（`NFX_Wind.hlsl`）。
- **重さ**: 距離で層の数を減らす / 遠くは描かない。
- **指定**: マップのプロファイルに `[shells]` セクションを作り、素材名の正規表現で「苔・芝・短い草」を指定する（`MapProfile.cs` / `MaterialProfiles.cs` の書式に合わせる）。`docs/map-authoring.md` にも書式を追記する。
- **注意**:
  - 新しいシェーダー変種はバンドルの変種リスト（`unity/variants/NatureFX_variants.txt`、記録した変種）に入らないと使えない。`BuildNatureFXBundle.cs` で確実に入るようにする。
  - 共通 hlsl を変えるとまた数時間のビルドになる。シェル用のコードは `NFX_SHELL` の中だけに置き、なるべく新しい hlsl ファイルにまとめる。
  - テストマップ（`NfxDataTestMap.cs`）に確認用の苔の岩・芝の区画を足し、`[shells]` を書く。

### 3.3 コミット
ユーザーがゲームで確認したら、コミットしてよいか聞く。変更が多いので、機能ごとに分けたコミットを提案する（マップ拡張の段階 1.5〜5、夜空・星、フォグ・光芒、アンチタイリング、テストマップと素材の取り込み、揺れる葉の負荷）。`NatureFX_maykr.kmc` は含めない。

---

## 4. ハマりどころ（今回の作業で分かったこと）

- **HDRP のボリューム**: `VolumeManager.instance.stack` は既定値だけを持つ（空の種類 0 と出た）。カメラごとの値は `HDCamera.GetOrCreate(cam).volumeStack`。`VolumeOverhaul.Stack()` を使う。
- **SampleUVMapping のフック**: `NFX_AntiTiling.hlsl` は HDRP の `__SAMPLEUVMAPPING_HLSL__` を先に定義して自前版に差し替える。`SURFACE_GRADIENT` は LitData.hlsl がこのファイルの後に定義するので、`#ifdef SURFACE_GRADIENT` に頼ったフィールドは置かない。
- **アンチタイリングを既定でオンにしない理由**: 白線が描き込まれた道路テクスチャや、1 枚絵のスキャン素材はずらすと壊れる。素材ごとに `antitiling=1` でオプトイン。
- **Unity の OBJ 読み込み**（materialImportMode None）は usemtl ごとのグループを 1 つのサブメッシュにまとめる → `polyhaven_import.py` はマテリアルごとに OBJ を分け、マニフェストの obj は `|` 区切り。頂点の多いモデルは UInt32 インデックスで読む。
- **Poly Haven の小枝**: glTF では OPAQUE でも、隣に `_alpha_` テクスチャがあれば cutoff 0.5 にする。
- **揺れる葉の BLAS 更新**は共有のスクラッチで直列になるので、全部を毎フレーム更新すると重い（Wind.inl の `kWindBudget`）。
- **バンドルのビルド時間**: 今は 2136 バリアント。`AGENT.md` の「40〜50 分」より長い。共通 hlsl を変えたときは数時間見込む。

---

## 5. 場所のまとめ

| 物 | 場所 |
|---|---|
| リポジトリ | `C:\Users\kppn1223\Documents\carx\mods\NatureFX` |
| Unity のバンドル用プロジェクト | `C:\Users\kppn1223\NatureFX-Unity` |
| Kino Content SDK（テストマップ） | `C:/Users/kppn1223/Desktop/kino_content_sdk`（スクリプトは `Assets/Editor/NatureFX/` にコピーしてからビルド） |
| ゲーム | `C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online`（ログは `kino/output.log`） |
| 外部の依存・素材 | `%USERPROFILE%\NatureFX-Deps`（Poly Haven の素材は `polyhaven\`。素材一覧とライセンスは `docs/testmap-assets.md`、合計約 377 MB） |
| 計画書 | `docs/map-extension-plan.md`（マップ拡張）、`docs/shader-rebuild-plan.md`、`docs/map-authoring.md`（プロファイルの書式） |
