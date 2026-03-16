# REIMU (Resolution-adaptive Engine for Integrated Multiscale Unification)
ローエンド環境でも動作する、海全体とインタラクション可能なリアルタイム・ハイブリッド流体エンジンです。Unity (URP) と Compute Shader を用いて実装されています。

🎥 [デモ動画を見る (YouTube)](https://youtu.be/wG5bqn34c7w)
📝 技術解説ブログ [前編]() / [後編]()

## 🌊 概要 (Overview)
既存のゲームにおいて広大な海全体に直接インタラクションすることは、流体シミュレーションの計算コストの観点から非常に困難です。
REIMUは、特性の異なる3種類の流体手法（APIC, SWE, FFT）を組み合わせることで、ローエンド環境でも「海全体がAPIC流体であるかのような表現」をリアルタイムで可能にしました。

## ✨ 主な特徴 (Features)
* 3層ハイブリッド・アーキテクチャ: プレイヤーの距離と波の激しさに応じて、計算手法をシームレスに切り替えます。

* 双方向の流体遷移 (Coupling): 2Dの浅水方程式(SWE)と3Dのパーティクル流体(APIC)を鉛直方向に重ね合わせ、波の激しさに応じて「昇華 (Sublimation)」と「凝縮 (Condensation)」を自動で行います。

* Compute Shaderによる徹底的な最適化: 生存しているAPIC粒子のみに計算を絞り、Jacobi-PCG法やParallel Reductionを活用してアトミック加算のボトルネックを排除。

* GPU完結のレンダリング: Marching Cubesによるボクセル化から描画(DrawProceduralIndirect)までをCPUを介さずに実行。

* 直感的なルックデヴ: Shader Graphを用いた1パラメータ調整で、南国の海から荒波まで多様なビジュアルを表現可能。

## ⚙️ アーキテクチャ (Architecture)
シミュレーション領域はプレイヤーに追従し、以下の3つの層で構成されています。

1. APIC (Affine Particle-in-Cell) 層

* 役割: 激しい波の巻き込み、水しぶき、プレイヤーとの立体的なインタラクション。

* 特徴: 最も高品質だが計算負荷が高いため、SWEの波が激しくなった瞬間のみ、局所的に発生（昇華）させます。

2. SWE (Shallow Water Equations) 層

* 役割: 波の伝播、障害物との干渉、白波の移流。

* 特徴: 2次元の高さマップとして計算されるため非常に軽量。APIC粒子の「床」として機能し、空中に舞った水滴が落下すると波紋を作ります（凝縮）。

3. FFT (Fast Fourier Transform) 層

* 役割: 遠景の海（背景）。

* 特徴: インタラクション不可ですが広大な領域を低負荷で描画。頂点シェーダーでの距離に応じたLODとモーフィングによりシームレスに繋がります。

## 📊 パフォーマンス (Performance)
2017年モデルのiGPUを用いた厳しい環境下でも動作可能な設計です。近年のディスクリートGPU環境であれば、余裕をもって60FPS動作が狙えます。

* 検証環境: Intel Core i5-8500 / Intel Graphics 630 / RAM 16GB / FHD解像度

* 動作速度: 約9〜12FPS (フレームタイム: 約83ms〜106ms)

* 純粋な流体計算負荷: 約50ms (移流, 圧力計算, ボクセル化など)

※ボトルネックは主にAPICのボクセル化と透明オブジェクトの描画にあります。詳細は[技術ブログ 後編]()をご覧ください。

## 🚀 動作要件 (Requirements)
* Unity 6 (6000.3.6f1 LTS 以上推奨)

* Universal Render Pipeline (URP)

* Compute Shaderが動作する環境 (DirectX 11互換以上)

## 使い方 (Getting Started)
1. 本リポジトリをクローンするか、ZIPでダウンロードします。

2. Unity Hubから対象のバージョンでプロジェクトを開きます。

3. Scenes/MainScene を開いてプレイモードを実行してください。

4. WASD(または矢印キー)で移動、Spaceでジャンプ、右ドラッグで視点移動、左ドラッグで水面のかき混ぜができます。

## 📄 ライセンス (License)
This project is licensed under the MIT License.
