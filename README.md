# PSXRecompStudio

PlayStation 1 タイトルのネイティブ再コンパイル・移植のための統合開発環境。

## 概要

PSXRecompStudio は、PlayStation 1 (PSX) ゲームを現代の PC OS 上でネイティブ実行可能な形へ再コンパイル・移植するための開発環境です。

タイトル固有の差分定義 (YAML)、逆アセンブル、デバッグ、再コンパイル、MCP サーバーによる AI ベースのゲーム操作・自動テストまでを統合的目标としています。

## 開発段階

| Phase | 内容 | 状態 |
|-------|------|------|
| Phase 1 | Avalonia UI MVVM プロジェクト構築 | 完了 |
| Phase 2 | アーキテクチャ文書化、Native Core (C ABI)、C# P/Invoke、テスト基盤 | 完了 |
| Phase 3 | PSX CPU 命令実装 | 未着手 |
| Phase 4 | GPU / SPU / DMA 実装 | 未着手 |
| Phase 5 | 再コンパイラ | 未着手 |
| Phase 6 | デバッガー | 未着手 |
| Phase 7 | MCP Server / AI | 未着手 |

## 対応 OS

- Windows 10/11 (x64)
- Linux (x86_64)
- macOS 12+ (x64, ARM64)

## 技術スタック

- **UI**: Avalonia UI / C#
- **ランタイム**: .NET 10+
- **ネイティブコア**: C++17 / CMake / Ninja / C ABI
- **テスト**: xUnit (C#), CTest (C++)
- **設定管理**: YAML
- **MCP Server**: Node.js / TypeScript
- **逆アセンブル連携**: Ghidra (予定)
- **バージョン管理**: Git

## ディレクトリ構成

```
PSXRecompStudio/
├── ARCHITECTURE.md                    # アーキテクチャ仕様
├── src/
│   ├── PSXRecompStudio.slnx           # .NET Solution
│   ├── PSXRecompStudio/               # Avalonia UI アプリケーション
│   ├── PSXRecomp.Core/                # C# Core: P/Invoke + ラッパー
│   ├── PSXRecomp.Native/              # C++ Core: PSX エミュレーション核
│   │   ├── CMakeLists.txt
│   │   ├── include/psx_core.h         # C ABI ヘッダー
│   │   ├── src/                       # C++ 実装
│   │   └── tests/                     # C++ テスト
│   └── PSXRecomp.Tests/               # xUnit テスト
├── mcp/                               # MCP Server (将来)
├── rom/                               # PSX ROM/ISO/BIOS (Git 管理対象外)
└── bin/                               # ビルド成果物 (Git 管理対象外)
```

## ビルド

### C# (ソリューション全体)

```bash
dotnet build src/PSXRecompStudio.slnx
```

### Native Core

```bash
cd src/PSXRecomp.Native
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

### テスト

```bash
# C# テスト
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj

# C++ テスト
cd src/PSXRecomp.Native && ctest --test-dir build
```

## 注意事項

本リポジトリには著作権のある ROM、ISO、BIOS 等は含まれません。配布・Git 管理対象外です。ユーザーは各自の環境で合法に入手した対象ファイルを使用してください。
