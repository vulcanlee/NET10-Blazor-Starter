using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 各個檔案存放類別的共同行為（0.10.44 起，整併成共用的 <c>RootedFiles</c> 之前先寫、改前改後都要通過）：
/// 寫入的相對路徑格式、讀回、刪除、清空、收掉舊月份與空月份目錄；根目錄沒設定時一律不動作。
/// 另外三個原本沒有根目錄檢查的類別（AI 對話紀錄、Token 用量原始檔、例外堆疊）0.10.44 起也不可讀刪根目錄以外的檔案。
/// </summary>
public sealed class FileStoreBehaviorTests : IDisposable
{
    private static readonly DateTime November = new(2026, 11, 8, 7, 30, 0);
    private readonly string root = Path.Combine(Path.GetTempPath(), "file-store-behavior-" + Guid.NewGuid().ToString("N"));

    public FileStoreBehaviorTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------- AI 對話紀錄 ----------

    [Fact]
    public async Task AiCallLog_WriteReadDelete()
    {
        var store = new AiCallLogFileStore(Settings(x => x.AiCallLogPath = Sub("ai")), NullLogger<AiCallLogFileStore>.Instance);
        var callId = Guid.NewGuid();

        var relative = await store.WriteAsync(November, callId, "內容");

        Assert.Equal($"202611/{callId:N}.json", relative);
        Assert.True(File.Exists(Path.Combine(Sub("ai"), "202611", $"{callId:N}.json")));
        Assert.Equal("內容", await store.ReadAsync(relative));
        Assert.Null(await store.ReadAsync("202611/missing.json"));
        Assert.Null(await store.ReadAsync(" "));
        Assert.Null(await store.WriteAsync(November, Guid.NewGuid(), string.Empty));

        store.Delete(relative);
        store.Delete(relative);   // 不存在視為成功
        Assert.Null(await store.ReadAsync(relative));
    }

    [Fact]
    public async Task AiCallLog_DeleteMonthsBefore_And_DeleteAll()
    {
        var store = new AiCallLogFileStore(Settings(x => x.AiCallLogPath = Sub("ai")), NullLogger<AiCallLogFileStore>.Instance);
        await store.WriteAsync(new DateTime(2026, 9, 1), Guid.NewGuid(), "九月");
        await store.WriteAsync(new DateTime(2026, 10, 31), Guid.NewGuid(), "十月");
        var kept = await store.WriteAsync(November, Guid.NewGuid(), "十一月");
        Directory.CreateDirectory(Path.Combine(Sub("ai"), "not-a-month"));

        store.DeleteMonthsBefore(new DateTime(2026, 11, 20));

        Assert.False(Directory.Exists(Path.Combine(Sub("ai"), "202609")));
        Assert.False(Directory.Exists(Path.Combine(Sub("ai"), "202610")));
        Assert.Equal("十一月", await store.ReadAsync(kept));
        Assert.True(Directory.Exists(Path.Combine(Sub("ai"), "not-a-month")));   // 不是月份的目錄不動

        File.WriteAllText(Path.Combine(Sub("ai"), "loose.txt"), "x");
        store.DeleteAll();

        Assert.True(Directory.Exists(Sub("ai")));                                  // 根目錄本身留著
        Assert.Empty(Directory.EnumerateFileSystemEntries(Sub("ai")));
    }

    // ---------- Token 用量原始檔 ----------

    [Fact]
    public async Task TokenUsage_WriteReadDelete_AndMonths()
    {
        var store = new TokenUsageRawStore(Settings(x => x.TokenUsagePath = Sub("usage")), NullLogger<TokenUsageRawStore>.Instance);

        var relative = await store.WriteAsync(November, "{\"total\":1}");

        Assert.NotNull(relative);
        Assert.StartsWith("202611/", relative);
        Assert.EndsWith(".json", relative);
        Assert.Equal("{\"total\":1}", await store.ReadAsync(relative));
        Assert.Null(await store.WriteAsync(November, "  "));
        Assert.Null(await store.WriteAsync(November, null));

        var old = await store.WriteAsync(new DateTime(2026, 8, 3), "{}");
        store.DeleteMonthsBefore(November);
        Assert.Null(await store.ReadAsync(old));
        Assert.NotNull(await store.ReadAsync(relative));

        store.Delete(relative);
        Assert.Null(await store.ReadAsync(relative));

        await store.WriteAsync(November, "{}");
        store.DeleteAll();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Sub("usage")));
    }

    // ---------- 例外堆疊（管線內，不寫日誌）----------

    [Fact]
    public async Task ExceptionStack_WriteReadDelete_AndDeleteAll()
    {
        var store = new ExceptionStackFileStore(Settings(x => x.ExceptionPath = Sub("exception")));
        var signature = new string('a', 40);

        var relative = await store.WriteAsync(signature, November, "at Foo()");

        Assert.Equal(ExceptionStackFileStore.BuildRelativePath(signature, November), relative);
        Assert.Equal($"202611/{new string('a', 16)}.txt", relative);
        Assert.Equal("at Foo()", await store.ReadAsync(relative));

        store.Delete(relative);
        Assert.Null(await store.ReadAsync(relative));

        await store.WriteAsync("b", November, "x");
        store.DeleteAll();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Sub("exception")));
    }

    // ---------- 稽核內容檔 ----------

    [Fact]
    public async Task AuditContent_DeleteMonthsBefore_KeepsTheThresholdMonth()
    {
        var store = new AuditContentFileStore(Settings(x => x.AuditContentPath = Sub("audit")), NullLogger<AuditContentFileStore>.Instance);
        var old = await store.WriteAsync(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), "舊");
        var current = await store.WriteAsync(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), "新");

        store.DeleteMonthsBefore(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Null(await store.ReadAsync(old!.RelativePath));
        Assert.Equal("新", await store.ReadAsync(current!.RelativePath));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("新"), await store.ReadBytesAsync(current.RelativePath));
    }

    // ---------- 系統巡檢報告與舊版巡檢報告 ----------

    [Fact]
    public async Task AdminPatrol_WriteContent_ReadDelete_AndEmptyMonthFolders()
    {
        var store = new AdminPatrolReportFileStore(Settings(x => x.AiAgentConversationPath = Sub("agent")), NullLogger<AdminPatrolReportFileStore>.Instance);
        var reportId = Guid.NewGuid();
        var patrolRoot = Path.Combine(Sub("agent"), AdminPatrolReportFileStore.FolderName);

        var relative = await store.WriteContentAsync(November, reportId, "json", "{}");

        Assert.Equal($"202611/{reportId:N}.json", relative);
        Assert.Equal("{}", await store.ReadAsync(relative));
        Assert.Null(await store.WriteContentAsync(November, Guid.NewGuid(), "JSON", "{}"));   // 副檔名只能小寫英文
        Assert.Null(await store.WriteContentAsync(November, Guid.NewGuid(), "json", string.Empty));

        var other = await store.WriteContentAsync(new DateTime(2026, 10, 2), Guid.NewGuid(), "json", "{}");
        store.Delete(other);
        Directory.CreateDirectory(Path.Combine(patrolRoot, "keep-me"));
        store.DeleteEmptyMonthFolders();

        Assert.False(Directory.Exists(Path.Combine(patrolRoot, "202610")));   // 空的月份目錄收掉
        Assert.True(Directory.Exists(Path.Combine(patrolRoot, "202611")));    // 還有檔案的留著
        Assert.True(Directory.Exists(Path.Combine(patrolRoot, "keep-me")));   // 不是月份的不動
    }

    // ---------- Agent 對話 ----------

    [Fact]
    public async Task AgentConversation_DeleteEmptyMonthFolders_KeepsFoldersWithFiles()
    {
        var store = new AiAgentConversationFileStore(Settings(x => x.AiAgentConversationPath = Sub("agent")), NullLogger<AiAgentConversationFileStore>.Instance);
        var kept = await store.WriteAsync(November, Guid.NewGuid(), "{}");
        var removed = await store.WriteAsync(new DateTime(2026, 9, 9), Guid.NewGuid(), "{}");
        store.Delete(removed);

        store.DeleteEmptyMonthFolders();

        Assert.False(Directory.Exists(Path.Combine(Sub("agent"), "202609")));
        Assert.Equal("{}", await store.ReadAsync(kept));
    }

    // ---------- 專案附件 ----------

    [Fact]
    public void ProjectFile_ResolveAndDelete_RefusesPathsOutsideRoot()
    {
        var store = new ProjectFileStore(Settings(x => x.ProjectFilePath = Sub("project")), NullLogger<ProjectFileStore>.Instance);
        Directory.CreateDirectory(Path.Combine(Sub("project"), "2026", "11"));
        var inside = Path.Combine(Sub("project"), "2026", "11", "a.pdf");
        File.WriteAllText(inside, "pdf");
        var outside = Path.Combine(root, "outside.txt");
        File.WriteAllText(outside, "機密");

        Assert.Equal(inside, store.TryResolveFullPath("2026/11/a.pdf"));
        Assert.Null(store.TryResolveFullPath("../outside.txt"));
        Assert.False(store.Delete("../outside.txt"));
        Assert.True(File.Exists(outside));
        Assert.True(store.Delete("2026/11/a.pdf"));
        Assert.False(File.Exists(inside));
        Assert.True(store.Delete("2026/11/a.pdf"));   // 不存在視為成功
    }

    // ---------- 根目錄沒設定 ----------

    [Fact]
    public async Task EmptyRoot_ShouldDoNothing()
    {
        var empty = Settings(_ => { });
        var ai = new AiCallLogFileStore(empty, NullLogger<AiCallLogFileStore>.Instance);
        var usage = new TokenUsageRawStore(empty, NullLogger<TokenUsageRawStore>.Instance);
        var stack = new ExceptionStackFileStore(empty);
        var audit = new AuditContentFileStore(empty, NullLogger<AuditContentFileStore>.Instance);
        var patrol = new AdminPatrolReportFileStore(empty, NullLogger<AdminPatrolReportFileStore>.Instance);

        Assert.Null(await ai.WriteAsync(November, Guid.NewGuid(), "x"));
        Assert.Null(await usage.WriteAsync(November, "x"));
        Assert.Null(await stack.WriteAsync("sig", November, "x"));
        Assert.Null(await audit.WriteAsync(November, "x"));
        Assert.Null(await patrol.WriteContentAsync(November, Guid.NewGuid(), "json", "x"));
        Assert.Null(await ai.ReadAsync("202611/a.json"));
        ai.DeleteAll();
        ai.DeleteMonthsBefore(November);
        usage.DeleteAll();
        stack.DeleteAll();
        patrol.DeleteEmptyMonthFolders();
    }

    // ---------- 0.10.44 起：原本沒有根目錄檢查的三個類別 ----------

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    public async Task PreviouslyUncheckedStores_ShouldNotTouchFilesOutsideTheRoot(string relativePath)
    {
        var outside = Path.Combine(root, "outside.txt");
        File.WriteAllText(outside, "機密");
        var settings = Settings(x =>
        {
            x.AiCallLogPath = Sub("ai");
            x.TokenUsagePath = Sub("usage");
            x.ExceptionPath = Sub("exception");
        });
        var ai = new AiCallLogFileStore(settings, NullLogger<AiCallLogFileStore>.Instance);
        var usage = new TokenUsageRawStore(settings, NullLogger<TokenUsageRawStore>.Instance);
        var stack = new ExceptionStackFileStore(settings);

        Assert.Null(await ai.ReadAsync(relativePath));
        Assert.Null(await usage.ReadAsync(relativePath));
        Assert.Null(await stack.ReadAsync(relativePath));
        Assert.Null(await ai.ReadAsync(outside));          // 絕對路徑也不行
        ai.Delete(relativePath);
        usage.Delete(relativePath);
        stack.Delete(relativePath);
        ai.Delete(outside);

        Assert.True(File.Exists(outside));
    }

    /// <summary>根目錄設成磁碟或檔案系統的根（D:\、/）時，底下的路徑照樣接受（獨立審查發現：曾因多加一個分隔符號而全部被當成越界）。只解析、不寫檔。</summary>
    [Fact]
    public void RootedFiles_DriveRootAsTheRoot_ShouldStillResolve()
    {
        var driveRoot = Path.GetPathRoot(root)!;
        var files = new RootedFiles(() => driveRoot);

        Assert.Equal(Path.Combine(driveRoot, "202611", "a.json"), files.TryResolve("202611/a.json"));
        Assert.Null(new RootedFiles(() => string.Empty).TryResolve("202611/a.json"));
    }

    /// <summary>
    /// 根目錄檢查的大小寫依作業系統決定（0.10.47 起 <see cref="PathComparison"/>，與 .NET 判斷路徑是否分大小寫的規則相同）：
    /// Windows、macOS 的檔案系統預設不分大小寫；Linux 等分大小寫。
    /// </summary>
    [Fact]
    public void PathComparison_ShouldIgnoreCaseOnlyOnCaseInsensitiveFileSystems()
    {
        Assert.Equal(StringComparison.OrdinalIgnoreCase, PathComparison.For(caseInsensitiveFileSystem: true));
        Assert.Equal(StringComparison.Ordinal, PathComparison.For(caseInsensitiveFileSystem: false));
        Assert.Equal(PathComparison.For(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()), PathComparison.Current);
    }

    /// <summary>
    /// 只差大小寫的同層資料夾（根目錄 <c>Files</c>、路徑走到 <c>files</c>）：不分大小寫的檔案系統上就是同一個資料夾，照樣接受（Windows 上行為不變）；
    /// 分大小寫的（Linux）是根目錄旁邊另一個資料夾，必須拒絕——0.10.46 以前一律不分大小寫，在 Linux 上會被當成在根目錄底下。
    /// </summary>
    [Fact]
    public void RootedFiles_SiblingThatDiffersOnlyInCase_ShouldFollowTheFileSystem()
    {
        var files = new RootedFiles(() => Path.Combine(root, "Files"));

        var resolved = files.TryResolve("../files/a.json");

        if (PathComparison.Current == StringComparison.OrdinalIgnoreCase)
        {
            Assert.Equal(Path.Combine(root, "files", "a.json"), resolved);
        }
        else
        {
            Assert.Null(resolved);
        }
    }

    private string Sub(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static IOptions<SystemSettings> Settings(Action<ExternalFileSystem> configure)
    {
        var settings = new SystemSettings();
        configure(settings.ExternalFileSystem);
        return Options.Create(settings);
    }
}
