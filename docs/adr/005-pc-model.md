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

1. KUc←KUp, IEc←IEp（SR[1:0] ← SR[3:2]）
2. KUp←KUo, IEp←IEo（SR[3:2] ← SR[5:4]）
3. KUo/IEo（SR[5:4]）はRFEでは変更しない（実機は最古レベルを再利用し続ける）
4. ソフトウェアが明示的にPC復帰を行う: MFC0 $k0, EPCでEPCをGPRへ読み出し、
   JR $k0でそのアドレスへジャンプする（例外の入口はJALではないため $ra は
   使えない）。RFEはJRの遅延スロット内で実行するのが定石:
   MFC0 $k0, EPC / JR $k0 / RFE
```

以前の版では手順2を「SR[5:4] = 0（最古のレベルをクリア）」としていたが、これは誤りだった。
実機（psx-spx）およびPR #193（Issue #141）の実装・テストでは、RFEはKUo/IEo
（SR bit 4-5）を変更せずそのまま保持する。実装は
`src/PSXRecomp.Native/src/psx_cpu.cpp` の `PSXCpu::ExecRfe()`、根拠となるテストは
`src/PSXRecomp.Native/tests/test_psx_core.cpp` の `test_rfe_pop`
（`0x3C -> 0x3F` の境界ケース: KUo/IEo=1 がRFE後も1のまま保持されることを確認）を参照。
詳細な3-levelスタックの遷移は `docs/cpu/cop0.md`（SRのビット定義）・
`docs/cpu/exceptions.md`（RFE手順）に準じる。

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
