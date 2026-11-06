using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理「AI 管理」的異動工具（0.9.158 起）：存新版提示詞（以目前作用中版本做衝突檢查）、切換版本或改回內建預設；
/// 稽核同提示詞頁（Prompt.Update／Prompt.Activate），detail 加 via。
/// </summary>
public sealed class AiMutationToolsTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly PromptTemplateService prompts;
    private readonly RecordingAuditLogService audit = new();
    private readonly AiAssistantTools tools;

    public AiMutationToolsTests()
    {
        connection.Open();
        var factory = new TestDbContextFactory(connection);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        prompts = new PromptTemplateService(factory, TimeProvider.System, NullLogger<PromptTemplateService>.Instance);
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "admin", IsAdmin = true } };
        tools = new AiAssistantTools(prompts, null!, null!, null!, audit, currentUser);
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task SavePromptVersion_TwiceInARow_CreatesActiveVersionsAndAudits()
    {
        var first = await tools.SavePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "第一版內容", "default", "初版");
        var second = await tools.SavePromptVersionAsync("loganalysis", "第二版內容", "1");

        Assert.Contains("第 1 版", first, StringComparison.Ordinal);
        Assert.Contains("第 2 版", second, StringComparison.Ordinal);
        var history = await prompts.GetHistoryAsync(PromptTemplateKeys.LogAnalysis);
        Assert.Equal(2, history.Single(version => version.IsActive).Version);
        Assert.Equal("admin", history[0].CreatedByAccount);
        Assert.Equal(["Prompt.Update", "Prompt.Update"], audit.Entries.Select(entry => entry.Action));
        Assert.Equal("version=2; via=admin-assistant", audit.Entries[^1].Detail);
    }

    [Fact]
    public async Task SavePromptVersion_UnknownKey_RejectedWithAvailableKeys()
    {
        var text = await tools.SavePromptVersionAsync("NoSuchKey", "內容", "default");

        Assert.Contains(PromptTemplateKeys.LogAnalysis, text, StringComparison.Ordinal);
        Assert.Empty(audit.Entries);
    }

    /// <summary>超過核准窗能完整顯示的長度就拒絕（含前後空白，服務 trim 之前就算）：核准的內容必須就是會存的內容。</summary>
    [Fact]
    public async Task SavePromptVersion_LongerThanApprovalCanShow_Refused()
    {
        var text = await tools.SavePromptVersionAsync(PromptTemplateKeys.LogAnalysis, " " + new string('長', 8000), "default");

        Assert.Contains("8000", text, StringComparison.Ordinal);
        Assert.Empty(await prompts.GetHistoryAsync(PromptTemplateKeys.LogAnalysis));
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// 以模型讀到的作用中版本（baseVersion）做衝突檢查（0.9.159 起）：讀取之後別人另存了一版，就不能蓋掉它。
    /// </summary>
    [Fact]
    public async Task SavePromptVersion_StaleBaseVersion_RefusedAsConflict()
    {
        await tools.SavePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "別人存的第一版", "default");

        var stale = await tools.SavePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "根據內建預設改的內容", "default");
        var invalid = await tools.SavePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "內容", "最新");

        Assert.Contains("其他人修改", stale, StringComparison.Ordinal);
        Assert.Contains("baseVersion", invalid, StringComparison.Ordinal);
        Assert.Single(await prompts.GetHistoryAsync(PromptTemplateKeys.LogAnalysis));
    }

    [Fact]
    public async Task ActivatePromptVersion_DefaultAndNumber()
    {
        await tools.SavePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "第一版內容", "default");
        await tools.SavePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "第二版內容", "1");

        await tools.ActivatePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "default");
        Assert.DoesNotContain(await prompts.GetHistoryAsync(PromptTemplateKeys.LogAnalysis), version => version.IsActive);
        Assert.Equal("version=default; via=admin-assistant", audit.Entries[^1].Detail);

        await tools.ActivatePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "1");
        Assert.Equal(1, (await prompts.GetHistoryAsync(PromptTemplateKeys.LogAnalysis)).Single(version => version.IsActive).Version);
        Assert.Equal("Prompt.Activate", audit.Entries[^1].Action);
        Assert.Equal("version=1; via=admin-assistant", audit.Entries[^1].Detail);
    }

    [Fact]
    public async Task ActivatePromptVersion_InvalidOrMissingVersion_Rejected()
    {
        var invalid = await tools.ActivatePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "最新");
        var missing = await tools.ActivatePromptVersionAsync(PromptTemplateKeys.LogAnalysis, "9");

        Assert.Contains("default", invalid, StringComparison.Ordinal);
        Assert.Contains("找不到第 9 版", missing, StringComparison.Ordinal);
        Assert.Empty(audit.Entries);
    }
}
