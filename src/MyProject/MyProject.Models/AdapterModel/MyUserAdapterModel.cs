using MyProject.Models.Admins;
using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.AdapterModel;

public class MyUserAdapterModel : ICloneable
{
    public int Id { get; set; }
    [Required(ErrorMessage = "帳號 不可為空白")]
    public string Account { get; set; } = String.Empty;
    public string Password { get; set; } = String.Empty;
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = String.Empty;
    public string? Salt { get; set; }
    public bool Status { get; set; } = true;
    public string? Email { get; set; }
    public bool IsAdmin { get; set; } = false;
    public DateTime CreateAt { get; set; } = DateTime.Now;
    public DateTime UpdateAt { get; set; } = DateTime.Now;
    [Required(ErrorMessage = "角色 不可為空白")]
    public int? RoleViewId { get; set; }
    public RoleViewAdapterModel? RoleView { get; set; }
    public string RoleViewName => RoleView?.Name ?? string.Empty;
    public string StatusText => Status ? "啟用" : "停用";
    public string IsAdminText => IsAdmin ? "是" : "否";

    public MyUserAdapterModel Clone()
    {
        return ((ICloneable)this).Clone() as MyUserAdapterModel;
    }
    object ICloneable.Clone()
    {
        return this.MemberwiseClone();
    }
}
