# PSX Memory Map

## Address Space

PSXは32ビットアドレス空間を持ち、リトルエンディアン。

## Physical Memory Map

PSXの物理アドレス空間:

| Address Range | Size | Description |
|---------------|------|-------------|
| 0x00000000 - 0x001FFFFF | 2 MB | PSX RAM |
| 0x1F000000 - 0x1F7FFFFF | 8 MB | Expansion Region 1 |
| 0x1F800000 - 0x1F8003FF | 1 KB | Scratchpad (CPU内部SRAM) |
| 0x1F801000 - 0x1FBFFFFF | - | I/O Ports (Hardware Registers) |
| 0x1FC00000 - 0x1FC7FFFF | 512 KB | BIOS ROM |

## Kernel Memory Segments (MIPS)

| Segment | Address Range | Cache | Physical Mapping |
|---------|---------------|-------|------------------|
| KUSEG | 0x00000000 - 0x7FFFFFFF | Yes | TLB使用（PSXでは固定マッピング） |
| KSEG0 | 0x80000000 - 0x9FFFFFFF | Yes | 物理アドレス & 0x1FFFFFFF |
| KSEG1 | 0xA0000000 - 0xBFFFFFFF | No | 物理アドレス & 0x1FFFFFFF |
| KSEG2 | 0xC0000000 - 0xFFFDFFFF | - | TLB使用（PSXでは基本的に未使用） |
| KSEG2 | 0xFFFE0000 - 0xFFFFFFFF | - | キャッシュ制御レジスタ |

### PSX固有マッピング

PSXは固定マッピングを使用し、TLBは基本的に未使用。

```text
KSEG0 (0x80000000): 物理 0x00000000 (RAM, キャッシュ対象)
KSEG1 (0xA0000000): 物理 0x00000000 (RAM, 非キャッシュ)
KSEG1 (0xBF800000): 物理 0x1F800000 (Scratchpad)
KSEG1 (0xBF801000): 物理 0x1F801000 (Hardware Registers)
KSEG1 (0xBFC00000): 物理 0x1FC00000 (BIOS ROM)
KSEG2 (0xFFFE0000): キャッシュ制御レジスタ
```

BIOSは起動時にKSEG1経由でアクセスされ、後にKSEG0にリミラーリングされる。

## Scratchpad (1 KB)

```text
物理: 0x1F800000 - 0x1F8003FF
KSEG1: 0xBF800000 - 0xBF8003FF
```

- CPU内部の高速SRAM
- データキャッシュとして使用
- 開発者が明示的に操作する

## Hardware Registers

```text
KSEG1: 0xBF801000 - 0xBFBFFFFF
物理: 0x1F801000 - 0x1FBFFFFF
```

主なレジスタ:

| Address | Name | Description |
|---------|------|-------------|
| 0x1F801070 | I_STAT | Interrupt Status |
| 0x1F801074 | I_MASK | Interrupt Mask |
| 0x1F801080 | D0_MADR | DMA Channel 0 Memory Address |
| 0x1F801084 | D0_BCR | DMA Channel 0 Block Control |
| 0x1F801088 | D0_CHCR | DMA Channel 0 Control |
| 0x1F801090 | D1_MADR | DMA Channel 1 Memory Address |
| 0x1F801094 | D1_BCR | DMA Channel 1 Block Control |
| 0x1F801098 | D1_CHCR | DMA Channel 1 Control |
| 0x1F8010A0 | D2_MADR | DMA Channel 2 Memory Address |
| 0x1F8010A4 | D2_BCR | DMA Channel 2 Block Control |
| 0x1F8010A8 | D2_CHCR | DMA Channel 2 Control |
| 0x1F8010B0 | D3_MADR | DMA Channel 3 Memory Address |
| 0x1F8010B4 | D3_BCR | DMA Channel 3 Block Control |
| 0x1F8010B8 | D3_CHCR | DMA Channel 3 Control |
| 0x1F8010C0 | D4_MADR | DMA Channel 4 Memory Address |
| 0x1F8010C4 | D4_BCR | DMA Channel 4 Block Control |
| 0x1F8010C8 | D4_CHCR | DMA Channel 4 Control |
| 0x1F8010D0 | D5_MADR | DMA Channel 5 Memory Address |
| 0x1F8010D4 | D5_BCR | DMA Channel 5 Block Control |
| 0x1F8010D8 | D5_CHCR | DMA Channel 5 Control |
| 0x1F8010E0 | D6_MADR | DMA Channel 6 Memory Address |
| 0x1F8010E4 | D6_BCR | DMA Channel 6 Block Control |
| 0x1F8010E8 | D6_CHCR | DMA Channel 6 Control |
| 0x1F8010F0 | DPCR | DMA Control Register |
| 0x1F8010F4 | DICR | DMA Interrupt Register |
| 0x1F801100 | TMR0 | Timer 0 |
| 0x1F801104 | TMR1 | Timer 1 |
| 0x1F801108 | TMR2 | Timer 2 |
| 0x1F801810 | GP0 | GPU Data |
| 0x1F801814 | GP1 | GPU Status |
| 0x1F801C00 | SPU | SPU Registers |

## BIOS ROM

```text
物理: 0x1FC00000 - 0x1FC7FFFF
KSEG1: 0xBFC00000 - 0xBFC7FFFF
```

- BIOSコードとデータ
- 例外ベクトル（BEV=1時: 0xBFC00180）
- システムコール

## Endianness

PSXはリトルエンディアン。

```text
Memory address:  A+0  A+1  A+2  A+3
Value:           LSB  ...  ...  MSB
```

LWは4バイトアラインメントが必要（アドレスの下位2ビットが0）。アンラインアクセスはLWL/LWRで対応。
