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

const string quantityReceipt = "DAYCARE %20 *54.50\n2 ad X 43.25\nSUT %1 *86.50\n2 ad X 1.00\nPOSET %20 *2.00";
Require(!ReceiptQuantityValidation.Matches(quantityReceipt, [(1m, 54.50m), (1m, 86.50m), (1m, 2m)]),
    "Equal paid totals must not hide wrong quantities");
Require(ReceiptQuantityValidation.Matches(quantityReceipt, [(1m, 54.50m), (2m, 86.50m), (2m, 2m)]),
    "Quantity rows should belong to the following priced products");

Console.WriteLine("ReceiptPartsSmoke passed");

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
