# superpowers/specs — 設計規格

- 文件版本：1.3
- 文件狀態：維護中
- 現行系統版本：0.9.59
- 首次實作版本：0.4.23
- 最後核對日期：2026/09/24

以 brainstorming 流程產出的設計規格；每份對應一次功能實作，實作結果見 [changelog/](../../changelog/README.md)。

> 📌 以下皆為**歷史設計規格**，保留當時的決策脈絡；系統現況請以 [`docs/prd/`](../../prd/README.md) 為準。

| 文件 | 說明 |
|------|------|
| [分類清單 / 團隊清單管理頁面（階段一）](2026-06-22-category-team-pages-design.md) | Category／Team 管理頁面、Web API、權限與SQLite migration 設計 |
| [紀錄分類/團隊標籤與團隊權控（階段二）](2026-06-22-record-tags-team-access-design.md) | 紀錄分類/團隊標籤與團隊權控設計 |
| [系統例外紀錄（ExceptionLog）](2026-09-16-system-exception-log-design.md) | 例外自動記錄管線（ILoggerProvider＋佇列＋背景寫入器）、相同例外合併累加、堆疊存檔案系統、管理員專屬頁面設計 |
| [寄信服務與忘記密碼](2026-09-24-email-password-reset-design.md) | MailKit 寄信（None／Pickup／Smtp）、背景佇列防時間差列舉、健康監控項與測試寄信；忘記密碼的 token 表、防列舉、單次使用與共用登入外觀（兩階段：0.9.59／0.9.60）|

> 返回 [文件總索引](../../README.md)
