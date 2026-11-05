using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;
using System.Globalization;

namespace EvArkadasim.API.Services.Notifications;

public sealed class HouseActivityNotificationService
{
    private readonly AppDbContext _db;
    private readonly IPushNotificationService _push;
    private readonly ILogger<HouseActivityNotificationService> _logger;

    public HouseActivityNotificationService(
        AppDbContext db,
        IPushNotificationService push,
        ILogger<HouseActivityNotificationService> logger)
    {
        _db = db;
        _push = push;
        _logger = logger;
    }

    public async Task NotifyExpenseCreatedAsync(
        int houseId, int actorId, int expenseId, string expenseTitle, decimal amount)
    {
        try
        {
            var members = await _db.HouseMembers.AsNoTracking()
                .Where(member => member.HouseId == houseId && member.IsActive && member.User.IsActive)
                .Select(member => new { member.UserId, member.User.FirstName, member.User.LastName })
                .ToListAsync();
            var actor = members.FirstOrDefault(member => member.UserId == actorId);
            var actorName = actor is null
                ? "Bir üye"
                : $"{actor.FirstName} {actor.LastName}".Trim();
            var title = string.IsNullOrWhiteSpace(expenseTitle) ? "Harcama" : expenseTitle.Trim();
            if (title.Length > 64) title = title[..64];
            var amountTr = amount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"));
            var amountEn = amount.ToString("N2", CultureInfo.GetCultureInfo("en-US"));

            foreach (var member in members.Where(member => member.UserId != actorId))
            {
                await _push.SendAsync(
                    member.UserId,
                    "group",
                    "Yeni harcama",
                    $"{actorName}, {title} için {amountTr} TL harcama ekledi.",
                    "New expense",
                    $"{actorName} added {amountEn} TRY for {title}.",
                    data: new { route = "HarcamaDetayi", houseId, expenseId });
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Expense {ExpenseId} was saved but push notification failed", expenseId);
        }
    }
}
