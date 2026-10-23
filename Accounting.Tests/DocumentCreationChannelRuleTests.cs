using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวตัดสิน "เอกสารมาจากช่องทางไหน" ของแท็บตรวจสอบการใช้งาน (แอดมินแพลตฟอร์ม) —
/// ล็อกลำดับความเชื่อถือ (OriginModule → ตารางผูกกลับ → ป้าย CreatedBy → รายการประจำ → System → ผู้ใช้)
/// และทิศตรงข้าม: ไม่มีหลักฐาน = "ไม่ทราบ" (ห้ามเดาเป็น "หน้าจอ")
/// </summary>
public class DocumentCreationChannelRuleTests
{
    private static DocumentCreationChannel C(string? origin, string? marker,
        bool ocr = false, bool pos = false, bool sf = false, bool api = false, bool rec = false)
        => DocumentCreationChannelRule.Classify(new DocumentOriginProbe(origin, marker, ocr, pos, sf, api, rec));

    [Theory]
    [InlineData("Import", DocumentCreationChannel.Import)]
    [InlineData("Lodging", DocumentCreationChannel.Lodging)]
    [InlineData("lodging", DocumentCreationChannel.Lodging)]
    [InlineData("Pos", DocumentCreationChannel.Pos)]
    [InlineData("Ocr", DocumentCreationChannel.Ocr)]
    public void OriginModule_ชนะหลักฐานอื่นทั้งหมด(string origin, DocumentCreationChannel expected)
    {
        // ใบนำเข้าที่บังเอิญมี mark ของ API/ลิงก์ OCR ยังเป็นช่องทางที่โมดูลประทับเอง
        Assert.Equal(expected, C(origin, DocumentCreationChannelRule.MarkerApi, ocr: true, api: true));
    }

    [Fact]
    public void ตารางผูกกลับ_ชนะป้ายผู้ใช้()
    {
        Assert.Equal(DocumentCreationChannel.Pos, C(null, DocumentCreationChannelRule.MarkerUser, pos: true));
        Assert.Equal(DocumentCreationChannel.Ocr, C(null, DocumentCreationChannelRule.MarkerUser, ocr: true));
        Assert.Equal(DocumentCreationChannel.Storefront, C(null, DocumentCreationChannelRule.MarkerUser, sf: true));
        Assert.Equal(DocumentCreationChannel.Api, C(null, DocumentCreationChannelRule.MarkerUser, api: true));
    }

    [Fact]
    public void ลำดับตารางผูกกลับ_POS_ก่อน_OCR()
    {
        Assert.Equal(DocumentCreationChannel.Pos, C(null, null, ocr: true, pos: true));
    }

    [Theory]
    [InlineData(DocumentCreationChannelRule.MarkerApi, DocumentCreationChannel.Api)]
    [InlineData(DocumentCreationChannelRule.MarkerStorefront, DocumentCreationChannel.Storefront)]
    [InlineData(DocumentCreationChannelRule.MarkerOcr, DocumentCreationChannel.Ocr)]
    [InlineData(DocumentCreationChannelRule.MarkerSettlement, DocumentCreationChannel.Settlement)]
    [InlineData(DocumentCreationChannelRule.MarkerExpenseClaim, DocumentCreationChannel.ExpenseClaim)]
    [InlineData(DocumentCreationChannelRule.MarkerPlatformBilling, DocumentCreationChannel.PlatformBilling)]
    [InlineData(DocumentCreationChannelRule.MarkerIntegration, DocumentCreationChannel.Integration)]
    [InlineData(DocumentCreationChannelRule.MarkerSystem, DocumentCreationChannel.SystemJob)]
    [InlineData(DocumentCreationChannelRule.MarkerUser, DocumentCreationChannel.Manual)]
    public void ป้าย_CreatedBy_ของเส้นระบบ(string marker, DocumentCreationChannel expected)
    {
        Assert.Equal(expected, C(null, marker));
    }

    [Fact]
    public void ใบล่าสุดของรายการประจำ_ชนะ_System_และผู้ใช้_แต่แพ้ป้ายเส้นระบบ()
    {
        Assert.Equal(DocumentCreationChannel.Recurring, C(null, DocumentCreationChannelRule.MarkerSystem, rec: true));
        Assert.Equal(DocumentCreationChannel.Recurring, C(null, DocumentCreationChannelRule.MarkerUser, rec: true));
        Assert.Equal(DocumentCreationChannel.Api, C(null, DocumentCreationChannelRule.MarkerApi, rec: true));
    }

    [Theory]
    [InlineData(DocumentCreationChannelRule.MarkerEmail)]   // LINE บันทึกอีเมลผู้ใช้ — ไม่มีป้ายช่องทาง
    [InlineData(DocumentCreationChannelRule.MarkerOther)]
    [InlineData(DocumentCreationChannelRule.MarkerNone)]
    [InlineData(null)]
    public void ไม่มีหลักฐาน_ต้องเป็นไม่ทราบ_ไม่ใช่หน้าจอ(string? marker)
    {
        Assert.Equal(DocumentCreationChannel.Unknown, C(null, marker));
        Assert.Equal(DocumentCreationChannel.Unknown, C("SomethingNew", marker));
    }

    [Fact]
    public void ทุกช่องทางมีป้ายไทย_และไม่ทราบมีคำอธิบาย()
    {
        foreach (var c in Enum.GetValues<DocumentCreationChannel>())
            Assert.False(string.IsNullOrWhiteSpace(DocumentCreationChannelRule.Label(c)));
        Assert.Equal("ไม่ทราบ", DocumentCreationChannelRule.Label(DocumentCreationChannel.Unknown));
        Assert.NotNull(DocumentCreationChannelRule.Caveat(DocumentCreationChannel.Unknown));
        Assert.Null(DocumentCreationChannelRule.Caveat(DocumentCreationChannel.Import));
    }

    [Theory]
    [InlineData(DocumentStatus.Draft, "Draft")]
    [InlineData(DocumentStatus.WaitingApproval, "Pending")]
    [InlineData(DocumentStatus.Approved, "Approved")]
    [InlineData(DocumentStatus.Sent, "Approved")]
    [InlineData(DocumentStatus.Paid, "Approved")]
    [InlineData(DocumentStatus.PartiallyPaid, "Approved")]
    [InlineData(DocumentStatus.Overdue, "Approved")]
    [InlineData(DocumentStatus.Voided, "Voided")]
    [InlineData(DocumentStatus.Rejected, "Rejected")]
    public void กลุ่มสถานะ(DocumentStatus s, string bucket)
    {
        Assert.Equal(bucket, DocumentCreationChannelRule.StatusBucket(s));
        Assert.NotEqual(bucket, DocumentCreationChannelRule.StatusBucketLabel(bucket));
    }

    [Fact]
    public void โควตาเอกสาร_ใช้กติกาเดียวกับตอนสร้าง()
    {
        // ทุกชนิด × มัดจำ × ที่พัก ต้องตรงกับ DocumentQuotaPolicy.Classify ตัวจริง (ไม่มีตารางที่สอง)
        foreach (var t in Enum.GetValues<DocumentType>())
            foreach (var dep in new[] { false, true })
                foreach (var origin in new string?[] { null, "Lodging", "Import" })
                {
                    var expected = DocumentQuotaPolicy.Classify(t, dep, fromLodging: origin == "Lodging") != QuotaEnforcement.NotCounted;
                    Assert.Equal(expected, DocumentCreationChannelRule.CountsTowardDocumentQuota(t, dep, origin));
                }
    }
}
