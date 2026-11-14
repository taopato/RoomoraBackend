using EvArkadasim.API.Services.Receipts;

var upper = new ReceiptOcrResult
{
    StoreName = "Market",
    ReceiptDate = new DateTime(2026, 11, 14),
    ImageWidth = 1200,
    ImageHeight = 1800,
    TotalAmount = 50,
    Items =
    [
        new ReceiptParsedItem { Name = "Ekmek", LineTotal = 20 },
        new ReceiptParsedItem { Name = "Su", LineTotal = 30 },
    ],
};
var lower = new ReceiptOcrResult
{
    TotalAmount = 80,
    Items = [new ReceiptParsedItem { Name = "Makarna", LineTotal = 30 }],
};
var merged = ReceiptOcrParts.Merge(upper, lower);
Require(merged.TotalAmount == 80, "Upper subtotal must not replace final total");
Require(merged.Items.Select(item => item.Name).SequenceEqual(["Ekmek", "Su", "Makarna"]), "Items must stay in photo order");
Require(merged.StoreName == "Market" && merged.ReceiptDate == upper.ReceiptDate, "Receipt header must come from upper photo");
Require(merged.ImageWidth == 1200 && merged.ImageHeight == 1800, "Photo dimensions must describe the upper photo");

var lowerWithoutTotal = ReceiptOcrParts.Merge(upper, new ReceiptOcrResult
{
    Items = [new ReceiptParsedItem { Name = "Makarna", LineTotal = 30 }],
});
Require(lowerWithoutTotal.TotalAmount == 80, "Missing lower total must use item sum, not upper subtotal");

var synthetic = ReceiptOcrParts.Merge(
    new ReceiptOcrResult { Items = [new ReceiptParsedItem { Name = "Fiş Toplamı", LineTotal = 50 }] },
    lower);
Require(synthetic.Items.Count == 1 && synthetic.Items[0].Name == "Makarna", "Fallback total must not become a product");

Console.WriteLine("ReceiptPartsSmoke passed");

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
