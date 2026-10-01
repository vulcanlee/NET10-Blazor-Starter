using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Diagnostics;

namespace MyProject.Tests;

/// <summary>
/// 例外紀錄與稽核紀錄的自動保存期限（LOG-13）。以假時鐘驗證門檻，
/// 並確認兩張表各自用對時間基準（例外＝本地時間、稽核＝UTC）。
/// </summary>
public sealed class LogRetentionWorkerTests : IAsyncDisposable
{
    // UTC 2026-10-01 01:00 ＝ 本地（UTC+8）2026-10-01 09:00。
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string exceptionPath = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
    private readonly RecordingAuditLogService audit = new();
    private ServiceProvider? services;

    [Fact]
    public async Task RunOnce_ShouldPurgeOnlyExpiredRows_AndAuditEachPurge()
    {
        var worker = await CreateWorkerAsync(new LogRetentionSettings { ExceptionLogDays = 90, AuditLogDays = 365 });
        await SeedAsync(
            exceptionLastOccurredLocal: [new DateTime(2026, 7, 1), new DateTime(2026, 9, 30)],
            auditOccurredUtc: [new DateTime(2025, 9, 1), new DateTime(2026, 9, 1)]);

        await worker.RunOnceAsync();

        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.Equal(new DateTime(2026, 9, 30), Assert.Single(await context.ExceptionLog.ToListAsync()).LastOccurredAt);
        Assert.Equal(new DateTime(2026, 9, 1), Assert.Single(await context.AuditLog.ToListAsync()).OccurredAt);

        Assert.Equal(
            [AuditActions.ExceptionLog.AutoPurge, AuditActions.Audit.AutoPurge],
            audit.Entries.Select(x => x.Action));
        Assert.Equal("rows=1; days=90", audit.Entries[0].Detail);
        Assert.All(audit.Entries, entry => Assert.Null(entry.ActorUserId));
    }

    [Fact]
    public async Task RunOnce_WithNothingExpired_ShouldNotWriteAudit()
    {
        var worker = await CreateWorkerAsync(new LogRetentionSettings());
        await SeedAsync(exceptionLastOccurredLocal: [new DateTime(2026, 9, 30)], auditOccurredUtc: [new DateTime(2026, 9, 30)]);

        await worker.RunOnceAsync();

        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task RunOnce_WithZeroDays_ShouldNotPurge()
    {
        var worker = await CreateWorkerAsync(new LogRetentionSettings { ExceptionLogDays = 0, AuditLogDays = 0 });
        await SeedAsync(exceptionLastOccurredLocal: [new DateTime(2020, 1, 1)], auditOccurredUtc: [new DateTime(2020, 1, 1)]);

        await worker.RunOnceAsync();

        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.Equal(1, await context.ExceptionLog.CountAsync());
        Assert.Equal(1, await context.AuditLog.CountAsync());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void ManualDays_ShouldFallBackToDefaults_WhenAutoPurgeIsDisabled()
    {
        var settings = new LogRetentionSettings { ExceptionLogDays = 0, AuditLogDays = 0 };

        Assert.Equal(LogRetentionSettings.DefaultExceptionLogDays, settings.ManualExceptionLogDays);
        Assert.Equal(LogRetentionSettings.DefaultAuditLogDays, settings.ManualAuditLogDays);
    }

    private async Task SeedAsync(DateTime[] exceptionLastOccurredLocal, DateTime[] auditOccurredUtc)
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        for (var index = 0; index < exceptionLastOccurredLocal.Length; index++)
        {
            context.ExceptionLog.Add(new ExceptionLog
            {
                Signature = $"sig-{index}",
                ExceptionType = "System.Exception",
                Message = $"m-{index}",
                Source = ExceptionSources.Unknown,
                OccurrenceCount = 1,
                FirstOccurredAt = exceptionLastOccurredLocal[index],
                LastOccurredAt = exceptionLastOccurredLocal[index],
            });
        }

        foreach (var occurredAt in auditOccurredUtc)
        {
            context.AuditLog.Add(new AuditLog { Action = AuditActions.Email.Test, OccurredAt = occurredAt, Success = true });
        }

        await context.SaveChangesAsync();
    }

    private async Task<LogRetentionWorker> CreateWorkerAsync(LogRetentionSettings settings)
    {
        await connection.OpenAsync();
        await using (var context = new TestDbContextFactory(connection).CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        Directory.CreateDirectory(exceptionPath);
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.ExceptionPath = exceptionPath;

        var collection = new ServiceCollection();
        collection.AddSingleton<IDbContextFactory<BackendDBContext>>(new TestDbContextFactory(connection));
        collection.AddSingleton<IMapper>(new MapperConfiguration(
            configuration => configuration.AddProfile<AutoMapping>(),
            NullLoggerFactory.Instance).CreateMapper());
        collection.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        collection.AddSingleton(Options.Create(systemSettings));
        collection.AddSingleton<IAuditLogService>(audit);
        collection.AddScoped<ExceptionStackFileStore>();
        collection.AddScoped<ExceptionLogService>();
        collection.AddScoped<AuditLogQueryService>();
        services = collection.BuildServiceProvider();

        return new LogRetentionWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new ExceptionContextAccessor(),
            new StaticMonitor(settings),
            clock,
            NullLogger<LogRetentionWorker>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (services is not null)
        {
            await services.DisposeAsync();
        }

        await connection.DisposeAsync();
        if (Directory.Exists(exceptionPath))
        {
            Directory.Delete(exceptionPath, recursive: true);
        }
    }

    private sealed class StaticMonitor(LogRetentionSettings value) : IOptionsMonitor<LogRetentionSettings>
    {
        public LogRetentionSettings CurrentValue { get; } = value;

        public LogRetentionSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<LogRetentionSettings, string?> listener) => null;
    }
}
