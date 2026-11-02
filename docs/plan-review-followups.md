# レビュー後の 7 項目: 調査結果と設計メモ（2026-11-02）

`docs/review-2026-11-02.md` で未修正としたもののうち、ユーザーが詰めたい 7 項目の調査。**実装は別セッションが行う**前提で、根拠のコード位置・具体案・リスク・確認方法を書く。すべてコードリーディング（HDRP 16.0.6 のソースは `C:\Users\kppn1223\Desktop\kino_content_sdk\Library\PackageCache` を参照）。ゲーム内は未確認。

目次
1. 夜の GI のクランプ基準
2. ライトの単位（HDRP 16 の `SetIntensity`）
3. タイヤスモークの CPU / GPU コスト
4. 3D 霧のローカルライト総当たり
5. ジオメトリごとのコミット済みリソース
6. 各パスの画面コピーの共有
7. トレースバッファのストライド削減

着手の順（効果 ÷ 手間）: 6 → 1 → 2 → 7 → 4 → 3 → 5。6・1・2 は C# と HLSL の小さな変更（DLL は 1 だけ）。7・4 は DLL とバンドル（ブリッジは数分）。5 はネイティブの大きな変更。3 は見た目の調整を伴うのでユーザーと相談しながら。

---

## 1. 夜の GI のクランプ基準

### 現状

`native/NFXNative/shaders/GI.hlsl` 85 行目:
```hlsl
// a bright dot (one ray onto a lamp or a sunlit patch) is capped at about a sunlit white surface
float limit = 2.0 * (Luminance(SunColor) * INV_PI + Luminance(SampleSH9(float3(0.0, 1.0, 0.0))));
...
bounce = ClampBright(Outgoing(s, hitAWS, dir), limit) * (1.0 - fade);
```
`native/NFXNative/shaders/NFXCommon.hlsli` 766 行目:
```hlsl
// The scale the accumulated sums are stored with: about 1000 steps for the sunlit level of the scene
float CacheScale()
{
    float reference = Luminance(SunColor) * INV_PI + Luminance(SampleSH9(float3(0.0, 1.0, 0.0)));
    return 1024.0 / max(reference, 1e-3);
}
```
`CacheAdd`（同 806 行付近）は 1 サンプルを `min(irradiance * scale, 65535)` で丸めて 32 bit 整数の和に足す。`GICacheResolve.hlsl` は `mean = sum / (samples * CacheScale())`。

- `SunColor` は `RTShadowPass.SunIlluminance`（lux、実単位。`Units.PhysicalScale` 適用後）。`SampleSH9` は HDRP の環境光プローブ、または独自の空の SH（`NativeEngine.SkyProbe`、昼で数千〜1 万 lux/π 相当、夜は月の空で 0.0x）。
- 夜（`SunColor` = 月 ≈ 0.25 lx、空 SH ≈ 0.01）: `reference ≈ 0.09`、`limit ≈ 0.18`、キャッシュの 1 サンプル上限 = 65535 / (1024 / 0.09) ≈ 5.8（照度 lx）。
- ヘッドライト（数千 cd、距離 5 m で 100〜300 lx）に照らされた路面からの跳ね返り `Outgoing` は数十、`DirectLight(s, hitAWS)` は 100 以上 → `limit` で 1/100〜1/500 に切られ、キャッシュにも 5.8 lx までしか入らない。
- 昼は `reference` が 3 万 lx 前後なので上限は 200 万 lx: 影響なし。

つまり「夜はランプの跳ね返りがほぼ消える」は仕様の帰結で、ランプを基準に入れていないことが原因。

### 案

**A. 基準にランプを入れる（推奨）**
- C# で毎フレーム「カメラ周辺で最も強いランプの照度の目安」`lampRef` を出し、空いている定数 `LightCount.w`（`NFXCommon.hlsli` 32 行目 `float4 LightCount; // x lights in use; y aerosols, z ozone`、C# は `NativeEngine.cs` 706 行目 `c[25] = aerosols_; c[26] = 1.0f;` で w = c[27] は未使用）で渡す。
- `lampRef` の出し方（`LocalLights.Update`、`LocalLights.cs` 83〜123 行目のループ内で `color_` を詰めるところ）: 各ライトについて `E = max(color.r, color.g, color.b) / max(1.0, (0.5 * range)²)`（範囲の半分の距離での照度、cd ÷ m²）。`lampRef = max(E)`、無ければ 0。範囲は `pos_[i].w` に入れている `light.range` そのもの。エミッシブ（`w = 2.0` の行）も同じ式で。
- HLSL: `GI.hlsl` 85 行目と `CacheScale()` の `reference` を `max(Luminance(SunColor) * INV_PI + Luminance(SampleSH9(up)), LightCount.w * 0.5)` に。`0.5` は「ランプの跳ね返りは照度の半分程度まで」の係数（白い壁で albedo 0.8 × 照度 ÷ π ≈ 0.25 倍なので余裕を持って 0.5）。`limit` は 2 倍のまま。
- 整合性: `CacheAdd` と `GICacheResolve` は同じフレームの同じ定数で `CacheScale()` を評価するので、`lampRef` がフレームごとに変わっても「足した値 ÷ 同じスケール」で割り戻せる。ただし **和を複数フレームにまたいで溜めたまま**（`count < 64` でリゾルブ前に次フレームの `CacheAdd` が同じセルに足す）ところが変わると、古い和が新しいスケールで割られてずれる。`GICacheResolve` は毎フレーム全セルを「count > 0 ならリゾルブして和を 0 に」するので（`GICacheResolve.hlsl` 22〜41 行目）、和は 1 フレームしか生きない → 問題なし。
- 変更範囲: `LocalLights.cs`（`LampReference` プロパティ）、`NativeEngine.cs` `Issue()`（`c[27] = lights_?.LampReference ?? 0`）、`GI.hlsl`、`NFXCommon.hlsli`。DLL の再ビルドが要る（`-ShadersOnly` で Reload shaders でも試せる）。.ksm も（C#）。

**B. 固定スケール + サンプル上限の拡大**
- `CacheScale` を定数（例 1024 / 20 lx = 51 段/lx）、1 サンプル上限を 2^24 / 64（32 bit の和に 64 サンプルが収まる）に。
- 却下理由: 昼の太陽直射（100 000 lx）を 51 段/lx で足すと 1 サンプル 5.1M → 上限 262k で頭打ち（太陽の跳ね返りが切られる）。逆に 512 段/lx にすると上限 32k lx で昼が壊れる。6 桁の明るさ差を固定スケールでは持てない。

**C. 対数エンコードの和**（和を float の代わりに「log2 を 16 bit 固定小数」で持つ）: 足し算が線形にならないので平均が取れない。却下。

**D. 和を float に**（`InterlockedAdd` の float 版は D3D12 標準にない。NV の `NvInterlockedAddFp32` は拡張）。却下。

### 確認方法
- 夜、ヘッドライトで白い壁を照らし、F8 で GI をオン/オフ。壁の横の地面や車体下への色のにじみが昼と同じ程度に出るか。
- 昼の見た目が変わらないこと（`lampRef` が昼の基準を超えない: 昼は `reference` ≈ 3 万なので超えない）。
- 街灯の下（2 000 lm ≈ 160 cd、range 20 m → E ≈ 1.6 lx）: `lampRef` 0.8 → 1 サンプル上限 ≈ 51 lx。十分。

---

## 2. ライトの単位（HDRP 16 の `SetIntensity`）

### 調べたこと（HDRP 16.0.6 `Runtime/Lighting/Light/HDAdditionalLightData.cs`）

- `HDAdditionalLightData.intensity`（`m_Intensity`、154 行目）は **そのライトの `lightUnit` の単位**の値。セッターが `UpdateLightIntensity()` を呼ぶ。
- `UpdateLightIntensity()`（3087 行目）が **レガシー `Light.intensity` に内部単位で書く**: 平行光は lux、点光源・スポットは **candela**（`SetLightIntensityPunctual` 3033 行目: `Candela` ならそのまま、`Lumen` なら `ConvertPointLightLumenToCandela` / スポットの反射鏡換算、`Lux` は `ConvertLuxToCandela(m_Intensity, luxAtDistance)`、面光源は luminance、`Ev100` は `ConvertEvToLuminance`）。
- `lightUnit` のセッター（306 行目）は単位変換して `m_Intensity` を書き換え、`UpdateLightIntensity()`。`SetIntensity(float, LightUnit)`（3438 行目）= `lightUnit = unit; intensity = value;`。
- ライトループは `VisibleLight.finalColor`（= `Light.color` × レガシー `Light.intensity`）を読む（`LightLoop.cs` 975 行目、`HDGpuLightsBuilder.Jobs.cs` 132 行目 `GetLightColor`）。**描画に使われるのはレガシー側（candela）**で、`m_Intensity` / `lightUnit` は編集用の値。
- レガシー側が `m_Intensity` から書き直されるのは `UpdateLightIntensity()` が走るときだけ: `intensity`/`lightUnit`/`luxAtDistance`/`enableSpotReflector` 等のセッター、`UpdateAllLightValues()`（形状系のセッター、`Awake` 3120 行目、`OnEnable` 3831 行目）。毎フレームではない。

### 結論（前回 R16 の見直し）

- `ParticleLighting.cs` 85・92 行目と `LocalLights.cs` 97 行目が `light.intensity` を cd として使うのは **正しい**（点光源・スポットのレガシー値は常に cd）。`MapLights`（lumen で `SetIntensity`）も HDRP が cd に換算してレガシーに書くので、雨・煙・葉の照明に単位のずれはない。**R16 の「街灯が 12 倍明るい」は起きない。**
- `HeadlightController.cs` 189 行目 `hd.SetIntensity(t.Candela * …, LightUnit.Candela)` → 253〜261 行目 `Apply` が `light.intensity = o.Intensity`（レガシー、cd）で戻す。描画はこれで元に戻る。残る不整合は `hd.lightUnit`（Candela のまま）と `hd.m_Intensity`（NatureFX の値のまま）。その後 `Awake`/`OnEnable`（車の再有効化）や形状系のセッターが走ると `UpdateLightIntensity()` が NatureFX の値をレガシーに書き戻す → **「ヘッドライトを game に戻したのに、車を乗り換えると NatureFX の明るさになる」**が起きうる。
- ゲーム側（`OriginLightObject.SetLightIntensity`、逆コンパイル。今回のセッションには無いが前回の確認結果）はレガシー `light.intensity` を直接書く。`UpdateLightIntensity()` が走ればそれも `m_Intensity` で上書きされるが、それは NatureFX が触る前から同じ。

### 提案（小さい）

- `HeadlightController.Original`（33 行目）に `Unit = hd.lightUnit` と `HdIntensity = hd.intensity` を足し、`Apply`（253 行目）で `hd.lightUnit = o.Unit; hd.intensity = o.HdIntensity;`（この順。`lightUnit` セッターは換算するので、先に単位を戻してから値を書く）に続けて今までどおり `light.intensity = o.Intensity`。これで `UpdateLightIntensity()` がいつ走ってもゲームの値に戻る。
- `ParticleLighting` の「ランキング `intensity / d²`」は cd 基準で正しいので変更不要。コメントに「レガシー intensity は HDRP が cd で持つ」と書いておく（`LocalLights.cs` 97 行目にはある）。
- 確認: Headlight type を 3 → 0 に戻し、車を乗り換えても明るさが元のままか。

---

## 3. タイヤスモークの CPU / GPU コスト

### 現状（`src/NatureFX/Rendering/TireSmoke.cs`）

毎フレーム、煙の粒子がある間（`Update` 549 行目 → `UpdateShadows` 315 行目）:

| 処理 | 場所 | 量 |
|---|---|---|
| `particles_.GetParticles(buffer_, count)` | 326 | 最大 12 000 粒子 × `ParticleSystem.Particle`（約 120 B）のコピー |
| `GetCustomParticleData` / `SetCustomParticleData` | 327 / 375 | `List<Vector4>` 12 000 |
| 奇数フレーム `UpdateSmokeVolume` | 167〜230 | 粒子ごとに `(2rc+1)³` セル（size 4.8 m × 0.4 → r 1.9 m、rc 2 → 125 セル）→ 12 000 × 125 = 150 万回の加算、`Texture3D.SetPixelData`（48×24×48 × 4 B = 221 KB）＋ `fogSmoke_` 13 827 要素の詰め直し |
| 偶数フレーム `UpdateSmokeShadow` | 243〜306 | 粒子ごとに `(2rc+1)²`（r 2.4 m ÷ 0.4 → rc 6 → 169 セル）→ 200 万回、`smokePixels_` 16 384 要素の `SetPixels`（RGBAFloat 256 KB） |
| レイキャスト | 342〜351 | `ShadowRaysPerFrame = 2000` 本、300 m、全コライダー、`ScheduleBatch(...).Complete()` で**同期待ち** |
| 粒子ごとの `SmokeTransmittance`（`exp` ×4） | 352 付近 | 2 000 回 |

発生量（`EmitParticles` 477 行目、`Update` 597〜604 行目）: 1 回 8 個（しぶき 4）、間隔 `SpawnInterval 0.07 / 2 = 0.035 s ÷ amount` → 1 輪 229 個/秒、4 輪で 900 個/秒、寿命 4.5〜7.5 s → 同時 5 000〜6 000 個/台（上限 12 000）。

GPU（`NFX_Particle.shader` mode 5 の煙）: `renderer.maxParticleSize = 4.0f`（464 行目）= 画面の 400 % まで許す。近い煙はフルスクリーン級のソフトパーティクルで、1 画素ごとに深度の Load、ノイズ 3 タップ（246〜248 行目）、ランプ 8 灯のループ（128〜145 行目、各 `NFX_SmokeLampTransmittance` = 3D テクスチャ）、`NFX_Fog`（172 行目）。重なりが 20〜50 枚になる。

### 案（効果の大きい順）

1. **CPU のラスタライズを Burst ジョブに**（`UpdateSmokeVolume` / `UpdateSmokeShadow`）: 配列は既にフラット（`volume_`、`smokeTau_`…）なので `IJobParallelFor`（粒子ごと、`NativeArray<float>` に `Interlocked` 加算は Burst でできない → セルごとのジョブに裏返す: 各セルが近傍の粒子リストを走査。粒子を 4 m 格子にビニングして候補を絞る）。想定 3〜8 ms → 1 ms 未満。`GetParticles` のコピーは避けられないが、粒子数を減らせば比例して減る。
2. **描画は近い N 個だけでマップを作る**: `buffer_` をカメラ距離で並べ替えず、距離 60 m 以上の粒子はマップに入れない（影も霧もカメラ近傍にしか効かない）。
3. **レイキャストを非同期に**: `ScheduleBatch` の `Complete()` を次のフレームの先頭に（`JobHandle` を持ち越す。結果は 1 フレーム遅れでよい）。本数は 2 000 → 1 000（2 フレームで一巡）。
4. **粒子数**: 1 回 8 個 → 4 個、大きさ 1.3 倍、`maxParticles` 12 000 → 6 000。見た目の分解能はユーザーが「足りない」と言った経緯があるので要相談（代わりにシェーダーの billows ノイズを細かく）。
5. **`maxParticleSize` 4.0 → 0.8**（画面の 80 %）: 至近の煙がフルスクリーンにならない。カメラが煙に入ったとき「切れる」ように見えるなら 1.5。
6. **半解像度で描いて合成**: HDRP の「Low resolution transparent」（`HDAdditionalCameraData`/マテリアルの `_RenderQueueType` LowTransparent、`Rendering pass = Low resolution`）が使える。マテリアルのレンダーキューを `LowTransparent` に、ソフトパーティクルの深度は HDRP が半解像度深度を用意する。NatureFX のシェーダーは HDRP の `Lit` 系ではないので、`CustomPass` で自前の半解像度ターゲットに描いて合成する方が確実（`AtmospherePass` と同じ仕組み、1 枚の RGBA16F 半解像度 + アップサンプル）。
7. シェーダー: ランプ 8 灯のループを「距離 ÷ range で 4 灯まで」に。`NFX_SmokeLampTransmittance` の 3D テクスチャ参照をランプごとでなく、煙の濃さ `volume_` を 1 回読んで全灯に流用。

### 見積もり
- 1〜3 で CPU 側は 1 ms 前後に。5 で GPU は至近の最悪ケースが数分の 1。6 は品質と相談。
- 確認: F8 の「shadowed 粒子数」、`Time.deltaTime`、ドリフト中の fps（4 台）。

---

## 4. 3D 霧のローカルライト総当たり

### まず前提の訂正

`LocalLights.cs` 63〜71 行目のゲート `Vector3.Distance(light.position, eye) - light.range < 60` により、cbuffer に乗る最大 128 灯は**すべて「範囲球がカメラから 60 m 以内に届く」ライト**（中心はカメラから 80〜100 m 以内）。1 km のコースの街灯のうち霧に入るのは手前の 20〜30 灯で、遠くの街灯は霧に入らない（これは別の設計上の制限）。128 灯になるのはマルチプレイで車が多い（1 台 8〜12 灯）か市街地マップ。そのとき全灯がカメラの近くにあり、画角内のものは画面上でとても大きい（角半径 10° 以上）。→ 「列ごと」の間引きより「タイル × 奥行き帯」が合う。

### 現状（`native/NFXNative/shaders/FogVolume.hlsl`）

- 格子 160 × 90 × 64 = 921 600 セル（14〜15 行目）。スライス深度 `0.5 × 6000^(s/64)` m: s=16 で 4.4 m、32 で 38.7 m、48 で 341 m。
- 起動は `NativeEngine.TraceFogVolume`（1448 行目）→ `Dispatch((160+7)/8, (90×64+7)/8)`、`[numthreads(8,8,1)]`。`id.y = y + s × 90`。90 は 8 の倍数でないので 8 行のグループがスライス境界をまたぐ。
- セルごと（109〜181 行目）: 列 3 float4 読み → `BoxesSigma`（箱 64 まで、各 5 float4）→ `sigma > 0` のとき太陽へのレイ 1 本（`Blocked`、近傍はアルファテスト付き）、空 SH、**ライトループ 147〜172 行目**:
  ```hlsl
  uint count = min((uint)LightCount.x, (uint)NFX_MAX_LOCAL_LIGHTS);
  for (uint i = 0; i < count; ++i) {
      float3 d = LightPos[i].xyz - p; float range = LightPos[i].w; float raw = dot(d, d);
      if (raw > range * range) continue;          // 唯一の早期打ち切り
      ... window / spot（NFX_BeamPattern: atan2 ×2, exp ×3）/ SmokeTransmittance（4 段）/ FogPhaseMix（pow ×2）
  }
  ```
  ループ変数 `i` はウェーブ内で一様 → cbuffer 読みはブロードキャスト（安い）。**この一様性を壊さないことが設計の要点**（レーンごとのアドレスは直列化される）。
- Surface バッファ（921 600 float4 = 14.7 MB）: 列 0〜43 199（3 float4/列）、箱 43 200〜43 519（64 × 5）、煙 43 520〜57 346（3 + 13 824）、**57 347 以降は空き**。
- cbuffer（`NFXCommon.hlsli` 18〜44 行目）: `LightCount`（x 灯数、y エアロゾル、z オゾン、**w 空き**）、`LightPos/LightColor/LightDir/LightSpot[128]`。`constants_ = new float[2100]` = 8 400 B + 48 = 8 448 = `kConstantSlot` ぴったり → **ライトごとの新配列を cbuffer に足す余地はない**（本案は増やさない）。

### 最悪ケースの作業量（128 灯）
- 判定部: 921 600 × 128 = 118 M 反復（LightPos 読み + sub + dot + cmp ≈ 8〜10 命令）≈ 1.0〜1.2 G 命令。
- 範囲内の本体: 1 ペア 60〜180 命令。全灯が 100 m 以内なので範囲内率は 10〜20 % → 12〜24 M ペア ≈ 1.2〜2.4 G 命令。
- 合計 2.5〜3.5 G 命令 → 実測 1〜2 ms と見込む。典型（25 灯）は 0.2〜0.4 ms で、**太陽レイ（921 600 本/フレーム）のほうが重い**。現状 F8 に霧の時間は出ない（`RTTimings.cs` の Stages に fog がない）ので、**先に計測を足す**。

### 選択肢と推奨
- (a) 列ごとのリスト: マスクがレーンごとに違い `i` が発散 → cbuffer 読みが直列化（最大 32 倍）。ライトの角半径（≥ 10°）≫ 列幅（0.56°）で精度の意味もない。不採用。
- **(b) タイル 8 × 8 列 × 奥行き 4 帯の 128 bit マスク（推奨）**: タイル = スレッドグループなのでマスクはグループ内でほぼ一様。リスト = uint4 1 個。帯 4（16 スライスずつ、境界 id.y = 1440 / 2880 / 4320 は 8 の倍数なので帯はグループを割らない）。20 × 12 × 4 = 960 float4 = 15 KB を Surface の空きに。構築は `PrepareFogVolume` の各グループが 64 スレッドで 128 灯を判定、groupshared に `InterlockedOr`。追加ディスパッチなし、上限なし、結果はビット一致（除外されるのは全セルで `raw > range²` のライトだけ）。
- (c) セルごとの奥行き早期打ち切り: 既存判定と同コスト。効果なし。
- (d) C# 側の視錐台カリング: `lights_` は影・反射・LampMask と共有（`_NFX_LampPos`、LampMask の index）なので霧専用の詰め直しが要る。(b) に包含される。不採用。

### 数字（(b) 後）
- 構築: 240 グループ × 128 灯 = 30 720 判定 + 960 float4 書き → 0.02 ms 未満。
- 典型（20〜30 灯）: 帯 3（> 341 m）0 灯、帯 0〜2 で平均 6〜8 灯 → 約 5 M 反復（現状 23 M、約 4.5 倍減）。
- 最悪 128 灯: 帯 0/1/2 ≈ 30/50/40 灯 → 27.6 M（現状 118 M、約 4.3 倍減）、0.5〜1 ms 相当の削減見込み。範囲内の本体（BeamPattern・pow）は残る: それを削るなら別件（`FogPhaseMix` の pow の近似、ヘッドライトの BeamPattern の事前計算）。

### 変更箇所

**`NFX_NativeBridge.compute`**（323 行目 `_NFX_FogCamDelta` の後）:
```hlsl
float4 _NFX_FogLampPos[128];   // xyz absolute position, w range (m)   ← LocalLights.Positions
float4 _NFX_FogLampParams;     // x lights in use
#define FOG_TILES_X 20
#define FOG_TILES_Y 12          // ceil(90 / 8): the last row holds 2 rows of columns
#define FOG_BANDS 4
#define FOG_LIST_BASE (FOG_W * FOG_H * 4)   // 57600: after the columns, the boxes and the smoke
groupshared uint fogMask_[FOG_BANDS * 4];
```
`PrepareFogVolume`（331〜348 行目）を置き換える。要点: **早期 return をバリアの前に置かない**（`valid` で囲む。タイル行 11 は行 88・89 しかない）。列の 3 float4 を書いた後、タイルの楔（4 隅のレイから 4 枚の平面、`centre` との符号で向きを揃える）を作り、`for (l = gi; l < count; l += 64)` で各ライトの球（半径 + 0.5 m の余裕）を 4 平面と、`dz = dot(forward, L)` を各帯の `[FogSliceDepth(16b), FogSliceDepth(16b+16))` と判定して `InterlockedOr(fogMask_[b*4 + (l>>5)], 1u << (l&31))`。最後に `gi < 4` のスレッドが `_NFX_NativeSurface[FOG_LIST_BASE + tile*4 + gi] = asfloat(uint4(...))`。`FogSliceDepth` は 325〜328 行目に既存。楔の座標はカメラ相対（`GetAbsolutePositionWS(0)` = `_WorldSpaceCameraPos`、エンジンの `Camera` と同じフレーム）。ディスパッチは現状の `(20, 12)` = タイル数のまま。

**`FogVolume.hlsl`**: 定数に `FogTilesX = 20, FogBands = 4, FogListBase = FogW*FogH*4` を追加。147〜148 行目を
```hlsl
uint4 mask = LightCount.w > 0.5
    ? Surface.Load4((FogListBase + ((y >> 3) * FogTilesX + (x >> 3)) * FogBands + (s >> 4)) * 16)
    : (uint4)0xFFFFFFFFu;
#ifndef NFX_PIPELINE
mask = WaveActiveBitOr(mask);   // a group straddling two tile rows (90 rows): one mask for the wave
#endif
uint count = min((uint)LightCount.x, (uint)NFX_MAX_LOCAL_LIGHTS);
[loop] for (uint w = 0; w < 4; ++w) { uint bits = mask[w]; while (bits != 0u) { uint i = w * 32u + firstbitlow(bits); bits &= bits - 1u; if (i >= count) break; ... 既存 150〜171 行目 ... } }
```
に。`LightCount.w = 0` で従来の全灯ループ（Developer トグルで比較用）。

**`NativeEngine.cs`**: `FogLampPosId` / `FogLampParamsId`、`public static bool FogLightLists = true`、`fogLists_`。`TraceFogVolume` の署名に `Vector4[] lampPositions = null, int lampCount = 0` を追加し、ディスパッチ前に `SetComputeVectorArrayParam(bridge, FogLampPosId, lampPositions)` と `SetComputeVectorParam(FogLampParamsId, (count,0,0,0))`。`Issue` の `c[24] = lights_.Count;` の次に `c[27] = fogLists_ ? 1 : 0`（LightCount.w）。Surface サイズは不変（57 600 + 960 = 58 560 < 921 600。煙は 57 347 で終わる）。

**`AtmospherePass.cs`** 328〜329 行目: `TraceFogVolume(..., Lights?.Positions, Lights?.Count ?? 0)`。`FogStatus` に灯数を出す。

**計測（先に）**: `RTTimings.cs` に `FogVolume = Create("NFX RT fog volume")` と Stages の `("fog volume", FogVolume)`、`AtmospherePass.cs` 328 行目の呼び出しを `Begin/End` で挟む（`RTGlassPass.cs` 67〜69 と同形）。

### リスクと確認
- グループがタイル行をまたぐ（90 mod 8 = 2）→ `WaveActiveBitOr` で和集合（1〜2 灯の無駄）。DXR 1.0 ライブラリ版は `#ifndef NFX_PIPELINE` で wave 命令を外す。
- 楔はジッター込みの `UNITY_MATRIX_I_VP` だが列レイと同じ行列なので整合。エンジンの `Camera` との差は丸め程度 → +0.5 m。
- `Lights.Positions` の `Count` 以降は古い値 → `l < count` で止める。
- ゲームでの確認: F8 の「fog volume」ms を `FogLightLists` の on/off で比較（夜、ランプの多いマップ、マルチプレイ）。にじみが on/off で同じ。20 × 12 の格子に継ぎ目が出ない（出たら楔が狭い）。4.4 / 38.7 / 341 m 付近に水平な輪が出ない。画面端ぎりぎり外のランプが端の霧をまだ照らす。煙と霧箱が引き続き効く（Surface のオフセット衝突なし）。
- 付記: 将来 60 m ゲートを広げて遠い街灯のにじみを出すとき、帯 3 に遠灯が入り、このタイル × 帯の間引きがコストを抑える。

---

## 5. ジオメトリごとのコミット済みリソース

### 現状（`native/NFXNative/src/NFXNative.cpp`）

すべて `CreateBuffer`（250 行目、`desc.Width = size < 256 ? 256 : size`、1 バッファ = 1 `CreateCommittedResource`）。`struct Geometry`（572〜606 行目）が永続で持つのは `ownIndices / ownPositions / ownNormals / ownUvs / blas` の 5 本。

| バッファ | 作成 | サイズ | ヒープ / 状態 |
|---|---|---|---|
| `ownIndices` | 1309（`SanitizeIndices`） | `triangles × 12` | DEFAULT / UAV → 1322 で SRV へ遷移 |
| `ownPositions` / `ownNormals` / `ownUvs` | 1329（`extract` ラムダ、呼び出し 1351 / 1354 / 1357） | `VertexCount × 12 / 12 / 8` | 同上 |
| `blas` | 1388 | `ResultDataMaxSizeInBytes` | DEFAULT / AS 状態 |
| `AddGeometryData` 経路（1075〜）: `indexSource`=`ownIndices`、`vertexSource`=`ownPositions`、`ownNormals`、`ownUvs` | 1088〜1127 | 同上 | **UPLOAD ヒープのまま永続**（ヒットのたびにシェーダーが PCIe 越しに読む） |
| 風（`Wind.inl` 143〜146） | `positions`（毎フレーム UAV↔SRV 遷移 240〜241）、`blas` | | |

- committed buffer は 1 本ごとに暗黙ヒープを持ち、`D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT`（64 KB）粒度。the sample touge のログ（`1865 geometries, 6.1 M triangles, BLAS 428.4 MB`）で 1865 × 4〜5 = 7 500〜9 300 本。実データはインデックス 73 MB、頂点 100〜200 MB 程度なのに、粒度のムダだけで **約 230〜290 MB（最悪 2 倍）**。小さいメッシュほど酷い（前のマップは平均 535 三角形 → 6 KB が 64 KB を占有）。
- 副作用: 6.7k 回の `CreateCommittedResource` がビルド 1 095 ms の一部。デスクリプタ（`RawBufferDescriptor` 688 行目、1 件 4 個）は `ClearScene` 以外で回収されず（677 行目のコメント）、8〜9 マップで `DESCRIPTOR HEAP FULL`。
- 解放: `ReleaseGeometry`（1230 行目）は `g_retiredGeometries` へ退避、1512 行目（`WaitRenderIdle` 後）で丸ごと解放。id は再利用されない（`g_geometries` は増える一方）。`NFX_Native_ClearScene` は C# から未使用。DXR 1.2 側は 64 MB チャンク（`RtDevice.inl` 14〜17 `RtChunk`、158〜178 `RtAllocate`: bump のみ、free なし）だが、BLAS（831〜837）と OMM（684〜694、1 件 4 本）はまだ committed。

### 設計: 「1 本の大バッファ + オフセット」のプール（placed resource ではなく）

placed resource でもバッファは 64 KB 整列が必須（4 KB の small alignment はテクスチャだけ）なので小物のムダは消えない。BLAS は 256 B 整列で 1 バッファに複数置ける（`D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BYTE_ALIGNMENT`）。`RtDevice.inl` が既に同方式で動いており、ビューと VA の計算を流用できる。

**プールは 2 種類**（状態が違うので同居不可）:

| プール | 状態 | 整列 | 用途 |
|---|---|---|---|
| Data pool | 書き込み中 UNORDERED_ACCESS、封印後 NON_PIXEL_SHADER_RESOURCE（**チャンク単位で遷移**。バリアはリソース単位なのでサブ範囲ごとの状態は持てない） | 256 B（raw SRV 16、インデックス 4、頂点ストライド 12/8 を包含） | ownIndices / ownPositions / ownNormals / ownUvs |
| BLAS pool | RAYTRACING_ACCELERATION_STRUCTURE 固定（遷移不可） | 256 B | blas |

```cpp
struct Span { int chunk = -1; UINT64 offset = 0, bytes = 0; bool Valid() const { return chunk >= 0; } };
struct PoolChunk { ComPtr<ID3D12Resource> buffer; UINT64 size, used, top; std::vector<std::pair<UINT64,UINT64>> freeList; D3D12_RESOURCE_STATES state; bool open; };
struct Pool { std::vector<PoolChunk> chunks; bool Allocate(UINT64, Span*); void Free(const Span&); D3D12_GPU_VIRTUAL_ADDRESS Address(const Span&) const; ID3D12Resource* Resource(const Span&) const; void Reset(); };
Pool g_dataPool, g_blasPool;   // kPoolChunkBytes = 64 MB（RtAllocate と同じ）、kPoolAlign = 256
```
`Allocate` は free リスト first-fit → bump → 新チャンク（64 MB 超は専用サイズ）。`Free` は隣接結合、`used == 0` でチャンク破棄。

- `Geometry`: ComPtr 5 本 → `Span indices, positions, normals, uvs, blas`。真偽判定（1273, 1305, 1347, 1373, 1408, 1456〜1466、`Wind.inl` 71, 227, 253、`RtDevice.inl` 564, 569, 637, 666, 681）は `.Valid()` に。
- **アドレスとデスクリプタ**: TLAS の `AccelerationStructure`（1408）、`DestAccelerationStructureData`（1394）、`IndexBuffer`（1323）、`WindBlas` の `VertexBuffer.StartAddress`（`Wind.inl` 128）、`RecordWind` の root SRV（228）、`MirrorScene` の `copy` の root SRV（`RtDevice.inl` 579〜582）はすべて GPU VA → `base + offset` に置き換えるだけで**シェーダーは無変更**。`RawBufferDescriptor` を `(buffer, offset, bytes)` に（`RtRawDescriptor` `RtDevice.inl` 181〜197 と同形: `FirstElement = offset / 4`）。シェーダーはビュー先頭相対で読む（`NFXCommon.hlsli` 181〜194）ので無変更。レコードはデスクリプタ index のみ（1479〜1500）→ 無変更。
- **サニタイズ／抽出カーネル**: 出力は root UAV（1318、1339 の `SetComputeRootUnorderedAccessView(1, …)`）→ `g_dataPool.Address(span)` にするだけ（`Output.Store3(id.x*12)` はバインド先頭相対）。`MirrorScene` が既に同じこと（`RtDevice.inl` 556）をしている。
- **状態と同期（最重要）**:
  1. BuildScene の開始時、新規ジオメトリがあれば先に `WaitRenderIdle()`（今は 1509 でビルド後だけ）。部分的に埋まった封印済みチャンクに追記するには NPSR → UAV 遷移が要り、その間 in-flight のフレームが同じチャンクの別範囲を読んでいてはいけない。`g_sceneMutex` を BuildScene が握っている間レンダースレッドは記録も投入もしない（11-02 の修正で保証）ので Wait 後は安全。
  2. 書くチャンクを `open` にしてリスト先頭で NPSR → UAV（新規チャンクは UAV で作る）。
  3. per-buffer の遷移（1322, 1341, 1362）を per-chunk の UAV → NPSR に置き換えて封印。
  4. BLAS チャンクは遷移なし。1397 の `UavBarrier(g.blas)` はチャンクのリソースか null バリア。
  5. `WaitRenderIdle` が false（5 秒タイムアウト）なら再利用も遷移もせず新チャンクへ逃がす。
- **解放**: `ReleaseGeometry` は従来どおり退避のみ（`g_resolved` が BLAS を参照中）。1512 の `clear()` 直前（`WaitRenderIdle` 後）で各 Span を `Free`、デスクリプタ 4 個も free リストへ（`AllocateDescriptor` 664〜686 に `g_heapFree` を足して先に pop）→ `DESCRIPTOR HEAP FULL` も解消。`ClearScene`（980〜997）で両プール `Reset()`。free リストが要る理由: マップ切替は差分方式（`Mesh == null` のものだけ）なので前マップの生き残り（the sample touge では 520 件）と新マップが同じチャンクに混ざり、`Rebuild` ごとに地形チャンクと焼き込みスキンメッシュが release → 再 Add される（マップ内の churn）。
- **AddGeometryData 経路**: upload は一時バッファに留め、BuildScene で `CopyRaw`（`RtDevice.inl` に既存の root SRV → root UAV コピー）で Data pool へ写し、1448〜1451 で upload を Reset。性能面でも改善（毎ヒットの PCIe 読みがなくなる）。
- **ClearScene をマップ切替に接続（推奨、別作業でも可）**: `PrepareSceneForRayTracing` 由来の `Rebuild` だけにフラグを立て、C# `NativeEngine.ClearScene()`（`geometries_` と `materials_` も Clear）→ `NFX_Native_ClearScene`。チャンク・デスクリプタ・`g_windBlas`・墓標だらけの `g_geometries`、DXR 1.2 側の `g_rtChunks`（free なし）がまとめて消える。コスト: 共有メッシュ（520 件、400 ms 級）をマップロード中に作り直す。
- **Wind.inl**（フェーズ 2）: `positions` は専用の UAV 常駐プール（チャンク単位で 2 回遷移: 今の 2 × 64 回より安い）、`blas` は BLAS pool（ALLOW_UPDATE の同所更新は VA 指定なので問題なし）。
- **DXR 1.2 側**（フェーズ 3、任意）: BLAS / OMM / 風を `g_rt.device` 用プールに、`RtAllocate` に Free。

### 移行順（各段でシェーダー無変更、単独検証可）
1. Pool 実装 + BLAS pool（最小。1373〜1375, 1388〜1398, 1408 と `.Valid()` 判定）。
2. Data pool（1297〜1363, 1456〜1467、`Wind.inl` 128, 228、`RtDevice.inl` 579〜582）。
3. Free リスト（1509〜1513、ClearScene、デスクリプタ free リスト）。
4. AddGeometryData を pool へ（CopyRaw）。
5. ClearScene をマップロードに接続（C#）。
6. Wind.inl、DXR 1.2 側。

### リスク
- in-flight フレームとチャンク共有: Free・再利用・NPSR → UAV 遷移は必ず `WaitRenderIdle` 後かつ `g_sceneMutex` 下。
- BLAS 圧縮（COMPACTION）は未使用。入れるなら「scratch にビルド → `CopyRaytracingAccelerationStructure(COMPACT)` でプールへ → 元を Free」で 428 MB の半減が見込めるが、本件とは分ける。
- 64 MB を超える 1 件は専用チャンク。末尾の部分充填チャンク（最大 64 MB × 2）は残る。
- 予算ぎりぎりでは 64 MB の確保が先に失敗しうる → 16 MB に落として再試行。
- ビルド失敗時（1310 / 1330 / 1389 の return）に割り当て済み Span を Free するか、`chunk = -1` で次回再試行。

### 確認方法
- 同一マップ（the sample touge）ロード直後の F8「GPU memory」を前後で比較（現状 4007 → 5711 MB）。目標 −200〜−500 MB。
- `Native engine scene:` 行に `pool data used/total, BLAS used/total, chunks` を追記して、合計が「used + 最大 1 チャンク」に収まるか。
- parking ↔ the sample touge を 5 往復以上: GPU memory が戻る、`DESCRIPTOR HEAP FULL` が出ない、device removed なし、`geometries` が増え続けない（ClearScene 接続後）。
- 車交換とシェーダーリビルドの繰り返しで pool used が増え続けない（free リストの検証）。
- `Native engine scene check` 行（`static 60 self / 1160 other / 128 through`）が変更前と同程度、葉のアルファテスト・揺れる葉の影が同じ見た目。`DXR 1.2 sun shadows: ready` 行が従来どおり。

---

## 6. 各パスの画面コピーの共有

### 現状

7 か所が `CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, 自前のフル解像度 RGBA16F)` をしている。理由はどれも「同じ `cameraColorBuffer` に合成して書き戻すので、読む側の複製が要る」。

| パス | コピー先 | 使い方 | インジェクション / 順 |
|---|---|---|---|
| `RTShadowPass.cs` 493 | `color_`（254 行目 `Alloc(Vector2.one, "NFX_RTShadow_Color")`） | 合成（641〜642）の入力。未使用テクスチャの穴埋め（600〜630 の `?? color_`） | AfterOpaqueAndSky（RT ボリューム、先） |
| `RTReflectionPass.cs` 121 | `color_`（55） | `TraceReflection` の画面色（`PrepareScreen` が `screen_` へ詰める）と合成（165〜170） | 同ボリューム、影の後 |
| `AtmospherePass.cs` 400 | `color_`（150） | 合成（402〜） | 同ボリューム、最後 |
| `RTGlassPass.cs` 66 | `color_`（31） | `TraceGlass` と合成 | BeforePostProcess（`glassVolume`） |
| `PostProcess.cs` 149 | `source_`（67） | DLSS の入力、ブルーム・トーン（`fullSource_` へアップスケール時） | BeforePostProcess、priority −1000（最後） |
| `CarRain.cs` 30 | `color_`（21） | 車の水滴の屈折で読む | BeforePostProcess、−100 |
| `LensEffects.cs` 27 | `copy_`（19） | 暗視 | BeforePostProcess |

どのパスも **コピー → 自分の合成で読む → 終わり** で、別パスの Execute をまたいで読まない（`props_` の `?? color_` も自分の Draw の中だけ）。4K（7.9 Mpx）の RGBA16F は 63 MB なので 7 枚で 442 MB、1916x997 で 107 MB。

### 設計

- `Rendering/SharedColor.cs`（静的）: `public static RTHandle Get()` が 1 枚の `RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: R16G16B16A16_SFloat, useDynamicScale: true, name: "NFX_SharedColor")` を遅延確保して返す。`Release()` を `RayTracingEffects.Destroy`（または `NatureFXMod.OnDestroy`）から。
- 各パスの `color_`/`source_`/`copy_` を `SharedColor.Get()` に置き換え、各クラスの `Alloc`/`Release` を消す。**`PostProcess.source_` だけは例外**: DLSS が `source_` を入力にして `fullSource_` へ書く（149〜160 行目）が、そのあと `source` をブルームのダウンサンプルが読む（162 行目以降）。同じ Execute 内で完結するので共有可。ただし `Dlaa.Run(ctx, source_, source_, …)`（160 行目、DLAA の in-place）は `source_` を読み書き → 共有でも 1 枚で足りる（in-place は DLSS 側の内部コピー）。
- `AtmospherePass` は 2 インスタンス（trace と composite）で `color_` は composite だけが使う: 共有に。
- `LensEffects.copy_`/`CarRain.color_` も共有に。`PhotoMode.average_` は蓄積用なので対象外。
- 順序の安全性: HDRP は同じインジェクションポイントの `CustomPassVolume` を priority 順に直列に実行し、ボリューム内のパスも順。各パスは Execute 冒頭でコピーしてから読むので、前のパスが残した内容に依存しない。**唯一の注意**: `RTShadowPass` が `color_` を「穴埋めテクスチャ」として `props_` に入れたまま後続の `DrawFullScreen` が複数回走る（641〜679 行目: Composite の前に Temporal/Denoise が別ターゲットへ）。これらは全部同じ Execute 内なので問題ない。
- 期待効果: −6 枚 = 4K で −380 MB、1916x997 で −92 MB。コピー回数は変わらない（7 回のフル解像度コピー ≈ 4K で各 0.1 ms 程度）。
- さらに減らすなら: 隣り合う 2 パス（影の合成 → 反射の合成）を 1 つの `CustomPass` にまとめ、影の合成結果を共有バッファ側に書いて反射がそれを読む（ping-pong）ことで**コピー自体を 1 回減らす**。今回は対象外（パスの構造変更）。

### 確認
- 影・反射・ガラス・空の合成、DLSS、雨の屈折、暗視が以前と同じに見えること。
- F8 の GPU memory が 4K で 300 MB 以上減ること。

---

## 7. トレースバッファのストライド削減

### 設計を決める前提
- ネイティブはルート UAV/SRV（生の GPU アドレス。`NFXNative.cpp` 1968〜1975、`RtDevice.inl` 1106〜1113、`Nrd.inl` 475〜477）でバッファを受け取り、C++ はストライドを知らない → **レイアウト変更で C++ を直すのは `RtDevice.inl` の PackSun 呼び出しだけ**。あとは埋め込みシェーダーの再コンパイル。
- 位置は**絶対ワールド座標**（`Frame.Camera // absolute world position`）。float32 のまま（half・固定小数は不可）。削るのは法線・方向・色・粗さ・フレネル・短距離の距離。
- オクタヘドロン法線は両側に実例あり（ブリッジ `PrepareDlss` 730 行 `PackNormalOctQuadEncode`、`DlssPackGuides.hlsl` 19〜25 `UnpackOct`）。今回は unorm16 × 2（最大誤差 ≈ 3e-5 rad = 4K の 1 画素の 1/10）。
- **詰めた実例が既に実働**: DXR 1.2 への受け渡し `PackSun.hlsl`（入力 32 → 16 B: origin xyz + packed pixel、0xFFFFFFFF = トレースなし。出力 8 → 4 B の half2）。`SunShadow.hlsl` 14〜22 が `NFX_PACKED_SUN` で読む。
- 太陽トレースの Surface[1] の法線 xyz は**誰も読まない**（`SunShadow.hlsl` は `b.w` のみ、`NrdSigmaPack` は +12 の `a.w` のみ、`PackIn` は +28 のみ）。

### 現状（`NativeEngine.Buffers(kind, pixels, surfaceFloat4s, resultStride)`）

| 種類 | 行 | 解像度 | Surface | Result | B/px | 1916x997 → 案 (MB) | 3840x2054 → 案 (MB) |
|---|---|---|---|---|---|---|---|
| Sun | 873 | フル | 2 float4 | 8 B | 40 | 76 → 38 | 316 → 158 |
| NRD 太陽ガイド | 921 | フル | 2 float4 | – | 32 | 61 → 31 | 252 → 126 |
| Surface | 1256 | フル | 1 float4 | 32 B | 48 | 92 → 61 | 379 → 252 |
| Glass | 1336 | フル | 2 float4 | 16 B | 48 | 92 → 61 | 379 → 252 |
| Screen | 1313 | フル | 12 B | – | 12 | 23 → 15 | 95 → 63 |
| Reflection | 1281 | 1/2 | 3 float4 | 32 B | 80 | 38 → 23 | 158 → 95 |
| Ambient（GI/Local/LampMask 共用） | 1109 | 1/2 or 1/4 | 3 float4 | 16 B | 64 | 23 → 15 | 95 → 63 |
| NRD 反射・環境光ガイド | 921 | 1/2 | 2 float4 | – | 32 | 15 → 8（各） | 63 → 32（各） |

合計（NRD オン、反射・環境光 1/2）: **1916x997 で 458 → 283 MB（−176）、3840x2054 で 1893 → 1167 MB（−726）**。4K の「画面サイズ依存 約 4 GB」のうち 0.7 GB が消える。帯域は 4K で約 2.9 → 2.0 GB/フレーム（トレースは BVH とテクスチャが支配的なのでフレーム時間は数 % の改善見込み。実益は予算超過の回避）。

### 共通ヘルパー（両側に同じ式）
```hlsl
uint   NfxPackOct16(float3 n);     // HDRP PackNormalOctQuadEncode と同式 → unorm16 x2（round で量子化）
float3 NfxUnpackOct16(uint p);     // DlssPackGuides の UnpackOct を NFXCommon.hlsli へ移す
uint   NfxPackHalf2(float2 v);  float2 NfxUnpackHalf2(uint p);
uint   NfxPackUnorm4x8(float4 v); float4 NfxUnpackUnorm4x8(uint p);
```
ブリッジ側は新レイアウト用に `RWStructuredBuffer<uint4>` / `<uint>` / `<uint2>` を**別名で宣言**し、同じ GraphicsBuffer を新しいプロパティ ID で束ねる（要素型のサイズと C# の stride を必ず一致させる。ずれると黙って壊れる）。

### 種類ごとの案（推奨順: A → B → C → D → E → F → G。A〜D で 4K の −567 MB）

**A. Sun: Surface 32 → 16 B、Result 8 → 4 B（PackSun の実働レイアウトをそのまま）**
- Surface uint4 = `{ origin.xyz (f32), traced ? (pixel.x | pixel.y << 16) : 0xFFFFFFFF }`、Result uint = `f32tof16(visibility) | f32tof16(blockerDistance) << 16`。行き先は既に R16G16_SFloat（`RTShadowPass.cs` 202 行）、NRD の半影入力も R16 → **無損失**。
- 変更: ブリッジ `Prepare` 126〜128（既定値は `0x00003C00u` = half 1.0）、`Unpack` 462、宣言 39〜40。`SunShadow.hlsl` 14〜22 / 70〜74 を packed 一本に（`#ifndef NFX_PACKED_SUN` の 40〜65 行はガラス影の有無の意味なので `build_native.ps1` 48 行の `-D NFX_PACKED_SUN` を `NFX_NO_GLASS_SHADOWS` などに改名して残す）。`NrdSigmaPack.hlsl` 38〜39 → `Load(index*16+12) != 0xFFFFFFFF`、`NfxUnpackHalf2(SunResult.Load(index*4))`。`NrdSigmaUnpack.hlsl` 25, 29 同様。`RtDevice.inl` 1137 の `RtPack(g_packSunIn)` と 1155 の `g_unpackSunOut` は **`g_copyRaw`（`CopyRaw.hlsl`）に置き換え**（受け渡しのバイト数 16 / 4 は不変）。C# 873 行 `Buffers(TraceSun_, w*h, 1, 4)`。
- 再ビルド: バンドル + .ksm + **DLL**（SunShadow619 と PackSun の差し替えは Reload shaders の対象外）。

**B. Surface Result 32 → 16 B**
- uint4 = `{ NfxPackUnorm4x8(albedo rgba), NfxPackOct16(normal), asuint(signedDistance), 0 }`。
- α の契約 `metallic * 0.49 + 0.5` は **8 bit で既に成立**（`UnpackSurface` が R8G8B8A8_UNorm に書いた時点で丸まる: 0.5 → 128/255 = 0.502 > 0.495、metallic 1 → 125/255 → /0.49 = 1.0004 → saturate 1）。量子化は必ず `round`（切り捨てだと 0.5 → 0.498 でコート判定が揺れる）。
- 距離は **float32 必須**: `TraceSurface()`（ブリッジ 96 行）がこれでレイ原点を再構成し、押し出しは `0.01 + viewDepth × 0.0005`（100 m で 6 cm）。half だと同じ桁の誤差 → 影のアクネ・浮き。符号で thin、0 で未検出は今のまま。
- 変更: `Surface.hlsl` 16〜17, 29〜30, 37〜38（未検出のとき oct(0) は (0,0,1) に化けるが読み手は全員 w≠0 を先に見る。`UnpackSurface` で w==0 なら 0 を書けばテクスチャは今と同一）。ブリッジ `PrepareSurface` 494〜495、`UnpackSurface` 506〜507（復号して同じ 2 枚のテクスチャへ）。C# 1256 行 `Buffers(TraceSurface_, w*h, 1, 16)`。**NFX_RTShadow / NFX_RTReflect / RTSurfaceTrace.cs のフォーマットは変更不要**。

**C. Glass Surface 32 → 16 B**
- uint4 = `{ asuint(dir.xyz), f32tof16(maxDistance × 1.001) | f32tof16(log2(exposure)) << 16 }`。pixel は捨てる: TraceGlass はカメラの actualWidth/Height で走るので `TracePixel(id) == id`（`Glass.hlsl` 90 行を `uint2 pixel = id.xy` に。220 行の Screen 参照も同じ値）。
- 露出は **log2 を half に**（昼の露出倍率 ≈ 2.5e-5 は half の正規化最小 6.1e-5 を下回る）。maxDistance half の相対刻み ≤ 0.1 % に対し余裕は `×1.002 + 0.02`、念のため 0.1 % 上乗せ。
- 変更: ブリッジ `PrepareGlass` 623〜625、`Glass.hlsl` 79〜80, 84, 89〜91、C# 1336 行 `Buffers(TraceGlass_, w*h, 1, 16)`。Result は 16 B のまま（w に整メートル × 32768 を詰めていて f32 が要る）。

**D. NRD ガイド 32 → 16 B（3 つのガイド全部）**
- uint4 = `{ NfxPackOct16(normalWS), asuint(viewZ), NfxPackHalf2(-motion uv), round(roughness × 65535) }`。元が HDRP の 12 bit oct 法線 / 8 bit 粗さ / RG16F の動き、NRD 側は R10G10B10A2 と RG16F に落とす → **無損失**。viewZ は f32（空の 1e6 もそのまま）。
- 変更: ブリッジ `PrepareNrdGuide` 643, 662〜663、`NrdSigmaPack.hlsl` 36〜42、`NrdReblurPack.hlsl` 42〜50、`NrdSpecPack.hlsl` 35, 43, 47、C# 921 行 `new GraphicsBuffer(Structured, pixels, 16)`。Nrd*Pack は `NrdDropKernels` でホット再読込可。

**E. Screen 12 → 8 B**: uint2 = `{ f16 r | f16 g << 16, f16 b | f16 depth << 16 }`、空は depth 65504。`ScreenVisibility` の許容 `0.1 + 0.03 w`（3 %）に対し half の刻み 0.05 % → 無損失。変更: ブリッジ 513, 526〜527、`NFXReflect.hlsli` 19〜26、`Glass.hlsl` 220〜221、C# 1313 行 stride 8。

**F. Reflection: Surface 48 → 32 B、Result 32 → 16 B**: Surface A = `{ origin.xyz, packed pixel }`、B = `{ oct16(n), f16 fresnel.rg, f16 fresnel.b | f16 alpha, asuint(exposure) }`（alpha は half でも −1 のトレースなし・0.002 の下限・`mirror <= 0.003` 判定を保つ）。Result = `{ half2(rg), half2(b, Luminance(fresnel)), asuint(dist), 0 }`（rgb は `min(., 60000)`、dist は R32 行きなので f32）。変更: ブリッジ 551〜553, 584〜585, 596, 599〜603, 636〜637、`Reflection.hlsl` 18〜29, 103〜104、`NrdSpecPack.hlsl` 36〜40, 50、`NrdSpecUnpack.hlsl` 25〜36、C# 1281 行 `Buffers(TraceReflection_, w*h, 2, 16)`。

**G. Ambient 系 Surface 48 → 32 B（優先度低）**: A = `{ origin.xyz, valid ? packed pixel : 0xFFFFFFFF }`、B = `{ oct16(n), oct16(geomN), asuint(viewDepth), travelled(f32、Ambient/GI が +28 に書く) }`。変更: ブリッジ 143〜146、`Ambient.hlsl` 27〜38, 82、`GI.hlsl` 68〜80, 89、`LocalShadows.hlsl` 41〜42、`LampMask.hlsl` 20〜21、`NrdReblurPack.hlsl` 44〜45（+44 → +28）、`NrdReblurUnpack.hlsl` 28、C# 1109/1128/1140 の `3` → `2`。Result 3 種は 16 B のまま（LampMask は整数を float に詰めている）。

### ビルドの対応
| 変更 | 再ビルド | 再起動 |
|---|---|---|
| ブリッジ | バンドル（RT 合成シェーダーだけ: 数分、`reload/` でホット可） | 不要 |
| ネイティブのトレース・NRD pack/unpack | DLL（`-ShadersOnly` + Reload shaders でも可） | 通常ビルドなら要 |
| `RtDevice.inl` の PackSun → CopyRaw、SunShadow619 | DLL のみ | 要 |
| C# のストライド | .ksm | 要 |
**どの段階も .ksm・バンドル・DLL の三つがそろって初めて整合する**（レイアウトは三者間の API）。1 種類ずつ進め、各段で三つをそろえて再起動。

### リスク
1. 位置精度: 絶対座標の f32 はそのまま。レイ原点は Sun / Reflection / Ambient の Surface と Surface Result の距離（f32 必須）。
2. α の契約は 8 bit で保たれるが `round` 必須。読み手 7 か所（RTShadow 463, 527, 548, 585, 619, 689、RTReflect 37〜40、ブリッジ 562〜576）は不変。
3. NRD: viewZ は f32、空は 1e6、動きの単位・符号は不変、太陽の `traced` 判定を 0xFFFFFFFF 比較に両側でそろえる。
4. Glass の露出は log2 で詰める（Reflection は f32 の枠がある）。
5. DXR 1.2 の受け渡し: PackIn/UnpackOut を CopyRaw に替えるので `RtEnsureHandover` のバイト数は不変。初回トレースは Prepare の既定値を使うので既定は `0x3C00`。

### ゲームでの確認（各段で F8 の `Camera WxH: ... trace buffers N MB` が減ったことを先に見る）
- A: 半影幅が前と同じか（遮蔽物距離が壊れると一律に太い/細い）、影が全面に出る/全く出ない（traced の比較ミス）、木漏れ日のちらつき増（pixel ハッシュ）、DXR 1.2 オンで同じ形、NRD で SIGMA が真っ黒/真っ白なら NrdSigmaPack の読みずれ。
- B: 塗装の metallic が狂う → α の丸め、クリアコートの鏡面が消える → 0.5 フラグ、**地面の影のアクネ・浮き** → 距離、葉の裏の光が消える → 符号、エッジのざらつき → oct の復号。
- C: 窓の映り込みが消える → maxDistance の丸め、明るさが桁で狂う → log2、屈折の色ずれ → `pixel = id` の前提。
- D: 環境光/影が動きに引きずられる → 動きの単位・符号、エッジが溶ける → 法線、特定距離で突然ノイズ → viewZ、反射がざらつく → 粗さ。
- E: 反射・ガラスの画面色が消える → 深度一致（空 65504）。F: 粒状 → alpha の下限、角度ずれ → 法線。G: 環境光が消える → valid、REBLUR のヒット距離 → +28。
