# 對話窗 UI 設計規範

- 文件版本：1.3
- 文件狀態：已實作
- 現行系統版本：0.9.28
- 首次實作版本：0.9.25
- 最後核對日期：2026/09/18

## 目的

本文件是後台所有浮層的固定設計模式：大量資料輸入對話窗（記錄的新增與修改窗）、
小型確認窗，以及通知與消息條。日後有同類需求一律照本文辦理，
不要各自發明版型、確認行為或視覺。

速查表 [§6.3](開發慣例與限制速查.md)（鍵盤事件）與 §6.4（尺寸來源）仍是硬性紅線，
本文件是它們的展開版。

## 1. 三種對話窗，只有一種適用本規範

**所有**對話窗共用同一層果凍基底（`.ant-modal .ant-modal-content`），
差異只在尺寸與版型 —— 見第 2.1 節。下表是各類型的差異：

| 類型 | 判準 | 版型 |
|------|------|------|
| **表單對話窗** | 內含 `<EditForm>`，使用者要輸入或修改記錄 | `Class="form-modal"`，本文件全部適用 |
| 唯讀明細窗 | 只是把一筆記錄攤開來看（例外紀錄、Token 用量、AI 分析） | 各自的 `*-modal` class，不適用本文件 |
| **小型確認窗** | `ModalService.ConfirmAsync`，只有一句話與兩個按鈕 | 全域 `.ant-modal-confirm`，**不滿版**，見第 9 節 |
| 通知／消息條 | `NotificationService`（右下角）、`MessageService`（頂部） | 全域選擇器，見第 10 節 |
| 其餘對話窗 | 關於、變更密碼、唯讀明細窗、AI 分析窗 | 只寫尺寸，果凍由基底提供 |

## 2.1 果凍是共用基底，不是逐窗複製 ⚠️

`OverlayStyles.razor` 的分層：

```
.ant-modal                      CSS 變數 --ov-*（色票、圓角、模糊度）
.ant-modal .ant-modal-content   果凍基底 —— 邊框、圓角、毛玻璃、內外亮邊、ov-arrive 380ms
.ant-modal .ant-modal-header / -footer / -title / -close
.ant-modal-footer .ant-btn      膠囊圓角 ＋ Q 彈回饋
.ant-modal .ant-input …         圓角 ＋ 聚焦柔光

  ├─ .form-modal         --ov-radius: 34px、--ov-blur: 26px、fm-arrive 620ms、95vh flex 版型
  ├─ .ant-modal-confirm  440px、:has(.ant-btn-dangerous) 轉紅調
  └─ 其餘                只寫尺寸
```

⚠️ **新增對話窗時只寫尺寸，不要再複製一份果凍。**
0.9.25／0.9.26 是用「逐一為 `.form-modal` 與 `.ant-modal-confirm` 各寫一份」的加法做法，
結果關於、變更密碼與三個唯讀明細窗整組被漏掉 —— 沒被點名的窗自動落空。
由 `FormModalConventionTests.JellyStyles_ShouldLiveOnTheSharedModalBase` 守門。

要調整單一窗的果凍強度，覆寫 `--ov-radius` / `--ov-blur` / `--ov-tint-glass` 等變數即可，
不要整段重寫 `.ant-modal-content`。

## 2. 版型

| 項目 | 規格 |
|------|------|
| 寬高 | `95vw` × `95vh`，`max-width: 1400px`，`top: 2.5vh` |
| 為什麼不是 100% | 上下留 2.5vh 讓遮罩透出來，果凍與毛玻璃才看得出來；`max-width` 避免超寬螢幕把欄位拉成難讀的長條 |
| 結構 | 標題列固定、**中間內容區是唯一會捲動的地方**、按鈕列固定 |
| 欄位排列 | 2 欄 CSS Grid（`repeat(2, minmax(0, 1fr))`），由左而右 |
| 分組 | `<FormSection Title="...">`，每組一個小標題 |
| 窄螢幕 | ≤ 767.98px 塌成單欄，窗擴成 `100vw` × `100svh`、無圓角無邊框 |

尺寸與版型**只寫在** `src/MyProject/MyProject.Web/Components/Commons/OverlayStyles.razor` 的
`.form-modal` 區塊。⚠️ 不要改用 Modal 的 `Width` 參數（速查表 §6.4）。

## 3. 欄位排版規則

預設兩欄。下列欄位加 `Class="form-field-full"` **獨占整行**：

| 欄位型態 | 為什麼 |
|----------|--------|
| 多行文字（`TextArea`） | 擠在半欄裡會逼使用者在窄框裡讀長句 |
| 多選（`Select Mode="SelectMode.Multiple"`） | 選項一多就換行，半欄寬度會讓它一直長高、把右邊欄位擠歪 |
| 檔案上傳與待上傳清單 | 清單是縱向的，橫向切一半只會兩邊都不好讀 |
| 權限矩陣、巢狀勾選群 | 本身就是二維排列，再塞進半欄會變成三層巢狀 |
| 帶有長說明文字的欄位 | 說明會把那一格撐高，兩欄高度參差 |

其餘短欄位（單行文字、下拉、日期、數字、勾選、開關）一律留在 2 欄裡。

欄位多到一個畫面看不完時，**分組**而不是縮小字級：每個 `<FormSection>` 一個語意群
（例如「帳號與登入」「基本資料」「角色與團隊」「狀態」）。

> ⚠️ **哪些樣式不能寫在 `XxxView.razor.css`：**
> AntDesign 自己渲染的元素（`.ant-modal-content`、`.ant-form-item`、`.ant-btn`…）
> **不會**帶上檢視的 scope 屬性，因此檢視的 `.razor.css` 永遠打不到它們；`::deep` 也無效
> （它編成 `[b-hash] 後代`，而 modal 的外框是 AntDesign 渲染的，沒有那個屬性）。
> 這類樣式一律寫在 `OverlayStyles.razor` 的全域 `<style>`。
>
> 反過來說，**檢視自己在 `.razor` 裡寫的元素仍然會拿到 scope 屬性**（即使位於 `<Modal>` 內容中），
> 所以像角色權限矩陣那種純自有 markup 的樣式，留在該檢視的 `.razor.css` 是正確的。
> `FormSection.razor` 刻意不附 `.razor.css`，是因為它產生的 class 要被上面那份全域樣式命中。

## 4. 未儲存保護

| 入口 | 行為 |
|------|------|
| 遮罩點擊 | **完全不關窗**（`MaskClosable="false"`）。大量輸入的窗，點遮罩從來不是「我要關窗」的刻意手勢 |
| ✕ / 取消鈕 / ESC | 有變更 → 問「尚有未儲存的變更」［繼續編輯／放棄變更］；無變更 → 直接關閉，不打擾 |
| 儲存鈕 | 有變更 → 問「確認儲存」［再檢查／儲存］；一個字都沒改 → 提示「沒有任何變更，未進行儲存」後直接關窗，不寫入資料庫 |

三個關窗入口（✕、取消鈕、ESC）在 AntDesign 內部都收斂到 `Dialog.CloseAsync()` → `Modal.OnCancel`，
所以**只掛 `OnCancel` 一處就全涵蓋**。不要自己攔 `@onkeydown`（速查表 §6.3）。

### 4.1 變更偵測：與原始快照做值比對 ⚠️

用 `Components/Commons/FormDirtyTracker.cs`：開窗前 `Capture(CurrentRecord)`，
按鈕觸發時 `IsDirty(CurrentRecord)`。改了又改回原值視為無變更。

⚠️ **不要改用 `EditContext.IsModified()`。** 只有繼承 `InputBase<T>` 的 Blazor 內建元件才會呼叫
`EditContext.NotifyFieldChanged`；本專案的欄位全是 AntDesign 元件直接綁模型屬性，
外層 `EditContext` 一輩子收不到欄位變更通知 —— `IsModified()` 會**恆為 false**，
未儲存提示永遠不跳，而且不會有任何錯誤或徵兆。
（`Validate()` 之所以能用，是因為 `DataAnnotationsValidator` 在當下整包重驗，與欄位通知無關。）

`Capture` 必須是**開窗前的最後一步**：預設角色、關聯資料都要先塞完再拍快照，
否則那些值會被當成「使用者的變更」。

不在模型裡的暫存狀態（待上傳檔案、待刪除檔案 ID）要傳指紋給第二個參數：

```csharp
dirtyTracker.Capture(CurrentRecord, UploadStateFingerprint);

private string UploadStateFingerprint()
    => string.Join('|', pendingUploadFiles.Select(x => $"{x.Name}:{x.Size}"))
       + "#" + string.Join(',', removedFileIds.OrderBy(x => x));
```

⚠️ 快照字串含 `Password` 等敏感欄位，**絕不可寫進 log**。

實例見 `ProjectViewView.UploadStateFingerprint()`：待上傳與待刪除的檔案不在
`ProjectAdapterModel` 裡，不納入比對的話，「只加了檔案、沒動欄位」會被判定為無變更而直接關窗。
同一個檢視的取消流程也要注意 —— 檔案清單只能在**確定要關窗之後**才清掉，
否則使用者選「繼續編輯」會發現選好的檔案不見了。

### 4.2 確認窗的文案由樣板決定

`Components/Commons/FormEditConfirm.cs` 的 `AskDiscardChangesAsync` / `AskSaveAsync`，
以及既有的 `TeamBindingConfirm.AskAsync`。**不要各 View 自己寫 `ConfirmAsync`** ——
文案必定漂移成「這個窗問得很兇、那個窗問得很客氣」。

三者都設 `ZIndex = 1100`：表單窗在等待確認期間會維持開啟（見 4.3），
確認窗若沿用預設的 1000 就只剩 DOM 先後可以決定疊放順序。

### 4.3 OnOk／OnCancel 的第一行 ⚠️

AntDesign 在呼叫 `OnOk` / `OnCancel` **之前**就已送出 `VisibleChanged(false)`，
而且宣告式 `<Modal>` 無法否決關窗（`ModalClosingEventArgs.Cancel` 只有 `ModalService` 建的窗才管用）。
因此 handler 的**第一行**必須把 Visible 搶回來：

```csharp
private async Task OnModalCancelHandleAsync(MouseEventArgs args)
{
    modalVisible = true;
    modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, dirtyTracker.IsDirty(CurrentRecord));
    // ...
}
```

這樣不會閃爍：`VisibleChanged(false)` 與 handler 之間沒有任何 await 讓步，
整段流程都在同一個 render batch 內，那個 `false` 從來不會被畫出來。

⚠️ `args` 可能是 `null`（ESC／✕ 走 `Config.OnCancel.Invoke(null)`），不要解參考它。

⚠️ **不要把 `modalVisible = true;` 補在每條失敗路徑。** 舊寫法一個 View 重複四次，
只要新增一條早退路徑而忘了補，症狀就是「按儲存 → 驗證失敗 → 窗關了 → 輸入全丟」。
改用 `Components/Commons/FormModalFlow.cs`：失敗路徑只要 `return false`。

## 5. 果凍視覺規格

色票沿用登入頁（`Components/Auths/Login.razor.css`）的粉梅暖雪：

| 用途 | 值 |
|------|-----|
| 文字 | `#51132f` |
| 次要文字 | `#704054` |
| 重點色 | `#a52b59`（聚焦邊框 `#d37598`） |
| 面板底 | `rgba(255, 234, 243, 0.62)` + `backdrop-filter: blur(26px)` |
| 主要按鈕 | `linear-gradient(165deg, #ea88ad 0%, #c43970 58%, #b52a60 100%)` |

動態：

| 元素 | 效果 |
|------|------|
| 開窗 | `fm-arrive` 620ms，`scale(0.72, 0.86)` → 過衝 `scale(1.035, 0.965)` → 收斂到 1（squash & stretch） |
| 按鈕 | hover 上移 2px、按下 `scale(0.97)`，`cubic-bezier(0.2, 0.8, 0.3, 1.3)` 220ms |
| 勾選 | `fm-jelly-check` 220ms 小幅回彈 |
| 輸入框聚焦 | 柔光增亮 + 邊框轉重點色，200ms |

⚠️ 毛玻璃寫在 `@supports` 裡：不支援 `backdrop-filter` 的瀏覽器維持可讀的實底，不要變成半透明糊成一片。

⚠️ 必須帶 `@media (prefers-reduced-motion: reduce)` 降級，做法比照 `Login.razor.css` 的同名區段。

## 6. 陷阱

| 陷阱 | 症狀 | 正解 |
|------|------|------|
| 在 `XxxView.razor.css` 裡寫 `.ant-*` 或用 `::deep` | 規則靜默失效 | AntDesign 渲染的元素不帶檢視的 scope 屬性，這類樣式寫在 `OverlayStyles.razor` 的全域 style |
| `OverlayStyles.razor` 裡寫 `@media` | 建置錯誤 RZ1003 | 該檔是 `.razor`，要寫 `@@media` / `@@supports` / `@@keyframes` |
| CSS 註解裡寫 `<Modal Width>` | 建置錯誤 RZ1034（Razor 把它當標籤） | 註解裡不要放角括號標籤 |
| `Class` 掛了卻沒補樣式規則 | 窗退回 AntDesign 預設 520px，只有打開那頁才看得出來 | `FormModalConventionTests` 守門 |
| 用「逐窗加法」寫果凍 | 沒被點名的窗自動落空，下一個新增的窗也會再落空一次 | 果凍寫在 `.ant-modal .ant-modal-content` 共用基底，各 class 只寫尺寸（§2.1）|
| 沒有按鈕列的窗（`Footer="null"`）下緣被切平 | 圓角落在 footer 上，而那個窗沒有 footer | 基底已用 `.ant-modal-body:last-child` 接住 |
| `Clone()` 是淺複製 | 改多選欄位會連帶改到表格那一列 | List 欄位一律「指派新清單」，多選用 `Values` + `OnSelectedItemsChanged` |
| scoped CSS 裡用 `:root` | 變數完全不生效 | 掛在最外層容器 class 上 |
| 把 `OverlayStyles` 掛在 layout 或檢視裡 | 用別的 layout 的頁面整組吃不到樣式，只有打開那一頁才看得出來 | 只在 `Routes.razor` 的 `AntContainer` 旁渲染一次，守門測試會擋 |
| 想給個別確認窗掛 class | `ConfirmOptions.ClassName` 被 AntDesign 內部**無條件覆寫**，完全無效 | 只能吃全域 `.ant-modal-confirm`；要分辨輕重用 `OkButtonProps.Danger` 當 CSS hook |
| 確認窗改寬度沒加 `!important` | 無效 —— 416px 是 AntDesign 寫在 `.ant-modal` 上的 inline style | `width: ... !important` |
| 忘了中和 AntDesign 的 `antZoomIn` | 果凍動畫與它疊加，彈跳幅度比設計時選定的更誇張 | `.ant-modal.ant-zoom-enter { animation: none !important; transform: none !important; }` |
| 各檢視自己寫 `ConfirmAsync` | 必定漏掉 `Danger`／`MaskClosable`，破壞性動作會長得像提醒、誤點遮罩就執行 | 一律走 `ConfirmDialog` 樣板，守門測試會擋 |

## 7. 守門測試

`src/MyProject/MyProject.Tests/FormModalConventionTests.cs`：

1. 含 `<EditForm>` 的 Modal 必須有 `form-modal`、`MaskClosable="false"`、`Keyboard="true"`、`OnCancel`，且不得用 `Width`。
2. 每個用到的 `*-modal` class，`OverlayStyles.razor` 裡都要有對應規則。
3. code-behind 不得出現「失敗路徑各補一次 `modalVisible = true;`」的舊寫法。
4. 待遷移清單（`PendingMigrationViews`）不得放著爛掉：名單上的檢視一旦遷移完成就要移除。
5. `Components/Commons/` 之外不得出現 `ConfirmAsync(` —— 確認窗一律走 `ConfirmDialog` 樣板。
6. `<OverlayStyles />` 只能在 `Routes.razor` 出現，且恰好一次。
7. 果凍必須寫在 `.ant-modal .ant-modal-content` 共用基底上（§2.1）。

搭配既有的 `ModalKeyboardConventionTests.cs`（表單層不得攔截鍵盤事件）。

## 8. 遷移現況

| 檢視 | 狀態 |
|------|------|
| `MyUserView`（使用者管理） | ✅ 0.9.25（示範頁） |
| `CategoryViewView`（分類清單） | ✅ 0.9.25 |
| `TeamViewView`（團隊清單） | ✅ 0.9.25 |
| `RoleViewView`（角色管理） | ✅ 0.9.27，權限矩陣走 `SingleColumn` 區塊 |
| `ProjectViewView`（專案項目） | ✅ 0.9.27，含 `UploadStateFingerprint` |

**五個 CRUD 檢視全部遷移完成**，`FormModalConventionTests.PendingMigrationViews` 已清空，
守門規則現在涵蓋所有表單對話窗。

小型浮層（確認窗、通知、消息條）是全域選擇器，**0.9.26 起全部一次到位**，沒有待遷移項目。

## 9. 小型確認窗

`ModalService.ConfirmAsync` 產生的窗。**不滿版**：它只有一句話與兩個按鈕，撐大只會讓游標跑更遠。

| 項目 | 規格 |
|------|------|
| 寬度 | `440px`，`max-width: 90vw` |
| 圓角／邊框 | 24px／2px |
| 毛玻璃 | `blur(20px)`，包在 `@supports` 裡 |
| 進場 | `cfm-arrive` 380ms 短版擠壓回彈（出現頻率高，動作太大或太久都是干擾） |
| 一般提醒 | 粉梅調 |
| 破壞性動作 | 紅調（邊框、光暈、圖示） |

### 9.1 一律走 `ConfirmDialog` 樣板 ⚠️

`Components/Commons/ConfirmDialog.cs`：

| 方法 | 用途 | 自動帶入 |
|------|------|----------|
| `AskDestructiveAsync` | 刪除、清空、放棄變更 | `Danger`、`MaskClosable = false`、`ZIndex = 1100` |
| `AskAsync` | 一般確認與提醒 | `MaskClosable = false`、`ZIndex = 1100` |
| `AskDeleteRecordAsync` | 五個 CRUD 檢視共用的「刪除這一筆」 | 同 `AskDestructiveAsync` |

具體文案仍由呼叫端提供 ——「清除 30 天未再發生的紀錄」這種訊息有資訊價值，
不該為了收斂被壓成罐頭句子。樣板只負責統一參數與按鈕行為。

⚠️ **不要自己寫 `ConfirmAsync`。** 不只是文案會漂移 —— 自己寫必定漏參數。
0.9.26 之前，例外紀錄與 Token 用量的六個「刪除／清空」全都少了 `Danger` 與 `MaskClosable = false`：
長得像一般提醒，而且**誤點遮罩就直接執行了不可復原的動作**。

### 9.2 紅調是怎麼來的

`ConfirmOptions.ClassName` 會被 AntDesign 的 `Confirm::BuildDialogOptions` 無條件覆寫成
`"ant-modal-confirm ant-modal-confirm-" + ConfirmType`，呼叫端給的 class 會被丟掉。
因此輕重之分改用 `.ant-modal-confirm:has(.ant-btn-dangerous)` —— 以「確定鈕是 Danger」當 hook。
`:has()` 不支援時只是退回粉梅（按鈕本身仍是紅的），屬漸進增強。

**少設一次 `Danger`，那個窗就不會轉紅調**，這也是為什麼它必須由樣板決定。

## 10. 通知與消息條

| 浮層 | 選擇器 | 規格 |
|------|--------|------|
| 右下角通知 | `.ant-notification-notice` | 384px 卡片、圓角 18px、`blur(14px)`、`notice-arrive` 右側滑入＋回彈 420ms |
| 頂部消息條 | `.ant-message-notice-content` | 膠囊（圓角 999px）、`blur(14px)`、`msg-arrive` 由上落下＋回彈 400ms |

離場動畫沿用 AntDesign 原本的 `NotificationFadeOut` / `MessageMoveOut`，不覆寫。

⚠️ **圖示的語意色（成功綠／錯誤紅／警告橘／資訊藍）刻意不動。**
為了視覺統一把錯誤訊息染成粉紅，等於拿掉使用者最先讀到的那個訊號。

## 延伸閱讀

- [開發慣例與限制速查](開發慣例與限制速查.md) §6.3 鍵盤事件、§6.4 對話窗尺寸
- [建立一個新 CRUD 操作網頁說明](../guides/建立一個新%20CRUD%20操作網頁說明.md)
- [AI 日誌分析](../features/AI日誌分析.md) §10 —— 唯讀對話窗的三狀態規格（不適用本文件）

> 返回 [文件總索引](../README.md)
