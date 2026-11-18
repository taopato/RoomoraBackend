using System.Globalization;
using System.Text.RegularExpressions;

namespace EvArkadasim.API.Services.Receipts;

public static class ReceiptQuantityValidation
{
    private static readonly Regex QuantityRow = new(
        @"^\s*(?<quantity>\d+(?:[.,]\d{1,3})?)\s*(?:ad(?:et)?|kg|gr|g)\s*[x×]\s*(?<unit>\d+(?:[.,]\d{2}))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProductAmount = new(
        @"\*\s*(?<amount>(?:\d{1,3}(?:[ .]\d{3})+|\d+)[.,]\d{2})",
        RegexOptions.Compiled);

    public static bool Matches(string? rawText, IEnumerable<(decimal Quantity, decimal LineTotal)> items)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return true;
        var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var available = items.ToList();
        for (var index = 0; index < lines.Length - 1; index++)
        {
            var match = QuantityRow.Match(lines[index]);
            if (!match.Success || !TryAmount(match.Groups["quantity"].Value, out var quantity)
                || !TryAmount(match.Groups["unit"].Value, out var unit)
                || quantity <= 0 || quantity > 1000 || unit <= 0) continue;

            for (var next = index + 1; next < Math.Min(index + 3, lines.Length); next++)
            {
                var amounts = ProductAmount.Matches(lines[next]);
                if (amounts.Count == 0 || !TryAmount(amounts[^1].Groups["amount"].Value, out var lineTotal)) continue;
                if (Math.Abs(quantity * unit - lineTotal) > 0.02m) break;
                var itemIndex = available.FindIndex(item =>
                    Math.Abs(item.LineTotal - lineTotal) <= 0.02m && Math.Abs(item.Quantity - quantity) <= 0.001m);
                // İndirim uygulanmış kalem basılı satır tutarından düşük olabilir; adet yine birebir eşleşmeli
                if (itemIndex < 0)
                    itemIndex = available.FindIndex(item =>
                        item.LineTotal > 0 && item.LineTotal <= lineTotal + 0.02m && Math.Abs(item.Quantity - quantity) <= 0.001m);
                if (itemIndex < 0) return false;
                available.RemoveAt(itemIndex);
                break;
            }
        }
        return true;
    }

    private static bool TryAmount(string text, out decimal amount)
    {
        var normalized = text.Replace(" ", "");
        var separator = normalized.LastIndexOfAny([',', '.']);
        if (separator >= 0 && normalized.Length - separator - 1 == 2)
            normalized = normalized[..separator].Replace(",", "").Replace(".", "") + "." + normalized[(separator + 1)..];
        else
            normalized = normalized.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }
}
