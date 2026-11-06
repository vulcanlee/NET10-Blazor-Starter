using System.Text.RegularExpressions;
using MyProject.AccessDatas.Models;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>系統管理助理的 Agent 定義與提示詞組裝（0.9.155 起）。</summary>
public sealed class AdminAssistantAgentTests
{
    private static readonly IReadOnlyDictionary<AdminAssistantDomain, string> DomainTexts = new Dictionary<AdminAssistantDomain, string>
    {
        [AdminAssistantDomain.Accounts] = "[帳號說明]",
        [AdminAssistantDomain.Monitoring] = "[監控說明]",
        [AdminAssistantDomain.Ai] = "[AI說明]",
        [AdminAssistantDomain.Ops] = "[維運說明]",
    };

    public static TheoryData<AdminAssistantDomain[]> DomainSets =>
    [
        [],
        [AdminAssistantDomain.Ops],
        [AdminAssistantDomain.Patrol],
        [AdminAssistantDomain.Accounts, AdminAssistantDomain.Monitoring, AdminAssistantDomain.Ai, AdminAssistantDomain.Ops],
    ];

    [Theory]
    [MemberData(nameof(DomainSets))]
    public void DefinitionFor_AnyDomains_SharesNameOperationPromptAndModel(AdminAssistantDomain[] domains)
    {
        var definition = AdminAssistantAgent.DefinitionFor(domains.ToHashSet());

        Assert.Equal(AdminAssistantAgent.Name, definition.Name);
        Assert.Equal("AdminAssistant", definition.Name);
        Assert.Equal(TokenUsageOperations.AdminAssistant, definition.Operation);
        Assert.Equal(PromptTemplateKeys.AdminAssistant, definition.PromptTemplateKey);
        Assert.Equal(nameof(AiModelSettings.AdminAssistant), definition.ModelKey);
        Assert.Equal(AdminAssistantAgent.Home.Name, definition.Name);
    }

    [Fact]
    public void DefinitionFor_AlwaysIncludesAllReadTools()
    {
        var definition = AdminAssistantAgent.DefinitionFor(new HashSet<AdminAssistantDomain> { AdminAssistantDomain.Ops });

        Assert.All(AdminAssistantAgent.ReadTools, tool => Assert.Contains(tool, definition.Tools));
    }

    [Fact]
    public void ComposeInstructions_SelectedDomainsOnly()
    {
        var text = AdminAssistantAgent.ComposeInstructions("[共同]", DomainTexts, new HashSet<AdminAssistantDomain> { AdminAssistantDomain.Monitoring });

        Assert.StartsWith("[共同]", text, StringComparison.Ordinal);
        Assert.Contains("## 專家：監控與診斷", text, StringComparison.Ordinal);
        Assert.Contains("[監控說明]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[帳號說明]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[維運說明]", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposeInstructions_EmptyOrPatrol_IncludesAllFourDomains(bool patrol)
    {
        var domains = patrol ? new HashSet<AdminAssistantDomain> { AdminAssistantDomain.Patrol } : [];

        var text = AdminAssistantAgent.ComposeInstructions("[共同]", DomainTexts, domains);

        Assert.All(DomainTexts.Values, value => Assert.Contains(value, text, StringComparison.Ordinal));
    }

    [Fact]
    public void AllTools_AreAdminOnly_UniqueSnakeCase_AndReadToolsNotMutating()
    {
        var adminOnly = ((IEnumerable<object[]>)AdminOnlyPermissionTests.AdminOnlyPermissionKeys).Select(row => (string)row[0]).ToHashSet();
        var tools = AdminAssistantAgent.Home.Tools;

        Assert.Equal(tools.Count, tools.Select(tool => tool.Name).Distinct().Count());
        Assert.All(tools, tool =>
        {
            Assert.Matches(new Regex("^[a-z][a-z0-9_]*$"), tool.Name);
            Assert.Contains(tool.PermissionPage, adminOnly);
        });
        Assert.All(AdminAssistantAgent.ReadTools, tool => Assert.False(tool.IsMutating));
    }

    [Fact]
    public void DefaultInstructions_ExistForAllSixKeys()
    {
        string[] keys =
        [
            PromptTemplateKeys.AdminAssistant, PromptTemplateKeys.AdminAssistantRouter, PromptTemplateKeys.AdminAssistantAccounts,
            PromptTemplateKeys.AdminAssistantMonitoring, PromptTemplateKeys.AdminAssistantAi, PromptTemplateKeys.AdminAssistantOps,
        ];

        Assert.All(keys, key =>
        {
            Assert.Contains(key, PromptTemplateKeys.All);
            Assert.False(string.IsNullOrWhiteSpace(MyProject.Web.Ai.AiPromptGuardrails.DefaultInstructions(key)));
        });
    }
}
