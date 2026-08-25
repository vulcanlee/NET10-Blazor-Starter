using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Tests;

public sealed class ProjectServiceTeamAccessTests
{
    [Fact]
    public async Task GetAsync_Admin_ShouldSeeAllRecords()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.GetAsync(NewRequest());
        var titles = result.Result.Select(x => x.Title).OrderBy(x => x).ToList();

        // 公開（無團隊）與 團隊A 可見；團隊B 不可見
        Assert.Equal(["公開專案", "團隊A專案"], titles);
    }

    [Fact]
    public async Task GetAsync_NonAdminWithoutTeams_ShouldSeeOnlyPublicRecords()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: false);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(["公開專案"], result.Result.Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task GetAsync_WithTeamFilter_ShouldFilterByTeam()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var request = NewRequest();
        request.TeamFilters = ["團隊B"];
        var result = await service.GetAsync(request);

        Assert.Equal(["團隊B專案"], result.Result.Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task GetById_NonAdmin_ShouldDenyRecordOutsideTeamScope()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var denied = await service.GetAsync(ids["團隊B專案"]);
        var allowed = await service.GetAsync(ids["團隊A專案"]);

        Assert.Equal(0, denied.Id); // 守門回空模型
        Assert.Equal("團隊A專案", allowed.Title);
    }

    [Fact]
    public async Task GetFileDownloadAsync_Admin_ShouldReturnStreamWithOriginalName()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var fileId = await fixture.SeedFileAsync(ids["團隊B專案"], "季報表.pdf", "application/pdf", "PDF-CONTENT");
        var service = fixture.CreateService(isAdmin: true);

        var result = await service.GetFileDownloadAsync(fileId);

        Assert.NotNull(result);
        await using var content = result.Content;
        Assert.Equal("季報表.pdf", result.DownloadFileName);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal("PDF-CONTENT", await new StreamReader(content).ReadToEndAsync());
    }

    [Fact]
    public async Task GetFileDownloadAsync_UnknownId_ShouldReturnNull()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        Assert.Null(await service.GetFileDownloadAsync(9999));
    }

    [Fact]
    public async Task GetFileDownloadAsync_NonAdminOutsideTeam_ShouldReturnNull()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var denied = await fixture.SeedFileAsync(ids["團隊B專案"], "b.txt", "text/plain", "B");
        var allowed = await fixture.SeedFileAsync(ids["團隊A專案"], "a.txt", "text/plain", "A");
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        Assert.Null(await service.GetFileDownloadAsync(denied));

        var result = await service.GetFileDownloadAsync(allowed);
        Assert.NotNull(result);
        await result.Content.DisposeAsync();
    }

    [Fact]
    public async Task GetFileDownloadAsync_WhenPhysicalFileMissing_ShouldReturnNull()
    {
        // metadata 還在、實體檔案被刪掉 —— 備份還原或人工清檔之後的常見狀態。
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var fileId = await fixture.SeedFileAsync(ids["公開專案"], "gone.txt", "text/plain", content: null);
        var service = fixture.CreateService(isAdmin: true);

        Assert.Null(await service.GetFileDownloadAsync(fileId));
    }

    [Fact]
    public async Task GetFileDownloadAsync_WhenRelativePathEscapesRoot_ShouldReturnNull()
    {
        // 只有直接改資料庫才寫得出這種值；這個端點會把伺服器本機的檔案送出去，
        // 因此不把「RelativePath 一定安全」當成前提。
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        // 實體檔案必須真的存在於根目錄之外，這條測試才分得出「被守門擋下」與「檔案剛好不存在」。
        await File.WriteAllTextAsync(Path.Combine(fixture.Sandbox, "outside.txt"), "SECRET");

        var fileId = await fixture.SeedFileAsync(
            ids["公開專案"], "outside.txt", "text/plain", content: null, relativePath: "../outside.txt");
        var service = fixture.CreateService(isAdmin: true);

        Assert.Null(await service.GetFileDownloadAsync(fileId));
    }

    private static DataRequest NewRequest() => new()
    {
        Search = string.Empty,
        SortField = string.Empty,
        CurrentPage = 1,
        PageSize = 50,
        Take = 0,
    };

    private sealed class FakeScopeProvider(bool isAdmin, IReadOnlyList<string> teams) : IRecordAccessScopeProvider
    {
        public Task<RecordAccessScope> GetAsync() => Task.FromResult(new RecordAccessScope(isAdmin, teams));
    }

    private sealed class ProjectServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private ProjectServiceFixture(SqliteConnection connection, BackendDBContext context, string sandbox)
        {
            this.connection = connection;
            Context = context;
            Sandbox = sandbox;
            FileRoot = Path.Combine(sandbox, "root");

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        /// <summary>附件根目錄。附件下載會比對實體路徑，因此不能沿用空字串設定。</summary>
        public string FileRoot { get; }

        /// <summary>FileRoot 的上層；逸出測試把「根目錄外」的檔案放在這裡。</summary>
        public string Sandbox { get; }

        public static async Task<ProjectServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            // 附件根目錄放在一個「自己的」上層目錄底下，逸出測試才有地方擺根目錄外的檔案，
            // 而且不會與平行執行的其他 fixture 撞名。
            var sandbox = Path.Combine(Path.GetTempPath(), $"MyProject-ProjectFile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(sandbox, "root"));

            return new ProjectServiceFixture(connection, context, sandbox);
        }

        public ProjectService CreateService(bool isAdmin, params string[] teams)
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.ProjectFilePath = FileRoot;

            return new ProjectService(
                Context,
                mapper,
                loggerFactory.CreateLogger<ProjectService>(),
                Options.Create(settings),
                new FakeScopeProvider(isAdmin, teams));
        }

        /// <summary>
        /// 建立一筆附件 metadata；<paramref name="content"/> 為 null 代表只建 metadata、不寫實體檔案。
        /// </summary>
        public async Task<int> SeedFileAsync(
            int projectId,
            string originalFileName,
            string contentType,
            string? content,
            string? relativePath = null)
        {
            relativePath ??= $"2026/08/{Guid.NewGuid():N}{Path.GetExtension(originalFileName)}";

            if (content is not null)
            {
                var fullPath = Path.Combine(FileRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, content);
            }

            var file = new ProjectFile
            {
                ProjectId = projectId,
                OriginalFileName = originalFileName,
                StoredFileName = Path.GetFileName(relativePath),
                RelativePath = relativePath,
                ContentType = contentType,
                FileSize = content?.Length ?? 0,
            };

            Context.ProjectFile.Add(file);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return file.Id;
        }

        public async Task<Dictionary<string, int>> SeedDefaultProjectsAsync()
        {
            var pub = NewProject("公開專案", null);
            var teamA = NewProject("團隊A專案", ["團隊A"]);
            var teamB = NewProject("團隊B專案", ["團隊B"]);

            Context.Project.AddRange(pub, teamA, teamB);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return new Dictionary<string, int>
            {
                ["公開專案"] = pub.Id,
                ["團隊A專案"] = teamA.Id,
                ["團隊B專案"] = teamB.Id,
            };
        }

        private static Project NewProject(string title, IEnumerable<string>? teams) => new()
        {
            Title = title,
            Status = "未開始",
            Priority = "中",
            Owner = "tester",
            Teams = TagStringHelper.ToStored(teams),
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                Directory.Delete(Sandbox, recursive: true);
            }
            catch (IOException)
            {
                // 清理是盡力而為，不該讓斷言結果被作業系統的檔案時序影響。
            }
        }
    }
}
