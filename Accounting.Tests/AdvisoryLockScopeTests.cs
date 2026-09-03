using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ขอบเขตของ advisory lock** — ล็อกที่กว้างเกินไปคือ head-of-line blocking
/// ข้ามผู้เช่า (CLAUDE.md กฎเหล็ก #4 D)
///
/// ที่มา: `DocumentService.UndueVatExpiryLockKey` เคยเป็นค่าคงที่ `828_003L`
/// ทั้งระบบ ทั้งที่งานทำ**รายบริษัท** และมีปุ่มผู้ใช้เรียกตรง ๆ ⇒ ผู้ใช้บริษัท A
/// กดปุ่มแล้วสแกนนาน 10 วินาที ผู้ใช้บริษัท B ที่กดปุ่มเดียวกันต้องรอจนเสร็จ
/// ทั้งที่คนละชุดข้อมูลโดยสิ้นเชิง
///
/// เทสต์นี้ล็อก **คุณสมบัติสองข้อที่ต้องเป็นจริงพร้อมกัน**:
///   1. คนละบริษัท = คนละคีย์ (ไม่บล็อกกัน)
///   2. บริษัทเดียวกัน = คีย์เดียวกันเสมอ (ปุ่มกับ job ยังกันซ้อนได้ — เจตนาเดิม)
/// </summary>
public class AdvisoryLockScopeTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ล้างภาษีซื้อพ้นกำหนด_คนละบริษัทต้องไม่บล็อกกัน()
    {
        var ka = AdvisoryLockKey.For(A, AdvisoryLockKey.UndueVatExpiry, "reclassify");
        var kb = AdvisoryLockKey.For(B, AdvisoryLockKey.UndueVatExpiry, "reclassify");
        Assert.NotEqual(ka, kb);
    }

    [Fact]
    public void ล้างภาษีซื้อพ้นกำหนด_บริษัทเดียวกันต้องได้คีย์เดิมเสมอ()
    {
        var k1 = AdvisoryLockKey.For(A, AdvisoryLockKey.UndueVatExpiry, "reclassify");
        var k2 = AdvisoryLockKey.For(A, AdvisoryLockKey.UndueVatExpiry, "reclassify");
        Assert.Equal(k1, k2);
    }

    /// <summary>คีย์ต้องเท่ากันข้าม process ด้วย — เทียบกับ **ค่าคงที่ที่ hard-code ไว้**
    /// ไม่ใช่แค่ "เรียกสองครั้งได้เท่ากัน" (ซึ่ง `HashCode.Combine` ก็ผ่าน ทั้งที่
    /// สุ่ม seed ใหม่ทุก process = ล็อกข้ามเครื่องไม่ได้จริง)</summary>
    [Fact]
    public void คีย์คงที่ข้าม_process_ยืนยันด้วยค่าที่ตรึงไว้()
    {
        Assert.Equal(
            AdvisoryLockKey.For(A, AdvisoryLockKey.UndueVatExpiry, "reclassify"),
            FnvReference(A, AdvisoryLockKey.UndueVatExpiry, "reclassify"));
        Assert.Equal(
            AdvisoryLockKey.For(A, AdvisoryLockKey.QuotaGrant, "reward"),
            FnvReference(A, AdvisoryLockKey.QuotaGrant, "reward"));
    }

    [Fact]
    public void แลกโควตากับซื้อโควตา_เป็นคนละล็อก_ไม่ต้องรอกัน()
    {
        Assert.NotEqual(
            AdvisoryLockKey.For(A, AdvisoryLockKey.QuotaGrant, "reward"),
            AdvisoryLockKey.For(A, AdvisoryLockKey.QuotaGrant, "topup"));
    }

    /// <summary>งานระดับระบบ (ปิดรอบบิล/ค่าเหมารายเดือน) **ตั้งใจ**ให้ไม่ผูกบริษัท —
    /// ทั้งระบบรันได้ทีละราย ซึ่งถูกต้องเพราะงานเดินทีเดียวทุก tenant</summary>
    [Fact]
    public void งานปิดรอบบิลเป็นคีย์ระดับระบบ_ไม่ผูกบริษัทโดยตั้งใจ()
    {
        Assert.Equal(
            AdvisoryLockKey.For(AdvisoryLockKey.UsageInvoicing, "2026-09"),
            AdvisoryLockKey.For(Guid.Empty, AdvisoryLockKey.UsageInvoicing, "2026-09"));
        // คนละงวด = คนละล็อก (ปิดรอบเดือนก่อนกับเดือนนี้พร้อมกันได้)
        Assert.NotEqual(
            AdvisoryLockKey.For(AdvisoryLockKey.UsageInvoicing, "2026-09"),
            AdvisoryLockKey.For(AdvisoryLockKey.UsageInvoicing, "2026-10"));
    }

    /// <summary>FNV-1a 64-bit เขียนซ้ำแบบอิสระ — ถ้าอัลกอริทึมในโปรดักชันถูกเปลี่ยน
    /// เทสต์นี้จะฟ้องทันที (คีย์ที่เปลี่ยน = ล็อกที่ไม่กันของเก่าระหว่าง deploy)</summary>
    private static long FnvReference(Guid companyId, string scope, string part)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (var ch in companyId.ToString("N")) { h ^= ch; h *= 1099511628211UL; }
            foreach (var ch in $"|{scope}|") { h ^= ch; h *= 1099511628211UL; }
            foreach (var ch in part) { h ^= ch; h *= 1099511628211UL; }
            return (long)h;
        }
    }
}
