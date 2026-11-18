using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Services.Repositories;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Application.Services.PlannedExpenses
{
    public class PlannedExpenseLedgerSyncService : IPlannedExpenseLedgerSyncService
    {
        private readonly IExpenseRepository _expenseRepository;
        private readonly IHouseMemberRepository _houseMemberRepository;
        private readonly ILedgerLineRepository _ledgerLineRepository;

        public PlannedExpenseLedgerSyncService(
            IExpenseRepository expenseRepository,
            IHouseMemberRepository houseMemberRepository,
            ILedgerLineRepository ledgerLineRepository)
        {
            _expenseRepository = expenseRepository;
            _houseMemberRepository = houseMemberRepository;
            _ledgerLineRepository = ledgerLineRepository;
        }

        // Aynı ev için eş zamanlı senkron çift borç satırı üretmesin
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> HouseLocks = new();

        public async Task EnsureVisibleExpenseLedgersAsync(int houseId, CancellationToken ct = default)
        {
            var houseLock = HouseLocks.GetOrAdd(houseId, _ => new SemaphoreSlim(1, 1));
            await houseLock.WaitAsync(ct);
            try
            {
                await SyncHouseAsync(houseId, ct);
            }
            finally
            {
                houseLock.Release();
            }
        }

        private async Task SyncHouseAsync(int houseId, CancellationToken ct)
        {
            var nowUtc = DateTime.UtcNow;
            var visiblePlannedExpenses = await _expenseRepository.Query()
                .Include(expense => expense.Shares)
                .Include(expense => expense.PersonalExpenses)
                .Where(expense =>
                    expense.HouseId == houseId &&
                    expense.ParentExpenseId != null &&
                    expense.PostDate <= nowUtc &&
                    expense.OrtakHarcamaTutari > 0)
                .OrderBy(expense => expense.PostDate)
                .ToListAsync(ct);

            if (visiblePlannedExpenses.Count == 0)
            {
                return;
            }

            var activeParticipants = (await _houseMemberRepository.GetActiveUserIdsAsync(houseId, ct))
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            if (activeParticipants.Count == 0)
            {
                return;
            }

            foreach (var expense in visiblePlannedExpenses)
            {
                await EnsureExpenseLedgerAsync(expense, activeParticipants, ct);
            }
        }

        private async Task EnsureExpenseLedgerAsync(Expense expense, List<int> participants, CancellationToken ct)
        {
            var activeLines = await _ledgerLineRepository.GetListAsync(
                line => line.ExpenseId == expense.Id && line.IsActive,
                ct);

            if (activeLines.Any(line => line.PaidAmount > 0m || line.IsClosed))
            {
                return;
            }

            var expectedLines = BuildExpectedLines(expense, participants);

            if (HasSameOpenShape(activeLines, expectedLines))
            {
                return;
            }

            foreach (var line in activeLines)
            {
                line.IsActive = false;
                line.UpdatedAt = DateTime.UtcNow;
                await _ledgerLineRepository.UpdateAsync(line, ct);
            }

            if (expectedLines.Count > 0)
            {
                await _ledgerLineRepository.AddRangeAsync(expectedLines, ct);
            }

            await _ledgerLineRepository.SaveChangesAsync(ct);
        }

        private static bool HasSameOpenShape(List<LedgerLine> activeLines, List<LedgerLine> expectedLines)
        {
            if (activeLines.Count != expectedLines.Count)
            {
                return false;
            }

            var actual = activeLines
                .OrderBy(line => line.FromUserId)
                .ThenBy(line => line.ToUserId)
                .ThenBy(line => line.Amount - line.PaidAmount)
                .Select(line => $"{line.FromUserId}:{line.ToUserId}:{Math.Round(line.Amount - line.PaidAmount, 2):0.00}")
                .ToList();

            var expected = expectedLines
                .OrderBy(line => line.FromUserId)
                .ThenBy(line => line.ToUserId)
                .ThenBy(line => line.Amount)
                .Select(line => $"{line.FromUserId}:{line.ToUserId}:{Math.Round(line.Amount, 2):0.00}")
                .ToList();

            return actual.SequenceEqual(expected);
        }

        private static List<LedgerLine> BuildExpectedLines(Expense expense, List<int> participants)
        {
            var expectedLines = new List<LedgerLine>();
            if (participants.Count == 0 || expense.OrtakHarcamaTutari <= 0)
            {
                return expectedLines;
            }

            // Taksitte seçilen katılımcıların kayıtlı payları kullanılır; düzenli giderde mevcut davranış korunur
            var useStoredSplit = expense.InstallmentIndex != null && expense.Shares.Count > 0;
            var shares = useStoredSplit
                ? expense.Shares.Select(share => (share.UserId, Amount: share.PaylasimTutar))
                    .Concat(expense.PersonalExpenses.Select(personal => (personal.UserId, Amount: personal.Tutar)))
                    .ToList()
                : BuildEqualShares(expense.OrtakHarcamaTutari, participants);
            foreach (var share in shares)
            {
                if (share.UserId == expense.OdeyenUserId || share.Amount <= 0)
                {
                    continue;
                }

                expectedLines.Add(new LedgerLine
                {
                    HouseId = expense.HouseId,
                    ExpenseId = expense.Id,
                    FromUserId = share.UserId,
                    ToUserId = expense.OdeyenUserId,
                    Amount = share.Amount,
                    PostDate = expense.PostDate,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            return expectedLines;
        }

        private static List<(int UserId, decimal Amount)> BuildEqualShares(decimal total, List<int> participants)
        {
            var result = new List<(int UserId, decimal Amount)>();
            var uniqueParticipants = participants.Distinct().OrderBy(id => id).ToList();
            if (uniqueParticipants.Count == 0 || total <= 0)
            {
                return result;
            }

            var baseShare = Math.Round(total / uniqueParticipants.Count, 2, MidpointRounding.AwayFromZero);
            var diff = Math.Round(total - (baseShare * uniqueParticipants.Count), 2, MidpointRounding.AwayFromZero);

            for (var i = 0; i < uniqueParticipants.Count; i++)
            {
                result.Add((uniqueParticipants[i], baseShare + (i == 0 ? diff : 0m)));
            }

            return result;
        }
    }
}
