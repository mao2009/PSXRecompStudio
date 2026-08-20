# ADR-002: Machine-Readable Instruction Definition (YAML)

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

PSX R3000A CPU命令を将来的にDecoder、Interpreter、Recompiler、Test generator、Debugger、MCPから利用する必要がある。人間が読める仕様と機械可読な定義を分離する必要がある。

## Decision

命令定義をYAML形式で `config/cpu/r3000a-instructions.yaml` に管理する。

### YAML構造

```yaml
# メタ情報
meta:
  version: "1.0"
  description: "PSX R3000A CPU Instruction Set Definitions"
  references:
    - "MIPS R3000 Hardware Manual"
    - "PSX-SX (psx-spx.consoledev.net)"
    - "IDT R30xx Family Software Reference Manual"

# 命令定義
instructions:
  - name: ADD
    opcode: 0x00
    funct: 0x20
    format: R
    category: arithmetic
    operands:
      - type: register
        name: rd
        bits: [15, 11]
      - type: register
        name: rs
        bits: [25, 21]
      - type: register
        name: rt
        bits: [20, 16]
    semantics: |
      temp = GPR[rs] + GPR[rt]
      if overflow(temp) then
        trap(OV)
      else
        GPR[rd] = temp[31:0]
    flags:
      overflow: true
      signed: true
    delay_slot: false
    exceptions:
      - Ov
    references:
      - "MIPS R3000A, ADD instruction"
```

### 設計方針

1. **Decoder**: opcode + funct + format から命令を特定
2. **Interpreter**: semantics から実行ロジックを生成
3. **Recompiler**: operands + semantics からIR生成
4. **Test generator**: test_cases からテストコードを生成
5. **Debugger**: name + operands から逆アセンブル
6. **MCP**: 全フィールドを参照可能

### 拡張性

- 将来の命令追加に対応
- PSX固有の命令（COP2/GTE）への拡張
- タイトル固有の命令差分への対応

## Consequences

- YAMLのスキーマは将来的にJSON Schemaで検証可能
- テスト生成はYAMLから自動生成
- MCPサーバーはYAMLを直接参照可能
