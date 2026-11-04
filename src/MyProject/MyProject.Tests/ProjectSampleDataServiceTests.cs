using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using Fixture = MyProject.Tests.ProjectServiceTeamAccessTests.ProjectServiceFixture;

namespace MyProject.Tests;

/// <summary>
/// Agent 測試台的「產生／清除測試專案」（0.9.145 起）：一律經 ProjectService，以標題開頭「【測試】」辨認。
/// </summary>
public sealed class ProjectSampleDataServiceTests
{
    private static readonly DateTime Today = new(2026, 11, 4);

    [Fact]
    public void Build_ShouldBeTenValidPublicProjects_WithAnOverdueOne()
    {
        var samples = ProjectSampleDataService.Build(Today);

        Assert.Equal(10, samples.Count);
        Assert.Equal(10, samples.Select(sample => sample.Title).Distinct().Count());
        Assert.All(samples, sample =>
        {
            Assert.StartsWith(ProjectSampleDataService.TitlePrefix, sample.Title, StringComparison.Ordinal);
            Assert.Empty(sample.Teams);
            Assert.Contains(sample.Status, ProjectAdapterModel.StatusOptions);
            Assert.Contains(sample.Priority, ProjectAdapterModel.PriorityOptions);
            Assert.False(string.IsNullOrWhiteSpace(sample.Description));
            var errors = new List<ValidationResult>();
            Assert.True(Validator.TryValidateObject(sample, new ValidationContext(sample), errors, validateAllProperties: true), string.Join("；", errors));
        });
        Assert.Contains(samples, sample => sample.Status != "已完成" && sample.EndDate < Today);
    }

    [Fact]
    public async Task Generate_ShouldCreateTenProjects_ThenRefuseWhileSamplesExist()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = Create(fixture);

        var first = await service.GenerateAsync(Today);
        var second = await service.GenerateAsync(Today);

        Assert.True(first.Success);
        Assert.Equal(10, first.Titles.Count);
        Assert.False(second.Success);
        Assert.Equal(0, second.Count);
        Assert.Contains("請先按「清除測試專案」", second.Message, StringComparison.Ordinal);
        Assert.Equal(10, await fixture.Context.Project.CountAsync());
        Assert.All(await fixture.Context.Project.ToListAsync(), project => Assert.Null(project.Teams));
    }

    [Fact]
    public async Task Generate_ShouldAlsoBeBlockedBySoftDeletedSamples()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = Create(fixture);
        await service.GenerateAsync(Today);
        var one = await fixture.Context.Project.FirstAsync();
        await fixture.CreateService(isAdmin: true).DeleteAsync(one.Id);

        var result = await service.GenerateAsync(Today);

        Assert.False(result.Success);
        Assert.Contains("10 筆", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clear_ShouldPurgeAllSamplesIncludingDeleted_AndKeepOtherProjects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        fixture.Context.Project.Add(new Project { Title = "正式專案【測試】不是開頭", Status = "進行中", Priority = "中", Owner = "某人" });
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        var service = Create(fixture);
        await service.GenerateAsync(Today);
        var softDeleted = await fixture.Context.Project.FirstAsync(project => project.Title.StartsWith(ProjectSampleDataService.TitlePrefix));
        await fixture.CreateService(isAdmin: true).DeleteAsync(softDeleted.Id);

        var result = await service.ClearAsync();

        Assert.True(result.Success);
        Assert.Equal(10, result.Count);
        var remaining = await fixture.Context.Project.IgnoreQueryFilters().AsNoTracking().Select(project => project.Title).ToListAsync();
        Assert.Equal(4, remaining.Count);
        Assert.DoesNotContain(remaining, title => title.StartsWith(ProjectSampleDataService.TitlePrefix, StringComparison.Ordinal));
        Assert.Contains("正式專案【測試】不是開頭", remaining);
        Assert.Contains(ids.Keys.First(), remaining);

        // 清除後可以再產生。
        Assert.True((await service.GenerateAsync(Today)).Success);
    }

    [Fact]
    public async Task Clear_WithoutSamples_ShouldSucceedWithZero()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await Create(fixture).ClearAsync();

        Assert.Equal((true, 0, "沒有測試專案。"), (result.Success, result.Count, result.Message));
    }

    private static ProjectSampleDataService Create(Fixture fixture)
        => new(fixture.CreateService(isAdmin: false), NullLogger<ProjectSampleDataService>.Instance);
}
