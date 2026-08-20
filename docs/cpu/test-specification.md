# CPU Test Specification

PSX R3000A CPU のテスト設計。将来のInterpreter/Recompiler実装時に使用する。

## Test Structure

### Layer 1: Decoder Tests

命令デコードの正確さを検証する。

### Layer 2: Instruction Tests

各命令の基本的な動作を検証する。

### Layer 3: Pipeline Tests

遅延スロット、ロードデレイ、例外処理を検証する。

### Layer 4: Integration Tests

複数命令の組み合わせを検証する。

---

## Layer 1: Decoder Tests

### R-Type Decode

| Test ID | Input | Expected |
|---------|-------|----------|
| DEC-R-001 | opcode=0x00, funct=0x20 | ADD |
| DEC-R-002 | opcode=0x00, funct=0x21 | ADDU |
| DEC-R-003 | opcode=0x00, funct=0x24 | AND |
| DEC-R-004 | opcode=0x00, funct=0x00 | SLL |
| DEC-R-005 | opcode=0x00, funct=0x08 | JR |
| DEC-R-006 | opcode=0x00, funct=0x0C | SYSCALL |

### I-Type Decode

| Test ID | Input | Expected |
|---------|-------|----------|
| DEC-I-001 | opcode=0x08 | ADDI |
| DEC-I-002 | opcode=0x09 | ADDIU |
| DEC-I-003 | opcode=0x23 | LW |
| DEC-I-004 | opcode=0x2B | SW |
| DEC-I-005 | opcode=0x04 | BEQ |

### J-Type Decode

| Test ID | Input | Expected |
|---------|-------|----------|
| DEC-J-001 | opcode=0x02 | J |
| DEC-J-002 | opcode=0x03 | JAL |

---

## Layer 2: Instruction Tests

### Arithmetic Instructions

#### ADD

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ADD-001 | 通常加算 | rs=5, rt=3 | rd=8 |
| ADD-002 | 正のオーバーフロー | rs=0x7FFFFFFF, rt=1 | trap(Ov) |
| ADD-003 | 負のオーバーフロー | rs=0x80000000, rt=-1 | trap(Ov) |
| ADD-004 | ゼロ加算 | rs=0, rt=0 | rd=0 |
| ADD-005 | マイナス加算 | rs=-1, rt=-1 | rd=-2 |

#### ADDU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ADDU-001 | 通常加算 | rs=5, rt=3 | rd=8 |
| ADDU-002 | オーバーフローwraparound | rs=0xFFFFFFFF, rt=1 | rd=0 |
| ADDU-003 | ゼロ加算 | rs=0, rt=0 | rd=0 |

#### ADDI

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ADDI-001 | 通常加算 | rs=5, imm=3 | rt=8 |
| ADDI-002 | 負の即値 | rs=10, imm=-5 | rt=5 |
| ADDI-003 | オーバーフロー | rs=0x7FFFFFFF, imm=1 | trap(Ov) |

#### ADDIU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ADDIU-001 | 通常加算 | rs=5, imm=3 | rt=8 |
| ADDIU-002 | オーバーフローwraparound | rs=0xFFFFFFFF, imm=1 | rt=0 |

#### SUB

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SUB-001 | 通常減算 | rs=10, rt=3 | rd=7 |
| SUB-002 | 負の結果 | rs=3, rt=10 | rd=-7 |
| SUB-003 | オーバーフロー | rs=0x7FFFFFFF, rt=-1 | trap(Ov) |
| SUB-004 | 同値 | rs=5, rt=5 | rd=0 |

#### SUBU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SUBU-001 | 通常減算 | rs=10, rt=3 | rd=7 |
| SUBU-002 | オーバーフローwraparound | rs=0, rt=1 | rd=0xFFFFFFFF |

#### SLT

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SLT-001 | 小さい場合 | rs=3, rt=5 | rd=1 |
| SLT-002 | 大きい場合 | rs=5, rt=3 | rd=0 |
| SLT-003 | 同値 | rs=5, rt=5 | rd=0 |
| SLT-004 | 負の値 | rs=-1, rt=0 | rd=1 |
| SLT-005 | 符号付き比較 | rs=0xFFFFFFFF(-1), rt=1 | rd=1 |

#### SLTU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SLTU-001 | 小さい場合 | rs=3, rt=5 | rd=1 |
| SLTU-002 | 大きい場合 | rs=5, rt=3 | rd=0 |
| SLTU-003 | 符号なし比較 | rs=0xFFFFFFFF(很大), rt=1 | rd=0 |

### Logical Instructions

#### AND

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| AND-001 | ビット演算 | rs=0xFF, rt=0x0F | rd=0x0F |
| AND-002 | ゼロ | rs=0xFF, rt=0x00 | rd=0x00 |
| AND-003 | 全ビット | rs=0xFF, rt=0xFF | rd=0xFF |

#### OR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| OR-001 | ビット演算 | rs=0xF0, rt=0x0F | rd=0xFF |
| OR-002 | ゼロ | rs=0xFF, rt=0x00 | rd=0xFF |

#### XOR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| XOR-001 | ビット演算 | rs=0xFF, rt=0x0F | rd=0xF0 |
| XOR-002 | 同値 | rs=0xFF, rt=0xFF | rd=0x00 |

#### NOR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| NOR-001 | ビット演算 | rs=0xF0, rt=0x0F | rd=0x00 |
| NOR-002 | ゼロ | rs=0x00, rt=0x00 | rd=0xFFFFFFFF |

#### ANDI

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ANDI-001 | 0拡張 | rs=0xFF00, imm=0x0F | rt=0x0000 |
| ANDI-002 | 上位ビット | rs=0xFFFF, imm=0xFF | rt=0x00FF |

#### ORI

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| ORI-001 | 0拡張 | rs=0xF000, imm=0x0F | rt=0xF00F |

#### LUI

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LUI-001 | 基本 | imm=0x1234 | rt=0x12340000 |
| LUI-002 | ゼロ | imm=0x0000 | rt=0x00000000 |

### Shift Instructions

#### SLL

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SLL-001 | 1ビット左シフト | rt=1, shamt=1 | rd=2 |
| SLL-002 | 31ビット | rt=1, shamt=31 | rd=0x80000000 |
| SLL-003 | shamt=0 | rt=0x1234, shamt=0 | rd=0x1234 |

#### SRL

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SRL-001 | 1ビット右シフト | rt=2, shamt=1 | rd=1 |
| SRL-002 | 符号拡張なし | rt=0x80000000, shamt=1 | rd=0x40000000 |

#### SRA

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SRA-001 | 正の値 | rt=4, shamt=1 | rd=2 |
| SRA-002 | 負の値（符号拡張） | rt=0x80000000, shamt=1 | rd=0xC0000000 |

#### SLLV

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SLLV-001 | レジスタ指定 | rt=1, rs=3 | rd=8 |

#### SRLV

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SRLV-001 | レジスタ指定 | rt=8, rs=3 | rd=1 |

#### SRAV

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SRAV-001 | 負の値 | rt=0x80000000, rs=1 | rd=0xC0000000 |

### Multiply / Divide Instructions

#### MULT

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| MULT-001 | 正×正 | rs=3, rt=5 | HI:LO=0x00000000:0x0000000F |
| MULT-002 | 負×正 | rs=-1, rt=5 | HI:LO=0xFFFFFFFF:0xFFFFFFFB |
| MULT-003 | ゼロ | rs=0, rt=5 | HI:LO=0:0 |

#### MULTU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| MULTU-001 | 通常 | rs=3, rt=5 | HI:LO=0:15 |
| MULTU-002 | 大きい値 | rs=0xFFFFFFFF, rt=2 | HI:LO=0x00000001:0xFFFFFFFE |

#### DIV

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| DIV-001 | 通常 | rs=10, rt=3 | LO=3, HI=1 |
| DIV-002 | 負の値 | rs=-10, rt=3 | LO=-3, HI=-1 |
| DIV-003 | ゼロ除算(正) | rs=5, rt=0 | LO=0xFFFFFFFF, HI=5 |
| DIV-004 | ゼロ除算(負) | rs=-5, rt=0 | LO=0x00000001, HI=-5 |

#### DIVU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| DIVU-001 | 通常 | rs=10, rt=3 | LO=3, HI=1 |
| DIVU-002 | ゼロ除算 | rs=5, rt=0 | LO=0xFFFFFFFF, HI=5 |

#### MFHI / MTHI / MFLO / MTLO

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| MFHI-001 | HI読み取り | HI=0x1234 | rd=0x1234 |
| MFLO-001 | LO読み取り | LO=0x5678 | rd=0x5678 |
| MTHI-001 | HI書き込み | rs=0xABCD | HI=0xABCD |
| MTLO-001 | LO書き込み | rs=0xEF01 | LO=0xEF01 |

### Load Instructions

#### LB / LBU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LB-001 | 正のバイト | mem[addr]=0x7F | rt=0x0000007F |
| LB-002 | 負のバイト | mem[addr]=0xFF | rt=0xFFFFFFFF |
| LBU-001 | 0拡張 | mem[addr]=0xFF | rt=0x000000FF |

#### LH / LHU

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LH-001 | 正のハーフワード | mem[addr:16]=0x7FFF | rt=0x00007FFF |
| LH-002 | 負のハーフワード | mem[addr:16]=0xFFFF | rt=0xFFFFFFFF |
| LHU-001 | 0拡張 | mem[addr:16]=0xFFFF | rt=0x0000FFFF |

#### LW

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LW-001 | 通常ロード | mem[addr:32]=0x12345678 | rt=0x12345678 |

#### LWL / LWR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| LWL-001 | byte_offset=0 | mem[addr]=0xAA | rt[31:24]=0xAA |
| LWL-002 | byte_offset=3 | mem[addr:3]=0xAA BB CC DD | rt=0xAABBCCDD |
| LWR-001 | byte_offset=0 | mem[addr]=0xDD | rt[7:0]=0xDD |
| LWR-002 | byte_offset=3 | mem[addr:3]=0xAA BB CC DD | rt=0xAABBCCDD |
| LWL-LWR-001 | 組み合わせ | mem[addr:3]=0xAA BB CC DD | rt=0xAABBCCDD |

### Store Instructions

#### SB

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SB-001 | 通常ストア | rt=0x12345678 | mem[addr]=0x78 |

#### SH

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SH-001 | 通常ストア | rt=0x12345678 | mem[addr:16]=0x5678 |

#### SW

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SW-001 | 通常ストア | rt=0x12345678 | mem[addr:32]=0x12345678 |

#### SWL / SWR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SWL-001 | byte_offset=0 | rt=0xAABBCCDD | mem[addr]=0xAA |
| SWL-002 | byte_offset=3 | rt=0xAABBCCDD | mem[addr:3]=0xAABBCCDD |
| SWR-001 | byte_offset=0 | rt=0xAABBCCDD | mem[addr]=0xDD |
| SWR-002 | byte_offset=3 | rt=0xAABBCCDD | mem[addr:3]=0xAABBCCDD |

### Jump Instructions

#### J

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| J-001 | 基本ジャンプ | instr_index=0x100 | PC=0x00000400 |
| J-002 | 上位4ビット維持 | PC=0x80001000, instr_index=0x200 | PC=0x80000800 |

#### JAL

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| JAL-001 | 基本ジャンプ＆リンク | PC=0x1000, instr_index=0x100 | PC=0x00000400, $31=0x1008 |

#### JR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| JR-001 | 基本 | rs=0x80001234 | PC=0x80001234 |

#### JALR

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| JALR-001 | 基本 | PC=0x1000, rs=0x80001234 | PC=0x80001234, $31=0x1008 |

### Branch Instructions

#### BEQ

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BEQ-001 | 分岐成立 | rs=5, rt=5 | PC=branch_target |
| BEQ-002 | 分岐不成立 | rs=5, rt=3 | PC=PC+8 |

#### BNE

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BNE-001 | 分岐成立 | rs=5, rt=3 | PC=branch_target |
| BNE-002 | 分岐不成立 | rs=5, rt=5 | PC=PC+8 |

#### BLEZ

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BLEZ-001 | 0以下 | rs=0 | PC=branch_target |
| BLEZ-002 | 負の値 | rs=-1 | PC=branch_target |
| BLEZ-003 | 正の値 | rs=1 | PC=PC+8 |

#### BGTZ

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BGTZ-001 | 正の値 | rs=1 | PC=branch_target |
| BGTZ-002 | 0 | rs=0 | PC=PC+8 |

### Special Instructions

#### SYSCALL

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| SYSCALL-001 | 基本 | - | trap(SyscallException) |

#### BREAK

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| BREAK-001 | 基本 | - | trap(BreakpointException) |

### Coprocessor Instructions

#### MFC0 / MTC0

| Test ID | Description | Input | Expected |
|---------|-------------|-------|----------|
| MFC0-001 | COP0読み取り | COP0[rd]=0x1234 | rt=0x1234 |
| MTC0-001 | COP0書き込み | rt=0x5678 | COP0[rd]=0x5678 |

---

## Layer 3: Pipeline Tests

### Branch Delay Slot

| Test ID | Description | Expected |
|---------|-------------|----------|
| DELAY-B-001 | 分岐遅延スロット基本 | 遅延スロット命令が実行される |
| DELAY-B-002 | 分岐成立時の遅延スロット | 遅延スロット実行後、ターゲットにジャンプ |
| DELAY-B-003 | 分岐不成立時の遅延スロット | 遅延スロット実行後、次の命令を継続 |
| DELAY-B-004 | 遅延スロット内の分岐 | UNPREDICTABLE |
| DELAY-B-005 | 遅延スロット内で例外 | EPC=分岐命令のアドレス, BD=1 |

### Load Delay Slot

| Test ID | Description | Expected |
|---------|-------------|----------|
| DELAY-L-001 | ロードデレイ基本 | ロード直後の命令は古い値を見る |
| DELAY-L-002 | ロードデレイ後の命令 | 2命令目は新しい値を見る |
| DELAY-L-003 | LWL/LWRの特殊回路 | 先行ロードの値を読むことができる |
| DELAY-L-004 | ロードデレイとJAL | BIOSコードとの互換性 |

### Exception in Delay Slot

| Test ID | Description | Expected |
|---------|-------------|----------|
| DELAY-E-001 | 遅延スロット内でSYSCALL | EPC=分岐命令, BD=1 |
| DELAY-E-002 | 遅延スロット内でBREAK | EPC=分岐命令, BD=1 |

---

## Layer 4: Integration Tests

### Common Patterns

| Test ID | Description | Expected |
|---------|-------------|----------|
| INT-001 | 関数呼び出し (JAL + JR) | 正しい戻りアドレス |
| INT-002 | 条件分岐 (BEQ + delay) | 正しい分岐先 |
| INT-003 | ループ (BNE + delay) | 正しいループ動作 |
| INT-004 | LUI + ORI (32bit即値) | 正しい32ビット値 |
| INT-005 | メモリコピー (LW/SW) | 正しいデータ転送 |

### BIOS Compatibility

| Test ID | Description | Expected |
|---------|-------------|----------|
| BIOS-001 | BIOS起動シーケンス | 正常起動 |
| BIOS-002 | BIOSコール | 正しいコール結果 |

---

## Test Execution Strategy

### Native C++ Tests

1. 各命令のデコードテスト
2. 各命令の基本実行テスト
3. 遅延スロットテスト
4. 例外処理テスト

### C# Integration Tests

1. P/Invoke経由の命令実行テスト
2. レジスタ状態の確認
3. メモリアクセステスト

### External Test Programs

- Amidog's psxtest_cpu.exe（将来使用）
