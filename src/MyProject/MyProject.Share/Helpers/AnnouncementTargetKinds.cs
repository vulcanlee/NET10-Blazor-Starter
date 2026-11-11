namespace MyProject.AccessDatas.Models;

/// <summary>
/// 公告對象的種類（0.9.100 起）。0.10.6 起從 AccessDatas 搬到 Share（命名空間不變）：
/// WASM 的公告管理頁（只能參考 Dtos／Models／Share）與資料層共用同一份常數。
/// </summary>
public static class AnnouncementTargetKinds
{
    public const string All = "All";
    public const string Role = "Role";
    public const string Team = "Team";
}
