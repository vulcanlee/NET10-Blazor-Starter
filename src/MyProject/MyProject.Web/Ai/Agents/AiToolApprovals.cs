using System.Text.Json;
using Microsoft.Extensions.AI;
using MyProject.Business.Helpers;

namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 工具核准的共用規則（0.9.144 起由 <see cref="AiAgentChat"/> 抽出，Agent 測試台與工作流程共用）：
/// 先替使用者檢查權限，沒有權限的直接拒絕（寫 <c>Ai.ToolDenied</c>、不跳核准窗），有權限的才請使用者決定。
/// </summary>
internal static class AiToolApprovals
{
    public const string NoPermissionReason = "使用者沒有執行這個操作的權限";
    public const string RejectedReason = "使用者拒絕執行";
    public const string UnknownToolReason = "這不是這個 Agent 可以使用的工具";

    /// <summary>
    /// 分流：沒有權限（或不是這個 agent 的工具）的直接拒絕並寫稽核，回傳在 <c>AutoRejected</c>；
    /// 有權限的放在 <c>ForUser</c> 等使用者決定。
    /// </summary>
    public static async Task<(List<(ToolApprovalRequestContent Content, AiToolDefinition Tool, AiToolApprovalRequest Request)> ForUser, List<AIContent> AutoRejected)> TriageAsync(
        AiAgentDefinition definition,
        IEnumerable<ToolApprovalRequestContent> approvals,
        IAiToolPermissionGuard guard)
    {
        var forUser = new List<(ToolApprovalRequestContent, AiToolDefinition, AiToolApprovalRequest)>();
        var autoRejected = new List<AIContent>();
        foreach (var request in approvals)
        {
            var call = request.ToolCall as FunctionCallContent;
            var tool = definition.Tools.FirstOrDefault(item => item.Name == call?.Name);
            if (call is null || tool is null)
            {
                autoRejected.Add(request.CreateResponse(false, UnknownToolReason));
                continue;
            }

            if (await guard.CanInvokeAsync(tool) == false)
            {
                await guard.AuditAsync(AuditActions.Ai.ToolDenied, definition.Name, tool, success: false, note: "stage=approval");
                autoRejected.Add(request.CreateResponse(false, NoPermissionReason));
                continue;
            }

            forUser.Add((request, tool, Describe(request, tool, call)));
        }

        return (forUser, autoRejected);
    }

    /// <summary>給畫面的一筆待核准：工具名稱與參數（名稱＝值，值一律轉成文字、太長截斷）。</summary>
    public static AiToolApprovalRequest Describe(ToolApprovalRequestContent content, AiToolDefinition tool, FunctionCallContent call)
        => new(content.RequestId, tool.Name, tool.DisplayName, DescribeArguments(call));

    private static IReadOnlyList<KeyValuePair<string, string>> DescribeArguments(FunctionCallContent call)
        => (call.Arguments ?? new Dictionary<string, object?>())
            .Select(argument => new KeyValuePair<string, string>(argument.Key, DescribeValue(argument.Value)))
            .ToList();

    private static string DescribeValue(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement element => element.GetRawText(),
            _ => value.ToString() ?? string.Empty,
        };

        return text.Length <= 2000 ? text : string.Concat(text.AsSpan(0, 2000), "…");
    }
}
