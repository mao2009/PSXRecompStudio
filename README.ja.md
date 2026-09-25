# PSXRecompStudio

[![CI](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml/badge.svg)](https://github.com/mao2009/PSXRecompStudio/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

PSXRecompStudio は、PlayStation 1（PS1 / PSX）の静的再コンパイルとリバースエンジニアリングを研究するための環境です。

現在は PS-X EXE / CHD の解析、MIPS から決定論的な再コンパイルパイプラインへの lowering、実行可能なホスト成果物の生成、bounded な実行結果と interpreter の比較まで動作します。ただし、まだ研究段階であり、商用 PS1 タイトル全体の静的再コンパイルは **未実装** です。

[English README](README.md) · [Project website](https://mao2009.github.io/PSXRecompStudio/) · [Latest release](https://github.com/mao2009/PSXRecompStudio/releases/latest)

## 現在できること

- **実行可能な再コンパイルパイプライン:** MIPS → IR/lowering → 決定論的 host code → native artifact → bounded execution。
- **差分検証:** synthetic 入力と bounded な実入力を interpreter 実行と比較できます。
- **PS-X EXE / CHD 入力:** CHD は production の disc-analysis pipeline で解決され、その後は EXE と同じ再コンパイル経路へ入ります。
- **Headless CLI:** `psxrecomp recompile` と `psxrecomp run` で現在の build/run フローを利用できます。
- **診断 bundle:** `psxrecomp run ... --report` は privacy-safe な `diagnostic-report.zip` を生成できます。自動 upload は行いません。
- **Runtime 基盤:** R3000A/MIPS I 実行、Delay Slot、COP0/例外、割り込み、メモリーカード保存、部分的な BIOS/HW model、決定論的 GPU frame snapshot をテストしています。
- **Native 共存:** Native Core は安定した同一 C ABI の背後で C++ と段階的に移行した Rust component を共存させています。
- **Cross-platform 検証:** Linux / Windows / macOS を CI と release workflow で検証しています。

詳細な実装状況や証拠は README に重複させず、維持管理されている docs と tests に置いています。

## 現在の制約

- 商用タイトル全体の静的再コンパイルは未実装です。
- 実タイトル実行は bounded な研究段階であり、互換性を保証するものではありません。
- BIOS HLE coverage は部分的です。
- GPU の product-level integration は未完成で、SPU / CD-ROM / MDEC / GTE も未完成です。
- generated-host path は、完全なタイトルを実行する一般的な production backend にはまだなっていません。

## クイックスタート

### Build と test

```bash
dotnet build src/PSXRecompStudio.slnx --configuration Release
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj --configuration Release
```

Native / Rust の build 詳細は [Native library build](docs/development/native-library-build.md) を参照してください。

### CLI を試す

合法的に入手した PS-X EXE または CHD を使用します。

```bash
psxrecomp recompile <input.exe|input.chd> --output out
psxrecomp run <input.exe|input.chd> --output out
```

診断 bundle を追加する場合:

```bash
psxrecomp run <input.exe|input.chd> --output out --report
```

JSON 出力、exit code、report 内容、入力処理の詳細は [Headless CLI reference](docs/development/headless-cli.md) を参照してください。

## アーキテクチャ

```text
PS-X EXE / CHD
      ↓
disc / executable analysis
      ↓
R3000A / MIPS I
      ↓
IR + lowering
      ↓
deterministic generated host artifact
      ↓
bounded execution + differential validation
```

Application Core は C#/.NET です。Native 機能は安定した C ABI を介して C++ / Rust 混在ライブラリへ接続します。Rust 化は段階的に進めていますが、公開される Native 境界は変えません。

詳細は [ARCHITECTURE.md](ARCHITECTURE.md)、[architecture matrix](docs/architecture-matrix.md)、[ADR](docs/adr/)、[Rust FFI contract](docs/development/rust-ffi-contract.md) を参照してください。

## 実装の証拠

実行可能な証拠は実装の近くに置いています。

- [Synthetic differential recompiler tests](src/PSXRecomp.Tests/Recompiler/RecompilerVerticalSliceTests.cs)
- [Bounded real-ROM recompiler tests](src/PSXRecomp.Tests/RealRomAnalysis/RealRomRecompilerVerticalSliceTests.cs)
- [Runnable recompiled-artifact E2E tests](src/PSXRecomp.Tests/E2E/RecompiledArtifactE2ETests.cs)
- [Real-input E2E tests](src/PSXRecomp.Tests/E2E/RealExeE2ETests.cs)
- [Persona E2E status](docs/v0.1.0/persona-e2e-status.md)

実 ROM test は合法的に用意したユーザー提供 input を必要とし、fixture が無い場合は明示的に skip します。

## ロードマップ

当面の重点は次のとおりです。

1. 実タイトル実行に必要な BIOS / Runtime coverage の拡大。
2. differential validation を correctness gate として維持しながら再コンパイル範囲を拡大。
3. 既存 C++ / Rust ABI 境界の背後で Native 実装の Rust 化を継続。
4. bounded proof から production generated-host execution backend へ進む。

タスク単位の計画は Issue tracker を正とし、README には backlog を複製しません。

## ドキュメント

入口は [docs/README.md](docs/README.md) です。主な資料:

- [System architecture](ARCHITECTURE.md)
- [Architecture / dependency SSOT](docs/architecture-matrix.md)
- [CPU / R3000A documentation](docs/cpu/)
- [Headless CLI](docs/development/headless-cli.md)
- [Native library build](docs/development/native-library-build.md)
- [Rust FFI contract](docs/development/rust-ffi-contract.md)
- [Real-ROM analysis](docs/development/real-rom-analysis.md)
- [Diagnostics and recovery](docs/architecture/diagnostics.md)
- [Memory-card runtime](docs/runtime/memory-card.md)
- [Repository artifact policy](docs/development/artifact-policy.md)

## 開発

`main` は CI gate 付きです。変更は branch で行い、repository の build/test check と review を通して Pull Request から merge します。

Architecture と依存方向は `loach.ArchitectureAnalyzer` が build 時に強制し、repository artifact policy も CI で強制します。

プロジェクト固有の開発ガイドは [docs/development/agent-guide.md](docs/development/agent-guide.md) を参照してください。

## サポート

プロジェクトが役に立つと感じた場合は [GitHub Sponsors](https://github.com/sponsors/mao2009) から支援できます。スポンサーシップに ROM、BIOS、ゲームデータ、互換性保証などの特典は含まれません。

## ライセンス / 法的事項

PSXRecompStudio は [MIT License](LICENSE) の下で公開されています。

本リポジトリには著作権のある ROM、ISO、CHD、BIOS、firmware、商用ゲーム asset を含めません。ユーザー提供 input は合法的に入手・利用し、リポジトリへ commit しないでください。生成物や build artifact も [artifact policy](docs/development/artifact-policy.md) の対象です。
