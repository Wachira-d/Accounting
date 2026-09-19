using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **อัตรา VAT ของเอกสารที่คู่ค้ายิงเข้ามาทาง `/api/integration/*`**
/// (DECISION_AUDIT_2026-09-18 §3 D8-4)
///
/// <para>บั๊กจริง: <c>IntegrationService</c> ถอยไปหาเลข 7 ตายตัว 6 จุด และ
/// <c>BuildDocumentLinesAsync(defaultVatRate = 7)</c> อีก 2 จุด โดยไม่เคยอ่าน
/// สถานะจดทะเบียน VAT ของบริษัทเลย ⇒ tenant ที่ไม่ได้จด VAT ออกเอกสารพร้อม
/// VAT 7% = เรียกเก็บภาษีโดยไม่มีสิทธิ์ (§90/2) และยอด ภ.พ.30 เพี้ยน</para>
///
/// <para>ล็อกทั้งสองทิศ: ที่เคยพัง (ไม่จด VAT ต้องไม่ได้ 7) **และ** ที่ต้องไม่ถูกแตะ
/// (บริษัทที่จดแล้วยังได้อัตราเดิม · คู่ค้าที่ส่ง 0 มาเองยังได้ 0 · ฝั่งซื้อยอดที่
/// ต้องจ่ายผู้ขายไม่เปลี่ยน)</para>
/// </summary>
public class IntegrationVatRateTests
{
    // ═══ ฝั่งขาย — เอกสารที่ "เราออก" ═══

    [Fact]
    public void บริษัทจด_VAT_คู่ค้าไม่ส่งอัตรามา_ใช้อัตราของบริษัท()
    {
        var d = PartnerVatRate.ForIssuedDocument(null, vatRegistered: true, companyVatRate: 7m);
        Assert.False(d.Rejected);
        Assert.Equal(7m, d.Rate);

        // บริษัทที่ตั้งอัตราของตัวเองไว้ต่างจาก 7 ต้องได้อัตราของตัวเอง
        // (เดิมได้ 7 ตายตัวเสมอเพราะไม่มีใครอ่านค่าที่ตั้งไว้)
        var custom = PartnerVatRate.ForIssuedDocument(null, vatRegistered: true, companyVatRate: 10m);
        Assert.Equal(10m, custom.Rate);
    }

    // ── ทิศที่เคยพัง ──

    [Fact]
    public void ไม่จด_VAT_คู่ค้าไม่ส่งอัตรามา_ต้องได้ศูนย์_ไม่ใช่เจ็ด()
    {
        var d = PartnerVatRate.ForIssuedDocument(null, vatRegistered: false, companyVatRate: 7m);
        Assert.False(d.Rejected);
        Assert.Equal(0m, d.Rate);
    }

    [Fact]
    public void ไม่จด_VAT_แต่คู่ค้าสั่งเก็บ_7_ต้องปฏิเสธพร้อมบอกวิธีแก้()
    {
        var d = PartnerVatRate.ForIssuedDocument(7m, vatRegistered: false, companyVatRate: 7m);

        // เลือก "ล้มดัง" ไม่ใช่ "ตัดเป็น 0 เงียบ ๆ": ถ้าเงียบ ระบบต้นทางยังเชื่อว่า
        // เก็บ VAT จากลูกค้าได้ 7% ⇒ เงินจริงกับเอกสารไม่ตรงโดยไม่มีใครเห็น
        Assert.True(d.Rejected);
        Assert.Equal(0m, d.Rate);
        Assert.NotNull(d.Error);
        Assert.Contains("§90/2", d.Error);
        // ข้อความต้องบอก "ทำอะไรต่อได้" ทั้งสองทาง (กฎเหล็ก #4 F2 ข้อ 8)
        Assert.Contains("vatRate = 0", d.Error);
        Assert.Contains("จดทะเบียนภาษีมูลค่าเพิ่ม", d.Error);
    }

    // ── ทิศตรงข้าม: ที่ถูกอยู่แล้วห้ามถูกแตะ ──

    [Fact]
    public void คู่ค้าส่งศูนย์มาเอง_ต้องได้ศูนย์เสมอ_ไม่ว่าบริษัทจดหรือไม่()
    {
        // §81 ยกเว้น · §80/1 ส่งออกอัตรา 0 — ตัวแนะนำห้ามทับค่าที่คู่ค้าระบุ
        Assert.Equal(0m, PartnerVatRate.ForIssuedDocument(0m, true, 7m).Rate);
        Assert.False(PartnerVatRate.ForIssuedDocument(0m, true, 7m).Rejected);
        Assert.Equal(0m, PartnerVatRate.ForIssuedDocument(0m, false, 7m).Rate);
        Assert.False(PartnerVatRate.ForIssuedDocument(0m, false, 7m).Rejected);
    }

    [Fact]
    public void รหัสยกเว้นติดลบ_ผ่านไปได้ไม่ถูกแปลงเป็นอัตราบริษัท()
    {
        // VatRate = -1 คือรหัส "ยกเว้น §81" ของเรพนี้ (ดู DocumentVatFallback)
        var d = PartnerVatRate.ForIssuedDocument(-1m, vatRegistered: true, companyVatRate: 7m);
        Assert.False(d.Rejected);
        Assert.Equal(-1m, d.Rate);
    }

    [Fact]
    public void บริษัทจด_VAT_คู่ค้าส่งอัตราผสมมา_ใช้ตามที่ส่ง()
    {
        Assert.Equal(7m, PartnerVatRate.ForIssuedDocument(7m, true, 7m).Rate);
        Assert.Equal(10m, PartnerVatRate.ForIssuedDocument(10m, true, 7m).Rate);
    }

    // ═══ ฝั่งซื้อ — เอกสารที่ "ผู้ขายออกให้เรา" ═══

    [Fact]
    public void ฝั่งซื้อ_คู่ค้าส่งอัตรามา_ใช้ตามนั้นรวมถึงศูนย์()
    {
        Assert.Equal(7m, PartnerVatRate.ForReceivedDocument(7m, 7m));
        Assert.Equal(0m, PartnerVatRate.ForReceivedDocument(0m, 7m));
    }

    [Fact]
    public void ฝั่งซื้อ_ไม่ส่งอัตรามา_ใช้อัตราตั้งต้นของบริษัทไม่ใช่เลขตายตัว()
    {
        Assert.Equal(7m, PartnerVatRate.ForReceivedDocument(null, 7m));
        Assert.Equal(10m, PartnerVatRate.ForReceivedDocument(null, 10m));
    }

    /// <summary>
    /// ทิศตรงข้ามที่สำคัญที่สุดของข้อนี้ — **ห้าม**เอาสถานะจดทะเบียนของเราไปตัด
    /// อัตราฝั่งซื้อ: VAT บนใบเป็นของผู้ขาย ตัดแล้วยอดที่ต้องจ่ายผู้ขายหายไป 7%
    /// เงียบ ๆ. สิ่งที่สถานะของเราตัดสินคือ "เคลมได้ไหม" เท่านั้น
    /// </summary>
    [Fact]
    public void ฝั่งซื้อ_บริษัทไม่จด_VAT_อัตราไม่ถูกตัด_แต่เคลมไม่ได้()
    {
        // ลายเซ็นของ ForReceivedDocument ไม่มีช่อง vatRegistered เลยโดยตั้งใจ
        Assert.Equal(7m, PartnerVatRate.ForReceivedDocument(null, 7m));

        Assert.False(PartnerVatRate.InputVatClaimable(vatRegistered: false, lineVatAmount: 70m));
        Assert.True(PartnerVatRate.InputVatClaimable(vatRegistered: true, lineVatAmount: 70m));
    }

    [Fact]
    public void บรรทัดที่ไม่มี_VAT_ไม่มีอะไรให้เคลม_แม้บริษัทจดทะเบียน()
    {
        Assert.False(PartnerVatRate.InputVatClaimable(vatRegistered: true, lineVatAmount: 0m));
    }

    // ═══ ค่าถอยหลังสุดท้าย ═══

    [Fact]
    public void อัตราตามกฎหมายมีที่เดียวและเท่ากับเจ็ด()
    {
        Assert.Equal(7m, PartnerVatRate.StatutoryRate);
        // บริษัทที่ "ยังไม่เคยตั้งค่า" ต้องได้พฤติกรรมเดิมทุกประการ
        Assert.Equal(7m, PartnerVatRate.ForReceivedDocument(null, PartnerVatRate.StatutoryRate));
        Assert.Equal(7m, PartnerVatRate
            .ForIssuedDocument(null, true, PartnerVatRate.StatutoryRate).Rate);
    }

    [Fact]
    public void ตัวตัดสินฝั่งขายยังเป็น_OutputVatRate_ตัวเดิม_ไม่ใช่สำเนาที่สอง()
    {
        // ผลของ ForIssuedDocument (กรณีไม่ส่งอัตรามา) ต้องเท่ากับ OutputVatRate
        // ทุกคู่ค่า — ถ้าวันหนึ่งมีใครเขียนสูตรใหม่ที่นี่ เทสต์นี้จะจับได้
        foreach (var reg in new[] { true, false })
            foreach (var rate in new[] { 0m, 7m, 10m })
                Assert.Equal(
                    OutputVatRate.ForCompany(reg, rate),
                    PartnerVatRate.ForIssuedDocument(null, reg, rate).Rate);
    }
}
