# R3000A COP0 Registers

## Register Summary

| Reg | Name | R/W | Description |
|-----|------|-----|-------------|
| r0 | * | - | 未使用 |
| r1 | * | - | 未使用 |
| r2 | * | - | 未使用 |
| r3 | BPC | R/W | Breakpoint Program Counter |
| r4 | * | - | 未使用 |
| r5 | BDA | R/W | Breakpoint Data Address |
| r6 | TAR | R | Jump Target Address |
| r7 | DCIC | R/W | Debug and Cache Invalidate Control |
| r8 | BadVaddr | R | Bad Virtual Address |
| r9 | BDAM | R/W | Breakpoint Data Address Mask |
| r10 | * | - | 未使用 |
| r11 | BPCM | R/W | Breakpoint Program Counter Mask |
| r12 | SR | R/W | System Status Register |
| r13 | CAUSE | R/W* | Exception Cause (* bit 8-9 R/W) |
| r14 | EPC | R | Exception Program Counter |
| r15 | PRID | R | Processor Revision Identifier |

## SR (System Status Register) - cop0r12

```
Bit  Name    Description
0    IE      Interrupt Enable
1    EXL     Exception Level
2    BEV     Bootstrap Exception Vector
3-5  *       未使用
6-7  CU0/1   Coprocessor Usability (bit6=CP0, bit7=CP1)
8-15 IM[7:0] Interrupt Mask (hardware)
16-25 IM[9:8] Interrupt Mask (software, R/W in CAUSE)
26-27 *      未使用
28    CU2     Coprocessor 2 Usability (GTE)
29    CU3     Coprocessor 3 Usability
30-31 *      未使用
```

### IE (Interrupt Enable)

- 0: 割り込み無効
- 1: 割り込み有効

### EXL (Exception Level)

- 0: 通常動作
- 1: 例外処理中（割り込み無効）

### BEV (Bootstrap Exception Vector)

- 0: 例外ベクトル 80000080h
- 1: 例外ベクトル BFC00180h

## CAUSE (Exception Cause) - cop0r13

```
Bit  Name    Description
0-1  *       未使用（ゼロ）
2-6  Excode  例外コード
7    *       未使用（ゼロ）
8-9  IP[1:0] 割り込みペンディング（R/W）
10-15 IP[7:2] 割り込みペンディング（hardware, R only）
16-27 *      未使用（ゼロ）
28-29 CE     Coprocessor Error（opcode bit 26-27）
30    *      未使用（PSX固有: branch condition when BD=1）
31    BD     Branch Delay
```

### IP (Interrupt Pending)

| Bit | Source |
|-----|--------|
| IP[0] | Software interrupt 0 |
| IP[1] | Software interrupt 1 |
| IP[2] | Hardware: VBlank |
| IP[3] | Hardware: GPU |
| IP[4] | Hardware: CD-ROM |
| IP[5] | Hardware: DMA |
| IP[6] | Hardware: TMR0 |
| IP[7] | Hardware: TMR1 |

## EPC (Exception Program Counter) - cop0r14

- 例外発生時のPCを保存
- 遅延スロット内なら分岐命令のアドレス
- RFEでPCに復元

## BadVaddr (Bad Virtual Address) - cop0r8

- アドレスエラー発生時のアドレスを保存
- AdEL (ExcCode 0x04) と AdES (ExcCode 0x05) のみ更新
- それ以外の例外では更新されない

## DCIC (Debug and Cache Invalidate Control) - cop0r7

```
Bit  Name    Description
0    DB      Debug breakpoint occurred (R/W)
1    PC      Program Counter break match (R/W)
2-11 *       未使用
12   BCA     Breakpoint Context Active (R/W)
13   BCO     Breakpoint Condition met (R/W)
14-29 *      未使用
30    UD      User Debug Enable (R/W)
31    TR      Trap Enable (R/W)
```

## COP0 Instructions

| Instruction | Opcode | Description |
|-------------|--------|-------------|
| MFC0 rt, rd | 0x10, rs=0x00 | rt = COP0[rd] |
| MTC0 rt, rd | 0x10, rs=0x04 | COP0[rd] = rt |
| RFE | 0x10, rs=0x10 | 例外から復帰 |

## PSX固有メモ

- TLB関連命令（TLBR, TLBWI, TLBWR, TLBP）はPSXでは基本的に未使用
- PSXは固定マッピングを使用
- GTE命令はCOP2として実行（COP0ではない）
- COP2へのアクセスはSR.CU2ビットで制御
