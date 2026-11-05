using Application.Features.Expenses;
using Application.Features.Expenses.Dtos;

var units = ShareUnitAllocation.Validate(new[]
{
    new ShareUnitDto { UserId = 1, Units = 1 },
    new ShareUnitDto { UserId = 2, Units = 2, ForLabel = "Sedef icin" },
    new ShareUnitDto { UserId = 3, Units = 1 }
}, new[] { 1, 2, 3 })!;
var split = ShareUnitAllocation.Allocate(400m, units);
Assert(split.SequenceEqual(new[] { (1, 100m), (2, 200m), (3, 100m) }), "4 pay / 3 uye");
Assert(ShareUnitAllocation.Allocate(99.99m, units).Sum(entry => entry.Amount) == 99.99m, "kurus kaybi");
Assert(ShareUnitAllocation.ReadStored(System.Text.Json.JsonSerializer.Serialize(units))[1].ForLabel == "Sedef icin", "etiket kaydi");
Assert(ShareUnitAllocation.Validate(Array.Empty<ShareUnitDto>(), new[] { 1, 2, 3 }) is null, "esit varsayilan");
try
{
    ShareUnitAllocation.Validate(new[] { new ShareUnitDto { UserId = 1, Units = 2 } }, new[] { 1, 2 });
    throw new Exception("eksik uye reddedilmedi");
}
catch (InvalidOperationException) { }
Console.WriteLine("Share allocation smoke tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
