namespace MyProject.Web.Filters;

/// <summary>
/// 被要求先改密碼或先設定兩步驟驗證的使用者，仍可呼叫這個 API（0.10.0 起）。
/// 只給完成設定所需的端點使用，其餘一律被 <see cref="ApiSessionGateFilter"/> 擋下。目前是：<c>me</c>、<c>logout</c>、改密碼與兩步驟驗證設定，以及外框需要的 shell、通知、公告端點（0.10.8 起，bridge 已移除）；
/// 0.10.8 起加上變更密碼、兩步驟驗證設定（兩頁搬到 WASM），以及那兩頁的外框要用的 <c>shell/*</c>、通知鈴鐺、公告橫幅（只涉及自己的資料）。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowDuringForcedSetupAttribute : Attribute
{
}
