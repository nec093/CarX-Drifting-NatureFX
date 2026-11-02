# NatureFX (KSL mod)

Repository: https://github.com/nec093/CarX-Drifting-NatureFX

CarX Drift Racing Online (Unity 2023.2.22f1 / HDRP 16) 向けのレイトレーシング・シェーダー総合 Mod。
照明・空・霧・反射・後処理の大部分を、NatureFX の独自エンジン `NFXNative.dll`（DX12、DXR）とカスタムパスで描き直します。
HDRP に残すのは G バッファ（マテリアルのラスタライズ）、半透明、UI、ゲーム自体が要る処理です。

開発の引き継ぎ（構成・既定値・未確認の項目）は [docs/HANDOFF.md](docs/HANDOFF.md)、これからの作業は [docs/ROADMAP.md](docs/ROADMAP.md)。

## 機能

**レイトレーシング・照明（独自エンジン、DX12 のみ）**

| 機能 | 中身 |
|---|---|
| 太陽・月の影 | シーン全体へのレイ。接地で鋭く離れるほど柔らかい半影、ガラス越しは色付きで減衰、揺れる葉の影も揺れる、透明な面（白線など）にも影 |
| 太陽・ランプの光 | HDRP の太陽・点光源・スポットは何も照らさず、G バッファから同じ式で求めた光を独自エンジンの影を付けて足す（ランプは 1 画素 4 灯まで個別の影） |
| 環境光・GI | 空の光を周りが遮る分だけ暗く、跳ね返りの光（ワールド空間のキャッシュで多重バウンス）、ネオン・看板などの発光面も周りを照らす |
| 反射 | 濡れた路面・水たまり・塗装・クローム。画面外も映る。粗い面は独自の空のプローブ |
| ガラス | 独自エンジンが透過・映り込み・屈折まで描く（手前から 3 枚まで） |
| 光の筋 | 木漏れ日などの光芒 |
| ノイズ除去 | NVIDIA NRD（SIGMA / REBLUR、既定）か NatureFX のフィルター |
| 方式 | DXR 1.1（インライン、既定）か DXR 1.0（パイプライン）。DXR 1.2 の太陽の影はテスト |

**空・霧・雲**

| 機能 | 中身 |
|---|---|
| 空 | 太陽・月と大気から物理ベースで計算した空、太陽と月の円盤、星。HDRP の空・空の光・空の映り込みは止めて独自のものを使う |
| 雲 | 天気の雲の種類に合わせた体積雲と地面の雲の影 |
| 霧 | 独自エンジンの 3D の霧（太陽・月・空・ランプ・ヘッドライトで照らす、遠景のかすみ、局所の霧とタイヤスモークも中に入る） |
| 時刻 | 時刻の上書き、時間の経過（1〜720 倍）、ゲームの夜の照明（街灯）を NatureFX の時刻に合わせる |

**後処理・露出（独自の後処理、既定オン）**

| 機能 | 中身 |
|---|---|
| 露出 | Fixed（手で決めた EV、既定 14）か目の順応（中央重みの測光、暗い場面は暗く見せる、閃光には順応しない）＋補正 |
| トーン・色 | トーンカーブ（Khronos PBR Neutral が既定、ACES・AgX・なし）、コントラスト・彩度、目のホワイトバランス |
| レンズ | ブルーム、光源ごとのレンズフレアと光条、被写界深度（車に合焦、F 値）、モーションブラー（シャッター角）、暗所の見え方（プルキンエ・粒状感） |
| HDRP 側 | 独自の後処理の間、HDRP のトーンマップ・ブルーム・色調整・フレア・ビネット等と TAA はオフ |

**アンチエイリアス・アップスケール**

| 機能 | 中身 |
|---|---|
| NVIDIA DLSS | DLAA（既定）か Quality / Balanced / Performance / Ultra perf. のアップスケール。アップスケールの画は HDRP の最終パスを `NFX_UpscaledFinal` に差し替えて表示 |
| 独自の TAA | DLSS が動かないとき（RTX 以外など）。HDRP の TAA は使わない |
| Ray Reconstruction | 太陽の影を DLSS でノイズ除去（DLAA のときだけ、既定オフ） |

**天気・雨**

| 機能 | 中身 |
|---|---|
| 天気 | プリセット（Clear / Overcast / Rain / Storm / Snow / Autumn / Fog / Manual）、自動で移り変わる天気、季節 |
| 雨 | 雨量に応じた空気の霧、雨粒（太陽の影・ヘッドライトや街灯の光の中で光る、着地のしぶき）、路面の濡れ（染み込み → 水膜・波紋、時間をかけて濡れる・乾く）、車の水滴と流れる水、タイヤの水しぶき |
| 雪・蓄積 | 降る雪、雪・落ち葉・砂ぼこり・濡れが時間とともに積もる、雪の轍 |

**自然・路面・車**

| 機能 | 中身 |
|---|---|
| 風 | 草・葉の揺れ、突風、葉のはためき、車が草を押し分ける（画面の揺れはマップ設定 `foliageWind = true` のときだけ、RT の影は常に揺れる） |
| シェーダーリビルド | ゲームのマテリアルを NatureFX のシェーダーに置き換え（全マテリアル / 規則に当たるものだけ、草木も）。アンチタイリング、路面の摩耗・タイヤ痕・ドライライン |
| ヘッドライト | 本物のロービーム配光（ハロゲン / HID / LED、日本 / 欧米）、光軸、色温度、ハイビーム（F7） |
| 車 | タイヤスモーク（光と影を受ける）、ブレーキの赤熱とアフターファイア、泥・ほこり・雪の汚れ、クロームのホイール |
| ゲームのパーティクル | ゲーム・トラックの半透明のパーティクルを独自エンジンの光と霧で照らす |

**マップ拡張**

マップ作者が KN マップ（またはマップ外のプロファイル）で、天候ゾーン・霧・光源・反射プローブ・素材の見た目・発光・3D の草・遠景の森・藪・地面のなじませ・汚れ・デトリタス・デカール・水たまり・局所の霧・パーティクルを指定できます。書式は [docs/map-authoring.md](docs/map-authoring.md)。F8 の Map タブで、読み込んだ内容とマーカーの状態を見られます。

**デバッグページ**

開発用の情報と道具はブラウザで開くデバッグページにあります（既定オン、この PC だけ `http://127.0.0.1:8765/`）。
タブ: overview / performance / memory / scene / developer / log。VRAM の内訳、GPU・CPU の負荷と時間、遅いフレーム、シーン、ログ。
developer タブ: RT の確認用表示、シェーダーの再読み込み、NRD の各部のスイッチ、DLSS のテスト、テクスチャの予算、シェーダーのダンプ・差し替えルールの再読み込み・バリアントの記録、マテリアル一覧。

## インストール

ゲームのフォルダ（`C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online`）に次を置きます。

| ファイル | 置き場所 |
|---|---|
| `NatureFX.ksm` | `kino/mods/` |
| `naturefx.bundle`（シェーダーバンドル） | `kino/mods/NatureFX/` |
| `NFXNative.dll`、`nvngx_dlss.dll`、`nvngx_dlssd.dll`、`DLSS-LICENSE.txt` | `kino/mods/NatureFX/` |
| `D3D12Core.dll`（Agility SDK 1.619、DXR 1.2 のテスト用） | `kino/mods/NatureFX/D3D12/` |

- レイトレーシングと独自エンジンは DX12 だけで動きます。Steam の起動オプションに `-force-d3d12`（F8 > Settings > Graphics API から再起動して切り替えもできる）
- `.ksm`・DLL・バンドルは同じ版をそろえて入れます（どれか 1 つだけ古いと表示が壊れることがある）
- ゲーム起動中は DLL を上書きできず、バンドルは上書きすると壊れたまま読まれるので、ゲームを閉じてからコピーします

## ビルド

| 対象 | コマンド | 出力先 |
|---|---|---|
| Mod 本体（C#、net472） | `dotnet build src/NatureFX/NatureFX.csproj -c Release` | `kino/mods/NatureFX.ksm`（maykr と署名キーがあれば自動） |
| シェーダーバンドル | `bash tools/build_bundle.sh <Unity プロジェクト>`（Git Bash） | `kino/mods/NatureFX/naturefx.bundle` |
| 独自エンジン（C++ / D3D12） | `powershell -File tools/build_native.ps1` | `kino/mods/NatureFX/NFXNative.dll` ほか |

- **Mod 本体**: [ksl_sdk の Releases](https://github.com/trbflxr/ksl_sdk/releases) から `KSL.API.dll` と `0Harmony.dll` を `lib/` に、`maykr.exe` を `tools/` に置く（`lib/README.md`）。Unity / HDRP の DLL はインストール済みのゲームから参照します。ゲームが別の場所なら `-p:GameDir="D:\SteamLibrary\steamapps\common\CarX Drift Racing Online"`。`.ksm` の署名キーは `NatureFX_maykr.kmc`（リポジトリ直下、コミットしない）。`-p:DeployToGame=true` で生の .dll を `kino/mods` にコピー（KSL の開発者モードが必要）
- **シェーダーバンドル**: Unity 2023.2.x（2023.2.20f1 で確認）で空のプロジェクトを作り、`Packages/manifest.json` に `"com.unity.render-pipelines.high-definition": "16.0.6"` を足す。Windows の「コントロールされたフォルダーアクセス」があれば Documents の外に作る。スクリプトがソースをコピーし、Unity をバッチモードで動かしてシェーダー生成 → バンドル作成 → ゲームへのコピーまで行います（ログは `<プロジェクト>/Logs/naturefx_build.log`）。ゲーム起動中は `kino/mods/NatureFX/reload/` に別名で置かれ、デバッグページの Reload shaders で読み直せます。変種の絞り込みは `unity/variants/README.md`
- **独自エンジン**: Visual Studio（vcvars64）、Windows SDK の dxc（SM 6.6 以降）、Unity エディタの `PluginAPI` ヘッダー、`%USERPROFILE%\NatureFX-Deps` の NRD・DLSS・Agility SDK 1.619・dxc 1.9 を使います（詳細は `AGENT.md`）。`-ShadersOnly` でシェーダーだけを `NativeShaders/*.cso` に置き、再起動なしで読み直せます

NatureFX のシェーダー（`NatureFX/Foliage`、`NatureFX/LitAccumulation`）は、インストールされている HDRP/Lit をコピーして風・蓄積・アンチタイリングなどを差し込んだものです（`Editor/GenerateNatureFXShaders.cs` がビルドのたびに生成）。プロパティ名・キーワード・パスが HDRP/Lit と同じなので、シェーダーだけを差し替えてもテクスチャや設定はそのまま使われます。

## 使い方と設定の場所

- **F8**: NatureFX のウィンドウ（Esc / F8 で閉じる、Ctrl+F で検索、右クリックで既定値に戻す）
- **F7**: ハイビーム、**F9**: 写真モード（カメラを止めている間、RT を最高品質で積み上げて 1 枚にする）
- KSL メニュー > NatureFX: オン / オフ、風・蓄積の簡単な設定、シェーダーのダンプとルールの再読み込み

F8 のタブ:

| グループ | タブ | 主な設定 |
|---|---|---|
| GENERAL | Overview | 自分のプリセット（保存・読み込み・1 行のコードで共有）、すべてを既定値に戻す |
| LIGHT & SKY | Sun & time | 時刻、時間の経過、太陽の向き・高さ・強さ・色温度、月、ゲームの夜の照明 |
| | Sky & fog | 独自の空、空の光・太陽の光・ランプの光を独自エンジンから、独自 BSDF（実験的）、半透明の照明、雲・星、独自の霧、遠景のかすみ、光の筋 |
| | Lighting & shadows | Native lighting、RT の太陽の影（強さ・解像度・ガラス・揺れる葉・ランプごとの影・太陽の大きさ・葉の透過）、ランプとヘッドライト、ゲームのパーティクル |
| | Ambient light & GI | 環境光、跳ね返り、GI（距離・強さ）、日陰の空の光の強さと色、コンタクトシャドウ |
| | Reflections & glass | RT の反射（強さ・粗さ・距離・品質）、ガラスの映り込みと屈折、濡れた路面の伸び、独自のガラス |
| CAMERA | Exposure & tone | 独自の後処理、ブルーム、露出（Fixed / 目の順応）、トーンカーブ、コントラスト・彩度、ホワイトバランス |
| | DLSS | DLSS のオン / オフ、DLAA / アップスケールの品質、Ray Reconstruction |
| | Lens & eye | 暗所の見え方、レンズフレア・光条、被写界深度、モーションブラー |
| WORLD | Weather | 天気のプリセット、移り変わり、季節、雨と雪、手動の値、時間で積もる |
| | Nature | 風、葉のはためき、揺れ、車が草を押す |
| | Road | タイヤ痕・ドライライン・轍、路面の細かさ・摩耗 |
| | Cars | ヘッドライト、塗装、雨の車、スモーク・赤熱・汚れ |
| | Map | マップのプロファイルと読み込んだ内容、マーカーの表示、各効果の状態 |
| ADVANCED | Ray tracing | DXR 1.0 / 1.1、トレースの解像度、ノイズ除去、DXR 1.2 のテスト、RT のコスト計測 |
| | Shader rebuild | 置き換え（全マテリアル・草木）、アンチタイリング、置き換えなかったマテリアル |
| | Settings | ウィンドウの見た目、Graphics API（DX11 / DX12） |
| | Developer | デバッグページのオン / オフと URL だけ（開発用の道具はデバッグページへ移した） |

設定は KSL の設定として自動で保存されます。そのほかのファイル:

| ファイル | 中身 |
|---|---|
| `kino/config/NatureFX.rules.txt` | シェーダー差し替えのルール（初回起動時に作られる） |
| `kino/config/NatureFX/maps/<シーン名>.nfx` | マップ外のマッププロファイル（マップ内の `nfx_profile` より優先） |
| `kino/config/NatureFX/presets/` | F8 Overview で保存したプリセット |
| `kino/output.log` | NatureFX のログ（`[NatureFX]` の行） |

### シェーダー差し替えルール

```
<ゲームのシェーダー名 正規表現> | <名前 正規表現> => <NFX シェーダー名>
```

- 区切りは前後に空白のある ` | `。空白なしの `|` は正規表現の「または」
- `<名前 正規表現>` は **マテリアル名** と、そのマテリアルを使う各オブジェクトの **階層パス**（最大 3 階層、例: `Track/Road/main_LOD0`）の両方に当てはめる
- CarX のマテリアル名はほとんどが `0x4C6A925E` のような番号なので、オブジェクト名で書くのが確実
- マテリアルは共有されるため、1 つでも当てはまるオブジェクトがあれば、そのマテリアルを使う全オブジェクトが変わる
- 「Every material（full replacement）」がオンなら、ルールにないマテリアルも汎用の変換で置き換える

例（道路と柵に雪・濡れを足す）:

```
(?i)^HDRP/Lit$ | (?i)(road|asphalt|concrete_barrier) => NatureFX/LitAccumulation
```

デバッグページの developer タブ（または KSL メニューの Tools）の **Dump scene shaders** で、読み込まれているシーンのシェーダーごとのプロパティ・マテリアルの使用数・オブジェクトの階層パスが `kino/config/NatureFX_dump_*.txt` に出ます。ルールを直して **Reload replacement rules** で再適用します。

## フォルダ構成

```
├─ README.md / AGENT.md        概要 / 作業者向けの指示
├─ docs/                       HANDOFF.md（現状と引き継ぎ）、ROADMAP.md（残りの作業）、map-authoring.md（マップの書式）ほか
├─ lib/                        KSL.API.dll, 0Harmony.dll（ksl_sdk から、gitignore）
├─ src/NatureFX/               Mod 本体（C#, net472）
│  ├─ NatureFXMod.cs           エントリポイント（BaseMod）、毎フレームの更新、HDRP の機能の切り替え
│  ├─ NatureFXWindow.cs        F8 のウィンドウ / SettingsUI.cs は KSL メニュー
│  ├─ Settings.cs              設定と既定値（移行）/ UserPresets.cs
│  ├─ Rendering/               独自エンジンの橋渡し（NativeEngine）、RT のパス、空・霧、OwnLighting、後処理、DLSS、車・ヘッドライト・雨
│  ├─ Nature/                  風、蓄積・天気、シェーダーバンドル、マテリアル置き換え、草・森・藪、タイヤ痕
│  ├─ Map/                     マッププロファイルとマーカー、天候ゾーン、霧・光源・プローブ・デカール・水・パーティクル
│  ├─ Rebuild/                 シェーダーリビルドの汎用変換
│  ├─ Tools/                   デバッグページ、計測、ダンプ類
│  └─ Ui/                      ウィンドウの部品
├─ native/
│  ├─ NFXNative/               独自エンジン（C++ / D3D12、src/ と shaders/）
│  └─ Dxr12Probe/              DXR 1.2（SER / OMM）の確認
├─ unity/
│  ├─ Assets/NatureFX/         バンドルのソース（Editor/、ShaderLibrary/、Shaders/）
│  └─ variants/                変種の絞り込み用のデータ
└─ tools/                      build_bundle.sh, build_native.ps1, build_dxr12_probe.ps1, kino_sdk/（テストマップ）, maykr.exe（gitignore）
```

## 既知の制限

- レイトレーシング・独自エンジン（空・霧・照明）・DLSS は DX12 だけ。DX11 で動くのは風・蓄積・シェーダーリビルドなど、独自エンジンを使わない部分
- DLSS は RTX の GPU だけ（ほかは独自の TAA）
- ゲームのアップデートでシェーダー名が変わると差し替えルールの更新が必要
- 未解決の項目とゲームで未確認の項目は [docs/HANDOFF.md](docs/HANDOFF.md)

## 使用しているサードパーティ SDK

- **NVIDIA Real-Time Denoisers (NRD)**: レイトレースの影・環境光・GI・ランプの影・反射のノイズ除去に NVIDIA の NRD を使用しています。
  NVIDIA, the NVIDIA logo and NRD are trademarks and/or registered trademarks of NVIDIA Corporation.
  NRD は NVIDIA RTX SDKs License に従って配布されます（NatureFX の独自エンジン `NFXNative.dll` にオブジェクトコードとして組み込み）。
- **NVIDIA DLSS**: アンチエイリアスとアップスケール（F8 > DLSS）に NVIDIA DLSS を使用しています。
  NVIDIA, the NVIDIA logo and DLSS are trademarks and/or registered trademarks of NVIDIA Corporation.
  DLSS は NVIDIA RTX SDKs License に従って配布されます（NGX ライブラリを `NFXNative.dll` に組み込み、実行時の `nvngx_dlss.dll` / `nvngx_dlssd.dll` を同梱。ライセンス文は `DLSS-LICENSE.txt`）。
