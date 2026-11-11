using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;

namespace MyProject.Web.Client.Api;

/// <summary>分類的 API 用戶端（0.10.2 起，WASM 版分類頁）：固定端點見 <see cref="CrudApi{TDto, TSearch}"/>，另有表單選項。</summary>
public sealed class CategoryApi : CrudApi<CategoryDto, CategorySearchRequestDto>
{
    public CategoryApi(ApiClient api)
        : base(api, "Category")
    {
    }

    /// <summary>表單的「適用團隊」選項與自己的團隊範圍。</summary>
    public Task<ApiResult<CategoryFormOptionsDto>> OptionsAsync() => Api.GetAsync<CategoryFormOptionsDto>($"{BaseUrl}/options");
}
