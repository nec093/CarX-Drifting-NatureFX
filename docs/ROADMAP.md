# ロードマップ（未完了の項目だけ）

2026-11-02 時点。旧資料 6 本（`map-extension-plan.md`、`native-atmosphere-lighting-plan.md`、`shader-rebuild-plan.md`、`plan-review-followups.md`、`review-2026-11-01.md`、`review-2026-11-02.md`）をまとめ、コードで確認して**終わったもの・取りやめたものは外した**。経緯は `git log -- docs/<旧ファイル名>` で読める。
括弧内の記号（B12、P5 など）は旧レビューの番号。

---

## 1. 描画・独自エンジン

- **HDRP のアトラスのメモリ**: クッキーアトラス・反射プローブのアトラス・シャドウアトラスは HDRP のパイプラインアセットの設定で、実行中に小さくするにはパイプラインの作り直しが要る（ゲーム全体の描画を作り直すので危険）。独自 BSDF のランプも HDRP の `EvaluateLight_Punctual` 経由でクッキーを使っている。`Rendering/OwnLighting.cs`、`Rendering/VolumeOverhaul.cs`、`Rendering/Capabilities.cs`
- **HDRP の空回りの処理**: 独自エンジンが照明を上書きしても、HDRP の deferred lighting とライトリストの構築は毎フレーム走っている（G バッファのコピーと exact パスも残る）。フレーム設定で止められるか調べる。`Rendering/OwnLighting.cs`、`Rendering/CameraVolumetrics.cs`、`Shaders/NFX_SunExact.shader`
- **Own BSDF は実験的のまま**: 表面トレースの色に NatureFX の材質処理（アンチタイリング、積もり・濡れ、地面のなじませ、ペイント、ディテール）が無く、路面の模様が二重になる。トレースしない物（森・草・Detritus の板、飛ばした小物）は灰 0.3。独自エンジンのヒットのシェーダーにも hex tiling を入れれば反射・GI の色も正しくなる。`Shaders/NFX_LampsOwn.shader`、`ShaderLibrary/NFX_LampsExact.hlsl`、`ShaderLibrary/NFX_AntiTiling.hlsl`、`native/NFXNative/shaders/NFXCommon.hlsli`・`Surface.hlsl`
- **DXR 1.2 側（試験機能 `RTDxr12Sun`）の残り**: `RtAllocate` のチャンクは追記のみで解放されない、BLAS・OMM はコミット済みリソースのまま、揺れる葉の更新に予算（`kWindBudget`）が無い（P6）、`TraceRt` のディスパッチ後に UAV バリアが無い（B41）。`native/NFXNative/src/RtDevice.inl`
- **ランプの影は 1 画素 4 灯まで**、3D 霧に入るランプは「範囲球がカメラから 60 m 以内」のものだけ（遠い街灯のにじみが出ない）。霧のタイル × 帯のリストがあるので、ゲートは広げられる。`Rendering/LocalLights.cs`、`native/NFXNative/shaders/FogVolume.hlsl`

## 2. 空・霧・天候（雨を含む）

- **車の塗装そのものの濡れ**: 塗装はゲームの Shader Graph（`CarPaint*`）で、中身は変えられない。今は `CarRain` の水滴を上に描くだけ。塗装を置き換えるか（5 章の車の段階）、上描きで濡れの暗さ・つやを足すか。`Rendering/CarRain.cs`、`Shaders/NFX_CarRain.shader`
- **屋根・トンネルの下が雨で濡れる**: 濡れは場所を見ていない。真上からの「雨の当たる地図」（遮蔽マップ）が要る。`GroundMap.hlsl`（真下へのレイ）や `SunMap.hlsl`（太陽方向の格子）と同じ作りで、新しいトレースの種類として作れる。`Nature/AccumulationController.cs`、`ShaderLibrary/NFX_Accumulation.hlsl`、`Rendering/Precipitation.cs`
- **雲からの雨の筋**: 雲の種類マップの雨のチャンネル（`native/NFXNative/shaders/CloudNoise.hlsl` の map.w、積乱雲と厚い高層雲）が未使用。雲の下の雨の筋（3D 霧か空のレイマーチで）に使う。`Clouds.hlsl`、`CloudsCommon.hlsli`、`FogVolume.hlsl`
- 天候の変化時間 `CloudController.WeatherChangeTime` の扱い（R20、小）。`Rendering/CloudController.cs`

## 3. ポストプロセス

- **アップスケール中に PostPass が走らなかったフレーム**は前フレームの絵が出る、主カメラが見つからないフレームの DRS・ジッターの残り（R17）。DLSS の経路を作り直した（`973a25f`）ので、まず今の形で起きるか確認。`Rendering/Dlaa.cs`、`Rendering/PostProcess.cs`、`Shaders/NFX_UpscaledFinal.shader`
- 負荷: レンズフレアのゴーストの明るさを画素ごとに計算、被写界深度・モーションブラーが表示解像度で 48 / 16 タップ（P12）。AgX の Rec.2020 変換の見直し（R20）。`Shaders/NFX_Post.shader`

## 4. 材質・シェーダーの作り直し

- **車のシェーダー（段階 2）は保留**（11-01 ユーザー判断でスキップ）: `CarPaint`/`CarPaintRim`/`CarPaintStitches`/`CarGeneric`/`CarGlass`/`CarTireCustomization`/`CarLights`/`mirror_shader` の翻訳器と NFX シェーダー。塗装の `_Spectrum`・ステッカー・塗装エディターの全組み合わせの再現が最大の手間。`Rebuild/GenericTranslator.cs`、`Nature/MaterialReplacer.cs`
- **仕上げ（段階 5）**: DX11 を続けるかの判断（RT は DX12 専用）、変種の削減、主要マップでの fps の比較。`Editor/BuildNatureFXBundle.cs`、`Rendering/GraphicsApiSwitcher.cs`
- **シェル法の苔・短い草**（ユーザー承認済み、未着手）: 地面メッシュを法線方向に重ねて描く `NatureFX/Shell` 変種と `[shells]` セクション。`Editor/GenerateNatureFXShaders.cs`、`Map/MaterialProfiles.cs`
- バンドルの変種: `_NORMALMAP` だけの変種で `_NormalMapOS` を与えず `_NORMALMAP` が落ちる（既知の `lost _NORMALMAP` 警告、B31）。`Editor/BuildNatureFXBundle.cs` `ApplyKeywordProperties`
- 両面材質の裏面で `geomNormalWS` を反転していない（葉の裏に雪・埃・水たまり、B32）。`ShaderLibrary/NFX_LitData.hlsl`
- 共通 hlsl の軽量化（フルビルドが要る）: 天候が全部 0 でも毎画素 fbm ×2 とトラックマップ（早期 return、`NFX_SnowBank` を `snow > 0` で囲む、P11）、地面マップを 1 画素 3 回読む（11-02 P13）、`[loop]` 内の暗黙勾配の `SAMPLE_TEXTURE2D_ARRAY` を `_GRAD` に（P20）。`NFX_Accumulation.hlsl`、`NFX_Ground.hlsl`、`NFX_MaterialProfile.hlsl`

## 5. マップ拡張

- **未決の仕様**: マップの外の `.nfx` は「丸ごと置き換え」か「セクションごとに上書き」か／無効にした Light 等とプリセット名だけの空オブジェクトの値の優先／F8 で動かした値の記録をゲーム再起動後も残すか／書式の版（`[map] format = 1`）を入れるか。`Map/MapProfile.cs`、`Map/MapDefaults.cs`、`Map/MarkerScanner.cs`
- **静的バッチでオブジェクト単位の区別ができない**（窓ごとの点灯、木ごとの色）。今は 6 m 区画で代用。頂点カラーか UV2 に「オブジェクト番号」を焼く案（map generator / SDK スクリプト側の対応が要る）。`Map/MaterialProfiles.cs`、`ShaderLibrary/NFX_MaterialProfile.hlsl`
- `[materials]` でプリセット名が key=value の後にあると前のキーを捨てる（B36）。`Map/MaterialProfiles.cs` `ParseLook`
- `MapLights.Clear` が作者の Light の `shadows`・dimmer を戻さない（B35）。`Map/MapLights.cs`

## 6. 性能

- **シーン構築**: `BuildScene` が GPU 待ち（最大 10 秒）と初回の PSO・RT ステートオブジェクト作成の間 `g_sceneMutex` を握り、描画スレッドが止まる（P4、11-02 P10）。BLAS 構築はロックの外で、差し替えだけロックに。`native/NFXNative/src/NFXNative.cpp`
- ジオメトリが 1 つ増えるたびに `CheckScene` が全インスタンスへテストレースして同期待ち（P3）。`clear` のときか開発フラグの下だけに。`Rendering/NativeEngine.cs`
- TLAS を何も動かないフレームでも毎フレーム作り直す（P5）。`UpdateTransforms` の dirty と `g_windActive` を見る。`NFXNative.cpp` `RecordRenderTlas`
- **CPU の定期走査**: `RayTracingScene.Sync` が毎秒全 MeshRenderer で `gameObject.name` と `GetComponent` を既知判定の前に実行（P7）、`MaterialReplacer` の 10 秒ごとの再分類にキャッシュが無い（P8）、`CarFinish`（1 秒）・`FakeShadows`（2 秒）がシーン全体を `FindObjectsByType`（P9）。
- `MapNight.Stars()` が最初の夜に 6 × 1024² の `Color[]` を同期で作る（数百 ms のヒッチ、P10）。`Map/MapNight.cs`
- **タイヤスモークの残り**（CPU の並列化と最大サイズ 0.8 は済み）: 粒子数（1 回 8 個・最大 12000）、半解像度での描画、シェーダーのランプ 8 灯ループを 4 灯に・3D テクスチャの参照を 1 回に。見た目が変わるのでユーザーと相談。レイキャストの非同期化は粒子の添字が入れ替わるので見送り。`Rendering/TireSmoke.cs`、`Shaders/NFX_Particle.shader`
- 画面コピー: 共有バッファにはしたが、影の合成 → 反射の合成を 1 つのパスにまとめればコピー自体を 1 回減らせる。`Rendering/SharedColor.cs`、`RTShadowPass.cs`、`RTReflectionPass.cs`
- 雲: 太陽マーチの早期打ち切り、雲の影は 256² か隔フレームで十分（11-02 P11）。`native/NFXNative/shaders/Clouds.hlsl`、`CloudShadow.hlsl`
- 小さな無駄（まとめて）: `UpdateWind` の配列確保と Wind オフでの `SetWind`（P13）、ランプトレースの未使用 Surface バッファ（P14）、`PrepareNrdGuide` の同サイズ 2 回（P15）、`HdrpBindings` の `MethodInfo.Invoke`（P16）、`MapProfile.Get` の毎フレーム行走査（P17）、毎フレームの `Status` 文字列・`GetGlobalVector` の文字列参照・`new GUIStyle` 等（P18、11-02 P14）、草オフでもセルメッシュを保持（P19）。

## 7. コードの品質・不具合（レビューの未修正分）

- **`AddGeometry*` / `SetInstances` がロック無しで `g_geometries` に `push_back`**、描画スレッドが同じ vector を参照（再割り当ての瞬間に use-after-free、B23 / C12）。`g_sceneMutex` を取るとヒッチが増えるので、構築の分割（6 章）と一緒に。`native/NFXNative/src/NFXNative.cpp`
- `Hash22` が `frame * 4 + i` を float で足す。長時間で乱数が量子化し、影・AO・反射のノイズが縞になる（B20）。`frame & 1023u` か整数ハッシュに。`NFXCommon.hlsli` と `NFX_NativeBridge.compute` の両方
- NRD に渡す行列（Unity の右手系・GL 式射影、viewZ 正）が NRD の規約に合っているか未検証（B21）。`enableValidation` のオーバーレイで確認。`Rendering/NativeEngine.cs`、`native/NFXNative/src/Nrd.inl`
- `NativeEngine.NrdReset` を書くのは影のパスだけ。影・AO・光芒オフで反射＋NRD のときカメラカットでゴースト（B30）。`NrdWrapWait` は 2 秒で諦めてリングを上書き（実害はほぼ無い、C13）。
- 揺れる草・森の板に `MotionVectorGenerationMode.Object` が無く、DLSS / TAA で揺れる植生がゴースト（B24）。`Nature/GrassRenderer.cs`、`Nature/ForestRenderer.cs`（低木は Object だと画面が流れたので Camera のまま）
- `Glass.hlsl` の屈折の合成で、外側の `bool own`（`NFX_FLAG_OWN_GLASS`）を画面バッファの値が隠していた名残（今は `shownBits`）。意図が `own` なら `select(own, …)` に。見た目が変わるのでゲームで比較してから。
- `AccumulationController.ApplyWetness` は後から `NatureFX/LitAccumulation` に置き換わった材質にも暗化を掛ける（既定オフの `WetGameMaterials` のときだけ、B22）。
- トラックマップのゴムの蓄積がフレームごと（144 fps で 60 fps の 2.4 倍速く黒くなる、B33）。`Nature/TrackMap.cs`、`Shaders/NFX_TrackMap.compute`
- `CarGlow` の減速度を描画フレームの `Rigidbody.velocity` 差で出す（高 fps で軽いブレーキでもディスクが光る、B34）。`Rendering/CarGlow.cs`
- 共有プリセットに `RTBackend`・`RTDxr12Sun`・`RTOverlay`・`ShowMarkers`・`UseMapProfile` が入る（B37）。`UserPresets.cs` の `Excluded`
- `Widgets`: 右クリックのリセットが対象を探すため副作用付きセッターを試し呼びする、検索とカード開閉を同じイベントで反映して IMGUI の layout 例外（B38）。`Ui/Widgets.cs`
- 月の GameObject（`DontDestroyOnLoad`）を破棄しない、`Refresh` が `sunShadowsOff_` を戻さない（B40）。`Rendering/SunController.cs`
- 11-02 R20 の小粒: `NrdReblur` のモード切替、`DlssUnpack` の SRV/UAV 同時束縛、`CarRain`/`WindController` の `OverlapSphere` 256 本、`VolumeOverhaul.trackSky_` の表示、`MapDecals` の `seed`、インポスターの影の向き、`NFX_Ground` のペイント α の補間、`OwnGlass` のスロット外、`BakeOmm` の Level < 2。

---

## ゲームでまだ確認していないもの

- **DLSS の切替で落ちない**（11-02 X1〜X3）: Quality / Balanced / Ultra perf. とウィンドウ ↔ 4K の往復、`kino/output.log` の `GPU memory` が戻るか。二重像・霧やランプの帯の遅れ・アップスケールの揺れ（R1〜R3）。
- **長時間とマップ切替**: 数時間走っても煙・雨・ブレーキ光が動く（C1）。マップを何度も切り替えて device removed が出ない、`DESCRIPTOR HEAP FULL` が出ない、GPU memory・BLAS / data pool が増え続けない（C2、B6、B12、ClearScene の接続）。
- 霧のある公式トラックでトラックの霧が残る（B8、R4）。走行中に Native sky をオフにして雲の影や古い霧が残らない（R5）。夜の月の影がチラつかない（R7）。雲なしのセッションで霧の箱と煙が出る（R10）。
- 曇りの天候で Sun override をオフにすると太陽の影が戻る（B4）。Tire marks オフで車を替えても例外が出ない（B5）。
- 3D 霧のランプのリスト（Developer「3D fog lamps」のオン/オフで見た目が同じ、20×12 の継ぎ目や 4.4 / 38.7 / 341 m の輪が出ない）。この GPU では F8 の「fog volume」の ms が出ないので fps で比べる。
- `MapNight.Stars()` の `Cubemap.SetPixels` が `RGB9e5Float` で通るか（B39、通らなければ最初の夜に例外）。
- マップ拡張の段階 1.5〜5（天候ゾーン、光源マーカー、葉の色、立体の草、デカール・水・局所の霧・舞うもの）の見た目。NatureFX を無効にして同じマップを読んだとき何も出ないこと。負荷の上限（光源 64・影 8、プローブ 16、草 60 m、デカール 512）が足りるか。
