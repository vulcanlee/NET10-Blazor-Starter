# 現有架構盤點 TODO

## 目標說明

- [ ] 盤點目前方案內每個專案與主要資料流，作為後續重構、補強與新增 API 的依據。
- [ ] 找出目前架構中適合保留為腳手架慣例的設計。
- [ ] 找出目前架構中需要抽象化、拆分或文件化的區塊。

## 現況盤點

- [ ] `MyProject.Web` 負責 Blazor UI、Layout、登入頁、Controller、Swagger、DI 註冊與 middleware pipeline。
- [ ] `MyProject.Business` 負責 CRUD service、登入驗證、角色權限、AutoMapper profile、密碼與 tracking helper。
- [ ] `MyProject.Models` 負責 AdapterModel、系統設定模型、資料請求模型、回傳結果模型與目前使用者模型。
- [ ] `MyProject.AccessDatas` 負責 EF Core DbContext、Entity、migration 與資料庫關聯設定。
- [ ] `MyProject.Share` 負責共用 helper、magic string、擴充方法。
- [ ] `Program.cs` 目前同時負責 NLog、DI、Options、目錄建立、EF Core、seed data、middleware 與 endpoint mapping。
- [ ] `RolePermissionService` 目前以程式碼硬編碼角色權限結構，並與 `Menu.json` 的階層順序相依。
- [ ] 檔案上傳邏輯目前分散在 Project、MyTas、Meeting service，各自處理大小限制、實體檔案、資料庫紀錄與下載。

## 實作待辦

- [ ] 將 `Program.cs` 拆成 extension methods，例如 `AddAppLogging`、`AddAppAuthentication`、`AddAppPersistence`、`AddAppServices`、`UseAppPipeline`。
- [ ] 將資料庫 migration 與 seed data 拆成獨立啟動服務或 extension，降低 `Program.cs` 複雜度。
- [ ] 將 `SystemSettings` 加上預設值、nullable 修正與 Options validation。
- [ ] 將 `RolePermissionService` 的權限定義方式文件化，後續評估改由設定檔、資料庫或 Menu.json 衍生。
- [ ] 將 Project/MyTas/Meeting 檔案上傳重複邏輯抽成通用 `IFileStorageService` 或 `AttachmentService`。
- [ ] 將 `VerifyRecordResult` 與未來 `ApiResult<T>` 的責任分清楚：前者用於 Business 操作，後者用於 Web API 回應。
- [ ] 補上資料流文件：Blazor 元件呼叫 Business Service、Business Service 使用 DbContext、Controller 呼叫 Service 並回傳 API 結果。

## 驗收標準

- [ ] 文件可讓開發者理解每個專案的責任，不需要先閱讀全部原始碼。
- [ ] 文件列出的架構問題可直接轉成後續 issue 或實作工作。
- [ ] 文件能指出新增一個 CRUD 功能時需要碰到的層級與檔案類型。
- [ ] 文件能指出新增一個 API 功能時應放在哪個 Controller、DTO、Service 與測試專案。

## 相關檔案

- [ ] `src/MyProject/MyProject.Web/Program.cs`
- [ ] `src/MyProject/MyProject.Web/Components/`
- [ ] `src/MyProject/MyProject.Web/Controllers/`
- [ ] `src/MyProject/MyProject.Business/Services/`
- [ ] `src/MyProject/MyProject.Models/`
- [ ] `src/MyProject/MyProject.AccessDatas/BackendDBContext.cs`
- [ ] `src/MyProject/MyProject.Share/Helpers/MagicObjectHelper.cs`

## 備註風險

- [ ] `Program.cs` 若持續膨脹，日後新增 JWT、Rate Limiting、Health Check、CORS 時會更難維護。
- [ ] 權限與 Menu 階層若只靠陣列順序對應，未來調整選單順序可能造成權限錯置。
- [ ] 檔案處理邏輯若不抽象化，後續切換本機磁碟、NAS、S3、Azure Blob 時會牽動多個 service。

