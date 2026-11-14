# AI 快速模型 FastModel（0.10.22）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.10.22
- 首次實作版本：0.10.22
- 最後核對日期：2026/11/14

## 目的

`AiSettings:Model` 是所有 AI 呼叫的預設模型。有些工作不複雜，用便宜、快速的模型就夠，例如把一段文字或一個標題產生標籤或摘要。這次新增 `AiSettings:FastModel`，讓這類工作可以改用它。

## 設定

`appsettings.json` 的 `AiSettings` 新增 `"FastModel": ""`：

- 選填，空白＝一律用 `Model`。
- 只在設定檔設定，不放進系統參數頁（`Model` 本來也不在）。
- Azure 一樣填部署名稱。
- 例如 `Model` 填 `gpt-6.1-sol`、`FastModel` 填 `gpt-6-luna`，兩者都已有費率。新模型記得在 `AiPricingSettings` 補費率。

## 怎麼用

呼叫端在 `AiCallContext.UseFastModel`（或 `AiChatCompletionRequest.UseFastModel`）設為 `true`，就是要求使用快速模型。

`AiAgentFactory.CreateAgent` 選模型的順序：

1. 呼叫端明確指定的 `ModelOverride`
2. 系統參數頁替該功能指定的模型（`AiModelSettings`，依 `ModelKey`）
3. 要求快速模型且 `FastModel` 有填 → `FastModel`
4. `Model`

輔助方法 `AiSettings.DefaultModel(bool fast)`。

## 系統管理助理的分派改用快速模型

「管理助理／分派」每則訊息只回幾個領域代碼，正是快速模型的典型工作。分派選模型的順序：

1. 系統參數「系統管理助理分派使用的模型」有填就用它。
2. 否則用 `FastModel`。
3. `FastModel` 也空白時，照舊用系統管理助理的模型。

沒有設定 `FastModel` 的部署，行為與以前完全相同。

## 健康檢查

「系統健康監控」的「AI 計費表」也會檢查 `FastModel` 有沒有費率（`SystemHealthService.ModelsToPrice`：預設、快速〔有填才算〕與系統參數指定的模型，不分大小寫去重）。

## 測試

- **`AiModelSelectionTests`**（新）：
  - 快速模型只在要求時使用、空白退回 `Model`、前後空白會去掉。
  - 系統參數指定的模型優先。
  - 送出的 model 與記帳的模型一致。
  - `DefaultModel` 與計費表要檢查的模型清單。
- **`AdminAssistantTurnPlannerTests`**：分派的三層順序。
- **`AiSettingsTests`**：`FastModel` 預設為 null。

## 文件同步

- 操作說明：`system-parameters.md`、`system-health.md`、`admin-assistant.md`
- 設定檔說明
- MAF 呼叫架構
- AI 模型計費更新指南
- 速查表
- 新手完整操作指南 §8.1

## 版本

0.10.21 → 0.10.22（與 [移除 Agent 測試台與 Agent 工作流程](2026-11-14-移除Agent測試台與Agent工作流程.md) 同一版）。
