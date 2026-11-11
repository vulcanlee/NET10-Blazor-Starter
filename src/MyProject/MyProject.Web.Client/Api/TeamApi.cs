using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;

namespace MyProject.Web.Client.Api;

/// <summary>團隊的 API 用戶端（0.10.2 起，WASM 版團隊頁）：固定端點見 <see cref="CrudApi{TDto, TSearch}"/>，另有部門樹與可選的上層。</summary>
public sealed class TeamApi : CrudApi<TeamDto, TeamSearchRequestDto>
{
    public TeamApi(ApiClient api)
        : base(api, "Team")
    {
    }

    /// <summary>所有未刪除的團隊（依名稱），畫面上組成部門樹。</summary>
    public Task<ApiResult<List<TeamDto>>> GetAllAsync() => Api.GetAsync<List<TeamDto>>($"{BaseUrl}/all");

    /// <summary>可以當作上層的團隊（排除自己與下屬；新增時傳 0）。</summary>
    public Task<ApiResult<List<TeamNodeDto>>> GetParentCandidatesAsync(int teamId) => Api.GetAsync<List<TeamNodeDto>>($"{BaseUrl}/{teamId}/parent-candidates");
}
