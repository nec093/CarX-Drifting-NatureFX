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
- **11-01 05:05 コミット c056beb**（0093a94 以降の全作業をまとめたチェックポイント。ファイルが入り組んでいて機能ごとに分けられなかった）。
- **雲の影・雲のなめらかさ・段階 1c（05:11、.ksm のみインストール。DLL とバンドルはゲーム起動中のため未。新しい .ksm は古い DLL だと影と環境光が真っ暗になるので、再起動前に必ず DLL とバンドルを入れる）**:
  - `CloudsCommon.hlsli`（雲の密度を Clouds と共有）、`CloudShadow.hlsl`（種類 14: 地面 y=0 から太陽へ 24 ステップ、512²、16 km 四方、カメラにスナップ。前方散乱で真っ黒にはしない）。合成で `sunVisible *= CloudShadowAt(p)`（点を太陽に沿って y=0 へ）。雲の影があるときは `SunController` の雲量による太陽の一律の減光を止める（`CloudController.NativeShadowsActive`）。
  - 時間方向: `Clouds.hlsl` のジッタをフレームごとに変え、ブリッジの `CloudTemporal`（前フレームの VP で方向を再投影、3×3 の範囲でクランプ、今のフレーム 12%）。履歴 2 枚を交互に。
  - 段階 1c: `SkyAmbient.hlsl`（種類 15: 32×16 方向の空（雲を通した光）と地面）→ ブリッジの `SkyAmbientSH`（L2 SH に投影、余弦ローブ/π、HDRP の 7 float4 に詰める）→ `NativeEngine.SkyProbe`。そのフレームの以降のトレースは HDRP のプローブの代わりにこれを読む（`skyProbePtrFrame_`）。合成は `skyDelta = アルベド × (独自 SH − HDRP SH) × 露出 × 空の見える割合` を足す（拡散のみ。HDRP の鏡面の空の反射はそのまま）。
- 上記（雲の影・なめらかさ・1c）は 05:13 に DLL とバンドルもインストール済み。ゲームで未確認。
- **段階 2（霧）と段階 3（太陽の光）（05:18 .ksm・バンドル、DLL も同時刻台にインストール済み。ゲームで未確認）**:
  - 段階 2: `Fog.hlsl`（種類 16: 光の筋の解像度、16 ステップ、高さの指数分布、太陽は 4 ステップごとに影のレイ、空の光は SH から、HG 2 ローブ）。`PrepareFog`（方向と距離。空は 20 km まで）。`RTShadowPass` は Native fog のとき光の筋の代わりに `TraceFog` を同じテクスチャ・同じフィルターで使い、合成は `result × fog.a + fog.rgb × 露出`（空の画素にも）。`MapFog.UpdateNative` がマップの霧／霞／トラックの霧の値を `NativeParams`（消散係数・基準高さ・スケール高さ）にして、HDRP の全体の霧は平均自由行程 100 km で実質オフ（HDRP の体積フォグは局所フォグとタイヤスモーク用に残る）。設定 `NativeFog`（移行 43 でオン、Native sky の下に依存スイッチ）。
  - 段階 3: **方針の修正**。直接光の計算そのもの（BRDF）は HDRP の Lit と同じで、置き換えても見た目は同じなのに半透明物（ガラス・パーティクル）が暗くなる副作用が大きい。そこで「太陽の光の色と強さを独自エンジンの大気から決める」ことを段階 3 とした: `SunController.AtmosphereTransmittance`（Sky.hlsli と同じ定数で、太陽方向の光学的厚さを CPU で 32 ステップ）。Native sky のとき、色温度の代わりに透過率の色、強さ = 大気の外の照度 × 透過率 × 円盤の沈み。影は既に RT、雲の影もある。ランプは HDRP の物理単位のまま。
- **3D の霧・ガラス・パーティクル（05:29 .ksm、05:30 DLL、05:31 バンドル、すべてインストール済み。ゲームで未確認）**: ユーザーの承認で「1 3D の霧 → 2 ガラス → 3 パーティクル」を実装。
  - 3D の霧: `FogVolume.hlsl`（種類 17、160×90×64 のフロクセル、奥行き 0.5〜3000 m の指数分割。セルごとに太陽（影のレイ 1 本、セル内の位置を毎フレーム変える）・空（SH）・ローカルライト（ヘッドライト・街灯、HDRP の減衰とスポット、影なし）の散乱と消散係数）。ブリッジ: `PrepareFogVolume`（列ごとの視線、ビュー深度 1 に正規化）、`FogReproject`（前フレームのカメラ位置と VP で再投影、今のフレーム 10%、3D テクスチャ 2 枚を交互）、`FogIntegrate`（列ごとに積分、各スライスの奥の端の値）。参照は `ShaderLibrary/NFX_FogCommon.hlsl`（`NFX_Fog(uv, ビュー深度, 距離, 方向)`: 3 km までは格子、その先 20 km までは影なしの解析的な霞）。グローバル `_NFX_FogVolume` `_NFX_FogVolumeParams`（x 霧、y 独自の空の光）`_NFX_FogHeight` `_NFX_FogSun(Dir)` `_NFX_SkyLight`（SkyProbe、[7] に空の平均輝度を追加 → バッファ 8 要素）。Native fog オンのとき 2D の `Fog.hlsl` と光の筋は使わない（コードは残してある）。
  - ガラス: `Glass.hlsl` の出力 w に「フレネル + 2×距離(m)」を詰める（`RTGlassPass` のテクスチャを 32 bit に）。`GlassComposite` で、映り込みの分（フレネル）にだけガラスまでの霧を掛ける（透けて見える分は奥の面で霧済み）。
  - パーティクル: `NFX_Particle.shader` の Frag を包んで霧を掛ける（発光は暗くするだけ）。空の光 `_NFX_PSky` を独自の空の SH（真上の放射照度 = π×SH(上)）に置き換え、霧があるときは距離による減衰をやめる。
  - 未対応: ゲーム自身の半透明シェーダー（CarX のガラス以外の半透明物・パーティクル）。MapParticles のように NatureFX のシェーダーへ差し替える必要がある。
- **不具合（ユーザー）: 雲のなめらかさを入れてから雲がとても小さくなった** → 推定: `CloudTemporal` の再投影（HDRP の `UNITY_MATRIX_PREV_VP` と uv の上下の決まり）がずれていて、雲のある所に前フレームの空が混ざり、3×3 の min/max クランプで雲が芯だけに削られていた。修正: 前フレームのカメラの回転（`worldToCameraMatrix` の行）と画角を C# から渡して手で投影、クランプは分散クリッピング（平均 ± 1.5σ）。05:29〜05:31 にインストール、ゲームで未確認。
- **不具合（ユーザー）: 夜なのに空が白い（Native fog をオフにすると直る）** → 推定: `FogVolume.hlsl` のランプの距離の下限が 0.1 m で、ランプ（ヘッドライト・街灯）を含むセルが何百倍にも明るくなり、格子の補間と夜の自動露出で画面に広がった。修正: 距離² の下限をランプの大きさ（`LightSpot.y`）とセルの大きさ（奥行き × 0.13）の大きい方に。05:34 に NativeShaders の .cso を配置（Developer > Reload shaders で反映、DLL への埋め込みはゲームを閉じてから `build_native.ps1`）。ゲームで未確認。
- 夜空の白さはユーザーの画像で解消を確認（空は黒く、街灯の光の帯が霧に出る）。
- **不具合（ユーザー）: 光源が四角い（遠くの街灯のにじみ）、夜なのに車などが白い** → (1) 霧の格子のセル中心だけでランプのにじみを拾っていた: `PrepareFogVolume` が列の中心と隣の列・行への差分（3 float4）を書き、`FogVolume.hlsl` はセルの中の位置を縦横にも毎フレームずらす（履歴で平均）。(2) 独自エンジンの空は太陽の光だけ（月・星なし）で、夜に空の光が 0 → HDRP の月明かりの空の光を引いて暗くなり、自動露出が上がりきってランプで白く飛ぶ: `RTShadowPass.skyDay_`（太陽 y −0.1〜0 で 0〜1）で、夜は独自の空の光を使わず HDRP の空の光に戻す（合成の skyDelta に掛ける、プローブの差し替え・パーティクルの空の光も昼だけ）。05:4x に .ksm・NativeShaders、バンドルはビルド中。ゲームで未確認。
- **大気を RT から独立（ユーザーの要望「RT 関係なしに独自エンジンへ」）、月・星も独自エンジンへ（.ksm・DLL・バンドルすべてインストール済み、ゲームで未確認）**:
  - `Rendering/AtmospherePass.cs`: 2 つのインスタンス。トレース（AfterOpaqueAndSky ボリュームの先頭: BeginFrame → TraceSky（空・雲・雲の履歴・雲の影・空の光）→ 3D の霧）と合成（同じボリュームの最後: `NFX_Atmosphere.shader` で空の画素（空・太陽/月の円盤・星・雲）と全画素の霧）。RT の影のパスは `AtmospherePass.Shared`（雲の影・空の光の割合）を読むだけ。RT の合成シェーダーからは霧の適用を削除（空の描画は `_NFX_SkyParams.x = 0` で無効）。`RTShadowPass` に残った空・霧の古いフィールドは未使用（後で削除してよい）。
  - `NativeEngine.Enabled` は Native sky でもオン（RT の設定がすべてオフでもシーンを作る: 霧の太陽の影のレイに必要）。`UpdateRayTracing` も大気だけで動く。
  - 月: `SunController.UpdateSkyLight` が独自エンジンの光を決める（太陽、太陽が −2° より下で月が出ていれば月）。`SkyLightAbove`（大気の外の照度: 月は地上の照度 ÷ 大気の透過率）。`RayTracingEffects` の RT の太陽もこの光にした → 夜は月の影がレイトレースになる、空は月に照らされた大気になる、雲・霧も月の光。月なしの夜は HDRP の空の光に戻す。
  - 星: `MapNight.StarTexture`（物理単位の星のキューブマップ）と回転を合成で直接描く（視線方向の大気の透過率と雲で暗く）。HDRP の夜空を重ねるのはやめた。
- **ロードマップのステップ A（不透明物の照明を独自エンジンへ）着手（.ksm・バンドル・DLL すべてインストール済み、ゲームで未確認）**:
  - A1: 設定 `NativeLighting`（移行 44 でオン、F8 の Sky カード）。`Rendering/EngineLighting.cs` の `Shadows/Ambient/Reflections/LocalLights/LampShadows/GlassReflections(s)` が「RT のトグル || NativeLighting」を返し、`RayTracingEffects`・`VolumeOverhaul`・`NatureFXMod` はこれを読む。GI はバウンス・マルチバウンスもオン。
  - A2: `NFX_SunExact.shader` に 2 つ目のパス `LampsExact`（HDRP の Lit の BSDF で `_LightDatas` の全パンクチュアルライト、シャドウマップなし）。`RTShadowPass.DrawExactSun` が `lampsExact_` に描く。合成は「エンジンの遮られた照度 ÷ 同じ式の遮られていない照度（`LampsLuminance`、`_NFX_Lamp*` 配列）」の割合を、HDRP のランプの正確な光（鏡面も）に掛けて引く。NatureFX にしかない光（負の割合）は従来どおり拡散で足す。
  - 次: A3（空の鏡面反射を独自の空で: HDRP の反射プローブ／空の反射の代わり）、A4（HDRP の照明そのものを置き換える G バッファからの完全な再計算）を計画書に。
- 11-01 ユーザー確認: RT をすべてオフでも影・GI・反射・空・霧が動く ✓、夜の白さ ✓、遠くの光の四角さ「大分マシ」。**雲が小さいまま・星が見えない** → 真因の推定: 雲のなめらかさではなく、`NFX_Atmosphere` が空の画素にも 20 km の霞（霧の尾）を掛けていて、遠く（地平線寄り）の雲と星が隠れていた（独自の空はもともと大気の霞を含むので二重）。修正: 空の画素は格子（3 km）の霧だけ（`NFX_FogAt`）。バンドルは reload/naturefx-20261101-060236.bundle（Reload shaders で反映可）。ゲームで未確認。
- 月明かり: 「大部分が HDRP にフォールバックしていそう、地面が明るい、一部暗い所が正常に見える」→ 説明: 暗い所は月のレイトレースの影、明るい所は月の直接光（SunController の月は約 2 lux）を自動露出が昼並みに持ち上げたもの。提案中: 月を物理的な明るさ（満月 0.25 lux 前後）にする／夜は目が順応しきらないよう露出の上限を設ける。
- **A3（独自エンジンの空を HDRP の空として渡す）（.ksm・バンドル・DLL すべてインストール済み、ゲームで未確認）**: ユーザーの指摘「夜の反射に朝っぽい空が映る」= HDRP の空（月で照らされた PBS）が反射プローブと空の映り込みに使われていた。`SkyRadiance.hlsli`（SkyAmbient から切り出した方向ごとの空の輝度: スカイビュー＋雲＋地面）、`SkyCube.hlsl`（種類 18: 64²×6 面、Unity の面の順）、ブリッジの `SkyCubeUnpack`（RWTexture2DArray でキューブの RT へ）。`NativeEngine.TraceSky` の最後に（空の値があるうちに）作る。`AtmospherePass.SkyCube`（R16G16B16A16 のキューブ RT）。`VolumeOverhaul.UpdateSky(..., engineSky)` が HDRISky（Multiplier 1、Realtime 0.2 秒）にする。HDRP の環境光も独自の空になるので、合成の skyDelta（1c の差し替え）は使わない（`Shared.SkyLight = 0`）。太陽・月の円盤はキューブに入れない（HDRP のライトの鏡面と重なるため）。星もまだ入れていない。
- 11-01 ユーザー: A3 は「結構いい感じ」。対応（06:2x、.ksm のみ、ゲーム起動中。DLL・バンドルの変更なし）: (1) 夜が暗すぎて Darkest scene を 1.0 EV にしないと見えない → 移行 45 で初期値のまま（8 EV）の人だけ 1.0 に。(2) 星が見えない → 設定 `StarBrightness`（初期 4 倍、0〜20、物理的な明るさに対する倍率: 目の夜間視力にカメラの露出は届かない）と F8 の「Stars:」状態。(3) 天候の変化が遅い → 設定 `WeatherChangeTime`（初期 30 秒、5〜300）: `WeatherController` のプリセット間の遷移と、`CloudController` の雲量の追従（以前は 90 秒 + 180 秒）を同じ時間に。(4) 昼の鏡の反射の空が真っ黒 → `MapProbes` がキューブがまだ黒いうちに撮ったままだった: 空のキューブが変わったら 2 秒後に撮り直し、以後 20 秒ごとに撮り直す。
- **不要な設定の削除（11-01）**: どこからも読まれていなかった `RainStreaks`・`LensDrops`・`HeatHaze`（スライダーだけ残っていた）、`CarGlossyPaint`（no longer used）、`RTNativeSun`（移行 22 だけが読んでいた → `RTBackend = 0`）を削除。Native をオフにしたときの代替として残しているもの: `RTShafts`/`ShaftDensity`（光の筋）、`SunTemperature`、`RealisticShadows`/`SunSize` など。
- ロードマップ B（影と反射を常に独自エンジン）は A1 でほぼ完了（Native lighting で HDRP の太陽・ランプのシャドウマップ、SSR、SSGI/SSAO、接触影がオフ、反射プローブは A3 で独自の空）。
- **ロードマップ C（局所フォグとタイヤスモークを 3D の霧へ）（.ksm・DLL インストール済み、ゲームで未確認）**: `AtmospherePass.GatherBoxes` が有効な `LocalVolumetricFog`（マップの霧の箱・タイヤスモーク）を近い順に 64 個まで集め、4 float4（中心＋消散係数、軸/半サイズ＋アルベド）にして霧の Surface バッファの列データの後ろへ（`cmd.SetBufferData`）。`FogVolume.hlsl` の `BoxesSigma` が箱の中で外側 1/5 で濃くなる密度に雲の Worley ノイズ（数 m）を掛けて足す（Screen に空のデータ、ReflParams.x 箱の数・y 時間）。Native fog のとき HDRP の体積フォグとカメラの Volumetrics の上書きは使わない（`NatureFXMod`）。HDRP の箱のマスクテクスチャ・フェード設定は使っていない（独自のノイズ）。
- **反射の空がまだ黒い（昼）** → 推定: Unity のキューブの RenderTexture にコンピュートシェーダー（RWTexture2DArray）で書けていなかった。修正: `NativeEngine.TraceSkyCube` は 6 スライスの Tex2DArray（`skyFaces_`）に書いて、`cmd.CopyTexture` で面ごとにキューブへ。キューブの RT は enableRandomWrite なし。ゲームで未確認。
- **設定の整理（ユーザーの要望）**: サイドバーを GENERAL / SKY & LIGHT（新設 Sky & fog・Lighting、Shadows、Ambient light & GI、Reflections、Exposure & tone）/ WORLD（Sun & time、Weather、Nature、Road & cars、Map）/ ADVANCED（Shader rebuild、Settings、Developer）に。Weather タブの Sky カードを Sky & fog タブ（Sky・Clouds and stars・Fog の 3 カード）へ、Native lighting を Lighting タブへ、天候の変化時間を Weather カードへ。Sun タブの HDRP 専用の項目（色温度・ヘッドライトの光の筋・太陽の光の筋・HDRP の柔らかい影）は Native が担当中はグレー（「HDRP only」カード）。RT の各タブには「Native lighting がオンなら動いている」の注記と、詳細をグレーにしない（`EngineLighting`）。
- **反射の空がまだ黒い（コピー方式の後も）**。手がかり: Weather カードの night が「no stars (the track's sky is not physically based)」→ HDRP の空は HDRI に切り替わっている（`Sky type` の表示はトラックの元の種類を出していただけ → 修正: `VolumeOverhaul.SkyState` は HDRP が実際に描いている種類と HDRI のテクスチャ名を出す）。キューブの中身を確かめるため、`AtmospherePass.ReadCube` が 2 秒ごとに真上（面 2）と地平線（面 4）の texel を AsyncGPUReadback して F8 の Sky カード「Cube for HDRP:」に出す。黒（0）ならキューブの生成側、値があるなら HDRP の HDRI の読み方か反射プローブ側が原因。
- **タイヤスモークに違和感（出方と拡散）** → 箱を「外側 1/5 で薄くなる箱＋6 m の Worley」にしていたのが原因と推定。修正: 箱は 5 float4 に（5 つ目: フェードの割合・ノイズの大きさ・丸い（HDRP の Exponential）かどうか）。Exponential の箱は楕円体（半径方向に smoothstep）、フェードは HDRP の positive/negativeFade の平均、ノイズは size / textureTiling の大きさで上へゆっくり流れる。箱があるときは霧の履歴の今のフレームの割合を 0.1 → 0.3（動くスモークが尾を引かない）。06:4x に .ksm・DLL インストール。ゲームで未確認。
- 読み戻しの結果（夜）: HDRP は `HDRI NFX_SkyCube` を描いている、キューブは「0」（夜は暗すぎて 2 桁表示で 0 → 指数表記 G3 に変更）。夜なのに鏡に昼の青空 → **NatureFX の反射プローブ（MapProbes）が古い空のまま**（撮り直しが効いていない）。対応: Native sky の間は MapProbes を使わない（HDRP の空の映り込みは独自の空、近くは RT の反射）。06:5x .ksm。ゲームで未確認。なお月の「大気の外 0.001 lx」はトラックの太陽の強さ（物理単位でない）× 0.00002 のため。
- タイヤスモーク: 丸い塊に直した後も「現実っぽくない、消え方も」。霧の格子（160×90×64）でスモークを表すのは解像度と動きの面で無理がある → 提案中: 専用のスモーク（柔らかいパーティクル、独自エンジンの光と霧で照らす、カールノイズの流れ、年齢で広がり薄くなる）。
- **夜の地面の大きな暗い領域（走ると場所が変わる）**: 雲の影・月の影ではない（ユーザーが両方 0 で確認）。原因: ドリフトで出たタイヤスモークの箱が 3D の霧で濃い吸収体になり、夜は照らす光がほとんどないので真っ黒な煙になっていた。
- **タイヤスモークを専用のパーティクルで作り直し（ユーザーの決定: HDRP には戻さない）（06:51 .ksm、06:53 バンドル、インストール済み、ゲームで未確認）**: `TireSmoke.CreateParticles(bundle)`（`NatureFXMod` が Rain の後に作る、Reload shaders で作り直し）で Unity の ParticleSystem（ワールド空間、4000 個、大きさは最初に速く広がる曲線、アルファは素早く出てゆっくり消える、空気抵抗、カールノイズ、回転、上向き 0.35 と風の力）。発生は従来の `Spawn`（タイヤの滑り・濡れた路面の水しぶき）から 1 回に 2〜3 個 `Emit`。`NFX_Particle.shader` の mode 5（`ParticleAtlas.ModeSmoke`）: 丸い減衰 × `_NFX_NoiseTex` の 2 オクターブの流れるノイズ、深度で地面や車に柔らかく溶ける、球の法線で太陽／月の側が明るく、逆光と多重散乱、空の光（独自の空の SH）、ランプ。3D の霧は Frag の包みで掛かる。霧の箱からは `NatureFX.Puff` を除外。
- 夜の暗い地面は直った（ユーザー）。スモークは「分解能が足りない」→ 1 回の発生を煙 8 個・しぶき 4 個に、大きさ 0.55 倍、散らばり 0.4 m・初速のばらつき 0.9、最大 12000 個、発生間隔を半分、シェーダーのノイズを 3 オクターブで細かく。
- 反射の空はまだ黒（MapProbes を止めた後も）。RT の反射は空に抜けたレイで HDRP の反射をそのまま残す（Reflection.hlsl: 外れは result 0 → 合成で HDRP の色）→ 鏡の空は HDRP の空の映り込み（キューブから HDRP が作る）。**昼の「Cube for HDRP」の値待ち**: 0 ならキューブ生成側（TraceSkyCube / SkyCubeUnpack / CopyTexture）、値があれば HDRP が RenderTexture のキューブを HDRI として使えていない（その場合は Cubemap アセットへ CopyTexture するか、HDRI をやめて HDRP の空をカスタムの Sky にする）。
- **タイヤの熱による物理ベースの煙（ユーザーの要望）（07:0x .ksm、ゲームで未確認）**: `TrackMap.WheelContact` に `SlipSpeed`（横滑りと、ホイールの回転（`up` の軸まわりの角度変化）× 半径と地面の速さの差の合成、m/s。回転が一度も見えないホイールは縦の滑りなし）と `Load`（質量 × g ÷ 輪の数）。`TireSmoke.TyreSmokeRate`: トレッド表面温度（熱容量 150 J/K）を摩擦の仕事率（μ 1.0（濡れ 0.6）× 荷重 × 滑り速度 × 0.6）で加熱し、空気（20 + 2×速度）・路面と芯（60）・水（600×濡れ）W/K で冷却。煙は 160°C から 300°C で最大、(割合)² × 滑り速度/8。外気温は雪で −2°C、雨で −5°C。F8 の Road & cars に「hottest tread」の表示。
- 昼の読み戻し: キューブは up (0.177, 0.382, 0.899) nits、地平線 (1.05, 0.968, 0.874)（太陽は大気の外 33.1 lx = トラックの太陽の値）→ **キューブは正常**。HDRP も HDRI NFX_SkyCube を描いている。なのに鏡の空は黒 → HDRP がキューブから空の光・映り込みを作り直していないか、別の（暗い）反射プローブが効いている。切り分け: `AtmospherePass.ReadProbes` が HDRP の環境光プローブ（`HdrpBindings.AmbientProbe`）と独自の SkyProbe の真上の値を F8 の Sky カード「Sky light:」に出す（07:1x .ksm）。
- **未解決 3: ときどきゲームが落ちる**。`Player.log`（LocalLow/CarX Technologies/Drift Racing Online、前回は `Player-prev.log`）では 2 回とも `CreateCommittedResource ... failed (887a0005)`、`Device Remove Reason 0x887a0006`（DXGI_ERROR_DEVICE_HUNG = GPU のタイムアウト）。VRAM は 7.0/11.2 GB で不足ではない。マップを読み込んで十数分走った後で、直前のログはプローブのエラーで埋まっていて手がかりなし。RT のパスは `IsScreenCamera` でプローブのカメラを飛ばしている。プローブ修正後も落ちるなら、独自エンジン（NRD・GI キャッシュ・揺れる葉）を一つずつ切って切り分ける。

## 0.8 コードレビュー第 2 回（2026-11-02）の結果

`docs/review-2026-11-02.md` に一覧（クラッシュ 13 件、描画 20 件、性能 15 件、修正済み／未修正）。要点:

- **未解決 3（ときどき落ちる）の原因が分かった**: 落ちる直前は毎回「DLSS モード切替 ＋ 解像度 1916x997 → 3840x2054」で、(X1) `ScreenSize` が表示解像度のまま、画面色バッファは描画解像度で確保されるため、反射・ガラスのルート SRV を 9 倍先まで範囲外読み → GPU ページフォルト（DLAA では一致するので出なかった）。(X2) 4K では Mod の画面サイズ依存の確保だけで約 4 GB になり予算（10.85 GB）に届く。(X3) `WaitRenderIdle` が 5 秒で諦めた後に DLSS フィーチャを解放していた。3 つとも修正済み。
- ほかに必ず起きる例外（`TireSmoke` の `shadowCursor_` が約 5 時間で int 溢れ → `Update` 全停止）、描画スレッドとメインスレッドの競合（記録〜サブミットの窓で BLAS 解放、状態文字列の同期なし読み）、描画イベントのリング 64 → 256、DLSS の `MVLowRes` フラグ欠落、3D 霧の再投影でカメラ移動を二重加算、DLSS 入力の jittered 行列、トラックの霧が一度も読まれていなかった件、Native sky オフ時の共有状態の残り、履歴テクスチャ再確保時のリセット漏れ、月の影の二重掛け、低木がシーン切替で破棄済みメッシュを描く件、スライダードラッグ中の低木の毎フレーム再生成、各種リークを修正。
- **すべて未インストール・ゲーム未確認**。.ksm（Release）、DLL（`build_native.ps1`、ゲームを閉じて）、バンドル（`NFX_NativeBridge.compute`・`NFX_SunExact.shader` は RT の合成シェーダーなので数分）の 3 つを入れ替える。C# は Debug ビルド、C++ は `cl /Zs`、native の `Clouds.hlsl`/`Glass.hlsl` は dxc が通っている。
- 未修正で判断がいるもの: GI の夜のクランプ基準（R15）、ライトの単位（R16、HDRP 16 の `SetIntensity` の確認が先）、アップスケール中に PostPass が走らないフレーム（R17）、タイヤスモークの CPU/GPU コスト（P7）、3D 霧のライト総当たり（P8）、ジオメトリごとのコミット済みリソース（P9）、各パスの画面コピーの共有（P5）、トレースバッファのストライド（P6）。

## 0.9 レビュー後の 7 項目の設計メモ（2026-11-02）

`docs/plan-review-followups.md`: 夜の GI のクランプ基準、ライトの単位、タイヤスモークのコスト、3D 霧のライト絞り込み（タイル × 奥行き帯のビットマスク）、ジオメトリのチャンク・サブアロケーション、各パスの画面コピーの共有、トレースバッファのストライド削減（4K で −726 MB）。それぞれ現状のコード位置・具体案・リスク・ゲームでの確認方法つき。**実装は別セッションで**（着手順の目安は冒頭）。ライトの単位は HDRP 16 のソースで確認済み: レガシー `Light.intensity` は常に内部単位（点・スポットは candela）で描画もそれを読むので不具合ではなく、戻し方の整合だけ。

## 0.10 7 項目の実装（2026-11-02、クラウドのセッション。ビルドもゲーム確認もしていない）

`docs/plan-review-followups.md` の末尾「実装状況」に詳細。実装したのは **6・1・2・4・7B・7D・7E**。未着手は 7A・7C・7F・7G、3（見た目の相談が要る）、5（ネイティブの大きな変更）。
- この環境に dotnet / dxc / Unity が無く、**C#・ネイティブのシェーダー・バンドルのどれもコンパイルしていない**。まず 3 つともビルドして、エラーが出たら直す。
- レイアウトが変わったもの（7B Surface の結果、7D NRD ガイド、7E 画面バッファ）は **.ksm・DLL・バンドルの 3 つがそろって初めて整合する**。どれか 1 つだけ入れると表面・NRD・反射が壊れる。
- 4 の霧のライトリストは Developer の「3D fog lamps」でオン/オフ比較できる（F8 のタイミングに「fog volume」を追加）。
- **ユーザー確認（11-02）: 3 つをビルドして入れ替え、正常に表示された**（ガレージ）。途中、バンドルだけ古いまま起動して画面全体にざらつきと紫の点が出た → 7B/D/E のレイアウトの食い違い。新しいバンドルで解消。
- 1 の基準は、範囲 3 m 未満のライトを除き、2 m より近くでは見積もらず、上限 2000 lux に変えた（Developer「GI lamp reference」で比較、F8 の lamps 行に値）。夜のヘッドライトの跳ね返り、霧のランプの時間差、メモリの減り方は未確認。
- **ユーザー確認（11-02）: 1 は効いている**。夜（GI、半解像度）、F8 の `GI lamp reference 21.6 lx`。Developer のスイッチのオン/オフで、ヘッドライトが直接当たらない面（壁・車体）の明るさが変わった。直接照らされた地面の前では跳ね返りが埋もれて見えない。
- F8 に「GPU stage timings are not reported on this GPU / API」と出る環境で、**F8 の「fog volume」の ms は出ない**。4 の比較は fps / フレーム時間で（霧のあるマップができてから）。
- メモリ: F8 では GPU memory 8136 / 10379 MB（比較前の値なし）。`trace buffers N MB` は `kino/output.log` の `[NatureFX] Camera WxH: ... trace buffers` 行にだけ出る。1916x997・NRD オンなら 7B/D/E 前の見積もり 458 MB → 約 375 MB。

- **7F・7A・7C も実装（11-02、未ビルド・未確認）**。7A は C++ も変わる（PackSun → CopyRaw、`PackSun.hlsl` 削除）ので DLL の通常ビルドと再起動が要る。ここでも .ksm・DLL・バンドルの 3 つをそろえる。確認: 反射（塗装・水たまり・クローム、NRD オン/オフ）、太陽の影と半影の幅（DXR 1.2 オン/オフ、NRD の SIGMA）、ガラスの映り込み・屈折。ログの `by kind` で sun・reflection・glass が減ったか。

- **7G・3（CPU 側）・5（BLAS プールと記述子の回収）も実装（11-02、未ビルド・未確認）**。7G はレイアウト変更（.ksm・DLL・バンドルをそろえる）。5 は C++ のみ。確認: 環境光・GI・ランプの影・NRD（REBLUR）が前と同じ、タイヤスモークの見た目が同じで fps が上がる、F8 の Scene に `BLAS pool X of Y MB in N chunks`、BLAS の MB と GPU memory が減る、マップを何度切り替えても `DESCRIPTOR HEAP FULL` が出ない・落ちない。

- **ユーザー確認（11-02）: 7F・7A・7C・7G・タイヤスモークの並列化・BLAS プールと記述子の回収・煙の最大サイズ 0.8 はすべて問題なし**（煙は既定の 0.8 のままでよい）。残り: 5 のデータプール（移行順 2）・AddGeometryData・ClearScene の接続・Wind / DXR 1.2 側、3 の粒子数・半解像度・ランプ数（見た目が変わるので相談）、Glass.hlsl の `own` の隠れ（docs/plan-review-followups.md「気づいた既存の不具合候補」）。

- **5 の続き（11-02、未ビルド・未確認、DLL のみ）**: 頂点・インデックス・法線・UV のプール（`g_dataPool`）と BLAS の圧縮。確認: F8 の Scene の `BLAS ... MB` が約半分、`data pool X of Y MB`、GPU memory の減り、葉のアルファテスト・揺れる葉・DXR 1.2 の影が同じ、マップ往復で落ちない。失敗するとシーン構築のエラー番号（-9 / -10 / -12）が出る。

- **5 の続き 2（11-02、未ビルド・未確認、.ksm と DLL）**: AddGeometryData のメッシュ（車体など CPU で読めるメッシュ）もデータプールへ。新しいマップの読み込み（Single）ではエンジンのシーンを丸ごと作り直す（ClearScene）。確認: マップ切替の直後に F8 の geometries の数が前のマップの分だけ増えない、マップを何往復しても GPU memory と BLAS / data pool が増え続けない、マップ読み込みの時間（共有メッシュの作り直しで数百 ms 増える見込み）、昼夜切替でひっかからない、車体の反射・影が前と同じ。

- **デバッグページ（11-02、未ビルド・未確認、.ksm と DLL）**: F8 > Developer の「Debug page」をオンにすると `http://127.0.0.1:8765/`（設定 `DebugPagePort`）でブラウザ用のページ。VRAM の内訳（`NFX_Native_MemoryStats` の独自エンジン 12 種、トレースバッファ、`NFX_` / `NatureFX` 名の描画ターゲット・テクスチャ、それ以外の描画ターゲット）、GPU memory の推移グラフ、GPU 時間、状態行、`kino/output.log` の NatureFX 行。`Tools/DebugPage.cs`（TcpListener、ループバックのみ、スナップショットはページが開いている間だけメインスレッドで 1 秒ごと）、`Tools/DebugPageHtml.cs`。DLSS（NGX）の確保は数えられない。
- **計測オーバーレイを廃止してデバッグページへ移した（11-02）**: `RayTracingEffects.ReadStats()`（旧 ReadOverlay、`DebugStats`）の性能・シーン・RT 機能・デノイザー・パスに加え、F8 の各カードの「Now:」の状態行（エンジン、空・霧・雲、影・反射・ポスト・DLSS、天気・車・マップ）をセクションとして出す。F8 の状態行はそのまま残した。`Settings.RTOverlay` は設定ファイル互換のため残すが使わない。ページは等幅・表中心・色は警告だけ（ログの絞り込み、一時停止あり）。

- **デバッグページでの実測（11-02、1916x997、DXR 1.1）**: GPU 6422 / 11228 MB、NatureFX 計 3286 MB。独自エンジン 2366 MB のうち geometry data pool 1408 MB（使用 1383）、BLAS 162.6 MB（圧縮後）、揺れる葉 302.5 MB、NRD 288.3 MB。トレースバッファ 417.6 MB、NatureFX の描画ターゲット 501.9 MB、ゲーム・HDRP の描画ターゲット 377.6 MB。
- **データプールが大きすぎた原因**: C# がサブメッシュごとに頂点数 `mesh.vertexCount - sm.baseVertex` で登録し、ネイティブはサブメッシュごとにメッシュ全体の位置・法線・UV をコピーしていた（サブメッシュの数だけ重複）。→ `g_sharedData`（元のアドレス・間隔・形式・頂点数が同じなら 1 つを共有）と `g_spanRefs`（区間の参照カウント、ReleaseGeometry で減らし 0 で共有表から外す、退役時に 0 なら Free）。未ビルド・未確認。

- **頂点データの共有の結果（ユーザー実測）**: geometry data pool 1383 → 205 MB、GPU 6422 → 5248 MB（同じマップ・1916x997）。
- **VRAM 削減の続き（11-02、未ビルド・未確認、.ksm と DLL）**: NRD のとき自前フィルターのデノイズ・履歴を確保しない（RTShadowPass `Accumulation.EnsureFilter/DropFilter`、RTReflectionPass `EnsureFilterTargets`。見込み −90 MB）、3D 霧の Surface を必要量だけ（−14 MB）、ランプの影・ランプマスクは自分の Surface を持たない（−15 MB）、揺れる葉の揺らした頂点を `g_windPool`、BLAS を `g_blasPool` に置き圧縮（更新はそのまま）。見送り: 星のキューブマップ（点の大きさが変わる）、DBufferNone 系（HDRP が Load で読むので 1x1 にできない）、TrackMap（タイヤ痕の細かさと相談）。

- **ゲーム側の VRAM（11-02 実測、test_map、1916x997）**: ゲームのテクスチャ 1880 MB（うち CarX の `starmap` キューブマップ 4096、無圧縮 RGB8 で 288 MB 以上: 独自の空の間は誰も描かない）、メッシュ 214 MB（`NFX_RT_*` は 0.5 MB: 重複なし）、HDRP の描画ターゲット 430 MB（クッキーのアトラス 4096 で 85 MB、カスケード影 3072 で 36 MB、物理ベースの空のテーブル 48 MB）。HDRP の SSR/SSAO/SSGI/体積雲/ブルームは Volume でオフ（無駄なし）。mip ストリーミングはオン、予算 4198 MB。
- **対応（未ビルド・未確認、.ksm のみ）**: 独自の空の間、トラックの Volume の物理ベースの空の space emission と HDRI のテクスチャを `Resources.UnloadAsset`（`VolumeOverhaul.UnloadTrackSkyTextures`、独自の空をやめれば Unity がディスクから読み直す）。設定 `TextureStreamingBudget`（F8 > Developer > Video memory、0 はゲームの値、`TextureBudget.cs`）。デバッグページに mip ストリーミングの量・Unity の統計・降ろした空のテクスチャ・影を持つ平行光源。

- **ユーザー実測（11-02）**: トラックの空のテクスチャを降ろして game textures 1760 → 1488 MB、GPU 5150 → 4802 MB（starmap と HDRI 3 枚）。mip ストリーミングの対象は 66〜79 MB だけ（1.42 GB はストリーミングされない）ので予算のスライダーはほぼ効かない。残る HDRP の固定確保: クッキーのアトラス 85 MB（4096、mip 付き）、カスケード影 36 MB（影を持つ平行光源なしでも残る）。

- **デバッグページの負荷（11-02、未ビルド・未確認、.ksm と DLL）**: `RenderThreadGpu` の Begin / Submit にタイムスタンプ（クエリヒープ 2×64、readback を常時 Map、スロットが一巡したとき読んで名前ごとに合計、`NFX_Native_GpuTimings` で取り出して 0 に）。名前は `g_crumbTag`（trace の種類名、NRD は 4 種に分けた）。C# は `Tools/GpuStats.cs`（nvml.dll: 使用率・メモリ帯域・クロック・電力・温度・クロック制限の理由）、`FrameTimingManager`（ゲームのビルドで無効なら出ない）、プロセスの CPU 使用率。ページに「load」「gpu-time」と負荷のグラフ。シーン読み込み後の各ステップの VRAM（events）も追加済み。

- **events で分かったこと（11-02 実測）**: シーン読み込み後の「VRAM が倍」は MaterialInventory.Write（TextureSampler.Export がテクスチャを読み取り可能なコピーにして PNG に書く）の一時的な山（+330〜490 MB、次のフレームで戻る）。置き換えでは増えない。→ 設定 `WriteMaterialInventory`（F8 > Developer > Material inventory、既定オフ）のときだけ書く。
- **GPU 負荷（1916x997、60 fps 垂直同期、GPU 使用率 65 %）**: 独自エンジン 4.89 ms（フレームの 29 %）。上位: NRD REBLUR 反射 1.07、表面 0.94、sky view 0.64（そのフレームの最初のトレースなので毎フレームの TLAS 構築と揺れる葉の BLAS 更新 64 本を含む。空の LUT 自体はごく小さいはず）、SIGMA 0.51、反射 0.34、REBLUR GI 0.33、ランプ 0.30。

- **負荷の改善（11-02、未ビルド・未確認、DLL のみ）**: 表面のトレース（`Surface.hlsl`）を深度の前後の窓（距離の ±2 %、最低 10 cm）だけの短いレイに（カメラからのレイが BVH をほぼ全部たどっていた。窓の外の当たりは元々捨てていたので結果は同じ。窓の手前の葉などで灰色になっていた画素は奥の面が取れるようになる）。GPU 時間の計測にリスト内の区切り（`RenderThreadGpu::Mark`、1 リスト最大 7 区間）を足し、毎フレームの「frame: foliage sway (BLAS updates)」「frame: TLAS build」を別に出す。揺れる葉の BLAS 更新はスクラッチをリング（最大 32 MB）で分けて並列に、バリアはリングが一巡したときと最後だけ（以前は 1 本ごと）。

- **ユーザー実測（11-02）**: 独自エンジン 4.89 → 3.59 ms、表面 0.94 → 0.24 ms、TLAS 0.19 ms、揺れる葉の更新 0.019 ms、sky view は 0.017 ms（前の 0.64 は TLAS と揺れる葉）。BLAS pool の確保がマップを読むたびに 64 MB ずつ増えていた（使用 172 MB のまま 320 → 512 MB）→ 圧縮前の BLAS は `g_blasBuildPool` に作り、圧縮（大きさが分からなければ複製）して `g_blasPool` へ。`PoolSpan.pool` で元のプールへ返す（Free は別のプールの区間を転送）。

- **ユーザー実測（11-02、最新）**: マテリアル一覧の山なし（material inventory の差 0）。BLAS pool 192 MB / 3 チャンク（+0 building）でマップ往復でも増えない。GPU memory 4686 MB（最初の計測 6422 MB から −1.7 GB）、NatureFX 1657 MB（3286 から）。シーン構築 78.7 ms。独自エンジンの GPU 3.60 ms（フレームの 21 %）。残りの上位は NRD（反射 0.99、SIGMA 0.50、REBLUR GI 0.32、ランプ 0.27）。ときどき 33 ms のフレーム（原因未調査）。

- **CPU の計測と NRD のパフォーマンスモード（11-02、未ビルド・未確認、.ksm と DLL）**: `Tools/CpuTimings.cs`（デバッグページが動いている間だけ。NatureFXMod.Update を 13 区間に、カスタムパスの Execute と HDRP の描画全体（beginContextRendering〜end）をスコープで計測。25 ms か平均の 1.6 倍を超えたフレームを、そのフレームの上位 4 区間と GC 回数つきで記録）。ページに「cpu」と「slow frames」。NRD: `NrdEventData.pad` を `flags` に（bit 0 = REBLUR を軽く: この版の NRD には performance mode が無いので prepass・安定化・anti-firefly を止める `NrdFast`）、F8 > Developer > NRD parts に反射と環境光・GI・ランプのスイッチ（保存しない）。

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
- **反射に映る空（11-01 07:12 .ksm インストール、ゲームで未確認）**: 夜の読み戻しで HDRP の空の光 (6.7e-6, 1.29e-5, 2.64e-5) と独自 (6.46e-6, 1.27e-5, 2.63e-5) が一致 → HDRP の空は正しい。夜の鏡に昼の空が映る・再起動ごとに変わる原因はトラック自身の ReflectionProbe（ロード時に一度だけ、その時の HDRP の空で撮られる）。`Rendering/SceneProbes.cs`: Native sky の間はトラックの ReflectionProbe（`NatureFX.` で始まらないもの）を 2 秒ごとに探して無効にし、オフで戻す。F8 Sky カードに「Track reflection probes:」行。バンドル（reload/ の 07:00 のスモーク細分化）はゲーム終了後に本体へ。
- **タイヤスモークが出ない（ユーザー、11-01）**: (1) 入っているバンドル（06:53）は煙のシェーダー（NFX_Particle の mode 5、06:58 更新）より古い。最新は reload/ の 07:00（F8 > Developer > Reload shaders か、ゲーム終了後に本体へコピー）。(2) 熱モデルが煙の温度に届いていなかった: 接地中のタイヤにも空気での冷却（0.4/s）を二重にかけていた → 接地中は TyreSmokeRate の冷却だけ。冷却 45+1.5v W/K、荷重は 2000〜6000 N に制限、煙は 140→260°C（smoothstep）、滑り 6 m/s で最大。縦の滑りの路面速度は、回転するホイールの forward ではなく車軸×路面の法線で。.ksm インストール済み、ゲームで未確認。
- 11-01 ユーザー: 反射は「完璧に治ってそう」。グリップ時も煙が出る → `TrackMap` の滑りに不感帯（横 1 m/s + 5 %、縦 2 m/s + 25 % 未満は 0）。.ksm インストール済み。
- **地面の光の伸び（濡れた路面のランプの映り込み）が障害物越しに見える** → 合成はランプの遮られた割合を n·l だけの重みで求めていたので、拡散はほぼ無いが映り込みの強い遠くのランプが遮られても割合がほとんど変わらなかった。`NFXCommon.hlsli LampWeight`（n·l ＋ GGX α0.3・F0 0.04 の鏡面）を `LocalShadows.hlsl` の重みに（`NFX_FLAG_LAMP_SPECULAR`、`NativeEngine.LampSpecular` = RTShadowPass の exactUsed_）、合成の `LampsLuminance` も同じ重み。DLL は build/ に 07:32（ゲーム起動中でコピー失敗）、バンドルはビルド中。.ksm・DLL・バンドルの 3 つがそろうまで起動しないこと（フラグは古い DLL だと無視されるだけで壊れはしない）。
- 07:35 に DLL とバンドル（07:34）を本体へ。.ksm（07:31）とそろった。ゲームで未確認。
- **ロードマップ A4 の第 1 段: ランプごとの影マスク（11-01、.ksm 07:5x インストール、DLL は build/ にあり（ゲーム起動中でコピー失敗）、バンドルはビルド中。ゲームで未確認）**: 前の「LampWeight で遮られた割合を出す」案は、割合が全ランプ共通なので根本的には直らない → 置き換え。
  - `native/NFXNative/shaders/LampMask.hlsl`（種類 19、`kTraceKinds = 20`）: 半解像度の画素ごとに、HDRP のランプ（LightColor.w 1）のうち `LampWeight`（n·l ＋鏡面）が重い 4 つを選び、1 本ずつ光源の形の中のランダムな点へレイ。出力 float4 = 小さな整数を正確に持つ float（x ランプ 0 + 256×ランプ 1、y 2・3、255 はなし、z w 可視度 0..255 の組）。
  - `NativeEngine.TraceLampMask`（TraceLocal の面を使い回し、R32G32B32A32 の `lampMask_` へ Unpack4）。`NFX_FLAG_LAMP_MASK`（2）のとき LocalShadows は NatureFX が足す光（w 2）だけ。
  - `NFX_SunExact` の LampsExact パス（MRT）: HDRP の各ランプを `_NFX_LampPos` と位置（0.2 m 以内）で 4 つのスロットに対応づけ、ランプごとに exact × (1 − 可視度) を足す → `lampsExact_` は「影が取り去る光」。履歴（フル解像度、可視度は 12 bit の組）をモーションベクトルで再投影し、同じランプのスロットどうしで混ぜる（今のフレーム 0.12）。全 0 のマスクは「マスクなし」（古い DLL）。
  - 合成: `_NFX_SunExact.y` = マスク使用中 → removed = lampsExact + min(LocalShadows 分, 0)。
  - `RTShadowPass.DrawExactLamps`（トレースの後。マスクなしのときは従来どおり DrawExactSun でランプの exact）。設定 `RTLampMasks`（移行 46 で true）、Shadows タブ「Per-lamp shadows」。
  - 制限: 5 つ目以降のランプは影なし（一番弱いもの）。マスクは半解像度の最寄りの画素（縁は 2×2 のブロック、時間方向で平均）。
  - 07:44 に .ksm（07:42）・DLL・バンドルの 3 つをインストール（シェーダーエラーなし）。ゲームで未確認。
- **テストマップの組み直し（11-01、ユーザーの依頼「ルールを作りテストしやすく、素材はリアルに」）**: ルールと配置は docs/testmap-layout.md。Poly Haven CC0 を 18 点追加（253 MB、承認済み、docs/testmap-assets.md）。
  - `tools/kino_sdk/NfxTestBays.cs`（新規、partial）: 区画の表 `BayTable`（ID・左右・行・見出し・説明・作る関数）。区画はローカル座標（入口が原点、+Z が奥）。各区画に舗装・通りからのエプロン・駐車枠・カテゴリ色の枠・看板・ColorChecker の校正ボード。面は UV がメートルの箱（`MetricCube`）にスキャン素材。S1 スキッドパッド（x −110、z −80、半径 35 m、古タイヤ）、D0 データ（マップ内データのマーカー 1 種ずつ）、遠景の森は z 820。
  - `NfxDataTestMap.cs`: 旧区画の関数を削除、地面は 1.4 km の grass_ground、看板はカテゴリ色の見出し・発光 120 nits、プロファイルに nfxbay_ の [rules]/[materials]、霧と雪のゾーンの blend 8。
  - `NfxPreview.RenderBays`: 各区画の駐車枠からの画像（エディターのプレビューは空が暗く、日陰が真っ黒になる。ゲームでは空の光が入る）。
  - .knmap（518 MB）を 08:12 に `KN_Base/maps/test_map.knmap` へ上書き（マップはゲーム起動中でも上書きして反映できる）。ゲームで未確認。
- **A4 の残り・D・E（11-01、ユーザー「A4 を全て終わらせて E まで」）。.ksm・バンドル（08:24、シェーダーエラーなし）インストール済み、DLL は 07:44 のまま（変更なし）。ゲームで未確認**:
  - **A4 フルリライト**（設定 `RTFullRelight`、移行 47、Shadows タブ「Full relight」）: 合成が HDRP の画素を正確に分解して組み直す。GBuffer3（自前の再描画、`_NFX_GBuffer3Tex`）= 焼き込みの空の光＋発光（空の光は HDRP のプローブ×拡散色の推定で上限を取り、残りを発光とする）、太陽 = SunExact（影なし）、ランプ = LampsExact の第 3 出力 `lampsTotal_`（影なしの全ランプ）、残り = 映り込みなど。結果 = 発光 ＋ 空の光×空の見える割合 ＋ 独自の空との差 ＋ 太陽×影 ＋ ランプ（全体 − 遮られた分）＋ NatureFX だけのランプ ＋ 残り×（GI でないとき空の割合）＋ GI の跳ね返り ＋ 葉の透過（`LeafGlow` に切り出し）。`_NFX_SunExact.z` で切り替え。ランプがあってマスクがないときは従来の補正。
  - A4 の段階 2（太陽を独自の BRDF で）は行わない判断: SunExact は HDRP の BSDF を G バッファから正確に評価しており、書き直しても同じ式になるだけ。影・空の光・GI・反射は独自エンジンのもの。
  - **D 独自の後処理**（`Rendering/PostProcess.cs`、`NFX_Post.shader`、設定 `NativePost`・`PostBloom`、移行 48、Exposure & tone タブ「Own post-processing」）: BeforePostProcess の最後（優先度 −1000）で、測光（64×36 の log2 輝度、AsyncGPUReadback）→ C# の目の順応（中央重み、下 30 %・上 5 % を除く、EV100 = log2 L + 3）→ HDRP の Fixed 露出に渡す。ブルーム（6 段、13 タップの縮小、最初は Karis 平均、テントの拡大、しきい値なしで全体の 4 %）、トーンカーブ（ACES フィット・Khronos PBR Neutral・AgX・なし。設定の Filmic (GT) が AgX）、コントラストと彩度。HDRP のトーンマッピング（None）・ブルーム（0）・色調整（0）は優先度 10001 のボリュームで止める。HDRP に残るのは TAA・被写界深度・モーションブラー・レンズフレア（トーンマップ後の画に掛かるので、フレアは弱くなる可能性）。
  - **E ゲームのパーティクル**（`Rendering/GameParticles.cs`、設定 `NativeParticles`、移行 49、Lighting タブ「The game's particles」）: 半透明（加算・発光・メッシュ以外）の ParticleSystemRenderer のマテリアルを NatureFX/Particle のモード 6（元のテクスチャ・色・UV、柔らかい球として独自エンジンの太陽/月・空・ランプで照らし、3D の霧に入れる）に差し替え。3 秒ごとに探し、オフで元に戻す。
  - E のガラスを描き切る部分はまだ: ガラスの色と不透明度は HDRP のマテリアルにあり、独自エンジンの記録にないため、ゲームでガラスのマテリアルを調べてから。今は反射（RT）と屈折は独自、ガラスの照明だけ HDRP。
- **E のガラス: 独自エンジンでガラスを描き切る（11-01、ユーザー「ガラスも進めて」）。.ksm インストール済み、DLL は build/（08:35）、バンドルは reload/naturefx-20261101-083711.bundle（シェーダーエラーなし）。どちらもゲーム起動中でコピー待ち。3 つがそろうまで起動しないこと（古い DLL だと HDRP のガラスが消えたまま反射の差分しか描かれない）**:
  - `Glass.hlsl` の `NFX_FLAG_OWN_GLASS`（4）: 透過 = (1 − フレネル) × lerp(1, ガラスの色, 不透明度)（不透明度 = 記録の alphaScale × 色マップのアルファ、`NFXCommon GlassOpacity`）、映り込み = 滑らかなガラスはトレース、粗いガラスと届かない先は空の SH、太陽のきらめき（GGX、最小粗さ 0.02、`SunVisible`）、固いガラスの屈折は透過を掛けて差分。出力 w は負で −(1 + 透過 rgb 各 5 bit + 32768 × ガラスまでの m)。
  - `NFX_RTReflect` GlassComposite: w が負なら 色（ガラスの奥）× 透過 ＋ 映り込み（ガラスまでの 3D の霧を掛ける）。
  - `Rendering/OwnGlass.cs`: RayTracingScene が見つけたガラスのスロット（レンダラーとマテリアルの番号、`OwnGlass.Slots`）に `Hidden/NatureFX/Invisible`（`NFX_Invisible.shader`、HDRP が描かないライトモードだけ）を入れる。シーンは `OwnGlass.Originals` で元のマテリアルを読む。ガラスのパスが 30 フレーム以内に動いたときだけ（`RTGlassPass.LastRun`）。設定 `NativeGlass`（移行 50）、Lighting タブ「Glass」。
  - 制限: 一番手前のガラスだけ（奥のガラスは描かれない）。ガラスに付く HDRP の雨粒などの効果はなくなる可能性。
- **不具合（ユーザーの画像、ガレージ画面）: 車体が透けて見える** → ガレージの車の塗装は Transparent キューの不透明な面（アルファ 1）で、`RayTracingScene.Glass` の「滑らかなら glass」に当たり、OwnGlass が HDRP から隠してガラスとして描いていた（そのとき DLL は古く、差分だけ描かれて透けた）。修正: ベースカラーのアルファが 0.95 以上ならガラスではない（ガラスの語を名前に持つものは従来どおり）。さらに DLL の `NFX_Native_Info` に "features: own-glass lamp-mask" を足し、`NativeEngine.Supports("own-glass")` のときだけ HDRP のガラスを隠す（Mod が DLL より新しくても壊れない）。.ksm 08:3x インストール、DLL build/ 08:39、バンドル reload/083711 はコピー待ち。
  - 08:4x に DLL（08:39 ビルド）とバンドル（083711）を本体へ。.ksm（08:39）とそろった。ゲームで未確認。
- **11-01 ユーザー: 車体の映り込みに艶がない（ぼけている）、タイヤスモークが車体を貫通する**:
  - 映り込み: クリアコートはトレースでは粗さ 0.04 の鏡面だが、NRD の REBLUR_SPECULAR を通すと鋭さが落ちていたと推定。`NrdSpecUnpack.hlsl`: 粗さ 0.05 未満はトレースの結果をそのまま、0.05〜0.1 で REBLUR の結果へ移る（鏡面はレイ 1 本でノイズがほぼない）。DLL build/ 08:43（ゲーム起動中でコピー待ち）、NativeShaders は入れ済み（Developer > Reload shaders で反映）。
  - タイヤスモーク: パーティクルに衝突（World、3D、High 品質＝動く車のコライダーにも当たる、半径 0.12×大きさ、減衰 0.7、跳ね 0.05）。出る位置をタイヤの外側へ 0.3 m・進む向きの後ろへ 0.35 m。.ksm インストール済み（再起動で反映）。12000 粒の High の衝突は CPU の負荷になりうる（要確認）。
  - 08:4x に DLL（08:43）を本体へ。.ksm・バンドルとそろった。
- 11-01 ユーザー: 車体の映り込みは「完璧」。ただし RT のざらつきがゆず肌に見える → `Reflection.hlsl`: α ≤ 0.003（クリアコートなどの鏡面）は GGX を引かず正確な鏡の向き、濡れた路面の引き伸ばしも掛けない（車の屋根にも掛かっていた）。画面外の当たりの陰影は `ShadeReflectedHitSmooth`（空のレイ 4 本の層別平均、`NFXReflect.hlsli`）。DLL build/ 08:48（ゲーム起動中でコピー待ち）、NativeShaders は入れ済み（Reload shaders で反映）。
- 11-01 ユーザー: クロームの塗装もぴかぴかに → クロームの判定（トレースした面の金属度 ≥ 0.85、粗さ ≤ 0.25、クリアコートでない）: `NFX_NativeBridge` PrepareReflection で粗さを 0.03 まで（α 0.002 → Reflection.hlsl の鏡面扱い・NRD を通さない）、メッシュの法線で映す、フレネルは粗さ 0 の EnvBRDF。`NFX_RTReflect` の `ReflRoughness` も同じ規則（合成の重みと自前のぼかし）。バンドルは reload/naturefx-20261101-085156.bundle（エラーなし、コピー待ち）、DLL は build/ 08:48（コピー待ち）。
- **タイヤスモークを物理寄りに（11-01、ユーザー「障害物を避ける、風と同期、影」）**: .ksm インストール済み、バンドル reload/naturefx-20261101-085737.bundle（エラーなし）、DLL build/ 08:48 とともにコピー待ち。
  - 風: 抵抗を空気との相対速度に比例（limitVelocity の drag 1.5/s、大きさ・速度の掛け算なし）＋ 力 = drag × 風 → 約 0.7 秒で風と同じ速度に。浮力 0.8 m/s²（寿命で 1 → 0.1）。渦（curl noise）の強さは 0.5 + 0.12 × 風速。
  - 障害物: 前回の衝突（World・High）のまま。
  - 影: `TireSmoke.UpdateShadows` が毎フレーム 2000 粒ずつ太陽へ RaycastCommand（ジョブ）、Custom1.x（1 = 影、0.6 で寄せる）。頂点ストリームに Custom1X（TEXCOORD0.w）、`NFX_Particle` の煙で太陽の光に (1 − 影)。F8 の Tyre smoke の行に影の中の粒の数。
  - まだ: 煙が地面や車に落とす影（独自エンジンの太陽の影のトレースは煙を知らない）、煙どうしの自己遮蔽。
- **ゲームの夜のライトが NatureFX の時刻で点かない（11-01、ユーザー）**: ゲームは `DayNightController`（Assembly-CSharp、逆コンパイルは scratchpad/decomp_daynight のみ）がレースの夜モードで一度だけ夜の照明シナリオ（追加シーン）を読み、`IDayNightBehaviour`（DayNightLightBehaviour: ライトの GameObject を SetActive）を切り替える。`Rendering/GameDayNight.cs`: リフレクションで `instance`・`IsDayLight`・`isReady`・`isTimeoutToChangeModeOver`・`SetDayNightMode(bool)` を使い、太陽の高さ 2° 未満で夜、4° 超で昼（ヒステリシス）。NatureFX が時刻を動かしていないときはゲームの元の選択に戻す。設定 `SyncGameNight`（移行 51）、Sun & time タブ「The game's night lights」と状態の行。切り替えで追加シーンが読まれ、NatureFX の再スキャン（RT のシーン作り直し）が 2 秒後に走る。.ksm インストール済み、ゲームで未確認。夜のシナリオが太陽のライトを差し替えるトラックでの挙動は要確認。
  - 09:0x に DLL（08:48: 鏡面の映り込み）とバンドル（085737: クローム・煙の影）を本体へ。.ksm とそろった。
- **11-01 ユーザー: クローム・ざらつきは「完璧」、煙はまだ少し不自然、未実装を全部、月夜はユーザーが調整できるように**（すべて 09:1x に .ksm・DLL・バンドルをインストール、ゲームで未確認）:
  - 月: 設定 `MoonBrightness`（移行 52 で 1.5、0〜10、Sun & time タブ「Moon brightness」）。月のライトの強さに掛かり、独自エンジンの空・霧もそれに従う。
  - 煙の影のマップ（`TireSmoke.UpdateSmokeShadow`、1 フレームおき）: 粒を太陽の向きに 128×128（0.4 m）の格子へ投影し、光学的厚さ（1.4 × アルファ、半径内で (1 − r²)²）と太陽の向きの最近・最遠を記録。`NFX_FogCommon NFX_SmokeTransmittance`: その点と太陽の間の割合だけ減衰。合成（地面・車の太陽の光）と煙の粒（自己遮蔽、奥ほど暗い）で使う。
  - 煙が広がるほど薄く: 不透明度の時間変化を 1 → 0.6（0.15）→ 0.32（0.3）→ 0.16（0.6）→ 0 に（大きさの 2 乗に反比例する程度）。
  - ガラスの重なり（`Glass.hlsl` の `GlassPane`）: 一番手前の奥にあるガラスを最大 2 枚まで（映り込みは空の SH）、同じインスタンスの 10 cm 以内は同じ板の裏面として飛ばす。透過は掛け合わせ、光は手前の透過を掛けて足す。
  - ガラスの雨粒: CarRain が車の面ごとに描くので残る。判定は `OwnGlass.Originals` の元のマテリアルで。
  - **ユーザーの画像: 白い輪郭とライトアップされた所の残像** → ランプごとの影マスクが原因と推定。LampsExact: 周りの 4 つのマスク画素から深度が一番近いものを選ぶ（縁で背景のマスクを拾っていた）。履歴は 3×3 のマスク画素の同じランプの可視度の範囲に収める（動くヘッドライトの跡が残らない）、今のフレームの割合 0.12 → 0.2。
- **レンズフレアを独自の後処理へ（11-01、ユーザー）**: `NFX_Post.shader` の Flare パス（ブルームの 1/4 解像度の段から）: しきい値（露出後 3.0、中間の灰の約 30 倍）を超えた光から、ゴースト 5 つ（中心を挟んで倍率 −0.45/−0.9/0.3/0.65/−1.4、コーティングの色、端で弱まる、色収差）、中心のまわりのハロー、横の光の筋（12 タップ）。Final でトーンカーブの前に足す。強さは `LensEffects.FlareGhosts`（Lens flare × 夜 × 雨、従来の効き方）と `FlareStreaks`。`PostProcess.Active` の間は HDRP の ScreenSpaceLensFlare を止める。.ksm インストール済み、バンドルは reload/naturefx-20261101-091925.bundle（エラーなし、コピー待ち）。ゲームで未確認。
- **ヘッドライトを本物のロービームに（11-01、ユーザー「まぶしすぎる、現実と違う、LED・HID・ハロゲンを選びたい」）**: `HeadlightController` が前を向く赤くない車のスポットライト（重い Rigidbody の下）を、種類ごとの値に: ハロゲン 18 000 cd・3200 K、HID 36 000 cd・4300 K（カットオフの縁に青紫）、LED 30 000 cd・5800 K（1 灯の下向きの最大光度）。配光はクッキー（256²、80° の範囲）: 水平のカットオフ（水平線の 0.6° 下、左側は 15° で 1° 上まで上がる、左側通行）、その数度下にホットスポット、横に広い拡散、上は 1.5 % の漏れ光。元の値は覚えておいて「Game」で戻す。設定 `HeadlightType`（移行 53 で LED）、Road & cars タブ「Headlights」。.ksm インストール済み、ゲームで未確認。独自エンジンの霧・煙・ランプの影の重みはクッキーを知らない（円錐のまま）。ゲームが光の値を毎フレーム書き換える場合は 5 秒ごとの再適用まで戻る。
- **11-01 ユーザーの画像: 煙はだいぶ良いが影がおかしい**（地面に 0.4 m の階段、煙の中に太陽の向きの斜めの縞）→ `NFX_SmokeTransmittance` を 4 マスの双線形に（マスごとに透過率を出してから混ぜる）。煙の自己遮蔽は画素ごとではなく粒の中心で 1 回（CPU の同じ双線形 `TireSmoke.SmokeTransmittance`、Custom1.y = 遮られる割合、0.5 で寄せる、頂点ストリーム Custom1XY → TEXCOORD1.x）。09:3x に .ksm とバンドル（レンズフレアも含む）をインストール、DLL は 09:15 のもの。ゲームで未確認。
- **レンズフレアの作り直し（ユーザー: 主張が激しい、形がきれいでない）**: 画像の反転をやめ、光源ごとのゴーストに: 太陽（月）と ParticleLighting の近くのランプ 8 灯を画面に投影し、ブルームの 1/4 段のその位置の明るさ（しきい値 2.0、隠れていれば 0）で、中心を通る線上に絞りの六角形のゴースト 6 つ（縁がくっきり、縁は分散で色ずれ、大きいほど薄い、1 つあたり光の約 1/10000）、光条 6 本（Lens streaks）。ハローと横の筋はやめた。Final で描く（Flare パスは削除）。バンドルは reload/naturefx-20261101-094004.bundle（コピー待ち）。
- **ヘッドライトの光軸とハイビーム（ユーザー: 光軸調整が必要、照らし方が現実っぽくない、ハイビーム）**: 光の向きをゲームのライトの傾きではなく車体から決める（ロービームは `HeadlightAim`° 下、移行 54 で 0.6°、Road & cars タブ「Beam aim」）。ハイビーム: カットオフなしで軸に集まる配光（ホットスポット ±5°×±2.5°、広がり、手前は少し）、ロービームの 2.5 倍、自分の車（カメラに一番近い重い車）だけ。F7（Kino.Input）か Headlights カードのスイッチ（保存しない）。元の localRotation も覚えて「Game」で戻す。.ksm インストール済み、ゲームで未確認。
  - 09:4x にバンドル（094004、新しいフレア）を本体へ。.ksm・DLL とそろった。
- **11-01 ユーザーの依頼 4 件**（.ksm インストール済み、DLL build/ 09:47・バンドル reload/naturefx-20261101-094947.bundle はゲーム起動中でコピー待ち、NativeShaders は入れ済み。ゲームで未確認）:
  - ヘッドライトの値の保持: 毎フレーム、各ヘッドライトのクッキー・広がり・強さ（1 % 以内）・向き（0.05° 以内）が当てたままかを見て、違えばそのライトだけ当て直す（`Holds` / `ApplyBeam`）。
  - 配光を独自エンジンとパーティクルへ: `HeadlightController.Beams`（ライト → 1 ロー / 2 ハイ）。LocalLights が `LightSpot.z` に、ParticleLighting が `_NFX_PLightBeams` に。`NFX_BeamPattern`（クッキーと同じ式、ランプの上は世界の上）を NFXCommon の `LocalLightIlluminance`（ランプの影の重み・ランプマスク・GI の当たり）、FogVolume の霧の中の光、`NFX_FogCommon`（パーティクルの `LampLight`）で掛ける。
  - 煙の影をランプにも: `TireSmoke.UpdateSmokeVolume`（1 フレームおき、太陽のマップと交互）が 48×24×48・1 m の 3D 格子に消散係数（1.4 × α / 直径、(1 − r²) で広げる）と煙の箱を入れる。`NFX_SmokeLampTransmittance`: 点からランプまでを煙の箱で切り、6 段の三線形の読み。LampsExact で各 HDRP ランプの可視度に掛ける（フルリライトのランプの光も減る）、パーティクルの LampLight にも。
  - 奥のガラスの映り込みもトレース（`GlassPane` の trace を true）。
  - 09:5x に DLL（09:47）とバンドル（094947）を本体へ。.ksm とそろった。
- **11-01 ユーザーの依頼 4 件**（.ksm インストール済み、DLL build/ 09:56・バンドル reload/naturefx-20261101-095814.bundle はコピー待ち、NativeShaders は入れ済み。ゲームで未確認）:
  - 霧の中の光の筋に煙の影: 煙の 3D 格子をヘッダー 3 float4（原点と 1/セル、煙の箱）＋ 4 セルずつの float4 にして（`TireSmoke.FogSmoke`）、霧の Surface バッファの箱の後ろ（FogW×FogH×3 + 64×5）へ。`FogVolume.hlsl SmokeTransmittance`（4 段、箱で切る、ReflParams.z = 1）を太陽（30 m 先まで）とランプの光に掛ける。
  - 夜の照明で太陽が差し替わる場合: `GameDayNight` が切り替えの 0.5 秒後（読み込み済み）に `SunController.Refresh` し、ほかの平行光源（名前が NatureFX で始まらないもの）を消す（昼・NatureFX が時刻を動かさないときに戻す）。太陽が消えた間は昼へ戻さず、見つかるまで Refresh。
  - ヘッドライトの傾き: `HeadlightController.Roll`（水平の枠からのロール角、ラジアン）を LocalLights の `LightSpot.w`、ParticleLighting の `_NFX_PLightBeams.y` に。`NFX_BeamPattern(kind, forward, dir, roll)` は上方向をそのロールで回す。
  - レンズフレアをより自然に: ゴースト 5 つ（ほとんどは縁のやわらかい丸、1 つだけ六角形、色はほぼ無彩色、縁だけ分散、1 つあたり 1/20000）、画面の端ほど弱く（周辺減光）、光源のまわりのやわらかいにじみ、光条は太く短く長さに揺らぎ。
- **11-01 ユーザーの確認結果**: ヘッドライト・カットオフ・煙の影（ランプ）・奥のガラス・ランプの影の修正・テストマップは OK。月のスライダーが最大でも真っ暗、夜の照明の読み込みが遅い、ガラスと雨粒がリアルでない、ゲームのパーティクルは確認方法が分からない。
  - **月が暗い本当の原因 = 単位**: CarX のトラックの太陽は約 3 lux（HDRP 既定の π 程度、昼の空の天頂が約 0.18 nit）で、現実の約 3 万分の 1。月は太陽の比で 0.001 lux になり、目の順応の下限（ゲームの単位で 1 EV）に届かなかった。`Rendering/Units.cs`: 現実の単位 / ゲームの単位 = 100 000 / トラックの元の太陽（1000 未満のときだけ）、`SunController.Refresh` と `Update` で更新。目の順応の下限・上限は現実の EV（`PostProcess` と `VolumeOverhaul` で換算、移行 55 で −2 / 16）、Exposure の表示も現実の EV。現実の値で入れていたヘッドライトの cd と MapLights のプリセットの lm をゲームの単位に（÷ PhysicalScale）。月の明るさは 0〜20 倍。
  - 夜の照明: 切り替えを太陽 6° 未満（昼は 8° 超）に早め、切り替え中の追加シーンの読み込みは再スキャンを 0.3 秒後に（`GameDayNight.Switching`）。
  - 10:04 に DLL（09:56）とバンドル（095814）を本体へ。.ksm は 10:1x。ゲームで未確認。
- **雨粒を水らしく（11-01、ユーザー「雨粒の形と広がり方、物理液体のように」）**: `NFX_CarRain.shader` を作り直し: 小さな粒（2.5 cm のマスに 1 つ、落ちて育ち乾く、形は少し楕円、雨が強いほど早く入れ替わる）、流れる大きな粒（7 cm の筋、涙形で尾が上流、左右にふらつく、斜面ほど速い、後ろに乾いていく小粒の跡、通り道の小粒を拭う）。流れの向き = 面に沿った重力 ＋ 車の速度の空気（速度の 2 乗、約 70 km/h でフロントガラスの重力と釣り合う、`_NFX_CarAir` を車ごとに）。水平な面は流れず玉、速いと小粒が飛ぶ。見え方: 水滴の面の法線で屈折（1.33、0.6 m 先を画面から読む＝反転して見える）、縁の全反射の暗さ、空の映り込み、太陽のきらめき。注入を BeforePostProcess（優先度 −100、独自のガラスの後）へ。.ksm・バンドル（10:2x）インストール済み。ゲームで未確認。
- **11-01 ユーザーの画像: 夜の雨で街灯とライトが霧の中で大きく白飛び** → 前の単位の修正の読み違い。CarX は太陽だけが小さい単位（約 π lux）で、ランプ・ヘッドライト・発光面は HDRP の現実の単位だった。それを全部小さいとみなして目の順応の下限を下げたため、現実の値の光が白飛びした。修正: 逆に太陽（と独自エンジンの空・月）を `Units.PhysicalScale` 倍して現実の日差しへ（`SunController.physical_`、NatureFX がオンのとき。`Restore` は必ずトラックの元の値へ戻す：Refresh が強くなった太陽を元の値として読むと再スキャンごとに 3 万倍が重なるため）。ヘッドライトの cd・MapLights の lm・露出の換算は元（現実の値のまま）に戻した。目の順応の下限・上限は現実の EV（移行 55 の −2 / 16 はそのまま）。フルリライトの空の光は G バッファの焼き込み（弱い太陽で焼いたライトマップ）ではなく HDRP のプローブ（= 独自の空、A3）× 拡散色 × 空の割合。画像の白い点は雨粒（星ではない、星は雲の透過率を掛けている）。.ksm インストール済み、バンドルは reload/naturefx-20261101-102655.bundle（エラーなし、コピー待ち）。ゲームで未確認。
- **11-01 ユーザーの画像（夕方、車の横に大きな黄色いにじみと光条、白いひし形）**: (1) 独自のレンズフレアが車のランプを全方向の光源として扱い、照らされた路面やライトの縁の明るさを拾っていた → ランプごとに「カメラへ出す光」（スポットの円錐 × ヘッドライトの配光 `FlareBeam`）を掛ける（ParticleLighting の `_NFX_PLightColors/Dirs/Beams`）、しきい値 2 → 4、光源のまわりのにじみ 2.5e-4/0.012 → 6e-5/0.006。(2) 白いひし形 = ゲームのパーティクルの差し替えでアルファのないテクスチャが四角に → `GameParticles` がアルファの有無を調べ、ない場合は `_NFX_PTiles.z = 1` で明るさを被覆率に。.ksm インストール済み、バンドル reload/naturefx-20261101-103110.bundle（エラーなし、前回の照らし直しの修正も含む）はコピー待ち。ゲームで未確認。
  - 10:3x にバンドル（103110）を本体へ。.ksm・DLL とそろった。
- **11-01 ユーザー: 時刻の変更やトラックの読み込みでシーン読み込みのループ** → `GameDayNight` がゲームと綱引きしていたと推定（読み込みのあとゲームがレースの昼／夜を適用し直し、NatureFX がまた切り替える）。対策: 切り替えの間は 8 秒以上空ける、トラックの読み込み直後は 8 秒待つ、ゲームが自分で戻したら（期待した状態と違う）30 秒待つ、そのトラックで 3 回戻されたらゲームに任せる（状態の行とログに出す）。.ksm インストール済み、ゲームで未確認。
- 11-01 ユーザー: Moon brightness を 0.001〜0.1 に → 対数目盛のスライダー（log10 で −3〜−1、下に倍率と地面の lux）、SunController でも 0.001〜0.1 に制限、移行 56 で 0.1（1 倍 = 月明かり約 2 lux なので 0.1 倍でほぼ現実の満月）。.ksm インストール済み、ゲームで未確認。
- 11-01 ユーザーの画像（L2 ガレージの中が明るく床が青い、ネオンと GI の部屋が光らない）: 太陽を現実の明るさにしたことで、フルリライトの「残り」（主に HDRP の空の映り込み、HDRP は屋根の下でも青空を映す）が屋根の下で目立った → 残りに常に空の見える割合（ambientFactor）を掛ける（GI のときも）。ネオン・GI の色のにじみは昼の空の光より桁違いに弱いので、漏れが止まれば屋根の下で見えるはず。バンドル reload/naturefx-20261101-105210.bundle（エラーなし、コピー待ち）。ゲームで未確認。
  - 10:5x にバンドル（105210）を本体へ。.ksm・DLL とそろった。
- 11-01 ユーザーの画像（夜のネオンの小屋）: 床と球はピンクに染まっている（照らす処理は効いている）が、ネオンと看板のまわりのにじみが巨大 → 独自のブルームが 6 段を同じ重さで足していた。広い段ほど 0.55 倍ずつ弱く（`BloomFalloff`、Final で重みの合計で割る）。バンドルは reload/（コピー待ち）。
- 11-01 ユーザー: 床がネオンの色に染まらない → 不具合: RTShadow の `Upsample` が結果を 0 以上に切り捨てていて、ランプの影（3）が運ぶ「NatureFX だけの光（発光面）が足す光」（負の値）が毎回消えていた。ランプの影だけ切り捨てない。バンドルは reload/ または本体（このビルドの出力を参照）。ゲームで未確認。
- 11:23 にバンドル（111424: 発光面の光・ブルーム）を本体へ。.ksm（10:45）・DLL（10:04）とそろった。コミット 240d337 を push 済み。
- **11-01 DLAA（NVIDIA DLSS）で HDRP の TAA を置き換え（ビルド済み・DLL 未インストール・ゲームで未確認）**: ユーザーの選択は「独自エンジンで DLAA から」。`Dlss.inl`（NGX の初期化、機能の作成、パック → 評価 → アンパック）、`DlssPack/Unpack.hlsl`、ブリッジの `PrepareDlss`/`UnpackDlss`、`NativeEngine.Dlss`、`Rendering/Dlaa.cs`（ジッター、TAA を None に、DLSS が動かなければ TAA に戻す）。PostPass の最初で実行。設定 `Dlaa`（移行 57 で on、Own post-processing のカードにスイッチ）。11:4x 時点でゲームが起動中のため、DLL（build/ 11:39）と nvngx_dlss.dll は未コピー。.ksm（11:41）は入った（古い DLL は features に dlss がないので TAA のまま）。バンドルは reload/naturefx-20261101-114330.bundle。確認すること: ジッターの符号（違うと細部が揺れる・ぼける）、空の動き（カメラから計算）、車の動き、Status 行。
  - 11:45 に DLL・nvngx_dlss.dll・DLSS-LICENSE.txt・バンドル（114330）を本体へ。.ksm（11:41）とそろった。
  - ユーザー: DLAA を外すと綺麗（DLAA の方が汚い）。症状は未確認。Developer に DLAA カード（ジッターの符号 4 通り、動き HDRP+カメラ/カメラのみ/なし、ジッター量）を追加。.ksm 11:51、バンドル reload/115257（起動後に Reload shaders）。
  - ユーザーの画像: Status は DLAA at 1916x997（動いている）が輪郭がじゃぎじゃぎ → ジッターが絵に入っていない。HDRP は beginCameraRendering の前にカメラの行列を取るので、beginContextRendering / endContextRendering でずらすように変更（.ksm 再ビルド）。
  - ユーザー: ジッター 1 で改善したが輪郭がじらじら（符号 4 通りどれも同じ）→ 動きベクトルを自前に: Dlaa がジッターなしの行列（今と前フレーム）を渡し、PrepareDlss がカメラの動きを計算、HDRP の動きはそれ自身の行列でのカメラ分を引いた残り（0.5 px 超）だけ足す。DLSS の MVJittered を外した（DLL 12:03 build/、未コピー: ゲーム起動中）。.ksm 再ビルド、バンドル reload/120447。
  - DLL（12:03 ビルド）とバンドル（120447）を本体へコピー。.ksm とそろった。
  - 揺れが直らない → Player.log に「_NFX_NativeTarget ... wasnt created with the UAV usage flag」: PostPass の source_ がランダム書き込みなしで、UnpackDlss の書き戻しが捨てられていた（DLSS の結果が一度も絵に出ていなかった）。enableRandomWrite: true に（.ksm 再ビルド）。
  - ユーザー: DLAA で綺麗になった（確認済み）。既定のジッターの符号（-x -y、HDRP の DLSS と同じ）・動き（カメラ + 自分で動く物）のまま。次の候補: アップスケール（DLSS の品質モード、HDRP の動的解像度）、Ray Reconstruction。
- **11-01 DLSS のアップスケール（ビルド・インストール済み 12:20、ゲームで未確認）**: ユーザー「アップスケールと Ray Reconstruction を」。設定 DlssQuality（0 DLAA、1 Quality 67%、2 Balanced 58%、3 Performance 50%、4 Ultra perf 33%）。アップスケール時: Dlaa が HDRP アセットの動的解像度（m_RenderPipelineSettings をリフレクションで、Software・TAAU・forceResolution・useMipBias）を有効にし、カメラを TAA・allowDynamicResolution に、HDRP の m_TemporalAAMaterial のシェーダーを NFX_Upscaled に差し替え（TAAU パスが _NFX_UpscaledFinal をそのまま描く。DoF 後の 2 回目の TAA は taaAntiFlicker を 0 にして 3.5 で見分け、入力を素通し）。ジッターは HDRP の taaJitter。PostPass は DLSS で表示解像度の fullSource_ へ、ブルーム・トーンも表示解像度の専用テクスチャ（_NFX_PostPlain=1 で _RTHandleScale を掛けない）で fullFinal_ へ。DlssEventData 56 バイト（出力バッファ・出力サイズ・品質）。次は Ray Reconstruction。
  - ユーザー: Ultra perf. で DLSS 640x334 to 1916x997、ずれなし、GPU 使用率が大きく下がった（アップスケール確認済み）。
- **11-01 DLSS Ray Reconstruction（ビルド・インストール済み、ゲームで未確認）**: 設定 DlssRayReconstruction（既定 off、DLSS の下にスイッチ）。RR 中は太陽の影（SIGMA・自前フィルターなし、Filter(blend 1, 0 段)）と反射（REBLUR・自前フィルターなし）を生のまま合成し、DLSS-RR がまとめてノイズ除去。環境光・GI・ランプの影は 1/4 解像度なので従来のまま。ガイド: NFX_SunExact の DlssGuides パス（拡散アルベド・specularFGD）を RTShadowPass が描く（ExactFrame/GuidesFrame）、法線と粗さは HDRP の法線バッファ（PrepareDlss で 2 つ目の uint4、粗さは色の alpha）。ネイティブは DlssPackGuides.hlsl と NGX_D3D12_CREATE/EVALUATE_DLSSD_EXT（Roughness packed、HW depth）、行列はジッターなしの worldToView と GPU 射影（列優先）。DlssEventData 192 バイト、NFX_Native_DlssCaps（-1 未初期化、1 SR、2 RR）、features に dlss-rr。nvngx_dlssd.dll も同梱。途中で PC が再起動したが、スクリプトの再適用で復旧。
  - ユーザー: RR で動くが UI のちらつき・謎の光・がたつき、設定を何度か変えると画面がノイズに壊れた → Player.log: _NFX_DlssGuideDiffuse の次元違い（RR オフ時の代わりに Texture2D.blackTexture、カーネルは TEXTURE2D_X = 配列）で PrepareDlss ごと飛ばされ、DLSS が空のバッファを拡大していた。TextureXR.GetBlackTexture() に（.ksm 12:48）。UI のちらつき・謎の光・がたつきは切り分け待ち。
  - ユーザー: RR オンで Quality/Balanced などにすると光のノイズと画面の揺れ。ジッターの符号を試している途中でゲームが落ちた（Player.log: Device Remove Reason 0x887a0006 = DXGI_ERROR_DEVICE_HUNG、VRAM は 4.7/11.2 GB で不足ではない）。対策: RR は DLAA のときだけ（アップスケール中は自動で通常の DLSS、Status に表示）、ガイドのバッファは一度確保したら使い回す（.ksm 12:54）。RR とアップスケールの組み合わせの原因は未解明。
  - ユーザー: DLAA ＋ RR は問題なし、ノイズ等も出ない（確認済み）。残り: RR ＋ アップスケールの揺れ・光のノイズ・GPU ハングの原因調査。
- **11-01 RR ＋ アップスケールの調査**: NGX のログを NFX_ngx.log（NFXNative.dll の隣、起動ごとに作り直し）へ（LoggingInfo、レベル ON）。Developer の DLAA カードに「Ray Reconstruction with upscaling (test)」（Dlaa.DevRayReconstructionUpscaling、保存しない）。DLL は build/ 12:59（ゲーム起動中で未コピー）、.ksm は入った。
  - ユーザーのフォトモード比較（RR と従来）: RR だと床・タイヤの側面に白い粒（粗い面の反射のファイアフライ）。反射は RR でも REBLUR（鏡面は REBLUR でも生のまま）に戻した。RR が受け持つのは太陽の影だけ。.ksm 再ビルド済み、DLL（NGX ログ入り、build/ 12:59）はゲーム起動中で未コピー。
  - 結論（ユーザーと合意）: RR はガラス（独自エンジンが後から描くので G-buffer のガイドに入らない）や粗い面にノイズが残り、DLAA より重い。NRD より軽く高画質にはならない → おすすめは NRD ＋ DLSS Quality、RR は既定オフのオプションとして残す。RR ＋ アップスケールの調査は保留。
- **11-01 アップスケール時の動く影のノイズ**: ユーザー: Quality でバンパーの影が動くとノイズだらけ、DLAA では出ない、RR オフ。SIGMA オフ（自前フィルター）でも車の動きベクトルのパスのオン・オフでも変わらず → ノイズ除去ではない。切り分け用に Developer の DLAA カードへ「Upscale with HDRP's TAAU instead (test)」（Dlaa.DevHdrpTaau: 同じ低解像度を HDRP の TAAU で拡大、PostPass は DLSS を使わない）。.ksm 13:22。
  - ユーザー: TAAU のテストでも同じノイズ → DLSS ではなく低い解像度のせい。対策: SunShadow.hlsl を 1 ピクセル複数レイに（Flags の bit 8..10 = 本数 - 1、平均。x 見えた割合、y 遮った割合 × 距離＝ガラスと同じ表し方）。Dlaa がアップスケール中に NativeEngine.SunSamples = round(1/share^2)（Quality 2、Balanced 3、Performance 以下 4）。.ksm 済み、DLL（build/ 13:30）は起動中で未コピー、NativeShaders/SunShadow.cso は入った。
  - ユーザー: UI のちらつき（メニューと F8 のウィンドウが一瞬消える）が RR を入れた頃から稀に。今は起きていない。IMGUI も消えるので、何かがフレームの最後に画面全体を描いている可能性。再現条件待ち。
- **11-01 設定画面の整理（.ksm・DLL インストール済み、ゲームで未確認）**: サイドバー: GENERAL（Overview）/ LIGHT & SKY（Sun & time, Sky & fog, Lighting & shadows, Ambient light & GI, Reflections & glass）/ CAMERA（Exposure & tone, DLSS, Lens & eye）/ WORLD（Weather, Nature, Road, Cars, Map）/ ADVANCED（Ray tracing, Shader rebuild, Settings, Developer）。サイドバーはスクロール可。移動: ヘッドライト・車の塗装→Cars、レンズ・夜の目・被写界深度→Lens & eye、DLSS→DLSS、自前のガラス→Reflections & glass、光の筋と HDRP の霧→Sky & fog、ゲームのパーティクルと HDRP の影→Lighting & shadows、レイトレースのエンジン設定とコスト計測→Ray tracing、デバッグ表示（影だけ・正確な太陽光・GI だけ・What is this・履歴リセット）→Developer。重複を削除（Shader rebuild のタイヤ痕・路面・路面の摩耗→Road、Settings のシェーダー置き換え）。雪の深さは Rain and snow に。DLL（NGX ログ・影の複数レイ）も同時に入れた。
- **11-01 UI のちらつき**: 起動直後から、DLSS の設定は無関係。ユーザー: Every material（全置き換え）をオフにすると直る。メニューで置き換えていたのは Assetto Corsa Showroom（KSL のガレージ）・IconGenerator シーン（アイコン用の床・照明 Lamps/Cube 60 個・地面）・Kino.LiveryGroups.Background。IconGenerator シーンと Kino.LiveryGroups を置き換えの対象外に（MaterialReplacer.Classify、理由 menu pictures）。.ksm 再ビルド、ゲームで未確認。
  - ユーザー: Every material オンでもちらつかない（確認済み）。
- ユーザー: アップスケール中の動く影のノイズも大丈夫（影の複数レイ、確認済み）。
- **11-01 UI とモデルのちらつき（再発）**: NatureFX オフで収まる。DLSS オフで起動すると出ない、DLSS オンで起動すると出る（途中で DLSS を切っても収まらない、とユーザー）。NVIDIA App・コントロールパネルは入っていない（ドライバーのフレーム生成ではない）。NFX_ngx.log: 機能の作成は起動時の 1 回だけ（RR）、警告なし。調査用のログを追加: Dlaa の「DLSS diagnostics」（各コンテキストが描くカメラ、Camera.main の切り替わり、ジッターしたのに DLSS を通らなかったフレーム、メイン以外のカメラでの自前の後処理）。NGX ログは共有読み取りで開くように。.ksm・DLL インストール済み。
  - 調査結果: 診断ログは異常なし（カメラは GarageCamera だけ、ジッターの抜けなし）、NGX ログも警告なし。ユーザー: 崩れ方は UI のボタンなどが一部消える・黒や別の色（画面全体が古い画になるのではない）。DLSS オフで起動 → 途中でオン → ちらつかない（トラック往復でも出ない）。DLSS オンで起動したときだけ出て、そのまま残る。→ ゲームの起動中に DLSS（NGX の初期化・ジッター・TAA オフ）を始めると、ゲームの初期化の何かが狂う。仕組みは未解明。対策: Dlaa.Settled（独自エンジンのシーンが準備できてから 10 秒、レイトレースなしなら起動から 60 秒）まで DLSS を始めない。.ksm 再ビルド、ゲームで未確認。
  - ユーザー: DLSS オンのまま起動してもちらつかない（確認済み）。
- **11-01 車の動きベクトルと、自前の被写界深度・モーションブラー（.ksm・バンドル インストール済み、ゲームで未確認）**: (1) RayTracingScene.SetCarMotionVectors が動く車のレンダラーの motionVectorGenerationMode も Object に（元の値は保存して戻す）、NRD だけでなく DLSS 使用中も。(2) NFX_Post に MotionBlur（動きに沿って 16 サンプル、シャッターは 1 フレームに対する割合、手前でほとんど動かない面は除外）と DepthOfField（薄いレンズの CoC: 24 mm センサー・FOV からの焦点距離・F 値・ピント距離 = LensEffects.FocusDistance、40 タップ、手前のぼけは後ろに広がり、奥のぼけは手前に広がらない）。動きはブリッジの PrepareMotion（DLSS が走ったフレームはその バッファのジッターなしの動き、ほかは HDRP の動きベクトル）。順番は DLSS → モーションブラー → 被写界深度 → 露出・ブルーム・トーン。HDRP のモーションブラーと被写界深度はカメラのフレーム設定で止める（PostProcess.HdrpBlur、元に戻す）。設定 MotionBlur（0.5）・DofFStop（2.8）、移行 58、Lens & eye ページ。
- **11-01 シェーダー置き換えの段階 3・4（.ksm・バンドル インストール済み、ゲームで未確認）**: 段階 3: GameParticles が加算合成のパーティクル（_BlendMode 1、DstBlend One、名前 additive/glow/fire/flame/spark。premultiplied は除く）も NFX_Particle のモード 7（テクスチャの光 × 色 × _Emission cd/m² × 露出、加算、霧では減光のみ）に。明るさ: 火花 20000、炎・排気 5000、ほか 1500 cd/m²。段階 4: 不透明な Unlit（看板・画面）を LitAccumulation に置き換え、GenericTranslator が元の色・テクスチャを 250 cd/m² の発光に（地の色は 4%）。DerivedLitKeywords に _EMISSIVE_COLOR_MAP、NatureFX_variants.txt に発光の変種を追加（GBuffer Fragment 1824→2016）。透明な物（ガーランド等）は対象外: 画面に深度がなく、レイトレースの影を後から合成できない（シェーダー内でレイが要る）。
  - ユーザー: 車の動きベクトル・モーションブラー・被写界深度・段階 3・4、一旦 OK（確認済み）。
- **11-01 透明な物にレイトレースの太陽の影（DLL インストール済み、.ksm 済み、バンドルはビルド中、ゲームで未確認）**: (1) 独自エンジンのトレースの種類 20 SunMap（SunMap.hlsl）: 太陽の方向に垂直な格子（1024×1024、20 cm、約 200 m）の各マスから、太陽の方向 300 m 上から下へ Closest（アルファテストあり）の距離を書く。SunShadowMap.cs が 1 フレーム 64 行ずつ裏の地図へ（16 フレームで 1 枚）、できたら表と入れ替え、次の地図はそのときのカメラ（16 マス単位にスナップ）と太陽で。グローバル _NFX_SunMap（float）、_NFX_SunMapOrigin（中心、w マスの大きさ。0 = なし）、_NFX_SunMapDir（太陽、w マス数）、_NFX_SunMapParams（x 300 m、y バイアス 0.2 m）。features に sun-map。(2) GenerateNatureFXShaders が HDRP の ShaderPassForward.hlsl をコピーして、ライトループの直後に NFX_TransparentSunShadow（NFX_TransparentShadow.hlsl: 4 マスの判定の双線形で見える割合、太陽の DirectionalLight だけ ShadeSurface_Directional で計算し直し、影の分を引く）を _SURFACE_TYPE_TRANSPARENT のときだけ入れた Generated/NFX_ShaderPassForward.hlsl を作り、NatureFX/* の Forward パスがそれを使う。(3) 置き換え: HDRP の Shader Graph の透明（_SurfaceType がある、glass・water 以外）も LitAccumulation の透明に（_SurfaceType・_BlendMode などを引き継ぎ、キーワード _SURFACE_TYPE_TRANSPARENT・_ENABLE_FOG_ON_TRANSPARENT）。設定 RTTransparentShadows（移行 59、Lighting & shadows に状態表示）。
  - バンドルも 17:33 にインストール（Forward パスの差し替えはエラーなしでコンパイル）。
  - ユーザー: 透明な物の影、大丈夫（確認済み）。
- 11-01 ユーザー: シェーダー置き換えの段階 2（車）は一旦スキップ。RR ＋ アップスケールの調査もやめた。
- **11-01 ヘッドライト（ユーザーの画像: 夜の峠で手前の路面が白飛び、地面がぎらぎら、白い靄、走り出すと設定がリセット）**: (1) 露出: MeteredEV で明るい上位 10% が中間の灰より 3 段を超えないように（暗い夜の画面で露出が下限まで開き、照らされた路面が白飛びしていた）。(2) ロービームの手前の光: fill の減り方を 9° から 3.5° に（8° 下で最大の約 5%）、C# のクッキー・NFXCommon・NFX_FogCommon・NFX_Post の 4 か所。(3) ライトの探索を 5 秒から 1 秒ごとに（走り出すときにゲームがライトを作り直す・有効にする間、ゲーム自身のビームが見えていた想定）。.ksm 済み、DLL は build/ 18:02（起動中で未コピー）、NativeShaders は入れ済み、バンドルは reload へビルド中。
  - DLL（18:02）とバンドル（180401）を本体へ。.ksm とそろった。
  - ユーザー: 視点変更（走り出し？）でヘッドライトが元に戻る、手前はまだ白飛び、物理にのっとった実装に → 配光を ECE R149 class B の測定点に近づけた: 山を 1.2° 下に（上側の幅 1.2°、下側 2.2°）、手前の fill 0.25（4 か所）。露出の上限を 2 段に。車体（Rigidbody）の子でないスポットライトも、自分の車の 8 m 以内で同じ向きなら車のヘッドライトとして扱い、ログに名前（視点ごとのライトの想定、未確認）。.ksm 済み、DLL build/ 18:12 未コピー、NativeShaders 済み、バンドル reload/181427。
  - ユーザー: ハイビームや HID などに切り替えても、しばらくすると元に戻る。白飛びと地面のギラギラも残る → Holds が色温度・色・届く距離も見て、変わっていればかけ直す（ゲームが何を書き換えたかをログに 12 回まで: Headlights: the game set ... again）。ギラギラはランプごとの影のマスク（Per-lamp shadows、1 ピクセル 1 本のレイ）の可能性 → ユーザーにオフで試してもらう。白飛びは露出の表示を見てもらう。
  - ユーザー: Per-lamp shadows オフでもギラギラは変わらない。白飛びの場面の露出は -0.02 EV100（下限 0 EV に張り付き）→ 照らされた路面が画面の 10% 未満で、上位 10% の基準が効いていなかった。上位 3% が中間の灰より 2.5 段を超えないように。ギラギラは反射（ざらついた路面の 1 本のレイがヘッドライトのレンズに当たるファイアフライ）を疑う → 反射オフで試してもらう。
  - DLL（18:12）とバンドル（181427: ECE 配光）を本体へ。.ksm 18:29（上位 3% の露出）とそろった。ユーザー: ギラギラは白飛びが直れば消えるはず。
  - ユーザー: かなり良い。残り: 白い靄（霧はないので不要）、LED・HID の光の視差（カットオフの色のにじみと解釈）、色温度を変えたい → (1) FogVolume: ランプの光は天気の霧・箱・煙では全部、遠景のかすみ（トラックの短い霧距離）は澄んだ空気（MapFog.LampAirSigma 1e-4、天気の霧なら無制限、ReflParams.w）までしか散らない。(2) クッキー: HID と LED（プロジェクター）でカットオフの上に青紫、下にほのかな琥珀（HID 0.3、LED 0.18）。(3) 設定 HeadlightKelvin（0 = 種類の標準、2700〜8000 K、Cars のヘッドライト）。.ksm 済み、DLL build/ 未コピー（起動中）、NativeShaders/FogVolume.cso 済み（Reload shaders で反映）。
  - ユーザー: Track 拡張機能に、接地のなじませ・高さを使った混ぜ方・頂点カラーで塗る汚れ・細かいもの（デトリタス）の散布を → 地面の地図（独自エンジンのトレース種別 21 `GroundMap.hlsl`、機能名 `ground-map`）。インスタンスマスクに 0x04（地面: terrain か、動かない幅 6 m 以上の平たいメッシュ）と 0x08（路面: 名前に road/asphalt/tarmac）を足し、真下へのレイで 1024×1024 セル（12.5 cm、128 m）を 1 フレーム 64 行ずつ。`Rendering/GroundMap.cs` が `_NFX_GroundMap`（セルごとに色+高さ、法線+種類）を配る。シェーダー `NFX_Ground.hlsl`: (1) NFX_ApplyGroundBlend（`_NFX_Surface.w`、0 = マップの `[defaults] groundblend`、< 0 で切る）(2) NFX_HeightFill（`NFXSurface.height`。`_NFX_Surface2.x` で高さマップ、なければ明るさ×2.2。苔・汚れ・ほこり・落ち葉・雪）(3) NFX_ApplyPaint（`Map/MapPaint.cs`、プロファイルの `[paint]` を CPU で 512² のテクスチャに描く。頂点カラーの代わりに位置で指定）。(4) `Map/Detritus.cs`: ブリッジの `ScatterDetritus` が路面と地面の境目に置き、読み戻して種類ごとに 1 つのメッシュ（石・葉・小枝の手作りの形）、LitAccumulation で描く。設定 MapGroundDetail・Detritus（移行 60）、F8 > Map > Ground detail。C# はビルド済み、DLL は build/（起動中で未コピー）、バンドルもビルド。未確認。
  - ユーザー: 草のつなぎ目が気になる → (1) 地面の地図に草木の覆い: インスタンスマスク 0x10（抜きか風で揺れる物、NFX_FOLIAGE_MASK）、GroundMap.hlsl が地面の上 3 m 以内で草木に当たったセルの kind に +0.4（機能名 ground-cover）。NFX_Ground.hlsl NFX_GroundCover（0.35 m と 0.8 m の 2 重の輪）→ NFX_ApplyGroundCover: 草むらの下と周りの地面を暗く湿った落ち葉の色に。(2) NFX_ApplyFoliageFoot: 抜きのある草木のカードの地面から 35 cm を暗く地面の色へ、下 7 cm をばらばらに消す（全パスで同じ clip）。
  - 藪（ShrubRenderer、docs/map-authoring.md 5.6）: nocol_nfx_shrubproto_<名前>（nfxproto_lod0 / cloud / impostor）と nocol_nfx_shrub_<名前+名前>。スキャンは近い 40 本まで、雲 3 倍まで、インポスター 300 m。動きベクトルは Camera（Object だと画面全体が流れた）、HDRP の影は切る。独自エンジンには板の束を全部。焼き: tools/kino_sdk/bake_shrub_lods.py（Blender 5.2）→ shrubs_to_png.py。SDK: NfxShrubs.cs、テストマップ区画 N4。NFX_Impostor.hlsl（_NFX_Impostor）。
  - フレーム落ち: Detritus は 1 つのメッシュに組み直していた（24 m ごとに数百万頂点）→ インスタンス描画、1 フレーム 4000 個ずつ。MapPaint の描き直しは別スレッド。
  - ヘッドライト: 手前の塊と横の縁 → fill を /2.0（8° 下で 2 % 未満）、横 h/22、±38° で 0 に、カットオフの縁 0.5°（4 か所）。
- **11-02 ぼやけたテクスチャ（Showroom、full replace オンで再読み込み後）と車の影のカクつき**:
  - 日陰の壁や窓のぼけ: 独自エンジンが当たり先のテクスチャを読む mip を「1 枚約 4 m 四方」と仮定していた → `NFXCommon.hlsli` HitMip（三角形の UV 面積と実面積からテクセル密度、1 ピクセル 0.0009 rad、斜めは sqrt(cos) で一部だけ伸ばす）。表面トレース・窓越し・反射の当たり先に効く。
  - 色を持たない透明マテリアルの差し替え: 滑らかさ 0.3（すりガラス）→ 0.95（GenericTranslator）。
  - 調査用に追加: debug page「Switched materials' textures」（mip、追跡外の NatureFX マテリアル、シェーダー別の数、差し替えなかった理由）、Developer の「Load every texture mip」。差し替え後にゲームが元の名前で付け直したテクスチャを追う Resync、レンダラーへのマテリアルの付け直し（どちらも今回の原因ではなかったが残した）。
  - 白線などの半透明の面に落ちる車の影が 0.5 秒ほどでカクつく: SunShadowMap が 16 フレームで 1 枚 → 毎フレーム全体（約 0.1 ms 見込み）。ユーザー: 直った。
  - ネイティブ: SetInstances が使えないジオメトリを飛ばすと UpdateTransforms の番号がずれた → g_instanceSlot で呼び出し側の番号から引く。
