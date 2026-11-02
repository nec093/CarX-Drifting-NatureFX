# 引き継ぎ資料（2026-11-02 時点、ConfigVersion 78）

本資料は、次の作業者が本資料と `AGENT.md` を読んで作業を継続できるようにするためのものである。
記載対象は、現在の構成、既定値、未確認の項目および注意点に限る。
基本ルール（日本語での応答、署名キー、push、ダウンロード許可、ビルド方法の詳細）は `AGENT.md` に、残作業は [ROADMAP.md](ROADMAP.md) に、マップの書式は [map-authoring.md](map-authoring.md) に記載している。
作業の経緯は `git log` で参照できる。本資料には日付付きの経過を記載せず、作業の区切りごとに現在の状態へ書き改める。

---

## 1. 全体構成

NatureFX は次の 3 つの成果物で構成される。3 つの成果物は必ず同じ版を組み合わせて配置する。構造体やバッファの配置を変更した場合、1 つでも古い版が残っていると表面、NRD、反射の描画が破綻する。たとえば古いシェーダーバンドルを使うと、画面全体のざらつきと紫色の点が発生する。

| 成果物 | ソース | 配置先（ゲームのフォルダ） |
|---|---|---|
| Mod 本体 `NatureFX.ksm`（C#、net472） | `src/NatureFX` | `kino/mods/` |
| シェーダーバンドル `naturefx.bundle` | `unity/Assets/NatureFX` | `kino/mods/NatureFX/` |
| 独自エンジン `NFXNative.dll`（C++ / D3D12） | `native/NFXNative` | `kino/mods/NatureFX/`（ほかに `nvngx_dlss.dll`、`nvngx_dlssd.dll`、`DLSS-LICENSE.txt`、`D3D12/D3D12Core.dll`） |

### 1.1 役割分担

高度な描画は独自エンジンが担い、HDRP の利用は最小限にとどめる方針である。

| 担当 | 内容 |
|---|---|
| 独自エンジン（NFXNative） | Unity の D3D12 デバイスとキューを共有し、独自の BLAS / TLAS とトレース用シェーダーですべてのレイトレースを行う。Unity のレイトレーシング機能は使用しない。担当範囲は、表面、太陽の影、環境光、GI（ラディアンスキャッシュ）、ランプの影とランプマスク、光の筋、反射、ガラス、空（透過率、多重散乱、sky view、空のプローブ、キューブ）、雲と雲の影、霧（2D と 3D）、透明面用の太陽マップ、地面マップ、揺れる葉の BLAS 更新である。NRD と DLSS も本 DLL 内で Unity のキューに積む。 |
| C# のカスタムパス | 入力の準備（`NFX_NativeBridge.compute`）、トレースの要求（`NativeEngine`）、結果の合成（`NFX_RTShadow` / `NFX_RTReflect` / `NFX_Atmosphere`）、独自の後処理（`NFX_Post`）、DLSS / 独自 TAA（`Dlaa`）、雨、煙、パーティクル |
| HDRP に残る処理 | 深度、G バッファ、モーションベクトル（マテリアルのラスタライズ）、遅延ライティングのパス（独自照明の使用中はほぼ何も加算しない）、半透明（レイヤー 7 で HDRP の太陽とランプを受ける）、UI、最終パス |

### 1.2 1 フレームの処理順序

1. `BeforeRendering`: `AmbientFeedPass`（`OwnLighting.cs`）が独自の空のプローブを HDRP の `_AmbientProbeData` に設定する。
2. HDRP: 深度、G バッファ、遅延ライティングを処理する。HDRP の太陽、ランプ、空の光は 0 に設定している（2 章参照）。
3. `AfterOpaqueAndSky`: `RayTracingEffects` のグローバル CustomPassVolume で、次の順に実行する。
   1. `AtmospherePass`（トレース）: 空のテーブル、雲、雲の影、空のプローブ、3D の霧
   2. `RTShadowPass`: 表面をトレースし、太陽の影、環境光 / GI、ランプの影とマスクを求める。次に G バッファを再描画して太陽とランプの正確な光を計算する（`NFX_SunExact`、`RTExactSun`）。続いて NRD（SIGMA / REBLUR）を適用し、合成で太陽、ランプ、空の光を影付きで加算する。`RTFullRelight` では HDRP の画素を分解して再構成する。
   3. `RTReflectionPass`: 反射をトレースし、REBLUR_SPECULAR を経て合成する。粗い面には独自の空のプローブを使う。
   4. `AtmospherePass`（合成）: 空の画素、霧
4. HDRP: 半透明、パーティクルを描画する。
5. `BeforePostProcess`: `RTGlassPass`（独自のガラス）、車の雨（`CarRain`）、暗所の見え方（`LensEffects`）、写真モードを処理し、最後に `PostProcess`（優先度 −1000）を実行する。
   - `PostProcess` の処理順序は、DLSS（DLAA またはアップスケール）または独自 TAA、モーションブラーと被写界深度、測光（64×36）、ブルーム、レンズフレア、露出・ホワイトバランス・トーンカーブである。
6. HDRP の後処理: 独自の後処理の使用中は実質的に無効である。トーンマップ、ブルーム、色調整、フレア、ビネット等はフレーム設定で無効にし、TAA は使用しない。DLSS のアップスケール中は HDRP の最終パスのマテリアルを `NFX_UpscaledFinal` に差し替え、表示解像度の独自の画像を出力する。

### 1.3 独自エンジンの呼び出し

- C# から DLL へは `CommandBuffer.IssuePluginEvent(AndData)` の描画イベントで呼び出す。イベント番号は、1 キューのテスト、3 トレース（`TraceEventData`、種類は `kTraceKinds` = 22）、5 NRD SIGMA、6 NRD REBLUR 拡散（モード 2 はランプ）、7 REBLUR 鏡面、8 シェーダーの再読み込み、9 DLSS である。
- 受け渡すデータは、`NrdEventData` 328 バイト、`DlssEventData` 192 バイト（`static_assert` あり）、フレーム定数 `Frame` である。`Frame` は `NFXCommon.hlsli` で定義し、ローカルライトを末尾に置く。`NFX_MAX_LOCAL_LIGHTS` は 128（= `LocalLights.Max`）、`kConstantSlot` は 8448 である。C# 側の書き込み位置と必ず一致させる。
- トレースバッファの詰め方は、`NfxPack.hlsli` と `NFX_NativeBridge.compute` で同じ式を使う。片方だけを変更してはならない。
- 方式は `Settings.RTBackend` で選択する。1 は DXR 1.1（インラインのコンピュート、既定）、0 は DXR 1.0（`NFX_PIPELINE` で lib_6_6、状態オブジェクトとシェーダーテーブル）である。DXR 1.2（SM 6.9、SER / OMM）は、Agility SDK 1.619 の独立したデバイス上で太陽の影のみを試験している（`RTDxr12Sun`、既定オフ）。
- DLL が持つ機能は、`NFX_Native_Info` の `features:` 文字列（own-glass lamp-mask dlss dlss-rr sun-map ground-map ground-cover）で示す。C# 側は `NativeEngine.Supports(...)` でこれを確認し、古い DLL でも動作するようにする。
- GPU が停止した場合は、DLL と同じフォルダに `NFXNative-gpu-hang.txt`（ブレッドクラム）が出力される。NGX のログは `NFX_ngx.log` に出力される。

---

## 2. HDRP 機能の置き換え（OwnLighting と CameraVolumetrics）

独自エンジンが置き換えた HDRP の機能は、置き換え側が実際に動作している間に限り停止する。合成が 30 フレーム停止した場合は HDRP の機能に戻し、画面が暗くならないようにしている。

| HDRP の機能 | 停止する条件 | 実装箇所 |
|---|---|---|
| 空の光（indirect diffuse ×0）、空の映り込み（reflection ×0）、空の描画（種類 none、キューブを生成しない） | `OwnSkyLighting` が有効で、各合成が動作している | `OwnLighting.Ambient` / `Reflections` / `Sky`、`VolumeOverhaul`（IndirectLightingController） |
| 太陽の光 | `OwnSunLight`。太陽を、どの物体も属さないレンダリングレイヤー（1<<30）に移す | `OwnLighting.Sun`、`RTShadowPass.CheckOwnSun` |
| 点光源とスポットライトの光 | `OwnLampLight`（ランプマスクの動作中） | `OwnLighting.Lamps` |
| 半透明への照明 | `OwnTransparentLight`。HDRP の太陽とランプをレイヤー 7 に移し、全マテリアルが半透明であるレンダラーにのみビット 7 を付ける | `OwnLighting.UpdateRenderers` |
| 体積霧、体積雲、大気散乱、Fog | 独自の霧 / 空 | `NatureFXMod` の `HdrpFogOff` / `HdrpCloudsOff` / `HdrpScatterOff`、`MapFog` |
| SSR、反射プローブ、プレーナー反射、空の反射 | `OwnLighting.Reflections` | `HdrpSsrOff` ほか |
| SSAO、SSGI | `OwnLighting.Ambient` | `HdrpSsaoOff` / `HdrpSsgiOff` |
| コンタクトシャドウ | 影の合成が動作している | `HdrpContactOff` |
| シャドウマップ、シャドウマスク | 太陽とランプの両方が独自 | `HdrpShadowMapsOff` / `HdrpShadowmaskOff`、`RayTracingEffects`（影を持つほかの平行光源も対象） |
| ブルーム、スクリーン空間のフレア、データ駆動のフレア、色調整、トーンマップ、ビネット、色収差、粒状、歪み、Panini | 独自の後処理（`PostProcess.Active`） | `HdrpBloomOff` / `HdrpFlareOff` / `HdrpPostOff` |
| 被写界深度、モーションブラー | 独自の後処理で使用するとき | `PostProcess`（フレーム設定） |
| TAA | 独自の後処理の使用中は常時（DLSS または独自 TAA） | `Dlaa` |
| 自動露出 | 常時。NatureFX の露出を使う（HDRP の Exposure は Fixed で上書き） | `PostProcess` |
| ゲームが描画する影の板 | RT の太陽の影が動作している | `FakeShadows` |
| トラックの ReflectionProbe | 独自の空の使用中（ロード時に一度だけ旧来の空で撮影されているため） | `SceneProbes` |

- `CameraVolumetrics` は、カメラのフレーム設定のうち該当する 1 項目のみを上書きし、不要になった時点で元に戻す。終了時（`OnDestroy`）にはすべての項目を元に戻す。
- 太陽は `Units.PhysicalScale` 倍して現実の明るさに合わせている。CarX の太陽は約 π lux であり、ランプ、ヘッドライト、発光は HDRP の現実の単位で扱う。露出の EV は現実の EV である。
- 独自 BSDF（`OwnBrdf`、既定オフ、実験的機能）は、G バッファを再描画せず、表面トレースの色と HDRP の法線で太陽とランプの照明を計算する。トレース対象外の物体（遠景の森、草、デトリタス）はレイヤー 7 に移し、HDRP に照明させる（`OwnLighting.Untraced`）。

---

## 3. ビルドと動作確認

コマンドは `AGENT.md` の「ビルド」と README に記載している。主な注意点は次のとおりである。

- クラウドのセッションには dotnet、dxc、Unity がない。クラウドで行った変更はコンパイル未確認としてユーザーに引き渡し、3 つの成果物のビルドを依頼する。
- シェーダーバンドルのビルドログ `<Unity プロジェクト>/Logs/naturefx_build.log` では、必ず次の内容を確認する。
  ```bash
  grep -a "Shader error" $L | sort | uniq -c
  grep -a "lost keywords\|variant materials" $L
  ```
  シェーダーにコンパイルエラーがあると、そのシェーダーはエラー表示なしにバンドルから除外される。過去には、合成で `NFX_COAT_ROUGHNESS` が未定義だったために影の合成全体が消えた事例がある。`variant_054 (NatureFX/LitAccumulation) lost keywords: _NORMALMAP` は既知の警告である。
- フルビルドの所要時間は約 30 分である（変種の絞り込み後）。共通の hlsl（`NFX_Accumulation.hlsl` など）を変更すると、全バリアントの再コンパイルが必要になる。RT の合成シェーダーのみの変更であれば数分で済む。
- 変種の絞り込みは次の手順で行う。ゲームが `kino/config/NatureFX/game_hdrp/game_variant_profile.txt` を出力する（`Tools/VariantProfileDump.cs`）。このファイルを `unity/variants/` にコピーすると、`VariantProfileStripper` がこれを使用する（`unity/variants/README.md`）。製品ビルドの Unity は変種のログを出力しないため、Unity 側の記録は利用できない。
- 新しい .ksm は `_NFX_NoiseTex`（`ShaderGlobals.EnsureNoiseTexture`）を生成する。このテクスチャがないとバンドルのノイズが一定値になり、雪、濡れ、落ち葉の模様が表示されない。
- 再起動せずに確認する場合は、デバッグページの developer タブ > Reload shaders を使う（`reload/` の新しいバンドルと `NativeShaders/*.cso` を読み込む）。C#、C++、構造体の配置を変更した場合は再起動が必要である。
- 作業者はゲームの表示を直接確認できない。変更のたびに、確認する場所と内容（F8 のタブ、デバッグページの行、`kino/output.log` の行）を具体的にユーザーへ伝える。Unity のエラー（カーネルの次元の不一致、UAV のフラグなど）は `kino/output.log` ではなく Unity の `Player.log` に出力される。

---

## 4. 設定と既定値

`Settings.cs` は、`ISettings`（KSL が自動保存する）、`SettingsDefaults`（同じメンバーの既定値を保持する）、`ApplyFirstRun` と `Migrate` で構成される。

- 既定値を変更する場合は、必ず新しい移行処理を追加する（`if (d.ConfigVersion < N) { ...; d.ConfigVersion = N; }`）。`Migrate` はユーザーの設定と `Defaults` の両方に適用されるため、F8 の「既定値に戻す」（`UserPresets.ResetAll`）と右クリックのリセットも移行後の値に従う。ユーザーが変更した値を保持する必要がある場合は、元の既定値のままであることを条件に加える（移行 45 を参照）。
- 現在の ConfigVersion は 78 である。29 は欠番である。
- `RTOverlay` は現在使用していないが、設定ファイルとの互換性のために残している。計測オーバーレイはデバッグページへ移した。

### 4.1 主要な既定値（移行適用後）

| 分野 | 既定値 |
|---|---|
| 独自エンジン | `NativeSky` / `NativeFog` / `NativeLighting` オン、`OwnSkyLighting` / `OwnSunLight` / `OwnLampLight` / `OwnTransparentLight` オン、`OwnBrdf` オフ |
| RT | `RTBackend` 1（DXR 1.1、移行 75）、`RTDenoiser` 2（NRD、移行 77）、`RTTraceScale` 0（半解像度）。影、環境光、跳ね返り、GI（20 m）、反射（品質 Quality、粗さ 0.8、150 m）、光の筋、ランプ、ランプの影、ランプマスク、`RTExactSun`、`RTFullRelight`、透明面の影、揺れる葉の影はオン。`RTDxr12Sun` はオフ |
| ガラス・粒子 | `NativeGlass` / `NativeParticles` オン |
| 空の光 | `SkyLightStrength` 1、`SkyLightSaturation` 0.5（実測の空の青さ）、`MoonBrightness` 0.1（現実の満月に近い値） |
| 後処理 | `NativePost` オン、`PostBloom` 0.02、トーンは `TonemapMode` 1（PBR Neutral、移行 70）、コントラスト・彩度 0、`WhiteBalance` 0.6 |
| 露出 | `ExposureMode` 1（目の順応）、`AutoExposureMin` 2 EV / `Max` 16 EV、`NightDarkness` 1、`FixedExposure` 14 EV（Fixed 選択時、移行 76） |
| DLSS | `Dlaa` オン、`DlssQuality` 0（DLAA）、`DlssRayReconstruction` オフ |
| レンズ | `MotionBlur` 0.5（180°）、`DofFStop` 2.8、`DepthOfField` オフ、`LensFlare` / `LensStreaks` 0.3、`NightVision` 0.8 |
| 時刻 | `SunOverride` オフ、`TimeOfDay` 14、`TimePasses` オフ（速さ 60 倍）、`SyncGameNight` オン |
| 車 | `HeadlightType` 3（LED）、`HeadlightStandard` 0（日本）、`HeadlightAim` 0.6°、`OilFilm` 0（移行 74。設定項目は存続）、タイヤスモーク 1、赤熱・汚れ・クロームのホイール オン |
| シェーダーリビルド | `ReplaceShaders` / `FullReplacement` / `RebuildFoliage` オン、`AntiTiling` 1（マップが `antitiling=` を指定した素材のみ） |
| マップ | `UseMapProfile` / `MapEffects` / `MapProbes` / `MapGroundDetail` オン、草 1・60 m、藪 1・25 m、デトリタス 1 |
| 開発 | `DebugPage` オン（移行 78、`127.0.0.1:8765`）、`WriteMaterialInventory` オフ、`TextureStreamingBudget` 0（ゲームの値） |

### 4.2 F8 とデバッグページ

- F8 のタブ構成は README の表に記載している（`NatureFXWindow.cs`）。Developer タブには、デバッグページのスイッチと状態表示のみを置く。
- 開発用のスイッチと機能は `Tools/DevControls.cs`（デバッグページの developer タブ）に集約している。内容は次のとおりである。
  - RT の確認用表示（影のみ、正確な太陽の光、環境光 / GI のみ、履歴のリセット、What is this?）
  - Reload shaders
  - NRD の各設定（保存しない）
  - DLSS のテスト（ジッターの符号・動き・量、RR とアップスケールの併用、HDRP の TAAU 経路）
  - テクスチャ予算、全 mip の読み込み
  - 比較機能（GI のランプ基準、3D 霧のタイルごとのライトリスト、煙の最大サイズ）
  - シェーダーのダンプ、ルールの再読み込み、変種の記録、マテリアル一覧
  - 計測のスイッチ（CPU、NVML）
- デバッグページ（`Tools/DebugPage.cs`、`DebugPageHtml.cs`）は TcpListener をループバックのみで開く。スナップショットはページを開いている間に限り、メインスレッドで 1 秒ごとに取得する。CPU と GPU の計測は別スレッドで行う（`CpuStats`、`GpuStats`）。NVML によるクロック、電力、温度の取得は、ドライバーによって GPU が停止するため既定でオフにしている。DLSS（NGX）が確保するメモリは VRAM の内訳に含まれない。

---

## 5. 既知の問題と未確認の項目

### 5.1 ゲーム上で未確認の項目

最終コミット `efc4012` までの次の変更は、ユーザーによるゲーム上での確認記録がない。次回ゲームを起動した際に確認を依頼する。これより前の未確認の項目（DLSS 切り替え時のクラッシュ対策、長時間動作とマップ切り替え、マップ拡張の表示など）は、[ROADMAP.md](ROADMAP.md) の「ゲーム上で未確認の項目」に記載している。

| 項目 | 確認内容 | 主な実装箇所 |
|---|---|---|
| 独自の後処理の使用中に HDRP の後処理、TAA、Fog を使用しない | 昼夜と雨で画面が崩れないこと。HDRP のフレアやビネットが重ならないこと | `NatureFXMod`（`HdrpPostOff`）、`PostProcess` |
| 独自 TAA | DLSS をオフにしたときの輪郭とゴースト | `PostProcess`（Taa）、`Dlaa.OwnTaa` |
| DLSS アップスケールの独自経路 | Quality〜Ultra perf. での UI、解像度、ずれ。旧経路はデバッグページの「Upscaling through HDRP's TAAU pass」で確認する | `Dlaa`、`NFX_UpscaledFinal.shader` |
| 露出 Fixed（14 EV）と目の順応、暗い場面 | F8 Exposure & tone の Current exposure。夜の街灯の下 | `PostProcess`、`Settings.FixedExposure` |
| トーンと色（PBR Neutral、ホワイトバランス、空の光の色 0.5） | 夕方やランプの下での色かぶり、日陰の青さ | `PostProcess`、`NativeEngine.SkyLightSaturation` |
| 雨（空気、雨粒、路面、車の 4 段階） | 雨天時の視程、雨粒の影と光、路面が濡れていく過程と水膜・波紋、車の水滴と水しぶき | `MapFog`、`Precipitation`、`AccumulationController`、`CarRain`、`TireSmoke` |
| 粒子の太陽の影（透明面用の太陽マップ） | 日陰にある雨粒と煙が暗くなること | `NFX_Particle.shader`、`SunShadowMap` |
| 3D の霧の空の光（空が見える割合に応じる）、GI の空 0.15 | 屋根の下や室内に白いもやが出ないこと | `FogVolume.hlsl`、`GI.hlsl` |
| ヘッドライトのカットオフの色のにじみ、欧米の配光 | 夜の路面上のカットオフ | `HeadlightController` |
| 既定値の移行 75〜78 | 既定値に戻したときに DXR 1.1、NRD、デバッグページがオンになること | `Settings.cs` |

### 5.2 未解決の問題と制限事項

- 33 ms のフレームが散発的に発生する。原因は未調査である。デバッグページの performance > slow frames で該当区間を確認する。
- DLSS Ray Reconstruction は、アップスケールと併用すると画面の揺れ、光のノイズ、GPU ハング（`DXGI_ERROR_DEVICE_HUNG`）が発生したため、DLAA のときのみ使用する。RR の対象は太陽の影のみである。反射は RR では粗い面に白い粒が出たため、REBLUR を使う。
- 独自 BSDF は、表面トレースの色にアンチタイリング、積もり、地面へのなじませ、ペイント、ディテールが反映されないため、模様が二重に見える。実験的機能の扱いを継続する。
- ランプの影は 1 画素あたり負荷の大きい 4 灯までであり、5 灯目以降は影を落とさない。ランプマスクは半解像度である。
- 合成後に HDRP が描画する半透明について、HDRP が透明パスの前に `_AmbientProbeData` を再設定する場合、空の光は黒のままになる。この点は未確認である。
- 独自ガラスは手前の 1 枚と奥の 2 枚までを扱う。HDRP がガラスに付ける効果は失われる。車の雨粒は `CarRain` が描画するため残る。
- 雨について次の項目は未対応である。塗装自体の濡れ（ゲームの Shader Graph）、屋根の下の乾燥、雲から降る雨の筋。
- HDRP の固定確保領域（クッキーのアトラス 85 MB、カスケード影 36 MB、プローブのアトラス）は実行中に縮小できない。縮小にはパイプラインアセットの再生成が必要であり、危険を伴うため見送っている。
- テクスチャのストリーミング予算はほとんど効果がない。ストリーミング対象は 70 MB 前後であり、残りはストリーミングされない。
- その他の残作業（シェル法の苔・草、レビュー指摘の未修正分、車のシェーダーリビルド、性能、DXR 1.2 側など）は [ROADMAP.md](ROADMAP.md) に記載している。

---

## 6. 注意点（現時点で有効なもの）

### 6.1 HDRP / Unity

- `VolumeManager.instance.stack` は既定値のみを保持する。カメラごとの値は `HDCamera.GetOrCreate(cam).volumeStack` を経由し、`VolumeOverhaul.Stack()` で取得する。
- HDRP は `beginCameraRendering` より前にカメラの行列と動的解像度を確定する。ジッターは `beginContextRendering` / `endContextRendering` で設定する（`Dlaa`）。
- ライトの `affectDiffuse` と `affectSpecular` を両方オフにすると、HDRP はそのライトを一覧から除外し、正確な太陽のパスが太陽を参照できなくなる。このため太陽は、どの物体も属さないレンダリングレイヤーに移している（`OwnLighting`）。HDRP のライトレイヤーが無効なアセットではレイヤーを参照できないため、`RTShadowPass.CheckOwnSun` が太陽の消失を監視し、消失した場合はそのセッションの間 HDRP の太陽に戻す。
- HDRP の自動露出は、独自の後処理の画像には使用しない。トーンマップ済みの画像を測光して下限まで露出を上げ、画面が真っ白になった事例がある。露出は常に NatureFX が測光する。
- カスタムパスの時点では、HDRP の G バッファは別用途に再利用されており、内容が残っていない。このため正確な光のパスでは、不透明物の GBuffer パスを再描画する（デカールのバッファも中立のものを使う）。
- トラックの ReflectionProbe は、ロード時に一度だけその時点の空で撮影される。過去には夜に昼の空が映り込んだ。このため独自の空の使用中は無効にしている（`SceneProbes`）。
- CarX の HDRP のコンピュートシェーダー、GPU カリング、GPU タイヤスモークには DX12 用のカーネルがない。これらは `Dx12Fix`、`GpuCullingDx12`、`TireSmokeDx12` で補っている。
- コンピュートシェーダーで書き込むテクスチャには `enableRandomWrite` が必要である。指定がないと書き込みがエラーなしに破棄され、過去には DLSS の結果が一度も画面に出なかった。`SharedColor` には指定済みである。
- `TEXTURE2D_X`（配列）の引数に `Texture2D.blackTexture` を渡すと、次元の不一致によりカーネル全体の実行が省略される。このエラーは `Player.log` に出力される。
- RTHandle の余白には古い内容が残る。後処理での読み取りは、画像の内側 1 テクセルまでに制限している（画面端のにじみ対策）。
- ゲームの起動直後（最初のシーンの読み込み中）に DLSS を開始すると、そのセッションの間、メニューの UI やモデルがちらつく。このため `Dlaa` は、シーンを一定時間トレースした後（RT を使わない場合は起動 1 分後）に最初の DLSS を開始する。
- ゲームは夜の照明を追加シーンで切り替える。NatureFX 側でも切り替えると両者が競合し、読み込みが繰り返された。このため切り替えの間隔を 8 秒以上空けている（`GameDayNight`）。
- ゲームはヘッドライトの値（クッキー、強さ、向き、色温度）を書き戻すことがある。`HeadlightController.Holds` が毎フレーム値を確認し、再設定している。
- `MaterialInventory.Write` はすべてのテクスチャを読み戻して PNG に保存するため、数百 MB の VRAM を一時的に消費し、フレームの引っかかりが生じる。このため、指定された場合（`WriteMaterialInventory`）にのみ実行する。
- `NFX_AntiTiling.hlsl` は `__SAMPLEUVMAPPING_HLSL__` を先に定義して、HDRP の SampleUVMapping を差し替える。`SURFACE_GRADIENT` はこのファイルより後で定義されるため、これに依存するフィールドを置いてはならない。
- アンチタイリングは素材ごとに明示的に有効化する方式である。白線入りの道路や 1 枚絵のスキャン素材は、ずらすと表示が崩れるためである。

### 6.2 独自エンジン

- 合成で光を差し引く場合は、色チャンネルごとにクランプせず、明るさの比率で処理する。ランプの影が運ぶ NatureFX 固有の光は負の値であるため、アップサンプルや合成で 0 に切り捨ててはならない。
- 作業用メモリの確保と解放は描画スレッドで行わない。`WaitRenderIdle` の後にのみ行う。BLAS は退役させてから解放する。
- 揺れる葉の BLAS 更新は、1 フレームあたり `kWindBudget`（64）本までを順番に処理する（`Wind.inl`）。
- `windows.h` は `small` を `char` として定義しているため、変数名に使用しない。
- ペイロード、アルベドの α の詰め方、クリアコートの判定は、`AGENT.md` の「実装上の注意」に記載している。

---

## 7. ファイル構成

| 分野 | 主なファイル |
|---|---|
| 入口・毎フレームの処理 | `src/NatureFX/NatureFXMod.cs`（HDRP の機能の切り替えも含む）、`Settings.cs`、`UserPresets.cs` |
| UI | `NatureFXWindow.cs`（F8）、`SettingsUI.cs`（KSL メニュー）、`Ui/` |
| 独自エンジンとの連携 | `Rendering/NativeEngine.cs`、`RayTracingScene.cs`（シーンの収集）、`RayTracingEffects.cs`（パスの構成）、`EngineLighting.cs`、`LocalLights.cs`、`Capabilities.cs` |
| RT のパス | `Rendering/RTShadowPass.cs`、`RTReflectionPass.cs`、`RTGlassPass.cs`、`RTSurfaceTrace.cs`、`SunShadowMap.cs`、`GroundMap.cs`、`HistoryGuard.cs`、`SharedColor.cs` |
| HDRP 機能の置き換え | `Rendering/OwnLighting.cs`、`CameraVolumetrics.cs`、`VolumeOverhaul.cs`、`OwnGlass.cs`、`SceneProbes.cs`、`FakeShadows.cs`、`Units.cs` |
| 空・霧・雲・時刻 | `Rendering/AtmospherePass.cs`、`CloudController.cs`、`SunController.cs`、`GameDayNight.cs`、`Map/MapFog.cs`、`Map/MapNight.cs` |
| 後処理・露出・AA | `Rendering/PostProcess.cs`、`Dlaa.cs`、`ExposureProbe.cs`、`LensEffects.cs`、`PhotoMode.cs` |
| 天候・自然 | `Nature/WeatherController.cs`、`AccumulationController.cs`、`WindController.cs`、`TrackMap.cs`、`Rendering/Precipitation.cs`、`Map/Seasons.cs` |
| 車 | `Rendering/HeadlightController.cs`、`TireSmoke.cs`、`CarRain.cs`、`CarGlow.cs`、`CarFinish.cs`、`GameParticles.cs`、`ParticleLighting.cs` |
| シェーダーリビルド | `Nature/MaterialReplacer.cs`、`ShaderBundle.cs`、`Rebuild/`、`Map/MaterialProfiles.cs` |
| マップ拡張 | `Map/`、`Nature/GrassRenderer.cs`、`ForestRenderer.cs`、`ShrubRenderer.cs`（書式は [map-authoring.md](map-authoring.md)） |
| 開発ツール | `Tools/DebugPage.cs`、`DebugPageHtml.cs`、`DevControls.cs`、`CpuTimings.cs`、`CpuStats.cs`、`GpuStats.cs`、`SceneShaderDumper.cs`、`VariantProfileDump.cs` |
| 独自エンジン | `native/NFXNative/src/NFXNative.cpp`、`Nrd.inl`、`Dlss.inl`、`Wind.inl`、`RtDevice.inl`（DXR 1.2）、`shaders/`（`NFXCommon.hlsli`、`NfxPack.hlsli`、トレースごとの .hlsl） |
| シェーダーバンドル | `unity/Assets/NatureFX/Shaders/`（合成、後処理、パーティクル、連携）、`ShaderLibrary/`（Lit に組み込む hlsl）、`Editor/`（生成、ビルド、変種の削減） |
| ビルド | `tools/build_bundle.sh`、`tools/build_native.ps1`、`tools/build_dxr12_probe.ps1`、`unity/variants/` |
| テストマップ | `tools/kino_sdk/`（`NfxDataTestMap.cs`、`NfxTestBays.cs`、素材の取り込み） |

### 7.1 リポジトリ外の場所（ユーザーの PC）

| 対象 | 場所 |
|---|---|
| リポジトリ | `C:\Users\kppn1223\Documents\carx\mods\NatureFX` |
| シェーダーバンドル用の Unity プロジェクト | `C:\Users\kppn1223\NatureFX-Unity`（ログは `Logs\naturefx_build.log`） |
| Kino Content SDK（テストマップ） | `C:/Users/kppn1223/Desktop/kino_content_sdk` |
| ゲーム | `C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online`（ログは `kino/output.log`） |
| 外部の依存物・素材 | `%USERPROFILE%\NatureFX-Deps`（NRD、DLSS、Agility SDK 1.619、dxc 1.9、Poly Haven の素材） |
