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
4. **SRスタック退避**: 3レベルスタックを右にシフト
   - KUo ← KUp, IEo ← IEp
   - KUp ← KUc, IEp ← IEc
5. **割り込み無効化**: KUc ← 0（カーネル）, IEc ← 0（割り込み無効）
6. **PC転送**: PC = 例外ベクトルアドレス

### RFE (Return From Exception)

1. SRの3レベルスタックをポップ
   - KUc ← KUp, IEc ← IEp
   - KUp ← KUo, IEp ← IEo
   - KUo/IEo（SR bit 4-5）はRFEでは変更しない
2. RFE自体はPCを変更しない。PC復元はソフトウェアの責務であり、通常はEPCを
   MFC0でGPRに読み出した上でJRにより行う（遅延スロット内で例外が発生した
   場合は分岐命令のアドレスからリターンする）。詳細: ADR-005。

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

1. I_STAT & I_MASK が非ゼロなら割り込み発生（Interrupt Controllerの集約ペンディング状態、`PSXInterruptController::GetInterruptPending()`）
2. CPUのStep()毎に、その集約ペンディング状態をCOP0 CAUSE.IP2（bit 10, `docs/cpu/cop0.md` IP参照）へ反映
3. `(CAUSE.IP & SR.IM) != 0 && SR.IEc == 1` の場合、INT例外（Excode 0x00）として処理（SRの3レベルスタックへ退避、EPC/CAUSE.BD設定は他の例外と同じ#141モデルに従う）。分岐の遅延スロット実行中はチェックを行わず、分岐＋遅延スロットのペアが完了してから判定する（Issue #144, ADR-004/ADR-005）。

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
# 例外ハンドラ例（R3000A 3-level stack）
mfc0 k0, C0_SR     # SR退避
sw   k0, saved_sr
ori  k0, k0, 0x3   # KUc=0, IEc=0 (カーネル、割り込み無効)
mtc0 k0, C0_SR     # スタック退避完了
# ... 処理 ...
lw   k0, saved_sr
mtc0 k0, C0_SR     # SR復元
mfc0 k1, C0_EPC    # 復帰先アドレスをEPCから読み出す
jr   k1            # PCへジャンプ
rfe                # 遅延スロットで実行し、3-levelスタックをポップ
```
