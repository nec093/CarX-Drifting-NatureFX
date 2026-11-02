# 引き継ぎ資料（2026-11-02 時点、ConfigVersion 78）

次のセッションがこのファイルと `AGENT.md` を読めば作業を続けられるようにまとめたもの。
基本ルール（日本語で返答、署名キー、push、ダウンロード許可、ビルド方法の細部）は `AGENT.md`、残りの作業は [ROADMAP.md](ROADMAP.md)、
マップの書式は [map-authoring.md](map-authoring.md)。ここには **今の構成・既定値・未確認の項目・ハマりどころ** だけを書く。
作業の経緯は `git log` にある（このファイルには日付つきの経過を書かない。区切りごとに「今の状態」を書き直す）。

---

## 1. 全体の構成

NatureFX は 3 つの成果物でできている。**3 つは同じ版をそろえて入れる**（構造体・バッファの配置が変わると、1 つだけ古いと表面・NRD・反射が壊れる。例: 古いバンドルで画面全体のざらつきと紫の点）。

| 成果物 | ソース | 置き場所（ゲームのフォルダ） |
|---|---|---|
| Mod 本体 `NatureFX.ksm`（C#、net472） | `src/NatureFX` | `kino/mods/` |
| シェーダーバンドル `naturefx.bundle` | `unity/Assets/NatureFX` | `kino/mods/NatureFX/` |
| 独自エンジン `NFXNative.dll`（C++ / D3D12） | `native/NFXNative` | `kino/mods/NatureFX/`（ほかに `nvngx_dlss.dll`、`nvngx_dlssd.dll`、`DLSS-LICENSE.txt`、`D3D12/D3D12Core.dll`） |

### 1.1 役割の分け方（方針: 高度な描画は独自エンジン、HDRP は最小限）

| 担当 | 中身 |
|---|---|
| **独自エンジン（NFXNative）** | Unity の D3D12 デバイスとキューをそのまま使い、自前の BLAS / TLAS とトレースのシェーダーで全レイを撃つ（Unity のレイトレーシングは使わない）。表面・太陽の影・環境光・GI（ラディアンスキャッシュ）・ランプの影とランプマスク・光の筋・反射・ガラス・空（透過率・多重散乱・sky view・空のプローブ・キューブ）・雲と雲の影・霧（2D と 3D）・透明面用の太陽マップ・地面の地図・揺れる葉の BLAS 更新。NRD と DLSS もこの DLL の中で Unity のキューに積む |
| **C# のカスタムパス** | 入力の用意（`NFX_NativeBridge.compute`）、トレースの依頼（`NativeEngine`）、結果の合成（`NFX_RTShadow` / `NFX_RTReflect` / `NFX_Atmosphere`）、独自の後処理（`NFX_Post`）、DLSS / 独自 TAA（`Dlaa`）、雨・煙・パーティクル |
| **HDRP に残るもの** | 深度・G バッファ・動きベクトル（マテリアルのラスタライズ）、遅延ライティングのパス（独自照明の間はほぼ何も足さない）、半透明（レイヤー 7 で HDRP の太陽・ランプを受ける）、UI、最終パス |

### 1.2 1 フレームの流れ

1. `BeforeRendering`: `AmbientFeedPass`（`OwnLighting.cs`）が独自の空のプローブを HDRP の `_AmbientProbeData` に入れる
2. HDRP: 深度・G バッファ・遅延ライティング（HDRP の太陽・ランプ・空の光は 0 にしてある、2 章）
3. `AfterOpaqueAndSky`（`RayTracingEffects` のグローバル CustomPassVolume、この順）
   1. `AtmospherePass`（トレース）: 空の表、雲、雲の影、空のプローブ、3D の霧
   2. `RTShadowPass`: 表面のトレース → 太陽の影・環境光 / GI・ランプの影とマスク → G バッファを描き直して太陽・ランプの正確な光（`NFX_SunExact`、`RTExactSun`）→ NRD（SIGMA / REBLUR）→ 合成（太陽・ランプ・空の光を影付きで足す。`RTFullRelight` では HDRP の画素を分解して組み直す）
   3. `RTReflectionPass`: 反射のトレース → REBLUR_SPECULAR → 合成（粗い面は独自の空のプローブ）
   4. `AtmospherePass`（合成）: 空の画素、霧
4. HDRP: 半透明・パーティクル
5. `BeforePostProcess`: `RTGlassPass`（独自のガラス）、車の雨（`CarRain`）、暗所の見え方（`LensEffects`）、写真モード、最後に `PostProcess`（優先度 −1000）
   - `PostProcess`: DLSS（DLAA かアップスケール）または独自 TAA → モーションブラー・被写界深度 → 測光（64×36）→ ブルーム → レンズフレア → 露出・ホワイトバランス・トーンカーブ
6. HDRP の後処理: 独自の後処理の間は実質オフ（トーンマップ・ブルーム・色調整・フレア・ビネット等はフレーム設定でオフ、TAA は使わない）。DLSS のアップスケール中は HDRP の最終パスのマテリアルを `NFX_UpscaledFinal` に差し替え、表示解像度の独自の画を出す

### 1.3 独自エンジンの呼び出し

- C# → DLL は `CommandBuffer.IssuePluginEvent(AndData)` の描画イベント: 1 キューのテスト、3 トレース（`TraceEventData`、種類は `kTraceKinds` = 22）、5 NRD SIGMA、6 NRD REBLUR 拡散（モード 2 はランプ）、7 REBLUR 鏡面、8 シェーダーの読み直し、9 DLSS。
- 受け渡しのデータ: `NrdEventData` 328 バイト、`DlssEventData` 192 バイト（`static_assert` あり）、フレーム定数 `Frame`（`NFXCommon.hlsli`、ローカルライトは最後、`NFX_MAX_LOCAL_LIGHTS` 128 = `LocalLights.Max`、`kConstantSlot` 8448）。C# の書き込み位置と必ず揃える。
- トレースバッファの詰め方は `NfxPack.hlsli` と `NFX_NativeBridge.compute` の同じ式（片方だけ変えない）。
- 方式は `Settings.RTBackend`: 1 = DXR 1.1（インラインのコンピュート、既定）、0 = DXR 1.0（`NFX_PIPELINE` で lib_6_6、状態オブジェクト＋シェーダーテーブル）。DXR 1.2（SM 6.9、SER / OMM）は Agility SDK 1.619 の独立デバイスで太陽の影だけのテスト（`RTDxr12Sun`、既定オフ）。
- DLL の機能は `NFX_Native_Info` の `features:` 文字列（own-glass lamp-mask dlss dlss-rr sun-map ground-map ground-cover）。C# は `NativeEngine.Supports(...)` で見て、古い DLL でも壊れないようにする。
- GPU が止まったときは DLL の隣に `NFXNative-gpu-hang.txt`（パンくず）、NGX のログは `NFX_ngx.log`。

---

## 2. HDRP からの置き換え（OwnLighting と CameraVolumetrics）

独自エンジンが置き換えた HDRP の機能は、**置き換える側が実際に動いている間だけ** 止める（合成が 30 フレーム止まれば HDRP に戻す。何も暗くならないように）。

| HDRP の機能 | 止める条件 | 場所 |
|---|---|---|
| 空の光（indirect diffuse ×0）・空の映り込み（reflection ×0）・空の描画（種類 none、キューブを作らない） | `OwnSkyLighting` かつ各合成が動いている | `OwnLighting.Ambient` / `Reflections` / `Sky`、`VolumeOverhaul`（IndirectLightingController） |
| 太陽の光 | `OwnSunLight`。太陽を誰も載らないレンダリングレイヤー（1<<30）に移す | `OwnLighting.Sun`、`RTShadowPass.CheckOwnSun` |
| 点光源・スポットの光 | `OwnLampLight`（ランプマスクが動いている間） | `OwnLighting.Lamps` |
| 半透明への照明 | `OwnTransparentLight`: HDRP の太陽・ランプはレイヤー 7 に。全マテリアルが半透明のレンダラーだけにビット 7 を付ける | `OwnLighting.UpdateRenderers` |
| 体積霧・体積雲・大気の散乱・Fog | 独自の霧 / 空 | `NatureFXMod` の `HdrpFogOff` / `HdrpCloudsOff` / `HdrpScatterOff`、`MapFog` |
| SSR・反射プローブ・プレーナー・空の反射 | `OwnLighting.Reflections` | `HdrpSsrOff` ほか |
| SSAO・SSGI | `OwnLighting.Ambient` | `HdrpSsaoOff` / `HdrpSsgiOff` |
| コンタクトシャドウ | 影の合成が動いている | `HdrpContactOff` |
| シャドウマップ・シャドウマスク | 太陽とランプの両方が独自 | `HdrpShadowMapsOff` / `HdrpShadowmaskOff`、`RayTracingEffects`（影を持つほかの平行光源も） |
| ブルーム・スクリーン空間のフレア・データ駆動のフレア・色調整・トーンマップ・ビネット・色収差・粒状・歪み・Panini | 独自の後処理（`PostProcess.Active`） | `HdrpBloomOff` / `HdrpFlareOff` / `HdrpPostOff` |
| 被写界深度・モーションブラー | 独自の後処理で使うとき | `PostProcess`（フレーム設定） |
| TAA | 独自の後処理の間は常に（DLSS か独自 TAA） | `Dlaa` |
| 自動露出 | 常に NatureFX の露出（HDRP の Exposure は Fixed で上書き） | `PostProcess` |
| ゲームの描いた影の板 | RT の太陽の影が動いている | `FakeShadows` |
| トラックの ReflectionProbe | 独自の空の間（ロード時に一度だけ古い空で撮られている） | `SceneProbes` |

- `CameraVolumetrics` はカメラのフレーム設定の 1 項目だけを上書きし、要らなくなれば元に戻す。終了時（`OnDestroy`）にすべて戻す。
- 太陽は `Units.PhysicalScale` 倍して現実の明るさにしてある（CarX の太陽は約 π lux、ランプ・ヘッドライト・発光は HDRP の現実の単位）。露出の EV は現実の EV。
- 独自 BSDF（`OwnBrdf`、既定オフ、実験的）: G バッファを描き直さず、表面トレースの色と HDRP の法線で太陽・ランプを照らす。トレースしない物（遠景の森・草・デトリタス）はレイヤー 7 にして HDRP に照らさせる（`OwnLighting.Untraced`）。

---

## 3. ビルドと確認

コマンドは `AGENT.md` の「ビルド」と README。要点だけ:

- クラウドのセッションには dotnet・dxc・Unity が無い。そこで書いた変更は **コンパイル未確認** としてユーザーに渡し、3 つをビルドしてもらう。
- バンドルのビルドログ `<Unity プロジェクト>/Logs/naturefx_build.log` で必ず見るもの:
  ```bash
  grep -a "Shader error" $L | sort | uniq -c
  grep -a "lost keywords\|variant materials" $L
  ```
  シェーダーのコンパイルエラーはそのシェーダーがバンドルから **黙って抜ける**（例: 合成で `NFX_COAT_ROUGHNESS` が未定義 → 影の合成ごと消えた）。`variant_054 (NatureFX/LitAccumulation) lost keywords: _NORMALMAP` は既知の警告。
- フルビルドは約 30 分（変種の絞り込み後）。共通の hlsl（`NFX_Accumulation.hlsl` など）を変えると全バリアントのコンパイルし直し。RT の合成シェーダーだけなら数分。
- 変種の絞り込み: ゲームが `kino/config/NatureFX/game_hdrp/game_variant_profile.txt` を書く（`Tools/VariantProfileDump.cs`）→ `unity/variants/` にコピーすると `VariantProfileStripper` が使う（`unity/variants/README.md`）。製品ビルドの Unity は変種のログを書かないので、Unity の記録は使えない。
- 新しい .ksm は `_NFX_NoiseTex`（`ShaderGlobals.EnsureNoiseTexture`）を作る。無いとバンドルのノイズが一定値になり、雪・濡れ・落ち葉の模様が消える。
- 再起動なしの確認: デバッグページの developer タブ > Reload shaders（`reload/` の新しいバンドルと `NativeShaders/*.cso`）。C#・C++・構造体の配置を変えたら再起動。
- ゲームの見た目は自分で確認できない。変更ごとに「どこで何を見るか」（F8 のどのタブ、デバッグページのどの行、`kino/output.log` のどの行）を具体的に伝える。Unity のエラー（カーネルの次元違い、UAV のフラグなど）は `kino/output.log` ではなく Unity の `Player.log` に出る。

---

## 4. 設定と既定値

`Settings.cs`: `ISettings`（KSL が自動保存）、`SettingsDefaults`（同じメンバーの既定値の入れ物）、`ApplyFirstRun` と `Migrate`。

- **既定値を変えるときは必ず新しい移行を足す**（`if (d.ConfigVersion < N) { ...; d.ConfigVersion = N; }`）。`Migrate` はユーザーの設定と `Defaults` の両方に走るので、F8 の「既定値に戻す」（`UserPresets.ResetAll`）と右クリックのリセットもそれに従う。ユーザーが動かした値を守りたいときは「元の既定値のままなら」の条件を付ける（移行 45 の例）。
- 現在の ConfigVersion は **78**。29 は欠番。
- 使われなくなったが設定ファイル互換のために残すもの: `RTOverlay`（計測オーバーレイはデバッグページへ移した）。コードのコメントで古いもの: `ExposureMode` の「0 game」は今は **0 = Fixed**、`TonemapMode` の「2 Filmic (GT)」は AgX。

### 4.1 意味のある既定値（移行のあとの値）

| 分野 | 既定値 |
|---|---|
| 独自エンジン | `NativeSky` / `NativeFog` / `NativeLighting` オン、`OwnSkyLighting` / `OwnSunLight` / `OwnLampLight` / `OwnTransparentLight` オン、`OwnBrdf` オフ |
| RT | `RTBackend` 1（DXR 1.1、移行 75）、`RTDenoiser` 2（NRD、移行 77）、`RTTraceScale` 0（半解像度）、影・環境光・跳ね返り・GI（20 m）・反射（品質 Quality、粗さ 0.8、150 m）・光の筋・ランプ・ランプの影・ランプマスク・`RTExactSun`・`RTFullRelight`・透明面の影・揺れる葉の影 オン、`RTDxr12Sun` オフ |
| ガラス・粒子 | `NativeGlass` / `NativeParticles` オン |
| 空の光 | `SkyLightStrength` 1、`SkyLightSaturation` 0.5（実測の空の青さ）、`MoonBrightness` 0.1（ほぼ現実の満月） |
| 後処理 | `NativePost` オン、`PostBloom` 0.02、トーンは `TonemapMode` 1（PBR Neutral、移行 70）、コントラスト・彩度 0、`WhiteBalance` 0.6 |
| 露出 | `ExposureMode` 1（目の順応）、`AutoExposureMin` 2 EV / `Max` 16 EV、`NightDarkness` 1、`FixedExposure` 14 EV（Fixed を選んだとき、移行 76） |
| DLSS | `Dlaa` オン、`DlssQuality` 0（DLAA）、`DlssRayReconstruction` オフ |
| レンズ | `MotionBlur` 0.5（180°）、`DofFStop` 2.8、`DepthOfField` オフ、`LensFlare` / `LensStreaks` 0.3、`NightVision` 0.8 |
| 時刻 | `SunOverride` オフ、`TimeOfDay` 14、`TimePasses` オフ（速さ 60 倍）、`SyncGameNight` オン |
| 車 | `HeadlightType` 3（LED）、`HeadlightStandard` 0（日本）、`HeadlightAim` 0.6°、`OilFilm` 0（移行 74。設定としては残る）、タイヤスモーク 1、赤熱・汚れ・クロームのホイール オン |
| シェーダーリビルド | `ReplaceShaders` / `FullReplacement` / `RebuildFoliage` オン、`AntiTiling` 1（マップが `antitiling=` を指定した素材だけ） |
| マップ | `UseMapProfile` / `MapEffects` / `MapProbes` / `MapGroundDetail` オン、草 1・60 m、藪 1・25 m、デトリタス 1 |
| 開発 | `DebugPage` オン（移行 78、`127.0.0.1:8765`）、`WriteMaterialInventory` オフ、`TextureStreamingBudget` 0（ゲームの値） |

### 4.2 F8 とデバッグページ

- F8 のタブは README の表のとおり（`NatureFXWindow.cs`）。Developer タブはデバッグページのスイッチと状態だけ。
- 開発用のスイッチと道具は `Tools/DevControls.cs`（デバッグページの developer タブ）: RT の確認用表示（影だけ・正確な太陽の光・環境光 / GI だけ、履歴のリセット、What is this?）、Reload shaders、NRD の各部（保存しない）、DLSS のテスト（ジッターの符号・動き・量、RR ＋アップスケール、HDRP の TAAU 経路）、テクスチャの予算・全 mip の読み込み、比較（GI のランプ基準、3D 霧のタイルごとのライトリスト、煙の最大サイズ）、シェーダーのダンプ・ルールの再読み込み・変種の記録・マテリアル一覧、計測のスイッチ（CPU・NVML）。
- デバッグページ（`Tools/DebugPage.cs`、`DebugPageHtml.cs`）は TcpListener をループバックだけに開く。スナップショットはページが開いている間だけ、メインスレッドで 1 秒ごと。CPU・GPU の計測は別スレッド（`CpuStats`、`GpuStats`。NVML のクロック・電力・温度は既定オフ: ドライバーによっては GPU が止まる）。DLSS（NGX）の確保は VRAM の内訳に数えられない。

---

## 5. 既知の問題と未確認の項目

### 5.1 ゲームで未確認（確認の記録がないもの）

最後のコミット `efc4012` までの次の変更は、ユーザーのゲーム確認の記録がない。次にゲームを動かしたら見てもらう。それより前の未確認の項目（DLSS 切替のクラッシュ対策、長時間・マップ切替、マップ拡張の見た目など）は [ROADMAP.md](ROADMAP.md) の「ゲームでまだ確認していないもの」。

| 項目 | 何を見るか | 主な場所 |
|---|---|---|
| 独自の後処理の間 HDRP の後処理・TAA・Fog を使わない | 昼夜・雨で画が崩れない、HDRP のフレアやビネットが重ならない | `NatureFXMod`（`HdrpPostOff`）、`PostProcess` |
| 独自 TAA | DLSS をオフにしたときの輪郭とゴースト | `PostProcess`（Taa）、`Dlaa.OwnTaa` |
| DLSS アップスケールの独自経路 | Quality〜Ultra perf. で UI・解像度・ずれ。旧経路はデバッグページの「Upscaling through HDRP's TAAU pass」 | `Dlaa`、`NFX_UpscaledFinal.shader` |
| 露出 Fixed（14 EV）と目の順応・暗い場面 | F8 Exposure & tone の Current exposure、夜の街灯の下 | `PostProcess`、`Settings.FixedExposure` |
| トーン・色（PBR Neutral、ホワイトバランス、空の光の色 0.5） | 夕方・ランプの下の色かぶり、日陰の青さ | `PostProcess`、`NativeEngine.SkyLightSaturation` |
| 雨（空気・雨粒・路面・車の 4 段階） | 雨の視程、雨粒の影と光、濡れていく路面と水膜・波紋、車の水滴・水しぶき | `MapFog`、`Precipitation`、`AccumulationController`、`CarRain`、`TireSmoke` |
| 粒子の太陽の影（透明面用の太陽マップ） | 日陰の中の雨粒・煙が暗いか | `NFX_Particle.shader`、`SunShadowMap` |
| 3D の霧の空の光（空が見える分だけ）、GI の空 0.15 | 屋根の下・部屋の中の白いもや | `FogVolume.hlsl`、`GI.hlsl` |
| ヘッドライトのカットオフの色のにじみ・欧米の配光 | 夜の路面のカットオフ | `HeadlightController` |
| 既定値の移行 75〜78 | 既定値に戻したとき DXR 1.1・NRD・デバッグページがオン | `Settings.cs` |

### 5.2 未解決・制限

- ときどき 33 ms のフレームが出る（原因未調査。デバッグページの performance > slow frames で区間を見る）。
- DLSS Ray Reconstruction はアップスケールと組み合わせると画面の揺れ・光のノイズ・GPU ハング（`DXGI_ERROR_DEVICE_HUNG`）が出たので DLAA のときだけ。RR が受け持つのは太陽の影だけ（反射は RR だと粗い面に白い粒が出たので REBLUR）。
- 独自 BSDF は、表面トレースの色にアンチタイリング・積もり・地面なじませ・ペイント・ディテールが無いので模様が二重に見える（実験的のまま）。
- ランプの影は 1 画素で重い 4 灯まで（5 灯目以降は影なし）。ランプマスクは半解像度。
- 合成のあとに HDRP が描く半透明: HDRP が透明パスの前に `_AmbientProbeData` を入れ直すなら空の光は黒のまま（未確認）。
- 独自ガラスは手前の 1 枚＋奥の 2 枚まで。HDRP がガラスに付ける効果は消える（車の雨粒は `CarRain` が描くので残る）。
- 雨で未対応: 塗装そのものの濡れ（ゲームの Shader Graph）、屋根の下の乾き、雲の中の雨の筋。
- HDRP の固定確保（クッキーのアトラス 85 MB、カスケード影 36 MB、プローブのアトラス）は実行中に小さくできない（パイプラインアセットの作り直しが要る、危険なので見送り）。
- テクスチャのストリーミング予算はほぼ効かない（ストリーミング対象は 70 MB 前後、残りはストリーミングされない）。
- F8 の文言で古いもの（コード、未修正）: Exposure & tone の「HDRP then only does depth of field and motion blur」、DLSS カードの「others keep TAA」（実際は独自 TAA）。Lighting の「The 32 point and spot lights」（実際は 128 まで）。
- そのほかの残り（シェル法の苔・草、レビューの未修正分、車のシェーダーリビルド、性能、DXR 1.2 側など）は [ROADMAP.md](ROADMAP.md)。

---

## 6. ハマりどころ（今も当てはまるもの）

**HDRP / Unity**

- `VolumeManager.instance.stack` は既定値しか持たない。カメラごとの値は `HDCamera.GetOrCreate(cam).volumeStack` → `VolumeOverhaul.Stack()` を使う。
- HDRP は `beginCameraRendering` より前にカメラの行列と動的解像度を取る。ジッターは `beginContextRendering` / `endContextRendering` で入れる（`Dlaa`）。
- ライトの `affectDiffuse` / `affectSpecular` を両方オフにすると HDRP はライトを一覧から外し、正確な太陽のパスが太陽を失う → 誰も載らないレンダリングレイヤーに移す（`OwnLighting`）。HDRP のライトレイヤーが無効なアセットではレイヤーが見られないので、`RTShadowPass.CheckOwnSun` が太陽の消失を見張り、消えたらそのセッションは HDRP の太陽に戻す。
- HDRP の自動露出を独自の後処理の画に使わない（トーンマップ済みの画を測って下限まで開き、真っ白になった）。露出は常に NatureFX が測る。
- カスタムパスの時点で HDRP の G バッファは別用途に使われて残っていない → 正確な光のパスは不透明物の GBuffer パスを描き直す（デカールのバッファも中立のものに）。
- トラックの ReflectionProbe はロード時に一度だけ、その時の空で撮られる（夜に昼の空が映った）→ 独自の空の間は無効（`SceneProbes`）。
- CarX の HDRP のコンピュートシェーダー・GPU カリング・GPU タイヤスモークには DX12 のカーネルが無い → `Dx12Fix`、`GpuCullingDx12`、`TireSmokeDx12`。
- コンピュートで書き戻すテクスチャは `enableRandomWrite` が要る（無いと書き込みが黙って捨てられ、DLSS の結果が一度も画に出なかった）。`SharedColor` は付けてある。
- `TEXTURE2D_X`（配列）の引数に `Texture2D.blackTexture` を渡すと次元違いでカーネルごと飛ばされる（`Player.log` に出る）。
- RTHandle の余白には古い内容が残る。後処理の読み取りは画の内側 1 テクセルまでに制限（画面端のにじみ）。
- DLSS をゲームの起動直後（最初のシーンの読み込み中）に始めると、そのセッションの間メニュー UI やモデルがちらつく → `Dlaa` はシーンをしばらくトレースしてから（RT なしなら起動 1 分後）最初の DLSS を始める。
- ゲームは夜の照明を追加シーンで切り替える。NatureFX が切り替えるとゲームと綱引きになり読み込みがループした → 切り替えの間を 8 秒以上空ける（`GameDayNight`）。
- ゲームはヘッドライトの値（クッキー・強さ・向き・色温度）を書き戻すことがある → `HeadlightController.Holds` が毎フレーム確かめて当て直す。
- `MaterialInventory.Write` は全テクスチャを読み戻して PNG にするので数百 MB の VRAM の山とひっかかり → 頼まれたときだけ（`WriteMaterialInventory`）。
- `NFX_AntiTiling.hlsl` は `__SAMPLEUVMAPPING_HLSL__` を先に定義して HDRP の SampleUVMapping を差し替える。`SURFACE_GRADIENT` はこのファイルの後で定義されるので、それに頼ったフィールドを置かない。
- アンチタイリングは素材ごとのオプトイン（白線入りの道路や 1 枚絵のスキャンはずらすと壊れる）。

**独自エンジン**

- 合成で光を差し引くときは色チャンネルごとにクランプしない（明るさの割合で）。ランプの影が運ぶ「NatureFX だけの光」は負の値なので、アップサンプルや合成で 0 に切り捨てない。
- 作業用メモリの確保・解放は描画スレッドで行わない（`WaitRenderIdle` の後だけ）。BLAS は退役させてから解放する。
- 揺れる葉の BLAS 更新は 1 フレーム `kWindBudget`（64）本まで、順番に（`Wind.inl`）。
- `windows.h` は `small` を `char` に定義する（変数名に使わない）。
- ペイロード・アルベドの α の詰め方・クリアコートの判定は `AGENT.md` の「実装上の注意」。

---

## 7. ファイルの地図

| 分野 | 主なファイル |
|---|---|
| 入口・毎フレーム | `src/NatureFX/NatureFXMod.cs`（HDRP の機能の切り替えもここ）、`Settings.cs`、`UserPresets.cs` |
| UI | `NatureFXWindow.cs`（F8）、`SettingsUI.cs`（KSL メニュー）、`Ui/` |
| 独自エンジンの橋渡し | `Rendering/NativeEngine.cs`、`RayTracingScene.cs`（シーンの収集）、`RayTracingEffects.cs`（パスの組み立て）、`EngineLighting.cs`、`LocalLights.cs`、`Capabilities.cs` |
| RT のパス | `Rendering/RTShadowPass.cs`、`RTReflectionPass.cs`、`RTGlassPass.cs`、`RTSurfaceTrace.cs`、`SunShadowMap.cs`、`GroundMap.cs`、`HistoryGuard.cs`、`SharedColor.cs` |
| HDRP からの置き換え | `Rendering/OwnLighting.cs`、`CameraVolumetrics.cs`、`VolumeOverhaul.cs`、`OwnGlass.cs`、`SceneProbes.cs`、`FakeShadows.cs`、`Units.cs` |
| 空・霧・雲・時刻 | `Rendering/AtmospherePass.cs`、`CloudController.cs`、`SunController.cs`、`GameDayNight.cs`、`Map/MapFog.cs`、`Map/MapNight.cs` |
| 後処理・露出・AA | `Rendering/PostProcess.cs`、`Dlaa.cs`、`ExposureProbe.cs`、`LensEffects.cs`、`PhotoMode.cs` |
| 天気・自然 | `Nature/WeatherController.cs`、`AccumulationController.cs`、`WindController.cs`、`TrackMap.cs`、`Rendering/Precipitation.cs`、`Map/Seasons.cs` |
| 車 | `Rendering/HeadlightController.cs`、`TireSmoke.cs`、`CarRain.cs`、`CarGlow.cs`、`CarFinish.cs`、`GameParticles.cs`、`ParticleLighting.cs` |
| シェーダーリビルド | `Nature/MaterialReplacer.cs`、`ShaderBundle.cs`、`Rebuild/`、`Map/MaterialProfiles.cs` |
| マップ拡張 | `Map/`、`Nature/GrassRenderer.cs`、`ForestRenderer.cs`、`ShrubRenderer.cs`（書式は [map-authoring.md](map-authoring.md)） |
| 開発ツール | `Tools/DebugPage.cs`、`DebugPageHtml.cs`、`DevControls.cs`、`CpuTimings.cs`、`CpuStats.cs`、`GpuStats.cs`、`SceneShaderDumper.cs`、`VariantProfileDump.cs` |
| 独自エンジン | `native/NFXNative/src/NFXNative.cpp`、`Nrd.inl`、`Dlss.inl`、`Wind.inl`、`RtDevice.inl`（DXR 1.2）、`shaders/`（`NFXCommon.hlsli`、`NfxPack.hlsli`、トレースごとの .hlsl） |
| バンドル | `unity/Assets/NatureFX/Shaders/`（合成・後処理・パーティクル・橋渡し）、`ShaderLibrary/`（Lit に差し込む hlsl）、`Editor/`（生成・ビルド・変種の削減） |
| ビルド | `tools/build_bundle.sh`、`tools/build_native.ps1`、`tools/build_dxr12_probe.ps1`、`unity/variants/` |
| テストマップ | `tools/kino_sdk/`（`NfxDataTestMap.cs`、`NfxTestBays.cs`、素材の取り込み） |

### 7.1 リポジトリ外の場所（ユーザーの PC）

| 物 | 場所 |
|---|---|
| リポジトリ | `C:\Users\kppn1223\Documents\carx\mods\NatureFX` |
| Unity のバンドル用プロジェクト | `C:\Users\kppn1223\NatureFX-Unity`（ログ `Logs\naturefx_build.log`） |
| Kino Content SDK（テストマップ） | `C:/Users/kppn1223/Desktop/kino_content_sdk` |
| ゲーム | `C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online`（ログ `kino/output.log`） |
| 外部の依存・素材 | `%USERPROFILE%\NatureFX-Deps`（NRD、DLSS、Agility SDK 1.619、dxc 1.9、Poly Haven の素材） |
