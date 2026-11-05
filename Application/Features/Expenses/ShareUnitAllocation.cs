using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Application.Features.Expenses.Dtos;

namespace Application.Features.Expenses
{
    public static class ShareUnitAllocation
    {
        public static List<ShareUnitDto>? Validate(
            IEnumerable<ShareUnitDto>? requested,
            IReadOnlyCollection<int> participants)
        {
            if (requested == null) return null;
            if (requested.Any(entry => entry == null))
                throw new InvalidOperationException("Pay bilgileri geçersiz.");
            var units = requested.Select(entry => new ShareUnitDto
            {
                UserId = entry.UserId,
                Units = entry.Units,
                ForLabel = string.IsNullOrWhiteSpace(entry.ForLabel) ? null : entry.ForLabel.Trim()
            }).OrderBy(entry => entry.UserId).ToList();
            if (units.Count == 0) return null;
            if (units.Select(entry => entry.UserId).Distinct().Count() != units.Count
                || units.Any(entry => !participants.Contains(entry.UserId)
                    || entry.Units < 1 || entry.Units > 50 || entry.ForLabel?.Length > 80)
                || units.Select(entry => entry.UserId).OrderBy(id => id)
                    .SequenceEqual(participants.OrderBy(id => id)) == false
                || units.Sum(entry => entry.Units) > 100
                || JsonSerializer.Serialize(units).Length > 2000)
                throw new InvalidOperationException("Pay sayıları seçilen üyelere ait olmalı ve toplam 100 payı aşmamalıdır.");
            return units;
        }

        public static List<(int UserId, decimal Amount)> Allocate(decimal amount, IReadOnlyList<ShareUnitDto> units)
        {
            if (amount < 0 || decimal.Round(amount, 2) != amount || units.Count == 0)
                throw new InvalidOperationException("Paylaştırılacak tutar geçersiz.");
            var totalCents = decimal.ToInt64(amount * 100m);
            var totalUnits = units.Sum(entry => entry.Units);
            if (totalUnits <= 0) throw new InvalidOperationException("En az bir pay gerekli.");

            var portions = units.Select(entry =>
            {
                var exact = (decimal)totalCents * entry.Units / totalUnits;
                var floor = decimal.ToInt64(decimal.Floor(exact));
                return (entry.UserId, Cents: floor, Fraction: exact - floor);
            }).ToList();
            var remainder = totalCents - portions.Sum(entry => entry.Cents);
            var extraIds = portions.OrderByDescending(entry => entry.Fraction)
                .ThenBy(entry => entry.UserId).Take((int)remainder)
                .Select(entry => entry.UserId).ToHashSet();
            return portions.OrderBy(entry => entry.UserId)
                .Select(entry => (entry.UserId, (entry.Cents + (extraIds.Contains(entry.UserId) ? 1 : 0)) / 100m))
                .ToList();
        }

        public static List<ShareUnitDto> ReadStored(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return JsonSerializer.Deserialize<List<ShareUnitDto>>(json) ?? new(); }
            catch (JsonException) { return new(); }
        }
    }
}
