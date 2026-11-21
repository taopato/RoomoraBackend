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

    // Kullanıcının bütün aktif cihazlarına deneme bildirimi; her cihaz için teslim sonucu döner
    Task<IReadOnlyList<PushTestResult>> SendTestAsync(int userId, CancellationToken ct = default);
}

// Status: delivered (Apple/Google kabul etti), sent (sonuç henüz yok) ya da error (Error alanında Expo kodu)
public sealed record PushTestResult(string Platform, string? DeviceName, DateTime LastSeenAt, string Status, string? Error);
