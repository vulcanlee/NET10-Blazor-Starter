using MyProject.Models.AdapterModel;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>系統管理助理異動工具的共用規則（0.9.158 起）：批次清單檢查、整批做完後至少留一位啟用中的管理員、逐筆結果。</summary>
public sealed class AdminAssistantMutationTests
{
    [Fact]
    public void ValidateBatch_TrimsDeduplicatesAndKeepsOrder()
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch([" a ", "b", "a", "", "  "]);

        Assert.Null(error);
        Assert.Equal(["a", "b"], items);
    }

    [Fact]
    public void ValidateBatch_EmptyOrTooMany_ReturnsError()
    {
        Assert.Contains("至少", AdminAssistantMutation.ValidateBatch(null).Error, StringComparison.Ordinal);
        Assert.Contains("至少", AdminAssistantMutation.ValidateBatch(["", " "]).Error, StringComparison.Ordinal);

        var many = Enumerable.Range(1, 51).Select(i => $"u{i}").ToArray();
        var (items, error) = AdminAssistantMutation.ValidateBatch(many);
        Assert.Empty(items);
        Assert.Contains("50", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAdminsRemain_CountsOnlyEnabledAdminsAfterWholeBatch()
    {
        List<MyUserAdapterModel> users =
        [
            new() { Id = 1, Account = "support", IsAdmin = true, Status = true },
            new() { Id = 2, Account = "admin2", IsAdmin = true, Status = true },
            new() { Id = 3, Account = "old-admin", IsAdmin = true, Status = false },
            new() { Id = 4, Account = "user", IsAdmin = false, Status = true },
        ];

        Assert.Null(AdminAssistantMutation.CheckAdminsRemain(users, new HashSet<int> { 2 }));
        Assert.Null(AdminAssistantMutation.CheckAdminsRemain(users, new HashSet<int> { 4 }));

        var error = AdminAssistantMutation.CheckAdminsRemain(users, new HashSet<int> { 1, 2 });
        Assert.Contains("管理員", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchResult_SummarizesSuccessesAndFailures()
    {
        var result = new BatchResult("停用帳號");
        result.Add("a", true, null);
        result.Add("b", false, "找不到帳號");

        var json = AdminAssistantToolText.ToJson(result.ToOutput());

        Assert.Contains("\"succeeded\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"failed\":1", json, StringComparison.Ordinal);
        Assert.Contains("找不到帳號", json, StringComparison.Ordinal);
    }
}
