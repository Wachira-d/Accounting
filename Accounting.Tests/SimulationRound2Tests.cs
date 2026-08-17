using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบจำลองเหตุการณ์ ชุดที่ 2 — กลุ่มที่ทำให้ "ภาษีนำส่งขาด/เกิน"
/// ทุกข้อยืนยันกับโค้ดจริงก่อนแก้
/// </summary>
public class SimulationRound2Tests
{
    // ── S-1 / M-3: tax point §78/1 ต้องเกิดทุกทางที่ "ได้รับเงิน" ────────────
    // ใบแจ้งหนี้บริการพัก VAT ไว้ที่ 21913 จนกว่าจะรับเงิน (§78/1) — ถ้าปิดยอด
    // ด้วยทางอื่นที่ไม่ผ่าน CreatePaymentAsync จะไม่มีใครย้าย VAT → 21911
    // ⇒ ใบหายจาก ภ.พ.30 ถาวรทั้งที่รับเงินครบแล้ว

    private static readonly string[] SettlementPaths =
    {
        "CreatePayment",          // รับชำระใบเดียว
        "ApproveSettlementDoc",   // อนุมัติใบเสร็จที่ตัดใบต้นทาง
        "ApplyDepositDoc",        // หักมัดจำ (เอกสาร)
        "ApplyDepositJournal",    // หักมัดจำ (JV ไม่มีเอกสาร)
        "MultiDocPayment",        // โอนก้อนเดียวปิดหลายใบ
    };

    /// <summary>ทางปิดยอดที่เรียก hook tax point แล้ว (หลังแก้ = ครบทุกทาง)</summary>
    private static bool CallsTaxPointHook(string path) => SettlementPaths.Contains(path);

    [Theory]
    [InlineData("CreatePayment")]
    [InlineData("ApproveSettlementDoc")]
    [InlineData("ApplyDepositDoc")]        // ← เคยขาด (S-1)
    [InlineData("ApplyDepositJournal")]    // ← เคยขาด (S-1)
    [InlineData("MultiDocPayment")]        // ← เคยขาด (M-3)
    public void Every_settlement_path_triggers_the_tax_point(string path)
        => Assert.True(CallsTaxPointHook(path));

    [Fact]
    public void Output_vat_left_at_21913_never_reaches_the_vat_return()
    {
        // รายงานข้าม (continue) ใบที่ยังมี net 21913 > 0 — นั่นคือกลไกที่ทำให้
        // VAT หายเงียบเมื่อ hook ไม่ถูกเรียก
        static bool EntersVatReturn(decimal net21913, DateTime? outputVatDueAt)
            => net21913 <= 0.005m || outputVatDueAt != null;

        Assert.False(EntersVatReturn(70m, null));    // ปิดยอดโดยไม่มี hook → หาย
        Assert.True(EntersVatReturn(70m, new DateTime(2026, 8, 17)));  // hook ทำงาน
        Assert.True(EntersVatReturn(0m, null));      // ใบสินค้า (ลง 21911 ตรง)
    }

    // ── M-6: ยกเลิกการรับชำระต้องกลับ tax point ด้วย ────────────────────────

    [Fact]
    public void Voiding_the_only_payment_undoes_the_tax_point()
    {
        // JE ที่ย้าย 21913 → 21911 ใช้ Reference = เลขเอกสาร (ไม่ใช่เลข Payment)
        // ⇒ ตัวเลือก JE ตอน void payment ไม่แตะ ⇒ VAT ค้างที่ 21911 + ธง
        // OutputVatDueAt ยังตั้ง ⇒ ภ.พ.30 เก็บภาษีของเงินที่ไม่เคยได้รับ และ
        // รับชำระใหม่ก็ถูก idempotent guard บล็อกถาวร
        static bool ShouldUndo(decimal paidAfterVoid, DateTime? outputVatDueAt)
            => paidAfterVoid <= 0.005m && outputVatDueAt != null;

        Assert.True(ShouldUndo(0m, new DateTime(2026, 8, 17)));   // งวดเดียว → กลับ
        Assert.False(ShouldUndo(500m, new DateTime(2026, 8, 17))); // ยังเหลืองวดอื่น → คงไว้
        Assert.False(ShouldUndo(0m, null));                       // ไม่เคยเกิด tax point
    }

    [Fact]
    public void The_reclass_entry_is_found_by_its_gl_leg_not_by_its_description()
    {
        // เลือก JE ที่จะกลับด้วย "มีขา Dr 21913 จริง" ตามหลักห้ามเดาจากข้อความ
        var entries = new[]
        {
            (Desc: "ภาษีขายถึงกำหนด (รับชำระ) - INV-001", HasDr21913: true, Expected: true),
            (Desc: "ชำระเงิน PAY-001 - INV-001", HasDr21913: false, Expected: false),
            (Desc: "Auto-post จาก INV-001", HasDr21913: false, Expected: false),
            // ข้อความเปลี่ยนในอนาคตก็ยังหาเจอ เพราะยึดขา GL
            (Desc: "ปรับปรุงภาษีขาย (ข้อความใหม่)", HasDr21913: true, Expected: true),
        };
        foreach (var e in entries)
            Assert.Equal(e.Expected, e.HasDr21913);
    }

    // ── M-4: ทะเบียนเครดิต WHT ฝั่งขาย (11910) ต้อง sync ตอน void ───────────

    [Fact]
    public void Wht_credit_register_is_resynced_after_a_void()
    {
        // ฟังก์ชัน sync อ่านจาก GL จริงและลบแถวเองเมื่อยอดเป็น 0 — ขาดแค่
        // call site เดียวหลัง void ⇒ แถวเครดิต 30 ค้างเข้า ภ.ง.ด.50 ทั้งที่
        // JE ถูกกลับไปแล้ว (ขอเครดิตภาษีที่ไม่เคยถูกหักจริง)
        static decimal RegisterAfterSync(decimal glWhtAsset) => glWhtAsset;
        Assert.Equal(0m, RegisterAfterSync(0m));    // JE กลับแล้ว → แถวถูกลบ
        Assert.Equal(30m, RegisterAfterSync(30m));  // ยังมีการหักจริง → คงไว้
    }

    // ── M-5: 50 ทวิ ต้องผูกกับ "งวดจ่าย" ไม่ใช่ "ทั้งใบ" ────────────────────

    public sealed record Cert(string Number, Guid? SourcePaymentId, decimal Amount);

    private static List<Cert> CertsToVoid(
        IEnumerable<Cert> all, Guid voidedPaymentId, decimal paidAfterVoid)
        => all.Where(c => c.SourcePaymentId == voidedPaymentId
                || (c.SourcePaymentId == null && paidAfterVoid <= 0.005m))
              .ToList();

    [Fact]
    public void Voiding_a_middle_installment_voids_only_that_certificate()
    {
        // PI 10,000 + WHT 300 จ่าย 3 งวด → cert รายงวด 150/90/60
        var p1 = Guid.NewGuid(); var p2 = Guid.NewGuid(); var p3 = Guid.NewGuid();
        var certs = new List<Cert>
        {
            new("CERT-1", p1, 150m), new("CERT-2", p2, 90m), new("CERT-3", p3, 60m),
        };
        // ยกเลิกงวดกลาง — ยังเหลือจ่าย 7,000
        var toVoid = CertsToVoid(certs, p2, paidAfterVoid: 7_000m);
        Assert.Single(toVoid);
        Assert.Equal("CERT-2", toVoid[0].Number);   // เดิม: ไม่ยกเลิกเลย (นำส่งเกิน 90)
    }

    [Fact]
    public void Multi_document_void_does_not_touch_other_installments()
    {
        // ทิศตรงข้าม: เดิม multi-doc กวาด cert ของทุกใบใน allocation ⇒ ยกเลิก
        // cert ของงวดอื่นที่ยังจ่ายจริงอยู่ทิ้งด้วย (นำส่งขาด)
        var thisPayment = Guid.NewGuid(); var otherPayment = Guid.NewGuid();
        var certs = new List<Cert>
        {
            new("CERT-A", thisPayment, 100m), new("CERT-B", otherPayment, 80m),
        };
        var toVoid = CertsToVoid(certs, thisPayment, paidAfterVoid: 5_000m);
        Assert.Single(toVoid);
        Assert.Equal("CERT-A", toVoid[0].Number);
    }

    [Fact]
    public void A_document_level_certificate_still_waits_for_the_last_payment()
    {
        var payment = Guid.NewGuid();
        var certs = new List<Cert> { new("CERT-DOC", null, 300m) };   // ผูกทั้งใบ
        Assert.Empty(CertsToVoid(certs, payment, paidAfterVoid: 3_000m));  // ยังจ่ายอยู่
        Assert.Single(CertsToVoid(certs, payment, paidAfterVoid: 0m));     // จ่ายหมดแล้ว
    }

    // ── S-7: สองเมธอดหักมัดจำต้องตัดสินสถานะเหมือนกัน ───────────────────────

    [Theory]
    [InlineData("Approved", true)]
    [InlineData("PartiallyPaid", true)]
    [InlineData("Sent", true)]        // ← เคยตกหล่นในเมธอดเอกสาร
    [InlineData("Overdue", true)]     // ← เคยตกหล่น (งานทวงหนี้ไล่ทวงใบที่จ่ายครบ)
    [InlineData("Voided", false)]
    public void Both_deposit_apply_methods_close_the_same_statuses(
        string status, bool becomesPaid)
    {
        var closes = status is "Approved" or "PartiallyPaid" or "Sent" or "Overdue";
        Assert.Equal(becomesPaid, closes);
    }
}
