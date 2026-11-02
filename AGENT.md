# AGENT.md — NatureFX で作業するエージェントへの指示

## 1. 言語

- 回答と報告は必ず日本語で書く。英語では返答しない。ただし、コード、コミットメッセージ、ログ文字列は英語でよい。
- 説明はユーザーに向けて平易な表現で書く。変更した内容と、未確認の事項（ゲーム内で未検証の点など）を必ず記載する。

## 2. 引き継ぎ

- セッションの開始時に、まず `docs/HANDOFF.md` と `docs/ROADMAP.md` を読む。HANDOFF.md には現在の構成、既定値、未確認の項目、注意点を記載している。ROADMAP.md には残作業を記載している。
- 作業の区切りごとに両文書を更新する。HANDOFF.md は経過の記録にせず、現在の状態を書き直す。経過は git log に残る。

## 3. 方針（2026-11-01、ユーザー決定）

- RT の設定にかかわらず、移行できる処理はすべて独自エンジン（NFXNative）に移す。
- HDRP の役割は必要最小限とする。HDRP に残すのは、G バッファー（マテリアルのラスタライズ）、UI、ゲーム本体が必要とする処理である。高度なレンダリングは独自エンジンで行う。
- 新しい描画機能は、独自エンジンのパス（例: `AtmospherePass`）として実装する。パスは RT のトグルではなく、専用の設定で有効にする。
- HDRP の機能を無効にすると半透明物などが壊れる場合は、機能を無効にせず、出力を上書きする。
- 残作業は `docs/ROADMAP.md` に記載している。

## 4. プロジェクト概要

- 本プロジェクトは、CarX Drift Racing Online（Unity 2023.2 / HDRP 16）向けの KSL Mod である。リポジトリはこのフォルダーである。
- Unity のビルド用プロジェクトは、リポジトリ外の `C:\Users\kppn1223\NatureFX-Unity` に置く。Documents 配下には、Controlled Folder Access により Unity が書き込めないためである。
- 現在の開発の中心は次の 3 点である。
  - 独自エンジンによる照明、空、霧、後処理
  - CarX のシェーダーを置き換えるシェーダーリビルド
  - マップ拡張（書式は docs/map-authoring.md）
- HDRP の機能のうち独自エンジンが置き換えたものは、独自エンジンの動作中は停止する（`OwnLighting`、`NatureFXMod` の `CameraVolumetrics`、`VolumeOverhaul`。docs/HANDOFF.md 2 章を参照）。
- 当面の目標は、公式マップ parking（vp_parking）を完璧に仕上げることである。
- 独自のレイトレーシングエンジン `native/NFXNative`（C++ / D3D12）を開発中である。Unity のプレイヤーはインライン RT（DXR 1.1）を無効と報告する。このため、Unity の D3D12 デバイスを直接使用し、BLAS/TLAS の構築とシェーダーの実行を自前で行う。

## 5. ビルド

| 対象 | コマンド | 出力先 |
|---|---|---|
| Mod 本体（C#） | `dotnet build src/NatureFX/NatureFX.csproj -c Release` | `kino/mods/NatureFX.ksm`（maykr で自動生成） |
| シェーダーバンドル | `bash tools/build_bundle.sh /c/Users/kppn1223/NatureFX-Unity` | `kino/mods/NatureFX/naturefx.bundle` |
| 独自エンジン（C++） | `powershell -File tools/build_native.ps1` | `kino/mods/NatureFX/NFXNative.dll` |

### 5.1 シェーダーバンドルのビルド時間

- シェーダーバンドルのビルドには時間がかかる。`NFX_Accumulation.hlsl` など全マテリアル共通の hlsl を変更すると、Lit 系の全バリアントを再コンパイルする。ノイズのテクスチャー参照と、`unity/variants/game_variant_profile.txt` によるバリアント削減を適用した状態で、フルビルドは約 30 分である（`unity/variants/README.md` 4 章）。RT の合成シェーダーのみの変更であれば数分で終わる。
- ビルドは必ずバックグラウンドで実行する。
- ビルドログの `finished in ... compiled N variants (... CPU time)` の行で、どのパスに時間がかかったかを確認できる。

### 5.2 実行環境

- `build_bundle.sh` は、`cygpath` を使用するため Git Bash で実行する。PowerShell で `bash` と入力すると WSL の bash が起動する。この場合、Unity は起動せず、古いログの末尾（`Built ... Copied to ...`）のみが表示される。PowerShell からは次のコマンドで実行する。

  ```
  & "C:\Program Files\Git\bin\bash.exe" tools/build_bundle.sh /c/Users/kppn1223/NatureFX-Unity
  ```

- ビルドスクリプトは、開始時にソースを Unity プロジェクトへコピーする。このため、ビルド中にリポジトリを編集してよい。
- ネイティブのビルドには、VS（vcvars64）、Windows SDK の dxc.exe（SM 6.6 以降）、Unity エディターの `Editor/Data/PluginAPI` ヘッダーを使用する。Unity のヘッダーはリポジトリにコピーしない。

### 5.3 再起動なしのシェーダー再読み込み

- ゲームの起動中にシェーダーバンドルをビルドすると、`kino/mods/NatureFX/reload/naturefx-<日時>.bundle` に別名で配置される。
- 独自エンジンのシェーダーは、`powershell -File tools/build_native.ps1 -ShadersOnly` で `NativeShaders/*.cso` に出力される。この場合、DLL は生成しない。
- デバッグページの developer タブ > Reload shaders で、両方を再読み込みする。シェーダーバンドルは、同名シェーダーを使うマテリアルを差し替える。独自エンジンは、描画イベント 8 でトレースのパイプラインと NRD の受け渡しカーネルを再作成する。
- C#、C++、構造体の配置を変更した場合は、ゲームの再起動が必要である。再起動後は DLL に埋め込まれたシェーダーに戻るため、変更を残すには通常のビルドも行う。

### 5.4 ファイルのロック

- ゲームの起動中は `NFXNative.dll` がロックされ、コピーに失敗する。
- **`naturefx.bundle` はロックされないため上書きできてしまい、ゲームが破損したバンドルを読み込む**（"archive ... is corrupted"）。シェーダーバンドルのビルドは、ゲームの起動中であればコピーしないようにしている。
- どちらのファイルも、ゲームを終了してからコピーする。
- MOD（.ksm）と DLL の間の API を変更した場合は、両方がそろうまでゲームを起動しない。

## 6. 遵守事項

- **`NatureFX_maykr.kmc`（署名キー）は絶対にコミットしない。内容を貼り付けることもしない。**
- 逆コンパイルしたゲームのコードは scratchpad のみに置き、リポジトリに含めない。
- ファイルをダウンロードする前に、ユーザーの明示的な許可を得る。その際、ファイル名、入手元、サイズを伝える。
- push はユーザーから依頼があった場合のみ行う。コミットは作業の区切りで行ってよい。
- ゲームの exe は書き換えない。
- コミットメッセージの末尾に `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` を付ける。
- ゲーム内の見た目はエージェント自身では確認できない。変更後は、確認すべき点をユーザーに具体的に伝え、スクリーンショットやログ（`kino/output.log`）での確認を依頼する。

## 7. 実装上の注意点

- Git Bash の `python - <<'EOF'` で C# を書き換えると、文字列中の `\n` が実際の改行に変換され、ビルドが失敗する。バックスラッシュを含む変更は、Edit ツールまたは raw 文字列で行う。
- 独自エンジンのペイロード（`NfxPayload`、68 バイト）は、`NFXCommon.hlsli` と `NFXNative.cpp` の `kPayloadBytes` で一致させる。
- 表面トレースのアルベド画像の α には、metallic とクリアコートのフラグを格納している（クリアコート時は `metallic * 0.49 + 0.5`。`Surface.hlsl` を参照）。α を metallic として直接読まない。
- 合成で光を差し引く際は、色チャンネルごとにクランプしない。過去に、赤い塗装が緑になる不具合が発生した。明るさの割合で差し引く。
- CarX の車体塗装（`Shader Graphs/CarPaint*`）は、HDRP のクリアコート設定を持たない。このため、シェーダー名でクリアコートかどうかを判定している。
- シェーダーリビルド時の路面表現（粒、摩耗、ゴム）は、`NatureFXMod.Update` の `rebuiltSurface`（`Enabled && ReplaceShaders`）で有効にしている。
- 旧描画方式は削除済みである。対象は、HDRP ボリューム上書きによる SSGI・SSR・フォグ・ブルーム・LUT、画面空間パストレーシング（SSPT）、インライン RT の試作、RT のみモード、露出の「Game」である。
- RT のパス群は `Rendering/RayTracingEffects.cs`（`mod_.RT`）にある。`VolumeOverhaul` は、ゲームの画面効果の切り離し、空の光の倍率、露出とトーンの Volume を担当する。

## 8. 独自エンジン（NFXNative）の現状

### 8.1 基盤

- Unity のプラグインローダー経由で読み込み、`UnityPluginLoad`（D3D12v7 インターフェース）で Unity のデバイスとキューを使用する。
- Unity のメッシュの GPU バッファーから BLAS/TLAS を構築する。頂点データは共有のプールに 1 つ保持し、BLAS は圧縮してプールに格納する。
- ゲーム内で動作している（RTX 4070 Ti、ray tracing tier 1.2、SM 6.8、binding tier 3）。
- 新しいマップを読み込んだとき（Single の読み込み）は、シーンを再構築する。
- 構成の全体は docs/HANDOFF.md 1 章に記載している。

### 8.2 レイトレーシングの方式

- レイトレーシングはすべて独自エンジンで行う。Unity のレイトレーシング（`NFX_RTGI.raytrace`、Unity の加速構造、`DispatchRays`）は削除済みである。
- `Settings.RTBackend` は独自エンジンの方式を指定する。
  - 0 = DXR 1.0。`NFX_PIPELINE` でトレースを lib_6_6 としてビルドし、状態オブジェクトとシェーダーテーブルを使って `DispatchRays` を実行する。
  - 1 = DXR 1.1。インラインのコンピュートで実行する。こちらが既定である。
- トレースのシェーダーは、`NFXCommon.hlsli` の `Blocked` / `FirstBlocker` / `Closest` / `ClosestGlass` と入口 `NFX_ENTRY` のみを使用すれば、両方式でコンパイルできる。
- `NFX_RTProxy.shader` はマテリアル値の格納用であり、描画には使われない。

### 8.3 揺れる葉

- 揺れる葉は `Wind.inl` で処理する。NatureFX/Foliage のインスタンスは、専用の頂点と更新可能な BLAS を持つ。毎フレーム、TLAS の構築前に `WindDisplace.hlsl` で頂点を揺らす。
- 画面上の葉の揺れは、マップ設定が `[map] foliageWind = true` のときのみ有効である。
- 作業用メモリの確保と解放は描画スレッドで行わない。行うのは `WaitRenderIdle` の後に限る。

### 8.4 DXR 1.2（SER / OMM）

- DXR 1.2 には、シェーダー側で SM 6.9 が必要である。ゲームに含まれる Agility SDK 1.610 では SM 6.8 までしか使えない。
- Agility SDK 1.619 のデバイスファクトリー（`ID3D12SDKConfiguration1::CreateDeviceFactory`）で独立デバイスを作成すると、SM 6.9 / Tier 1.2 を使用できる。単体テスト `tools/build_dxr12_probe.ps1` では、RTX 4070 Ti で OMM、SER ともに合格した。
- ゲーム内での確認には、Settings の「Test DXR 1.2」（`NFX_Native_Dxr12Probe`）を使う。このテストは、独立デバイスと、Unity デバイスとの共有バッファー・フェンスを使用する。
- DXR 1.2 用の依存ファイルは、リポジトリ外の `%USERPROFILE%\NatureFX-Deps\agility-1.619.6`（NuGet Microsoft.Direct3D.D3D12 1.619.6）と `dxc-1.9.2609` に置く。`build_native.ps1` は 1.619 のヘッダーで NFXNative をビルドし、`D3D12Core.dll` を `kino/mods/NatureFX/D3D12/` に配置する。ゲーム本体の D3D12 フォルダーは変更しない。
- DXR 1.2 デバイスでの揺れる葉は、`RtDevice.inl` の `BuildRtWind` / `RecordRtWind` で処理する。Unity 側の `g_windBlas` と同じ添字で、そのデバイスの静止ジオメトリから更新可能な BLAS を作成する。この BLAS には OMM を使用しない。更新時に、焼き込んだ OMM が古いまま残るためである。毎フレーム、TLAS の構築前に、同じデバイスで `WindDisplace.hlsl` を実行する。
- SER（`MaybeReorderThread`）はレイ生成シェーダー専用であり、現在のインラインレイ（コンピュート）には使用できない。OMM はインラインでも使用できる（`RAYQUERY_FLAG_ALLOW_OPACITY_MICROMAPS`）。

### 8.5 NRD

- NRD（NVIDIA Real-Time Denoisers、`src/Nrd.inl`）のソースは `%USERPROFILE%\NatureFX-Deps\NRD` に置く（GitHub NVIDIA-RTX/NRD を clone）。
- vcvars64 の環境で次のコマンドを順に実行し、`_Bin\NRD.lib` を生成する。ShaderMake、MathLib、DXC は自動で取得される。

  ```
  cmake -S . -B _Build -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release -DNRD_STATIC_LIBRARY=ON -DNRD_NRI=OFF -DNRD_EMBEDS_SPIRV_SHADERS=OFF -DNRD_EMBEDS_DXBC_SHADERS=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
  cmake --build _Build
  ```

- NRI は使用しない。NRD のディスパッチは、独自エンジンが Unity のキューで直接実行する。ルートシグネチャは共有とし、ルート CBV、SRV テーブル、UAV テーブル、静的サンプラーで構成する。記述子は専用ヒープのリングで管理する。
- 各デノイザーの用途は次のとおりである。
  - SIGMA_SHADOW_TRANSLUCENCY は太陽の影に使用する。ガラス越しの影は、透過率に応じて暗くする。描画イベント 5 で処理し、`NrdSigmaPack.hlsl` / `NrdSigmaUnpack.hlsl` を使用する。
  - REBLUR_DIFFUSE の 2 インスタンスは、環境光と GI に使用する（イベント 6）。GI では、跳ね返りの光と空の見える割合を別々に処理する。これは半解像度のときのみである。`Ambient.hlsl` / `GI.hlsl` は、レイの距離を環境光の表面バッファーのバイト 28（`NfxAmbientTravelledOffset`、`NfxPack.hlsli`）に書き込む。
  - ランプの影にも REBLUR を使用する（イベント 6 モード 2、別インスタンス）。遮られた光と、発光面が加える光を別々に処理する。
  - 反射には REBLUR_SPECULAR を使用する（イベント 7）。フレネル項で割った値を渡し、結果に再び掛ける。粗さと法線は、トレース入力の値を使用する。
- NRD を使用するときは、影のパスと反射のパスにある独自のフィルターを省略する。
- C# 側の呼び出しは `NativeEngine.IssueNrd` で行う。`NrdEventData` は 328 バイトである。
- 配布時は、ライセンスに従い、README に NVIDIA の表記を残す。

### 8.6 フレーム定数とローカルライト

- フレーム定数（`NFXCommon.hlsli` の `Frame`）では、ローカルライトを末尾に配置する（`NFX_MAX_LOCAL_LIGHTS` = 128 = `LocalLights.Max`、2100 float、`kConstantSlot` 8448）。C# の `NativeEngine.Issue` の添字と必ず一致させる。
- `Flags`（c[15]）には `NFX_FLAG_*` を使用する。
- ランプの影（`LocalShadows.hlsl`）は、寄与に比例して 1 画素あたり 2 本のライトを選択する（RIS）。
- `LightColor.w` が 2 のライトは、発光面の代役（HDRP にない光）である。届いた光を、負の「遮られた光」として加算する。合成の `color -= min(...)` は、負の値を通すことを前提としている。

### 8.7 テストマップ

- テストマップ `test_map` は、区画ごとに 1 つのテーマを割り当てる。配置のルールは docs/testmap-layout.md に、区画の表と内容は `tools/kino_sdk/NfxTestBays.cs` に、地面・峠区間・看板は `NfxDataTestMap.cs` に記述している。
- 新しいテストを追加するときは、区画の表に 1 行追加する。
- ビルドの前に、`NfxDataTestMap.cs` と `NfxTestBays.cs` を SDK の `Assets/Editor/NatureFX/` にコピーする。その後、次のコマンドを実行する。ゲームの起動中は、環境変数 `NFX_MAP_OUT` で出力先を別のフォルダーに指定する。

  ```
  Unity.exe -batchmode -quit -projectPath C:/Users/kppn1223/Desktop/kino_content_sdk -executeMethod NfxDataTestMap.BuildAll
  ```

- 実写素材の峠区間（`BuildRealArea`、スポーン地点の後方）には、Poly Haven の CC0 素材を使用する。事前に `tools/kino_sdk/polyhaven_import.py` で、SDK の `Assets/Content/NfxAssets` に変換しておく（docs/testmap-assets.md）。

### 8.8 DLSS

- DLSS（`src/Dlss.inl`）のソースは `%USERPROFILE%\NatureFX-Deps\DLSS` に置く（GitHub NVIDIA/DLSS を clone）。
- `build_native.ps1` は、`include` と `nvsdk_ngx_s.lib`（/MT）でリンクする。また、`lib/Windows_x86_64/rel/nvngx_dlss.dll`、`nvngx_dlssd.dll`（Ray Reconstruction）、LICENSE を `kino/mods/NatureFX/` に配置する。NGX は NFXNative.dll と同じフォルダーから DLL を探す。
- Unity のテクスチャーは DLSS に渡さない。処理の流れは次のとおりである。
  1. ブリッジの `PrepareDlss` が、色（半精度 4 成分）、動き、深度を uint4 のバッファーにまとめる。
  2. 描画イベント 9 で `DlssPack.hlsl`、DLSS、`DlssUnpack.hlsl` の順に実行し、色を書き戻す。
  3. `UnpackDlss` が、結果を PostPass のコピーへ書き込む。
- ジッターは、`Rendering/Dlaa.cs` がカメラの射影行列に加える。この処理は `beginContextRendering` で行う。HDRP は `beginCameraRendering` より前に行列を取得するためである。
- DLSS に渡す動きは、ジッターを含まない行列（現在と前フレーム）から `PrepareDlss` が算出する。
- DLSS に渡すジッターは、HDRP の DLSS と同様に、射影のずらし量の符号を反転した値とする。
- 独自の後処理を使用している間は、HDRP の TAA を使用しない。DLSS が動作しない場合は、PostPass の独自 TAA を使用する。
- アップスケール（品質 1〜4）では、HDRP の動的解像度で低い解像度に描画させる。PostPass が DLSS で表示解像度に拡大し、HDRP の最終パスのマテリアルを `NFX_UpscaledFinal` に差し替えて表示する。旧来の HDRP の TAAU 経路は、デバッグページのスイッチで引き続き使用できる。
- `DlssEventData` は 192 バイトである。
