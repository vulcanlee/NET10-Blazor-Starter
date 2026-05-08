# Web API 設計慣例

## 文件目的

統一規範本專案 Web API（位於 `MyProject.Web/Controllers/`）的命名、回應信封、分頁查詢與例外處理慣例。新增 API 時請完整遵循。

---

## 1. Controller 結構

- 路由統一為 `[Route("api/[controller]")]`。
- 一律加上 `[ApiController]`，並套用 `[ApiValidationFilter]`（位於 `MyProject.Web/Filters/`）以接管模型繫結錯誤的格式。
- 建構式注入三件套：`ILogger<TController>`、Repository、`IMapper`。
- 內部不直接相依 `BackendDBContext`，一律透過 `Repository`。

範例：[`src/MyProject/MyProject.Web/Controllers/MyTaskController.cs`](../src/MyProject/MyProject.Web/Controllers/MyTaskController.cs)

```csharp
[Route("api/[controller]")]
[ApiController]
[ApiValidationFilter]
public class MyTaskController : ControllerBase
{
    private readonly ILogger<MyTaskController> logger;
    private readonly MyTaskRepository myTaskRepository;
    private readonly IMapper mapper;
    // ...
}
```

---

## 2. 回應信封 `ApiResult<T>`

所有 API 回應都包進 `MyProject.Dtos.Commons.ApiResult<T>`，提供統一欄位：

- `Success`：作業是否成功。
- `StatusCode`：HTTP 狀態碼。
- `Message`：給使用者的簡述訊息。
- `Data`：回應主體（型別 `T`）。
- `ErrorMessage`：錯誤詳情（僅錯誤時填入）。

提供工廠方法：

| 方法 | 用途 |
|------|------|
| `ApiResult<T>.SuccessResult(data, message)` | 200 成功 |
| `ApiResult<T>.NotFoundResult(message)` | 404 找不到 |
| `ApiResult<T>.ServerErrorResult(message, errorMessage)` | 500 伺服器錯誤 |

實際回傳時搭配 ASP.NET 的 `Ok(...)`、`NotFound(...)`、`StatusCode(500, ...)`。

---

## 3. 分頁查詢 `PagedResult<T>`

分頁回應另外包一層 `MyProject.Dtos.Commons.PagedResult<T>`：

- `Items`：當頁項目集合。
- `TotalCount`：符合條件的總筆數。
- `PageIndex` / `PageSize` / `TotalPages`。

外層仍包 `ApiResult<PagedResult<T>>`。

---

## 4. Search DTO 模式

凡是「列表 / 搜尋」端點皆使用 `[HttpPost("search")]` + `XxxSearchRequestDto`：

- 接收 `[FromBody]`，將條件、排序、分頁、是否載入關聯資料寫成單一 DTO。
- 常見欄位：`Keyword`、`PageIndex`、`PageSize`、`SortBy`、`SortDescending`、`IncludeRelatedData`、其他模組特定欄位（例如 `Category`、`Status`、`Priority`、`ProjectId`）。
- Repository 接收同一份 DTO 進行查詢、排序、分頁。

範例（節錄自 `MyTaskController.Search`）：

```csharp
[HttpPost("search")]
public async Task<ActionResult<ApiResult<PagedResult<MyTaskDto>>>> Search(
    [FromBody] MyTaskSearchRequestDto request)
{
    var pagedResult = await myTaskRepository.GetPagedAsync(request, request.IncludeRelatedData);
    var taskDtos = mapper.Map<List<MyTaskDto>>(pagedResult.Items);
    var result = new PagedResult<MyTaskDto>
    {
        Items = taskDtos,
        TotalCount = pagedResult.TotalCount,
        PageIndex = request.PageIndex,
        PageSize = request.PageSize,
        TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
    };
    return Ok(ApiResult<PagedResult<MyTaskDto>>.SuccessResult(result, "搜尋任務成功"));
}
```

---

## 5. 端點命名與動詞約定

| HTTP 動詞 | 路由樣板 | 用途 |
|-----------|----------|------|
| `GET` | `api/{controller}/{id}` | 取得單筆 |
| `POST` | `api/{controller}/search` | 條件搜尋（含分頁、排序） |
| `POST` | `api/{controller}` | 新增 |
| `PUT` | `api/{controller}/{id}` | 更新 |
| `DELETE` | `api/{controller}/{id}` | 刪除 |

額外操作（例如附件上傳）以子路徑命名，例如 `POST api/project/{id}/files`。

---

## 6. 日誌規範

每個端點至少寫三類紀錄：

| 等級 | 時機 | 範例 |
|------|------|------|
| `Debug` | 收到請求、列出主要參數 | `logger.LogDebug("Received task get request. TaskId={TaskId}", id);` |
| `Information` | 處理成功 | `logger.LogInformation("Task retrieved successfully. TaskId={TaskId}", id);` |
| `Warning` | 找不到資源 / 業務驗證失敗 | `logger.LogWarning("Task get request could not find record. TaskId={TaskId}", id);` |
| `Error` | 例外 | `logger.LogError(ex, "Failed to get task. TaskId={TaskId}", id);` |

訊息一律採英文模板 + 結構化參數，方便日後檢索。詳見 [日誌與設定檔說明](日誌與設定檔說明.md)。

---

## 7. Swagger UI

- `Program.cs:65-66` 註冊 `AddEndpointsApiExplorer()` 與 `AddSwaggerGen()`。
- 在 `Development` 環境啟用 `app.UseSwagger()` 與 `app.UseSwaggerUI()`（`Program.cs:269-275`）。
- 啟動後可瀏覽 `/swagger` 取得 OpenAPI 文件。

---

## 8. ApiValidationFilter

位於 `MyProject.Web/Filters/`，用途是統一處理 `ModelState` 無效時的回應，使其符合 `ApiResult<T>` 信封格式（不採用 `[ApiController]` 預設的 ProblemDetails）。

新增 Controller 時請務必加上 `[ApiValidationFilter]` 屬性。

---

## 9. 延伸閱讀

- [架構總覽](架構總覽.md)
- [資料模型與資料庫](資料模型與資料庫.md)
- [日誌與設定檔說明](日誌與設定檔說明.md)
- [建立一個新 CRUD 操作網頁說明](建立一個新%20CRUD%20操作網頁說明.md)
