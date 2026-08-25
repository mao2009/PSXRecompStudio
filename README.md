# PSXRecompStudio

PlayStation 1 (PSX) タイトルの解析・再コンパイル・ネイティブ移植を支援する統合開発環境。

## 概要

PSXRecompStudio は、PlayStation 1 タイトルを現代の PC 環境でネイティブ実行可能な形へ再コンパイル・移植するための開発基盤です。

現在は、将来の再コンパイル機能を支える **CPU ドメインモデル／デコーダー、C# Core、Native Core 境界、アーキテクチャ強制 Analyzer、テスト基盤、AI が参照する開発ドキュメント** の整備を優先しています。

最終的には、タイトル固有差分の定義、逆アセンブル・解析、デバッグ、再コンパイル、MCP 経由の AI 支援・自動テストまでを一貫して扱える開発環境を目指します。

## 現在の開発状況

| 領域 | 状態 |
|---|---|
| Avalonia UI / MVVM 基盤 | 完了 |
| C# Core / Native Core 境界 | 基盤実装済み |
| C ABI / P/Invoke | 基盤実装済み |
| アーキテクチャ強制 Analyzer | 実装済み・CI で強制 |
| Analyzer テスト基盤 | 実装済み |
| R3000A 命令ドメインモデル | 実装済み |
| R3000A 命令 Decoder | 実装済み |
| CPU 実行エンジン | 開発中 |
| GPU / SPU / DMA 等 | 未実装 |
| Runtime | 未実装 |
| Recompiler | 未実装 |
| Debugger | 未実装 |
| MCP / AI | 設計・基盤整備中 |
| Ghidra 連携 | 予定 |

現在は **CPU 実装を中心としたコア基盤の構築フェーズ** です。再コンパイラや GPU/SPU 等の実装を急ぐのではなく、ドメインモデル、アーキテクチャ境界、テスト、解析基盤を先に固めています。

## 開発方針

- **SSOT を優先**: アーキテクチャ、GUI/UX、開発エージェント向け情報をリポジトリ内の文書で管理します。
- **アーキテクチャを機械的に強制**: Roslyn Analyzer によりレイヤーと依存方向の違反をコンパイル時に検出します。
- **CPU をドメインモデルから構築**: R3000A/MIPS I の命令仕様を明示的な型として表現し、Decoder と実行系を分離します。
- **C# / Native の境界を固定**: Native Core との通信は C ABI + P/Invoke を境界とします。
- **テスト可能性を優先**: CPU 命令、Decoder、Analyzer 等を個別に検証できる構成にします。
- **AI 開発との親和性を重視**: AI エージェントが SSOT と検証ルールを参照しながら実装・レビューできる基盤を整備します。
- **成果物をリポジトリに含めない**: ROM / ISO / BIOS やビルド成果物など、生成物・権利上の問題があるファイルは Git 管理対象外とします。

## 技術スタック

- **UI**: Avalonia UI / C#
- **Runtime**: .NET 10+
- **Native Core**: C++ / CMake / Ninja / C ABI
- **Analyzer**: Roslyn Analyzer
- **Test**: xUnit / CTest
- **Configuration**: YAML（将来のタイトル固有差分定義）
- **AI Integration**: MCP / TypeScript（予定）
- **Reverse Engineering**: Ghidra（予定）
- **Version Control**: Git / GitHub

## アーキテクチャ

```text
PSXRecompStudio
├── PSXRecompStudio       # Avalonia UI
├── PSXRecomp.Core        # C# Core / P/Invoke
├── PSXRecomp.Native      # Native Core
├── PSXRecomp.Analyzer    # Architecture enforcement
├── PSXRecomp.Analyzer.Tests
├── PSXRecomp.Tests
├── PSXRecomp.Runtime     # 将来
├── PSXRecomp.Recompiler  # 将来
├── PSXRecomp.Debugger    # 将来
└── mcp/                  # 将来
```

C# と Native の境界は以下の通りです。

```text
C#
  │
  │ P/Invoke
  ▼
C ABI
  │
  ▼
C++ Native Core
```

C++ のクラスを C# に直接公開せず、C ABI を安定した境界として扱います。

詳細な設計は [`ARCHITECTURE.md`](ARCHITECTURE.md) および [`docs/`](docs/) 配下の SSOT を参照してください。

## ディレクトリ構成

```text
PSXRecompStudio/
├── ARCHITECTURE.md
├── docs/                              # プロジェクト / アーキテクチャ SSOT
├── src/
│   ├── PSXRecompStudio.slnx
│   ├── PSXRecompStudio/               # Avalonia UI
│   ├── PSXRecomp.Core/                # C# Core / P/Invoke
│   ├── PSXRecomp.Analyzer/            # Roslyn Analyzer
│   ├── PSXRecomp.Analyzer.Tests/      # Analyzer tests
│   └── PSXRecomp.Tests/               # xUnit tests
├── mcp/                               # MCP Server（将来）
├── rom/                               # ROM / ISO / BIOS（Git 管理対象外）
└── bin/                               # ビルド成果物（Git 管理対象外）
```

## ビルド

### C\#

```bash
dotnet build src/PSXRecompStudio.slnx
```

### テスト

```bash
dotnet test src/PSXRecomp.Analyzer.Tests/PSXRecomp.Analyzer.Tests.csproj
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj
```

Native Core を使用する場合は `src/PSXRecomp.Native` の CMake/Ninja ビルドを行います。

```bash
cd src/PSXRecomp.Native
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
ctest --test-dir build
```

## 開発ドキュメント

AI エージェントを含む開発者は、実装前にリポジトリ内の SSOT を確認してください。

- [`docs/`](docs/) — ドキュメント索引
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — システムアーキテクチャ
- [`docs/architecture-matrix.md`](docs/architecture-matrix.md) — アーキテクチャ境界の SSOT
- [`docs/adr/`](docs/adr/) — Architecture Decision Records
- [`docs/architecture/gui-ux.md`](docs/architecture/gui-ux.md) — GUI / UX 設計文書
- [`docs/development/agent-guide.md`](docs/development/agent-guide.md) — AI 開発エージェント向けガイド

## Git Hooks（main ブランチ保護）

本リポジトリでは、main ブランチへの直接 commit / push を防止する Git hook を導入しています。

clone 後、以下を実行して hook を有効化してください：

```bash
git config core.hooksPath .githooks
```

これにより、以下が自動的に適用されます：

- **pre-commit**: main ブランチ上での commit を拒否
- **pre-push**: main ブランチ上での push を拒否

推奨フロー：
1. feature branch を作成: `git checkout -b feature/your-feature`
2. 変更をコミット・push
3. PR を作成し、レビュー・CI を通過したら main へマージ

## ライセンス

本プロジェクトは [MIT License](LICENSE) の下で公開されています。

## 注意事項

本リポジトリには著作権のある ROM、ISO、BIOS 等は含まれません。対象ファイルは各自の環境で合法的に入手し、Git 管理対象へ追加しないでください。

ビルド成果物や一時生成物もリポジトリへ含めない方針です。この方針は [Repository Artifact Policy](docs/development/artifact-policy.md) として SSOT 化されており、CI の `Artifact Contamination Gate` ジョブが merge 前に機械的に検証します。
