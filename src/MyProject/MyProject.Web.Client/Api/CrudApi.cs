using MyProject.Dtos.Commons;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 資料維護頁共用的 API 用戶端（0.10.2 起，WASM 遷移 M3；CRUD 產生器產生的模組也用它）：對應伺服器 CRUD Controller 的固定端點
/// （search、單筆、check、新增、修改、enabled、刪除、restore、purge、export）。
/// 寫入一律回 <see cref="VerifyRecordResult"/>：與 Server 版檢視呼叫服務的寫法相同，訊息就是伺服器給的那一句。
/// </summary>
public class CrudApi<TDto, TSearch>
    where TDto : class
{
    protected CrudApi(ApiClient api, string resource)
    {
        Api = api;
        BaseUrl = $"api/v1/{resource}";
    }

    protected ApiClient Api { get; }

    protected string BaseUrl { get; }

    /// <summary>清單；失敗時 <c>Success</c> 為 false、訊息在 <see cref="ApiResultMessages.UserMessage{T}"/>。</summary>
    public Task<ApiResult<PagedResult<TDto>>> SearchAsync(TSearch request) => Api.PostAsync<PagedResult<TDto>>($"{BaseUrl}/search", request);

    /// <summary>單筆（伺服器寫一筆「查看」稽核）。</summary>
    public Task<ApiResult<TDto>> GetAsync(int id) => Api.GetAsync<TDto>($"{BaseUrl}/{id}");

    /// <summary>存檔前的名稱檢查；<paramref name="dto"/> 的 Id 有值時是修改。</summary>
    public async Task<VerifyRecordResult> CheckAsync(object dto) => (await Api.PostAsync<object>($"{BaseUrl}/check", dto)).ToVerify();

    public async Task<VerifyRecordResult> CreateAsync(object dto) => (await Api.PostAsync<TDto>(BaseUrl, dto)).ToVerify();

    public async Task<VerifyRecordResult> UpdateAsync(int id, object dto) => (await Api.PutAsync<object>($"{BaseUrl}/{id}", dto)).ToVerify();

    public async Task<VerifyRecordResult> SetEnabledAsync(int id, bool enabled)
        => (await Api.PutAsync<object>($"{BaseUrl}/{id}/enabled", new SetEnabledRequestDto { Enabled = enabled })).ToVerify();

    public async Task<VerifyRecordResult> DeleteAsync(int id) => (await Api.DeleteAsync<object>($"{BaseUrl}/{id}")).ToVerify();

    public async Task<VerifyRecordResult> RestoreAsync(int id) => (await Api.PostAsync<object>($"{BaseUrl}/{id}/restore")).ToVerify();

    public async Task<VerifyRecordResult> PurgeAsync(int id) => (await Api.DeleteAsync<object>($"{BaseUrl}/{id}/purge")).ToVerify();

    /// <summary>匯出 Excel（條件與清單相同）；超過上限時 <c>StatusCode</c> 為 400、訊息說明原因。</summary>
    public Task<ApiDownload> ExportAsync(TSearch request) => Api.DownloadAsync($"{BaseUrl}/export", request);
}

/// <summary>API 回應給使用者看的訊息與轉換（0.10.2 起）。</summary>
public static class ApiResultMessages
{
    /// <summary>給使用者看的那一句：失敗時伺服器把原因放在 <c>ErrorMessage</c>（<c>Message</c> 是「資料衝突」之類的分類）。</summary>
    public static string UserMessage<T>(this ApiResult<T> result)
        => !result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage) ? result.ErrorMessage : result.Message;

    /// <summary>寫入的結果轉成與 Server 版服務相同的形狀。</summary>
    public static VerifyRecordResult ToVerify<T>(this ApiResult<T> result) => new()
    {
        Success = result.Success,
        Message = result.Success ? string.Empty : result.UserMessage(),
    };
}
