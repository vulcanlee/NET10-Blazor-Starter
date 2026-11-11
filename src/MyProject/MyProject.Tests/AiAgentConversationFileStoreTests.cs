using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// Agent 對話內容檔的讀寫（0.10.7 起，M8 獨立審查）：另一個請求正在讀時寫入不可失敗（否則資料庫的「待核准」旗標與內容檔不一致、對話卡死），
/// 讀的人也不會讀到寫一半的內容。
/// </summary>
public sealed class AiAgentConversationFileStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "myproject-conversation-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Write_WhileTheFileIsOpenForReading_ShouldStillSucceed()
    {
        var store = Create();
        var id = Guid.NewGuid();
        var createdAt = new DateTime(2026, 11, 11);
        var path = await store.WriteAsync(createdAt, id, "舊的內容");
        Assert.NotNull(path);

        // 模擬另一個請求以 ReadAsync 相同的共用模式開著檔案。
        var fullPath = Path.Combine(root, path!.Replace('/', Path.DirectorySeparatorChar));
        await using (new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.NotNull(await store.WriteAsync(createdAt, id, "新的內容"));
        }

        Assert.Equal("新的內容", await store.ReadAsync(path));
    }

    [Fact]
    public async Task Read_WhileTheFileIsBeingWritten_ShouldNotBlockTheWriter()
    {
        var store = Create();
        var id = Guid.NewGuid();
        var createdAt = new DateTime(2026, 11, 11);
        var path = await store.WriteAsync(createdAt, id, "內容");
        var fullPath = Path.Combine(root, path!.Replace('/', Path.DirectorySeparatorChar));

        // 寫入端以 WriteAsync 相同的共用模式開著：讀取照樣成功。
        await using (new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal("內容", await store.ReadAsync(path));
        }
    }

    [Fact]
    public async Task Read_ShouldReturnNullForMissingFiles()
    {
        Assert.Null(await Create().ReadAsync("202611/does-not-exist.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private AiAgentConversationFileStore Create()
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiAgentConversationPath = root;
        return new AiAgentConversationFileStore(Options.Create(settings), NullLogger<AiAgentConversationFileStore>.Instance);
    }
}
