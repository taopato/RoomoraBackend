namespace Domain.Entities;

public class PushDevice
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string ExpoPushToken { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = "tr";
    public string? DeviceName { get; set; }
    public bool PaymentEnabled { get; set; } = true;
    public bool BillEnabled { get; set; } = true;
    public bool GroupEnabled { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
