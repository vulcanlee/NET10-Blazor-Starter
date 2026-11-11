using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>公告管理頁的 API 用戶端（0.10.6 起；只限管理員）。外框橫幅用的 <c>active</c>／<c>dismiss</c> 在 <c>ClientAnnouncementBanner</c>。</summary>
public sealed class AnnouncementApi
{
    private const string BaseUrl = "api/v1/announcements";
    private readonly ApiClient api;

    public AnnouncementApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<AnnouncementListDto>> GetAllAsync() => api.GetAsync<AnnouncementListDto>(BaseUrl);

    /// <summary>新增；成功時把新的 Id 寫回 <paramref name="model"/>。</summary>
    public async Task<VerifyRecordResult> AddAsync(AnnouncementAdapterModel model)
    {
        var result = await api.PostAsync<int>(BaseUrl, AnnouncementDtoMapper.ToDto(model));
        if (result.Success)
        {
            model.Id = result.Data;
        }

        return result.ToVerify();
    }

    public async Task<VerifyRecordResult> UpdateAsync(AnnouncementAdapterModel model)
        => (await api.PutAsync<object>($"{BaseUrl}/{model.Id}", AnnouncementDtoMapper.ToDto(model))).ToVerify();

    public async Task<VerifyRecordResult> DeleteAsync(int id, string concurrencyStamp)
        => (await api.DeleteAsync<object>($"{BaseUrl}/{id}?concurrencyStamp={Uri.EscapeDataString(concurrencyStamp)}")).ToVerify();
}
