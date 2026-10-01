using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services;
using Accounting.Services.Implementations.Tax;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม TX (ภาษี/ด่านอนุมัติ/มัดจำ) — เทสต์สองทิศของ A-TX2 · A-TX3 · A-TX5 · A-TX6 · A-TX7 · A-TX9 · B-7
/// (A-TX1 อยู่ใน <see cref="Section65TerApprovalWarningGoldenTests"/> · A-TX8 ใน DepositRound194R2Tests · C-21 ใน PosSlipHeaderTests)
/// </summary>
public class Round201TxTests
{
    // ═════════════════ A-TX2 — tax point ภ.พ.36 (§83/6) = วันจ่าย ═════════════════

    [Fact]
    public void ATX2_บริการต่างประเทศ_ใบผู้ขายลงเดือนก่อน_จ่ายเดือนนี้_ได้เดือนที่จ่าย()
    {
        // ใบแจ้งหนี้ AWS ลง 31/01 · บันทึกใบสำคัญจ่าย (จ่ายเงินสด) 02/02
        var pv = new Document
        {
            DocumentType = DocumentType.PaymentVoucher, IsForeignService = true,
            SupplierTaxInvoiceDate = new DateTime(2026, 1, 31), DocumentDate = new DateTime(2026, 2, 2),
        };
        Assert.Equal(TaxPointResolver.SupplyKind.ReverseCharge, TaxPointResolver.KindForApproval(pv));
        Assert.Equal(new DateTime(2026, 2, 2), TaxPointResolver.Resolve(pv, TaxPointResolver.KindForApproval(pv)));
        // เดิม (Auto) = MIN(วันใบผู้ขาย, …) = ม.ค. ⇒ รายงาน ภ.พ.36 กับหน้านำส่ง (PaymentDate ?? DocumentDate) คนละเดือน
        Assert.Equal(new DateTime(2026, 1, 31), TaxPointResolver.Resolve(pv));
        // รู้วันจ่าย ⇒ วันจ่าย
        var paid = new Document
        {
            DocumentType = DocumentType.PurchaseInvoice, IsForeignService = true,
            DocumentDate = new DateTime(2026, 2, 2), PaymentDate = new DateTime(2026, 3, 5),
        };
        Assert.Equal(new DateTime(2026, 3, 5), TaxPointResolver.Resolve(paid, TaxPointResolver.KindForApproval(paid)));
    }

    [Fact]
    public void ATX2_ทิศตรงข้าม_ซื้อในประเทศ_และใบขาย_กติกาเดิม()
    {
        var domestic = new Document
        {
            DocumentType = DocumentType.PurchaseInvoice, IsForeignService = false,
            SupplierTaxInvoiceDate = new DateTime(2026, 1, 31), DocumentDate = new DateTime(2026, 2, 2),
        };
        Assert.Equal(TaxPointResolver.SupplyKind.Auto, TaxPointResolver.KindForApproval(domestic));
        Assert.Equal(new DateTime(2026, 1, 31), TaxPointResolver.Resolve(domestic, TaxPointResolver.KindForApproval(domestic)));
        // ธงบริการต่างประเทศบนใบขาย (ผิดที่) ไม่ทำให้เป็น §83/6
        var sale = new Document { DocumentType = DocumentType.TaxInvoice, IsForeignService = true, DocumentDate = new DateTime(2026, 2, 2) };
        Assert.False(TaxPointResolver.IsReverseCharge(sale));
    }

    // ═════════════════ A-TX3 — SoD ตัวตัดสินเดียว · เส้นอนุมัติเอกสาร = โหมดเงา ═════════════════

    [Fact]
    public void ATX3_สามสถานะ_คนเดียวกัน_ไม่รู้ผู้ทำ_ผ่าน()
    {
        Assert.Equal(SodVerdict.Pass, ApprovalControlPolicy.SelfApproval(false, "u1", null, "u1"));
        Assert.Equal(SodVerdict.SamePerson, ApprovalControlPolicy.SelfApproval(true, " ABC ", null, "abc"));
        Assert.Equal(SodVerdict.SamePerson, ApprovalControlPolicy.SelfApproval(true, "u1", new[] { null, "u2" }, "u2"));
        Assert.Equal(SodVerdict.MakerUnknown, ApprovalControlPolicy.SelfApproval(true, null, null, "u1"));
        Assert.Equal(SodVerdict.MakerUnknown, ApprovalControlPolicy.SelfApproval(true, "  ", new string?[] { "u3" }, "u1"));
        Assert.Equal(SodVerdict.Pass, ApprovalControlPolicy.SelfApproval(true, "u1", new string?[] { "", null }, "u2"));
    }

    [Fact]
    public void ATX3_เอกสาร_ไม่รู้ผู้ทำ_เงา_ไม่บล็อกแต่บันทึก_รอบโอน_บล็อก()
    {
        // เส้นอนุมัติเอกสาร: โหมดเงา (เข้มขึ้นกับเส้นเดิม) — ห้ามเปลี่ยนเป็นบังคับโดยไม่ผ่านเจ้าของ
        Assert.Equal(SodUnknownMakerMode.Shadow, ApprovalControlPolicy.DocumentUnknownMakerMode);
        Assert.False(ApprovalControlPolicy.SelfApprovalBlocked(true, null, "u1"));
        Assert.True(ApprovalControlPolicy.RecordsShadow(SodVerdict.MakerUnknown, ApprovalControlPolicy.DocumentUnknownMakerMode));
        Assert.True(ApprovalControlPolicy.SelfApprovalBlocked(true, "u1", "U1"));
        Assert.False(ApprovalControlPolicy.RecordsShadow(SodVerdict.SamePerson, SodUnknownMakerMode.Shadow));
        Assert.False(ApprovalControlPolicy.RecordsShadow(SodVerdict.Pass, SodUnknownMakerMode.Shadow));
        // เมื่อเจ้าของสั่งบังคับ: ไม่รู้ผู้ทำ = บล็อก (สูตรเดียวกับรอบโอน)
        Assert.True(ApprovalControlPolicy.BlocksDocumentApproval(SodVerdict.MakerUnknown, SodUnknownMakerMode.Enforce));
        Assert.False(ApprovalControlPolicy.RecordsShadow(SodVerdict.MakerUnknown, SodUnknownMakerMode.Enforce));
        // รอบโอน: "ไม่รู้" บล็อกเสมอ — พฤติกรรมเดิมทุกตัวอักษรผ่านตัวตัดสินเดียว
        var poster = Guid.NewGuid();
        Assert.True(SettlementPostingGate.SodSelfApproval(true, null, poster));
        Assert.True(SettlementPostingGate.SodSelfApproval(true, poster.ToString().ToUpperInvariant() + " ", poster));
        Assert.False(SettlementPostingGate.SodSelfApproval(true, Guid.NewGuid().ToString(), poster));
        Assert.False(SettlementPostingGate.SodSelfApproval(false, null, poster));
        Assert.True(SettlementPostingGate.SodSelfApproval(true, Guid.NewGuid().ToString(), new string?[] { poster.ToString() }, poster));
    }

    // ═════════════════ A-TX5 — แถวมัดจำที่ถูกแก้ก่อนล็อก ต้องล้มดัง ═════════════════

    [Fact]
    public void ATX5_แถวที่แก้ก่อนล็อก_ถูกแยกออก_แถวปกติอ่านใหม่()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var plan = DepositKindDocumentRules.LockReloadPlan(
            new[] { (a, EntityState.Unchanged), (b, EntityState.Modified), (c, EntityState.Modified) }, new[] { a, b });
        Assert.Equal(new[] { a }, plan.Reload);
        Assert.Equal(new[] { b }, plan.ModifiedBeforeLock);   // c ไม่ได้ล็อก ⇒ ไม่เกี่ยว
        Assert.Contains("ล็อก", DepositKindDocumentRules.DepositLockOrderMessage(1));
    }

    [Fact]
    public void ATX5_ทิศตรงข้าม_ทุกแถว_Unchanged_ไม่ล้ม()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var plan = DepositKindDocumentRules.LockReloadPlan(
            new[] { (a, EntityState.Unchanged), (b, EntityState.Unchanged) }, new[] { a, b });
        Assert.Empty(plan.ModifiedBeforeLock);
        Assert.Equal(2, plan.Reload.Count);
    }

    // ═════════════════ A-TX6 — อ้างสองใบแต่ resolve ได้ใบเดียว ═════════════════

    private static readonly (string, decimal, string?)[] TwoDepositLegs =
    {
        ("11100", 4_000m, "รับเงินสด"),
        ("21712", 3_000m, "ตัดขายรอรับรู้ (นำมัดจำ REC-9 มาหักเต็มใบ)"),
        ("21712", 5_000m, "ตัดขายรอรับรู้ (นำมัดจำ REC-10 มาหักเต็มใบ)"),
        ("21913", 350m, "ตัดภาษีขายรอเรียกเก็บ (มัดจำ REC-10 → ใบกำกับ)"),
    };

    [Fact]
    public void ATX6_อ้างสองเลข_หาเจอใบเดียว_ขาของอีกใบไม่ไหลไปใบที่เหลือ()
    {
        // เลขบนใบที่หัก = "REC-9, REC-10" · REC-10 หาไม่เจอแล้ว (ถูกลบ/เลขเปลี่ยน)
        var refs = DepositReversalMath.ParseDepositRefs("REC-9, REC-10").Length;
        var split = DepositReversalMath.SplitDrivesUnrealize(new[] { "REC-9" }, TwoDepositLegs, refs);
        Assert.Equal(3_000m, split.Shares.Single().Base);
        Assert.False(split.Shares.Single().Stamped21913);       // VAT พักเป็นของ REC-10 — ห้ามล้างธงของ REC-9
        Assert.Equal(5_000m, split.Unattributed);
        Assert.Equal(350m, split.UnattributedUndue);             // เดิมข้ามเงียบ
        // เดิม (ไม่ส่งจำนวนเลข) ⇒ ทุกขาเป็นของ REC-9 = คืน 8,000 + ล้างธง VAT ผิดใบ
        var legacy = DepositReversalMath.SplitDrivesUnrealize(new[] { "REC-9" }, TwoDepositLegs);
        Assert.Equal(8_000m, legacy.Shares.Single().Base);
    }

    [Fact]
    public void ATX6_ทิศตรงข้าม_เลขเดียว_สูตรเดิม()
    {
        var legs = new (string, decimal, string?)[] { ("21712", 1_000m, "ตัดขายรอรับรู้ (นำมัดจำมาหัก)"), ("21913", 70m, null) };
        var one = DepositReversalMath.SplitDrivesUnrealize(new[] { "REC-9" }, legs, DepositReversalMath.ParseDepositRefs("REC-9").Length);
        Assert.Equal(1_000m, one.Shares.Single().Base);
        Assert.True(one.Shares.Single().Stamped21913);
        Assert.Equal(0m, one.Unattributed);
        Assert.Equal(0m, one.UnattributedUndue);
    }

    // ═════════════════ A-TX7 — วันที่รับรู้/ริบ ตามปฏิทินไทย ═════════════════

    [Fact]
    public void ATX7_ไม่ระบุวัน_ใช้วันไทย_ข้ามเดือนตอนเช้ามืด()
    {
        // 31/01 17:30 UTC = 01/02 00:30 น. เวลาไทย ⇒ เดือน ก.พ. (เดิม UtcNow = ม.ค.)
        var when = DepositKindDocumentRules.RealizeDateOrToday(null, new DateTime(2026, 1, 31, 17, 30, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 2, 1), when.Date);
        // ทิศตรงข้าม: 16:59 UTC = 23:59 น. วันเดิม ⇒ ม.ค.
        Assert.Equal(new DateTime(2026, 1, 31),
            DepositKindDocumentRules.RealizeDateOrToday(null, new DateTime(2026, 1, 31, 16, 59, 0, DateTimeKind.Utc)).Date);
        // ผู้เรียกระบุวัน ⇒ ใช้ตามนั้น
        var given = new DateTime(2026, 1, 15);
        Assert.Equal(given, DepositKindDocumentRules.RealizeDateOrToday(given, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    // ═════════════════ A-TX9 — JE รอบโอนออกภาษีแทน (โอนสุทธิ 0) ═════════════════

    [Fact]
    public void ATX9_ออกภาษีแทน_ในประเทศและต่างประเทศ_ฐานเงินได้ตาม50ทวิ_ไม่ฟ้อง()
    {
        // W3 3%: ค่าธรรมเนียม 1,000 อยู่ในใบสำคัญจ่าย · JE รอบโอนมีแค่ Dr ภาษีที่ออกแทน 30.93 / Cr 21917 30.93
        var domestic = new List<JournalPostingGuard.LineFacts>
        {
            new("53170", AccountType.Expense, 30.93m, 0m),
            new("21917", AccountType.Liability, 0m, 30.93m),
        };
        Assert.Contains(JournalPostingGuard.Validate(domestic, null), f => f.RuleCode == "JE-WHT-RATIO");   // เดิม: ฟ้อง 100%
        Assert.DoesNotContain(JournalPostingGuard.Validate(domestic, null, 1_030.93m), f => f.RuleCode == "JE-WHT-RATIO");
        // ต่างประเทศ 15% (ภ.ง.ด.54): ภาษีออกแทน 176.47 · เงินได้ 1,176.47
        var foreign = new List<JournalPostingGuard.LineFacts>
        {
            new("53170", AccountType.Expense, 176.47m, 0m),
            new("21918", AccountType.Liability, 0m, 176.47m),
        };
        Assert.DoesNotContain(JournalPostingGuard.Validate(foreign, null, 1_176.47m), f => f.RuleCode == "JE-WHT-RATIO");
    }

    [Fact]
    public void ATX9_ทิศตรงข้าม_เครดิตทั้งใบลงWHT_107เปอร์เซ็นต์_ยังฟ้อง()
    {
        var bad = new List<JournalPostingGuard.LineFacts>
        {
            new("52120", AccountType.Expense, 17_890m, 0m),
            new("11610", AccountType.Asset, 1_252.30m, 0m),
            new("21917", AccountType.Liability, 0m, 19_142.30m),
        };
        Assert.Contains(JournalPostingGuard.Validate(bad, null), f => f.RuleCode == "JE-WHT-RATIO" && f.IsError);
        // ฐานภายนอกเล็ก (ไม่ใช่เงินได้จริงของก้อนนี้) ไม่ทำให้หลุด — ใช้ค่าที่มากกว่า ไม่ใช่บวกจนผ่าน
        Assert.Contains(JournalPostingGuard.Validate(bad, null, 1_000m), f => f.RuleCode == "JE-WHT-RATIO" && f.IsError);
    }

    // ═════════════════ B-7 — ภ.พ.36/ภ.ง.ด.54 เตือนตามวันกระดาษจนกว่าจะยืนยันมาตรการ e-Filing ═════════════════

    [Fact]
    public void B7_ภพ36_ภงด54_เตือนตามวันกระดาษ_แบบอื่นตามวัน_eFiling()
    {
        var (pp36Paper, pp36EFiling) = TaxFilingDeadline.For("VatPp36", 2026, 9);
        Assert.True(pp36Paper < pp36EFiling);                                   // ยังแสดงทั้งสองวัน
        Assert.Equal(pp36Paper, TaxFilingDeadline.WarnBy("VatPp36", 2026, 9));
        Assert.Equal(TaxFilingDeadline.For("WhtPnd54", 2026, 9).Paper, TaxFilingDeadline.WarnByFor(TaxType.WithholdingTax54, 2026, 9));
        Assert.NotNull(TaxFilingDeadline.EFilingCaveat("VatPp36"));
        Assert.NotNull(TaxFilingDeadline.EFilingCaveat("WhtPnd54"));
        // ทิศตรงข้าม: แบบที่ไม่มีข้อสงสัย ⇒ วัน e-Filing ตามเดิม · ไม่มีป้าย
        Assert.Equal(TaxFilingDeadline.For("WhtPnd53", 2026, 9).EFiling, TaxFilingDeadline.WarnBy("WhtPnd53", 2026, 9));
        Assert.Equal(TaxFilingDeadline.For("VatPp30", 2026, 9).EFiling, TaxFilingDeadline.WarnByFor(TaxType.VAT, 2026, 9));
        Assert.Null(TaxFilingDeadline.EFilingCaveat("VatPp30"));
        Assert.Null(TaxFilingDeadline.WarnByFor(TaxType.CorporateIncomeTax, 2026, 9));
    }
}
