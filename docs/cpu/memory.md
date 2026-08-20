# PSX Memory Map

## Address Space

PSXは32ビットアドレス空間を持ち、リトルエンディアン。

## Physical Memory Map

PSXの物理アドレス空間:

| Address Range | Size | Description |
|---------------|------|-------------|
| 0x00000000 - 0x001FFFFF | 2 MB | PSX RAM |
| 0x1F000000 - 0x1F0FFFFF | 64 KB | Expansion Region 1 |
| 0x1F800000 - 0x1F8003FF | 1 KB | Scratchpad (CPU内部SRAM) |
| 0x1F801000 - 0x1FBFFFFF | - | I/O Ports (Hardware Registers) |
| 0x1FC00000 - 0x1FC7FFFF | 512 KB | BIOS ROM |

## Kernel Memory Segments (MIPS)

| Segment | Address Range | Cache | Physical Mapping |
|---------|---------------|-------|------------------|
| KUSEG | 0x00000000 - 0x7FFFFFFF | Yes | TLB使用（PSXでは固定マッピング） |
| KSEG0 | 0x80000000 - 0x9FFFFFFF | Yes | 物理アドレス & 0x1FFFFFFF |
| KSEG1 | 0xA0000000 - 0xBFFFFFFF | No | 物理アドレス & 0x1FFFFFFF |
| KSEG2 | 0xC0000000 - 0xFFFFFFFF | - | TLB使用（PSXでは基本的に未使用） |

### PSX固有マッピング

PSXは固定マッピングを使用し、TLBは基本的に未使用。

```
KSEG0 (0x80000000): 物理 0x00000000 (RAM, キャッシュ対象)
KSEG1 (0xA0000000): 物理 0x00000000 (RAM, 非キャッシュ)
KSEG1 (0xBF800000): 物理 0x1F800000 (Scratchpad)
KSEG1 (0xBF801000): 物理 0x1F801000 (Hardware Registers)
KSEG1 (0xBFC00000): 物理 0x1FC00000 (BIOS ROM)
```

BIOSは起動時にKSEG1経由でアクセスされ、後にKSEG0にリミラーリングされる。

## Scratchpad (1 KB)

```
物理: 0x1F800000 - 0x1F8003FF
KSEG1: 0xBF800000 - 0xBF8003FF
```

- CPU内部の高速SRAM
- データキャッシュとして使用
- 開発者が明示的に操作する

## Hardware Registers

```
KSEG1: 0xBF801000 - 0xBFBFFFFF
物理: 0x1F801000 - 0x1FBFFFFF
```

主なレジスタ:

| Address | Name | Description |
|---------|------|-------------|
| 0x1F801080 | D0MAR | DMA Channel 0 Memory Address |
| 0x1F801084 | D0BCR | DMA Channel 0 Block Control |
| 0x1F801088 | D0PCR | DMA Channel 0 Control |
| 0x1F801090-1098 | D1MAR-BCR | DMA Channel 1 |
| 0x1F8010A0-10A8 | D2MAR-BCR | DMA Channel 2 |
| 0x1F8010B0-10E8 | D3-D6 | DMA Channel 3-6 |
| 0x1F8010F0 | DPCR | DMA Control Register |
| 0x1F8010F4 | DICR | DMA Interrupt Register |
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
物理: 0x1FC00000 - 0x1FC7FFFF
KSEG1: 0xBFC00000 - 0xBFC7FFFF
```

- BIOSコードとデータ
- 例外ベクトル（BEV=1時: 0xBFC00180）
- システムコール

## Endianness

PSXはリトルエンディアン。

```
Memory address:  A+0  A+1  A+2  A+3
Value:           LSB  ...  ...  MSB
```

 LWは4バイトアラインメントが必要。アンラインアクセスはLWL/LWRで対応。
