using MyProject.Web.Ai.Agents;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>
/// 核准窗的「核准」鈕（0.9.159 起）：任一筆是不可復原的動作時，要輸入「確認」才能核准（前後空白不計）；
/// 全部可以復原時不需要輸入。
/// </summary>
public sealed class AdminAssistantApprovalTests
{
    private static readonly AiToolApprovalRequest Reversible = new("r1", "set_users_enabled", "啟用／停用帳號", []);
    private static readonly AiToolApprovalRequest Irreversible = new("r2", "purge_users", "徹底刪除帳號", [], IsIrreversible: true);

    [Fact]
    public void ReversibleOnly_NoConfirmationNeeded()
    {
        Assert.False(AdminAssistantApproval.RequiresConfirmation([Reversible]));
        Assert.True(AdminAssistantApproval.CanApprove([Reversible], null));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("確", false)]
    [InlineData("確定", false)]
    [InlineData("確認", true)]
    [InlineData(" 確認 ", true)]
    public void AnyIrreversible_RequiresTypingConfirm(string? typed, bool expected)
    {
        Assert.True(AdminAssistantApproval.RequiresConfirmation([Reversible, Irreversible]));
        Assert.Equal(expected, AdminAssistantApproval.CanApprove([Reversible, Irreversible], typed));
    }

    [Fact]
    public void NoPendingApprovals_CannotApprove()
    {
        Assert.False(AdminAssistantApproval.CanApprove([], "確認"));
    }
}
