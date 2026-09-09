# PSX Memory Map

## Address Space

The PSX has a 32-bit address space and uses little-endian byte order.

## Physical Memory Map

PSX physical address space:

| Address Range | Size | Description |
|---------------|------|-------------|
| 0x00000000 - 0x001FFFFF | 2 MB | PSX RAM |
| 0x1F000000 - 0x1F7FFFFF | 8 MB | Expansion Region 1 |
| 0x1F800000 - 0x1F8003FF | 1 KB | Scratchpad (CPU-internal SRAM) |
| 0x1F801000 - 0x1FBFFFFF | - | I/O Ports (Hardware Registers) |
| 0x1FC00000 - 0x1FC7FFFF | 512 KB | BIOS ROM |

## Kernel Memory Segments (MIPS)

| Segment | Address Range | Cache | Physical Mapping |
|---------|---------------|-------|------------------|
| KUSEG | 0x00000000 - 0x7FFFFFFF | Yes | Uses TLB (fixed mapping on PSX) |
| KSEG0 | 0x80000000 - 0x9FFFFFFF | Yes | physical address & 0x1FFFFFFF |
| KSEG1 | 0xA0000000 - 0xBFFFFFFF | No | physical address & 0x1FFFFFFF |
| KSEG2 | 0xC0000000 - 0xFFFDFFFF | - | Uses TLB (generally unused on PSX) |
| KSEG2 | 0xFFFE0000 - 0xFFFFFFFF | - | Cache control registers |

### PSX-Specific Mapping

The PSX uses fixed mappings and generally does not use the TLB.

```text
KSEG0 (0x80000000): physical 0x00000000 (RAM, cached)
KSEG1 (0xA0000000): physical 0x00000000 (RAM, uncached)
KSEG1 (0xBF800000): physical 0x1F800000 (Scratchpad)
KSEG1 (0xBF801000): physical 0x1F801000 (Hardware Registers)
KSEG1 (0xBFC00000): physical 0x1FC00000 (BIOS ROM)
KSEG2 (0xFFFE0000): cache control registers
```

The BIOS is accessed through KSEG1 at startup and is later remirrored into KSEG0.

## Scratchpad (1 KB)

```text
Physical: 0x1F800000 - 0x1F8003FF
KSEG1:    0xBF800000 - 0xBF8003FF
```

- Fast CPU-internal SRAM
- Used as data cache
- Explicitly managed by software

## Hardware Registers

```text
KSEG1:   0xBF801000 - 0xBFBFFFFF
Physical: 0x1F801000 - 0x1FBFFFFF
```

Major registers:

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
Physical: 0x1FC00000 - 0x1FC7FFFF
KSEG1:    0xBFC00000 - 0xBFC7FFFF
```

- BIOS code and data
- Exception vector (when BEV=1: 0xBFC00180)
- System calls

## Endianness

The PSX is little-endian.

```text
Memory address:  A+0  A+1  A+2  A+3
Value:           LSB  ...  ...  MSB
```

LW requires 4-byte alignment (the low 2 address bits must be 0). Unaligned accesses are handled with LWL/LWR.
