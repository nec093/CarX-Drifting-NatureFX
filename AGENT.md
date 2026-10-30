# AGENT.md — NatureFX で作業するエージェント向けの指示

## 言語
- **回答・報告は必ず日本語で書く。** 英語での返答はしない（コード・コミットメッセージ・ログ文字列は英語のままでよい）。
- 説明はユーザーに向けて平易に。何を変えたか・何が未確認か（ゲーム内で未検証など）を必ず添える。

## プロジェクト概要
- CarX Drift Racing Online（Unity 2023.2 / HDRP 16）向けの KSL Mod。リポジトリはこのフォルダ。
- Unity のビルド用プロジェクトはリポジトリ外: `C:\Users\kppn1223\NatureFX-Unity`（Documents は Controlled Folder Access で Unity が書けないため）。
- 現在の開発の中心は **RT のみモード（ray tracing only）でのレイトレーシング** と、CarX のシェーダーを置き換える **シェーダーリビルド**。当面の目標は公式マップ parking（vp_parking）を完璧にすること。
- 独自レイトレーシングエンジン `native/NFXNative`（C++ / D3D12）を開発中。Unity のプレイヤーがインライン RT（DXR 1.1）を無効と報告するため、Unity の D3D12 デバイスを直接使って自前で BLAS/TLAS とシェーダーを動かす。

## ビルド
| 対象 | コマンド | 出力先 |
|---|---|---|
| Mod 本体（C#） | `dotnet build src/NatureFX/NatureFX.csproj -c Release` | `kino/mods/NatureFX.ksm`（maykr が自動で作る） |
| シェーダーバンドル | `bash tools/build_bundle.sh /c/Users/kppn1223/NatureFX-Unity` | `kino/mods/NatureFX/naturefx.bundle` |
| 独自エンジン（C++） | `powershell -File tools/build_native.ps1` | `kino/mods/NatureFX/NFXNative.dll` |

- バンドルのビルドは長い。`NFX_Accumulation.hlsl` など全マテリアル共通の hlsl を変えると 1248 バリアントを全部コンパイルし直すので **40〜50 分**かかる。RT の合成シェーダーだけなら数分。必ずバックグラウンドで実行する。
- ビルドスクリプトは開始時にソースを Unity プロジェクトへコピーするので、ビルド中にリポジトリを編集してよい。
- **再起動なしのシェーダー再読み込み**: ゲーム起動中にバンドルをビルドすると `kino/mods/NatureFX/reload/naturefx-<日時>.bundle` に別名で置かれる。独自エンジンのシェーダーは `powershell -File tools/build_native.ps1 -ShadersOnly` で `NativeShaders/*.cso` に置かれる（DLL は作らない）。ゲームの Developer > Reload shaders で両方を読み直す（バンドルは同名シェーダーのマテリアルを差し替え、エンジンは描画イベント 8 でトレースのパイプラインと NRD の受け渡しカーネルを作り直す）。C#・C++・構造体の配置を変えたときは再起動が必要。再起動後は DLL に埋め込まれたシェーダーに戻るので、変更を残すには通常のビルドもする。
- ゲーム起動中は `NFXNative.dll` がロックされてコピーに失敗する。`naturefx.bundle` は**ロックされず上書きできてしまい、ゲームが壊れたバンドルを読む**（"archive ... is corrupted"）。バンドルのビルドはゲーム起動中ならコピーしないようにしてある。どちらもゲームを閉じてからコピーする。MOD（.ksm）と DLL の API が変わったときは、両方そろうまで起動しない。
- ネイティブのビルドには VS（vcvars64）、Windows SDK の dxc.exe（SM 6.6 以降）、Unity エディタの `Editor/Data/PluginAPI` ヘッダーを使う。Unity のヘッダーはリポジトリにコピーしない。

## 守ること
- `NatureFX_maykr.kmc`（署名キー）は **絶対にコミットしない・貼り付けない**。
- 逆コンパイルしたゲームのコードは scratchpad にだけ置き、リポジトリに入れない。
- ファイルのダウンロードは事前にユーザーの明示的な許可を取る（ファイル名・入手元・サイズを伝える）。
- **push はユーザーに頼まれたときだけ**。コミットは作業の区切りで行ってよい。
- ゲームの exe は書き換えない。
- コミットメッセージの最後に `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` を付ける。
- ゲーム内の見た目は自分では確認できない。変更後は「何を見ればよいか」をユーザーに具体的に伝え、スクリーンショットやログ（`kino/output.log`）で確認してもらう。

## 実装上の注意（ハマりどころ）
- Git Bash の `python - <<'EOF'` で C# を書き換えると、文字列中の `\n` が本物の改行になってビルドが壊れる。バックスラッシュを含む変更は Edit ツールか raw 文字列で行う。
- 独自エンジンのペイロード（`NfxPayload`、68 バイト）は `NFXCommon.hlsli` と `NFXNative.cpp` の `kPayloadBytes` で揃える。
- 表面トレースのアルベド画像の α には metallic とクリアコートのフラグを詰めている（`metallic * 0.49 + 0.5`（クリアコート時）、`Surface.hlsl`）。α を直接 metallic として読まない。
- 合成で光を差し引くときは色チャンネルごとにクランプしない（赤い塗装が緑になった）。明るさの割合で引く。
- CarX の車体塗装（`Shader Graphs/CarPaint*`）は HDRP のクリアコート設定を持たないので、シェーダー名でクリアコートと判定している。
- RT のみモードでは `fx = Settings.Off` で蓄積・タイヤ痕が止まる。シェーダーリビルド時の路面表現は `rebuiltSurface` で別に有効にしている。
- RT 以外の旧描画（HDRP ボリューム上書きの SSGI・SSR・フォグ・ブルーム・LUT・フォトモード、画面空間パストレーシング SSPT、インライン RT の試作）は削除済み。RT のパス群は `Rendering/RayTracingEffects.cs`（`mod_.RT`）、`VolumeOverhaul` は RT のみモードの切り離しと露出・トーンだけを持つ。

## 独自エンジン（NFXNative）の現状
- 段階 1: デバイス取得（テクスチャのネイティブポインタ経由）、機能の報告、テスト三角形へのインラインレイ → 動作確認済み（RTX 4070 Ti、ray tracing tier 1.2、SM 6.8、binding tier 3）。
- 段階 2: Unity のメッシュの GPU バッファから BLAS/TLAS を構築し、物理レイキャストと距離を比べて検証。Unity のプラグインローダー経由で読み込み、`UnityPluginLoad`（D3D12v7 インターフェース）が渡るかを試している → ゲーム内で未確認。
- 現在: レイトレースはすべて独自エンジン。Unity のレイトレース（`NFX_RTGI.raytrace`、Unity の加速構造、`DispatchRays`）は削除済み。`Settings.RTBackend` は独自エンジンの方式で、0 = DXR 1.0（`NFX_PIPELINE` でトレースを lib_6_6 として作り、状態オブジェクト＋シェーダーテーブルで `DispatchRays`）、1 = DXR 1.1（インラインのコンピュート）。トレースのシェーダーは `NFXCommon.hlsli` の `Blocked` / `FirstBlocker` / `Closest` / `ClosestGlass` と入口 `NFX_ENTRY` だけを使えば両方式でコンパイルできる。`NFX_RTProxy.shader` はマテリアル値の入れ物（描かれない）。
- 揺れる葉（`Wind.inl`）: NatureFX/Foliage のインスタンスは専用の頂点と更新可能な BLAS を持ち、毎フレーム TLAS の前に `WindDisplace.hlsl` で揺らす。画面の葉の揺れはマップ設定 `[map] foliageWind = true` のときだけ。作業用メモリの確保や解放は描画スレッドで行わない（`WaitRenderIdle` の後だけ）。
- DXR 1.2（SER / OMM）はシェーダー側で SM 6.9 が必要で、ゲームの Agility SDK 1.610 では 6.8 まで。Agility SDK 1.619 のデバイスファクトリー（`ID3D12SDKConfiguration1::CreateDeviceFactory`）で独立デバイスを作れば SM 6.9 / Tier 1.2 になる（単体テスト `tools/build_dxr12_probe.ps1` で RTX 4070 Ti 合格: OMM・SER とも）。ゲーム内の確認は Settings の「Test DXR 1.2」（`NFX_Native_Dxr12Probe`: 独立デバイス + Unity デバイスとの共有バッファ・フェンス）。
- DXR 1.2 用の依存はリポジトリ外: `%USERPROFILE%\NatureFX-Deps\agility-1.619.6`（NuGet Microsoft.Direct3D.D3D12 1.619.6）と `dxc-1.9.2609`。`build_native.ps1` が 1.619 のヘッダーで NFXNative をビルドし、`D3D12Core.dll` を `kino/mods/NatureFX/D3D12/` に置く（ゲーム本体の D3D12 フォルダーは触らない）。
- DXR 1.2 デバイスの揺れる葉（`RtDevice.inl` の `BuildRtWind` / `RecordRtWind`）: Unity 側の `g_windBlas` と同じ添字で、そのデバイスの静止ジオメトリから更新可能な BLAS を作り（OMM なし。更新では焼いた OMM が古いまま残るため）、毎フレーム TLAS の前に `WindDisplace.hlsl` を同デバイスで走らせる。
- SER（`MaybeReorderThread`）はレイ生成シェーダー専用で、今のインラインレイ（コンピュート）には使えない。OMM はインラインでも使える（`RAYQUERY_FLAG_ALLOW_OPACITY_MICROMAPS`）。
- NRD（NVIDIA Real-Time Denoisers、`src/Nrd.inl`）: `%USERPROFILE%\NatureFX-Deps\NRD`（GitHub NVIDIA-RTX/NRD を clone）を vcvars64 の中で `cmake -S . -B _Build -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release -DNRD_STATIC_LIBRARY=ON -DNRD_NRI=OFF -DNRD_EMBEDS_SPIRV_SHADERS=OFF -DNRD_EMBEDS_DXBC_SHADERS=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded` → `cmake --build _Build` で `_Bin\NRD.lib` を作る（ShaderMake・MathLib・DXC を自動で取得する）。NRI は使わず、NRD のディスパッチを独自エンジンが Unity のキューで直接実行する（共有ルートシグネチャ: ルート CBV、SRV テーブル、UAV テーブル、静的サンプラー。記述子は専用ヒープのリング）。SIGMA で太陽の影（描画イベント 5、`NrdSigmaPack.hlsl` / `NrdSigmaUnpack.hlsl`）、REBLUR_DIFFUSE 2 つで環境光と GI（イベント 6、GI は跳ね返りの光と空の見える割合を別々に。半解像度のときだけ。`Ambient.hlsl` / `GI.hlsl` はレイの距離を表面バッファーの最後の float（+44）に書く）。ランプの影も REBLUR（イベント 6 モード 2、別インスタンス、遮られた光と発光面が足す光を別々に）、反射は REBLUR_SPECULAR（イベント 7、フレネルで割って渡し掛け戻す。粗さと法線はトレース入力のもの）。NRD のときは影のパスと反射のパスの自前のフィルターを飛ばす。C# は `NativeEngine.IssueNrd`、`NrdEventData` 328 バイト。配布時はライセンスに従い README に NVIDIA の表記を残す。
- フレーム定数（`NFXCommon.hlsli` の `Frame`）はローカルライトを最後に置く（`NFX_MAX_LOCAL_LIGHTS` = 32、564 float、`kConstantSlot` 2304）。C# の `NativeEngine.Issue` の添字と必ず揃える。`Flags`（c[15]）は `NFX_FLAG_*`。
- ランプの影（`LocalShadows.hlsl`）は寄与に比例して 1 画素 2 本のライトを選ぶ（RIS）。`LightColor.w` 2 は発光面の代役（HDRP にない光）で、届いた分を負の「遮られた光」として足す。合成の `color -= min(...)` は負を通す前提。
- テストマップ `test_map` の `props_rt_features`（`tools/kino_sdk/NfxDataTestMap.cs` の `BuildFeatureArea`）に各 RT 機能の確認場所がある。ビルドは `Unity.exe -batchmode -quit -projectPath C:/Users/kppn1223/Desktop/kino_content_sdk -executeMethod NfxDataTestMap.BuildAll`（スクリプトを SDK の `Assets/Editor/NatureFX/` にコピーしてから）。
