# REIMU
**Resolution-adaptive Engine for Integrated Multiscale Unification**

* FFT + SWE + APIC のマルチスケール流体
* SWE⇄APICの双方向カップリング
* 2017年のiGPUでも動作するリアルタイム流体
----
低スペックGPUでも海全体とインタラクションできる
**リアルタイム流体シミュレーションエンジン**です。

Unity（URP）とCompute Shaderを用いて、以下の3種類の流体手法を組み合わせることで、

* 広大な海の表現
* リアルタイムのインタラクション
* GPU計算負荷の最小化

を同時に実現しています。

## デモ

[![Demo](https://img.youtube.com/vi/wG5bqn34c7w/0.jpg)](https://youtu.be/wG5bqn34c7w)

## 概要

多くのゲームでは、広大な海の全体に対して直接インタラクションすることはできません。
理由は、リアルタイム流体シミュレーションの計算コストが非常に大きいためです。

本プロジェクトでは、特性の異なる3つの手法を組み合わせることで、

* 遠景は軽量に
* プレイヤー周囲は高精細に

計算する **マルチスケール流体システム** を構築しました。

## システム構成

プレイヤー周囲を中心とした3層構造になっています。

```
プレイヤー
   │
   ├ APIC層（粒子ベース流体）
   │
   ├ SWE層（浅水方程式）
   │
   └ FFT層（統計的海面）
```

### FFT層

背景の海を表現するための手法です。

特徴

* 非常に軽量
* GPUで高速に計算可能
* 広大な海を表現可能

ただし、インタラクションはできません。

### SWE層（Shallow Water Equations）

浅水方程式による高さマップ型流体です。

特徴

* 軽量
* 波の伝播を自然に表現
* キャラクターや物体とインタラクション可能

ただし

* 波の巻き込み
* 水しぶき

といった3次元的挙動は表現できません。

### APIC層（Affine Particle-in-Cell）

粒子ベースの高品質流体手法です。

特徴

* 水しぶき
* 波の巻き込み
* 飛沫

などの立体的表現が可能です。

しかし

* 計算コストが非常に高い

という問題があります。

そのため本システムでは**必要なときだけ局所的にAPICを使用する**設計にしています。

## 流体間の遷移（Coupling）

本システムの特徴は、
**SWE（2D）とAPIC（3D）の双方向遷移** です。

### SWE → APIC（昇華）

波が激しくなると
SWEの水面からAPIC粒子が生成されます。

判定には以下を使用しています。

* フルード数（Froude Number）
* 水面の勾配
* 水面ラプラシアン

これにより

* 波の砕け
* 水柱
* 飛沫

を自然に生成できます。

### APIC → SWE（凝縮）

空中のAPIC粒子が水面に落下した場合

* 粒子を消滅
* 質量と運動量をSWEへ書き戻し

することで

**波紋として再現** します。

## 描画

各流体に応じて描画手法を切り替えています。

### APIC

粒子密度に応じて描画方法を変更

* 高密度 → Marching Cubes

* 低密度 → ビルボード飛沫

### SWE / FFT

* メッシュ頂点変位
* FFT波形の重ね合わせ
* 泡・コースティクス表現

Shader Graphを用いてルックデヴを行っており、異なる海の表現を簡単に切り替えられます。

|南国風の海|荒波|
|---|---|
|![](https://storage.googleapis.com/zenn-user-upload/c1dd29b07adc-20260316.png)|![](https://storage.googleapis.com/zenn-user-upload/6971c8e8162c-20260316.png)|

## 技術構成

|項目|内容|
|---|---|
|エンジン|Unity (URP)|
|言語|C#, HLSL|
|計算|Compute Shader|
|粒子流体|APIC|
|高さ場流体|SWE|
|海洋波|FFT (JONSWAP)|
|ボクセル化|Marching Cubes|

## シミュレーションアルゴリズム
### APIC

* Jacobi-PCG圧力ソルバ
* Particle-to-Grid / Grid-to-Particle
* GPU並列計算

### SWE

* 有限体積法 (FVM)
* HLL近似リーマンソルバ
* MUSCLスキーム
* MinModリミッター

### FFT

* JONSWAPスペクトル
* GPU FFT

## Compute Shader構成

主な処理フロー

```
1. 地形取得
2. 入力処理
3. FFT計算
4. APIC移流
5. APIC → SWE（凝縮）
6. SWEシミュレーション
7. SWE → APIC（昇華）
8. APIC圧力計算
9. 粒子更新
10. ボクセル化
11. 描画
```

GPU並列処理を活用し、
CPUとのデータ転送を最小限にしています。

## ディレクトリ構成
役割は次の「シミュレーション処理フロー」を参照してください。

```text
Assets
├─ Main
│  ├─ Core
│  │  ├─ DataTypes.cs
│  │  ├─ SimulationManager.cs
│  │  ├─ SimulationManager.Terrain.cs
│  │  └─ SimulationManager.Voxelizer.cs
│  │
│  ├─ Environment
│  │  ├─ FFTManager.cs
│  │  ├─ FFTtracker.cs
│  │  └─ OceanGridGenerator.cs
│  │
│  ├─ Materials
│  │  ├─ APICMesh.mat
│  │  ├─ APICSplash.mat
│  │  ├─ SeaBottomMaterial.mat
│  │  ├─ SkyBox.mat
│  │  └─ SWEFFT.mat
│  │
│  ├─ Player
│  │  └─ PlayerController.cs
│  │
│  ├─ Rendering
│  │  ├─ APICMesh.shadergraph
│  │  ├─ APICSplash.shader
│  │  ├─ FluidCore.shadersubgraph
│  │  ├─ MarchingCubesTables.cs
│  │  └─ SWEFFT.shadergraph
│  │
│  ├─ ShaderFunctions
│  │  ├─ APICMeshFunction.hlsl
│  │  └─ SWEFFTFunctions.hlsl
│  │
│  └─ Solvers
│     ├─ APICSolver.compute
│     ├─ CSCommon.cginc
│     ├─ FFTSolver.compute
│     ├─ SWESolver.compute
│     └─ Voxelizer.compute
│
├─ Scenes
│  └─ MainScene.unity
│ 
├─ Settings
│
└─ Textures
   ├─ CausticsTexture.png
   ├─ FoamTex.png
   ├─ Seabed.asset
   └─ SkyBoxTex.png
```
## シミュレーション処理フロー
![](https://github.com/user-attachments/assets/ecd0501c-739c-4123-ab87-2166a586ceba)

## 実行方法
1. 本リポジトリをクローンするか、ZIPでダウンロードします。
2. Unity Hubから対象のバージョンでプロジェクトを開きます。
3. Scenes/MainScene を開いてプレイモードを実行してください。
4. WASDで移動、Spaceでジャンプ、右ドラッグで視点移動、左ドラッグで水面をかき混ぜることができます。
5. パラメータの調整は、SWESeaオブジェクトと、SWEFFTマテリアルのインスペクタから行ってください。

## 性能

テスト環境

|項目|仕様|
|---|---|
|CPU|Intel Core i5-8500|
|GPU|Intel Graphics 630|
|RAM|16GB|
|Unity|Unity6|
|解像度|1920×1080|

## デフォルト設定
```text
APIC: 64³
Voxel: 128³
SWE: 128²
FFT: 512² (3カスケード)
```

## フレーム時間

|状況|処理時間|
|---|---|
|静かな状態|約83ms|
|激しく攪拌|約106ms|

2017年のiGPUでもリアルタイム動作します。

## 既存手法との比較
提案手法の計算効率を既存手法と比較しました。

|手法|最小処理時間|
|---|---|
|フルAPIC|約7700ms(推定値)|
|APIC+FFT|約168ms|
|REIMU|約83ms|
|SWE+FFT|約78ms|
|フルFFT|約47ms|

APIC単体と比べて **約90倍以上高速化** しています。

## 技術ハイライト

* 異なる次元（2D/3D）の流体カップリング
* GPUアトミック演算の最適化
* 生存粒子のみ計算するスケジューリング
* LODによる計算負荷削減
* Compute Shaderのみで完結するGPUパイプライン


## 技術ブログ

詳細な解説はこちら

[前編](リンク): 全体の設計, ビジュアル面の工夫

[後編](リンク): 流体シミュレーション**全7手法のGIF比較**, 手法の選定理由, カップリングアルゴリズム, 技術的課題と解決策

## ライセンス

MIT
