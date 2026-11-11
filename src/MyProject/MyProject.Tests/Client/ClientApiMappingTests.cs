using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Models.AdapterModel;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;

namespace MyProject.Tests.Client;

/// <summary>
/// WASM 頁面與 API 之間的資料轉換（0.10.2 起，WASM 遷移 M3）：分類、團隊的 DTO 與畫面資料互轉（伺服器的 Controller 也用同一份）、
/// API 錯誤訊息、異動歷程、/me 轉成畫面上的權限判斷。
/// </summary>
public sealed class ClientApiMappingTests
{
    [Fact]
    public void Category_RoundTrip_ShouldKeepEveryField_AndConvertTeamsBetweenListAndStoredText()
    {
        var model = new CategoryAdapterModel
        {
            Id = 7,
            Name = "硬體",
            Description = "電腦",
            Teams = ["研發部", "業務部"],
            IsEnabled = false,
            ConcurrencyStamp = "abc",
            CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5),
            UpdatedAt = new DateTime(2026, 2, 3, 4, 5, 6),
            IsDeleted = true,
            DeletedAt = new DateTime(2026, 3, 4, 5, 6, 7),
            DeletedBy = "alice",
        };

        var dto = CategoryDtoMapper.ToDto(model);
        var back = CategoryDtoMapper.ToModel(dto);

        Assert.Equal("\n研發部\n業務部\n", dto.Teams);
        Assert.Equivalent(model, back, strict: true);
    }

    [Fact]
    public void Category_FromACreateDto_ShouldDefaultTheIdAndStamp()
    {
        var model = CategoryDtoMapper.ToModel(new CategoryCreateUpdateDto { Name = "新", Teams = null });

        Assert.Equal(0, model.Id);
        Assert.Equal(string.Empty, model.ConcurrencyStamp);
        Assert.Empty(model.Teams);
        Assert.False(model.IsDeleted);
    }

    [Fact]
    public void Team_RoundTrip_ShouldKeepEveryField()
    {
        var model = new TeamAdapterModel
        {
            Id = 3,
            Name = "研發一課",
            Code = "RD1",
            Description = "x",
            ParentId = 1,
            IsEnabled = true,
            ConcurrencyStamp = "s",
            CreatedAt = new DateTime(2026, 1, 1),
            UpdatedAt = new DateTime(2026, 1, 2),
            IsDeleted = true,
            DeletedAt = new DateTime(2026, 1, 3),
            DeletedBy = "bob",
        };

        Assert.Equivalent(model, TeamDtoMapper.ToModel(TeamDtoMapper.ToDto(model)), strict: true);
    }

    [Fact]
    public void ApiFailure_ShouldShowTheReason_NotTheCategory()
    {
        var conflict = ApiResult<object>.ConflictResult("分類名稱已存在，無法新增。");

        Assert.Equal("分類名稱已存在，無法新增。", conflict.UserMessage());
        var verify = conflict.ToVerify();
        Assert.False(verify.Success);
        Assert.Equal("分類名稱已存在，無法新增。", verify.Message);
        Assert.True(ApiResult<object>.SuccessResult(new { }, "ok").ToVerify().Success);
    }

    [Fact]
    public void AuditHistoryItem_ShouldOnlyTellWhetherThereIsContent_NotTheServerPath()
    {
        var withContent = ApiAuditHistorySource.ToModel(new AuditHistoryItemDto { Id = 1, Action = "Category.Update", HasContent = true });
        var without = ApiAuditHistorySource.ToModel(new AuditHistoryItemDto { Id = 2, Action = "Category.Create", HasContent = false });

        Assert.NotNull(withContent.ContentFile);
        Assert.Null(without.ContentFile);
        Assert.Equal("Category", withContent.ActionCategory);
    }

    [Theory]
    [InlineData(new string[0], false, "分類清單", "create", false)]
    [InlineData(new[] { "分類清單:view" }, false, "分類清單", "view", true)]
    [InlineData(new[] { "分類清單:view" }, false, "分類清單", "edit", false)]
    [InlineData(new[] { "分類清單" }, false, "分類清單", "delete", true)]
    [InlineData(new string[0], true, "分類清單", "delete", true)]
    public void PagePermissions_FromTheSession_ShouldFollowTheServerRules(string[] keys, bool isAdmin, string page, string action, bool expected)
    {
        var user = ClientPagePermissions.From(new MyProject.Dtos.Auths.SessionInfoDto { Id = 5, Account = "u", IsAdmin = isAdmin, Permissions = [.. keys] });

        Assert.Equal(expected, MyProject.Business.Helpers.PermissionRules.CanAccessAction(user, page, action));
        Assert.Equal(isAdmin || keys.Length > 0, MyProject.Business.Helpers.PermissionRules.CanAccessPage(user, page));
        Assert.False(ClientPagePermissions.From(null).IsAuthenticated);
    }
}
