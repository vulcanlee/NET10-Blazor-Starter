using System.Text.Json;
using Microsoft.Extensions.AI;
using MyProject.Web.Ai.Agents;

namespace MyProject.Tests;

/// <summary>核准窗要給人看的內容（0.9.158 起）：說明、提出的專家、批次清單逐行列出、較長的截斷上限。</summary>
public sealed class AiToolApprovalsTests
{
    private static readonly AiToolDefinition Tool = new()
    {
        Name = "set_users_enabled",
        DisplayName = "停用或啟用帳號",
        Description = "說明",
        PermissionPage = "使用者管理",
        PermissionAction = "edit",
        IsMutating = true,
        ApprovalNote = "停用後對方會被登出。",
        Create = _ => throw new NotSupportedException(),
    };

    [Fact]
    public void Describe_IncludesNoteAndLabel()
    {
        var request = AiToolApprovals.Describe(Request(("enabled", JsonSerializer.SerializeToElement(false))), Tool, Call(("enabled", false)), "帳號與權限");

        Assert.Equal("停用後對方會被登出。", request.Note);
        Assert.Equal("帳號與權限", request.Label);
        Assert.Equal("停用或啟用帳號", request.DisplayName);
    }

    [Fact]
    public void Describe_CarriesIrreversibleFlag()
    {
        var irreversible = Tool with { IsIrreversible = true };

        Assert.False(AiToolApprovals.Describe(Request(), Tool, Call()).IsIrreversible);
        Assert.True(AiToolApprovals.Describe(Request(), irreversible, Call()).IsIrreversible);
    }

    [Fact]
    public void Describe_ArrayArgument_ListsEachItemWithCount()
    {
        var accounts = JsonSerializer.SerializeToElement(new[] { "temp-a", "temp-b" });

        var request = AiToolApprovals.Describe(Request(("accounts", accounts)), Tool, Call(("accounts", accounts)));

        var value = Assert.Single(request.Arguments, argument => argument.Key == "accounts").Value;
        Assert.Equal("共 2 筆：\ntemp-a\ntemp-b", value);
    }

    [Fact]
    public void Describe_LongValue_TruncatedAt8000()
    {
        var text = new string('長', 9000);

        var request = AiToolApprovals.Describe(Request(("content", JsonSerializer.SerializeToElement(text))), Tool, Call(("content", text)));

        var value = request.Arguments.Single().Value;
        Assert.Equal(8001, value.Length);
        Assert.EndsWith("…", value, StringComparison.Ordinal);
    }

    /// <summary>提示詞最多 8000 字：核准窗必須完整顯示，否則模型可以把內容藏在截斷處之後（0.9.158 審查）。</summary>
    [Fact]
    public void Describe_PromptSizedValue_ShownInFull()
    {
        var text = new string('長', 8000);

        var request = AiToolApprovals.Describe(Request(("content", JsonSerializer.SerializeToElement(text))), Tool, Call(("content", text)));

        Assert.Equal(text, request.Arguments.Single().Value);
    }

    private static FunctionCallContent Call(params (string Key, object? Value)[] arguments)
        => new("call_1", Tool.Name, arguments.ToDictionary(item => item.Key, item => item.Value));

    private static ToolApprovalRequestContent Request(params (string Key, object? Value)[] arguments)
        => new("req_1", Call(arguments));
}
