# NatureFX (KSL mod)

Repository: https://github.com/nec093/CarX-Drifting-NatureFX

## 1. 概要

NatureFX は、CarX Drift Racing Online（Unity 2023.2.22f1 / HDRP 16）向けのレイトレーシング・シェーダー総合 Mod です。
照明、空、霧、反射、後処理を、NatureFX の独自エンジン `NFXNative.dll`（DX12、DXR 1.1）とカスタムパスで描画し直します。
HDRP に残す処理は、G バッファー（マテリアルのラスタライズ）、半透明、UI、およびゲーム本体が必要とする処理です。

独自エンジンの各部（空、霧、空の光、太陽とランプの光、照明、反射、ガラス、パーティクル、後処理）は常に有効で、個別に HDRP へ切り替える設定はありません。独自エンジンが使えない環境では NatureFX 全体が動作しません（7 章）。

開発の引き継ぎ事項（構成、既定値、未確認の項目）は [docs/HANDOFF.md](docs/HANDOFF.md) に、今後の作業は [docs/ROADMAP.md](docs/ROADMAP.md) に記載しています。

## 2. 機能

### 2.1 レイトレーシングと照明

以下の機能は独自エンジンで処理するため、DX12 でのみ動作します。

- 太陽と月の影は、シーン全体に対してレイを飛ばして求めます。接地部分では影が鋭く、遮蔽物から離れるほど半影が柔らかくなります。ガラス越しの影は色付きで減衰します。揺れる葉の影も葉に合わせて揺れ、白線などの透明な面にも影が落ちます。
- 太陽とランプの光について、HDRP の太陽、点光源、スポットライトは照明に使用しません。G バッファーから同じ式で求めた光に、独自エンジンの影を付けて加算します。ランプは 1 画素あたり 4 灯まで個別に影を付けます。
- 環境光は、周囲に遮られる割合に応じて空の光を暗くします。GI は跳ね返りの光を扱い、ワールド空間のキャッシュによって多重バウンスに対応します。ネオンや看板などの発光面も周囲を照らします。
- 反射は、濡れた路面、水たまり、塗装、クロームに適用します。画面外の物体も映り込みます。粗い面には独自の空のプローブを使用します。
- ガラスは、透過、映り込み、屈折までを独自エンジンで描画します。対象は手前から 3 枚までです。
- 木漏れ日などの光芒（光の筋）を描画します。
- ノイズ除去には、NVIDIA NRD（SIGMA / REBLUR、既定）または NatureFX のフィルターを使用します。
- レイトレーシングは DXR 1.1（インラインのレイクエリ）で行います。DXR 1.2 による太陽の影はテスト段階です。

### 2.2 空、霧、雲

- 空は、太陽、月、大気から物理ベースで計算します。太陽と月の円盤、星も描画します。HDRP の空、空の光、空の映り込みは停止し、独自の処理に置き換えます。
- 雲は、天気の雲の種類に合わせた体積雲として描画し、地面に雲の影を落とします。
- 霧は独自エンジンによる 3D の霧です。太陽、月、空、ランプ、ヘッドライトによって照らされます。遠景のかすみも扱い、局所の霧とタイヤスモークも霧の中で処理します。霧の中のヘッドライトの光芒の強さは「ヘッドライトの光芒」で調整します（0 で光芒なし）。
- 半透明の物体（柵、看板など）にも霧が掛かります。NatureFX のシェーダーの物体はシェーダー内で、ゲームのシェーダーのままの物体は描き直しで処理します。霧がほとんどない晴天時は、描き直しを省略します。
- 時刻の上書きと時間経過（1〜720 倍）に対応します。ゲームの夜間照明（街灯）は NatureFX の時刻に合わせて切り替えます。

### 2.3 後処理と露出

独自の後処理は既定で有効です。

- 露出は、Fixed（手動で設定した EV、既定 14）と目の順応から選択し、補正値を加えられます。目の順応は中央重点測光で、暗い場面は暗く表示し、閃光には順応しません。
- トーンカーブは、AgX（既定、カメラに近い見え方）、Khronos PBR Neutral、ACES、なしから選択します。コントラスト（既定 8）、彩度、目のホワイトバランス（夜は半分だけ順応）も調整できます。
- レンズ効果として、ブルーム、光源ごとのレンズフレアと光条、被写界深度（車に合焦、F 値指定）、モーションブラー（シャッター角指定）、暗所の見え方（プルキンエ現象、粒状感）を備えています。
- HDRP のトーンマップ、ブルーム、色調整、フレア、ビネットなどと TAA は無効にします。

### 2.4 アンチエイリアスとアップスケール

- NVIDIA DLSS は、DLAA（既定）と、Quality / Balanced / Performance / Ultra perf. のアップスケールに対応します。アップスケール時は、HDRP の最終パスを `NFX_UpscaledFinal` に差し替えて表示します。
- DLSS が動作しない環境（RTX 以外の GPU など）では、独自の TAA を使用します。HDRP の TAA は使用しません。
- Ray Reconstruction は、太陽の影のノイズ除去を DLSS で行う機能です。DLAA のときのみ使用でき、既定では無効です。

### 2.5 天気と雨

- 天気はプリセット（Manual / Clear / Overcast / Rain / Storm / Fog）から選択します。天気の自動遷移にも対応します。
- 雨天時は、雨量に応じて空気中に霧が発生します。雨粒は太陽の影の中や、ヘッドライトと街灯の光の中で光り、着地時にしぶきを上げます。路面は染み込みの段階から水膜と波紋の段階へ進み、時間をかけて濡れ、乾きます。車の水滴と流れる水、タイヤの水しぶきも描画します。
- 雪、落ち葉、砂ぼこり、時間経過による蓄積、季節の色、路面のゴムと摩耗、素材の経年変化（苔、錆、汚れなど）、地面のなじませ、塗る汚れ、デトリタスは廃止しました。

### 2.6 自然、路面、車

- 風によって草と葉が揺れます。突風と葉のはためきも再現し、車は草を押し分けます。画面上の揺れは、マップ設定が `foliageWind = true` のときのみ有効です。RT の影は常に揺れます。
- シェーダーリビルドは、ゲームのマテリアルを NatureFX のシェーダーに置き換える機能です。全マテリアルを置き換えるか、規則に一致するマテリアルのみを置き換えるかを選択でき、草木も対象です。アンチタイリング、路面の細かな凹凸、ドライライン（濡れた路面でタイヤが水を押し出した走行ライン）を追加します。
- 葉と草の色は、実物の葉の反射率（4〜12 %）に合わせて落ち着いた色にします（Weather > 葉の色）。逆光の葉は、実物の葉が通す程度の光だけを透過します。
- マップのプロファイルで素材（アスファルト、コンクリート、石、木、土など）を指定したマテリアルは、カラーマップの平均の明るさを実物の反射率の範囲に合わせます（Shader rebuild > Real albedo、`[materials]` の `albedo=`）。
- ヘッドライトは、実車に準じたロービーム配光（ハロゲン / HID / LED、日本 / 欧米、照射角 110°）を再現し、光軸と色温度を設定できます。ハイビームは F7 で切り替えます。ゲームのヘッドライトは光らせず、NatureFX 専用のライトを同じ位置に置いて照らします（ゲームがアクセル操作に合わせてライトを書き換えても影響を受けません）。
- 遠景の森（交差した板の木）は、カメラから 90 m より近くと、板を真横から見る角度では表示しません。
- 車については、タイヤスモーク（光と影を受ける）、ブレーキの赤熱とアフターファイア、濡れた路面を走行したときの泥汚れ、クロームのホイールを描画します。
- ゲームおよびトラックの半透明パーティクルを、独自エンジンの光と霧で照らします。

### 2.7 マップ拡張

マップ作者は、KN マップまたはマップ外のプロファイルで、次の項目を指定できます。

天候ゾーン、霧、光源、反射プローブ、素材の見た目、発光、3D の草、遠景の森、藪、デカール、水たまり、局所の霧、パーティクル

書式は [docs/map-authoring.md](docs/map-authoring.md) を参照してください。F8 の Map タブで、読み込んだ内容とマーカーの状態を確認できます。

### 2.8 デバッグページ

開発用の情報とツールは、ブラウザーで開くデバッグページにまとめています。デバッグページは既定で有効で、この PC からのみ `http://127.0.0.1:8765/` で開けます。

タブは overview / performance / memory / scene / settings / developer / log です。VRAM の内訳、GPU と CPU の負荷と処理時間、遅いフレーム、シーン、ログを確認できます。右上のボタンで日本語と英語を切り替えます（表示は Noto Sans JP / Noto Sans Mono。オフラインでは Windows の日本語フォント）。

settings タブでは、F8 ウィンドウの全設定を変更できます。

- 左に F8 と同じタブと「変更した設定」の一覧が並び、各行は項目名・操作・既定値の 1 行です。説明は項目名にカーソルを合わせると表示します。
- 既定値から変更した設定は色で示し、「既定値に戻す」ボタンで戻せます。上部の欄で全タブの名前と説明を検索できます。
- 前提の機能が無効な設定は表示しません（その件数を上部に表示します）。
- 変更はすぐに保存します。項目の一覧と条件は `tools/gen_settings_controls.py` が F8 ウィンドウのコードから生成し、日本語の文言は `tools/settings_ja.json` にあります。

developer タブには、RT の確認用表示（影・正確な太陽光・GI・アルベドチェック）、What is this?（クリックした物体の情報）、シェーダーの再読み込み、テクスチャーの予算、シェーダーのダンプ、差し替えルールの再読み込み、バリアントの記録、マテリアル一覧、CPU と GPU の計測があります。比較用の切り替えスイッチはありません。

## 3. インストール

ゲームのフォルダー（`C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online`）に、次のファイルを配置します。

| ファイル | 配置先 |
|---|---|
| `NatureFX.ksm` | `kino/mods/` |
| `naturefx.bundle`（シェーダーバンドル） | `kino/mods/NatureFX/` |
| `NFXNative.dll`、`nvngx_dlss.dll`、`nvngx_dlssd.dll`、`DLSS-LICENSE.txt` | `kino/mods/NatureFX/` |
| `D3D12Core.dll`（Agility SDK 1.619、DXR 1.2 のテスト用） | `kino/mods/NatureFX/D3D12/` |

- NatureFX は DX12 と DXR 1.1 対応の GPU（RTX 20 / RX 6000 / Arc 以降）が必要です。Steam の起動オプションに `-force-d3d12` を指定してください。DX11 で起動した場合は、DX12 で再起動するボタンを表示します。
- `.ksm`、DLL、シェーダーバンドルは同じバージョンのものをそろえて配置してください。いずれか 1 つでも古い場合、表示が崩れることがあります。
- ゲームの起動中は DLL を上書きできません。また、シェーダーバンドルは起動中に上書きすると、破損した状態で読み込まれます。コピーはゲームを終了してから行ってください。

## 4. ビルド

| 対象 | コマンド | 出力先 |
|---|---|---|
| Mod 本体（C#、net472） | `dotnet build src/NatureFX/NatureFX.csproj -c Release` | `kino/mods/NatureFX.ksm`（maykr と署名キーがある場合に自動生成） |
| シェーダーバンドル | `bash tools/build_bundle.sh <Unity プロジェクト>`（Git Bash） | `kino/mods/NatureFX/naturefx.bundle` |
| 独自エンジン（C++ / D3D12） | `powershell -File tools/build_native.ps1` | `kino/mods/NatureFX/NFXNative.dll` ほか |

### 4.1 Mod 本体

[ksl_sdk の Releases](https://github.com/trbflxr/ksl_sdk/releases) から取得した `KSL.API.dll` と `0Harmony.dll` を `lib/` に、`maykr.exe` を `tools/` に配置します（`lib/README.md` を参照）。Unity と HDRP の DLL は、インストール済みのゲームから参照します。

ゲームが別の場所にある場合は、`-p:GameDir="D:\SteamLibrary\steamapps\common\CarX Drift Racing Online"` を指定します。

`.ksm` の署名キーは、リポジトリ直下の `NatureFX_maykr.kmc` です。このファイルはコミットしません。

`-p:DeployToGame=true` を指定すると、ビルドした .dll をそのまま `kino/mods` にコピーします。この場合は KSL の開発者モードが必要です。

### 4.2 シェーダーバンドル

Unity 2023.2.x（2023.2.20f1 で動作確認済み）で空のプロジェクトを作成し、`Packages/manifest.json` に `"com.unity.render-pipelines.high-definition": "16.0.6"` を追加します。Windows の「コントロールされたフォルダーアクセス」が有効な場合は、Documents の外にプロジェクトを作成してください。

ビルドスクリプトはソースをコピーし、Unity をバッチモードで起動して、シェーダーの生成、シェーダーバンドルの作成、ゲームへのコピーまでを行います。ログは `<プロジェクト>/Logs/naturefx_build.log` に出力されます。

ゲームの起動中にビルドした場合、シェーダーバンドルは `kino/mods/NatureFX/reload/` に別名で配置されます。このファイルは、デバッグページの Reload shaders で再読み込みできます。

バリアントの絞り込みについては `unity/variants/README.md` を参照してください。

### 4.3 独自エンジン

ビルドには、Visual Studio（vcvars64）、Windows SDK の dxc（SM 6.6 以降）、Unity エディターの `PluginAPI` ヘッダー、および `%USERPROFILE%\NatureFX-Deps` に配置した NRD、DLSS、Agility SDK 1.619、dxc 1.9 を使用します。詳細は `AGENT.md` を参照してください。

`-ShadersOnly` を指定すると、シェーダーのみを `NativeShaders/*.cso` に出力します。この場合、ゲームを再起動せずにシェーダーを再読み込みできます。

### 4.4 NatureFX のシェーダー

NatureFX のシェーダー（`NatureFX/Foliage`、`NatureFX/LitAccumulation`）は、インストールされている HDRP/Lit をコピーし、風、濡れ、アンチタイリングなどの処理を追加したものです。ビルドのたびに `Editor/GenerateNatureFXShaders.cs` が生成します。

プロパティ名、キーワード、パスは HDRP/Lit と同一です。そのため、シェーダーのみを差し替えても、既存のテクスチャーと設定をそのまま利用できます。

## 5. 使い方と設定

### 5.1 操作

- F8 キーで NatureFX のウィンドウを開きます。Esc キーまたは F8 キーで閉じます。Ctrl+F で設定項目を検索でき、項目を右クリックすると既定値に戻ります。
- F7 キーでハイビームを切り替えます。
- F9 キーで写真モードに入ります。カメラを止めている間、RT を最高品質で積み上げて 1 枚の画像にします。
- KSL メニュー > NatureFX では、オン / オフの切り替え、風と濡れの簡易設定、シェーダーのダンプ、ルールの再読み込みを行えます。

### 5.2 F8 ウィンドウのタブ

| グループ | タブ | 主な設定 |
|---|---|---|
| GENERAL | Overview | ユーザープリセット（保存、読み込み、1 行のコードによる共有）、全設定の既定値への復帰 |
| LIGHT & SKY | Sun & time | 時刻、時間経過、太陽の向き・高さ・強さ・色温度、月、ゲームの夜間照明 |
| | Sky & fog | 独自エンジンの空・照明の状態、雲・星、遠景のかすみ、ヘッドライトの光芒、光の筋 |
| | Lighting & shadows | 照明の状態、RT の太陽の影（強さ・解像度・ガラス・揺れる葉・ランプごとの影・太陽の大きさ・葉の透過）、発光面の照明、ゲームのパーティクル |
| | Ambient light & GI | 環境光・GI（距離・強さ）、日陰の空の光の強さと色、コンタクトシャドウ |
| | Reflections & glass | RT の反射（強さ・粗さ・距離・品質）、ガラスの屈折、濡れた路面の映り込みの伸び |
| CAMERA | Exposure & tone | ブルーム、露出（Fixed / 目の順応）、トーンカーブ、コントラスト・彩度、ホワイトバランス |
| | DLSS | DLSS のオン / オフ、DLAA / アップスケールの品質、Ray Reconstruction |
| | Lens & eye | 暗所の見え方、レンズフレア・光条、被写界深度、モーションブラー |
| WORLD | Weather | 天気のプリセット、天気の遷移、葉の色、雨、手動設定値（雨と濡れ） |
| | Nature | 風、葉のはためき、揺れ、車による草の押し分け |
| | Road | ドライライン、路面の細かさ |
| | Cars | ヘッドライト、塗装、雨天時の車、スモーク・赤熱・泥 |
| | Map | マップのプロファイルと読み込んだ内容、マーカーの表示、各効果の状態 |
| ADVANCED | Ray tracing | トレースの解像度、ノイズ除去、DXR 1.2 のテスト、RT のコスト計測 |
| | Shader rebuild | 置き換え（全マテリアル・草木）、アンチタイリング、マップの素材の実測反射率への補正、置き換えなかったマテリアル |
| | Settings | ウィンドウの外観、Graphics API（DX12 必須） |
| | Developer | デバッグページのオン / オフと URL のみ（開発用のツールはデバッグページへ移行済み） |

### 5.3 設定ファイル

設定は KSL の設定として自動的に保存されます。そのほかに使用するファイルは次のとおりです。

| ファイル | 内容 |
|---|---|
| `kino/config/NatureFX.rules.txt` | シェーダー差し替えのルール（初回起動時に生成） |
| `kino/config/NatureFX/maps/<シーン名>.nfx` | マップ外のマッププロファイル（マップ内の `nfx_profile` より優先） |
| `kino/config/NatureFX/presets/` | F8 Overview で保存したプリセット |
| `kino/output.log` | NatureFX のログ（`[NatureFX]` で始まる行） |

### 5.4 シェーダー差し替えルール

ルールは次の形式で記述します。

```
<ゲームのシェーダー名 正規表現> | <名前 正規表現> => <NFX シェーダー名>
```

- 区切り記号は、前後に空白を置いた ` | ` です。空白を含まない `|` は、正規表現の「または」として扱われます。
- `<名前 正規表現>` は、マテリアル名と、そのマテリアルを使用する各オブジェクトの階層パス（最大 3 階層。例: `Track/Road/main_LOD0`）の両方に照合します。
- CarX のマテリアル名の多くは、`0x4C6A925E` のような番号です。このため、オブジェクト名で指定する方が確実です。
- マテリアルは複数のオブジェクトで共有されます。一致するオブジェクトが 1 つでもあれば、そのマテリアルを使用するすべてのオブジェクトが変更されます。
- 「Every material（full replacement）」が有効な場合、ルールに含まれないマテリアルも汎用の変換で置き換えます。

次の例は、道路と柵に濡れの表現を追加するルールです。

```
(?i)^HDRP/Lit$ | (?i)(road|asphalt|concrete_barrier) => NatureFX/LitAccumulation
```

デバッグページの developer タブ、または KSL メニューの Tools で Dump scene shaders を実行すると、`kino/config/NatureFX_dump_*.txt` が出力されます。このファイルには、読み込まれているシーンについて、シェーダーごとのプロパティ、マテリアルの使用数、オブジェクトの階層パスが記録されます。ルールを修正した後、Reload replacement rules で再適用します。

## 6. フォルダー構成

```
├─ README.md / AGENT.md        概要 / 作業者向けの指示
├─ docs/                       HANDOFF.md（現状と引き継ぎ）、ROADMAP.md（残りの作業）、map-authoring.md（マップの書式）ほか
├─ lib/                        KSL.API.dll, 0Harmony.dll（ksl_sdk から取得、gitignore）
├─ src/NatureFX/               Mod 本体（C#, net472）
│  ├─ NatureFXMod.cs           エントリポイント（BaseMod）、毎フレームの更新、HDRP の機能の切り替え
│  ├─ NatureFXWindow.cs        F8 のウィンドウ / SettingsUI.cs は KSL メニュー
│  ├─ Settings.cs              設定と既定値（移行）/ UserPresets.cs
│  ├─ Rendering/               独自エンジンとの連携（NativeEngine）、RT のパス、空・霧、OwnLighting、後処理、DLSS、車・ヘッドライト・雨
│  ├─ Nature/                  風、濡れ・天気、シェーダーバンドル、マテリアル置き換え、草・森・藪、ドライライン
│  ├─ Map/                     マッププロファイルとマーカー、天候ゾーン、霧・光源・プローブ・デカール・水・パーティクル
│  ├─ Rebuild/                 シェーダーリビルドの汎用変換
│  ├─ Tools/                   デバッグページ、計測、ダンプ類
│  └─ Ui/                      ウィンドウの部品
├─ native/
│  ├─ NFXNative/               独自エンジン（C++ / D3D12、src/ と shaders/）
│  └─ Dxr12Probe/              DXR 1.2（SER / OMM）の動作確認
├─ unity/
│  ├─ Assets/NatureFX/         シェーダーバンドルのソース（Editor/、ShaderLibrary/、Shaders/）
│  └─ variants/                バリアント絞り込み用のデータ
└─ tools/                      build_bundle.sh, build_native.ps1, build_dxr12_probe.ps1, kino_sdk/（藪の素材を焼くスクリプト、Poly Haven 素材の取り込み）, maykr.exe（gitignore）
```

## 7. 既知の制限

- NatureFX は DirectX 12 専用です。DX11 で起動した場合は何も変更せず、DX12 での再起動を案内する表示だけを出します。Steam の起動オプションに `-force-d3d12` を指定してください。
- 独自エンジン（DXR 1.1）が使えない場合（RTX 20 / RX 6000 / Arc 以前の GPU、NFXNative.dll がない、前回シーン構築中にクラッシュした）も、NatureFX は何も変更せず、理由を表示します。動作中に独自エンジンが止まった場合は、NatureFX 全体をまとめて無効にし、ゲームを再起動するまで使いません。一部の描画だけが HDRP に戻ることはありません。
- DLSS は RTX の GPU でのみ動作します。その他の GPU では独自の TAA を使用します。
- ゲームのアップデートでシェーダー名が変更された場合は、差し替えルールの更新が必要です。
- 未解決の項目と、ゲーム内で未確認の項目は [docs/HANDOFF.md](docs/HANDOFF.md) に記載しています。

## 8. 使用しているサードパーティ SDK

### 8.1 NVIDIA Real-Time Denoisers (NRD)

レイトレーシングによる影、環境光、GI、ランプの影、反射のノイズ除去に、NVIDIA の NRD を使用しています。

NVIDIA, the NVIDIA logo and NRD are trademarks and/or registered trademarks of NVIDIA Corporation.

NRD は NVIDIA RTX SDKs License に従って配布しています。NRD はオブジェクトコードとして NatureFX の独自エンジン `NFXNative.dll` に組み込まれています。

### 8.2 NVIDIA DLSS

アンチエイリアスとアップスケール（F8 > DLSS）に、NVIDIA DLSS を使用しています。

NVIDIA, the NVIDIA logo and DLSS are trademarks and/or registered trademarks of NVIDIA Corporation.

DLSS は NVIDIA RTX SDKs License に従って配布しています。NGX ライブラリを `NFXNative.dll` に組み込み、実行時に使用する `nvngx_dlss.dll` / `nvngx_dlssd.dll` を同梱しています。ライセンス文は `DLSS-LICENSE.txt` に収録しています。
