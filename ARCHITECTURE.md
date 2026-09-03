# Architecture

PSXRecompStudio のシステムアーキテクチャ。

## プロジェクト目的

PlayStation 1 (PSX) タイトルを解析・再コンパイルし、Windows / Linux / macOS でネイティブ実行可能にするための統合開発環境。

将来的な目標:

- PSX タイトルの再コンパイル
- タイトル固有差分の YAML 定義
- 逆アセンブル・解析・デバッグ
- MCP サーバーによる外部 AI からのゲーム状態取得・操作
- 権利者が公式移植・保存用途に利用できる汎用基盤

## コンポーネント構成

```text
src/
├── PSXRecompStudio/           # Avalonia UI アプリケーション
├── PSXRecomp.Core/            # C# Core: P/Invoke バインディング + ラッパー
├── PSXRecomp.Native/          # C++ Core: PSX エミュレーション核 (C ABI)
├── PSXRecomp.Analyzer/        # Roslyn Analyzer: アーキテクチャ強制
├── PSXRecomp.Analyzer.Tests/  # アナライザーのテスト
├── PSXRecomp.Runtime/         # 将来: PSX ランタイム管理
├── PSXRecomp.Recompiler/      # 将来: 再コンパイラ
├── PSXRecomp.Debugger/        # 将来: デバッガー
├── PSXRecomp.Infrastructure/  # 将来: 共通インフラ
└── PSXRecomp.Tests/           # xUnit テスト
mcp/                           # MCP Server (Node.js / TypeScript)
```

## アーキテクチャ強制

レイヤー属性（`[Domain]` `[Application]` `[Infrastructure]` 等）と依存方向は `PSXRecomp.Analyzer` によりコンパイル時に強制される（PSXR001-006、すべて Error）。

- SSOT: [docs/architecture-matrix.md](docs/architecture-matrix.md)
- 設計判断: [docs/adr/006-architecture-analyzer-enforcement.md](docs/adr/006-architecture-analyzer-enforcement.md)

すべてのクラスにレイヤー属性が必要（マーカー名前空間 `PSXRecomp.Architecture.*`、生成コード、属性済み型の入れ子クラスは除外）。違反はビルドエラーとなり CI も失敗する。

## C# / Native 責務分担

### C# 側 (PSXRecompStudio, PSXRecomp.Core)

- Avalonia UI / MVVM パターン
- プロジェクト管理 (YAML)
- ロギング
- Debugger UI
- Native Core との P/Invoke 連携
- リソース管理 (IDisposable)

### Native 側 (PSXRecomp.Native)

- PSX CPU (R3000A) エミュレーション
- PSX Memory (RAM, BIOS, Hardware Registers)
- PSX Hardware (GPU, SPU, DMA, CD-ROM, Timers, Interrupt Controller — 実装状況は下記「Hardware」節参照)
- 性能が重要な計算全般

### 境界

C# から Native への呼び出しは **C ABI** 経由のみ。

```text
C# (PSXRecomp.Core)
  ↓ P/Invoke
C ABI (psx_core.h)
  ↓
C++ (PSXRecomp.Native)
```

C++ クラスを直接 C# に公開する設計は採用しない。

## C ABI

### 設計方針

- 不透明ポインタ (`PSXCore*`) で状態を隠蔽
- C linkage でエクスポート
- エラーコードではなく戻り値で状態を返す
- メモリ所有権は Create/Destroy で明示

### 最小 API

```c
// Lifecycle
PSXCore* PSXCore_Create(void);
void     PSXCore_Destroy(PSXCore* core);
void     PSXCore_Reset(PSXCore* core);

// CPU Registers
uint32_t PSXCore_GetGPR(PSXCore* core, int index);
void     PSXCore_SetGPR(PSXCore* core, int index, uint32_t value);
uint32_t PSXCore_GetPC(PSXCore* core);
void     PSXCore_SetPC(PSXCore* core, uint32_t value);
uint32_t PSXCore_GetHI(PSXCore* core);
void     PSXCore_SetHI(PSXCore* core, uint32_t value);
uint32_t PSXCore_GetLO(PSXCore* core);
void     PSXCore_SetLO(PSXCore* core, uint32_t value);

// Memory
uint8_t* PSXCore_GetRAM(PSXCore* core);
uint32_t PSXCore_GetRAMSize(void);
```

## PSX Core

### CPU

- R3000A 互換 (MIPS I)
- 32 GPR (General Purpose Registers)
- PC (Program Counter)
- HI / LO (乗除算結果レジスタ)
- CP0 (System Control Coprocessor) の基本抽象化
- ハードウェア割り込みサンプリング: Step/Run 毎に Interrupt Controller の集約ペンディングを CAUSE.IP2 (bit 10) へ反映（詳細: [docs/cpu/exceptions.md](docs/cpu/exceptions.md)）

### Memory

- PSX RAM: 2 MB
- BIOS: 512 KB
- Hardware Register Space

### Hardware (実装状況)

状態は現在のリポジトリ状態（実装・テスト・CI）を反映したものであり、オープン中の Issue や設計上の予定・意図を表すものではない（README の Current Status と同一の基準）。

| コンポーネント | 状態 |
|---------------|------|
| Interrupt Controller | Implemented |
| CPU interrupt integration | Implemented |
| DMA | Partially implemented (register-level model + IRQ + C# MMIO adapter + tests; transfer engine / native 実行パスの MemoryBus 配線は未実装) |
| Timers | Partially implemented (register-level model + tick + IRQ + C# MMIO adapter + tests; GPU 由来 dotclock / HBlank 信号の結線は未実装) |
| GPU | Planned (interface contract only) |
| SPU | Planned (interface contract only) |
| CD-ROM | Planned (interface contract only) |
| MDEC | Planned (interface contract only) |
| GTE | Planned (interface contract only) |

Interrupt Controller はレジスタモデル、C ABI、C# アダプタ、ネイティブテストに加え、CPU の Step/Run 毎に集約ペンディングを CAUSE.IP2 へ反映する CPU 割り込み統合まで実装済み（詳細: [docs/cpu/exceptions.md](docs/cpu/exceptions.md)）。DMA / Timers はレジスタレベルモデルまでが実装されており、C# 側 `MemoryBus` の MMIO ルーティング（アダプタ群）とネイティブテストは存在するが、ネイティブ実行パス（`PSXMemory` の hw_regs 領域）から各コントローラへの完全な結線は進行中。GPU / SPU / CD-ROM / MDEC / GTE は `PSXRecomp.Core/Runtime` のインターフェース契約のみで、ネイティブ実装は存在しない。

## Runtime (将来)

PSX ランタイムは、BIOS ロード、EXE ロード、メモリマッピング、I/O ループを管理する。

## Recompiler

- `PSXRecomp.Core.Recompiler` owns the backend-agnostic IR and shared
  interpreter/recompiled state contract (Issue #206).
- IR values are fixed-width 32-bit guest values; blocks have explicit exits and
  deterministic canonical serialization.
- Lowering, host generation, differential comparison, and the executable
  vertical slice remain separate responsibilities of Issues #207, #208, #211,
  and #209. Memory and control-flow expansion is deferred until the GPR-only
  gate is green.

## Debugger (将来)

- ブレークポイント
- ステップ実行
- レジスタ / メモリビュー
- デザッサンブルビュー
- GPU レンダリングビュー

## MCP (将来)

- Model Context Protocol サーバー
- ゲーム状態の取得・操作
- AI によるプレイ・自動テスト
- Node.js / TypeScript 実装

## YAML

タイトル固有の差分定義に YAML を使用:

- メモリマッピング差分
- 命令別の特殊処理
- GPU レジスタ差分
- リジャイルム定義

## Ghidra (将来)

- 逆アセンブル結果のインポート
- 関数解析結果の活用
- Ghidra スクリプト連携

## Host Platform

### 第一級対応

| OS | アーキテクチャ | 状態 |
|----|---------------|------|
| Windows 10/11 | x64 | 将来対応 |
| Linux | x64 | 開発環境 |
| macOS 12+ | x64 | 将来対応 |
| macOS 12+ | ARM64 | 将来対応 |

### マルチ OS 方針

- PSX 固有処理と Host OS 依存処理を分離
- Wine / Proton を前提としない
- Native Core は CMake でクロスビルド
- C# は .NET のクロスプラットフォームで対応

## C++ 採用理由

| 要件 | 判断 |
|------|------|
| 性能 | PSX CPU エミュレーションは tight loop。C++ は最適化が効く |
| 制御 | メモリレイアウト、命令実行を直接制御する必要がある |
| 既存知見 | PSX エミュレータの多くは C/C++ (DuckStation, PCSX-Redux 等) |
| C ABI | C++ でも `extern "C"` で C ABI を提供可能 |
| 将来性 | JIT リコンパイラ等の実装にも適する |
| Clang | 現環境では GCC のみだが、C++ 自体は標準的 |

Rust も検討したが、既存 PSX エミュレータの知見や C# P/Invoke の親和性から C++ を第一候補とした。

## ビルド構成

```text
Native Core:    CMake + Ninja → .so / .dll / .dylib
C# Core:        dotnet build → .dll
UI:             dotnet build → 実行ファイル
テスト:         dotnet test (C#) + ctest (C++)
```

## 将来コンポーネント

```text
PSXRecompStudio       → UI (Avalonia)
PSXRecomp.Core        → P/Invoke + ラッパー
PSXRecomp.Native      → PSX エミュレーション核
PSXRecomp.Runtime     → ランタイム管理
PSXRecomp.Recompiler  → 再コンパイラ
PSXRecomp.Debugger    → デバッガー
PSXRecomp.Infrastructure → 共通インフラ
mcp/                  → MCP Server
```
