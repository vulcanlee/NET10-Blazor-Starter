using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.AdapterModel;

public class TeamAdapterModel : ICloneable
{
    /// <summary>樂觀並行的版本號：開啟編輯時的值，存檔時用來比對資料是否已被別人修改（0.9.93 起）。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public int Id { get; set; }

    [Required(ErrorMessage = "團隊名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    public string Name { get; set; } = string.Empty;

    [StringLength(50, ErrorMessage = "代號長度不可超過 50 字元")]
    public string? Code { get; set; }

    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public TeamAdapterModel Clone()
    {
        return (TeamAdapterModel)((ICloneable)this).Clone();
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }
}
