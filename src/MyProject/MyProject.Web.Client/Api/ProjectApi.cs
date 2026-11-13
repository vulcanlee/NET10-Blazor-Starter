using System.Text.Json;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Models.Systems;
using MyProject.Web.Client.Commons;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 專案的 API 用戶端（0.10.3 起，WASM 版專案頁）：固定端點見 <see cref="CrudApi{TDto, TSearch}"/>，
/// 另有表單選項、帶附件的存檔（multipart，檔案由瀏覽器直接送出）與附件下載（一次性票證）。
/// </summary>
public sealed class ProjectApi : CrudApi<ProjectDto, ProjectSearchRequestDto>
{
    private readonly BrowserFiles files;

    public ProjectApi(ApiClient api, BrowserFiles files)
        : base(api, "Project")
    {
        this.files = files;
    }

    /// <summary>工具列過濾與表單的選項、預設團隊、附件限制。</summary>
    public Task<ApiResult<ProjectFormOptionsDto>> OptionsAsync() => Api.GetAsync<ProjectFormOptionsDto>($"{BaseUrl}/options");

    /// <summary>
    /// 新增或修改，連同新附件與要移除的附件一次送出（與 Server 版同一條路：伺服器在同一次存檔裡處理資料與附件）。
    /// <paramref name="fileIds"/> 是 <see cref="BrowserFiles.CaptureAsync"/> 回傳的 id。
    /// </summary>
    public async Task<VerifyRecordResult> SaveAsync(ProjectCreateUpdateDto dto, bool isNew, IEnumerable<string> fileIds, IEnumerable<int> removedFileIds)
    {
        var url = isNew ? BaseUrl : $"{BaseUrl}/{dto.Id}";
        var method = isNew ? "POST" : "PUT";
        var data = JsonSerializer.Serialize(dto, JsonSerializerOptions.Web);
        var ids = fileIds.ToList();
        var removed = removedFileIds.ToList();
        return (await Api.SendFormAsync<object>(url, token => files.SendAsync(method, url, token, data, ids, removed))).ToVerify();
    }

    /// <summary>下載附件：先申請一次性票證，再讓瀏覽器原生下載（大檔不經過 WASM 記憶體）。</summary>
    public async Task<VerifyRecordResult> DownloadFileAsync(int fileId)
    {
        var ticket = await Api.PostAsync<DownloadTicketDto>($"{BaseUrl}/files/{fileId}/ticket");
        if (!ticket.Success || ticket.Data is null)
        {
            return ticket.ToVerify();
        }

        // 0.10.10 起：跨來源下載失敗時（票證失效、權限已失去）回伺服器的訊息，頁面留在原地。
        var error = await files.DownloadAsync(ticket.Data.Url);
        return error is null ? new VerifyRecordResult { Success = true } : new VerifyRecordResult { Success = false, Message = error };
    }
}
