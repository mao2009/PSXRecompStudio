# R3000A Exception Handling

## Exception Sources

| ExcCode | Mnemonic | Description |
|---------|----------|-------------|
| 0x00 | INT | 外部割り込み |
| 0x01 | MOD | TLB変更（PSXでは未使用） |
| 0x02 | TLBL | TLBロード（PSXでは未使用） |
| 0x03 | TLBS | TLBストア（PSXでは未使用） |
| 0x04 | AdEL | アドレスエラー（ロード/命令フェッチ） |
| 0x05 | AdES | アドレスエラー（ストア） |
| 0x06 | IBE | 命令フェッチバスエラー |
| 0x07 | DBE | データロード/ストアバスエラー |
| 0x08 | Sys | SYSCALL命令 |
| 0x09 | Bp | BREAK命令 |
| 0x0A | RI | 予約命令 |
| 0x0B | CpU | コプロセッサ使用不可 |
| 0x0C | Ov | 算術オーバーフロー |

## Exception Vectors

| Exception | BEV=0 | BEV=1 |
|-----------|-------|-------|
| Reset | BFC00000h | BFC00000h |
| UTLB Miss | 80000000h | BFC00100h |
| COP0 Break | 80000040h | BFC00140h |
| General | 80000080h | BFC00180h |

PSXでは通常BEV=1（BIOS起動時）からBEV=0（BIOSが変更）に切り替わる。

## Exception Processing

### 発生時

1. **EPC保存**: EPC = 遅延スロット内なら分岐命令のアドレス、それ以外なら現在のPC
2. **BD設定**: CAUSE.BD = 1（遅延スロット内なら）
3. **CAUSE設定**: CAUSE.Excode = 例外コード
4. **SR退避**: SRのCurrent-modeをPrevious-modeに退避
5. **割り込み無効化**: SR.IE = 0
6. **PC転送**: PC = 例外ベクトルアドレス

### RFE (Return From Exception)

1. SRのCurrent-modeビットをPrevious-modeに復元
2. IPフィールドを右に2ビットシフト
3. PC = EPC

## Interrupt Handling

### I_STAT (1F801070h)

割り込みステータスレジスタ。Edge-triggered。

| Bit | Source |
|-----|--------|
| 0 | VBlank |
| 1 | GPU |
| 2 | CD-ROM |
| 3 | DMA |
| 4 | TMR0 |
| 5 | TMR1 |
| 6 | TMR2 |
| 7 | SIO |
| 8 | SPU |
| 9 | PIO |

### I_MASK (1F801074h)

割り込みマスクレジスタ。R/W。

## Interrupt Processing

1. I_STAT & I_MASK が非ゼロなら割り込み発生
2. COP0 CAUSE.Ipフィールドに割り込みペンディングを設定
3. SR.IE=1 & SR.EXL=0 の場合、例外として処理

## Exception Priority

```
highest:  I-Fetch (Instruction Fetch)
          RI (Instruction Decode)
          CpU (Instruction Decode)
          TLBL (I-Fetch)
          AdEL (IVA)
          IBE (end of I-Fetch)
          ...
lowest:   ...
```

## Nested Exceptions

例外ハンドラ内でSRを退避/復元しないと、ネストされた例外で問題が生じる。

```asm
# 例外ハンドラ例
mfc0 k0, C0_SR     # SR退避
sw   k0, saved_sr
ori  k0, k0, 0x3   # EXL=1, IE=1
mtc0 k0, C0_SR     # ネスト許可
# ... 処理 ...
lw   k0, saved_sr
mtc0 k0, C0_SR     # SR復元
rfe                # 例外から復帰
```
