using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Application.Features.Expenses.Dtos;
using Application.Features.Expenses;
using Application.Services.Repositories;
using Domain.Entities;
using Domain.Enums;
using MediatR;

namespace Application.Features.Expenses.Commands.CreateExpense
{
    public class CreateExpenseCommandHandler
        : IRequestHandler<CreateExpenseCommand, CreatedExpenseResponseDto>
    {
        private const int RECURRING_HORIZON_MONTHS = 12;

        private readonly IExpenseRepository _expenseRepository;
        private readonly IHouseMemberRepository _houseMemberRepo;
        private readonly ILedgerLineRepository _ledgerRepo;
        private readonly IShareRepository _shareRepository;

        public CreateExpenseCommandHandler(
            IExpenseRepository expenseRepository,
            IHouseMemberRepository houseMemberRepo,
            ILedgerLineRepository ledgerRepo,
            IShareRepository shareRepository)
        {
            _expenseRepository = expenseRepository;
            _houseMemberRepo = houseMemberRepo;
            _ledgerRepo = ledgerRepo;
            _shareRepository = shareRepository;
        }

        public async Task<CreatedExpenseResponseDto> Handle(CreateExpenseCommand request, CancellationToken cancellationToken)
        {
            var mode = (request.Mode ?? "").Trim().ToLowerInvariant();

            if (mode == "recurring")
                return await HandleRecurringAsync(request, cancellationToken);

            if (mode == "installment" || (request.InstallmentCount ?? 0) > 1)
                return await HandleInstallmentAsync(request, cancellationToken);

            return await HandleSingleAsync(request, cancellationToken);
        }

        private async Task<CreatedExpenseResponseDto> HandleSingleAsync(CreateExpenseCommand request, CancellationToken ct)
        {
            var personalItems = NormalizePersonalItems(request.SahsiHarcamalar);
            var personalTotal = personalItems.Sum(x => x.Tutar);
            var sharedAmount = request.OrtakHarcamaTutari > 0
                ? request.OrtakHarcamaTutari
                : Math.Max(0m, request.Tutar - personalTotal);

            var whenUtc = request.PostDate.HasValue
                ? request.PostDate.Value.ToUniversalTime()
                : request.Date == default ? DateTime.UtcNow : request.Date.ToUniversalTime();
            var entity = BuildBaseExpense(request, ResolveTitle(request), ResolveCategoryNonNull(request), whenUtc);
            entity.DueDate = request.DueDate?.ToUniversalTime() ?? whenUtc;
            entity.Tutar = request.Tutar;
            entity.OrtakHarcamaTutari = sharedAmount;
            entity.Type = ExpenseType.Irregular;
            entity.SplitPolicy = personalItems.Count > 0 ? PaylasimTuru.KisiBazli : PaylasimTuru.Esit;
            entity.Note = request.Note ?? request.Aciklama ?? request.Description;

            foreach (var personal in personalItems)
            {
                entity.PersonalExpenses.Add(new PersonalExpense
                {
                    UserId = personal.UserId,
                    Tutar = personal.Tutar
                });
            }

            var activeMembers = (await _houseMemberRepo.GetActiveUserIdsAsync(request.HouseId, ct))
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            if (activeMembers.Count == 0)
                throw new InvalidOperationException("Aktif ev üyesi bulunamadı.");

            var requestedParticipants = (request.Participants ?? new List<int>())
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (requestedParticipants.Any(id => !activeMembers.Contains(id)))
                throw new InvalidOperationException("Seçilen katılımcılardan biri bu evin aktif üyesi değil.");

            var participants = requestedParticipants.Count > 0
                ? requestedParticipants
                : activeMembers;

            if (personalItems.Any(item => !participants.Contains(item.UserId)))
                throw new InvalidOperationException("Kişisel kalem yalnızca seçilen katılımcılara eklenebilir.");

            var shareUnits = ShareUnitAllocation.Validate(request.ShareUnits, participants);
            var sharedShares = shareUnits == null
                ? BuildEqualShares(sharedAmount, participants)
                : ShareUnitAllocation.Allocate(sharedAmount, shareUnits);
            entity.ParticipantsJson = shareUnits == null ? null : JsonSerializer.Serialize(shareUnits);
            entity.SplitPolicy = shareUnits != null ? PaylasimTuru.Agirlikli
                : personalItems.Count > 0 ? PaylasimTuru.KisiBazli : PaylasimTuru.Esit;

            var created = await _expenseRepository.AddAsync(entity);
            await _expenseRepository.SaveChangesAsync();

            var shareEntities = sharedShares
                .Select(share => new Share
                {
                    ExpenseId = created.Id,
                    UserId = share.UserId,
                    PaylasimTutar = share.Amount,
                    PaylasimTuru = shareUnits == null ? PaylasimTuru.Esit : PaylasimTuru.Agirlikli,
                    Date = whenUtc
                })
                .ToList();

            if (shareEntities.Count > 0)
            {
                await _shareRepository.AddRangeAsync(shareEntities);
                await _shareRepository.SaveChangesAsync();
            }

            var ledgerLines = BuildLedgerLinesForExpense(created, request.OdeyenUserId, sharedShares, personalItems);
            if (ledgerLines.Count > 0)
            {
                await _ledgerRepo.AddRangeAsync(ledgerLines, ct);
                await _ledgerRepo.SaveChangesAsync(ct);
            }

            return ToResponse(created, participants.Count, false, created.CreatedDate);
        }

        private static List<PersonalExpenseDto> NormalizePersonalItems(IEnumerable<PersonalExpenseDto>? items)
        {
            return (items ?? Enumerable.Empty<PersonalExpenseDto>())
                .Where(x => x != null && x.UserId > 0 && x.Tutar > 0)
                .GroupBy(x => x.UserId)
                .Select(g => new PersonalExpenseDto
                {
                    UserId = g.Key,
                    Tutar = g.Sum(x => x.Tutar)
                })
                .OrderBy(x => x.UserId)
                .ToList();
        }

        private static List<(int UserId, decimal Amount)> BuildEqualShares(decimal total, List<int> participants)
        {
            var shares = new List<(int UserId, decimal Amount)>();
            if (total <= 0 || participants.Count == 0) return shares;

            var ordered = participants.Distinct().OrderBy(x => x).ToList();
            var count = ordered.Count;
            var baseShare = Math.Round(total / count, 2, MidpointRounding.AwayFromZero);
            var diff = Math.Round(total - (baseShare * count), 2, MidpointRounding.AwayFromZero);

            for (var i = 0; i < ordered.Count; i++)
            {
                var share = baseShare + (i == 0 ? diff : 0m);
                shares.Add((ordered[i], share));
            }

            return shares;
        }

        private static List<LedgerLine> BuildLedgerLinesForExpense(
            Expense expense,
            int payerUserId,
            IReadOnlyList<(int UserId, decimal Amount)> sharedShares,
            List<PersonalExpenseDto> personalItems)
        {
            var result = new List<LedgerLine>();
            foreach (var kvp in sharedShares)
            {
                if (kvp.UserId == payerUserId || kvp.Amount <= 0) continue;

                result.Add(new LedgerLine
                {
                    HouseId = expense.HouseId,
                    ExpenseId = expense.Id,
                    FromUserId = kvp.UserId,
                    ToUserId = payerUserId,
                    Amount = kvp.Amount,
                    PostDate = expense.PostDate,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }

            foreach (var personal in personalItems)
            {
                if (personal.UserId == payerUserId || personal.Tutar <= 0) continue;

                result.Add(new LedgerLine
                {
                    HouseId = expense.HouseId,
                    ExpenseId = expense.Id,
                    FromUserId = personal.UserId,
                    ToUserId = payerUserId,
                    Amount = personal.Tutar,
                    PostDate = expense.PostDate,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }

            return result;
        }

        // --- Helpers ---
        private static DateTime FirstDayUtc(DateTime dtUtc)
            => new DateTime(dtUtc.Year, dtUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        private static DateTime MonthWithDueDayUtc(DateTime monthFirstUtc, byte dueDay)
        {
            var day = Math.Clamp((int)dueDay, 1, 28);
            return new DateTime(monthFirstUtc.Year, monthFirstUtc.Month, day, 0, 0, 0, DateTimeKind.Utc);
        }

        private static DateTime ResolveVisibilityStartUtc(DateTime dueDateUtc, short preShareDays)
            => dueDateUtc.AddDays(-Math.Max(0, (int)preShareDays));

        private Expense BuildBaseExpense(CreateExpenseCommand request, string title, ExpenseCategory category, DateTime whenUtc)
        {
            var description = request.Aciklama ?? request.Note ?? request.Description;
            var payerUserId = request.OdeyenUserId > 0 ? request.OdeyenUserId : request.KaydedenUserId;
            var creatorUserId = request.KaydedenUserId > 0 ? request.KaydedenUserId : payerUserId;

            return new Expense
            {
                Tur = title,
                HouseId = request.HouseId,
                OdeyenUserId = payerUserId,
                KaydedenUserId = creatorUserId,
                CreatedDate = whenUtc,
                IsActive = true,
                Description = description ?? string.Empty,
                Note = request.Note ?? request.Aciklama ?? request.Description,
                PostDate = whenUtc,
                DueDate = whenUtc,
                PeriodMonth = $"{whenUtc:yyyy-MM}",
                VisibilityMode = VisibilityMode.OnBillDate,
                Category = category
            };
        }

        private static ExpenseCategory ResolveCategoryNonNull(CreateExpenseCommand req)
        {
            if (req.Category.HasValue) return req.Category.Value;

            if (req.CategoryId.HasValue)
            {
                try { return (ExpenseCategory)req.CategoryId.Value; }
                catch { }
            }

            var text = (req.Tur ?? string.Empty).ToLowerInvariant();

            if (text.Contains("kira") || text.Contains("rent"))
                if (TryParseAny(out var rent, "Rent", "Kira")) return rent;

            if (text.Contains("elektrik") || text.Contains("electric"))
                if (TryParseAny(out var elec, "Electricity", "Elektrik", "Electric")) return elec;

            if (text.Contains(" water") || text.Contains(" su") || text.StartsWith("su") || text == "su" || text.Contains("water"))
                if (TryParseAny(out var water, "Water", "Su")) return water;

            if (text.Contains("internet"))
                if (TryParseAny(out var net, "Internet", "İnternet")) return net;

            if (text.Contains("gas") || text.Contains("doğalgaz") || text.Contains("dogalgaz"))
                if (TryParseAny(out var gas, "Gas", "Dogalgaz", "Doğalgaz")) return gas;

            if (text.Contains("market"))
                if (TryParseAny(out var market, "Market")) return market;

            if (text.Contains("yemek") || text.Contains("food"))
                if (TryParseAny(out var food, "Food", "Yemek")) return food;

            if (TryParseAny(out var other, "Other", "Diger", "Diğer")) return other;

            return ExpenseCategory.Other;
        }

        private static bool TryParseAny(out ExpenseCategory value, params string[] names)
        {
            foreach (var n in names)
            {
                if (Enum.TryParse(n, true, out value))
                    return true;
            }

            value = default;
            return false;
        }

        private static string ResolveTitle(CreateExpenseCommand req)
        {
            if (!string.IsNullOrWhiteSpace(req.Tur))
                return req.Tur.Trim();

            byte? catId = null;
            if (req.Category.HasValue) catId = (byte)req.Category.Value;
            else if (req.CategoryId.HasValue) catId = (byte)req.CategoryId.Value;

            return catId switch
            {
                0 => "Kira",
                1 => "İnternet",
                2 => "Elektrik",
                3 => "Su",
                4 => "Market",
                5 => "Yemek",
                99 => "Diğer",
                _ => "Diğer"
            };
        }

        private async Task<CreatedExpenseResponseDto> HandleInstallmentAsync(CreateExpenseCommand request, CancellationToken ct)
        {
            var total = request.Tutar;
            var count = Math.Max(1, request.InstallmentCount ?? 1);
            var dueDay = (byte)Math.Clamp((int)(request.DueDay ?? 1), 1, 28);
            const short preShareDays = 5;

            var startBase = request.StartMonth?.ToUniversalTime()
                         ?? (request.Date == default ? DateTime.UtcNow : request.Date.ToUniversalTime());
            var startMonthUtc = FirstDayUtc(startBase);
            var cardholderId = request.CardholderUserId ?? request.OdeyenUserId;

            var cat = ResolveCategoryNonNull(request);
            var title = ResolveTitle(request);

            List<int> participants;
            if (request.Participants != null && request.Participants.Count > 0)
                participants = request.Participants.Distinct().OrderBy(x => x).ToList();
            else
                participants = (await _houseMemberRepo.GetActiveUserIdsAsync(request.HouseId, ct))?.Distinct().OrderBy(x => x).ToList() ?? new();

            if (participants.Count == 0)
                throw new InvalidOperationException("Aktif ev üyesi bulunamadı.");

            var monthly = Math.Round(total / count, 2, MidpointRounding.AwayFromZero);

            var parentPostDate = MonthWithDueDayUtc(startMonthUtc, dueDay);
            var parent = BuildBaseExpense(request, string.IsNullOrWhiteSpace(title) ? "Taksit Planı" : $"{title} (Taksit Planı)", cat, parentPostDate);
            parent.Tutar = total;
            parent.OrtakHarcamaTutari = 0m;
            parent.DueDay = dueDay;
            parent.PlanStartMonth = startMonthUtc;
            parent.Type = ExpenseType.Regular;
            parent.InstallmentCount = count;

            parent = await _expenseRepository.AddAsync(parent);
            await _expenseRepository.SaveChangesAsync();

            var childExpenses = new List<Expense>(count);
            for (int i = 0; i < count; i++)
            {
                var monthFirst = FirstDayUtc(startMonthUtc.AddMonths(i));
                var dueDate = MonthWithDueDayUtc(monthFirst, dueDay);
                var child = BuildBaseExpense(request, $"{title} taksit {i + 1}/{count}", cat, dueDate);
                child.ParentExpenseId = parent.Id;
                child.Tutar = monthly;
                child.OrtakHarcamaTutari = monthly;
                child.DueDay = dueDay;
                child.PlanStartMonth = startMonthUtc;
                child.InstallmentIndex = i + 1;
                child.InstallmentCount = count;
                child.Type = ExpenseType.Regular;
                child.DueDate = dueDate;
                child.VisibilityMode = VisibilityMode.BeforeDueByDays;
                child.PreShareDays = preShareDays;
                child.PostDate = ResolveVisibilityStartUtc(dueDate, preShareDays);

                child = await _expenseRepository.AddAsync(child);
                childExpenses.Add(child);
            }

            await _expenseRepository.SaveChangesAsync();

            var installmentShares = childExpenses
                .SelectMany(child => BuildEqualShares(child.OrtakHarcamaTutari, participants)
                    .Select(share => new Share
                    {
                        ExpenseId = child.Id,
                        UserId = share.UserId,
                        PaylasimTutar = share.Amount,
                        PaylasimTuru = PaylasimTuru.Esit,
                        Date = child.DueDate ?? child.CreatedDate
                    }))
                .ToList();

            if (installmentShares.Any())
            {
                await _shareRepository.AddRangeAsync(installmentShares);
                await _shareRepository.SaveChangesAsync();
            }

            var firstLedgerPostDate = ResolveVisibilityStartUtc(MonthWithDueDayUtc(startMonthUtc, dueDay), preShareDays);
            return ToResponse(parent, participants.Count, firstLedgerPostDate > DateTime.UtcNow, firstLedgerPostDate);
        }

        private async Task<CreatedExpenseResponseDto> HandleRecurringAsync(CreateExpenseCommand request, CancellationToken ct)
        {
            var monthlyAmount = request.Tutar;
            var dueDay = (byte)Math.Clamp((int)(request.DueDay ?? 1), 1, 28);
            const short preShareDays = 5;

            var startBase = request.StartMonth?.ToUniversalTime()
                         ?? (request.Date == default ? DateTime.UtcNow : request.Date.ToUniversalTime());
            var startMonthUtc = FirstDayUtc(startBase);

            var cat = ResolveCategoryNonNull(request);
            var title = ResolveTitle(request);

            var participants = (await _houseMemberRepo.GetActiveUserIdsAsync(request.HouseId, ct))?.Distinct().OrderBy(x => x).ToList() ?? new();
            if (participants.Count == 0)
                throw new InvalidOperationException("Aktif ev üyesi bulunamadı.");

            var parentPostDate = MonthWithDueDayUtc(startMonthUtc, dueDay);
            var parent = BuildBaseExpense(request, string.IsNullOrWhiteSpace(title) ? "Düzenli Gider Planı" : $"{title} (Plan)", cat, parentPostDate);
            parent.Tutar = monthlyAmount * RECURRING_HORIZON_MONTHS;
            parent.OrtakHarcamaTutari = 0m;
            parent.DueDay = dueDay;
            parent.PlanStartMonth = startMonthUtc;
            parent.Type = ExpenseType.Regular;

            parent = await _expenseRepository.AddAsync(parent);
            await _expenseRepository.SaveChangesAsync();

            var childExpenses = new List<Expense>(RECURRING_HORIZON_MONTHS);
            for (int i = 0; i < RECURRING_HORIZON_MONTHS; i++)
            {
                var monthFirst = FirstDayUtc(startMonthUtc.AddMonths(i));
                var dueDate = MonthWithDueDayUtc(monthFirst, dueDay);
                var child = BuildBaseExpense(request, title, cat, dueDate);
                child.ParentExpenseId = parent.Id;
                child.Tutar = monthlyAmount;
                child.OrtakHarcamaTutari = monthlyAmount;
                child.DueDay = dueDay;
                child.PlanStartMonth = startMonthUtc;
                child.Type = ExpenseType.Regular;
                child.DueDate = dueDate;
                child.VisibilityMode = VisibilityMode.BeforeDueByDays;
                child.PreShareDays = preShareDays;
                child.PostDate = ResolveVisibilityStartUtc(dueDate, preShareDays);

                child = await _expenseRepository.AddAsync(child);
                childExpenses.Add(child);
            }

            await _expenseRepository.SaveChangesAsync();

            var recurringShares = childExpenses
                .SelectMany(child => BuildEqualShares(child.OrtakHarcamaTutari, participants)
                    .Select(share => new Share
                    {
                        ExpenseId = child.Id,
                        UserId = share.UserId,
                        PaylasimTutar = share.Amount,
                        PaylasimTuru = PaylasimTuru.Esit,
                        Date = child.DueDate ?? child.CreatedDate
                    }))
                .ToList();

            if (recurringShares.Any())
            {
                await _shareRepository.AddRangeAsync(recurringShares);
                await _shareRepository.SaveChangesAsync();
            }

            var firstLedgerPostDate = ResolveVisibilityStartUtc(MonthWithDueDayUtc(startMonthUtc, dueDay), preShareDays);
            return ToResponse(parent, participants.Count, firstLedgerPostDate > DateTime.UtcNow, firstLedgerPostDate);
        }

        private static CreatedExpenseResponseDto ToResponse(
            Expense created,
            int activeParticipantCount,
            bool ledgerStartsInFuture,
            DateTime? firstLedgerPostDate)
        {
            return new CreatedExpenseResponseDto
            {
                Id = created.Id,
                HouseId = created.HouseId,
                OdeyenUserId = created.OdeyenUserId,
                KaydedenUserId = created.KaydedenUserId,
                Tutar = created.Tutar,
                OrtakHarcamaTutari = created.OrtakHarcamaTutari,
                Tur = created.Tur,
                KayitTarihi = created.CreatedDate,
                ParentExpenseId = created.ParentExpenseId,
                InstallmentIndex = created.InstallmentIndex,
                InstallmentCount = created.InstallmentCount,
                PlanStartMonth = created.PlanStartMonth,
                DueDay = created.DueDay,
                ActiveParticipantCount = activeParticipantCount,
                LedgerStartsInFuture = ledgerStartsInFuture,
                FirstLedgerPostDate = firstLedgerPostDate
            };
        }
    }
}
