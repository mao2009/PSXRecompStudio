# ADR-003: MIPS ISA / R3000A / PSX Specification Layering

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

PSX CPUの仕様を扱う場合、MIPS ISA、R3000A実装、PSX固有の挙動を混同すると、将来のRecompilerやデバッガーで問題が生じる。例えば、MIPS ISAの仕様とPSXでの実際の挙動が異なる場合がある。

## Decision

仕様を3つのレイヤーに分離して管理する。

### Layer 1: MIPS ISA

MIPS I ISAの一般的な仕様。R2000/R3000/R4000共通。

- 32ビット命令
- R/I/J の3つの命令フォーマット
- 32個のGPR
- Branch delay slot
- Load delay slot
- 32ビットアドレス空間
- リトルエンディアン

### Layer 2: R3000A

R3000A固有の実装仕様。

- 5段パイプライン
- 4KB命令キャッシュ
- 1KBデータキャッシュ
- CP0（System Control Coprocessor）
- COP2（GTE）
- 例外ベクトル（BEVビットによる分岐）
- デバッグレジスタ（DCIC, BPC, BDA等）

### Layer 3: PSX

PSXでの実際の挙動。実機検証に基づく。

- メモリマップ
  - KUSEG: 0x00000000 - 0x7FFFFFFF
  - KSEG0: 0x80000000 - 0x9FFFFFFF（キャッシュ対象）
  - KSEG1: 0xA0000000 - 0xBFFFFFFF（非キャッシュ）
  - KSEG2: 0xC0000000 - 0xFFFFFFFF
- COP0レジスタ（PSX固有の値）
- 例外ベクトル（80000080h）
- GPU（COP2）コマンド
- BIOSコール
- DMA
- 終了条件

### 分離の重要性

```text
MIPS ISA:  "ADD rd, rs, rt は符号付き加算で、オーバーフロー時に例外を発生させる"
R3000A:    "ADDはR-type命令としてopcode=0x00, funct=0x20でエンコードされる"
PSX:       "ADDのオーバーフロー例外はCOP0 CAUSEレジスタにOv(0Ch)として記録される"
```

## Consequences

- 各レイヤーのドキュメントは独立して更新可能
- MIPS ISAの変更はR3000AやPSXに影響しない
- PSX固有の挙動はMIPS ISAと明示的に分離される
- テストは各レイヤーごとに設計可能
