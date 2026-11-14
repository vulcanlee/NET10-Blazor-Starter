using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>
/// 定時巡檢的權限窄門（0.9.161 起）：排程執行時沒有登入的使用者，只放行巡檢用的唯讀工具；
/// 會異動資料的工具就算名稱被塞進允許清單也一律拒絕。稽核的操作者留空，註明「排程巡檢」。
/// </summary>
public sealed class ScheduledPatrolToolGuardTests
{
    private static readonly IReadOnlyList<string> PatrolTools = AdminAssistantAgent.PatrolToolNames;

    public static TheoryData<string> ReadToolNames => [.. PatrolTools];

    /// <summary>審查發現：巡檢用不到的唯讀工具（例如讀 AI 對話內文）也不放行，降低提示注入把內文帶進通知信的風險。</summary>
    public static TheoryData<string> NonPatrolReadToolNames =>
        [.. AdminAssistantAgent.ReadTools.Select(tool => tool.Name).Except(AdminAssistantAgent.PatrolToolNames)];

    public static TheoryData<string> MutatingToolNames =>
    [
        .. AdminAssistantAgent.MutatingTools.Values.SelectMany(tools => tools).Concat(AdminAssistantAgent.SharedMutatingTools).Select(tool => tool.Name),
    ];

    [Theory]
    [MemberData(nameof(ReadToolNames))]
    public async Task ReadTool_InAllowedList_IsAllowed(string name)
    {
        var guard = CreateGuard(PatrolTools);

        Assert.True(await guard.CanInvokeAsync(ToolNamed(name)));
    }

    [Theory]
    [MemberData(nameof(MutatingToolNames))]
    public async Task MutatingTool_IsDenied_EvenWhenListed(string name)
    {
        var tool = ToolNamed(name);
        var guard = CreateGuard([.. PatrolTools, name]);

        Assert.False(await guard.CanInvokeAsync(tool));
    }

    [Theory]
    [MemberData(nameof(NonPatrolReadToolNames))]
    public async Task NonPatrolReadTool_IsDenied(string name)
    {
        Assert.False(await CreateGuard(PatrolTools).CanInvokeAsync(ToolNamed(name)));
    }

    /// <summary>不屬於系統管理助理的唯讀工具（0.10.22 前用 Agent 測試台的專案工具，改用測試自己定義的）。</summary>
    [Fact]
    public async Task ReadTool_NotInAllowedList_IsDenied()
    {
        var guard = CreateGuard(PatrolTools);
        var outsider = new AiToolDefinition
        {
            Name = "search_things",
            DisplayName = "搜尋",
            Description = "搜尋資料。",
            PermissionPage = "測試頁",
            PermissionAction = "view",
            Create = _ => throw new InvalidOperationException("不應被呼叫"),
        };

        Assert.False(await guard.CanInvokeAsync(outsider));
    }

    [Fact]
    public async Task Audit_WithNoUser_HasNullActor_AndPatrolNote()
    {
        var audit = new RecordingAuditLogService();
        var guard = CreateGuard(PatrolTools, audit);
        var tool = ToolNamed(PatrolTools[0]);

        await guard.AuditAsync(AuditActions.Ai.ToolInvoked, AdminAssistantAgent.Name, tool, success: true);
        await guard.AuditAsync(AuditActions.Ai.ToolInvoked, AdminAssistantAgent.Name, tool, success: false, note: "error=TimeoutException");

        Assert.All(audit.Entries, entry =>
        {
            Assert.Null(entry.ActorUserId);
            Assert.Null(entry.ActorAccount);
            Assert.EndsWith("; 排程巡檢", entry.Detail, StringComparison.Ordinal);
        });
        Assert.Contains("error=TimeoutException; 排程巡檢", audit.Entries[1].Detail, StringComparison.Ordinal);
    }

    private static AiToolDefinition ToolNamed(string name)
        => AdminAssistantAgent.Home.Tools.Single(tool => tool.Name == name);

    private static ScheduledPatrolToolGuard CreateGuard(IEnumerable<string> allowed, RecordingAuditLogService? audit = null)
        => new(
            new AiToolPermissionGuard(new ThrowingPermissionChecker(), new CurrentUserService { CurrentUser = new CurrentUser() }, audit ?? new RecordingAuditLogService()),
            allowed);

    /// <summary>窄門不可以回頭問一般的權限檢查（沒有使用者時它一律拒絕）。</summary>
    private sealed class ThrowingPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(int userId, string permissionKey) => throw new InvalidOperationException("不應被呼叫");

        public Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(int userId) => throw new InvalidOperationException("不應被呼叫");
    }
}
