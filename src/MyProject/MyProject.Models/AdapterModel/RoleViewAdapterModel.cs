using MyProject.Models.Admins;
using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.AdapterModel;

public class RoleViewAdapterModel : ICloneable
{
    public int Id { get; set; }
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = String.Empty;
    public string TabViewJson { get; set; }
    public RolePermission RolePermission { get; set; } = new();

    public RoleViewAdapterModel Clone()
    {
        return ((ICloneable)this).Clone() as RoleViewAdapterModel;
    }
    object ICloneable.Clone()
    {
        return this.MemberwiseClone();
    }
}
