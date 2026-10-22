namespace EvArkadasim.API.Services.Notifications;

public interface IPushNotificationService
{
    Task SendAsync(
        int userId,
        string category,
        string title,
        string body,
        string titleEn,
        string bodyEn,
        object? data = null,
        CancellationToken ct = default);
}
