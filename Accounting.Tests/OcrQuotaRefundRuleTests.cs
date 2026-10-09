using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านด่านไฟล์ซ้ำ (2026-10-09 · D1): คืนโควตาเฉพาะเมื่อไม่ได้ใช้ engine จริง — ไฟล์ซ้ำที่ถูก "อ่านใหม่" ด้วย Azure ต้องเสียโควตา
/// (เดิมสองทางเข้าคืนตามธง IsDuplicate ⇒ หน้า Azure ฟรีทุกครั้งที่อัปไฟล์เก่าซ้ำหลัง deploy ตัวแกะรุ่นใหม่)
/// </summary>
public class OcrQuotaRefundRuleTests
{
    [Theory]
    [InlineData("Completed", "Cached")]       // คัดลอกผลเดิม — engine ไม่รัน
    [InlineData("Completed", "EtaxXml")]      // อ่าน XML ฝัง — engine ไม่รัน
    [InlineData("Failed", "AzureDI")]         // ล้มเหลว — ไม่ควรเสียเครดิต (กติกาเดิม)
    [InlineData("Processing", null)]
    public void ไม่ได้ใช้engineจริง_หรือล้มเหลว_คืนโควตา(string status, string? engine)
        => Assert.True(OcrQuotaRefundRule.ShouldRefund(status, engine));

    [Theory]
    [InlineData("AzureDI")]
    [InlineData("EmbeddedTesseract")]
    [InlineData("PythonLocal")]
    [InlineData(null)]                        // ไม่ทราบ engine ⇒ ถือว่าใช้จริง (ไม่แจกฟรี)
    public void ทิศตรงข้าม_สแกนสำเร็จด้วยengineจริง_ไม่คืน_แม้ติดธงไฟล์ซ้ำ(string? engine)
        => Assert.False(OcrQuotaRefundRule.ShouldRefund("Completed", engine));

    [Fact]
    public void ค่าคงที่ตรงกับตัวตัดสินไฟล์ซ้ำ()
        => Assert.Equal("Cached", OcrDuplicateReusePolicy.CachedEngine);
}
