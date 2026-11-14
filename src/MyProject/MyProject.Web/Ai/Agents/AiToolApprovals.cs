using System.Text.Json;
using Microsoft.Extensions.AI;
using MyProject.Business.Helpers;

namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 工具核准的共用規則（0.9.144 起由 <see cref="AiAgentChat"/> 抽出，各 Agent 共用）：
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
        IAiToolPermissionGuard guard,
        string? label = null,
        Func<AiToolDefinition, FunctionCallContent, Task<string?>>? preview = null)
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

            var described = Describe(request, tool, call, label);
            if (preview is not null && tool.ApprovalPreview is not null)
            {
                described = described with { Preview = await preview(tool, call) };
            }

            forUser.Add((request, tool, described));
        }

        return (forUser, autoRejected);
    }

    /// <summary>
    /// 核准窗裡每個參數值的字數上限（0.9.158 起由 2000 放寬）：容得下 50 筆的批次清單與整份提示詞（最多 8000 字）。
    /// ⚠️ 會被截斷的參數，工具自己要拒絕超過這個長度的值，否則管理員核准的不是實際會寫入的內容。
    /// </summary>
    public const int ArgumentMaxLength = 8000;

    /// <summary>批次清單在核准窗最多逐行列出幾筆（同批次工具的上限）。</summary>
    public const int ListedItemLimit = 50;

    /// <summary>
    /// 給畫面的一筆待核准：工具名稱、參數（名稱＝值，值一律轉成文字、太長截斷；陣列逐行列出並標筆數）、
    /// 工具的核准說明（<see cref="AiToolDefinition.ApprovalNote"/>）與提出的專家（0.9.158 起）。
    /// </summary>
    public static AiToolApprovalRequest Describe(ToolApprovalRequestContent content, AiToolDefinition tool, FunctionCallContent call, string? label = null)
        => new(content.RequestId, tool.Name, tool.DisplayName, DescribeArguments(call), tool.ApprovalNote, label, tool.IsIrreversible);

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
            JsonElement { ValueKind: JsonValueKind.Array } element => DescribeList(element.EnumerateArray().Select(DescribeItem).ToList()),
            JsonElement element => element.GetRawText(),
            IEnumerable<string> items => DescribeList(items.ToList()),
            _ => value.ToString() ?? string.Empty,
        };

        return text.Length <= ArgumentMaxLength ? text : string.Concat(text.AsSpan(0, ArgumentMaxLength), "…");
    }

    /// <summary>批次清單：「共 N 筆：」加上逐行對象，超過 <see cref="ListedItemLimit"/> 筆只列前面的並註明。</summary>
    private static string DescribeList(IReadOnlyList<string> items)
    {
        var lines = items.Take(ListedItemLimit).ToList();
        var text = $"共 {items.Count} 筆：\n" + string.Join("\n", lines);
        return items.Count > ListedItemLimit ? text + $"\n…另有 {items.Count - ListedItemLimit} 筆" : text;
    }

    private static string DescribeItem(JsonElement item)
        => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText();
}
