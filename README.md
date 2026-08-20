# PSXRecompStudio

PlayStation 1 タイトルのネイティブ再コンパイル・移植のための統合開発環境。

## 概要

PSXRecompStudio は、PlayStation 1 (PSX) ゲームを現代の PC OS 上でネイティブ実行可能な形へ再コンパイル・移植するための開発環境です。

タイトル固有の差分定義 (YAML)、逆アセンブル、デバッグ、再コンパイル、MCP サーバーによる AI ベースのゲーム操作・自動テストまでを統合的目标としています。

## 開発段階

Phase 1 — プロジェクト構造の確立と Avalonia UI の最小起動確認完了。

| 項目 | 状態 |
|------|------|
| Avalonia UI MVVM プロジェクト | 完了 |
| .NET Solution 構成 | 完了 |
| Debug/Release ビルド | 成功 |
| Linux 起動確認 | 成功 |
| PSX Core / Recompiler / Debugger | 未着手 |
| MCP Server | 未着手 |
| YAML スキーマ | 未着手 |

## 対応 OS

- Windows 10/11
- Linux (x86_64)
- macOS 12+

## 技術スタック

- **UI**: Avalonia UI / C#
- **ランタイム**: .NET 10+
- **ネイティブ連携**: C/C++, CMake, Ninja
- **設定管理**: YAML
- **MCP Server**: Node.js / TypeScript
- **逆アセンブル連携**: Ghidra (予定)
- **バージョン管理**: Git

## ディレクトリ構成

```
PSXRecompStudio/
├── src/
│   ├── PSXRecompStudio.slnx              # .NET Solution
│   └── PSXRecompStudio/                  # Avalonia UI アプリケーション
│       ├── PSXRecompStudio.csproj
│       ├── Program.cs
│       ├── App.axaml / App.axaml.cs
│       ├── ViewLocator.cs
│       ├── ViewModels/
│       │   ├── ViewModelBase.cs
│       │   └── MainWindowViewModel.cs
│       ├── Views/
│       │   ├── MainWindow.axaml
│       │   └── MainWindow.axaml.cs
│       └── Assets/
├── mcp/                                  # MCP Server (将来)
├── rom/                                  # PSX ROM/ISO/BIOS (Git 管理対象外)
├── bin/                                  # ビルド成果物 (Git 管理対象外)
├── .gitignore
├── README.md
└── DEV_ENVIRONMENT.md
```

## 注意事項

本リポジトリには著作権のある ROM、ISO、BIOS 等は含まれません。配布・Git 管理対象外です。ユーザーは各自の環境で合法に入手した対象ファイルを使用してください。
