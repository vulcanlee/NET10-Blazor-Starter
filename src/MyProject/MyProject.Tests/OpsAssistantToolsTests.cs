using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Backup;
using MyProject.Web.Configuration.Parameters;
using MyProject.Web.DataRetention;

namespace MyProject.Tests;

/// <summary>系統管理助理「系統設定與維運」新增的唯讀工具（0.9.157 起）：參數不外送孤兒值與型別、備份不含路徑、清除預覽的參數防呆。</summary>
public sealed class OpsAssistantToolsTests
{
    private static readonly DateOnly Today = new(2026, 11, 6);

    [Fact]
    public void DescribeParameters_OmitsOrphanValuesAndTypes_AndFiltersGroup()
    {
        var definition = SystemParameterCatalog.All[0];
        var other = SystemParameterCatalog.All.First(item => item.Group != definition.Group);
        var overview = new SystemParameterOverview
        {
            Items =
            [
                new SystemParameterOverviewItem { Definition = definition, EffectiveValue = "30", BaseValue = "90", Source = SystemParameterSource.Override },
                new SystemParameterOverviewItem { Definition = other, EffectiveValue = "x", BaseValue = "x", Source = SystemParameterSource.File },
            ],
            Orphans = [new SystemParameterAdapterModel { ParameterKey = "Old:Key", Value = "ORPHAN-SECRET-VALUE" }],
            BaseInvalid = new Dictionary<string, IReadOnlyList<string>>(),
        };

        var json = AdminAssistantToolText.ToJson(OpsAssistantTools.DescribeParameters(overview, definition.Group));

        Assert.Contains(definition.Key, json, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Key, json, StringComparison.Ordinal);
        Assert.Contains("Old:Key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ORPHAN-SECRET-VALUE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("optionsType", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeParameters_UnknownGroup_ListsAllWithNote()
    {
        var overview = new SystemParameterOverview
        {
            Items = [new SystemParameterOverviewItem { Definition = SystemParameterCatalog.All[0], EffectiveValue = "1", Source = SystemParameterSource.File }],
            Orphans = [],
            BaseInvalid = new Dictionary<string, IReadOnlyList<string>>(),
        };

        var json = AdminAssistantToolText.ToJson(OpsAssistantTools.DescribeParameters(overview, "不存在的分組"));

        Assert.Contains(SystemParameterCatalog.All[0].Key, json, StringComparison.Ordinal);
        Assert.Contains("不存在的分組", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeBackups_FileNamesOnly_NewestFirstLimited()
    {
        IReadOnlyList<BackupFileInfo> files =
        [
            new("backup-20261106-102557.zip", 82865, new DateTime(2026, 11, 6, 10, 25, 57), null),
            new("backup-20261105-020000.zip", 80000, new DateTime(2026, 11, 5, 2, 0, 0), null),
        ];

        var json = AdminAssistantToolText.ToJson(OpsAssistantTools.DescribeBackups(files, take: 1));

        Assert.Contains("backup-20261106-102557.zip", json, StringComparison.Ordinal);
        Assert.DoesNotContain("backup-20261105-020000.zip", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("audit-log", 30, null, true, null)]
    [InlineData("audit-log", null, "2026-10-01", true, null)]
    [InlineData("backup", 30, null, false, "exception-log")]
    [InlineData("audit-log", null, null, false, "其中一種")]
    [InlineData("audit-log", null, "昨天", false, "昨天")]
    [InlineData("audit-log", 99999, null, false, "1～3650")]
    public void ParsePurgeRequest_ValidatesWithoutThrowing(string rowKey, int? keepDays, string? beforeDate, bool ok, string? messagePart)
    {
        var (cutoff, error) = OpsAssistantTools.ParsePurgeRequest(rowKey, keepDays, beforeDate, Today);

        Assert.Equal(ok, cutoff is not null);
        if (messagePart is not null)
        {
            Assert.Contains(messagePart, error, StringComparison.Ordinal);
        }
    }
}
