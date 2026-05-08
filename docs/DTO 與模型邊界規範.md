# DTO 與模型邊界規範

## 目標
- [x] 明確定義 API、UI、Business、Entity 的資料邊界，避免腳手架日後擴充時直接暴露資料庫 Entity。

## 邊界原則
- [x] Web API request/response 只使用 `MyProject.Dtos`，不可直接接收或回傳 `AccessDatas.Models` Entity。
- [x] Entity 僅作為 EF Core persistence model 使用。
- [x] AdapterModel 目前主要供 Blazor UI/後台頁面使用，不應與 API DTO 混用。
- [x] Controller 使用 AutoMapper 或明確 mapping 在 DTO 與 Entity 之間轉換。

## 新 CRUD 模組待辦
- [ ] 新增 `XxxDto` 作為 response DTO。
- [ ] 新增 `XxxCreateUpdateDto` 作為 create/update request DTO。
- [ ] 新增 `XxxSearchRequestDto` 作為查詢 request DTO。
- [ ] 新增 mapping profile，並補 DTO 欄位驗證 attribute。
- [ ] 新增 integration tests，確認 JSON body 不含不該暴露的 Entity 導覽屬性或內部欄位。

## 驗收標準
- [x] `ProjectController`、`MyTaskController`、`MeetingController` 目前均使用 DTO 作為 API 邊界。
- [x] integration tests 已驗證 Project CRUD 成功 response 可用 DTO contract 反序列化。
- [ ] 日後每新增一個 API Controller，都要在 PR checklist 檢查 DTO 邊界。

## 備註風險
- [ ] 目前 UI AdapterModel 與 API DTO 並存，命名與責任邊界需在 code review 中持續守住。