# 寄信服務 PRD

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.9.60
- 首次實作版本：0.9.59
- 最後核對日期：2026/09/24

## 一、目標與範圍

讓系統能對外寄信，作為忘記密碼、通知信等功能的共用基礎。寄送方式可切換，
開發機不必架 SMTP 也能看到信件內容，正式環境則以 MailKit 經 SMTP 寄出。

- **範圍**：`EmailSettings` 設定區段、三種寄送方式（None／Pickup／Smtp）、同步寄送介面 `IEmailSender`、
  背景佇列 `IEmailQueue`、系統健康監控的「寄信服務」檢查項與「寄信測試」區塊、Production 啟動安全檢查。
- **使用者**：忘記密碼／重設密碼（0.9.60 起）是第一個使用 `IEmailQueue` 的功能，流程見 [登入與帳號流程](登入與帳號流程-prd.md)。
- **非範圍**：
  信件範本管理介面、附件、寄送紀錄查詢、退信處理、重試與持久化佇列。

## 二、使用者與入口

| 入口 | 所需權限 | 主要使用者 |
|------|----------|-----------|
| `appsettings.json` 的 `EmailSettings`（＋環境變數／User Secrets）| 主機管理權 | 部署人員 |
| `/system-health` 的「寄信服務」檢查項與「寄信測試」區塊 | 管理員（`IsAdmin`）| 系統管理員 |
| `IEmailSender`／`IEmailQueue`（程式介面）| — | 開發者（新增會寄信的功能時）|

## 三、畫面與欄位

本能力沒有自己的頁面，畫面在 [系統健康監控](系統健康監控-prd.md)：

- **「寄信服務」檢查項**（權重 10）：`None`／`Pickup` 黃燈；`Smtp` 實際連線＋加密＋登入（上限 5 秒、不寄信），
  成功綠燈、失敗紅燈。佐證顯示 Provider、主機、埠號、加密方式；帳號與寄件者只顯示「已設定／未設定」。
- **「寄信測試」區塊**：目前的寄送方式、收件者 Email（預填登入者 Email）、「寄出測試信」按鈕。
  `None` 時輸入框與按鈕停用並提示如何啟用。結果以綠／紅訊息顯示；失敗只顯示例外型別名稱。

各欄位意思見 [畫面與欄位字典 §5.1](../guides/畫面與欄位字典.md)。

## 四、內部系統運作

### 4.1 寄送方式

| Provider | 行為 | 用途 |
|---|---|---|
| `None`（預設）| `NullEmailSender`：不寄信，只記一筆 Information 日誌 | 出貨預設、整合測試、不需要寄信的部署 |
| `Pickup` | `PickupEmailSender`：以 MimeKit 寫成 `{時間}-{Guid}.eml` 到 `PickupDirectory`（首次寫信時建立資料夾）| 開發機直接用郵件程式開啟信件 |
| `Smtp` | `SmtpEmailSender`：MailKit，每封信新建 `SmtpClient`，連線 → 有 `UserName` 才登入 → 寄出 → 中斷 | 正式環境 |

`IEmailSender` 在**解析時**依 `IOptionsMonitor<EmailSettings>.CurrentValue` 決定實作（scoped factory），
不是註冊時 switch —— 這樣整合測試的組態覆寫才生效。

### 4.2 兩個呼叫入口

| 介面 | 行為 | 何時用 |
|---|---|---|
| `IEmailSender.SendAsync` | 同步寄出，失敗拋例外 | 呼叫端要當場知道成敗（測試寄信）|
| `IEmailQueue.TryEnqueue` | 入列即返回；`EmailDispatchWorker` 在背景逐封建立 scope、以 `IEmailSender` 寄出 | 匿名流程（忘記密碼）—— 避免回應時間洩漏帳號是否存在 |

- 佇列：有界 `Channel`，容量 100，`FullMode = Wait` ＋ `TryWrite`，滿了回 `false` 並記 Warning。
- 單封信上限為 `TimeoutSeconds × 2`；失敗**不重試**，記 Error（進系統例外紀錄，來源＝背景作業）。

### 4.3 信件內容

- `Business/Helpers/EmailTemplates` 產生 HTML＋純文字（multipart/alternative），變數一律 HTML 編碼，主旨以 `[系統名稱]` 開頭。
- 寄件者：`FromName`（留空用系統名稱）＜`FromAddress`＞；`Pickup` 沒填位址時用 `no-reply@localhost`。

### 4.4 設定與驗證

- 設定鍵見 [日誌與設定檔說明 §4.3.2.2](../operations/日誌與設定檔說明.md)。
- 所有環境（`ValidateOnStart`）：Provider／Security 必須可解析、`Port` 1–65535、`TimeoutSeconds` 1–300；
  `Smtp` 時 `Host` 不可空、`FromAddress` 必須是有效 Email。
- Production（`StartupSafetyValidator`）：`Pickup` 拒絕啟動；`Smtp` 另需 `Host`、`FromAddress` 與完整的 http(s) `PublicBaseUrl`。

## 五、權限與安全

- 測試寄信只有管理員看得到（系統健康監控頁本身是管理員專屬）。
- 稽核 `Email.Test`：成功與失敗都寫，detail 只有 provider 與例外型別，**不含收件者**。
- 日誌只記 `Kind`（信件種類）、`Provider`、`Host`，不記收件者、主旨、內文。
- `Password` 出貨一律空字串，以 User Secrets／環境變數提供。
- 不略過 TLS 憑證驗證（不設 `ServerCertificateValidationCallback`）。
- 信中連結以 `PublicBaseUrl` 為準，不採用請求的 Host header。

## 六、錯誤與邊界

- Provider 打錯字、`Smtp` 缺主機或寄件者 → 啟動失敗，訊息指出是哪個鍵。
- 測試寄信的收件者空白或格式錯誤 → 顯示「請輸入有效的收件者 Email。」，不寄出、不寫稽核。
- SMTP 連不上或登入失敗 → 健康項紅燈（5 秒內回應），測試寄信顯示「寄送失敗：{例外型別}」。
- 背景佇列滿（100 封未寄）→ `TryEnqueue` 回 false，該封信不會寄出。

## 七、已知限制與規劃中需求

**已知限制**

- 佇列只在記憶體：程式重啟時尚未寄出的信會遺失。
- 寄送失敗不重試、沒有寄送紀錄頁（失敗只在系統例外紀錄與日誌裡）。
- 不支援附件、CC／BCC、多收件者。

**規劃中需求**

- 無。忘記密碼／重設密碼已於 0.9.60 實作（重設信 `PasswordReset`、密碼已變更通知 `PasswordChanged`），
  設計見 [設計文件](../superpowers/specs/2026-09-24-email-password-reset-design.md)。

## 八、測試

- `EmailSettingsTests`：解析、出貨值、`ValidateOnStart`、`IEmailSender` 依 Provider 解析、佇列單一實例。
- `PickupEmailSenderTests`：寫出可解析的 multipart `.eml`。
- `EmailQueueTests`：佇列滿回 false；背景寄信器依序寄出、一封失敗不影響下一封。
- `EmailHealthProbeTests`：SMTP 連不上時回報失敗、不拋例外。
- `EmailTestServiceTests`：None／無效收件者拒絕、成功與失敗都稽核且不含收件者。
- `EmailTemplatesTests`：HTML 編碼、主旨。
- `ApiIntegrationTests`：Production 下 Pickup 拒絕、Smtp 設定不完整拒絕、完整設定通過。
- `SystemHealthTests.CheckWeights_ShouldSumTo135`：寄信服務權重 10。

## 九、相關程式與文件

- `src/MyProject/MyProject.Web/Configuration/EmailSettings.cs`、`StartupSafetyValidator.cs`
- `src/MyProject/MyProject.Web/Email/`（三個 sender、`ChannelEmailQueue`、`EmailDispatchWorker`、`EmailHealthProbe`、`EmailTestService`、`MimeMessageFactory`）
- `src/MyProject/MyProject.Business/Services/Other/IEmailSender.cs`、`IEmailQueue.cs`
- `src/MyProject/MyProject.Business/Helpers/EmailTemplates.cs`、`src/MyProject/MyProject.Models/Systems/EmailMessage.cs`
- `src/MyProject/MyProject.Web/Extensions/ServiceCollectionExtensions.cs`（`AddConfiguredEmail`）
- 交叉連結：[系統健康監控](系統健康監控-prd.md)、[稽核紀錄](稽核紀錄-prd.md)、[開發慣例與限制速查 §6.12](../architecture/開發慣例與限制速查.md)、[正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)

> 返回 [PRD 主控台](README.md)
