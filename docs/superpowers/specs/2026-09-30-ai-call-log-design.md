# AI 對話紀錄設計規格

- 文件版本：1.0
- 文件狀態：已實作
- 目標系統版本：0.9.72
- 設計日期：2026/09/30

> 本文為 brainstorming 流程的產出，記錄「為什麼這樣做」。實作後的現況請以
> [AI 對話紀錄 PRD](../../prd/AI對話紀錄-prd.md) 為準，變更結果見 [`docs/changelog/`](../../changelog/README.md)。

> 📌 現況差異（2026/10/05 核對 0.9.114）：本文為封存快照，內容維持當時原貌。與現行系統不同之處：
> - 文中的「統計與分析群組」已於 0.9.113 改名為「監控與診斷」，而 AI 對話紀錄（id 66、圖示 `forum` 不變）移到新的「AI 管理」子群組（id 7），與 AI 提示詞、Token 用量同組 —— 見 [AI 對話紀錄 PRD](../../prd/AI對話紀錄-prd.md)
> - 自動過期不再由獨立背景服務（`AiCallLogRetentionWorker`）執行：0.9.96 起改為排程作業 `AiCallLogRetention`（預設每天 03:00），並寫稽核 `AiCallLog.AutoPurge` —— 見[排程作業 PRD](../../prd/排程作業-prd.md)

---

## 一、需求

把 ReviewSkills.AI 的「AI 對話紀錄」頁（`/ai-call-logs`，1.0.34）複刻到本專案，UI/UX 改用本專案的設計規範。

## 二、關鍵衝突與決定

本專案速查表 §6.7 原本明文禁止保存提示詞與模型回應內文（並有測試守門），複刻本功能等於要保存完整內文。

| 項目 | 決定 | 理由 |
|---|---|---|
| 內文紅線 | 比照 ReviewSkills 有條件放寬：完整內文只准進 AI 對話紀錄（獨立內容檔目錄、管理員專屬、自動過期、`Enabled=false` 即不記）| 排查 AI 問題需要原文；Token 用量、NLog、稽核仍不得存內文，原本的風險（保存期長、可被統計匯出）不擴散 |
| 功能範圍 | 基本功能＋與 Token 用量互跳（`CallId`）＋同一段對話串（`ConversationId`）＋JSON／PDF＋自動過期 | 使用者全選 |
| 串流（SSE）| 不移植 | 本專案沒有串流呼叫點（YAGNI）；日後以可空欄位擴充 |
| 區塊樣式 | 原始 Request／Response 用深梅黑原文區塊；對話訊息為淺色卡片＋左側角色色條；Markdown 沿用 AI 分析報告排版 | 原文區塊在本專案只用於「程式碼類」內容（0.9.70 統一）；長篇 Prompt 用淺色較好讀 |
| 字級 | 明細窗加「一般／中／大」並納入 `AiModalStyleConventionTests` | 與另外兩個 AI 視窗操作一致 |
| 預設設定 | `Enabled=true`、`RetentionDays=90` | 裝好就能用；頁面說明框標示內容敏感與保留天數 |
| 健康探測 | 要記 | 規則單純：凡送出就記；可用作業篩掉、也會過期 |
| 稽核 | 刪除／清除／清空／匯出都寫（只記動作、Id、筆數）| 內容敏感，誰匯出或刪除過要查得到（統計與分析群組的新前例）|
| Token 用量 CSV | 不加 `CallId` 欄 | 維持既有格式；要對照走明細窗的「查看對話」|
| Token 用量行為 | 不變（仍不記取消與非預期錯誤）| 不擴大既有功能的異動面；兩頁筆數不同在文件與說明中交代 |
| 權限／入口 | 管理員專屬、權限鍵不可授予；選單 id 66、圖示 `forum`；不做 Web API | 與統計與分析群組其他頁一致 |

## 三、與 ReviewSkills 的結構差異

1. 本專案的 `AiChatCompletionClient` 由 DI 注入（ReviewSkills 在服務內 `new`），所以只有它與 `AiHealthProbe` 的建構子要多一個 `IAiCallLogRecorder`。
2. 本專案 AI 例外分析的第一次分析與追問共用作業名稱「AI 例外分析」，以關聯說明區分輪數。
3. `AiExceptionAnalysisService` 無狀態、對話在 `ExceptionAiAnalysisModal` 裡，所以 `ConversationId` 由視窗在開窗時產生，
   經 `AiExceptionCallContext` 傳入（ReviewSkills 由服務產生並回填到結果上）。
4. 自動過期的背景服務比照 `EmailDispatchWorker`（而非 `ExceptionLogWriter`，後者在例外管線內禁止使用 `ILogger`）。
5. PDF 改建在本專案既有的 `AiMarkdownPdfRenderer` 上，章節標題用 Heading1（本專案慣例：H1 是報告結構、H2／H3 是模型內容）。
6. 明細窗內容樣式放在元件自己的 `.razor.css`（元件自有的標記即使在 Modal 內也帶 scope 屬性），只有尺寸放 `OverlayStyles.razor`。

## 四、風險與對策

| 風險 | 對策 |
|---|---|
| 整合測試的 host 會真的跑自動過期，刪到開發者的真實目錄 | `ApiIntegrationTests.CreateSettings()` 同步加 `AiCallLogPath` |
| `finally` 裡記錄丟例外會蓋掉原本的回傳值 | `RecordSafelyAsync` 再包一層；以會丟例外的 recorder 測試守門 |
| `MarkSent()` 位置錯誤會多記或漏記 | 緊貼 `SendAsync`；未設定不記、傳輸失敗有記都有測試 |
| 深連結帶查詢字串，選單與頁名比對失敗 | `MainLayout`／`NavMenu` 比對前去掉 `?`／`#` |
| 內容檔很大拖慢畫面 | 只渲染目前檢視、每段 20 萬字元、Markdown 超過 512 KB 退回純文字 |
| 內容檔放錯位置被當靜態檔公開 | 設定說明與部署檢查清單明確禁止放在 `DownloadPath` 底下 |
