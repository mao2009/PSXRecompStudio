# ユニット retirement トレース（Issue #774）

**ステータス:** 診断用（opt-in）。既定では無効で、既定の生成ホストは変わりません。

**関連 Issue:** #774

**関連コンポーネント:** `UnitTraceScope` と `UnitTraceRun`（`src/PSXRecomp.Infrastructure/UnitTraceRun.cs`）、`UnitTraceJoiner`（`src/PSXRecomp.Core/Diagnostics/UnitTraceJoiner.cs`）、`RecompilerHostCodeGen`（`src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs`）

[English (Canonical / SSOT)](unit-trace.md)

## 目的

ユニットトレースは、生成ホストの 1 回の実行について、実行した native ユニット、ホストへの transfer、インタプリタが実行した fallback 区間を記録します。親プロセスは両者を結合し、実行順に並んだ guest 命令位置の列にします。この列により、後でインタプリタと比較して最初に相違する区間を探せます。

トレースは計測のための補助であり、実行結果は変えません。

**ローカル限定（レジスタ値）。** `state` の各フィールド（`gpr`、`hi`、`lo`、`sr`、`cause`、`epc`）は CPU のレジスタ値を含みます。PR #839 の決定 A は、レジスタ値をすべての公開出力から外します。このトレースはその意図的な例外で、ローカルの診断用です。ファイルは `PSX_UNIT_TRACE_FILE` で指定したパスに書かれます。そのパスは Git 作業ツリーの外のローカルファイルでなければならず、PR・CI 成果物・公開出力に添付してはなりません。プログラムはパスを検査しません。これは運用者の規則です。

## 有効化

| 設定 | 効果 |
|---|---|
| `PSX_UNIT_TRACE_FILE=<path>` | トレースビルドの生成ホストを選び、native トレースの出力先を指定します。親は同じ場所に fallback トレースを書きます。 |
| `PSX_UNIT_TRACE_MAX_BYTES=<bytes>` | native トレースの上限。既定は 64 MiB。不正な値の場合、その実行ではトレースを無効にし、実行自体は未計測と同じに進みます。 |
| `UnitTraceScope.Begin(path)` | テストで使う、プロセス内の経路。その async コンテキストで開始された実行に対して、環境変数と同じ効果があります。 |

どちらも設定しない場合、生成ソースはトレースなしのビルドとバイト単位で一致し、トレースファイルも fallback ファイルも書かれません。

トレースファイルを作成できなかった場合、その実行は未計測で続きます。親も fallback トレースを書かないため、その実行について完全な結合は得られません。

## 出力ファイル

| ファイル | 書き出し元 | 内容 |
|---|---|---|
| `<path>` | 生成ホスト（子プロセス） | header、unit レコード、handoff マーカー、end 行 |
| `<path>.fallback` | 親プロセス | header、fallback と transfer のレコード、end 行 |

どちらも JSON Lines です。各行は `"v":1` と `"kind"` を持つ 1 つのオブジェクトです。

## 識別子: session と handoff

- **session ID。** 親が実行ごとに 1 つ生成し、`PSX_TRACE_SESSION` で子に渡します。両方の header が保持します。両者の session が異なる場合、結合は拒否されます。
- **handoff ID。** native コードからホストへの transfer は、起きた順に 1 から番号が振られる handoff です。native の handoff マーカーと、親が同じ transfer について書いたレコードは同じ番号を持ちます。結合は番号が連続していること、両者が transfer PC で一致することを確認します。

## レコード schema（version 1）

native ファイル（`<path>`）:

| `kind` | フィールド | 意味 |
|---|---|---|
| `header` | `session`、`cap` | 先頭行。 |
| `unit` | `seq`、`entry`、`next`、`start`、`end`、`retired`、`interrupted`、`exception`、`termination`、`state`（任意） | ディスパッチャの 1 回の通過。`start` と `end` は、その通過の前後の累積 native retirement 数です。`retired` は `end - start` で、完了した命令だけを数えます。`entry` は、その通過の割り込みによる PC 変更の後の PC です。`interrupted` と `exception` は 0 または 1 です。`termination` は `RecompilerIrTerminationReason` の値で、0 が成功です。 `state`（B-6b。トレースビルドのみ、追加的）は unit の終端の CPU 状態で、`pc`（後続）、`gpr`（32 個の値）、`hi`、`lo`、`sr`、`cause`、`epc` を持ちます。 |
| `handoff` | `seq`、`handoff`、`native_total`、`pc` | native コードがホストへ transfer した。`pc` は transfer 時の state の PC です。 |
| `truncated` | `seq`、`reason` | 出力上限に達した。以降のレコードは捨てられます。 |
| `end` | `seq`、`complete`、`reason`（任意） | 最終行。切り詰め後は `complete:false` と `reason:"output_cap"`。 |

fallback ファイル（`<path>.fallback`）:

| `kind` | フィールド | 意味 |
|---|---|---|
| `header` | `session`、`stream`、`cap` | 先頭行。 |
| `fallback` | `seq`、`handoff`、`retired`、`status`、`start_pc`、`end_pc`、`stop`（任意）、`entry`（任意）、`exit`（任意） | インタプリタの 1 区間。`status` は `returned`、`stopped`、`not_run` のいずれか。`end_pc` は区間が CPU を離れた PC です。`stop` は区間が止まった場合の診断名です。`entry` と `exit`（Issue #774、B-6b。トレースビルドのみ、追加的）は、区間の開始時と終了時の CPU 状態で、`pc`、`gpr`（32 個の値）、`hi`、`lo`、`sr`、`cause`、`epc` を持ちます。 |
| `transfer` | `seq`、`handoff`、`retired`、`status`、`start_pc`、`end_pc` | guest 命令を実行せずにホストが処理した transfer。`retired` は常に 0、`status` は `host` または `declined`。 |
| `truncated`、`end` | 上記と同じ | |

1 つのファイル内で、`seq` はレコードごとに 1 ずつ増えます。`seq` の欠落や重複は sequence gap とみなされ、結合を拒否します。

## 結合

`UnitTraceJoiner.Join(nativeLines, fallbackLines)` は次の 3 つのいずれかを返します。

| ステータス | 意味 | 使用してよい範囲 |
|---|---|---|
| `Complete` | 両ファイルが正常終了し、すべての handoff に fallback レコードがある。 | すべての区間。 |
| `Partial` | ここまでの記録は整合しているが、どちらかが途中で切れている、切り詰められている、または終了していない。 | 返された区間のうち `ComparableUpToRetired` までのみ。 |
| `Rejected` | 両者が矛盾している、またはストリームの途中にあるレコードが壊れている。 | なし。`Errors` に理由があります。 |

結合は次の不変条件を確認します。

- session が一致し、`seq` に欠落がない。
- 各 native unit は、直前のレコードが終わった累積数から始まる。
- 何も完了しない unit は、host transfer の通過（後述）か、割り込み境界のどちらかである。
- native の区間は、直前の区間が終わった場所から入る。例外は 2 つあります。割り込みを受けた unit は entry がベクタになるため除外されます。host transfer の通過も除外されます。
- returned の fallback の後、次の native 区間はその区間の `end_pc` から入る。host が処理した transfer の後は、ホストが再開位置を決めるため連続性を確認しない。
- 停止した fallback 区間または native の停止の後には、停止レコード自体を除いて何も続かない。

### 不完全と切り詰め

- **切り詰め。** native ファイルが上限に達した。`truncated` レコードと `complete:false` の `end` により、結合は `Partial` になります。
- **end 行がない。** 実行が正常終了しなかった（例: kill された）。結合は `Partial` になります。途中で切れた最終行は捨て、その前の行は残します。
- **fallback レコードの欠落。** native 側に fallback レコードのない handoff があり、両ファイルが正常終了している場合、結合は `Rejected` です。native 側が途中で切れている場合は `Partial` です。子が親の応答より前に止まった可能性があるためです。

## host transfer の通過を読む

handoff の後、その transfer を処理したディスパッチャの通過は、`retired:0`、`start == end`、`entry` が handoff の PC と等しい `unit` として記録されます。これは guest 命令ではありません。その通過が `next`（artifact が再開した PC）で終わったことだけを示します。結合はこのレコードを handoff の直後にのみ受け入れ、その `next` が返された fallback の `end_pc` と一致することを確認します。

## このトレースで分かること

- 1 回の実行における native 区間、fallback 区間、host transfer の順序。
- 各 native unit が完了した命令数と、各 fallback 区間が retire した命令数。
- 各区間の entry PC と next PC。
- トレースが完全、部分的、または矛盾しているか。

## このトレースで分からないこと

- native の融合 unit 内部の命令位置。1 つの unit は 1 レコードで、その内部の部分完了は retire 数としてのみ報告されます。
- GPU、VRAM、DMA、デバイスの状態。
- native の融合 unit の内部で受けた割り込みは B-6a で再現済み（`InterruptInsideAFusedBlock_...`）。ただし、その他の unit 内部の位置は未検証です。
- プロセスの異常終了。子の kill やタイムアウトは、実際の子を kill せず、トレースを各行で切ることで確認しています。

## 既知の意味の差

以下は B-5 では記録のみで、修正していません。利用側はこれを考慮する必要があります。

1. **処理済み SYSCALL の計上（#790 で修正済み）。** native でも fallback でも、処理済み SYSCALL は 1 回計上され、`pc + 4` から再開します。インタプリタはその命令を fetch し、native と同じく 1 単位のデバイス時間を消費します。#790 以前は fallback が計上していませんでしたが、現在は揃っています。`ServicedSyscallInTheFallback_IsRetiredLikeTheNativeOne` で検証しています。
2. **割り込み後の PC。** fallback 区間内の割り込みでは `end_pc` はベクタで、割り込まれた命令は記録されません。native の unit 内部の割り込み（B-6a で再現）でも `next` はベクタです。一方、native の unit 境界での割り込みでは `next` はプログラム順の PC のままで、transfer レコードがベクタを持ちます。
3. **termination 5。** これらの実行では、fallback が停止を報告したため、`termination` 5（`UnresolvedIndirectFlow`）が fallback の停止と共に現れました。
4. **遅延スロットの SYSCALL。** 両エンジンとも処理せず、実行時の失敗で止まります。分岐は両側で 1 回計上されます。R3000 の再実行モデルでは分岐は再実行されるため、分岐を計上するかは未決の規則です（`DelaySlotSyscall_IsRefusedByBothEngines_AndNeitherCompletes` で固定）。

## 検証

- `UnitTraceJoinerTests`: 不変条件、拒否、部分結合を合成行で検証。
- `UnitTraceEndToEndTests`: 子プロセスとして実行される本番の生成ホストを、handoff 3 回、処理済み SYSCALL、タイマー IRQ を含む合成プログラムで検証。各区間をインタプリタの fetch と比較し、結合列を各行で切って検証。
- `HostCodeGenTests`（`UnitTrace_*`）: トレース有効・無効時の生成ソースの動作、上限、オープン失敗、停止 unit。
