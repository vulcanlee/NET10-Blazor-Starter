using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

/// <summary>
/// 使用者
/// </summary>
public class MyUser
{
    public MyUser()
    {
    }
    public int Id { get; set; }
    [Required(ErrorMessage = "帳號 不可為空白")]
    public string Account { get; set; } = String.Empty;
    [Required(ErrorMessage = "密碼 不可為空白")]
    public string Password { get; set; } = String.Empty;
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = String.Empty;
    public string? Salt { get; set; }
    public bool Status { get; set; } = true;
    public string? Email { get; set; }
    public bool IsAdmin { get; set; } = false;
    public DateTime CreateAt { get; set; }= DateTime.Now;
    public int? RoleViewId { get; set; }
    public DateTime UpdateAt { get; set; }=DateTime.Now;
    /// <summary>
    /// 外部身分驗證提供者，例如 "Google"；本地帳號為 null 或 "Local"
    /// </summary>
    public string? OAuthProvider { get; set; }
    /// <summary>
    /// 外部身分驗證的使用者唯一識別碼（Google 的 sub）
    /// </summary>
    public string? GoogleId { get; set; }
    /// <summary>
    /// 連續登入失敗次數；成功登入後歸零。
    /// </summary>
    public int AccessFailedCount { get; set; } = 0;
    /// <summary>
    /// 帳號鎖定截止時間（UTC）；為 null 或已過期表示未鎖定。
    /// </summary>
    public DateTime? LockoutEndUtc { get; set; }
    public RoleView? RoleView { get; set; }
}
