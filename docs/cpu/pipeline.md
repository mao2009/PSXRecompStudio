# R3000A Pipeline and Delay Slots

## Pipeline

PSXのR3000Aは5段パイプラインを使用。

```
IF (Instruction Fetch)
 ↓
ID (Instruction Decode)
 ↓
EX (Execute)
 ↓
MEM (Memory Access)
 ↓
WB (Write Back)
```

## Branch Delay Slot

分岐命令の直後1命令は、分岐の有無に関わらず常に実行される。

### 動作

```
1000: BEQ $1, $2, target
1004: ADD $3, $4, $5    ← Delay Slot (常に実行)
1008: ...               ← Branch not taken場合
100C: target: ...       ← Branch taken場合
```

### 分岐命令一覧

| 命令 | 条件 | Delay Slot |
|------|------|------------|
| J | 無条件 | あり |
| JAL | 無条件 | あり |
| JR | 無条件 | あり |
| JALR | 無条件 | あり |
| BEQ | rs == rt | あり |
| BNE | rs != rt | あり |
| BLEZ | rs <= 0 | あり |
| BGTZ | rs > 0 | あり |
| BLTZ | rs < 0 | あり |
| BGEZ | rs >= 0 | あり |
| BLTZAL | rs < 0, ra=PC+8 | あり |
| BGEZAL | rs >= 0, ra=PC+8 | あり |

### Delay Slot内の分岐

遅延スロット内に分岐命令を配置した場合の挙動は **UNPREDICTABLE**。PSX実機では以下の挙動を示す:

```
BEQ $1, $2, target1
BEQ $3, $4, target2    ← Delay Slot (branch in delay slot)
```

この場合、内側の分岐が先に実行され、その後に外側の分岐が適用される。

### Exception in Delay Slot

遅延スロット内で例外が発生した場合:

1. CAUSE.BD = 1
2. EPC = 分岐命令のアドレス（遅延スロットではなく）
3. 例外ハンドラはBDを確認し、EPC+8にリターンする

## Load Delay Slot

ロード命令の直後1命令は、ロード結果を見ない。

### 動作

```
LW $1, 0($2)      ← Load $1 from memory
ADD $3, $1, $4    ← Delay Slot: $1はまだ古い値
NOP                ← $1に新しい値が反映される
ADD $5, $1, $6    ← $1は新しい値を使用
```

### LWL/LWRの特殊動作

LWL/LWRは **連続するLWL/LWRペア** でのみ直前のロード結果を読むことができる:

```
# 正しいペア（LWR → LWL の順序）
LWR $1, 3($2)     ← Load delay内
LWL $1, 0($2)     ← $1のLWR結果を読むことができる

# 間違い（LWL → LWR はペアとしない）
LWL $1, 0($2)     ← Load delay内
LWR $1, 3($2)     ← $1のLWL結果は読めない
```

連続するペアでない場合、通常のload delay規則が適用される。

### テストでの重要性

PSXのBIOSコードはload delayに依存している。特に:

```asm
lw   $31, 0($sp)    # Load return address
jal  function        # Delay slot: $31はまだ古い値
```

この場合、jalは$31に正しいリターンアドレス（PC+8）を格納する。

## Pipeline Hazards

### Data Hazard

```asm
ADD $1, $2, $3
SUB $4, $1, $5    ← $1の結果がまだ利用可能でない
```

R3000Aはハードウェアによるハザード解決を持たない。アセンブラがNOPを挿入する。

### Control Hazard

分岐命令によるハザード。遅延スロットで解決。

## Cache

### Instruction Cache (4 KB)

- 1 line = 16 bytes
- 物理アドレスタグ
- 80%ヒット率

### Data Cache (1 KB)

- 1 line = 4 bytes (1 word)
- Write-through
- 物理アドレスタグ
- 4-deep write buffer

PSXでは通常、データキャッシュをスクラッチパッドとして使用する。
