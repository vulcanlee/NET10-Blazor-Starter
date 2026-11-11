using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas.Models;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using static MyProject.Tests.ProjectServiceTeamAccessTests;

namespace MyProject.Tests;

/// <summary>
/// 專案存檔的一致性（0.10.3 起，WASM 遷移 M4 獨立審查）：資料與附件同一次存檔，附件存失敗時整筆不留（不會出現「資料存了、附件沒存、稽核沒寫」）；
/// 標題存檔前去前後空白；還原時與其他未刪除的專案同名就擋下；「顯示已刪除」也套用分類、團隊過濾。
/// </summary>
public sealed class ProjectServiceSaveTests
{
    [Fact]
    public async Task AddAsync_WhenAnAttachmentFailsToSave_ShouldLeaveNothingBehind()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService(isAdmin: true);

        var result = await service.AddAsync(NewProject("附件失敗"), [Upload("好.txt", new MemoryStream("ok"u8.ToArray())), Upload("壞.txt", new BrokenStream())]);

        Assert.False(result.Success);
        Assert.Equal(VerifyRecordErrorKind.Error, result.Kind);
        Assert.Equal(0, await fixture.Context.Project.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await fixture.Context.ProjectFile.CountAsync());
        Assert.Empty(Directory.Exists(fixture.FileRoot) ? Directory.GetFiles(fixture.FileRoot, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task UpdateAsync_WhenAnAttachmentFailsToSave_ShouldKeepTheRecordAndItsVersion_SoTheUserCanRetry()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService(isAdmin: true);
        var project = NewProject("重試");
        Assert.True((await service.AddAsync(project)).Success);
        var opened = await service.GetAsync(project.Id);
        opened.Description = "改過";

        var failed = await service.UpdateAsync(opened, [Upload("壞.txt", new BrokenStream())]);
        fixture.Context.ChangeTracker.Clear();
        var afterFailure = await fixture.Context.Project.AsNoTracking().SingleAsync(x => x.Id == project.Id);

        Assert.False(failed.Success);
        Assert.Null(afterFailure.Description);
        Assert.Equal(opened.ConcurrencyStamp, afterFailure.ConcurrencyStamp);

        // 使用者照原本的視窗再按一次儲存，不會變成「別人已修改」。
        var retried = await service.UpdateAsync(opened, [Upload("好.txt", new MemoryStream("ok"u8.ToArray()))]);
        Assert.True(retried.Success, retried.Message);
        Assert.Equal(1, await fixture.Context.ProjectFile.CountAsync());
    }

    [Fact]
    public async Task SavedTitles_ShouldBeTrimmed_SoTheDuplicateCheckCannotBeBypassedWithSpaces()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService(isAdmin: true);

        var project = NewProject("\t新系統　");
        Assert.True((await service.AddAsync(project)).Success);

        Assert.Equal("新系統", (await fixture.Context.Project.AsNoTracking().SingleAsync()).Title);
        var duplicate = await service.BeforeAddCheckAsync(NewProject("新系統"));
        Assert.Equal(VerifyRecordErrorKind.Conflict, duplicate.Kind);
    }

    [Fact]
    public async Task RestoreAsync_WhenAnActiveProjectHasTheSameTitle_ShouldReturnConflict()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService(isAdmin: true);
        var old = NewProject("同名");
        await service.AddAsync(old);
        await service.DeleteAsync(old.Id);
        await service.AddAsync(NewProject("同名"));

        var result = await service.RestoreAsync(old.Id);

        Assert.False(result.Success);
        Assert.Equal(VerifyRecordErrorKind.Conflict, result.Kind);
        Assert.Contains("同名", result.Message);
    }

    [Fact]
    public async Task GetDeletedAsync_ShouldHonourTheCategoryAndTeamFilters()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService(isAdmin: true);
        var tagged = NewProject("有分類");
        tagged.Categories = ["硬體"];
        var other = NewProject("沒分類");
        foreach (var project in new[] { tagged, other })
        {
            await service.AddAsync(project);
            await service.DeleteAsync(project.Id);
        }

        var result = await service.GetDeletedAsync(new DataRequest { CurrentPage = 1, PageSize = 10, Take = 10, CategoryFilters = ["硬體"] });

        Assert.Equal(["有分類"], result.Result.Select(x => x.Title));
    }

    private static ProjectAdapterModel NewProject(string title) => new()
    {
        Title = title,
        Owner = "測試",
        Status = ProjectAdapterModel.StatusOptions[0],
        Priority = ProjectAdapterModel.PriorityOptions[1],
        CreatedAt = new DateTime(2026, 11, 1),
        UpdatedAt = new DateTime(2026, 11, 1),
    };

    private static ProjectUploadFileInput Upload(string name, Stream content)
        => new() { FileName = name, ContentType = "text/plain", FileSize = 2, Content = content };

    /// <summary>讀到一半就壞掉的上傳（例如連線中斷、磁碟滿）。</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("上傳中斷");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new IOException("上傳中斷");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("上傳中斷");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
