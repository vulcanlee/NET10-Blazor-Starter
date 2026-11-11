namespace MyProject.Dtos.Commons;

/// <summary>清單上點狀態標籤切換啟用／停用（0.10.2 起，<c>PUT {id}/enabled</c>）：要切換成的狀態（重複送同一個值不會反轉）。</summary>
public class SetEnabledRequestDto
{
    public bool Enabled { get; set; }
}
