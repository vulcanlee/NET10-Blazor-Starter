namespace MyProject.Business.Services.Other;

/// <summary>
/// 解析使用者的有效團隊清單（用於列級權控）。
/// 有效團隊 = 直接綁在使用者的 UserTeam ∪ 其角色的 DefaultTeamsJson（聯集、去重）。
/// 以聯集方式相容「團隊綁角色」與「團隊綁使用者」兩種來源。
/// </summary>
public interface IEffectiveTeamResolver
{
    Task<IReadOnlyList<string>> GetEffectiveTeamNamesAsync(int userId);

    /// <summary>
    /// 反向查詢：有效團隊包含 <paramref name="teamName"/> 的使用者 Id（0.9.100 起，發通知給整個團隊用）。
    /// 與 <see cref="GetEffectiveTeamNamesAsync"/> 同一個定義（測試以兩邊互相對照）：使用者 u 在結果裡 ⇔ 團隊名稱在 u 的有效團隊裡。
    /// </summary>
    Task<IReadOnlyList<int>> GetUserIdsInTeamAsync(string teamName);
}
