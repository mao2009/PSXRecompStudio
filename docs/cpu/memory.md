# PSX Memory Map

## Address Space

PSXは32ビットアドレス空間を持ち、リトルエンディアン。

## Memory Regions

```
0x00000000 - 0x001FFFFF  PSX RAM (2 MB)
0x00200000 - 0x002FFFFF  Expansion Region 1 (Unused)
0x00300000 - 0x003FFFFF  Scratchpad (1 KB)
0x00400000 - 0x005FFFFF  I/O Ports
0x00600000 - 0x006FFFFF  CD-ROM Drive
0x00700000 - 0x007FFFFF  Audio Processing Unit
0x00800000 - 0x00FFFFFF  Interrupt/Timers/Controllers/Memory Cards
0x01000000 - 0x01FFFFFF  Expansion Region 2
0x1FC00000 - 0x1FC7FFFF  BIOS ROM (512 KB)
```

## Kernel Memory Segments (MIPS)

| Segment | Address Range | Cache | Description |
|---------|---------------|-------|-------------|
| KUSEG | 0x00000000 - 0x7FFFFFFF | Yes | ユーザーモード（2 GB） |
| KSEG0 | 0x80000000 - 0x9FFFFFFF | Yes | カーネル（直接マッピング、キャッシュ対象） |
| KSEG1 | 0xA0000000 - 0xBFFFFFFF | No | カーネル（直接マッピング、非キャッシュ） |
| KSEG2 | 0xC0000000 - 0xFFFFFFFF | - | カーネル（TLB使用） |

### マッピング

```
KUSEG: 0x00000000 - 0x7FFFFFFF → 物理アドレス（TLB使用）
KSEG0: 0x80000000 - 0x9FFFFFFF → 物理アドレス & 0x1FFFFFFF（キャッシュ対象）
KSEG1: 0xA0000000 - 0xBFFFFFFF → 物理アドレス & 0x1FFFFFFF（非キャッシュ）
KSEG2: 0xC0000000 - 0xFFFFFFFF → TLB使用（PSXでは基本的に未使用）
```

## Scratchpad (1 KB)

```
0x00300000 - 0x003003FF (1024 bytes)
```

- CPU内部の高速SRAM
- データキャッシュとして使用
- 開発者が明示的に操作する

## I/O Ports (DMA, GPU, SPU, etc.)

```
0x1F801000 - 0x1F801FFF  Hardware Registers
```

主なレジスタ:

| Address | Name | Description |
|---------|------|-------------|
| 0x1F801000 | DPCR | DMA Control Register |
| 0x1F801004 | DICR | DMA Interrupt Register |
| 0x1F801008-10 | D0-D2 | DMA Channel Registers |
| 0x1F801070 | I_STAT | Interrupt Status |
| 0x1F801074 | I_MASK | Interrupt Mask |
| 0x1F801100 | TMR0 | Timer 0 |
| 0x1F801104 | TMR1 | Timer 1 |
| 0x1F801108 | TMR2 | Timer 2 |
| 0x1F801810 | GP0 | GPU Data |
| 0x1F801814 | GP1 | GPU Status |
| 0x1F801C00 | SPU | SPU Registers |

## BIOS ROM

```
0x1FC00000 - 0x1FC7FFFF (512 KB)
```

- BIOSコードとデータ
- 例外ベクトル（BEV=1時）
- システムコール

## Endianness

PSXはリトルエンディアン。

```
Memory address:  A+0  A+1  A+2  A+3
Value:           LSB  ...  ...  MSB
```

 LW/LWL/LWR命令はアンラインされたアクセスをサポートする。
