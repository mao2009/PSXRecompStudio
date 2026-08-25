# ADR-007: Repository Artifact Policy and CI Contamination Gate

- **Status**: Accepted
- **Date**: 2026-08-24
- **Issue**: #91

## Context

PSXRecompStudio は将来的にユーザー提供の ROM / ISO / BIOS を取り扱う。これらは著作物であり、リポジトリへの混入が絶対に許されない。`.gitignore` による除外だけでは、一度 stage / commit されたファイルや、拡張子を改名したバイナリを防止できない。

また、ビルド成果物 (`bin/`, `obj/`, `build/` 等) や巨大ファイルの誤コミットも、現状ではレビューの目視に依存している。Golden Tests (Issue #39) 以降、テストデータが増えるにつれ、正当な fixture と実機由来データの境界を機械的に保証する仕組みが必要になる。

## Decision

1. **ポリシー SSOT**: 禁止拡張子・禁止パスセグメント・サイズ上限・コンテンツ署名・allowlist を `config/artifact-policy.json` に一元定義する。閾値やリストをスクリプト側に重複させない。
2. **CI 品質ゲート**: `scripts/ci/check-artifact-policy.ps1` (pwsh, CI/local 共通) を GitHub Actions の `Artifact Contamination Gate` ジョブとして実行し、集約 `ci` ジョブの必須条件に含める。
3. **全体走査**: PR 差分ではなく追跡対象ツリー全体を毎回走査する。現規模ではコストが無視でき、diff 走査の上限集合となる。
4. **改名バイナリ対策**: 拡張子に依存せず、PS-X EXE / ISO 9660 / CHD / CSO / PBP / MDS のオフセット固定シグネチャで内容を検査する。
5. **allowlist**: 正当な例外は `allowedPaths` の明示的な exact-path 登録のみとし、実機由来データの allowlist は禁止する。

## Alternatives Considered

- **`.gitignore` 強化のみ**: stage 後の検出不可のため不十分。補完としては維持する。
- **PR diff のみ走査**: 実装は軽いが、既存混入やマージ後の変化を見逃す。全体走査に対して優位性がない。
- **bash + jq 実装**: runner 依存を増やすより、runner 標準の pwsh + ConvertFrom-Json が Windows ローカル検証とも一致する。
- **専用 Roslyn Analyzer への即時昇格**: Analyzer はコンパイル単位の強制であり、バイナリ走査とは対象が異なる。将来の昇格候補としつつ、まずは CI スクリプトで足場を固める。

## Consequences

- ROM/BIOS/生成物/巨大ファイル/改名バイナリの混入が merge 前に機械的に阻止される。
- ポリシー変更は JSON 1 ファイルの diff として review 可能であり、判断履歴が残る。
- 閾値超過の正当アセットは allowlist 登録の手続きコストを生む (意図された摩擦)。
- 履歴監査・hash denylist 等は本 ADR の範囲外 (#91 の将来拡張)。
- 詳細な運用規約は [docs/development/artifact-policy.md](../development/artifact-policy.md) を SSOT とする。
