using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// Covers ExpenseCategoryResolver.Resolve — the Thailand-wide default
/// expense-categorisation rules that bootstrap OCR account suggestions
/// before a tenant has any history of its own. Pure static logic.
/// </summary>
public class ExpenseCategoryResolverTests
{
    [Fact]
    public void Resolves_fuel_from_ptt_vendor_brand()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "บริษัท ปตท. จำกัด (มหาชน)",
            headerDescription: null,
            lineDescriptions: null,
            rawText: null);

        Assert.NotNull(result);
        Assert.Equal("ค่าน้ำมันเชื้อเพลิง", result!.Category);
        Assert.Equal("5402", result.AccountCode);
    }

    [Fact]
    public void Resolves_electricity_from_keyword_in_raw_text()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: null,
            headerDescription: null,
            lineDescriptions: null,
            rawText: "ใบแจ้งค่าไฟฟ้า การไฟฟ้านครหลวง");

        Assert.NotNull(result);
        Assert.Equal("5303", result!.AccountCode);
    }

    [Fact]
    public void Resolves_repair_with_statutory_withholding_tax()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: null,
            headerDescription: "ค่าซ่อมแซมเครื่องปรับอากาศ",
            lineDescriptions: null,
            rawText: null);

        Assert.NotNull(result);
        Assert.Equal("5306", result!.AccountCode);
        Assert.Equal(3m, result.StatutoryWhtRate);
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: null,
            headerDescription: "อออ",
            lineDescriptions: null,
            rawText: "บบบ");

        Assert.Null(result);
    }

    [Fact]
    public void Confidence_is_bounded_between_zero_and_one()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "ปตท.",
            headerDescription: null,
            lineDescriptions: null,
            rawText: null);

        Assert.NotNull(result);
        Assert.InRange(result!.Confidence, 0m, 1m);
    }
}
