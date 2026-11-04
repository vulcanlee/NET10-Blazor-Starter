# 新增 AI Agent PRD（0.9.151）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.9.151
- 首次實作版本：0.9.151
- 最後核對日期：2026/11/04

## 目的

「Agent 測試台」（0.9.142）與「Agent 工作流程」（0.9.144）都是選單頁，但設計只寫在 [MAF AI 呼叫架構](../architecture/MAF-AI呼叫架構.md) 與 5 篇 changelog 裡，
沒有 PRD，也不在 [PRD 主控台](../prd/README.md) 的能力矩陣裡，違反 PRD 維護規則第 1 條（新增選單頁時 PRD 與矩陣必須同步）。
另外 0.9.149（Agent 改走 Responses API）沒有同步 AI 對話紀錄 PRD。這一版只補文件，不改程式。

## 一、新增 [AI Agent PRD](../prd/AI-Agent-prd.md)

一份 PRD 同時涵蓋 Agent 測試台與 Agent 工作流程：

- 目標與範圍（包含與不包含）、入口與權限鍵（不自動開給預設角色）。
- Agent 測試台：範例 Agent 與工具、核准窗、對話保存與接續、測試專案。
- Agent 工作流程：兩條範例流程、三種人工介入、執行紀錄。
- 模型、Responses API（`store=false`）與推論等級。
- 記帳與資料生命週期、權限與安全、錯誤與邊界、驗收與測試。
- 已知限制、規劃中需求。

### 推論等級（§5.3）

| 項目 | 內容 |
|---|---|
| 設定鍵 | `AiSettings:AgentReasoningEffort` |
| 預設 | `null`，不送 `reasoning.effort`，由模型決定 |
| 實際採用的等級 | 查 2026/11/04 實測的 AI 對話紀錄：20 筆 Responses 回應的 `reasoning.effort` 都是 `medium`，所以 Azure `gpt-6.1-sol` 實際用 `medium` |
| 可填值 | `none`／`low`／`medium`／`high`／`xhigh`；`gpt-6.1-sol` 不接受 `none` |
| 改法 | User Secrets、`appsettings.json` 或環境變數 |
| 生效時機 | 下一則訊息或下一次執行，不必重啟 |
| 範圍 | 全域，所有 Agent 共用 |

依功能分設、在畫面上切換列在「規劃中需求」，目前不提供（使用者決定維持設定檔）。

## 二、同步既有 PRD

| 文件 | 異動 |
|---|---|
| [PRD 主控台](../prd/README.md) | 能力矩陣新增「AI Agent」列；開頭能力描述補上 AI Agent；AI 對話紀錄列補 0.9.149 |
| [AI 對話紀錄 PRD](../prd/AI對話紀錄-prd.md) | §3.3 補上 Responses 格式的請求、回應文字與角色解析（`instructions`、`input`、`function_call`、`function_call_output`）；§4.2 補「工具結果（Tool）」角色 |
| [系統參數 PRD](../prd/系統參數-prd.md) | 「AI 模型」列註明推論等級不在系統參數頁，改由設定檔調整 |

## 三、異動檔案

- `docs/prd/AI-Agent-prd.md`（新）
- `docs/prd/README.md`、`docs/prd/AI對話紀錄-prd.md`、`docs/prd/系統參數-prd.md`
- `docs/changelog/README.md`、`docs/README.md`、本篇
- `appsettings.json`：`SystemVersion` 0.9.150 → 0.9.151

## 四、驗證

- PRD 寫到的路由、權限鍵、工具名稱、設定鍵與測試類別，都已用原始碼逐一對照。
- `pwsh ./scripts/Invoke-QualityGate.ps1`（含文件編碼檢查）。
