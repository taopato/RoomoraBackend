namespace EvArkadasim.API.Services.Receipts
{
    public class ReceiptOcrResult
    {
        public string RawText { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public DateTime? ReceiptDate { get; set; }
        public decimal? TotalAmount { get; set; }
        public int? ImageWidth { get; set; }
        public int? ImageHeight { get; set; }
        public List<ReceiptParsedItem> Items { get; set; } = new();
    }

    public class ReceiptParsedItem
    {
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal LineTotal { get; set; }
        public int? BoxLeft { get; set; }
        public int? BoxTop { get; set; }
        public int? BoxWidth { get; set; }
        public int? BoxHeight { get; set; }
    }

    public static class ReceiptOcrParts
    {
        public static ReceiptOcrResult Merge(ReceiptOcrResult upper, ReceiptOcrResult lower)
        {
            var items = upper.Items.Concat(lower.Items)
                .Where(item => !IsAggregateItem(item.Name))
                .ToList();
            return new ReceiptOcrResult
            {
                RawText = $"{upper.RawText}\n{lower.RawText}".Trim(),
                StoreName = upper.StoreName ?? lower.StoreName,
                ReceiptDate = upper.ReceiptDate ?? lower.ReceiptDate,
                ImageWidth = upper.ImageWidth,
                ImageHeight = upper.ImageHeight,
                // The upper photo can contain a subtotal, not the final receipt total.
                TotalAmount = lower.TotalAmount ?? (items.Count > 0 ? items.Sum(item => item.LineTotal) : null),
                Items = items,
            };
        }

        // OCR servisi "Fis Toplami", .NET yedeği "Fiş Toplamı" üretir; ikisi de ürün değildir
        public static bool IsAggregateItem(string? name)
        {
            var value = name?.Trim() ?? string.Empty;
            return value.StartsWith("Fis Toplami", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Fiş Toplamı", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Ortak Kalan Tutar", StringComparison.OrdinalIgnoreCase);
        }
    }
}
