# ロードマップ（未完了の項目）

2026-11-02 時点の未完了の項目をまとめる。
本資料は旧資料 6 本（`map-extension-plan.md`、`native-atmosphere-lighting-plan.md`、`shader-rebuild-plan.md`、`plan-review-followups.md`、`review-2026-11-01.md`、`review-2026-11-02.md`）を統合したものである。コードで状況を確認し、完了済みの項目と中止した項目は除外した。経緯は `git log -- docs/<旧ファイル名>` で参照できる。
括弧内の記号（B12、P5 など）は旧レビューの指摘番号である。

各項目は「課題」「残作業」「関連ファイル」で記載する。

---

## 1. 描画・独自エンジン

### 1.1 HDRP のアトラスのメモリ

- 課題：クッキーアトラス、反射プローブのアトラス、シャドウアトラスは HDRP のパイプラインアセットで設定されている。実行中に縮小するにはパイプラインの再生成が必要であり、ゲーム全体の描画を作り直すため危険を伴う。独自 BSDF のランプも、HDRP の `EvaluateLight_Punctual` を経由してクッキーを使用している。
- 残作業：対応方針の検討。
- 関連ファイル：`Rendering/OwnLighting.cs`、`Rendering/VolumeOverhaul.cs`、`Rendering/Capabilities.cs`

### 1.2 HDRP の不要な処理

- 課題：独自エンジンが照明を上書きしても、HDRP の deferred lighting とライトリストの構築は毎フレーム実行されている。G バッファのコピーと exact パスも残っている。
- 残作業：フレーム設定で停止できるかの調査。
- 関連ファイル：`Rendering/OwnLighting.cs`、`Rendering/CameraVolumetrics.cs`、`Shaders/NFX_SunExact.shader`

### 1.3 独自 BSDF（実験的機能）

- 課題：表面トレースの色に NatureFX の材質処理（アンチタイリング、積もり・濡れ、地面へのなじませ、ペイント、ディテール）が反映されず、路面の模様が二重になる。トレース対象外の物体（森、草、Detritus の板、除外した小物）は灰色 0.3 になる。
- 残作業：独自エンジンのヒットシェーダーにも hex tiling を組み込む。これにより反射と GI の色も正しくなる。
- 関連ファイル：`Shaders/NFX_LampsOwn.shader`、`ShaderLibrary/NFX_LampsExact.hlsl`、`ShaderLibrary/NFX_AntiTiling.hlsl`、`native/NFXNative/shaders/NFXCommon.hlsli`・`Surface.hlsl`

### 1.4 DXR 1.2 側（試験機能 `RTDxr12Sun`）

- 課題：次の 4 点が残っている。
  - `RtAllocate` のチャンクは追記のみで解放されない。
  - BLAS と OMM はコミット済みリソースのままである。
  - 揺れる葉の更新に予算（`kWindBudget`）がない（P6）。
  - `TraceRt` のディスパッチ後に UAV バリアがない（B41）。
- 残作業：上記 4 点の修正。
- 関連ファイル：`native/NFXNative/src/RtDevice.inl`

### 1.5 ランプの影と 3D 霧のランプ

- 課題：ランプの影は 1 画素あたり 4 灯までである。3D 霧に含めるランプは、範囲球がカメラから 60 m 以内にあるものに限られるため、遠方の街灯のにじみが表示されない。
- 残作業：霧のタイル × 帯のリストが実装済みであるため、この条件を広げる。
- 関連ファイル：`Rendering/LocalLights.cs`、`native/NFXNative/shaders/FogVolume.hlsl`

---

## 2. 空・霧・天候（雨を含む）

### 2.1 車の塗装自体の濡れ

- 課題：塗装はゲームの Shader Graph（`CarPaint*`）であり、内容を変更できない。現状は `CarRain` の水滴を上に描画しているのみである。
- 残作業：塗装を置き換える（4.1 の車のシェーダー）か、上描きで濡れによる暗さとつやを加えるかを決める。
- 関連ファイル：`Rendering/CarRain.cs`、`Shaders/NFX_CarRain.shader`

### 2.2 屋根・トンネルの下が雨で濡れる問題

- 課題：濡れの処理は場所を考慮していない。
- 残作業：真上から見た雨の当たる範囲の地図（遮蔽マップ）を作成する。`GroundMap.hlsl`（真下へのレイ）や `SunMap.hlsl`（太陽方向の格子）と同じ構成で、新しいトレースの種類として実装できる。
- 関連ファイル：`Nature/AccumulationController.cs`、`ShaderLibrary/NFX_Accumulation.hlsl`、`Rendering/Precipitation.cs`

### 2.3 雲から降る雨の筋

- 課題：雲の種類マップの雨のチャンネル（`native/NFXNative/shaders/CloudNoise.hlsl` の map.w。積乱雲と厚い高層雲）が使用されていない。
- 残作業：このチャンネルを使い、雲の下の雨の筋を 3D 霧または空のレイマーチで描画する。
- 関連ファイル：`Clouds.hlsl`、`CloudsCommon.hlsli`、`FogVolume.hlsl`

### 2.4 天候の変化時間

- 課題：`CloudController.WeatherChangeTime` の扱い（R20、小規模）。
- 残作業：扱いの見直し。
- 関連ファイル：`Rendering/CloudController.cs`

---

## 3. 後処理

### 3.1 アップスケール中のフレームの不整合

- 課題：アップスケール中に PostPass が実行されなかったフレームでは、前フレームの画像が表示される。主カメラが見つからないフレームでは DRS とジッターが残る（R17）。
- 残作業：DLSS の経路を作り直した（`973a25f`）ため、まず現在の実装で発生するかを確認する。
- 関連ファイル：`Rendering/Dlaa.cs`、`Rendering/PostProcess.cs`、`Shaders/NFX_UpscaledFinal.shader`

### 3.2 後処理の負荷と色変換

- 課題：レンズフレアのゴーストの明るさを画素ごとに計算している。被写界深度とモーションブラーは表示解像度で 48 / 16 タップである（P12）。
- 残作業：上記の負荷の削減と、AgX の Rec.2020 変換の見直し（R20）。
- 関連ファイル：`Shaders/NFX_Post.shader`

---

## 4. 材質・シェーダーリビルド

### 4.1 車のシェーダー（段階 2、保留）

- 課題：11-01 のユーザー判断によりスキップした。対象は `CarPaint`/`CarPaintRim`/`CarPaintStitches`/`CarGeneric`/`CarGlass`/`CarTireCustomization`/`CarLights`/`mirror_shader` の翻訳器と NFX シェーダーである。最も工数を要するのは、塗装の `_Spectrum`、ステッカー、塗装エディターの全組み合わせの再現である。
- 残作業：保留中。再開時に上記を実装する。
- 関連ファイル：`Rebuild/GenericTranslator.cs`、`Nature/MaterialReplacer.cs`

### 4.2 仕上げ（段階 5）

- 課題：DX11 対応の継続可否が未決定である（RT は DX12 専用）。
- 残作業：DX11 の継続可否の判断、変種の削減、主要マップでの fps の比較。
- 関連ファイル：`Editor/BuildNatureFXBundle.cs`、`Rendering/GraphicsApiSwitcher.cs`

### 4.3 シェル法による苔・短い草

- 課題：ユーザー承認済みであるが、未着手である。
- 残作業：地面メッシュを法線方向に重ねて描画する `NatureFX/Shell` 変種と、`[shells]` セクションの実装。
- 関連ファイル：`Editor/GenerateNatureFXShaders.cs`、`Map/MaterialProfiles.cs`

### 4.4 バンドルの変種で `_NORMALMAP` が落ちる問題

- 課題：`_NORMALMAP` のみの変種で `_NormalMapOS` を与えていないため、`_NORMALMAP` が落ちる（既知の `lost _NORMALMAP` 警告、B31）。
- 残作業：修正。
- 関連ファイル：`Editor/BuildNatureFXBundle.cs` `ApplyKeywordProperties`

### 4.5 両面材質の裏面の法線

- 課題：両面材質の裏面で `geomNormalWS` を反転していないため、葉の裏に雪、埃、水たまりが表示される（B32）。
- 残作業：修正。
- 関連ファイル：`ShaderLibrary/NFX_LitData.hlsl`

### 4.6 共通 hlsl の軽量化（フルビルドが必要）

- 課題：次の 3 点である。
  - 天候がすべて 0 でも、毎画素 fbm ×2 とトラックマップを処理している（P11）。
  - 地面マップを 1 画素あたり 3 回読み込んでいる（11-02 P13）。
  - `[loop]` 内で暗黙勾配の `SAMPLE_TEXTURE2D_ARRAY` を使用している（P20）。
- 残作業：早期 return の追加と `NFX_SnowBank` の `snow > 0` による囲い込み、地面マップの読み込み回数の削減、`SAMPLE_TEXTURE2D_ARRAY` の `_GRAD` への置き換え。
- 関連ファイル：`NFX_Accumulation.hlsl`、`NFX_Ground.hlsl`、`NFX_MaterialProfile.hlsl`

---

## 5. マップ拡張

### 5.1 未決定の仕様

- 課題：次の仕様が決まっていない。
  - マップ外の `.nfx` を丸ごと置き換えるか、セクションごとに上書きするか。
  - 無効にした Light 等と、プリセット名のみの空オブジェクトの値の優先順位。
  - F8 で変更した値の記録をゲーム再起動後も保持するか。
  - 書式の版（`[map] format = 1`）を導入するか。
- 残作業：仕様の決定と実装。
- 関連ファイル：`Map/MapProfile.cs`、`Map/MapDefaults.cs`、`Map/MarkerScanner.cs`

### 5.2 静的バッチ時のオブジェクト単位の区別

- 課題：静的バッチではオブジェクト単位の区別（窓ごとの点灯、木ごとの色）ができない。現状は 6 m 区画で代用している。
- 残作業：頂点カラーまたは UV2 にオブジェクト番号を焼き込む案を検討する。マップ作成側のスクリプトでの対応が必要である。
- 関連ファイル：`Map/MaterialProfiles.cs`、`ShaderLibrary/NFX_MaterialProfile.hlsl`

### 5.3 `[materials]` の解析

- 課題：`[materials]` でプリセット名が key=value の後にあると、前のキーが破棄される（B36）。
- 残作業：修正。
- 関連ファイル：`Map/MaterialProfiles.cs` `ParseLook`

### 5.4 `MapLights.Clear` の復元漏れ

- 課題：`MapLights.Clear` が作者の Light の `shadows` と dimmer を元に戻さない（B35）。
- 残作業：修正。
- 関連ファイル：`Map/MapLights.cs`

---

## 6. 性能

### 6.1 シーン構築時のロック

- 課題：`BuildScene` が GPU 待ち（最大 10 秒）と初回の PSO・RT ステートオブジェクト作成の間 `g_sceneMutex` を保持し、描画スレッドが停止する（P4、11-02 P10）。
- 残作業：BLAS の構築をロックの外で行い、差し替えのみをロック内で行う。
- 関連ファイル：`native/NFXNative/src/NFXNative.cpp`

### 6.2 `CheckScene` のテストトレース

- 課題：ジオメトリが 1 つ増えるたびに、`CheckScene` が全インスタンスに対してテストトレースを行い、同期待ちが発生する（P3）。
- 残作業：`clear` の場合または開発フラグの指定時のみに限定する。
- 関連ファイル：`Rendering/NativeEngine.cs`

### 6.3 TLAS の毎フレームの再構築

- 課題：何も動かないフレームでも TLAS を毎フレーム再構築している（P5）。
- 残作業：`UpdateTransforms` の dirty と `g_windActive` を参照して判定する。
- 関連ファイル：`NFXNative.cpp` `RecordRenderTlas`

### 6.4 CPU の定期走査

- 課題：次の 3 点である。
  - `RayTracingScene.Sync` が毎秒、全 MeshRenderer に対して既知判定の前に `gameObject.name` と `GetComponent` を実行している（P7）。
  - `MaterialReplacer` の 10 秒ごとの再分類にキャッシュがない（P8）。
  - `CarFinish`（1 秒ごと）と `FakeShadows`（2 秒ごと）がシーン全体に対して `FindObjectsByType` を実行している（P9）。
- 残作業：上記の走査の削減。

### 6.5 星空の生成によるヒッチ

- 課題：`MapNight.Stars()` が最初の夜に 6 × 1024² の `Color[]` を同期処理で生成し、数百 ms のヒッチが発生する（P10）。
- 残作業：生成処理の改善。
- 関連ファイル：`Map/MapNight.cs`

### 6.6 タイヤスモーク

- 課題：CPU の並列化と最大サイズ 0.8 への変更は完了している。残る負荷要因は、粒子数（1 回 8 個、最大 12000）、描画解像度、シェーダーのランプ 8 灯のループ、3D テクスチャの参照回数である。
- 残作業：粒子数の削減、半解像度での描画、ランプのループの 4 灯への削減、3D テクスチャの参照の 1 回への削減。表示が変わるため、ユーザーと相談のうえ進める。レイキャストの非同期化は、粒子の添字が入れ替わるため見送る。
- 関連ファイル：`Rendering/TireSmoke.cs`、`Shaders/NFX_Particle.shader`

### 6.7 画面コピー

- 課題：共有バッファ化は完了しているが、コピー回数はさらに削減できる。
- 残作業：影の合成と反射の合成を 1 つのパスに統合し、コピーを 1 回減らす。
- 関連ファイル：`Rendering/SharedColor.cs`、`RTShadowPass.cs`、`RTReflectionPass.cs`

### 6.8 雲

- 課題：雲の描画負荷（11-02 P11）。
- 残作業：太陽方向のマーチの早期打ち切り。雲の影は 256² または隔フレームの更新で十分である。
- 関連ファイル：`native/NFXNative/shaders/Clouds.hlsl`、`CloudShadow.hlsl`

### 6.9 小規模な無駄の解消

- 課題：次の項目である。
  - `UpdateWind` の配列確保と、Wind オフ時の `SetWind`（P13）
  - ランプトレースの未使用の Surface バッファ（P14）
  - `PrepareNrdGuide` での同サイズの 2 回の処理（P15）
  - `HdrpBindings` の `MethodInfo.Invoke`（P16）
  - `MapProfile.Get` の毎フレームの行走査（P17）
  - 毎フレームの `Status` 文字列、`GetGlobalVector` の文字列参照、`new GUIStyle` 等（P18、11-02 P14）
  - 草がオフでもセルメッシュを保持している（P19）
- 残作業：まとめて修正する。

---

## 7. コード品質・不具合（レビュー指摘の未修正分）

### 7.1 `g_geometries` の競合

- 課題：`AddGeometry*` / `SetInstances` がロックなしで `g_geometries` に `push_back` し、描画スレッドが同じ vector を参照している。再割り当ての瞬間に use-after-free が発生する（B23 / C12）。
- 残作業：`g_sceneMutex` を取るとヒッチが増えるため、構築の分割（6.1）と合わせて対応する。
- 関連ファイル：`native/NFXNative/src/NFXNative.cpp`

### 7.2 `Hash22` の精度

- 課題：`Hash22` が `frame * 4 + i` を float で加算している。長時間の動作で乱数が量子化し、影、AO、反射のノイズが縞状になる（B20）。
- 残作業：`frame & 1023u` または整数ハッシュに変更する。
- 関連ファイル：`NFXCommon.hlsli` と `NFX_NativeBridge.compute` の両方

### 7.3 NRD に渡す行列の規約

- 課題：NRD に渡す行列（Unity の右手系、GL 式射影、viewZ 正）が NRD の規約に合っているか検証していない（B21）。
- 残作業：`enableValidation` のオーバーレイで確認する。
- 関連ファイル：`Rendering/NativeEngine.cs`、`native/NFXNative/src/Nrd.inl`

### 7.4 NRD のリセットと待機

- 課題：`NativeEngine.NrdReset` を書き込むのは影のパスのみである。影、AO、光芒をオフにして反射と NRD を使う場合、カメラカットでゴーストが発生する（B30）。また `NrdWrapWait` は 2 秒で待機を打ち切り、リングを上書きする。後者の実害はほとんどない（C13）。
- 残作業：修正。

### 7.5 植生のモーションベクトル

- 課題：揺れる草と森の板に `MotionVectorGenerationMode.Object` が設定されておらず、DLSS / TAA で揺れる植生にゴーストが発生する（B24）。低木は Object にすると画面が流れたため、Camera のままとしている。
- 残作業：草と森の板への設定。
- 関連ファイル：`Nature/GrassRenderer.cs`、`Nature/ForestRenderer.cs`

### 7.6 `Glass.hlsl` の屈折の合成

- 課題：外側の `bool own`（`NFX_FLAG_OWN_GLASS`）を、画面バッファの値が隠していた名残がある（現在は `shownBits`）。
- 残作業：意図が `own` であれば `select(own, …)` に変更する。表示が変わるため、ゲーム上で比較してから行う。
- 関連ファイル：`Glass.hlsl`

### 7.7 `ApplyWetness` の適用範囲

- 課題：`AccumulationController.ApplyWetness` は、後から `NatureFX/LitAccumulation` に置き換わった材質にも暗化を適用する。既定オフの `WetGameMaterials` を有効にした場合にのみ発生する（B22）。
- 残作業：修正。

### 7.8 トラックマップのゴムの蓄積

- 課題：ゴムの蓄積がフレーム単位であるため、144 fps では 60 fps の 2.4 倍の速さで黒くなる（B33）。
- 残作業：修正。
- 関連ファイル：`Nature/TrackMap.cs`、`Shaders/NFX_TrackMap.compute`

### 7.9 `CarGlow` の減速度の算出

- 課題：減速度を描画フレームの `Rigidbody.velocity` の差で算出しているため、高 fps では軽いブレーキでもディスクが発光する（B34）。
- 残作業：修正。
- 関連ファイル：`Rendering/CarGlow.cs`

### 7.10 共有プリセットに含まれる設定

- 課題：共有プリセットに `RTBackend`、`RTDxr12Sun`、`RTOverlay`、`ShowMarkers`、`UseMapProfile` が含まれる（B37）。
- 残作業：`Excluded` への追加。
- 関連ファイル：`UserPresets.cs` の `Excluded`

### 7.11 `Widgets` の不具合

- 課題：次の 2 点である（B38）。
  - 右クリックのリセットが対象を探すため、副作用のあるセッターを試し呼びしている。
  - 検索とカードの開閉を同じイベントで反映するため、IMGUI の layout 例外が発生する。
- 残作業：修正。
- 関連ファイル：`Ui/Widgets.cs`

### 7.12 `SunController` の後始末

- 課題：月の GameObject（`DontDestroyOnLoad`）を破棄していない。また `Refresh` が `sunShadowsOff_` を元に戻さない（B40）。
- 残作業：修正。
- 関連ファイル：`Rendering/SunController.cs`

### 7.13 11-02 R20 の小規模な指摘

- 課題：次の項目である。
  - `NrdReblur` のモード切り替え
  - `DlssUnpack` の SRV/UAV の同時バインド
  - `CarRain`/`WindController` の `OverlapSphere` 256 本
  - `VolumeOverhaul.trackSky_` の表示
  - `MapDecals` の `seed`
  - インポスターの影の向き
  - `NFX_Ground` のペイント α の補間
  - `OwnGlass` のスロット範囲外
  - `BakeOmm` の Level < 2
- 残作業：まとめて修正する。

---

## 8. ゲーム上で未確認の項目

次の項目は、ユーザーによるゲーム上での確認が済んでいない。

1. DLSS の切り替えでクラッシュしないこと（11-02 X1〜X3）。Quality / Balanced / Ultra perf. の切り替えと、ウィンドウと 4K の往復を行い、`kino/output.log` の `GPU memory` が元に戻るかを確認する。あわせて、二重像、霧やランプの帯の遅れ、アップスケールの揺れ（R1〜R3）を確認する。
2. 長時間の動作とマップの切り替え。数時間走行しても煙、雨、ブレーキの発光が動作すること（C1）。マップを繰り返し切り替えても device removed と `DESCRIPTOR HEAP FULL` が発生せず、GPU memory と BLAS / data pool が増え続けないこと（C2、B6、B12、ClearScene の接続）。
3. 霧と天候の表示。
   - 霧のある公式トラックで、トラックの霧が残ること（B8、R4）。
   - 走行中に Native sky をオフにしても、雲の影や古い霧が残らないこと（R5）。
   - 夜の月の影がちらつかないこと（R7）。
   - 雲なしのセッションで霧の箱と煙が表示されること（R10）。
4. 曇りの天候で Sun override をオフにすると、太陽の影が戻ること（B4）。Tire marks をオフにして車を替えても、例外が発生しないこと（B5）。
5. 3D 霧のランプのリスト。Developer の「3D fog lamps」のオン/オフで表示が同じであり、20×12 の継ぎ目や 4.4 / 38.7 / 341 m の輪が出ないこと。この GPU では F8 の「fog volume」の ms が表示されないため、fps で比較する。
6. `MapNight.Stars()` の `Cubemap.SetPixels` が `RGB9e5Float` で動作すること（B39）。動作しない場合は最初の夜に例外が発生する。
7. マップ拡張の段階 1.5〜5（天候ゾーン、光源マーカー、葉の色、立体の草、デカール・水・局所の霧・舞う物体）の表示。NatureFX を無効にして同じマップを読み込んだとき、何も表示されないこと。負荷の上限（光源 64・影 8、プローブ 16、草 60 m、デカール 512）が十分であること。
