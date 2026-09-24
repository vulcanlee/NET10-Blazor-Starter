# 寄信服務與忘記密碼 設計規格

- 文件版本：1.1
- 文件狀態：已實作（兩階段皆完成）
- 現行系統版本：0.9.60
- 首次實作版本：0.9.59
- 最後核對日期：2026/09/24

## 一、起因

《腳手架開發指引》§10 缺口 #4、§11.2 與路線圖第 5 期 5-1／5-2：全 repo **沒有任何寄信能力**，
連帶**沒有忘記密碼**，使用者被鎖在外面只能找管理員。

## 二、範圍與交付節奏

| 階段 | 版本 | 內容 |
|---|---|---|
| 一 | 0.9.59 | 寄信基礎設施（None／Pickup／Smtp）、背景佇列、健康監控「寄信服務」項、管理員測試寄信 |
| 二 | 0.9.60 | 忘記密碼／重設密碼（匿名兩頁、token 資料表、通知信） |

**不做**：TOTP 接線、密碼原則引擎、帳號審核通知、Web API 端點、依 IP 限流、security stamp（既有工作階段撤銷）。

## 三、已定案的決策

| 主題 | 決定 | 理由 |
|---|---|---|
| Provider | `None`（預設）／`Pickup`（寫 `.eml`，開發用）／`Smtp`（MailKit）| `None` 讓腳手架出貨即可啟動、不會意外寄信；Pickup 讓開發者不用架 SMTP 也能點重設連結 |
| 套件 | MailKit | `System.Net.Mail.SmtpClient` 微軟已標示不建議新專案使用 |
| provider 決定時機 | 解析 `IEmailSender` 時（scoped factory 讀 `IOptionsMonitor`）| 註冊時 switch 會讓 `WebApplicationFactory` 子類的組態覆寫失效 |
| 驗證 | 所有環境 `ValidateOnStart`：Smtp 時 Host、FromAddress 必填。Production 另擋 Pickup、要求 `PublicBaseUrl` | Pickup 的 `.eml` 是明文，讀得到資料夾就能重設任何帳號 |
| 公開網址 | `EmailSettings:PublicBaseUrl`；留白時僅非 Production 退回目前請求網址 | 不採用 Host header，防止重設連結被偽造的 Host 導走（password reset poisoning）|
| 寄送方式 | 忘記密碼與通知信走背景佇列；測試信同步 | 同步寄信時「帳號存在」明顯比「不存在」慢，回應時間本身就洩漏帳號是否存在 |
| 佇列 | 有界 Channel（100），`FullMode = Wait` ＋ `TryWrite` | Drop 系列模式下 `TryWrite` 永遠回 true，丟信也不知道 |
| 失敗處理 | 不重試，記 Error（進系統例外紀錄，來源＝背景作業）| 多數失敗重試不會好；佇列在記憶體，重啟遺失可接受（使用者重新申請）|
| 健康監控 | 「寄信服務」權重 10（總分 125→135）；None／Pickup 黃燈，Smtp 5 秒內連線＋TLS＋登入 | 比照 LLM 選配功能；只看設定抓不到帳密過期或防火牆 |
| 測試寄信 | 系統健康監控頁（管理員專屬）內嵌區塊，收件者預填登入者 Email，稽核 `Email.Test`（不記收件者）| 不必新增選單與權限鍵 |
| 信件 | HTML＋純文字 multipart，模板寫在 C#（`EmailTemplates`），變數一律 HTML 編碼 | 可單元測試、不怕部署漏檔 |
| 識別方式（二） | 帳號或 Email：先比 `Account`（Ordinal），對不到再比 `Email`（不分大小寫）| 使用者常忘帳號但記得信箱 |
| 多帳號同信箱（二） | 每個帳號各寄一封、各自 token | `Email` 沒有唯一索引 |
| 排除對象（二） | `support`、停用帳號、沒有本地密碼的 Google 帳號、Email 無效 → 不寄信、畫面訊息相同、寫稽核 | support 每次啟動會被重設；停用者重設也登不進來 |
| Token（二） | 獨立資料表 `PasswordResetToken`，只存 SHA-256；效期 30 分、冷卻 60 秒（可設定）；新申請與重設成功都作廢其餘 token | 管理員編輯使用者時 `MyUserService.UpdateAsync` 會覆寫 `MyUser` 上 AdapterModel 沒帶的欄位，所以不能加欄位在 `MyUser` |
| 重設後（二） | 解除鎖定、寄「密碼已變更」通知、導回登入頁（不自動登入）| 非本人操作時帳號主人能察覺 |
| 新密碼規則（二） | ≥ 6 字元、兩次一致、≠ `123456`；規則失敗不消耗 token | `123456` 是強制改密碼的哨兵值 |
| 防濫用（二） | 同帳號冷卻 60 秒 ＋ 沿用 4 碼驗證碼；不做 IP 限流 | 驗證碼答案在頁面隱藏欄位，只是減速帶 |
| 外觀（二） | 忘記／重設頁與登入頁同一套外觀 | — |

## 四、元件與分層

```
Business（不引用 MailKit）
  IEmailSender          同步寄出，失敗拋例外
  IEmailQueue           入列即返回
  Helpers/EmailTemplates
Models/Systems/EmailMessage（To, Subject, HtmlBody, TextBody, Kind）

Web/Email
  NullEmailSender / PickupEmailSender / SmtpEmailSender   ← IEmailSender 依 Provider 解析
  ChannelEmailQueue（singleton）→ EmailDispatchWorker（BackgroundService，逐封建 scope）
  EmailHealthProbe      健康頁「寄信服務」項
  EmailTestService      健康頁「寄出測試信」
Web/Configuration/EmailSettings、StartupSafetyValidator
```

日誌只記 `Kind`（信件種類）、`Provider`、`Host`，**不記收件者、主旨、內文**（`LoggingConventionTests` 守門佔位名稱）。

## 五、第二階段設計（0.9.60 已實作）

- `PasswordResetToken`（`Id`、`MyUserId`、`TokenHash`、`CreatedAtUtc`、`ExpiresAtUtc`）：`TokenHash` 唯一索引；
  FK 在 `OnModelCreating` 的 Restrict 迴圈**之後**明確設 Cascade，否則刪除有未用 token 的使用者會失敗。
- `PasswordResetSettings`（`TokenLifetimeMinutes` 5–1440、預設 30；`RequestCooldownSeconds` 0–3600、預設 60）。
- `PasswordResetService`（Business，`IDbContextFactory`）：`RequestAsync`、`ValidateTokenAsync`、`ResetAsync`。
  單次使用以交易內 `ExecuteDeleteAsync` 搶占（刪到 1 列才繼續），避免同一連結並發送出兩次都成功。
  稽核 `Password.ResetRequested`／`ResetCompleted`／`ResetFailed`，在交易 commit 之後才寫。
- 匿名頁 `/Auths/ForgotPassword`、`/Auths/ResetPassword`（靜態 SSR，`NoFooterLayout`）；
  重設頁回應 `Referrer-Policy: no-referrer`、`Cache-Control: no-store`；
  靜態 SSR 下 `NavigateTo` 不會拋例外（`BlazorDisableThrowNavigationException`），每次呼叫後都要 `return`。
- 共用外觀：抽出 `AuthShell` 元件，以 Razor SDK 的 `CssScope` 讓登入、忘記、重設三頁共用 `Login.razor.css` 的 scope。

## 六、已知限制

- 背景佇列只在記憶體，重啟遺失；失敗不重試。
- （二）重設後其他裝置的 Cookie／JWT 仍有效（沒有 security stamp）。
- （二）沒有依 IP 限流；驗證碼只是減速帶。
- （二）token 在網址 query 中，IIS W3C 日誌或開啟 `Hosting.Diagnostics` 的 NLog 規則會記到。
- （二）Email 不分大小寫比對由 SQLite `lower()` 處理，只摺 ASCII。

## 七、驗證

- 每階段：`dotnet build`（0 warning）、`dotnet test`、`dotnet list package --vulnerable`、`scripts/Test-DocsEncoding.ps1`。
- 第一階段實測：Pickup 下健康頁黃燈、寄出測試信產生 `.eml`、稽核有 `Email.Test`；None 下按鈕停用。
- 第二階段實測：Pickup 下完整走一次忘記 → 收信 → 重設 → 登入，並驗證連結單次使用與冷卻。

實作結果見 [寄信服務基礎設施 changelog](../../changelog/2026-09-24-寄信服務基礎設施.md)、
[忘記密碼與重設密碼 changelog](../../changelog/2026-09-24-忘記密碼與重設密碼.md)；
產品現況以 [寄信服務 PRD](../../prd/寄信服務-prd.md)、[登入與帳號流程 PRD](../../prd/登入與帳號流程-prd.md) 為準。

> 返回 [specs 索引](README.md)
