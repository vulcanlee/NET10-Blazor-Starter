# API Versioning 策略

## 目標
- [x] 先建立版本化策略文件，但不在本輪改變既有 `/api/...` 路由，避免破壞目前 API 用戶端。

## 現況
- [x] 目前 Controller 路由維持 `[Route("api/[controller]")]`。
- [x] 本輪 integration tests 以既有 `/api/...` contract 驗證登入、refresh、me、Project CRUD 與 validation。
- [ ] 尚未導入 `/api/v1/...` 或 ASP.NET API Versioning 套件。

## 後續導入策略
- [ ] 第一階段先保留 `/api/...`，新增 `/api/v1/...` 平行路由，不立即移除舊路由。
- [ ] Swagger 分組顯示 `v1`，讓新用戶端從版本化路由開始串接。
- [ ] 文件標示 `/api/...` 為相容路由，設定 sunset 日期後再移除。
- [ ] 所有 versioned endpoint 仍固定回傳 `ApiResult<T>`，並維持 DTO 邊界。
- [ ] integration tests 同時覆蓋相容路由與版本化路由，避免遷移期間 regression。

## 驗收標準
- [ ] `/api/...` 舊路由在公告期內不破壞。
- [ ] `/api/v1/...` 與 Swagger 分組可正常運作。
- [ ] CI 測試覆蓋兩套路由的主要 API contract。

## 備註風險
- [x] 本輪不實作版本化路由是刻意決策；直接切換會破壞現有 API 用戶端。