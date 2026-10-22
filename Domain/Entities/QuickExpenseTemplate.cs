namespace Domain.Entities;

public class QuickExpenseTemplate
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }
    public int HouseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int CategoryId { get; set; }
    public string? Description { get; set; }
    public int PayerUserId { get; set; }
    public string ParticipantUserIdsJson { get; set; } = "[]";
    public string PersonalAmountsJson { get; set; } = "{}";
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
