# ADR-004: Branch Delay Slot / Load Delay Slot Modeling

- **Status**: Accepted
- **Date**: 2026-08-20
- **Issue**: #18

## Context

PSX R3000A CPUはMIPS I ISAに基づいており、branch delay slotとload delay slotを持つ。これらは将来のRecompiler設計に直接影響する。正しくモデル化しないと、実際のPSXソフトウェアとの互換性が失われる。

## Decision

### Branch Delay Slot

**仕様**:
- 分岐命令（J, JAL, BEQ, BNE, BLEZ, BGTZ, BLTZ, BGEZ, BLTZAL, BGEZAL）の直後の1命令は、分岐の有無に関わらず常に実行される
- JR/JALRの遅延スロットも同様
- 遅延スロット内の命令が分岐命令の場合、挙動はUNPREDICTABLE

**モデル化**:

```text
現在のPC: 分岐命令のアドレス
次のPC: 分岐命令のアドレス + 4（遅延スロットのアドレス）
ターゲットPC: 分岐条件成立時に設定

実行順序:
1. 分岐命令をデコード
2. 分岐条件を評価
3. 遅延スロットの命令を実行
4. 分岐条件が成立していたら、ターゲットPCにジャンプ
5. 成立していなかったら、PC = delay_slot_pc + 4を継続
```

**実装方針**:

```cpp
// 分岐命令の実行時
void execute_branch(uint32_t target, bool condition) {
    // 遅延スロットを実行
    uint32_t delay_slot_pc = current_pc + 4;
    execute_instruction(delay_slot_pc);

    // 分岐条件に基づいてPCを更新
    if (condition) {
        pc = target;
    } else {
        pc = delay_slot_pc + 4;
    }
}
```

### Load Delay Slot

**仕様**:
- ロード命令（LB, LBU, LH, LHU, LW, LWL, LWR）の直後の1命令は、ロード結果を見ない
- ロード結果がまだレジスタに反映されていない状態
- 後のMIPSリビジョンではハードウェアによる解決が追加されたが、R3000A（MIPS I）では存在する

**モデル化**:

```text
現在の状態:
- load_pending: bool
- load_target_reg: int
- load_value: uint32_t

ロード命令実行時:
1. load_pending = true
2. load_target_reg = rt
3. load_value = memory[base + offset]

次の命令実行時:
1. load_pendingな命令がある場合、そのレジスタへの書き込みは保留
2. 連続するLWL/LWRペアはload_pendingな値を読むことができる（特殊回路）
3. それ以外の命令はload_pendingな値を見ない
4. load_pendingをクリア
```

**LWL/LWRの特殊動作**:
- LWL/LWRは **連続するLWL/LWRペア** でのみ直前のロード結果を読むことができる
- LWL→LWRまたはLWR→LWLの順序で同一レジスタを対象とする場合に限る
- これはハードウェアの特殊回路によるもの
- 2番目の命令の後に通常のload delayが残る
- テストではこの挙動を検証する必要がある

### Exception時のBDビット

**仕様**:
- 例外が遅延スロット内で発生した場合、BD(CAUSE bit 31)がセットされる
- EPCは分岐命令のアドレスを指す（遅延スロットのアドレスではなく）
- 例外ハンドラはBDを確認する:
  - BD=0: 通常復帰。RFE + JRでEPC+4にリターン
  - BD=1: 分岐命令と遅延スロットを再実行する必要がある。RFE後、分岐先（taken）またはEPC+8（not-taken）に復帰

## Consequences

- Interpreterでは遅延スロットを明示的にモデル化する必要がある
- Recompilerでは遅延スロットを含めたブロック解析が必要
- デバッガーでは遅延スロットのステップ実行をサポートする必要がある
- テストでは遅延スロットの挙動を網羅的に検証する
