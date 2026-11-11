namespace MyProject.Dtos.Models;

/// <summary>可選為上層的團隊（0.10.2 起，<c>GET api/v1/Team/{id}/parent-candidates</c>）。</summary>
public class TeamNodeDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? ParentId { get; set; }
}
