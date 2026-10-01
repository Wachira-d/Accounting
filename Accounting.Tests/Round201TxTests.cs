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
    public void ATX5_ทิศตรงข้าม_ล็อกซ้ำในธุรกรรมเดียวกัน_แถวที่แก้หลังล็อกครั้งแรก_ไม่ล้มและไม่อ่านทับ()
    {
        // เส้นหักฐานมัดจำล็อก+แก้ใบ a แล้วเส้นหักแบบขับ JE ล็อก a ซ้ำในการอนุมัติเดียวกัน
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var plan = DepositKindDocumentRules.LockReloadPlan(
            new[] { (a, EntityState.Modified), (b, EntityState.Unchanged) }, new[] { a, b }, new[] { a });
        Assert.Empty(plan.ModifiedBeforeLock);
        Assert.Equal(new[] { b }, plan.Reload);   // a ห้ามอ่านใหม่ (ทับการแก้ใต้ล็อก)
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

    // ═════════════════ ฝ่ายค้านรอบ 201 (RTX) ═════════════════

    private static string S65(string text) => Section65TerApprovalWarnings.Prefix + " (ป.รัษฎากร §65 ตรี (6)) " + text;

    [Fact]
    public void RTX1_ทางเข้าไม่มีคน_ข้อสังเกต65ตรีผ่าน_คำเตือนอื่นยังหยุด()
    {
        var s65 = S65("ค่าปรับ — บวกกลับ 1,000.00");
        const string other = "ใบกำกับภาษีซื้อเกิน 6 เดือน (§82/3) — ต้องระบุเหตุผล";
        // คำตัดสินข้อ 110: ทางเข้าอัตโนมัติ (ใบประจำ · ใบเบิก · LINE · OCR · integration) และ API v1 ไม่ถูกหยุดด้วย §65 ตรี
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, new[] { s65 }));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { s65 }));
        Assert.Null(ApprovalAcknowledgement.ApiRefusal(new[] { s65 }));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.SystemWorkflow, new[] { s65 }));
        // ทิศตรงข้าม: คำเตือนชนิดอื่นยังหยุดทางเข้าไม่มีคน (พฤติกรรมเดิม) · หน้าเว็บ (None) ยังต้องรับทราบ §65 ตรี
        Assert.Equal(new[] { other }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, new[] { s65, other }));
        Assert.Equal(new[] { other }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { s65, other }));
        Assert.Equal(new[] { s65 }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.None, new[] { s65 }));
        // ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ
        Assert.Equal(ApprovalAcknowledgement.UnattendedRuleCode, ApprovalAcknowledgement.RuleCode(ApprovalAckSource.Unattended));
        Assert.False(ApprovalAcknowledgement.AcknowledgedByPerson(ApprovalAckSource.Unattended));
        Assert.Contains("ไม่มีผู้ใช้เห็น", ApprovalAcknowledgement.Note(ApprovalAckSource.Unattended, new[] { s65 }, "system",
            new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void RTX5_ใบเบิก_กดจ่ายซ้ำใช้ใบร่างเดิม_ไม่สร้างซ้ำ()
    {
        var id = Guid.NewGuid();
        Assert.Equal(LinkedPayVoucherStep.Create, LinkedPayVoucher.StepFor(null, null));
        Assert.Equal(LinkedPayVoucherStep.ReuseDraft, LinkedPayVoucher.StepFor(id, DocumentStatus.Draft));
        Assert.Equal(LinkedPayVoucherStep.ReuseDraft, LinkedPayVoucher.StepFor(id, DocumentStatus.WaitingApproval));
        // ผู้ใช้อนุมัติที่หน้าเอกสารแล้ว ⇒ ไม่อนุมัติซ้ำ
        Assert.Equal(LinkedPayVoucherStep.AlreadyIssued, LinkedPayVoucher.StepFor(id, DocumentStatus.Paid));
        Assert.Equal(LinkedPayVoucherStep.AlreadyIssued, LinkedPayVoucher.StepFor(id, DocumentStatus.Approved));
        // ทิศตรงข้าม: ใบเดิมถูกยกเลิก/ปฏิเสธ/หาไม่เจอ ⇒ สร้างใบใหม่
        Assert.Equal(LinkedPayVoucherStep.Create, LinkedPayVoucher.StepFor(id, DocumentStatus.Voided));
        Assert.Equal(LinkedPayVoucherStep.Create, LinkedPayVoucher.StepFor(id, DocumentStatus.Rejected));
        Assert.Equal(LinkedPayVoucherStep.Create, LinkedPayVoucher.StepFor(id, null));
        var msg = LinkedPayVoucher.WarningsMessage(LinkedPayVoucherSource.ExpenseClaim, "DRAFT-1", new[] { "คำเตือน ก" });
        Assert.Contains("DRAFT-1", msg);
        Assert.Contains("ไม่สร้างซ้ำ", msg);
        Assert.Contains("ใบเบิก", msg);
        Assert.Equal("EXPENSE-PAY-PV-WARNINGS", LinkedPayVoucher.WarningsRuleCode(LinkedPayVoucherSource.ExpenseClaim));   // รหัสเดิมไม่เปลี่ยน
    }

    [Fact]
    public void RTX6_เลยวันกระดาษแต่ยังไม่ถึงวัน_eFiling_บอกตรง_ๆ()
    {
        var due = TaxFilingDeadline.For("VatPp36", 2026, 9);
        var between = due.Paper.AddDays(1);
        Assert.True(between <= due.EFiling);
        Assert.Contains("เลยกำหนดแบบกระดาษ", TaxFilingDeadline.EFilingCaveat("VatPp36", due, between));
        // ทิศตรงข้าม: ยังไม่เลยวันกระดาษ / เลยวัน e-Filing ไปแล้ว / แบบที่ไม่มีข้อสงสัย ⇒ ไม่มีประโยคนี้
        Assert.DoesNotContain("เลยกำหนดแบบกระดาษ", TaxFilingDeadline.EFilingCaveat("VatPp36", due, due.Paper)!);
        Assert.DoesNotContain("เลยกำหนดแบบกระดาษ", TaxFilingDeadline.EFilingCaveat("VatPp36", due, due.EFiling.AddDays(1))!);
        Assert.Null(TaxFilingDeadline.EFilingCaveat("WhtPnd53", TaxFilingDeadline.For("WhtPnd53", 2026, 9), between));
        // วันหยุดราชการไหลเข้าวันที่ใช้เตือนผ่านตัวเดียว
        var holidays = new HashSet<DateTime> { due.Paper.Date };
        Assert.True(TaxFilingDeadline.WarnByFor(TaxType.VatPp36, 2026, 9, holidays) > due.Paper);
    }

    // ═════════════════ ฝ่ายค้านรอบ 201 รอบสาม (P1-2 · P2-2/3/4) ═════════════════

    private static readonly Guid P12Acc = Guid.NewGuid();

    private static Section65TerValidator.Result EvalTyped(string desc, decimal amount, string code, AccountType? type)
    {
        var doc = new Document
        {
            DocumentType = DocumentType.Expense, TotalAmount = amount,
            Lines = new List<DocumentLine> { new() { AccountId = P12Acc, Amount = amount, VatAmount = 0m, Description = desc } },
        };
        var info = new Dictionary<Guid, (string Code, string Name)> { [P12Acc] = (code, "บัญชีทดสอบ") };
        var types = type.HasValue ? new Dictionary<Guid, AccountType> { [P12Acc] = type.Value } : null;
        return Section65TerValidator.Evaluate(doc, info, "บจก. ผู้รับ", "0105551234567",
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, PriorYtdEntertainmentExpense: 0m, HasSourceDocument: true),
            accountTypes: types);
    }

    [Fact]
    public void P12_ผังผู้ใช้สร้าง_6100_ชนิดค่าใช้จ่าย_ถูกตรวจ()
    {
        // ผังนำเข้า/สร้างเอง "6100 ค่าปรับ" ชนิด Expense — เดิมดูเลขนำหน้า 5 อย่างเดียว ⇒ หลุดการบวกกลับเงียบ
        var penalty = EvalTyped("ค่าปรับจราจร", 1_000m, "6100", AccountType.Expense);
        Assert.Contains(penalty.Findings, f => f.RuleCode == "RD-65ter(6)" && f.AddBackAmount == 1_000m);
        Assert.Equal(1_000m, penalty.TotalAddBack);
        // (6 ทวิ) · (3) · (5) capex ก็ตัดสินด้วยชนิดผังเดียวกัน
        Assert.Contains(EvalTyped("ภาษีเงินได้นิติบุคคล", 5_000m, "6900", AccountType.Expense).Findings, f => f.RuleCode == "RD-65ter(6bis)");
        Assert.Contains(EvalTyped("ค่าใช้จ่ายส่วนตัวกรรมการ", 4_000m, "6200", AccountType.Expense).Findings, f => f.RuleCode == "RD-65ter(3)");
        Assert.Contains(EvalTyped("เครื่องจักรใหม่", 80_000m, "6300", AccountType.Expense).Findings, f => f.RuleCode == "RD-65ter(5)");
        // ผังมาตรฐาน 5xxxx ชนิด Expense ยังตรวจเหมือนเดิม
        Assert.Equal(1_000m, EvalTyped("ค่าปรับจราจร", 1_000m, "53700", AccountType.Expense).TotalAddBack);
    }

    [Fact]
    public void P12_ทิศตรงข้าม_หนี้สิน_สินทรัพย์_ทุน_ไม่บวกกลับ()
    {
        // ชำระ ภ.ง.ด.50/51 · ถอนใช้ส่วนตัว — ไม่ใช่รายจ่ายของงวด แม้คำอธิบายมีคำต้องห้าม
        Assert.Equal(0m, EvalTyped("ชำระภาษีเงินได้นิติบุคคล ภ.ง.ด.50 พร้อมเงินเพิ่ม", 300_000m, "21920", AccountType.Liability).TotalAddBack);
        Assert.Equal(0m, EvalTyped("ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า", 150_000m, "11920", AccountType.Asset).TotalAddBack);
        Assert.Equal(0m, EvalTyped("ถอนใช้ส่วนตัวหุ้นส่วน", 20_000m, "31200", AccountType.Equity).TotalAddBack);
        // ชนิดผังชนะเลขนำหน้า: ผังเลข 5 ที่ผู้ใช้ตั้งชนิดเป็นหนี้สิน ไม่ถูกบวกกลับ · capex ไม่ฟ้องบัญชีสินทรัพย์
        Assert.Equal(0m, EvalTyped("ค่าปรับค้างจ่าย", 1_000m, "5999", AccountType.Liability).TotalAddBack);
        Assert.DoesNotContain(EvalTyped("เครื่องจักรใหม่", 80_000m, "12400", AccountType.Asset).Findings, f => f.RuleCode == "RD-65ter(5)");
        // ไม่รู้ชนิด (ผู้เรียกเก่า) = ทางสำรองเลขนำหน้าเดิม — 21920 ไม่ตรวจ · 53700 ตรวจ
        Assert.Equal(0m, EvalTyped("ค่าปรับ", 1_000m, "21920", null).TotalAddBack);
        Assert.Equal(1_000m, EvalTyped("ค่าปรับจราจร", 1_000m, "53700", null).TotalAddBack);
    }

    [Fact]
    public void P22_ข้อสังเกต65ตรีที่ผ่าน_คืนให้คนที่กดเห็น()
    {
        var s65 = S65("ค่าปรับ — บวกกลับ 1,000.00 [RD-65ter(6)]");
        const string other = "ใบกำกับภาษีซื้อเกิน 6 เดือน (§82/3) — ต้องระบุเหตุผล";
        var notice = Section65TerApprovalWarnings.PassedNotice(new[] { s65, other });
        Assert.NotNull(notice);
        Assert.Contains("1 ข้อ", notice);
        Assert.Contains("ไม่บล็อก", notice);
        Assert.DoesNotContain("§82/3", notice);   // คืนเฉพาะชุด §65 ตรี
        Assert.Equal(new[] { s65 }, Section65TerApprovalWarnings.PassedNotes(new[] { other, s65 }));
        // ทิศตรงข้าม: ไม่มีข้อสังเกต = ไม่มีข้อความ (ใบปกติไม่ขึ้นป้ายเตือน)
        Assert.Null(Section65TerApprovalWarnings.PassedNotice(new[] { other }));
        Assert.Null(Section65TerApprovalWarnings.PassedNotice(Array.Empty<string>()));
        Assert.Null(Section65TerApprovalWarnings.PassedNotice(null));
        Assert.Empty(Section65TerApprovalWarnings.PassedNotes(null));
    }

    [Fact]
    public void P23_เงินทดรอง_ใช้ตัวตัดสินเดียวกับใบเบิก_ข้อความบอกทางไปต่อของตัวเอง()
    {
        var id = Guid.NewGuid();
        Assert.Equal(LinkedPayVoucherStep.ReuseDraft, LinkedPayVoucher.StepFor(id, DocumentStatus.Draft));
        Assert.Equal(LinkedPayVoucherStep.AlreadyIssued, LinkedPayVoucher.StepFor(id, DocumentStatus.Approved));
        Assert.Equal(LinkedPayVoucherStep.Create, LinkedPayVoucher.StepFor(id, DocumentStatus.Voided));
        var msg = LinkedPayVoucher.WarningsMessage(LinkedPayVoucherSource.SalaryAdvance, "DRAFT-9", new[] { "คำเตือน ข" });
        Assert.Contains("เงินทดรอง", msg);
        Assert.Contains("DRAFT-9", msg);
        Assert.Contains("ไม่สร้างซ้ำ", msg);
        Assert.DoesNotContain("ใบเบิก", msg);
        Assert.Equal("ADVANCE-PAY-PV-WARNINGS", LinkedPayVoucher.WarningsRuleCode(LinkedPayVoucherSource.SalaryAdvance));
    }

    [Fact]
    public void P24_มาตราอ้างอิงตามชุดคำเตือนที่ผ่านจริง()
    {
        var s65a = Section65TerApprovalWarnings.Prefix + " (ป.รัษฎากร §65 ตรี (6)) ค่าปรับ (ไม่บล็อก — ตรวจก่อนอนุมัติ) [RD-65ter(6)]";
        var s65b = Section65TerApprovalWarnings.Prefix + " (ป.รัษฎากร §65 ตรี (3)) ส่วนตัว (ไม่บล็อก — ตรวจก่อนอนุมัติ) [RD-65ter(3)]";
        const string other = "ใบกำกับภาษีซื้อเกิน 6 เดือน (§82/3) — ต้องระบุเหตุผล";
        // ทางเข้าอัตโนมัติ/API ผ่านได้แค่ชุด §65 ตรี ⇒ อ้าง §65 ตรีตามข้อจริง ไม่ใช่ §86/§82/5(1)
        Assert.Equal("RD-65ter(6) · RD-65ter(3)", ApprovalAcknowledgement.LegalReference(new[] { s65a, s65b, s65a }));
        Assert.Equal("RD-65ter(11)(18)", Section65TerApprovalWarnings.RuleCodeOf(
            Section65TerApprovalWarnings.Prefix + " (ป.รัษฎากร §65 ตรี (11)(18)) x [RD-65ter(11)(18)]"));
        // ทิศตรงข้าม: คนรับทราบคำเตือนทั่วไป ⇒ มาตราทั่วไปเดิม · ผสมกัน ⇒ ทั้งสองชุด
        Assert.Equal(ApprovalAcknowledgement.GeneralWarningsLegalReference, ApprovalAcknowledgement.LegalReference(new[] { other }));
        Assert.Equal("RD-65ter(6) · " + ApprovalAcknowledgement.GeneralWarningsLegalReference,
            ApprovalAcknowledgement.LegalReference(new[] { s65a, other }));
        Assert.Null(Section65TerApprovalWarnings.RuleCodeOf(other));
        Assert.Equal(Section65TerApprovalWarnings.LegalReferenceFallback,
            ApprovalAcknowledgement.LegalReference(new[] { Section65TerApprovalWarnings.Prefix + " ไม่มีรหัส" }));
    }
}
