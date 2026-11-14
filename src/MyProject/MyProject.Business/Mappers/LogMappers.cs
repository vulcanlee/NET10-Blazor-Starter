using MyProject.AccessDatas.Models;
using MyProject.Models.AdapterModel;
using Riok.Mapperly.Abstractions;

namespace MyProject.Business.Mappers;

/// <summary>
/// 唯讀紀錄頁的對應（0.10.23 起以 Mapperly 在編譯時產生，取代 AutoMapper）：Token 用量、AI 對話紀錄、系統例外、稽核、排程。
/// 只有「實體 → 畫面」單向。
/// ⚠️ 稽核的 <c>OccurredAt</c> 在資料表是 UTC，這裡只做原值搬運；UTC → 本地的換算統一由 AuditLogQueryService 在對應之後處理。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class LogMappers
{
    public static partial TokenUsageLogAdapterModel ToModel(TokenUsageLog source);

    public static partial List<TokenUsageLogAdapterModel> ToModels(IEnumerable<TokenUsageLog> source);

    /// <summary>內文在內容檔，畫面只需要知道有沒有。</summary>
    [MapPropertyFromSource(nameof(AiCallLogAdapterModel.HasContent), Use = nameof(HasContent))]
    public static partial AiCallLogAdapterModel ToModel(AiCallLog source);

    public static partial List<AiCallLogAdapterModel> ToModels(IEnumerable<AiCallLog> source);

    public static partial ExceptionLogAdapterModel ToModel(ExceptionLog source);

    public static partial List<ExceptionLogAdapterModel> ToModels(IEnumerable<ExceptionLog> source);

    public static partial AuditLogAdapterModel ToModel(AuditLog source);

    public static partial List<AuditLogAdapterModel> ToModels(IEnumerable<AuditLog> source);

    public static partial JobRunAdapterModel ToModel(JobRun source);

    public static partial List<JobRunAdapterModel> ToModels(IEnumerable<JobRun> source);

    public static partial ScheduledJobStateAdapterModel ToModel(ScheduledJobState source);

    public static partial List<ScheduledJobStateAdapterModel> ToModels(IEnumerable<ScheduledJobState> source);

    [UserMapping(Default = false)]
    private static bool HasContent(AiCallLog source) => source.ContentFile != null;
}
