# Token 用量 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.9.14
- 首次實作版本：0.9.14
- 最後核對日期：2026/09/16

## 一、目標與範圍

觀察系統對 LLM API 的使用情況：**用在什麼地方、用了多少 token、能統計與篩選**。

- 範圍：一套通用的用量記錄基礎設施（任何 LLM 呼叫點都能接）、五個統計頁籤、
  CSV／PDF 匯出、單筆刪除／批次清除／清空全部。
- 非範圍：**不做費用計算**（只預留 `EstimatedCost` 欄位）；不提供 Web API。

> **現況提醒**：本腳手架目前只有**一個** LLM 呼叫點（日誌檢視頁的 AI 分析），
> 所以「依作業」「依型別」兩個頁籤各只會有一列。基礎設施是完整的 ——
> 日後新增呼叫點就會自動長出來。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 |
| --- | --- | --- |
| `/token-usage` | 統計與分析 → id=64「Token 用量」 | **管理員專屬**（`CheckIsAdmin()`；權限鍵不上架角色矩陣） |

## 三、畫面

### 3.1 篩選與動作

- 篩選：帳號、起始日、結束日、全部作業、全部型別、全部模型
- 動作：查詢、重新整理、匯出 CSV、下載 PDF 報表、清空全部；另有「清除此日之前 ＋ 批次清除」
- 破壞性動作一律 `ModalService.ConfirmAsync` 二次確認

### 3.2 合計列

`篩選範圍合計 — 輸入(送出) X、輸出(接收) Y、其中快取 Z、其中推理 W、合計 T`

⚠️ **加總語意**：快取是**輸入的折扣子集**、推理**計入輸出**，兩者都只回報「其中多少」，
不再加進合計，否則會重複計算。數字以 K／M 精簡呈現（`TokenUsageFormat.Compact`）。

### 3.3 五個統計頁籤

依使用者／依作業／依模型／依型別（四者共用 `TokenUsageGroupTable` 元件）＋ 明細。
頁籤標題帶筆數。明細逐筆顯示時間／使用者／作業／型別／模型／輸入／輸出／推理／快取／合計，
失敗的列在「合計」欄顯示紅色「失敗」標籤（滑過看原因）。

### 3.4 明細窗

點「詳細」開啟，分四區：基本資訊、輸入 (Input)、輸出 (Output)、其他，
最後是**原始明細（供應商回傳）**——把檔案裡的 usage JSON 平攤成 `prompt_tokens_details.cached_tokens`
這種點路徑逐列顯示。開窗當下才讀檔。

⚠️ 明細窗的**尺寸與內容樣式都寫在 `Components/Commons/FormModalHelper.razor` 的全域 `<style>`**：
AntDesign 的 Modal 渲染在元件 DOM 之外，scoped CSS（連 `::deep`）打不到。
該檔 `<style>` 內的 `@media` 必須寫成 `@@media`，否則 Razor 編譯失敗。

## 四、內部系統運作

### 4.1 記錄管線

```
LLM 呼叫點（發出 HTTP 的那一層）
   → ITokenUsageRecorder.RecordAsync(TokenUsageEntry)
   → TokenUsageLogService → SQLite（可聚合的數值欄位）
                          → TokenUsageRawStore → 檔案系統（原始 usage JSON）
```

**⚠️ 記錄點必須放在發出 HTTP 的那一層，不可放畫面層** —— 原始 usage、實際模型名稱與耗時，
回到畫面之前就已經丟失了。目前唯一的呼叫點是 `AiLogAnalysisService.AnalyzeAsync`。

**不需要佇列與背景寫入器**（與系統例外紀錄不同）：LLM 呼叫是使用者主動觸發、一次數秒到數分鐘，
多一次幾毫秒的資料庫寫入可忽略。

### 4.2 新增 LLM 呼叫點的擴充方式

1. 注入 `ITokenUsageRecorder`
2. 在 `TokenUsageOperations`／`TokenUsageCallKinds` 登記常數
3. 拿到 API 回應後呼叫一次 `RecordAsync`

該次呼叫就會自動出現在本頁與所有統計頁籤上，不需要改頁面。

### 4.3 記錄哪些呼叫

**全部記錄，成功與失敗都記**：

| 情境 | 有用量？ | 說明 |
| --- | --- | --- |
| 成功 | 有 | — |
| **回應成功但內容為空** | **有** | **付了錢卻沒拿到東西**，最值得被看見 |
| 逾時／傳輸失敗／上游錯誤 | 無 | 上游可能已計費，至少留下次數 |
| 設定不完整 | — | **不記**，根本沒發出請求 |
| 使用者取消 | — | **不記**，與例外紀錄不收 `OperationCanceledException` 同一原則 |

### 4.4 原始 usage JSON

- 路徑：`SystemSettings.ExternalFileSystem.TokenUsagePath`（預設 `C:\temp\MyProject\TokenUsage`）
- 檔名：`{yyyyMM}/{GUID}.json`（每次呼叫獨立一筆，不合併，所以用 GUID 而非雜湊）
- 生命週期集中在 `TokenUsageRawStore`；刪單筆／批次清除／清空全部三條路徑都經過它
- **價值**：供應商專有欄位（實測 Azure 會回 `latency_checkpoint`、`cache_write_tokens`）
  沒有對應的資料表欄位，但原始 JSON 完整留住了，日後新增欄位也不會漏接

### 4.5 ⚠️ 安全紅線

**只存 `usage` 子物件，絕不存提示詞與模型回應內文。**

本專案唯一的 LLM 呼叫是 AI 日誌分析，它的**提示詞就是整份日誌內容**，而日誌內容有一部分
來自使用者輸入。存進來等於繞過日誌檢視頁的管理員限制，也會破掉
「稽核紀錄不得寫入日誌內容或 AI 輸出」這條既有紅線。

`AiChatResponseParser.ExtractUsageJson` 只取 `usage` 那一段，**不可**改成回傳整個回應 body。
由 `AiLogAnalysisServiceTests.AnalyzeAsync_ShouldRecordOnlyUsageJson_NotTheWholeResponseBody` 守門。

### 4.6 順帶修正

0.9.14 之前，PDF 匯出會用同一份分析結果**再寫一筆帶相同用量數字的 `AuditLog`**，
若拿 `AuditLog` 做成本加總會**重複計算**。記錄點改在 service 內之後，
只會記真正發出 HTTP 的那一次。

### 4.7 兩個編碼／樣式的坑（0.9.14 實測踩到）

1. ⚠️ **`new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(text)` 不會輸出 BOM。**
   那個旗標只影響 `GetPreamble()` 與 `StreamWriter`。少了 BOM，Excel 開啟 CSV 會整片亂碼，
   而編譯、測試、程式碼審閱全都看不出來。正確寫法是把 `GetPreamble()` 接在 `GetBytes()` 前面。
2. ⚠️ **Blazor 的 scoped CSS 以元件為界。**`TokenUsageGroupTable` 是獨立元件，
   套不到 `TokenUsageView.razor.css` 裡的 `.token-usage-table-wrap`，所以四個統計頁籤
   會把整頁撐寬。修法是給該元件自己的 `TokenUsageGroupTable.razor.css`。

## 五、權限與安全

- **管理員專屬**：`CheckIsAdmin()` 守門，`角色_Token用量` **不列入**
  `RolePermissionService.GetRoleListPermissionAllName()`，由 `AdminOnlyPermissionTests` 守住。
- 只記 `Account` 與 `UserId`，**不記姓名／Email**，與全站日誌規範一致。
- ⚠️ 記錄相關的日誌訊息**不得含 `token` 字樣**（`LoggingConventionTests` 的禁用清單），
  這也是欄位叫 `InputCount` 而非 `InputTokens` 的原因。

## 六、驗收與測試

| 測試 | 驗什麼 |
| --- | --- |
| `TokenUsageLogServiceTests` | 寫檔＋寫列、失敗也記、刪列同時刪檔、清空、`PurgeBeforeAsync`、四個維度的分組統計、**合計不重複計算快取與推理**、結束日涵蓋當天整天、`RecordAsync` 絕不拋出 |
| `AiLogAnalysisServiceTests` | 成功／空回應／上游錯誤三種分支都有記錄；設定不完整不記；**只存 usage JSON、不含回應 body** |
| `TokenUsageReportPdfBuilderTests` | PDF 簽章、無資料、明細上限 200 筆、依時長計費與失敗列都排得出來 |
| 守門 | `DataAccessServiceLifetimeTests`、`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`ApiIntegrationTests`（`TokenUsagePath` 導向暫存目錄） |

手動驗收（0.9.14 實跑結果）：

- 統計與分析 →「Token 用量」可開，`toll` 圖示正常，五個頁籤數字一致，無 console 錯誤。
- 以真實 Azure OpenAI 呼叫驗證：記到 `gpt-5.6-sol-2026-07-09`、輸入 8,122／輸出 1,967／
  合計 10,089、推理 439，使用者 `support`。
- 檔案內容經檢查**只有 usage 結構**，不含 `choices`／`message`／`content`。
- 明細窗四區與原始明細（九個點路徑欄位）皆正確。
- 匯出 CSV：檔頭三個位元組為 `EF BB BF`（UTF-8 BOM），Excel 開啟繁中不亂碼。
- 下載 PDF：115KB，內嵌 **Noto Sans TC** 子集（`FontFile2` + `Identity-H` + `CIDFontType2`），
  抽出文字比對**零缺字**，中文不是空白方塊。
- 清除此日之前 2026-09-01：7 列→4 列，原始明細檔 6→3，只刪指定日之前的。
- 刪單筆：資料列與對應的原始明細檔一併消失。
- 清空全部：0 列，`TokenUsagePath` 目錄淨空（連年月子目錄一起清掉）。
- 非管理員（暫時把 `IsAdmin` 設為 0 驗證）：側邊欄不顯示本頁，直接輸入網址顯示「你沒有權限存取此頁面」。
- 1366px／1600px／400px 三種寬度頁面皆無水平捲軸（明細表在窄螢幕於容器內自行捲動）；全站 console 零錯誤。

## 七、相關程式與文件

- `MyProject.AccessDatas/Models/TokenUsageLog.cs`、`Migrations/*_AddTokenUsageLog.cs`
- `MyProject.Models/Systems/TokenUsageModels.cs`、`AdapterModel/TokenUsageLogAdapterModel.cs`
- `MyProject.Business/Services/DataAccess/ITokenUsageRecorder.cs`、`TokenUsageLogService.cs`
- `MyProject.Business/Services/Other/TokenUsageRawStore.cs`
- `MyProject.Web/Ai/AiLogAnalysisService.cs`（記錄點）、`AiChatResponseParser.ExtractUsageJson`
- `MyProject.Web/Ai/TokenUsageReportPdfBuilder.cs`、`Diagnostics/TokenUsageFormat.cs`
- `MyProject.Web/Components/Pages/Analytics/TokenUsagePage.razor`、
  `Components/Views/Analytics/TokenUsageView.razor`、`TokenUsageGroupTable.razor(.css)`
- 交叉連結：[AI 日誌分析](../features/AI日誌分析.md)、[系統例外紀錄 PRD](系統例外紀錄-prd.md)、
  [開發慣例與限制速查](../architecture/開發慣例與限制速查.md)
