using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

/// <summary>
/// 使用者
/// </summary>
public class RoleView
{
    public RoleView()
    {
    }
    public int Id { get; set; }
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; }
    [Required(ErrorMessage = "頁面可視權限 Json 不可為空白")]
    public string TabViewJson { get; set; }
}
