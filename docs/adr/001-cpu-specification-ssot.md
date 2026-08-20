# ADR-001: CPU Specification SSOT Management

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

PSX R3000A CPUの仕様は、将来のCPU実装、Recompiler、Debugger、MCPの基盤となる。複数の情報源（MIPS ISAマニュアル、PSX-SX、PSX開発者ドキュメント、テスト結果）を参照する必要があるが、各情報源の精度や詳細度が異なる。

## Decision

CPU仕様をSSOTとして `docs/cpu/` に管理し、以下の構造で分離する。

### ファイル構成

```text
docs/cpu/
├── r3000a.md              # R3000A全体の概要
├── registers.md           # レジスタセット
├── instruction-format.md  # 命令フォーマット（R/I/J）
├── instruction-set.md     # 命令セット（全命令一覧）
├── exceptions.md          # 例外処理
├── cop0.md                # COP0 レジスタ
├── memory.md              # メモリマップ
├── pipeline.md            # パイプラインとデレイスロット
└── test-specification.md  # テスト仕様
```

### 機械可読な命令定義

```text
config/cpu/
└── r3000a-instructions.yaml  # 全命令のYAML定義
```

### ADR

```text
docs/adr/
├── 001-cpu-specification-ssot.md       # このADR
├── 002-instruction-definition-yaml.md   # 命令定義のYAML形式
├── 003-mips-isa-r3000a-psx-layering.md  # 仕様のレイヤー分離
├── 004-branch-load-delay-modeling.md    # デレイスロットのモデル化
└── 005-pc-model.md                     # PC更新のモデル化
```

### 分離原則

1. **MIPS ISA**: 一般的なMIPS I ISAの仕様
2. **R3000A**: R3000A固有の実装仕様
3. **PSX**: PSXでの実際の挙動（COP0仕様、メモリマップ、例外ベクトル等）

### 参照優先度

1. 本プロジェクトの `docs/cpu/` （SSOT）
2. PSX-SX (psx-spx.consoledev.net)
3. MIPS R3000 Hardware Manual
4. IDT R30xx Family Software Reference Manual
5. 他エミュレータの実装（参考のみ）

## Consequences

- 仕様の変更はSSOTを優先的に更新する
- 外部資料との矛盾がある場合は、PSX実機での検証結果を優先する
- MCP/AIはSSOTを参照元として利用する
