# 站內通知與公告 PRD

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.9.101
- 首次實作版本：0.9.100
- 最後核對日期：2026/10/04

## 一、目標與範圍

讓系統與業務模組能「通知某人某件事」，並讓管理員對全體或特定對象發布公告；通知保存在資料庫、可標為已讀，不再只有轉眼即逝的右下角提示。

- **範圍**：通知（資料表、發送服務 `INotificationSender`、即時訊號、鈴鐺、已讀、可選的同步寄信、自動清除）、公告（資料表、管理頁 `/announcements`、每頁上方的橫幅、個人關閉）、四個內建事件。
- **非範圍**：管理員手動發通知的畫面（業務需要時由程式發）；通知的使用者偏好設定（訂閱／退訂）；公告寄信；公告的多選對象與富文字。

使用者決定（2026/10/04）：通知由程式發出、公告由管理員發布；內建事件四個；公告為每頁上方可關閉的橫幅；程式可指定同時寄信；公告對象為全體或單一角色／團隊。

## 二、使用者與入口

| 入口 | 位置 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| 通知鈴鐺 | 每個登入後頁面的右上角（`MainLayout`） | 登入即可（只看得到自己的） | 所有使用者 |
| 公告橫幅 | 每個登入後頁面的內容上方（`MainLayout`） | 登入即可（依對象） | 所有使用者 |
| `/announcements` | 系統管理 → 公告管理（`Menu.json` id=38）| 僅管理員（`CheckIsAdmin`；權限鍵 `角色_公告管理` 不進角色矩陣）| 系統管理員 |

## 三、畫面與欄位

- **鈴鐺**：未讀數徽章（超過 99 顯示 99+）；下拉列出最新 10 則（未讀以粗體標示）；點一則標為已讀，連結經 `ReturnUrlGuard` 確認是站內網址才導向；「全部標為已讀」。
- **公告橫幅**：顯示期間內、對象包含目前使用者、而且他沒有關閉過的公告；標題＋純文字內容（保留換行）；「知道了」只對自己關閉。
- **公告管理**：表格（標題與內容開頭、狀態未開始／進行中／已結束、期間、對象、建立者）；新增、修改（小型表單窗：標題、內容、開始與結束時間、對象全體／角色／團隊）、刪除（危險確認）。

## 四、內部系統運作

- **資料**（Migration `AddNotifications`）：
  - `Notification`：一位收件人一列（`RecipientUserId` Cascade、`Category`、`Title`、`Body`、`Link`、`SourceKey`、`CreatedAtUtc`、`ReadAtUtc`），索引 (收件人, 已讀)、(收件人, 建立時間)、(收件人, 去重鍵)、建立時間。
  - `Announcement`：標題、內容（純文字）、`StartAtUtc`、`EndAtUtc`（不含，可為空）、`TargetKind`（All／Role／Team）、`TargetId`（不設外鍵）、建立者、時間、`ConcurrencyStamp`（條件式 UPDATE 比對）。
  - `AnnouncementDismissal`：(公告, 使用者) 主鍵，兩邊 Cascade。
- **發送** `NotificationSender`（`INotificationSender.SendAsync(NotificationRequest)`）：
  1. 展開對象：`Users`、`Role`（`UserRole` ∪ 主要角色；已刪除的角色沒有收件人）、`Team`（`IEffectiveTeamResolver.GetUserIdsInTeamAsync`，與列級權控同一個定義）、`AllAdmins`（啟用中的管理員），可 `Union`；最後只留啟用、未刪除的帳號。
  2. 去重：有 `SourceKey` 時，已收過同鍵的收件人不再發。
  3. 一位收件人寫一列 → `INotificationSignal.Publish(收件人)` →（`AlsoEmail`）交給 `INotificationMailer`。
  4. ⚠️ 絕不丟例外：失敗記錯誤並回 `Failed`，不中斷登入或排程作業。
- **寄信** `NotificationMailer`：寄信停用不寄；信箱格式不合（例如 support 預設的 `support`）略過；連結以 `EmailSettings:PublicBaseUrl` 組成完整網址；佇列滿時停止並只記一筆警告；日誌不記信箱。信件種類 `EmailKinds.Notification`。
- **即時更新**：singleton `NotificationSignal` 依使用者 Id（與「公告」）訂閱，只送「有變動」訊號，畫面自己重查；取消訂閱時原子移除空的外層集合（防洩漏）；派送時不帶發送端的執行情境；一個處理程序失敗不影響其他人。單一行程以外（IIS 重疊回收、多台主機）靠鈴鐺與橫幅各自 60 秒輪詢補足。
- **公告快取** `AnnouncementCache`（singleton）：所有連線共用「還沒結束的公告」（含尚未開始的），管理頁存檔時失效並廣播，否則 60 秒重讀；橫幅的角色 Id 與已關閉清單每個連線讀一次，換頁不查資料庫。
- **內建事件**：

| 事件 | 收件人 | 同時寄信 | 去重鍵 | 來源 |
|---|---|---|---|---|
| Google 第一次登入建立待開通帳號 | 管理員 | 否 | `AccountPending:{使用者 Id}` | `ExternalLoginService` |
| 排程作業失敗（不含中斷、略過；含系統備份） | 管理員＋手動觸發者 | 是 | — | `ScheduledJobRunner`（結束後以新 scope 發送） |
| 帳號被鎖定 | 管理員 | 是 | 每次鎖定（不設去重鍵） | `MyUserServiceLogin`（0.9.101 起） |
| 密碼即將到期（7 天內） | 本人 | 否 | `PasswordExpiring:{使用者 Id}:{到期時間}` | 排程作業「密碼到期提醒」（`PasswordExpiryReminder`，`0 8 * * *`，0.9.101 起） |

- **清除**：排程作業「站內通知清理」（`NotificationRetention`，`0 3 * * *`）刪除建立超過 `NotificationSettings:RetentionDays`（預設 90，系統參數）天的通知，分批進行，寫 `Notification.AutoPurge`。

## 五、權限與安全

- 通知只看得到自己的；所有標為已讀的 UPDATE 都帶 `RecipientUserId = 目前使用者`。
- 通知與公告內容一律純文字（不經 Markdown／HTML），連結只導向站內網址。
- ⚠️ 例外記錄管線內（例外告警服務、例外寫入器）不可發通知：發送失敗會記錯誤，形成遞迴。
- 稽核：`Announcement.Create`／`Update`／`Delete`、`Notification.AutoPurge`。

## 六、錯誤與邊界

- 發送失敗（資料庫無法寫入）→ 記錯誤、回 `Failed`，呼叫端流程照常。
- 寄信佇列滿 → 剩下的信略過並記一筆警告；站內通知照常。
- 公告對象的角色或團隊被刪除 → 公告不再對任何人顯示，管理頁標示「（已刪除）」。
- 兩位管理員同時修改同一則公告 → 第二位得到並行衝突訊息。
- 已開啟的畫面：同一行程內立即更新；其他行程最晚 60 秒。

## 七、驗收與測試

- `NotificationTests`：各種對象（停用與已刪除帳號、停用的管理員、主要角色、已刪除角色、團隊直接與經角色預設團隊）、⭐ 團隊反查與有效團隊互相對照、去重、只在要求時寄信、發送失敗不丟例外、標為已讀只動自己的、最新排序與清除、徽章上限、⭐ 訊號只送給收件人且取消訂閱不留殘餘、處理程序失敗互不影響且不繼承呼叫端情境、並行訂閱不洩漏、寄信規則（停用、格式不合、佇列滿、完整網址）、公告時間邊界（開始含、結束不含）、橫幅篩選、公告驗證與衝突與個人關閉、已結束公告不快取、已刪除角色不算、Google 待開通通知一次、刪除使用者連帶刪除、清除作業。
- `ScheduledJobFrameworkTests`：失敗通知管理員與觸發者並寄信、排程失敗只通知管理員、成功與中斷不通知。
- `Components/NotificationComponentTests`（bUnit）：鈴鐺收到訊號不重新整理就更新、全部標為已讀；橫幅「知道了」立即隱藏且下一次連線不再出現。
- 守門同步：`ScheduledJobWiringTests`、`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`SystemParameterTests`、`DataAccessServiceLifetimeTests`、`LoggingConventionTests`、`PageHelpCatalogTests`。

## 八、相關程式與文件

- `src/MyProject/MyProject.Business/Services/Other/NotificationSender.cs`（`INotificationSender`、`NotificationTarget`、`NotificationRequest`）、`NotificationSignal.cs`、`NotificationCategories.cs`
- `src/MyProject/MyProject.Business/Services/DataAccess/NotificationQueryService.cs`、`AnnouncementService.cs`；`IEffectiveTeamResolver.GetUserIdsInTeamAsync`
- `src/MyProject/MyProject.Web/Email/NotificationMailer.cs`、`Components/Layout/NotificationBell`、`AnnouncementBanner`、`AnnouncementCache`、`Components/Views/Admins/AnnouncementView`、`Scheduling/Jobs/NotificationRetentionJob.cs`
- 規則：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)「站內通知」；操作說明 `announcements.md`、`app.md`
