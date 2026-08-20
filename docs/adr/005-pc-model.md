# ADR-005: PC Update Model

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

PSX R3000A CPUのPC更新を正しくモデル化しないと、分岐命令、遅延スロット、例外処理で問題が生じる。特にRecompiler設計ではPC管理が重要。

## Decision

### PC状態の定義

```text
PC状態:
- pc: 現在のPC（現在デコード中の命令のアドレス）
- next_pc: 次のPC（通常はpc + 4）
- delay_slot_pc: 遅延スロット内の命令のアドレス（分岐時にのみ使用）
```

### 通常のPC更新

```text
1. 命令をフェッチ: instruction = memory[pc]
2. 命令をデコード・実行
3. PCを更新: pc = next_pc; next_pc = pc + 4
```

### 分岐命令のPC更新

```text
1. 分岐命令をフェッチ・デコード
2. 分岐条件を評価
3. delay_slot_pc = pc + 4
4. 次の命令をフェッチ・実行（遅延スロット）
5. 分岐条件が成立していたら:
   pc = target_address
   next_pc = pc + 4
6. 成立していなかったら:
   pc = delay_slot_pc + 4
   next_pc = pc + 4
```

### ジャンプ命令のPC更新

```text
J/JAL命令:
1. ジャンプ命令をフェッチ・デコード
2. target = ((pc + 4) & 0xF0000000) | (instr_index << 2)
3. 遅延スロットを実行
4. pc = target
5. next_pc = pc + 4
```

### JR/JALR命令のPC更新

```text
JR rs命令:
1. JR命令をフェッチ・デコード
2. target = GPR[rs]
3. 遅延スロットを実行
4. pc = target
5. next_pc = pc + 4
```

### 例外発生時のPC更新

```text
1. 現在のPCをEPCに保存
2. BDフラグを設定（遅延スロット内なら）
3. CAUSEレジスタに例外コードを設定
4. pc = 例外ベクトルアドレス（80000080h）
5. next_pc = pc + 4
```

### 例外ベクトル

```text
BEV=0:
  Reset:         BFC00000h
  UTLB Miss:     80000000h
  COP0 Break:    80000040h
  General:       80000080h

BEV=1:
  Reset:         BFC00000h
  UTLB Miss:     BFC00100h
  COP0 Break:    BFC00140h
  General:       BFC00180h
```

### RFE (Return From Exception)

```text
RFEはSRステータススタックをポップするのみ。PC復元は行わない。

1. SR[3:0] = SR[5:2]  (KUc←KUp, IEc←IEp, KUp←KUo, IEp←IEo)
2. SR[5:4] = 0        (最古のレベルをクリア)
3. ソフトウェアがJRで復帰（通常: JR $ra）
```

BD=1時の例外復帰:
```text
1. EPCは分岐命令のアドレスを指す
2. 例外ハンドラは分岐命令と遅延スロットを再実行する必要がある
3. 分岐先に到達するか、分岐未成立で続行
```

## Consequences

- Interpreterではpc, next_pcの2変数でPCを管理する
- Recompilerではブロック境界のPCを追跡する必要がある
- 例外処理ではBDビットの処理が必須
- テストではPC更新の順序を網羅的に検証する
