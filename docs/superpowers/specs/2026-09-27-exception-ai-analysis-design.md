# AI 例外分析設計規格

- 文件版本：1.0
- 文件狀態：已實作
- 目標系統版本：0.9.68
- 設計日期：2026/09/27

> 本文為 brainstorming 流程的產出，記錄「為什麼這樣做」。實作後的現況請以
> [AI 例外分析](../../features/AI例外分析.md) 與 [系統例外紀錄 PRD](../../prd/系統例外紀錄-prd.md) 為準，
> 變更結果見 [`docs/changelog/`](../../changelog/README.md)。

---

## 一、需求（與使用者逐項確認）

「系統例外紀錄」點某列「查看」會開「例外明細」窗。需求是在明細窗加一顆按鈕，把畫面內容送給 AI API，
由大語言模型產出分析報告。

| 項目 | 決定 |
|---|---|
| 報告目的 | 根本原因、修正建議、嚴重度與影響、排查步驟四者皆要 |
| 讀者 | 兩者都要、分段：前段給管理者的白話摘要，後段給開發者的技術細節 |
| 送出內容 | 只送明細窗上的內容（類型、訊息、來源、頁面、操作、記錄器、次數、首次／最後發生、完整堆疊） |
| 個資 | 不送帳號／UserId，只送「有／無登入使用者」 |
| 保存 | 不保存（關窗即丟棄），但提供「產生 PDF 並下載」按鈕 |
| 追問 | 要，可多輪追問（帶前文） |
| 顯示位置 | 另開大型 AI 對話窗（96vw×96vh，疊在明細窗之上，對話式排版） |
| PDF 內容 | 例外明細＋初始報告＋每輪追問與回覆；堆疊放附錄 |
| 輪數上限 | 設上限，寫在 `AiSettings`（新鍵 `MaxFollowUpRounds`，預設 10） |
| 實作方式 | 抽出共用核心（HTTP／錯誤對應／用量記錄），日誌分析改用它、行為不變 |

## 二、實作時採用的預設

- 用量作業名稱 `AI 例外分析`，初始分析與追問共用。
- 例外分析用獨立的內建系統提示詞（不吃 `AiSettings.SystemPrompt`）；另加「需要確認的問題」一章，
  讓 AI 主動提出不確定之處，與追問功能互補。
- 堆疊全文送出不截斷；超過內容視窗時回情境化的中文訊息。
- 追問失敗：錯誤以泡泡留在對話中，該則提問從歷史移除並放回輸入框。
- 等待中遮罩不可關；關窗＝取消在途請求並丟棄整段對話。
- 堆疊讀取中按鈕停用；堆疊檔不存在時仍可分析，提示詞註明「堆疊不可得」。

## 三、方案比較

| 方案 | 取捨 | 結論 |
|---|---|---|
| **抽出共用核心** `AiChatCompletionClient` | 動到既有 `AiLogAnalysisService`，但錯誤對應、記帳、取消／逾時區分只有一份，既有測試做回歸守門 | ✅ 採用 |
| 複製一份新服務 | 完全不動既有程式，但約 300 行重複，日後改一邊容易漏另一邊 | 否決 |

## 四、設計

1. **共用核心**：`AiChatCompletionRequest`（`Operation`、`Messages`、三段情境化提示文字）→
   `AiChatCompletionClient.CompleteAsync` → `AiAnalysisResult`。`AiChatRequestFactory` 新增多訊息多載，舊多載委派。
2. **例外分析服務**：`AiExceptionAnalysisService.AskAsync(conversation)` 前置 system prompt、檢查追問上限（超過不發 HTTP）。
   `AiExceptionPromptBuilder.BuildDetailLines` 是送出欄位的唯一出口，畫面與 PDF 共用。
3. **PDF**：`AiReportPdfBuilder` 的樣式與 Markdown 渲染抽成 `AiMarkdownPdfRenderer`；新增 `AiExceptionReportPdfBuilder`。
4. **UI**：`ExceptionAiAnalysisModal` 以 `OpenAsync(item, stackTrace)` 開啟；三態（初始等待、初始失敗、對話中）；
   AI 回覆一律經 `AiMarkdownRenderer.ToSafeHtml`，使用者追問以純文字顯示；以 tabindex=-1 錨點的 `FocusAsync` 捲到最新回覆。
5. **稽核**：`ExceptionLog.AiAnalyze`／`AiFollowUp`／`AiExportPdf`，detail 只記數量；稽核所需數字在送出前先記下，
   關窗清空對話後仍能寫出正確的稽核。

## 五、測試

TDD：`AiChatRequestFactoryTests`、`AiChatCompletionClientTests`、`AiExceptionPromptBuilderTests`、
`AiExceptionAnalysisServiceTests`、`AiExceptionReportPdfBuilderTests`；`AiModalStyleConventionTests` 擴充到新對話窗；
`AiLogAnalysisServiceTests`、`AiReportPdfBuilderTests` 作為重構的回歸守門。
