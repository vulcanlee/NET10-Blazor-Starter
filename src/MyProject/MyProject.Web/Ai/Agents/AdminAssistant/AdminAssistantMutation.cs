using MyProject.Models.AdapterModel;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理異動工具的共用規則（0.9.158 起）：批次清單檢查、整批做完後至少留一位啟用中的管理員。
/// 服務層只擋 support 與「自己」，沒有「最後一位管理員」的保護，所以由工具在執行前整批檢查（不能逐筆判斷）。
/// </summary>
public static class AdminAssistantMutation
{
    /// <summary>批次工具單次最多幾筆（同核准窗逐行列出的上限）。</summary>
    public const int MaxBatch = 50;

    /// <summary>去除空白與重複（保留原順序）；空清單或超過 <paramref name="max"/> 筆時回錯誤說明、不執行任何一筆。</summary>
    public static (IReadOnlyList<string> Items, string? Error) ValidateBatch(IReadOnlyList<string>? source, int max = MaxBatch)
    {
        var items = (source ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (items.Count == 0)
        {
            return ([], "請至少指定一個對象。");
        }

        return items.Count > max
            ? ([], $"一次最多 {max} 筆，這次有 {items.Count} 筆，沒有執行任何一筆。請分批進行。")
            : (items, null);
    }

    /// <summary>
    /// 整批做完後（<paramref name="losingAdmin"/> 裡的人不再是可用的管理員）是否還有至少一位啟用中的管理員；
    /// 沒有時回錯誤說明（整批不執行）。只算 <c>IsAdmin &amp;&amp; Status</c>：停用的管理員無法登入。
    /// </summary>
    public static string? CheckAdminsRemain(IReadOnlyList<MyUserAdapterModel> users, IReadOnlySet<int> losingAdmin)
    {
        var remaining = users.Count(user => user.IsAdmin && user.Status && losingAdmin.Contains(user.Id) == false);
        return remaining > 0
            ? null
            : "這樣做之後系統將沒有任何啟用中的管理員，已整批拒絕、沒有執行。請保留至少一位啟用中的管理員。";
    }
}

/// <summary>批次異動的逐筆結果（0.9.158 起），輸出成 JSON 給模型照實回報。</summary>
public sealed class BatchResult(string action)
{
    private readonly List<(string Target, bool Success, string? Message)> items = [];

    public void Add(string target, bool success, string? message) => items.Add((target, success, message));

    public int SucceededCount => items.Count(item => item.Success);

    public object ToOutput() => new
    {
        action,
        succeeded = SucceededCount,
        failed = items.Count - SucceededCount,
        results = items.Select(item => new
        {
            target = item.Target,
            ok = item.Success,
            message = string.IsNullOrWhiteSpace(item.Message) ? null : item.Message,
        }),
    };
}
