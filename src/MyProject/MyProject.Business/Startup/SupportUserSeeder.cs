using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Business.Startup;

/// <summary>
/// 內建管理員帳號（<c>BootstrapSettings</c>，預設 support）。行為與 0.9.90 之前 Program.cs 的寫法完全相同：
/// <list type="bullet">
/// <item>不存在 → 建立為管理員，密碼取自設定。</item>
/// <item>已存在 → 密碼與設定不符（或仍是舊雜湊格式）就重設為設定值；強制 <c>IsAdmin = true</c>、角色指回預設角色。
/// 姓名、Email、啟用狀態、鎖定狀態不動。</item>
/// </list>
/// Production 不允許空白或範本預設密碼，由 Web 的 <c>StartupSafetyValidator</c> 在更早的階段擋下。
/// </summary>
public sealed class SupportUserSeeder : IDatabaseSeeder
{
    private readonly BackendDBContext dbContext;
    private readonly BootstrapSettings bootstrapSettings;
    private readonly ILogger<SupportUserSeeder> logger;

    public SupportUserSeeder(
        BackendDBContext dbContext,
        IOptions<BootstrapSettings> bootstrapSettings,
        ILogger<SupportUserSeeder> logger)
    {
        this.dbContext = dbContext;
        this.bootstrapSettings = bootstrapSettings.Value;
        this.logger = logger;
    }

    public int Order => 20;

    public string Name => "support 帳號";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // 以名稱重新查詢，不依賴前一個 seeder 留下的實體（初始化器會在 seeder 之間清空追蹤狀態）。
        var defaultRole = await dbContext.RoleView
            .FirstOrDefaultAsync(x => x.Name == MagicObjectHelper.預設角色, cancellationToken)
            ?? throw new InvalidOperationException($"找不到「{MagicObjectHelper.預設角色}」，support 帳號無法指定角色；DefaultRoleViewSeeder 應先執行。");

        // 連已刪除的一起找、優先取未刪除的（0.9.95 起）：只看有過濾的集合的話，被軟刪除的 support 會被當成不存在，
        // 建出第二個 support。服務層禁止刪除它，這裡是最後一道防線。
        // 0.9.171 起帳號不分大小寫：設定檔寫「Support」也找得到既有的 support，不會再建一個（唯一索引也不允許）。
        // 大小寫完全相同的優先；⚠️ 只差大小寫的那一筆如果不是管理員，代表是另一位使用者，絕不可把他提升成 support、改掉他的密碼
        // （知道 support 密碼的人就會登入成他）——讓啟動停下來，請管理員處理。
        var supportAccount = NameNormalizer.Normalize(bootstrapSettings.SupportAccount);
        var support = await dbContext.MyUser
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .Where(x => x.Account.ToLower() == NameNormalizer.ToLowerAscii(supportAccount))
            .OrderBy(x => x.Account == supportAccount ? 0 : 1)
            .ThenBy(x => x.IsDeleted)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (support is not null && support.Account != supportAccount && support.IsAdmin == false)
        {
            logger.LogCritical(
                "Support account only matches a non-admin user whose account differs in case; refusing to promote it. UserId={UserId}", support.Id);
            throw new InvalidOperationException(
                $"設定檔的 support 帳號「{supportAccount}」與既有的一般使用者「{support.Account}」只差大小寫。為避免把那位使用者提升成管理員，"
                + "請先把那位使用者改用其他帳號，或把設定檔的 BootstrapSettings:SupportAccount 改成既有 support 帳號的寫法後再啟動。");
        }

        if (support is { IsDeleted: true })
        {
            SoftDeleteHelper.Restore(support);
            logger.LogWarning("Support user was soft-deleted; restored it. UserId={UserId}", support.Id);
        }

        if (support is null)
        {
            support = new MyUser
            {
                Account = bootstrapSettings.SupportAccount,
                Name = bootstrapSettings.SupportName,
                Email = bootstrapSettings.SupportEmail,
                IsAdmin = true,
                Salt = Guid.NewGuid().ToString(),
                Status = true,
                RoleViewId = defaultRole.Id,
                Password = SecurePasswordHasher.HashPassword(bootstrapSettings.SupportPassword),
            };

            dbContext.MyUser.Add(support);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded default support user.");
            return;
        }

        if (SecurePasswordHasher.VerifyPassword(bootstrapSettings.SupportPassword, support.Password, support.Salt)
            != PasswordVerificationOutcome.Success)
        {
            support.Password = SecurePasswordHasher.HashPassword(bootstrapSettings.SupportPassword);
            // 設定檔換了 support 的密碼：既有的 support 工作階段一律失效（0.9.103 起）。
            support.SecurityStamp = SecurityStamps.New();
        }

        support.IsAdmin = true;
        support.RoleViewId = defaultRole.Id;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Updated existing support user seed data.");
    }
}
