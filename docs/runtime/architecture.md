# PS1 Hardware Runtime Architecture

Issue #44: R3000A CPU以外のPS1ハードウェアをRecompiled Codeから利用するためのRuntime/Hardware Abstraction Architecture

## 概要

PSXRecompStudioのRuntimeは、Recompiled CodeがPS1ハードウェアにアクセスするためのAbstraction Layerを提供する。ゲーム固有コードにハードウェア実装を直接埋め込まず、Domainインターフェースを通してアクセスする。

### 前提

- Issue #39 (R3000A CPU Domain) が完了済み
- `R3000aDecoder` / `R3000aInstruction` 等のCPU Domain Modelが存在
- `PSXRecomp.Core` (Domain層) と `PSXRecomp.Native` (Infrastructure層) のC ABI境界が確立

## アーキテクチャ方針

```text
Recompiled Code (生成されたC#コード)
    ↓ Domain インターフェース呼び出し
PSXRecomp.Core (Domain層: Hardware インターフェース定義)
    ↓ P/Invoke / C ABI
PSXRecomp.Native (Infrastructure層: 実際のハードウェア実装)
```

### 境界原則

1. **Recompiled Code は IHardwareComponent インターフェースのみを参照**
2. **Domain層は Pure である** (File I/O, Console, DateTime.Now, Environment 不可)
3. **Infrastructure層が実際の状態変更・I/Oを担当**
4. **C ABI境界で不透明ポインタ (IntPtr) を使用**

## Hardware Component Model

全PS1ハードウェアコンポーネントは `IHardwareComponent` を実装する。

```csharp
[Domain]
public interface IHardwareComponent
{
    string Name { get; }
    void Reset();
    uint Read32(uint offset);
    void Write32(uint offset, uint value);
    ushort Read16(uint offset);
    void Write16(uint offset, ushort value);
    byte Read8(uint offset);
    void Write8(uint offset, byte value);
}
```

### コンポーネント一覧

| コンポーネント | インターフェース | アドレス範囲 | 割り込み |
|---------------|----------------|-------------|---------|
| RAM | IMemoryBus (直接) | 0x00000000-0x007FFFFF (2MB, 8MBミラー) | なし |
| Scratchpad | IMemoryBus | 0x1F800000-0x1F8003FF (1KB) | なし |
| BIOS | IBios | 0x1FC00000-0x1FC7FFFF (512KB) | なし |
| Interrupt Controller | IInterruptController | 0x1F801070-0x1F801074 | 中枢 |
| DMA Controller | IDmaController | 0x1F801080-0x1F8010FF | IRQ3 |
| Timer 0-2 | ITimer | 0x1F801100-0x1F801128 | IRQ4-6 |
| Controller/MemCard | (将来追加) | 0x1F801040-0x1F80105E | IRQ7 |
| CD-ROM | ICdRom | 0x1F801800-0x1F801803 | IRQ2 |
| GPU | IGpu | 0x1F801810-0x1F801814 | IRQ0 (VBlank), IRQ1 (GPU cmd) |
| MDEC | IMdec | 0x1F801820-0x1F801824 | なし |
| SPU | ISpu | 0x1F801C00-0x1F801DFF | IRQ9 |
| GTE | IGte (COP2) | Coprocessor | なし |
| Cache Control | IMemoryBus | 0xFFFE0130 | なし |

## Memory / Bus Model

`IMemoryBus` が物理アドレスから適切なコンポーネントにルーティングする。

```text
Physical Address
    ↓ Address Decode
┌─────────────────────────────────────────────────────┐
│ 0x00000000-0x007FFFFF: RAM (2MB, 8MBミラー)       │
│ 0x1F000000-0x1F07FFFF: Expansion Region 1          │
│ 0x1F800000-0x1F8003FF: Scratchpad (1KB Fast RAM)  │
│ 0x1F801000-0x1F801FFF: I/O Ports                   │
│   0x1F801070-0x1F801074: Interrupt                 │
│   0x1F801080-0x1F8010FF: DMA                      │
│   0x1F801100-0x1F801128: Timers                   │
│   0x1F801800-0x1F801803: CD-ROM                   │
│   0x1F801810-0x1F801814: GPU                      │
│   0x1F801820-0x1F801824: MDEC                     │
│   0x1F801C00-0x1F801DFF: SPU                      │
│ 0x1FC00000-0x1FC7FFFF: BIOS ROM (512KB)            │
│ 0xFFFE0130: Cache Control Register                  │
└─────────────────────────────────────────────────────┘
```

### アクセスルール

- **未マッピングアドレス**: 読み: 0, 書き: 無視 (open bus)
- **RAM 16-bit/8-bit アクセス**: バイト単位/ハーフワード単位で直接アクセス可能
- **HW Register 16-bit/8-bit アクセス**: 32-bit レジスタへの部分書き込み
- **BIOS**: 書き込み不可 (ROM)
- **GetRamPointer()**: Recompiled Code の高速メモリアクセス用

## MMIO Model

各ハードウェアコンポーネントは固有のレジスタオフセット空間を持つ。

1. `IMemoryBus.Read32(address)` が物理アドレスを受け取る
2. `Ps1MemoryMap` の静的判定でコンポーネントを特定
3. 対応する `IHardwareComponent.Read32(offset)` を呼び出す
4. offset = `address - component_base_address`

## DMA Model

7チャンネルのDMA転送を管理する。

| チャンネル | 用途 | 方向 | 同期モード |
|-----------|------|------|-----------|
| 0: MDECin | MDEC入力 | FromRam (RAM→MDEC) | Slice (CHCR sync=1) |
| 1: MDECout | MDEC出力 | ToRam (MDEC→RAM) | Slice (CHCR sync=1) |
| 2: GPU | 描画 | 双方向 | Burst/Slice/LinkedList |
| 3: CD-ROM | セクタ読み取り | FromRam (CD→RAM) | Burst (CHCR sync=0) |
| 4: SPU | 音声データ | 双方向 | Slice (CHCR sync=1) |
| 5: PIO | 拡張ポート | 双方向 | Burst (CHCR sync=0) |
| 6: OTC | リバースクリア | ToRam (OTC→RAM) | Burst (CHCR sync=0) |

- DMA完了時に IInterruptController.Raise(IRQ3) を呼ぶ
- OTC は連結リストのリバースクリア専用 (GPU OT用)
- チャンネル 2 (GPU) は linked list モードをサポート

## GTE (Geometry Transformation Engine) Model

COP2コプロセッサとして実装される。

- **データレジスタ (32個)**: ベクトル (V0-V2), 中間値 (IR0-3), 画面座標 (SXY0-2), Z値 (SZ0-3), MAC累算器 (MAC0-3), 色 (RGBC, RGB0-2)
- **コントロールレジスタ (32個)**: 回転行列, ライトベクトル/カラー, プロジェクション平面距離, クリッピング値
- **コマンド**: COP2 指令で発行 (sf=shift fraction, lm=saturate)
- **主なコマンド群**: RTPS, NCLIP, AVSZ3, AVSZ4, SQR, NCCT, NCS, NCT, NCDS, NCDT, DPCL, DPCT, DPCS, DCT, INTPL, MVMVA, DCPL, DPCS, GPF, GPL, NCCT

## GPU Model

GP0/GP1 の2つのレジスタで制御する。

- **GP0 (0x1F801810)**: 描画コマンド, VRAM転送, 表示領域設定
- **GP1 (0x1F801814)**: ディスプレイ制御, リセット, DMA方向設定
- **GPUREAD (0x1F801810)**: GP0/GP1 の結果読み取り
- **GPUSTAT (0x1F801814)**: GPUステータスレジスタ (読み取り専用)
- **VBlank**: 垂直帰線時に IRQ0 を発火
- **GPU IRQ1**: GP0(1Fh) コマンドで要求, GP1(02h) で Acknowledge

## SPU Model

24ボイスの音声合成エンジン。

- **レジスタ空間**: 0x1F801C00-0x1F801DFF
- **ボイス**: ADPCMデコード, ADSRエンベロープ, ピッチ制御
- **メインボリューム/リバーブ**: ステレオ出力制御
- **CDオーディオ入力**: CD-ROMから直接音声データを受信
- **IRQ9**: サウンドバッファがIRQアドレスをクロスした時に発火

## CD-ROM Model

CD-ROMコントローラーを制御する。

- **レジスタ**: 0x1F801800-0x1F801803 (インデックス0-3)
- **コマンド**: セクタ読み取り, シーク, パケット読み取り, CDオーディオ
- **IRQ2**: コマンド完了, データレディ, エラー時に発火
- **モード**: Normal/Double speed, DMA/PIO

## BIOS Model

512KBのROMBIOS。

- **アドレス**: 0x1FC00000-0x1FC7FFFF
- **システムコール**: GPU, SPU, CD-ROM, メモリカード, コントローラI/O
- **オーバーレイ**: メモリ上的に関数を配置
- **イベントハンドリング**: タイマー, DMA, 割り込みのコールバック

## MDEC (Motion Decoder)

JPEGデコードとモーションビデオ復号。

- **レジスタ**: 0x1F801820-0x1F801824
- **DMA**: MDECin (ch0), MDECout (ch1)
- **状態**: Busy, FIFOワード数

## Interrupt Model

中央割り込みコントローラー。

```text
IRQ0: VBlank      (GPU vertical blank)
IRQ1: GPU command  (GPU command completion, GP1(02h) acknowledges)
IRQ2: CD-ROM       (CD-ROM)
IRQ3: DMA          (DMA Controller)
IRQ4: Timer 0      (Timer)
IRQ5: Timer 1      (Timer)
IRQ6: Timer 2      (Timer)
IRQ7: Controller   (Controller/Memory Card byte received)
IRQ8: SIO          (Serial Interface)
IRQ9: SPU          (Sound Processing)
IRQ10: PIO         (Expansion / Controller lightpen)
```

- **I_STAT (0x1F801070)**: 割り込みステータス (write-0-to-clear)
- **I_MASK (0x1F801074)**: 割り込みマスク
- **判定**: `(I_STAT & I_MASK) != 0` → 割り込み発生
- **Edge-triggered**: 各ビットは割り込みソースが false→true に変化した時にセットされる
- **IRQ Acknowledge**: I_STAT に 0 を書くことで該当ビットをクリア

## Timer Model

3つのハードウェアタイマー。

| Timer | ベース | クロック源 | 主な用途 |
|-------|-------|-----------|---------|
| Timer 0 | 0x1F801100 | ドットクロック/スキャンライン | GPU VBlank検出 |
| Timer 1 | 0x1F801110 | 水平リトレース | ディスプレイ同期 |
| Timer 2 | 0x1F801120 | システムクロック/8 | 一般的なタイマー |

- **MODE レジスタ**: 
  - Bit 0: 同期有効 (0=Free Run, 1=Synchronize)
  - Bit 1-2: 同期モード (カウンタリセット/一時停止条件)
  - Bit 3: リセット条件 (0=FFFFh到達時, 1=Target到達時)
  - Bit 4: Target到達時IRQ (0=無効, 1=有効)
  - Bit 5: FFFFh到達時IRQ (0=無効, 1=有効)
  - Bit 6: One-shot/Repeat (0=One-shot, 1=Repeat)
  - Bit 7: Pulse/Toggle (0=Pulse, 1=Toggle)
  - Bit 8-9: クロック源
- **COUNT レジスタ**: 16ビットカウンタ値 (0-FFFFh)
- **TARGET レジスタ**: 16ビットターゲット値

## Timing Model

- **CPUクロック**: 33.8688 MHz
- **1 CPU cycle**: 1 クロック (約29.5ns)
- **HBlank**: 15.734 kHz (63.5μs per scanline)
- **VBlank**: 59.94 Hz (16.68ms per frame)
- **DMA転送**: 1ワードあたり1-2サイクル消費
- **GTEコマンド**: 概ね1イテレーションで数クロック
- **SIO (Controller/Memory Card)**: ボーレート依存

### 同期方針

- CPU実行はサイクルカウントで同期
- ハードウェアイベントはサイクル境界で処理
- DMAはCPU実行と並列だが、バス競合時にストール
- タイマーはCPUサイクルに同期してカウント

## Recompiled Code ↔ Runtime ABI

```text
Recompiled Code
    │
    ├── Load/Store → IMemoryBus.Read32/Write32
    ├── COP2 (GTE) → IGte.ExecuteCommand
    ├── System Call → IBios (BIOS経由)
    └── I/O Check → メモリアクセス時にアドレス判定
                      │
                      ├── RAM → ポインタ直接アクセス
                      └── MMIO → IHardwareComponent.Read/Write
```

### パフォーマンス最適化

- **RAM直接アクセス**: `GetRamPointer()` でポインタを取得し、Unsafeコードで直接アクセス
- **MMIO遅延判定**: RAMアドレス範囲チェックを最短パスで実行
- **Hardware Inlining**: 頻繁にアクセスするレジスタはインライン化

## 複数 Platform Runtime 拡張方針

- `IHardwareComponent` / `IMemoryBus` 等のインターフェースはプラットフォーム非依存
- Infrastructure層 (`PSXRecomp.Native`) がプラットフォーム固有実装を提供
- C# 側は Domain インターフェースのみに依存
- 将来の拡張:
  - GPU バックエンド (Vulkan, OpenGL, DirectX)
  - オーディオバックエンド (SDL2, CoreAudio)
  - タイミングバックエンド (高精度タイマー, フレーム pacing)
  - ネットワーク (マルチプレイ)

## Acceptance Criteria 達成状況

| Criteria | Status |
|----------|--------|
| Hardware component model を定義 | ✅ IHardwareComponent + 全インターフェース |
| Recompiled Code と Runtime の境界を定義 | ✅ Section: Recompiled Code ↔ Runtime ABI |
| MMIO / memory access 方針を定義 | ✅ Section: MMIO Model, Memory / Bus Model |
| Timing / synchronization 方針を定義 | ✅ Section: Timing Model |
| BIOS interaction 方針を定義 | ✅ Section: BIOS Model |
| GTE/GPU/SPU/CD/DMA等の責務を整理 | ✅ 各Sectionで定義 |
| 将来の複数 platform Runtime を考慮 | ✅ Section: 複数 Platform Runtime 拡張方針 |
