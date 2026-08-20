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

R3000Aは3レベルのステータススタックを使用する。

```
Bit  Name    Description
0    KUc     Current Kernel/User mode (0=Kernel, 1=User)
1    IEc     Current Interrupt Enable
2    KUp     Previous Kernel/User mode
3    IEp     Previous Interrupt Enable
4    KUo     Oldest Kernel/User mode
5    IEo     Oldest Interrupt Enable
6    CU0     Coprocessor 0 Usability (未使用, always 1 on PSX)
7    CU1     Coprocessor 1 Usability (FPU, 未使用 on PSX)
8-15 IM[7:0] Interrupt Mask (hardware)
16-17 SW     Software Interrupt (R/W)
18-25 IM[9:8] Interrupt Mask (software)
26-27 *      未使用
28    CU2     Coprocessor 2 Usability (GTE)
29    CU3     Coprocessor 3 Usability
30-31 *      未使用
```

### KUc (Kernel/User Current)

- 0: カーネルモード
- 1: ユーザーモード

### IEc (Interrupt Enable Current)

- 0: 割り込み無効
- 1: 割り込み有効

### 3-Level Stack

例外発生時に:
```
KUo ← KUp, IEo ← IEp
KUp ← KUc, IEp ← IEc
KUc ← 0 (カーネル), IEc ← 0 (割り込み無効)
```

RFE時に:
```
KUc ← KUp, IEc ← IEp
KUp ← KUo, IEp ← IEo
```

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
