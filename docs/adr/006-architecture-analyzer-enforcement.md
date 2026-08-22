# ADR-006: アーキテクチャ属性と依存方向のビルドエラーによる強制

- **Status**: Accepted
- **Date**: 2026-08-21
- **Issue**: #8

## Context

docs/architecture-matrix.md（SSOT）はレイヤー属性、依存方向、Forbidden APIを文書化しているが、手動レビューのみでは遵守が保証されない。ドキュメントと実コードの乖離は時間とともに必ず発生する。SSOTを機械的に強制する仕組みが必要だった。

## Decision

Roslyn Analyzer（`PSXRecomp.Analyzer`）を導入し、SSOTのルールをコンパイル時にエラーとして強制する。

### 診断ルール

| ID | ルール |
|----|--------|
| `PSXR001` | クラスにアーキテクチャ属性がない |
| `PSXR002` | 1つの型に複数のアーキテクチャ属性が付いている |
| `PSXR003` | 属性のレイヤーと名前空間マッピングが不一致 |
| `PSXR004` | 禁止された依存方向（内側→外側、Production → Test） |
| `PSXR005` | レイヤーごとのForbidden API使用 |
| `PSXR006` | `PSXRecomp.Core` 以外でのP/Invoke（`DllImport` / `LibraryImport`） |

すべて Error 重大度で、CIは違反時に失敗する。

### 属性の配布方式

6つの属性（`[Domain]` `[Application]` `[Infrastructure]` `[Analyzer]` `[Test]` `[Generated]`）は `PSXRecomp.Architecture` 名前空間の internal 属性として、リポジトリ直下の `Directory.Build.props` が配布するリンクされたソースファイル（`<Compile Include="..." Link="..." />`）で各プロジェクトに取り込む。消費プロジェクトは `<CompileArchitectureAttributes>true</CompileArchitectureAttributes>` でオプトインする。新しいアセンブリやプロジェクト参照グラフを作らない。属性は完全修飾名で照合する。

### 適用スコープと除外

- 強制対象は **クラス**（record を含む）。struct / interface / enum / delegate は本イテレーションでは認識のみで必須としない（Issue #8 の文言に基づく）
- partial 型はどれか1部分に属性があれば満たす
- 入れ子クラスは外側の属性付き型のレイヤーを継承する（`ResolveLayer` の ContainingType チェーンと PSXR001 を一致させた）
- `PSXRecomp.Architecture.*` 名前空間は PSXR001 対象外（マーカー名前空間）
- 生成コードはパス規約（`.g.cs` / `.designer.cs` / `obj/` 等）と `IsImplicitlyDeclared` で除外
- 名前空間→レイヤー解決はルート接頭辞一致（`PSXRecomp.Analyzer.Tests` → Analyzer 等）。`PSXRecomp.Infrastructure` → Infrastructure も将来のプロジェクト用に予約済み

### Forbidden API の補足

- Domain は SSOT の行（`Random.Shared`）に加え **`System.Random` 全体**（`new Random()` 含む）を禁止する。行の理由欄（非決定的ランダム性の禁止）を型全体に適用したもの
- Test / Analyzer / Generated レイヤーで正当な用途（テストの一時ファイル等）がある場合は `#pragma warning disable PSXR005` または `.editorconfig` / `NoWarn` で抑制でき、レビューで根拠を示す

### 未強制の依存エッジ

Production → Analyzer / Generated は SSOT が明示していないため本ADRでは強制しない。将来 `PSXRecomp.Generated` プロジェクト定義と合わせて要整理（architecture-matrix.md の Missing Items に記載）。

## Consequences

- SSOT違反はビルドで即座に検出され、レビューに依存しない
- 新しいクラスには必ずレイヤー属性が必要になる（移行済み: 既存12クラスは全て注釈済み）
- アナライザー自体もソリューションに含まれ、自己適用の対象となる
- ルール変更時は architecture-matrix.md（SSOT）→ アナライザー実装 → テスト の順で同期する
