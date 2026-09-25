using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 194 ทีม M3 — แก้ผลฝ่ายค้านรอบสาม (<c>erp-review/2026-09-25/review194-r3.md</c> R3-1 · R3-2 + PLAUSIBLE P-1/P-2/P-4)
/// ทุกข้อล็อกสองครึ่ง: <b>ใบที่พังกลับมาถูก</b> · <b>ใบที่ถูกอยู่แล้วไม่ถูกแตะ</b> — ตัวเลขจากรายงานฝ่ายค้านตรง ๆ
/// (มัดจำเต็มยอด 10,700 รับ 20/01/2026 · ริบ 05/02/2026 ก่อนกำหนดยื่นกระดาษงวด ม.ค. 16/02)
/// <para>R3-2 (ล็อกยอดใบมัดจำทุกเส้น) เป็นการต่อสายใน service ซึ่งเรพนี้ไม่มีเทสต์ DB — ล็อกด้วย <c>tools/required_call_site_check.py</c></para>
/// </summary>
public class DepositRound194R3Tests
{
    private static readonly Guid Co = Guid.Parse("55555555-3333-3333-3333-333333333333");
    private static readonly Guid OtherCo = Guid.Parse("66666666-4444-4444-4444-444444444444");
    private static readonly DateTime Received = new(2026, 1, 20);
    private static readonly DateTime Forfeit = new(2026, 2, 5);

    // ═════════════════ R3-1 — ใบกำกับของยอดที่ริบ: tax point = วันที่ของใบเสมอ ═════════════════

    [Fact]
    public void R31_รับ2001ริบ0502_ใบกำกับของการริบ_taxPointวันริบ_ธงLATEตรงความจริง()
    {
        // รายงาน: เดิม PaymentDate = 20/01 ⇒ ภ.พ.30 ม.ค. +700 แต่ JE Cr 21911 700 ลง ก.พ. (ใบลงวันที่ 05/02 อยู่ในรายงานภาษีขาย ม.ค.)
        // ⇒ วันนี้ 05/02 ยังไม่เลยกำหนดงวด ม.ค. (16/02) แต่เส้นใบกำกับย้อนไม่ได้เลย
        var today = Forfeit;
        Assert.True(today <= TaxFilingDeadline.For("VatPp30", 2026, 1).Paper);
        var tp = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.ForfeitTaxInvoice, true, Received, Forfeit,
            depositPeriodLocked: false, today: today);
        Assert.Equal(Forfeit, tp.TaxPointDate);
        Assert.True(tp.LateFlag);
        Assert.StartsWith(DepositPolicyResolver.LateVatMarker, tp.Note);
        Assert.Contains("ถึงกำหนดงวด 01/2026", tp.Note);
        Assert.Contains("รับเงิน 20/01/2026", tp.Note);
        Assert.Contains("นำส่งในงวด 02/2026", tp.Note);
        Assert.Contains("§89/1", tp.Note);
        Assert.Contains("ห้ามนำส่งซ้ำ", tp.Note);
        Assert.Contains("ปรึกษานักบัญชี", tp.Note);
        Assert.Contains("ลงวันที่ 05/02/2026", tp.Note);
        Assert.DoesNotContain("ยังไม่เลยกำหนดยื่น", tp.Note);   // ข้อความของเส้น VAT พักที่ย้อนได้ — ห้ามปน
    }

    [Fact]
    public void R31_ใบกำกับของการริบ_ไม่ส่งวันรับเงิน_TaxPointResolverได้วันออกใบ_GLกับภพ30เดือนเดียวกัน()
    {
        // ใบกำกับของการริบ 10,700 (ราคารวม VAT ⇒ ฐาน 10,000 + VAT 700) ลงวันที่ 05/02 · JE ของใบ EntryDate = DocumentDate
        var inv = new Document { CompanyId = Co, DocumentType = DocumentType.TaxInvoice, DocumentDate = Forfeit,
            SubTotal = 10_000m, VatAmount = 700m, TotalAmount = 10_700m, PaymentDate = null };
        var taxPoint = TaxPointResolver.Resolve(inv);
        Assert.Equal(Forfeit, taxPoint);
        Assert.False(TaxPointResolver.DiffersFromDocumentMonth(inv, taxPoint));   // ภ.พ.30 ก.พ. = GL ก.พ.
        // ทิศตรงข้าม (บั๊กเดิม): ส่งวันรับเงินเป็น PaymentDate ⇒ tax point ม.ค. ขณะ JE ก.พ.
        inv.PaymentDate = Received;
        var old = TaxPointResolver.Resolve(inv);
        Assert.Equal(Received, old);
        Assert.True(TaxPointResolver.DiffersFromDocumentMonth(inv, old));
    }

    [Fact]
    public void R31_ทิศตรงข้าม_เส้นย้ายVATพัก_ยังย้อนเข้างวดเดือนรับเงินได้_JEแยกลงวันนั้น()
    {
        // เส้น 21913→21911 แยก JE ขาภาษีลงวัน tax point แล้ว (R2-1) ⇒ ยังไม่เลยกำหนด/ไม่ล็อก = เข้างวด ม.ค. ไม่มีธง
        var tp = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, Received, Forfeit,
            depositPeriodLocked: false, today: Forfeit);
        Assert.Equal(Received, tp.TaxPointDate);
        Assert.False(tp.LateFlag);
        Assert.Contains("ยังไม่เลยกำหนดยื่น", tp.Note);
        Assert.False(DepositPolicyResolver.SameVatPeriod(tp.TaxPointDate, Forfeit));   // ⇒ service แยก JE ขาภาษี
    }

    [Fact]
    public void R31_ทิศตรงข้าม_ใบกำกับของการริบเดือนเดียวกัน_และไม่ใช่ภาษีย้อนหลัง_ไม่มีธง()
    {
        var same = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.ForfeitTaxInvoice, true,
            new DateTime(2026, 2, 2), Forfeit, depositPeriodLocked: false, today: new DateTime(2026, 3, 30));
        Assert.Equal(Forfeit, same.TaxPointDate);   // วันที่ของใบ ไม่ใช่วันรับเงิน (เดือนเดียวกัน — ภ.พ.30 เหมือนกัน)
        Assert.False(same.LateFlag);
        Assert.Null(same.Note);
        // เงินประกันที่หักเป็นค่าของ/ค่าธรรมเนียม (lateVat=false) — จุดความรับผิด = วันที่หัก ไม่มีธง แม้ต่างเดือน
        var fee = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.ForfeitTaxInvoice, false, Received, Forfeit,
            depositPeriodLocked: true, today: Forfeit);
        Assert.Equal(Forfeit, fee.TaxPointDate);
        Assert.False(fee.LateFlag);
        Assert.Null(fee.Note);
        // ริบลงวันก่อนวันรับเงิน (ข้อมูลแปลก) — ไม่ประทับธง ไม่ย้อน
        var before = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.ForfeitTaxInvoice, true, Forfeit, Received,
            depositPeriodLocked: false, today: Forfeit);
        Assert.Equal(Received, before.TaxPointDate);
        Assert.False(before.LateFlag);
    }

    [Fact]
    public void R31_ใบกำกับของการริบ_ไม่ขึ้นกับล็อก_กำหนดยื่น_ปี_วันที่เป็นคศแม้วัฒนธรรมไทย()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            foreach (var locked in new[] { false, true })
            foreach (var today in new[] { Forfeit, new DateTime(2026, 9, 25) })
            {
                var tp = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.ForfeitTaxInvoice, true, Received, Forfeit,
                    locked, today);
                Assert.Equal(Forfeit, tp.TaxPointDate);
                Assert.True(tp.LateFlag);
                Assert.Contains("01/2026", tp.Note);
                Assert.Contains("02/2026", tp.Note);
                Assert.DoesNotContain("2569", tp.Note);
            }
            // ข้ามปีภาษี (รับ ธ.ค. ริบ ม.ค.) — วันที่ของใบเสมอ + ธง
            var cross = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.ForfeitTaxInvoice, true,
                new DateTime(2025, 12, 20), new DateTime(2026, 1, 5), false, new DateTime(2026, 1, 5));
            Assert.Equal(new DateTime(2026, 1, 5), cross.TaxPointDate);
            Assert.True(cross.LateFlag);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    // ═════════════════ P-4 — ยกเลิกใบที่หักมัดจำหลายใบ: คืนยอดรายใบ ═════════════════

    // ขา Dr ของ JE ใบเช็คเอาต์ที่เส้นหักหลายใบเขียน (คำอธิบายจาก AutoPostToJournalAsync ตรงตัว)
    private static readonly (string, decimal, string?)[] TwoDepositLegs =
    {
        ("11100", 4_000m, "รับเงินสด"),
        ("21712", 3_000m, "ตัดขายรอรับรู้ (นำมัดจำ REC-9 มาหักเต็มใบ)"),
        ("21913", 210m, "ตัดภาษีขายรอเรียกเก็บ (มัดจำ REC-9 → ใบกำกับ)"),
        ("21712", 5_000m, "ตัดขายรอรับรู้ (นำมัดจำ REC-10 มาหักเต็มใบ)"),
        ("21911", 350m, "ล้าง VAT มัดจำ REC-10"),
    };

    [Fact]
    public void P4_หักมัดจำสองใบ_REC9_REC10_คืนยอดรายใบตามขาจริง_REC1ไม่ชนREC10()
    {
        var refs = DepositReversalMath.ParseDepositRefs("REC-9, REC-10");
        Assert.Equal(new[] { "REC-9", "REC-10" }, refs);
        var split = DepositReversalMath.SplitDrivesUnrealize(refs, TwoDepositLegs);
        var rec9 = split.Shares.Single(s => s.DepositNumber == "REC-9");
        var rec10 = split.Shares.Single(s => s.DepositNumber == "REC-10");
        Assert.Equal(3_000m, rec9.Base);
        Assert.True(rec9.Stamped21913);          // ใบนี้ย้าย VAT พักของ REC-9 ⇒ ล้าง RecognizedAt ได้
        Assert.Equal(5_000m, rec10.Base);
        Assert.False(rec10.Stamped21913);        // REC-10 VAT เข้า 21911 แล้วแต่แรก ⇒ ห้ามล้าง RecognizedAt ของมัน
        Assert.Equal(0m, split.Unattributed);
        // เลขที่เป็นคำนำหน้าของอีกเลข (REC-1 กับ REC-10) ไม่ถูกผูกผิดใบ
        var prefix = DepositReversalMath.SplitDrivesUnrealize(new[] { "REC-1", "REC-10" }, TwoDepositLegs);
        Assert.Equal(0m, prefix.Shares.Single(s => s.DepositNumber == "REC-1").Base);
        Assert.Equal(5_000m, prefix.Shares.Single(s => s.DepositNumber == "REC-10").Base);
        Assert.Equal(3_000m, prefix.Unattributed);   // ขาของ REC-9 ไม่มีเจ้าของในชุดนี้ ⇒ บอกให้เห็น ห้ามเดา
    }

    [Fact]
    public void P4_ทิศตรงข้าม_มัดจำใบเดียว_สูตรเดิมทุกตัวอักษร_ไม่ดูคำอธิบาย()
    {
        // ใบเดียว: ทุกขา Dr 215/217 เป็นของใบนั้น (คำอธิบายรุ่นเก่า/ไม่มีเลขก็ได้) · มี Dr 21913 = ประทับ
        var legs = new (string, decimal, string?)[]
        {
            ("21712", 1_000m, "ตัดขายรอรับรู้ (นำมัดจำมาหัก)"),
            ("21510", 500m, null),
            ("21913", 105m, "ตัดภาษีขายรอเรียกเก็บ (มัดจำ→ใบกำกับ)"),
            ("41000", 9_999m, "ไม่ใช่ขาหนี้สินมัดจำ"),
        };
        var one = DepositReversalMath.SplitDrivesUnrealize(new[] { "REC-9" }, legs);
        Assert.Equal(1_500m, one.Shares.Single().Base);
        Assert.True(one.Shares.Single().Stamped21913);
        Assert.Equal(0m, one.Unattributed);
        // ไม่มีขา 21913 = ไม่ประทับ (net + 21911 — RecognizedAt เป็นของการรับรู้ครั้งก่อน ห้ามล้าง · audit F3)
        var net = DepositReversalMath.SplitDrivesUnrealize(new[] { "REC-9" },
            new (string, decimal, string?)[] { ("21712", 1_000m, "x"), ("21911", 70m, "ล้าง VAT มัดจำ") });
        Assert.False(net.Shares.Single().Stamped21913);
        // ขาเครดิต/ศูนย์ไม่นับ
        Assert.Equal(0m, DepositReversalMath.SplitDrivesUnrealize(new[] { "REC-9" },
            new (string, decimal, string?)[] { ("21712", 0m, "x") }).Shares.Single().Base);
    }

    // ═════════════════ P-1 — เช็คเอาต์ที่อนุมัติล้ม กดซ้ำใช้ใบร่างเดิม ═════════════════

    private static Document Draft(Action<Document>? tweak = null)
    {
        var d = new Document
        {
            Id = Guid.NewGuid(), CompanyId = Co, DocumentType = DocumentType.TaxInvoice, Status = DocumentStatus.Draft,
            OriginModule = "Lodging", BookingNumber = "RSV-001", Reference = "RSV-001", IsDeposit = false, IsDeleted = false,
            DocumentNumber = "DRAFT-" + Guid.NewGuid().ToString("N"),
        };
        tweak?.Invoke(d);
        return d;
    }

    [Fact]
    public void P1_ใบร่างเช็คเอาต์ที่ค้าง_ถูกพบ_ใบอื่นของการจองเดียวกันไม่ถูกแตะ()
    {
        var leftover = LodgingCheckoutDraft.LeftoverOf(Co, "RSV-001", "Lodging").Compile();
        Assert.True(leftover(Draft()));
        Assert.True(leftover(Draft(d => d.DocumentType = DocumentType.Invoice)));   // ที่พักไม่คิด VAT
        // ทิศตรงข้าม — ห้ามนำไปใช้/ลบ
        Assert.False(leftover(Draft(d => d.Status = DocumentStatus.Approved)));     // ใบที่ออกแล้ว
        Assert.False(leftover(Draft(d => d.IsDeposit = true)));                      // ใบมัดจำ/เงินประกันของการจอง
        Assert.False(leftover(Draft(d => d.Reference = "RC-0001")));                 // ใบกำกับของการริบมัดจำ (อ้างเลขใบมัดจำ)
        Assert.False(leftover(Draft(d => d.BookingNumber = "RSV-002")));
        Assert.False(leftover(Draft(d => d.OriginModule = null)));                   // ใบที่ผู้ใช้สร้างเองจากหน้าเอกสาร
        Assert.False(leftover(Draft(d => d.CompanyId = OtherCo)));
        Assert.False(leftover(Draft(d => d.IsDeleted = true)));
        Assert.False(leftover(Draft(d => d.DocumentType = DocumentType.Receipt)));
    }

    [Fact]
    public void P1_แผนใบร่าง_ใช้ใบใหม่สุดที่ชนิดตรง_ที่เหลือลบ_ไม่มีก็สร้างใหม่()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var plan = LodgingCheckoutDraft.Plan(new[]
        {
            (a, DocumentType.Invoice), (b, DocumentType.TaxInvoice), (c, DocumentType.TaxInvoice),
        }, DocumentType.TaxInvoice);
        Assert.Equal(b, plan.Reuse);
        Assert.Equal(new[] { a, c }, plan.Discard);
        // ชนิดเปลี่ยน (ที่พักเปลี่ยนโหมด VAT) ⇒ ไม่มีใบให้ใช้ต่อ · ลบใบเก่าทั้งหมดแล้วสร้างใหม่
        var changed = LodgingCheckoutDraft.Plan(new[] { (a, DocumentType.TaxInvoice) }, DocumentType.Invoice);
        Assert.Null(changed.Reuse);
        Assert.Equal(new[] { a }, changed.Discard);
        // ครั้งแรก (ไม่มีใบค้าง) ⇒ พฤติกรรมเดิม: สร้างใหม่ ไม่ลบอะไร
        var first = LodgingCheckoutDraft.Plan(Array.Empty<(Guid, DocumentType)>(), DocumentType.TaxInvoice);
        Assert.Null(first.Reuse);
        Assert.Empty(first.Discard);
    }
}
