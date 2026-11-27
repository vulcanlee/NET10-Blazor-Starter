# `.ai-markdown` 權重還原（0.10.45 → 0.10.46）

- 日期：2026/11/27
- 版本：0.10.45 → 0.10.46
- 起因：第三批前端整併（0.10.45）把三個 AI 視窗的報告內文樣式合併成共用的 `.ai-markdown` 時，權重比原本各頁的 scoped CSS 少一級（獨立審查 C.6 第 6 點）。畫面沒有變，但日後一般權重的全域規則會蓋過報告內文。使用者 2026/11/27 要求還原。
- 測試報告：[重複程式碼與死碼清理](../migration/測試報告/重複程式碼與死碼清理.md) 最後的「追加」。

## 變更

- `OverlayStyles.razor` 的 14 個選擇器由 `.ai-markdown …` 改寫成 `.ai-markdown.ai-markdown …`（同一個 class 寫兩次），權重與整併前的 `.log-ai-report[b-xxxx] …` 逐條相同（子元素 (0,2,1)、根 (0,2,0)、`pre code` (0,2,2)、`a[href=""]` (0,3,1)）。區段說明寫明原因，不要「簡化」回一個。
- 各頁保留的 `::deep h1/h2:first-child`（0,3,1）仍然勝出，畫面不變。
- 新增守門測試 `AiModalStyleConventionTests.SharedMarkdownSelectors_ShouldKeepThePreConsolidationSpecificity`。

## 驗證

- 新測試在改 CSS 前失敗、改完通過；品質關卡 8 步全部通過（測試 3,461）。
- 全新隔離主機跑 `m06-ai.mjs` 9 項通過；AI 視窗截圖與 0.10.45 並排比對樣式相同。

## 文件

速查表 §6.33 第 161 點、介面視覺設計規範、AI 日誌分析、CLAUDE.md、AGENTS.md、測試報告追加段落。
