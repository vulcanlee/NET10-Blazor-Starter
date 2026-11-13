using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Web.Client.Commons;

namespace MyProject.Web.Client.Api;

/// <summary>系統備份頁的 API 用戶端（0.10.6 起，WASM 遷移 M7；只限管理員）。</summary>
public sealed class BackupApi
{
    private const string BaseUrl = "api/v1/backups";
    private readonly ApiClient api;
    private readonly BrowserFiles files;

    public BackupApi(ApiClient api, BrowserFiles files)
    {
        this.api = api;
        this.files = files;
    }

    public Task<ApiResult<BackupListDto>> ListAsync() => api.GetAsync<BackupListDto>(BaseUrl);

    /// <summary>立即備份：伺服器放進排程的佇列後就回應（備份在背景進行）。</summary>
    public Task<ApiResult<object>> RunAsync() => api.PostAsync<object>($"{BaseUrl}/run");

    public async Task<VerifyRecordResult> DeleteAsync(string fileName)
        => (await api.DeleteAsync<object>($"{BaseUrl}/{Uri.EscapeDataString(fileName)}")).ToVerify();

    /// <summary>下載：先申請一次性票證，再讓瀏覽器以 <c>&lt;a download&gt;</c> 原生下載（備份檔很大，不經過 WASM 記憶體）。</summary>
    public async Task<VerifyRecordResult> DownloadAsync(string fileName)
    {
        var ticket = await api.PostAsync<DownloadTicketDto>($"{BaseUrl}/{Uri.EscapeDataString(fileName)}/ticket");
        if (!ticket.Success || ticket.Data is null)
        {
            return ticket.ToVerify();
        }

        // 0.10.10 起：跨來源下載失敗時（票證失效、權限已失去）回伺服器的訊息，頁面留在原地。
        var error = await files.DownloadAsync(ticket.Data.Url);
        return error is null ? new VerifyRecordResult { Success = true } : new VerifyRecordResult { Success = false, Message = error };
    }
}

/// <summary>資料保留與清理頁的 API 用戶端（0.10.6 起，WASM 遷移 M7；只限管理員）。</summary>
public sealed class DataRetentionApi
{
    private const string BaseUrl = "api/v1/data-retention";
    private readonly ApiClient api;

    public DataRetentionApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<DataRetentionOverviewDto>> OverviewAsync() => api.GetAsync<DataRetentionOverviewDto>(BaseUrl);

    /// <summary>立即清理：伺服器放進排程的佇列後就回應（清理在背景進行）。</summary>
    public Task<ApiResult<object>> PurgeNowAsync(string rowKey) => api.PostAsync<object>($"{BaseUrl}/{Uri.EscapeDataString(rowKey)}/purge-now");

    public Task<ApiResult<int>> PreviewAsync(string rowKey, CustomPurgeRequestDto request)
        => api.PostAsync<int>($"{BaseUrl}/{Uri.EscapeDataString(rowKey)}/preview", request);

    /// <summary>自訂清除：資料多時要刪一陣子，不受一般請求的逾時限制。</summary>
    public Task<ApiResult<CustomPurgeResultDto>> CustomPurgeAsync(string rowKey, CustomPurgeRequestDto request)
        => api.PostLongRunningAsync<CustomPurgeResultDto>($"{BaseUrl}/{Uri.EscapeDataString(rowKey)}/custom-purge", request);
}

/// <summary>資料庫用量頁的 API 用戶端（0.10.6 起，WASM 遷移 M7；只限管理員）。</summary>
public sealed class DatabaseUsageApi
{
    private readonly ApiClient api;

    public DatabaseUsageApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<DatabaseUsageDto>> GetAsync() => api.GetAsync<DatabaseUsageDto>("api/v1/database-usage");
}
