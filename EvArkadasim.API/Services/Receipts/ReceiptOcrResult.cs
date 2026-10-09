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
}
