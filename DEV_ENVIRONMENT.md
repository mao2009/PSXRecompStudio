# 開発環境 (Development Environment)

検出日: 2026-08-20

## OS

```
Linux nobara-pc 7.1.3-200.nobara.fc44.x86_64
Platform: Fedora 44 (x86_64)
```

## .NET SDK

```
.NET SDK 10.0.109
MSBuild 18.0.11+901ca9412
Runtime: .NET 10.0.9
RID: fedora.44-x64
```

## Git

```
git version 2.55.0
```

## C/C++ コンパイラ

```
GCC 16.1.1 20260515 (Red Hat 16.1.1-2)
```

Clang: 未検出

## ビルドツール

| ツール | バージョン |
|--------|-----------|
| CMake  | 4.3.0     |
| Ninja  | 1.13.2    |

## Python

```
Python 3.14.6
```

## Node.js

```
v22.22.2
```

## Avalonia テンプレート

```
avalonia.app      - Avalonia .NET App
avalonia.mvvm     - Avalonia .NET MVVM App
avalonia.xplat    - Avalonia Cross Platform Application
```

テンプレートはインストール済み。`dotnet new avalonia.app` 等で使用可能。

## 未確認ツール

| ツール | 状態 |
|--------|------|
| Clang  | 未検出（GCC で代替可能） |

## ソリューション構成

```
PSXRecompStudio/
├── src/
│   ├── PSXRecompStudio.slnx          # Solution ファイル (src/ 配下)
│   └── PSXRecompStudio/
│       └── PSXRecompStudio.csproj     # Avalonia UI MVVM プロジェクト
├── mcp/
├── rom/
└── bin/
```

### Solution ファイルの配置場所: `src/`

**理由**: 将来的に `src/` 配下に複数のプロジェクト（Core, Recompiler, Runtime, Debugger 等）が追加されるため、`src/` をソリューションルートとすることで全プロジェクトを一元管理できる。ルートに置くと `src/`, `mcp/` 等の異なるディレクトリにまたがる管理が必要になるため。

## ビルド結果

| モード | 状態 | 出力先 |
|--------|------|--------|
| Debug  | 成功 | `src/PSXRecompStudio/bin/Debug/net10.0/` |
| Release | 成功 | `src/PSXRecompStudio/bin/Release/net10.0/` |

Target Framework: `net10.0`

### 警告

- `Tmds.DBus.Protocol 0.21.2` に既知の脆弱性 (GHSA-xrw6-gwf8-vvr9)。Avalonia の依存パッケージ。次期版で解消される見込み。
