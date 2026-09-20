# PSXRecompStudio

[![CI](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml/badge.svg)](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

PSXRecompStudio は、PlayStation 1（PS1 / PSX）の**静的再コンパイルとリバースエンジニアリングを研究するための開発環境**です。

最大の特徴は、**差分検証付きの Recompiler パス**です。MIPS → IR/lowering → 決定論的な host C → bounded execution → interpreter state comparison という経路を、synthetic fixture と最初の bounded な実 ROM 関数の両方で検証済みです。商用 PS1 タイトル全体の再コンパイルは**まだ実装されていません**。

*[English README (Canonical / SSOT)](README.md)*

> このファイルは [`README.md`](README.md)（English Canonical / SSOT）の日本語訳です。内容に相違がある場合は `README.md` を正としてください。

## 現在のスコープ

**実装済み・検証済み**

- synthetic な MIPS fixture → Recompiler IR/lowering → 決定論的な host C → gcc build → bounded execution → interpreter state comparison が end-to-end で証明済み（下記の Evidence で再実行）: [`RecompilerVerticalSliceTests.cs`](src/PSXRecomp.Tests/Recompiler/RecompilerVerticalSliceTests.cs)。
- 同じパイプラインを、保守的に選定した最初の bounded な実 ROM 関数に適用（ユーザー提供 ROM が必要）: [`RealRomCandidateSelector`](src/PSXRecomp.Core/Recompiler/RealRomRecompilerBridge.cs)、[`RealRomRecompilerVerticalSliceTests.cs`](src/PSXRecomp.Tests/RealRomAnalysis/RealRomRecompilerVerticalSliceTests.cs)（#225）。
- full-title 実行ループのテストハーネス自身に対する、synthetic かつ常時実行される correctness oracle。実 ROM 実行テストが依拠する「classified-end」アサーションが、実際に誤った実行結果を棄却できることを証明する: [`ObservedTitleExecutionTests.cs`](src/PSXRecomp.Tests/Execution/ObservedTitleExecutionTests.cs)（#378）。

**実装済みの基盤 / 部分実装**

- R3000A / MIPS I の decode/execute、Branch / Load Delay Slot、COP0 / 例外処理、割り込みサンプリング、KSEG0/KSEG1 変換: [`src/PSXRecomp.Core/Cpu/`](src/PSXRecomp.Core/Cpu/)。Native 側では命令単位の Golden Trace がレジスタ書き込みをリタイア順に記録し、将来の backend 比較に備える: [`golden_trace.h`](src/PSXRecomp.Native/tests/golden_trace.h)。
- CHD → ISO 9660 → PS-X EXE → MIPS 解析 → basic blocks/CFG が end-to-end で動作し、[`DiscImageAnalyzerIntegrationTests.cs`](src/PSXRecomp.Tests/DiscImage/DiscImageAnalyzerIntegrationTests.cs) で検証済み。
- title-agnostic な bounded full-title 実行ループ [`ExecutionOrchestrator`](src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs) が、Domain 層の production interpreter engine によって駆動され、Studio UI から到達可能（[ADR-015](docs/adr/015-production-execution-engine-ownership.md)）。Studio は分類済み実行結果へ 2 つの独立したアクションで到達する:`RunDiagnosticTitleCommand`（組み込み診断プログラム）と、Issue #409 による `RunRealTitleCommand`（real-ROM 製品フロー。読み込んだ disc image を解析し、解析済み PS-X EXE（EXE ヘッダ由来の entry PC・SP・GP・text segment）を `RomAnalysisOutcome.Executable` から保持し、その同一の executable を `TitleExecutionService.Run(PsxExe, ...)` → `ExecutionOrchestrator` → `InterpreterTitleExecutionEngine` へ渡す。`RealRomProductionFlowTests` で end-to-end 検証済み）。生成 C（recompiled）engine は現時点でも test 専用のまま。
- interpreter / recompiled の両パスから共有 `BiosVectorDispatch` semantics で A0/B0/C0 vector を dispatch 可能。現在登録済みの service は5個（putchar、puts とその B0 alias、`GetB0Table`、`GetC0Table`）に限られ、広範な BIOS HLE ではない: [`BiosHleRuntime.cs`](src/PSXRecomp.Core/Runtime/BiosHleRuntime.cs)。
- レジスタレベルの DMA / interrupt / timer MMIO adapter と memory bus が専用テスト付きで実装済み: [`src/PSXRecomp.Core/Dma/`](src/PSXRecomp.Core/Dma/)。ただしどの実行エンジンにも結線されていない。
- 標準 raw 128 KiB PlayStation メモリーカードイメージを変換なしで読み書きし、他エミュレータとカードを共有可能。Slot 1 / Slot 2 の設定、アトミックな保存、外部変更の検出に対応: [`src/PSXRecomp.Core/MemoryCard/`](src/PSXRecomp.Core/MemoryCard/)、[`FileMemoryCardStorage.cs`](src/PSXRecompStudio/Services/FileMemoryCardStorage.cs)、[`docs/runtime/memory-card.md`](docs/runtime/memory-card.md)（#22）。メモリーカードの SIO/IRQ7 プロトコルおよびカード UI は未実装。
- 最初の実走行可能な recompiled artifact の証明（Issue #461）: synthetic な PS-X EXE を production パイプライン全体 — `PsxExe.Load` → `PsxExeTitleInput.Build` → decode/lowering → host + artifact コード生成 → gcc build（#458）→ launcher 実行（#459）— で通し、generated/recompiled code が実際に実行されたこと、決定論的であること（生成ソースに至るまで）、分類済み境界でのみ停止すること（クラッシュ・静かな終了をしないこと）を検証。合法な実入力を扱う側はユーザー提供の `rom/*.exe` fixture に対して同一パスを実行し、無い場合は明示的に skip: [`RecompiledArtifactE2ETests.cs`](src/PSXRecomp.Tests/E2E/RecompiledArtifactE2ETests.cs)、[`RealExeE2ETests.cs`](src/PSXRecomp.Tests/E2E/RealExeE2ETests.cs)。
- 最小構成のヘッドレス CLI `psxrecomp`。recompiled-artifact build 契約（#458）と runnable-artifact 実行契約（#459）を公開し、`psxrecomp recompile <input.exe|input.chd> --output <dir>` / `psxrecomp run <input.exe|input.chd>` を決定論的な JSON 出力と 0/1/2 の終了コードで提供。Issue #457 より `.chd` 入力は拡張子で振り分けられ、production `RomAnalysisPipeline`（CHD → ISO 9660 → SYSTEM.CNF → boot PS-X EXE → `PsxExeTitleInput`）を通じて解決されるため、正規の CHD は EXE と同じ下流パイプラインを実行する。それ以外の拡張子は従来どおり EXE 専用パス。CHD の失敗はパイプラインが分類し、無効な PS-X EXE としては報告されない: [`src/PSXRecomp.Cli/`](src/PSXRecomp.Cli/)、[`docs/development/headless-cli.md`](docs/development/headless-cli.md)（#460）。汎用コマンドフレームワーク（Issue #15）は対象外。
- `loach.ArchitectureAnalyzer` が [`architecture.contract.json`](src/architecture.contract.json) に基づきアーキテクチャレイヤーを機械的に強制。
- disc 発見 → 解析 → Recompiler slice → orchestrated execution を、ユーザーが合法的に用意した fixture に対して一気通貫で実行し、段階ごとに PASS/FAIL/SKIP を報告する Persona E2E gate: [`scripts/e2e/persona-e2e-gate.ps1`](scripts/e2e/persona-e2e-gate.ps1)、状況は [`docs/v0.1.0/persona-e2e-status.md`](docs/v0.1.0/persona-e2e-status.md) で追跡。実際のタイトル画面への次なる generic runtime blocker は広範な BIOS HLE coverage で、必要な BIOS call がサポートされた後も GPU/SPU/CD-ROM producer が必要です。

**未実装**

- 汎用的な実 ROM 関数再コンパイル — 候補選定は意図的に保守的（[再コンパイルワークフロー](#再コンパイルワークフロー) 参照）。
- 商用 PS1 タイトル全体の end-to-end 静的再コンパイルと実行。
- GPU、SPU、CD-ROM、MDEC、GTE — インターフェース定義のみで、実装・利用する側は存在しない（例: [`IGte.cs`](src/PSXRecomp.Core/Runtime/IGte.cs)）。
- 広範な BIOS HLE service coverage。
- 汎用的なプロダクト CLI。

### Evidence: 実際の差分検証の実行結果

```text
$ dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release \
    --filter "FullyQualifiedName~PSXRecomp.Tests.Recompiler.RecompilerVerticalSliceTests"

Passed PSXRecomp.Tests.Recompiler.RecompilerVerticalSliceTests.VerticalSlice_Matches_Interpreter_On_One_Plus_Two_Equals_Three [312 ms]
Passed PSXRecomp.Tests.Recompiler.RecompilerVerticalSliceTests.VerticalSlice_RecompiledSnapshots_Are_Deterministic_Across_Independent_Runs [623 ms]
Passed PSXRecomp.Tests.Recompiler.RecompilerVerticalSliceTests.VerticalSlice_Produced_Test_Binary_Is_Identical_Across_Runs [320 ms]

Total tests: 3
     Passed: 3
```

これは synthetic な MIPS → IR → 生成された host C → gcc build → bounded execution → interpreter state comparison の経路を、このリポジトリ上で実際に実行した結果です（Issue #209）。実 ROM 版は同一パイプラインをユーザー提供 ROM（`rom/` 配下）に対して実行し、ROM が存在しない場合は明示的に skip されます — 詳細は [`RealRomFixtures.cs`](src/PSXRecomp.Tests/RealRomAnalysis/RealRomFixtures.cs) を参照。

### Evidence: 最初の実走行可能な recompiled artifact（Issue #461）

```text
$ dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release \
    --filter "FullyQualifiedName~PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests"

Passed PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests.SyntheticExe_FullProductionChain_ExecutesGeneratedCodeToCompletion
Passed PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests.SyntheticExe_RepeatedChain_IsDeterministicSourceAndStableBuildInput
Passed PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests.SyntheticExe_UnresolvedJumpBoundary_IsClassifiedBlockedNotCrash
Passed PSXRecomp.Tests.E2E.RecompiledArtifactE2ETests.SyntheticExe_UnsupportedBiosBoundary_IsClassifiedFailureWithDiagnostic

Total tests: 4
     Passed: 4
```

これも同一の production パイプラインの垂直証明です（Issue #461）。synthetic な PS-X EXE 1 本を parse → input bridge → lowering → host/artifact コード生成 → gcc build（#458）→ launcher 実行（#459）で通し、generated/recompiled code が実際に実行されたことと、決定論的であること（繰り返し実行しても生成ソースが完全一致）、そして分類済み境界でのみ停止することを検証します。合法な実入力側は同一パスをユーザー提供の PS-X EXE（`rom/*.exe` 配下）に対して実行し、無い場合は明示的に skip されます — 詳細は [`RealExeE2ETests.cs`](src/PSXRecomp.Tests/E2E/RealExeE2ETests.cs) を参照。

## クイックスタート

### 現在の実装を検証する

```bash
dotnet build src/PSXRecompStudio.slnx --configuration Release
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release
```

成功すれば、現在の CPU / Runtime / Recompiler の契約と、上記の synthetic Recompiler vertical slice が検証されます。実 ROM の differential test は、ユーザー自身が合法的に用意したイメージを必要とし、無い場合は自動的に skip されます。Native Core と Headless GUI のテストについては [ビルド](#ビルド) と [テスト](#テスト) を参照してください。

### Recompiler を探索する

- Recompiler 実装: [`src/PSXRecomp.Core/Recompiler/`](src/PSXRecomp.Core/Recompiler/)（IR/lowering は `MipsToIrLowerer.cs`、host codegen は `RecompilerHostCodeGen.cs`、差分比較は `RecompilerDifferentialResult.cs`）。
- Synthetic な差分検証: [`RecompilerVerticalSliceTests.cs`](src/PSXRecomp.Tests/Recompiler/RecompilerVerticalSliceTests.cs)。
- Bounded な実 ROM 検証: [`RealRomRecompilerVerticalSliceTests.cs`](src/PSXRecomp.Tests/RealRomAnalysis/RealRomRecompilerVerticalSliceTests.cs) と [`RealRomTitleExecutionTests.cs`](src/PSXRecomp.Tests/RealRomAnalysis/RealRomTitleExecutionTests.cs)。
- Native の命令単位 Golden Trace: [`src/PSXRecomp.Native/tests/golden_trace.h`](src/PSXRecomp.Native/tests/golden_trace.h)。
- BIOS vector dispatch: [`BiosVectorDispatch.cs`](src/PSXRecomp.Core/Runtime/BiosVectorDispatch.cs)。

汎用的な再コンパイル CLI はまだ提供されていません。最小のヘッドレス surface（[`docs/development/headless-cli.md`](docs/development/headless-cli.md)）が #458/#459 の契約のみをカバーします。

## 次のマイルストーン

1. **BIOS HLE coverage の拡張（#279、#365）:** 次の real-title execution path に必要な service を実装し、未対応 call は引き続き明示的に失敗させる。
2. **実 ROM 再コンパイル範囲の拡大:** differential validation を正しさの gate として維持したまま、対応命令・制御フローを拡張する。
3. **production の生成ホスト（recompiled）実行 backend:** 再コンパイルされた guest code をコンパイルし、製品の実行 backend として稼働させる（ADR-015 Option B から延期）。

> **Asset policy:** ROM、ISO、CHD、BIOS、firmware image、商用ゲーム asset は本リポジトリに含めません。ユーザーが用意するファイルは合法的に入手・利用してください。

## PSXRecompStudio とは

PSXRecompStudio は、PlayStation 1（PS1）ソフトウェアを解析・リバースエンジニアリングするためのスクラッチ開発の統合開発環境です。PS-X EXE の逆アセンブル、R3000A / MIPS I コードの解析、CPU 挙動のバイト単位で忠実なモデル化を行い、最終的にはタイトルコードを静的に再コンパイルして、エミュレーションを介さずネイティブに直接実行できるプログラムへ変換することを目指します。

Avalonia ベースのデスクトップ UI、C# のドメイン／アプリケーション Core、そして安定した C ABI で接続された C++ ネイティブ Core から構成されます。AI 開発エージェントはプロダクトそのものではなく、Evidence-first な支援手段の一つという位置付けです。

保存（preservation）とリバースエンジニアリングの観点からは、opaque な互換性ヒューリスティックやタイトル固有のハックではなく、再現可能な解析と再検証可能な決定論的実行の根拠を重視しています。

## PSXRecompStudio が目指すもの

- **SSOT 駆動のアーキテクチャ**: アーキテクチャ、CPU 仕様、開発プロセスは [`docs/`](docs/) と [Architecture Decision Records](docs/adr/) に生きた Single Source of Truth として文書化されており、暗黙知に頼りません。
- **機械的に強制される境界**: [`src/architecture.contract.json`](src/architecture.contract.json) で構成された `loach.ArchitectureAnalyzer` がレイヤー違反・依存方向違反・禁止 API 使用をビルドエラーとして検出します。
- **安定した C# / Native 境界**: Native Core とのやり取りはすべて単一の C ABI（`psx_core.h`）経由の P/Invoke で行い、C++ の型を C# 側へ漏らしません。
- **Evidence-first・Human-in-the-loop な AI 協働**: AI 開発エージェントは交換可能な支援手段であり、ワークフローは Agent-agnostic（Claude Code、OpenCode、Codex 等を問わない）です。

## 現在の開発状況

以下は Issue や設計意図ではなく、現在のリポジトリの実装・テスト・CI の状態を反映しています。根拠は上記の [現在のスコープ](#現在のスコープ) を参照してください。

| 領域 | 状態 |
|---|---|
| CPU 実行（decode/execute、Delay Slot、COP0、割り込み、KSEG、Golden Trace） | 実装済み — [`docs/cpu/`](docs/cpu/) |
| Recompiler（synthetic + 最初の実 ROM 関数、差分検証） | 検証済み（bounded） — 汎用的な実 ROM 対応は未実装 |
| 実走行可能な recompiled artifact の E2E（synthetic + 合法な実 EXE） | 実装済み — Issue #461 |
| Disc / executable 解析（CHD → ISO 9660 → PS-X EXE → CFG） | 実装済み |
| Runtime / BIOS 実行境界（A0/B0/C0 dispatch、Studio に結線された production interpreter engine） | 部分実装 — 実 PS-X EXE のロードと interpreter 実行をサポート（#409）。広範な BIOS HLE ではない |
| Hardware — DMA / 割り込み / タイマー（MMIO adapter、memory bus） | 部分実装 — 単体では実装・テスト済みだが、どの実行エンジンにも未結線 |
| Hardware — GPU / SPU / CD-ROM / MDEC / GTE | 予定 — インターフェース定義のみ |
| メモリーカード（標準 raw 128 KiB イメージ、Slot 1/2 設定、安全な保存） | 部分実装 — ストレージとフォーマットは実装済み（[`docs/runtime/memory-card.md`](docs/runtime/memory-card.md)）。SIO/IRQ7 プロトコルとカード UI は未実装 |
| フルタイトルの静的再コンパイル | 未実装 |
| アーキテクチャ強制（Roslyn Analyzer、Artifact Contamination Gate） | 実装済み・CI で強制 |
| Avalonia UI アプリケーションシェル | 実装済み（最小構成。診断実行アクションが1つ。機能 UI は未実装） |
| Debugger / MCP / Ghidra 連携 | 予定 |

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

現在、2 つのパスがあります。どちらも同じ Recompiler contract（変更なし）を通じて実装済みで、interpreter に対して差分検証済みです。

**synthetic パス**:

```text
MIPS fixture → Recompiler IR（lowering + validation） → 決定論的な host C 生成
        → host compile → bounded execution → interpreter reference execution
        → State Snapshot 比較 → Differential validation → MATCH
```

**実 ROM パス**は同じ disc/EXE 解析と、上と同じ Recompiler IR/lowering/codegen/differential の各段階を再利用します。入力が異なるだけです。`RealRomCandidateSelector` が実 ROM のウィンドウを選定し、それが正しく lowering でき indirect jump を含まないことそのものが選定基準になっているため、実 ROM 専用の semantics は別途存在しません（#225）：

```text
PSX タイトル（ROM/EXE、ユーザーが用意） → 逆アセンブル・解析（Ghidra 連携：予定）
        → 関数・命令境界、MMIO の発見事項、CFG/basic blocks
        → RealRomCandidateSelector: bounded かつ indirect-jump を含まない候補ウィンドウ
        → ... 以降は上記と同じ Recompiler IR/codegen/differential パイプライン ...
        → Differential validation → MATCH（最初の1関数で証明済み；#225）
```

候補選定は意図的に保守的です。`MipsToIrLowerer` が実際に lowering できる場合のみ、かつ JR/JALR を含まない場合のみウィンドウを採用します。汎用的な実 ROM 関数対応と、フルタイトルの静的再コンパイル（実タイトルの全関数、および Runtime・ハードウェア統合）は未実装です。「最初の実 ROM 関数を証明済み」であることを「汎用的な実 ROM またはフルタイトルの再コンパイルが実装済み」と読み替えないでください。

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
- [`docs/adr/`](docs/adr/) — Architecture Decision Records（production execution engine については [ADR-015](docs/adr/015-production-execution-engine-ownership.md)）
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
