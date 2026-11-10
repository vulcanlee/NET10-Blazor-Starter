namespace MyProject.Web.Filters;

/// <summary>
/// 被要求先改密碼或先設定兩步驟驗證的使用者，仍可呼叫這個 API（0.10.0 起）。
/// 只給完成設定所需的端點使用（目前是 <c>me</c>、<c>logout</c>、<c>bridge</c>），其餘一律被 <see cref="ApiSessionGateFilter"/> 擋下。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowDuringForcedSetupAttribute : Attribute
{
}
