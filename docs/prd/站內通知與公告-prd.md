# 站內通知與公告 PRD

- 文件版本：1.13
- 文件狀態：已實作
- 現行系統版本：0.10.9
- 首次實作版本：0.9.100
- 最後核對日期：2026/11/11

## 一、目標與範圍

讓系統與業務模組能「通知某人某件事」，並讓管理員對全體或特定對象發布公告；通知保存在資料庫、可標為已讀，不再只有轉眼即逝的右下角提示。

- **範圍**：通知（資料表、發送服務 `INotificationSender`、即時訊號、鈴鐺、已讀、可選的同步寄信、自動清除）、公告（資料表、管理頁 `/announcements`、每頁上方的橫幅、個人關閉）、內建事件（0.9.100 起四種，0.9.109 加上 AI 用量、0.9.161 加上定時巡檢，共六種，見 §四 表格）。
- **非範圍**：管理員手動發通知的畫面（業務需要時由程式發；0.9.122 起「系統健康監控」有固定內容的「發送測試通知」給全體管理員，只用來確認鈴鐺正常）；通知的使用者偏好設定（訂閱／退訂）；公告寄信；公告的多選對象與富文字。

使用者決定（2026/10/04）：通知由程式發出、公告由管理員發布；內建事件四個；公告為每頁上方可關閉的橫幅；程式可指定同時寄信；公告對象為全體或單一角色／團隊。

## 二、使用者與入口

| 入口 | 位置 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| 通知鈴鐺 | 每個登入後頁面的右上角（`ClientMainLayout`） | 登入即可（只看得到自己的） | 所有使用者 |
| 公告橫幅 | 每個登入後頁面的內容上方（`ClientMainLayout`） | 登入即可（依對象） | 所有使用者 |
| `/announcements` | 系統管理 → 系統設定與維運 → 公告管理（`Menu.json` id=38）| 僅管理員（`CheckIsAdmin`；0.10.6 起 WASM 以 `Permissions.IsAdmin`；權限鍵 `角色_公告管理` 不進角色矩陣）| 系統管理員 |

## 三、畫面與欄位

- **鈴鐺**：未讀數徽章（超過 99 顯示 99+）；下拉列出最新 10 則（未讀以粗體標示）；點一則標為已讀，連結經 `ReturnUrlGuard` 確認是站內網址才導向；「全部標為已讀」。0.9.125 起每則右邊有 ×（刪除單則）、標題列有「清除已讀」（刪除自己所有已讀的通知，未讀的不動；0.9.136 起只要有任何已讀的通知就能按，`NotificationQueryService.HasReadAsync`，之前只看下拉顯示的 10 則）；都不跳確認、不寫稽核，只刪得到自己的。
- **公告橫幅**：顯示期間內、對象包含目前使用者、而且他沒有關閉過的公告；標題＋純文字內容（保留換行）；「知道了」只對自己關閉。
- **公告管理**：表格（標題與內容開頭、狀態未開始／進行中／已結束、期間、對象、建立者）；新增、修改（小型表單窗：標題、內容、開始與結束時間、對象全體／角色／團隊）、刪除（危險確認）。

**公告管理 WebAssembly（0.10.6 起，WASM 遷移 M7）**：頁面改在瀏覽器執行（`MyProject.Web.Client/Pages/AnnouncementPage.razor`、`Views/Admins/AnnouncementView.*`；Server 版已刪除），畫面與訊息和之前相同。資料經 `AnnouncementsController` 新增的只限管理員端點（`GET api/v1/announcements`〔清單、角色與團隊選項、伺服器的「現在」〕、`POST`、`PUT {id}`、`DELETE {id}?concurrencyStamp=`），走同一個 `AnnouncementService`，稽核 `Announcement.Create`／`Update`／`Delete` 由伺服器寫（刪除的標題取自資料庫）；原有的 `active`、`dismiss` 改為各自標 `[SignedInUser]`。公告對象種類 `AnnouncementTargetKinds` 搬到 `MyProject.Share`（命名空間仍是 `MyProject.AccessDatas.Models`）。
存檔或刪除後伺服器照舊 `AnnouncementCache.Invalidate()`＋`PublishAnnouncementsChanged()`（0.10.8 以前讓還在 Server 的頁面即時更新；0.10.7 起推播串流也訂閱這個訊號）；WASM 公告頁另以 `ClientAnnouncementSignal` 請**同一個分頁**的橫幅立即重讀，其他分頁與其他使用者照舊 60 秒輪詢與切回分頁時重讀。
WASM 外框的鈴鐺另外訂閱 `ClientNotificationSignal`：系統健康監控「發送測試通知」與首頁「我的通知」點開通知後立即重讀；反過來，在鈴鐺標為已讀、刪除、全部已讀或清除已讀後，鈴鐺以 `RequestRefresh(except: 自己)` 請首頁「我的通知」立即重讀（都只對同一個分頁）。

**通知推播串流（0.10.7 起，WASM 遷移 M8）**：WASM 外框啟動一條長連線（`Layout/ClientPushConnection`，`POST api/v1/notifications/stream`，`[SignedInUser]`），伺服器訂閱這位使用者的 `INotificationSignal` 與公告訊號（與 Server 版鈴鐺、橫幅同一個來源），有變動就送一行 `notifications` 或 `announcements`（不帶內容，短時間多次合併成一筆），外框收到後觸發 `ClientNotificationSignal`／`ClientAnnouncementSignal` 立即重讀——鈴鐺、公告橫幅、首頁「我的通知」在**其他分頁與其他使用者**那邊也通常幾秒內更新（M7 時只有同一個分頁立即、其他最慢 60 秒）。
- 串流：`ready` → 變動 → 閒置每 25 秒 `ping`；伺服器主動結束時最後一筆是 `renew`（存取 Token 到期前 1 分鐘，前端刷新後重連）或 `reconnect`（滿 30 分鐘、網站關閉）。產生端（`Streaming/NotificationPushStream.cs`）自己看 `RequestAborted` 結束並取消訂閱。
- 每位使用者最多 3 條推播連線（第 4 條起 429，那個分頁只靠輪詢）；推播連線不記慢請求。前端斷線以 2、4、8…最多 60 秒退避重連，重新接上時兩邊各重讀一次補上斷線期間的變動；401／403／404 不再重試。
- 開關 `NotificationSettings:PushEnabled`（預設 `true`）；`false` 時端點 404、`GET api/v1/shell/info` 的 `NotificationPushEnabled` 為 false，外框不連。**60 秒輪詢一直保留**，是推播關閉、超過連線上限、斷線或多台主機時的備援。
- 沒做跨分頁共用一條連線（BroadcastChannel）：複雜度高，而正式環境 IIS＋HTTPS 通常是 HTTP/2，不受 HTTP/1.1 每網域 6 條連線的限制（每人 3 條的上限保留另外 3 條給一般 API）。

## 四、內部系統運作

- **資料**（Migration `AddNotifications`）：
  - `Notification`：一位收件人一列（`RecipientUserId` Cascade、`Category`、`Title`、`Body`、`Link`、`SourceKey`、`CreatedAtUtc`、`ReadAtUtc`），索引 (收件人, 已讀)、(收件人, 建立時間)、(收件人, 去重鍵)、建立時間。
  - `Announcement`：標題、內容（純文字）、`StartAtUtc`、`EndAtUtc`（不含，可為空）、`TargetKind`（All／Role／Team）、`TargetId`（不設外鍵）、建立者、時間、`ConcurrencyStamp`（條件式 UPDATE 比對）。
  - `AnnouncementDismissal`：(公告, 使用者) 主鍵，兩邊 Cascade。
- **發送** `NotificationSender`（`INotificationSender.SendAsync(NotificationRequest)`）：
  1. 展開對象：`Users`、`Role`（`UserRole` ∪ 主要角色；已刪除的角色沒有收件人）、`Team`（`IEffectiveTeamResolver.GetUserIdsInTeamAsync`，與列級權控同一個定義；0.9.105 起含被指派到這個團隊**上層團隊**的人）、`AllAdmins`（啟用中的管理員），可 `Union`；最後只留啟用、未刪除的帳號。
  2. 去重：有 `SourceKey` 時，已收過同鍵的收件人不再發。0.9.136 起比對的是 `NotificationDispatch`（發送時一位收件人寫一列，和通知寫在同一次存檔），所以通知被手動刪除或已讀的被提早清除後，同一個鍵**不會**再發；去重紀錄跟著通知的建立保留天數（`RetentionDays`，預設 90 天）由「站內通知清理」清除。0.9.135 以前比對現存的通知列，刪掉之後會重發。
  3. 一位收件人寫一列 → `INotificationSignal.Publish(收件人)` →（`AlsoEmail`）交給 `INotificationMailer`。
  4. ⚠️ 絕不丟例外：失敗記錯誤並回 `Failed`，不中斷登入或排程作業。
- **寄信** `NotificationMailer`：寄信停用不寄；信箱格式不合（例如 support 預設的 `support`）略過；連結以 `EmailSettings:PublicBaseUrl` 組成完整網址；佇列滿時停止並只記一筆警告；日誌不記信箱。信件種類 `EmailKinds.Notification`。
- **即時更新**：singleton `NotificationSignal` 依使用者 Id（與「公告」）訂閱，只送「有變動」訊號，畫面自己重查；取消訂閱時原子移除空的外層集合（防洩漏）；派送時不帶發送端的執行情境；一個處理程序失敗不影響其他人。單一行程以外（IIS 重疊回收、多台主機）靠鈴鐺、橫幅與首頁「我的通知」（0.9.122 起）各自 60 秒輪詢補足。0.9.122 起標為已讀（真的改到資料時）也發這位使用者的訊號，鈴鐺、首頁小工具與他的其他分頁一起更新。
- **公告快取** `AnnouncementCache`（singleton）：所有連線共用「還沒結束的公告」（含尚未開始的），管理頁存檔時失效並廣播，否則 60 秒重讀；橫幅的角色 Id 與已關閉清單每個連線讀一次，換頁不查資料庫。
- **內建事件**：

| 事件 | 收件人 | 同時寄信 | 去重鍵 | 來源 |
|---|---|---|---|---|
| Google 第一次登入建立待開通帳號 | 管理員 | 否 | `AccountPending:{使用者 Id}` | `ExternalLoginService` |
| 排程作業失敗（不含中斷、略過；含系統備份） | 管理員＋手動觸發者 | 是 | — | `ScheduledJobRunner`（結束後以新 scope 發送） |
| 帳號被鎖定 | 管理員 | 是 | 每次鎖定（不設去重鍵） | `MyUserServiceLogin`（0.9.101 起） |
| 全系統短時間內登入失敗次數達門檻 | 管理員 | 是 | 不設去重鍵；`LoginFailureAlertSettings:CooldownMinutes`（預設 60）內不重發（記憶體） | `LoginFailureAlertService`（0.9.166 起，寫完登入失敗稽核後評估） |
| 密碼即將到期（7 天內） | 本人 | 否 | `PasswordExpiring:{使用者 Id}:{到期時間}` | 排程作業「密碼到期提醒」（`PasswordExpiryReminder`，`0 8 * * *`，0.9.101 起） |
| AI 用量達每人上限的 80%／100% | 本人 | 否 | `AiQuota:user:{使用者 Id}:{daily:日期\|monthly:年月}:{80\|100}` | `TokenUsageLogService` 記帳後（0.9.109 起，[AI 用量配額](AI用量配額-prd.md)） |
| AI 用量達全系統上限的 80%／100% | 管理員 | 100% 時是 | `AiQuota:global:{daily:日期\|monthly:年月}:{80\|100}` | 同上 |
| 定時巡檢有「注意」「異常」或無法判讀 | 管理員 | 是 | `AdminPatrol:{報告編號}`（每份報告一次） | 排程作業「系統管理助理巡檢」（`AdminPatrol`，`30 7 * * *`，0.9.161 起，[系統管理助理 PRD](系統管理助理-prd.md)）；內文是摘要（純文字、1000 字內），連結 `/admin-assistant?patrolReport={報告編號}` |

- **清除**：排程作業「站內通知清理」（`NotificationRetention`，`0 3 * * *`）刪除建立超過 `NotificationSettings:RetentionDays`（預設 90，系統參數）天的通知，分批進行，寫 `Notification.AutoPurge`。0.9.125 起同一個作業另外刪除**標為已讀**超過 `NotificationSettings:ReadRetentionDays`（預設 7，系統參數，從 `ReadAtUtc` 起算）天的通知；兩者各自 `0`＝不清除，稽核摘要分列 `rows`／`readRows`。

## 五、權限與安全

- 通知只看得到自己的；所有標為已讀的 UPDATE 與刪除的 DELETE（0.9.125 起）都帶 `RecipientUserId = 目前使用者`。
- 通知與公告內容一律純文字（不經 Markdown／HTML），連結只導向站內網址。
- ⚠️ 例外記錄管線內（例外告警服務、例外寫入器）不可發通知：發送失敗會記錯誤，形成遞迴。
- 稽核：`Announcement.Create`／`Update`／`Delete`、`Notification.AutoPurge`、`Notification.Test`（0.9.122 起）。

## 六、錯誤與邊界

- 發送失敗（資料庫無法寫入）→ 記錯誤、回 `Failed`，呼叫端流程照常。
- 寄信佇列滿 → 剩下的信略過並記一筆警告；站內通知照常。
- 公告對象的角色或團隊被刪除 → 公告不再對任何人顯示，管理頁標示「（已刪除）」。
- 兩位管理員同時修改同一則公告 → 第二位得到並行衝突訊息。
- 已開啟的畫面：同一行程內立即更新；其他行程最晚 60 秒。WASM 頁面（0.10.7 起）經通知推播通常幾秒內更新；推播關閉、斷線或超過每人 3 條連線時最晚 60 秒。
- 推播長連線只在開始時檢查一次身分：帳號停用、強制登出在 Token 到期前 1 分鐘或滿 30 分鐘重連時生效（期間外框的其他 API 照常被擋）。

## 七、驗收與測試

- `NotificationTests`：各種對象（停用與已刪除帳號、停用的管理員、主要角色、已刪除角色、團隊直接與經角色預設團隊）、⭐ 團隊反查與有效團隊互相對照、去重、只在要求時寄信、發送失敗不丟例外、標為已讀只動自己的、最新排序與清除、徽章上限、⭐ 訊號只送給收件人且取消訂閱不留殘餘、處理程序失敗互不影響且不繼承呼叫端情境、並行訂閱不洩漏、寄信規則（停用、格式不合、佇列滿、完整網址）、公告時間邊界（開始含、結束不含）、橫幅篩選、公告驗證與衝突與個人關閉、已結束公告不快取、已刪除角色不算、Google 待開通通知一次、刪除使用者連帶刪除、清除作業。
- `ScheduledJobFrameworkTests`：失敗通知管理員與觸發者並寄信、排程失敗只通知管理員、成功與中斷不通知。
- `Components/NotificationComponentTests`（bUnit，0.10.8 以前）：Server 版鈴鐺收到訊號不重新整理就更新、全部標為已讀；橫幅「知道了」立即隱藏且下一次連線不再出現。0.10.9 起隨 Server 版鈴鐺與橫幅刪除（WASM 版由實機情境涵蓋；徽章上限 `BadgeText` 改測 `ClientNotificationBell`，公告挑選規則改測 `AnnouncementCache.Select`）。
- 0.9.122 起：`NotificationTests.MarkRead_ShouldSignalTheUser_OnlyWhenSomethingChanged`（標為已讀才發訊號）、`DashboardTests` 小工具輪詢與 Dispose 停止輪詢、`NotificationTestServiceTests`（測試通知）。
- 0.9.136 起：`NotificationTests.SourceKey_ShouldStillPreventDuplicates_AfterTheNotificationIsDeleted`、`RetentionJob_ShouldPurgeDispatchRecordsByCreationRetentionOnly`；`NotificationComponentTests.Bell_ClearRead_ShouldBeClickable_WhenOnlyOlderNotificationsAreRead`。
- 0.9.125 起：`NotificationTests.Delete_ShouldOnlyAffectTheCurrentUsersNotifications_AndSignalOnlyWhenSomethingChanged`（刪除只動自己的、清除已讀不碰未讀、有刪到才發訊號）、`RetentionJob_ShouldPurgeReadOnesByReadTime_AndKeepUnreadOnes`；`NotificationComponentTests.Bell_DeleteOne_AndClearRead_ShouldOnlyRemoveThoseNotifications`。
- 0.10.6 起：`SystemParameterAnnouncementApiTests` 的公告部分（新增、修改、刪除與畫面相同且每個動作一筆稽核、不存在 404、Id 不符 400、輸入不合法不寫稽核、其他使用者的橫幅下一次輪詢就看得到、非管理員 403 但仍看得到橫幅）；`ShellApiIntegrationTests.NotificationsProfileAndShellInfo_ShouldSendLocalTimesWithoutAnOffset`（鈴鐺的時間不帶時差）、`Client/ClientRefreshSignalTests`（略過呼叫端、沒有訂閱者）；實機 `m07-ops.mjs` M7-P4／P5。
- 0.10.7 起：`NotificationPushApiTests`（沒有 Token 401、以 `ready` 開始、發通知／全部已讀／公告變動立即推送、別人的不推給我、斷線釋放訂閱與名額、Token 到期前 `renew`、最長時間 `reconnect`、閒置心跳、每人連線數上限、網站關閉、推播關閉時 404）、`Client/ClientPushConnectionTests`（觸發對應訊號、退避重連、`renew` 先刷新、重連後補讀、401／403／404 不再重試）、`NdjsonHeartbeatTests`；實機 `m08-agents.mjs` M8-G8（另一個瀏覽器的鈴鐺 2 秒內更新）、`m08-agents.mjs nopush` M8-N1（關閉推播仍會在輪詢時更新）。
- 守門同步：`ScheduledJobWiringTests`、`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`SystemParameterTests`、`DataAccessServiceLifetimeTests`、`LoggingConventionTests`、`PageHelpCatalogTests`。

## 八、相關程式與文件

- `src/MyProject/MyProject.Business/Services/Other/NotificationSender.cs`（`INotificationSender`、`NotificationTarget`、`NotificationRequest`）、`NotificationSignal.cs`、`NotificationCategories.cs`
- `src/MyProject/MyProject.Business/Services/DataAccess/NotificationQueryService.cs`、`AnnouncementService.cs`；`IEffectiveTeamResolver.GetUserIdsInTeamAsync`
- `src/MyProject/MyProject.Web/Email/NotificationMailer.cs`、`Components/Layout/AnnouncementCache`（0.10.9 起含公告挑選規則 `Select`）、`Scheduling/Jobs/NotificationRetentionJob.cs`；鈴鐺與公告橫幅在 `MyProject.Web.Client/Layout/ClientNotificationBell`、`ClientAnnouncementBanner`（0.10.9 起 Server 版 `NotificationBell`、`AnnouncementBanner` 已刪除）；0.10.6 起公告管理在 `MyProject.Web.Client/Views/Admins/AnnouncementView`、`Api/AnnouncementApi.cs`、`Layout/ClientNotificationSignal.cs`（`ClientRefreshSignal`）、`MyProject.Web/Controllers/AnnouncementsController.cs`；0.10.7 起通知推播在 `MyProject.Web/Controllers/NotificationsController.cs`（`Stream`）、`Streaming/NotificationPushStream.cs`（`NotificationPushOptions`、`NotificationPushConnections`）、`Streaming/NdjsonHeartbeat.cs`、`MyProject.Web.Client/Layout/ClientPushConnection.cs`、`MyProject.Dtos/Shell/PushDtos.cs`
- 規則：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)「站內通知」；操作說明 `announcements.md`、`app.md`
