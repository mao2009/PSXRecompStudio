# PSXRecompStudio

[![CI](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml/badge.svg)](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

PlayStation 1（PS1 / PSX）向けソフトウェアの静的再コンパイル、バイナリ解析、リバースエンジニアリング、MIPS コード解析、ネイティブ移植を支援するオープンソース開発環境。

*[English README (Canonical / SSOT)](README.md)*

> このファイルは [`README.md`](README.md)（English Canonical / SSOT）の日本語訳です。内容に相違がある場合は `README.md` を正としてください。

## PSXRecompStudio とは

PSXRecompStudio は、PlayStation 1（PS1）ソフトウェアを解析・リバースエンジニアリングするためのスクラッチ開発の統合開発環境です。PS-X EXE の逆アセンブル、R3000A / MIPS I コードの解析、CPU 挙動のバイト単位で忠実なモデル化を行い、最終的にはタイトルコードを静的に再コンパイルして、エミュレーションを介さず Windows / Linux / macOS 上でネイティブに直接実行できるプログラムへ変換することを目指します。

Avalonia ベースのデスクトップ UI、C# のドメイン／アプリケーション Core、そして安定した C ABI で接続された C++ ネイティブ Core から構成されます。AI 開発エージェントはプロダクトそのものではなく、Evidence-first な支援手段の一つという位置付けです。

## PSXRecompStudio が目指すもの

- **SSOT 駆動のアーキテクチャ**: アーキテクチャ、CPU 仕様、開発プロセスは [`docs/`](docs/) と [Architecture Decision Records](docs/adr/) に生きた Single Source of Truth として文書化されており、暗黙知に頼りません。
- **機械的に強制される境界**: [`src/architecture.contract.json`](src/architecture.contract.json) で構成された `loach.ArchitectureAnalyzer` がレイヤー違反・依存方向違反・禁止 API 使用をビルドエラーとして検出します。Architecture Matrix は図面ではなく、コンパイラが検証する契約です。
- **決定論的な CPU 基盤**: R3000A モデルは命令単位の Golden Trace で検証されています。すべてのレジスタ書き込みをリタイア順に記録・再生して差異を検出する仕組みは、将来の Recompiler バックエンドを interpreter と比較検証するための土台でもあります。
- **安定した C# / Native 境界**: Native Core とのやり取りはすべて単一の C ABI（`psx_core.h`）経由の P/Invoke で行い、C++ の型を C# 側へ漏らしません。
- **Evidence-first・Human-in-the-loop な AI 協働**: AI 開発エージェントはプロジェクトのアイデンティティではなく交換可能な支援手段です。User-driven analysis・検証可能な根拠・人間によるレビューを中心に据え、ワークフローは Agent-agnostic（Claude Code、OpenCode、Codex 等を問わない）です。

## 現在の開発状況

以下は Issue や設計意図ではなく、現在のリポジトリの実装・テスト・CI の状態を反映しています。

| 領域 | 状態 |
|---|---|
| アーキテクチャ基盤（レイヤー、C ABI 境界、ADR） | 実装済み |
| Avalonia UI アプリケーションシェル | 実装済み（最小構成。機能 UI は未実装） |
| C# Core / Native Core 境界 | 実装済み |
| C ABI / P/Invoke | 実装済み |
| アーキテクチャ強制 Analyzer（Roslyn） | 実装済み・CI で強制 |
| Analyzer テストスイート | 実装済み |
| R3000A 命令ドメインモデル | 実装済み |
| R3000A デコーダー | 実装済み |
| MemoryBus / KSEG0・KSEG1 アドレス変換 | 実装済み |
| Branch / Load Delay Slot モデリング | 実装済み |
| COP0 / 例外処理 | 実装済み |
| Interrupt Controller | 実装済み |
| CPU 割り込み統合 | 実装済み |
| Timer / DMA Controller | 部分実装（レジスタレベルの Native モデルは実装済み。メモリバスへの完全な結線は進行中） |
| 最小 MIPS プログラム実行パス | 実装済み |
| Golden Trace（決定論的実行トレース） | 実装済み |
| GPU / SPU / CD-ROM / MDEC / GTE | 予定（インターフェース定義のみ） |
| Runtime（BIOS-less BIOS service 境界、EXE ロード、I/O ループ） | Phase 1 契約 |
| Synthetic MIPS Recompiler vertical slice（IR/lowering、メモリ、制御フロー、host codegen、differential validation） | 実装済み・差分検証済み |
| 実 ROM 関数の再コンパイル | 最初の1関数を実装・差分検証済み（#225）。汎用対応は未完了 |
| フルタイトルの静的再コンパイル | 未実装 |
| Debugger | 予定 |
| MCP / AI 連携 | 予定 |
| Ghidra 連携 | 予定 |

**CPU 実行基盤について**: CPU 実行基盤はすでに機能しています。命令デコード、メモリパス経由の実行（KSEG 変換を含む）、Branch/Load Delay Slot の挙動、COP0・例外処理、ハードウェア割り込みのサンプリング、決定論的な実行トレースが組み合わさり、最小の MIPS プログラムをエンドツーエンドで実行できます。これは CPU を貫く縦切りの実装であり、完全なエミュレータではありません。詳細仕様は [`docs/cpu/`](docs/cpu/) を参照してください。

**Recompiler について**: PSXRecompStudio の最終目標は静的再コンパイルです。backend-agnostic な Recompiler IR と共有 state contract、MIPS→IR lowering、決定論的な host C 生成、メモリバックエンド（各幅の load/store、unaligned access、load-delay セマンティクス）、制御フローバックエンド（branch、jump、link、delay slot、bounded/budget 付きループ）、interpreter-vs-recompiled の differential validator が `PSXRecomp.Core.Recompiler` に実装済みです（`PSXRecomp.Recompiler` という独立プロジェクトはまだ存在しません。[ディレクトリ構成](#ディレクトリ構成) を参照）。これらにより、**synthetic な MIPS fixture** に対する MIPS → IR → 生成された host C → build → bounded execution → interpreter diff → MATCH という end-to-end vertical slice が実装・差分検証済みです（#207、#208、#209、#211。#266 の統合スモークテストで再確認済み）。

最初の実 ROM 関数についても、同じ仕組みで再コンパイル・差分検証済みです（#225）。`RealRomCandidateSelector`（`PSXRecomp.Core.Recompiler`）が、既存のディスク/EXE 解析出力（上記「[現在の開発状況](#現在の開発状況)」のディスクイメージ解析を参照）から、変更を加えていない `MipsToIrLowerer` で実際に lowering を試みて成功し、かつ indirect jump を含まない bounded な命令ウィンドウだけを候補として選定します。実 ROM 専用の第二の semantics 実装は存在しません。これはまだ汎用的な実 ROM 関数の再コンパイルではありません。候補選定は意図的に保守的であり、フルタイトルの再コンパイル、完全な Runtime 統合、完全なハードウェアサポートは未実装です。上記の CPU / デコーダーの実装は Recompiler の基盤ではありますが、Recompiler そのものの代替ではありません。

## Core Capabilities

- レイヤー・依存方向・禁止 API・P/Invoke 配置などのアーキテクチャルールを、文書化するだけでなくコンパイル時に強制。
- 実行エンジンから独立してテスト可能な R3000A/MIPS I 命令デコード・ドメインモデル。
- Delay Slot・例外処理のセマンティクスを正しく扱いながら実際の命令列を実行する Native CPU + Memory Bus。
- 将来の Recompiler バックエンドを interpreter と比較検証するための、決定論的で再生可能な実行トレース（Golden Trace）。
- 決定論的な MIPS→IR→host-C の Recompiler パイプライン。bounded execution と interpreter differential validation を備え、synthetic fixture と最初の実 ROM 関数（#225）の両方で end-to-end に証明済み（汎用的な実 ROM 対応はまだ未実装）。
- Native 実装の詳細を管理層へ漏らさない C# ⇄ C++ の相互運用境界（C ABI + P/Invoke）。

## アーキテクチャ

```text
PSXRecompStudio
├── PSXRecompStudio        # Avalonia UI（Application 層）
├── PSXRecomp.Core         # C# ドメインモデル + C ABI Interop ラッパー
├── PSXRecomp.Native       # C++ Native Core（CPU, Memory, DMA, Timer, Interrupt）
├── architecture.contract.json  # アーキテクチャ SSOT（loach.ArchitectureAnalyzer が NuGet で強制）
├── PSXRecomp.Tests
├── PSXRecompStudio.Tests  # Headless GUI テスト
├── PSXRecomp.Runtime      # 予定
├── PSXRecomp.Recompiler   # 予定（独立プロジェクトとして）。IR/lowering/codegen は現在 PSXRecomp.Core/Recompiler に実装済み
├── PSXRecomp.Debugger     # 予定
└── mcp/                   # 予定（MCP Server）
```

C# / Native の境界は単一の C ABI であり、Native の C++ 型が C# 側に公開されることはありません。

```text
C#（PSXRecomp.Core, NativeInterop）
        │  P/Invoke（[LibraryImport]）
        ▼
C ABI（include/psx_core.h）
        │
        ▼
C++ Native Core（PSXRecomp.Native）
```

レイヤーと依存方向（Domain / Application / Infrastructure / Interop / Special）は [`docs/architecture-matrix.md`](docs/architecture-matrix.md) がコンパイラにより強制される SSOT です。個々の設計判断の根拠は [`docs/adr/`](docs/adr/) を参照してください。システム全体の設計は [`ARCHITECTURE.md`](ARCHITECTURE.md) を参照してください。

## 再コンパイルワークフロー

現在、2 つのパスがあります。どちらも同じ Recompiler contract（変更なし）を通じて実装済みで、interpreter に対して差分検証済みです。**synthetic パス**:

```text
MIPS fixture
        ↓  decode / analysis
Recompiler IR（lowering + validation）
        ↓  決定論的な host C 生成
生成された host C
        ↓  host compile
Bounded execution
        ↓
Interpreter reference execution
        ↓  State Snapshot / checkpoint 比較
Differential validation → MATCH
```

**実 ROM パス**は同じ disc/EXE 解析（関数・命令境界、CFG/basic blocks）と、上と同じ Recompiler IR/lowering/codegen/differential の各段階を再利用します。入力が異なるだけです。`RealRomCandidateSelector` が実 ROM の関数/ウィンドウを選定し、それが正しく lowering でき indirect jump を含まないことそのものが「有効な入力」の条件になっているため、実 ROM 専用の semantics や実行パスは別途存在しません（#225）。

```text
PSX タイトル（ROM/EXE、ユーザーが用意）
        ↓  逆アセンブル・解析（実装済み。Ghidra 連携：予定）
関数・命令境界、MMIO の発見事項、CFG/basic blocks
        ↓  RealRomCandidateSelector: bounded かつ indirect-jump を含まない候補ウィンドウ
Recompiler IR（lowering + validation）   — synthetic パスと同じ段階
        ↓  ... 以降は上記と同じパイプライン ...
Differential validation → MATCH   （最初の1関数で証明済み；#225）
```

候補選定は意図的に保守的です。`MipsToIrLowerer` が実際に lowering できる場合のみ、かつ JR/JALR を含まない場合のみウィンドウを採用し、非対応命令や indirect jump は迂回せず除外します。汎用的な実 ROM 関数対応（任意の関数、全 MIPS I 命令のカバレッジ）と、フルタイトルの静的再コンパイル（実タイトルの全関数、および Runtime・ハードウェア統合）は未実装です。「最初の実 ROM 関数を証明済み」であることを「汎用的な実 ROM またはフルタイトルの再コンパイルが実装済み」と読み替えないでください。両者は別のマイルストーンです。

## 技術スタック

- **UI**: Avalonia UI / C#、MVVM
- **Runtime**: .NET 10+
- **Native Core**: C++17 / CMake / Ninja、C ABI 境界
- **アーキテクチャ強制**: Roslyn Analyzer
- **テスト**: xUnit（C#）、CTest（C++）、Avalonia Headless UI テスト
- **設定**: YAML（予定: タイトル固有差分定義）
- **AI 連携**: MCP（予定）
- **リバースエンジニアリング**: Ghidra（予定）
- **バージョン管理**: Git / GitHub、CI ゲート付き `main`

## ディレクトリ構成

```text
PSXRecompStudio/
├── ARCHITECTURE.md                    # システムアーキテクチャ（SSOT）
├── docs/                              # アーキテクチャ / 開発 SSOT・ADR
├── src/
│   ├── PSXRecompStudio.slnx
│   ├── PSXRecompStudio/               # Avalonia UI
│   ├── PSXRecompStudio.Tests/         # Headless GUI テスト
│   ├── PSXRecomp.Core/                # C# ドメインモデル + P/Invoke Interop
│   ├── PSXRecomp.Native/              # C++ Native Core（CMake プロジェクト）
│   ├── architecture.contract.json     # アーキテクチャ SSOT（loach.ArchitectureAnalyzer）
│   └── PSXRecomp.Tests/               # xUnit テスト（Core + Native、P/Invoke 経由）
├── config/                            # SSOT 設定（Artifact Policy、CPU 命令データ、README 自動化）
├── scripts/                           # CI・開発用スクリプト
└── skills/                            # AI 開発エージェント向け Skill 定義
```

`rom/`（ROM/ISO/BIOS）およびビルド成果物ディレクトリ（`bin/`, `obj/`, `build/`, `native/`）はバージョン管理対象外です。詳細は後述の [ライセンス / 法的事項](#ライセンス--法的事項) を参照してください。

## ビルド

### .NET（UI + C# Core）

```bash
dotnet build src/PSXRecompStudio.slnx --configuration Release
```

### Native Core（C++）

```bash
cd src/PSXRecomp.Native
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

`PSXRecomp.Core` は通常の `dotnet build` の一部として Native Core のビルドをトリガーし、生成された共有ライブラリを自身の出力ディレクトリへコピーします。OS ごとの成果物命名・解決規則の詳細は [`docs/development/native-library-build.md`](docs/development/native-library-build.md) を参照してください。

## テスト

```bash
# Native Core 単体テスト（CMake/CTest）
ctest --test-dir src/PSXRecomp.Native/build --output-on-failure

# C# テストスイート
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release

# Headless GUI テスト（Avalonia、ディスプレイサーバー不要）
dotnet test src/PSXRecompStudio.Tests/PSXRecompStudio.Tests.csproj --configuration Release
```

CI（`.github/workflows/ci.yml`）は Artifact Contamination Gate、Native ビルド／テスト、.NET ビルド／テスト、Headless GUI テストを独立した必須ジョブとして実行し、すべてが通過して初めて PR をマージできます。

## ドキュメント

ドキュメント全体の索引は [`docs/README.md`](docs/README.md) を参照してください。主な入口は以下の通りです。

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — システムアーキテクチャ
- [`docs/architecture-matrix.md`](docs/architecture-matrix.md) — レイヤー・依存方向の SSOT（Analyzer により機械的に強制）
- [`docs/adr/`](docs/adr/) — Architecture Decision Records
- [`docs/cpu/`](docs/cpu/) — R3000A 命令セット、パイプライン、COP0、例外、メモリモデル
- [`docs/architecture/gui-ux.md`](docs/architecture/gui-ux.md) — GUI/UX 設計
- [`docs/development/agent-guide.md`](docs/development/agent-guide.md) — AI 開発エージェント向けブートストラップガイド
- [`docs/development/documentation-policy.md`](docs/development/documentation-policy.md) — API ドキュメント／docstring ポリシー
- [`docs/development/native-library-build.md`](docs/development/native-library-build.md) — Native ライブラリのビルド／成果物規則
- [`docs/development/artifact-policy.md`](docs/development/artifact-policy.md) — Repository Artifact Policy
- [`docs/development/readme-autoupdate.md`](docs/development/readme-autoupdate.md) — README 自動更新の設計
- [`SECURITY.md`](SECURITY.md) — 脆弱性報告

## 開発ワークフロー

`main` ブランチは GitHub Repository Rules により保護されており、直接 push はできません。

```text
feature ブランチ
      ↓  コミット・push
Pull Request
      ↓  CI（Artifact Policy、Native、.NET、GUI テスト）
Human Review
      ↓
main へマージ
```

CI 駆動の Bot が、PR の変更内容が README の記述と実質的に食い違う場合に限り、最小限の `README.md` 更新を同一 PR 上へ提案することがあります。詳細は [`docs/development/readme-autoupdate.md`](docs/development/readme-autoupdate.md) を参照してください。現時点でこの自動化が管理するのは `README.md` のみです。多言語対応へ拡張されるまで、`README.ja.md`（本ファイル）は手動で保守します。

## サポート

このプロジェクトが役に立つと感じた場合は、[GitHub Sponsors](https://github.com/sponsors/mao2009) から支援できます。

義務も特典もありません。ROM ファイル・ゲームデータ・BIOS イメージは本プロジェクトの対象外であり、スポンサーシップの特典にも含まれません。資金の用途についても特別な約束はありません。

## ライセンス / 法的事項

PSXRecompStudio は [MIT License](LICENSE) の下で公開されています。

本リポジトリには著作権のある ROM、ISO、BIOS、CHD 等の PlayStation ディスク／ファームウェアイメージは含まれておらず、今後も含めません。該当ファイルは各自の正当な手段で入手し、バージョン管理へは追加しないでください。ビルド成果物その他の生成ファイルも同様に対象外です。これは文書化されているだけでなく機械的に強制されています。CI の **Artifact Contamination Gate** ジョブが、すべての Pull Request を [`config/artifact-policy.json`](config/artifact-policy.json)（禁止拡張子、禁止パスセグメント、ファイルサイズ上限、バイナリのコンテンツシグネチャ）に照らして検証します。詳細は [`docs/development/artifact-policy.md`](docs/development/artifact-policy.md) を参照してください。
