using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 194 ทีม M2 — แก้ผลฝ่ายค้านรอบสอง (<c>erp-review/2026-09-25/review194-r2.md</c> R2-1…R2-6 + P-a · P1 ค้าง · RevertTrackedChanges)
/// ทุกข้อล็อกสองครึ่ง: <b>ใบที่พังกลับมาถูก</b> · <b>ใบที่ถูกอยู่แล้วไม่ถูกแตะ</b> — ตัวเลขใช้ตัวอย่างจากรายงานฝ่ายค้านตรง ๆ
/// (มัดจำเต็มยอด 10,700 รับ 10/01/2026 ริบ 20/08/2026 · มัดจำ 10,000 → F 3,000 + X 7,000 · เงินประกัน 5,000)
/// </summary>
public class DepositRound194R2Tests
{
    private static readonly Guid Co = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherCo = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Dep = Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555");

    // ═════════════════ R2-1 — "ไม่มีแถวยื่นในระบบ" ≠ "ยังไม่ยื่น" ═════════════════

    [Fact]
    public void R21_รับเงินมกราริบสิงหา_ไม่มีแถวยื่นในระบบ_เลยกำหนดแล้ว_เข้างวดปัจจุบันพร้อมธงตรงความจริง()
    {
        // รายงาน: บริษัทยื่น ภ.พ.30 ทาง RD นอกระบบ · มัดจำเต็มยอด 10,700 รับ 10/01/2026 · ริบ 20/08/2026
        // เดิม: ไม่มีแถวยื่น ⇒ tax point 10/01 ⇒ รายงาน ส.ค. คัดออก · ม.ค. ยื่นไปแล้ว ⇒ VAT 700 ไม่อยู่ในแบบใดเลย + "ไม่ต้องยื่นเพิ่มเติม"
        var tp = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2026, 1, 10), new DateTime(2026, 8, 20),
            depositPeriodLocked: false, today: new DateTime(2026, 8, 20));
        Assert.Equal(new DateTime(2026, 8, 20), tp.TaxPointDate);
        Assert.True(tp.LateFlag);
        Assert.StartsWith(DepositPolicyResolver.LateVatMarker, tp.Note);
        Assert.Contains("ถึงกำหนดงวด 01/2026", tp.Note);
        Assert.Contains("นำส่งในงวด 08/2026", tp.Note);
        Assert.Contains("§89/1", tp.Note);
        Assert.Contains("ห้ามนำส่งซ้ำ", tp.Note);
        Assert.Contains("ปรึกษานักบัญชี", tp.Note);
        Assert.Contains("ระบบไม่รู้ว่ายื่นนอกระบบ", tp.Note);
        Assert.DoesNotContain("ไม่ต้องยื่นเพิ่มเติม", tp.Note);
    }

    [Fact]
    public void R21_กำหนดยื่นมาจากตารางกลางตัวเดียว_แบบกระดาษ_ขอบเขตวันกำหนดยื่น()
    {
        var deadline = TaxFilingDeadline.For("VatPp30", 2026, 8).Paper;
        Assert.Equal(new DateTime(2026, 9, 15), deadline);   // อังคาร — ไม่ต้องเลื่อน
        // วันกำหนดเอง = ยังไม่เลย ⇒ เข้างวดเดือนรับเงิน
        var onDay = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2026, 8, 10), new DateTime(2026, 9, 12),
            depositPeriodLocked: false, today: deadline);
        Assert.Equal(new DateTime(2026, 8, 10), onDay.TaxPointDate);
        Assert.False(onDay.LateFlag);
        // วันถัดไป = เลยแล้ว (อาจยื่นนอกระบบแล้ว) ⇒ งวดปัจจุบัน + ธง แม้วันที่ในใบ (ริบ) จะอยู่ก่อนกำหนด
        var dayAfter = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2026, 8, 10), new DateTime(2026, 9, 12),
            depositPeriodLocked: false, today: deadline.AddDays(1));
        Assert.Equal(new DateTime(2026, 9, 12), dayAfter.TaxPointDate);
        Assert.True(dayAfter.LateFlag);
        // เลื่อนวันหยุดตามตารางกลาง: งวด ม.ค. 2026 ครบ 15/02 (อาทิตย์) ⇒ 16/02
        Assert.Equal(new DateTime(2026, 2, 16), TaxFilingDeadline.For("VatPp30", 2026, 1).Paper);
        Assert.False(DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2026, 1, 10), new DateTime(2026, 2, 3),
            false, new DateTime(2026, 2, 16)).LateFlag);
    }

    [Fact]
    public void R21_ยื่นหรือล็อกหรือปิดงวดในระบบแล้ว_และข้ามปีภาษี_งวดปัจจุบันเสมอ()
    {
        var locked = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2026, 8, 28), new DateTime(2026, 9, 3),
            depositPeriodLocked: true, today: new DateTime(2026, 9, 3));
        Assert.Equal(new DateTime(2026, 9, 3), locked.TaxPointDate);
        Assert.True(locked.LateFlag);
        Assert.Contains("ยื่น/ปิดในระบบแล้ว", locked.Note);
        // ข้ามปีภาษี — แม้ยังไม่เลยกำหนดยื่นงวด ธ.ค. (15/01) = งวดปัจจุบันเสมอ
        var crossYear = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2025, 12, 20), new DateTime(2026, 1, 5),
            depositPeriodLocked: false, today: new DateTime(2026, 1, 5));
        Assert.Equal(new DateTime(2026, 1, 5), crossYear.TaxPointDate);
        Assert.True(crossYear.LateFlag);
        Assert.Contains("ข้ามปีภาษี", crossYear.Note);
    }

    [Fact]
    public void R21_ทิศตรงข้าม_เดือนเดียวกัน_และเงินประกันที่หักเป็นค่าธรรมเนียม_ไม่ติดธง()
    {
        // เดือนเดียวกันและไม่มีแถวยื่น — วันนี้เลยกำหนดไปแล้วก็ไม่เกี่ยว (ใบกำกับของการริบลงวันที่ในเดือนนั้นอยู่แล้ว)
        var same = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2026, 8, 2), new DateTime(2026, 8, 25),
            depositPeriodLocked: false, today: new DateTime(2026, 9, 25));
        Assert.Equal(new DateTime(2026, 8, 2), same.TaxPointDate);
        Assert.False(same.LateFlag);
        Assert.Null(same.Note);
        var notLate = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, false, new DateTime(2026, 1, 10), new DateTime(2026, 8, 20),
            depositPeriodLocked: false, today: new DateTime(2026, 8, 20));
        Assert.Equal(new DateTime(2026, 8, 20), notLate.TaxPointDate);
        Assert.False(notLate.LateFlag);
        Assert.Null(notLate.Note);
    }

    [Fact]
    public void R21_RecognizedAtต้องเดือนเดียวกับJEที่Cr21911_ตัวเทียบงวด()
    {
        // เส้นย้าย VAT พัก: tax point เดือนรับเงิน (ส.ค.) แต่ริบวันที่ ก.ย. ⇒ ต่างงวด ⇒ ขา 21913→21911 แยก JE ลงวัน tax point
        Assert.False(DepositPolicyResolver.SameVatPeriod(new DateTime(2026, 8, 28), new DateTime(2026, 9, 3)));
        Assert.True(DepositPolicyResolver.SameVatPeriod(new DateTime(2026, 9, 2), new DateTime(2026, 9, 30)));
        Assert.False(DepositPolicyResolver.SameVatPeriod(new DateTime(2025, 9, 2), new DateTime(2026, 9, 2)));
    }

    // ═════════════════ R2-2 — ใบเดิม (NULL · VAT 0 · ธง false) = กำกวม ═════════════════

    [Fact]
    public void R22_ใบเดิมเต็มยอดก่อน2409_ไม่ระบุ_คิดVATทิศปลอดภัย_ออกใบกำกับพร้อมธง()
    {
        var f = DepositPolicyResolver.ForfeitVatDecision(null, null, 0m, vatPendingUnrecognized: false,
            companyVatRate: 7m, depositOutputVatDeferred: false);
        Assert.Equal(DepositForfeitVatAction.IssueTaxInvoiceForForfeit, f.Action);
        Assert.Equal(7m, f.ForfeitInvoiceVatRate);
        Assert.True(f.LateVat);
        Assert.DoesNotContain("VAT 0 โดยชอบ", f.Explanation);
        Assert.Null(DepositPolicyResolver.ForfeitZeroVatDeferred(null, false, null));   // กำกวม
    }

    [Fact]
    public void R22_ใบเดิมกำกวม_ผู้ใช้ยืนยันไม่มีVATมาแต่แรก_ไม่ออกใบกำกับ_ไม่มีธง()
    {
        var f = DepositPolicyResolver.ForfeitVatDecision(null, DepositForfeitAs.OriginallyNoVat, 0m, false,
            companyVatRate: 7m, depositOutputVatDeferred: false);
        Assert.Equal(DepositForfeitVatAction.ZeroVatAtIssue, f.Action);
        Assert.Equal(DepositForfeitAs.OriginallyNoVat, f.EffectiveAs);
        Assert.False(f.LateVat);
        Assert.False(f.RequestIgnored);
        Assert.Contains("ผู้ใช้ยืนยัน", f.Explanation);
        // ตัวเลือกบนหน้าศูนย์มัดจำ: สามข้อ · ค่าเริ่มต้น = มี VAT · ข้อใหม่ส่งเป็นชื่อ enum
        var opts = DepositKindDocumentRules.ForfeitOptions(null, 0m, false, 7m, depositOutputVatDeferred: false);
        Assert.NotNull(opts);
        Assert.Equal(3, opts!.Count);
        Assert.Equal(nameof(DepositForfeitAs.PriceOrFee), Assert.Single(opts, o => o.IsDefault).Value);
        var noVat = Assert.Single(opts, o => o.Value == nameof(DepositForfeitAs.OriginallyNoVat));
        Assert.False(noVat.IsDefault);
        Assert.Contains("ไม่มี VAT มาแต่แรก", noVat.Label);
        Assert.Equal(3, (int)DepositForfeitAs.OriginallyNoVat);   // persist/ส่งเป็นชื่อ — เลขต้องคงที่
    }

    [Fact]
    public void R22_ทิศตรงข้าม_ใบที่รู้แน่ไม่ถูกแตะ_และคำยืนยันกับใบที่เลื่อนVATไม่มีผลต้องบอก()
    {
        // ใบรอบ 194+ (มีลักษณะเงิน) ธง false = VAT 0 โดยชอบ — ไม่ถามข้อ "ไม่มี VAT มาแต่แรก"
        Assert.Equal(DepositForfeitVatAction.ZeroVatAtIssue, DepositPolicyResolver.ForfeitVatDecision(
            DepositNature.PartOfPrice, null, 0m, false, 7m, depositOutputVatDeferred: false).Action);
        // ช่องทางไม่คิด VAT (ที่พัก ChargeVat=false) — ใบเดิมไม่มีลักษณะก็รู้แน่
        Assert.Equal(DepositForfeitVatAction.ZeroVatAtIssue, DepositPolicyResolver.ForfeitVatDecision(
            null, null, 0m, false, 7m, depositOutputVatDeferred: false, channelVatRate: 0m).Action);
        // ใบที่ธงบอกว่าเลื่อน VAT (มัดจำเต็มยอดจริง) — ยืนยัน "ไม่มี VAT มาแต่แรก" ไม่มีผล (ห้าม silent no-op)
        var ignored = DepositPolicyResolver.ForfeitVatDecision(DepositNature.PartOfPrice, DepositForfeitAs.OriginallyNoVat, 0m, true,
            7m, depositOutputVatDeferred: true);
        Assert.Equal(DepositForfeitVatAction.IssueTaxInvoiceForForfeit, ignored.Action);
        Assert.True(ignored.RequestIgnored);
        Assert.Contains("ไม่มีผล", ignored.Explanation);
        // ใบที่มี VAT พัก — ยืนยันไม่มีผล (ย้าย 21913→21911 ตามเดิม)
        var pending = DepositPolicyResolver.ForfeitVatDecision(null, DepositForfeitAs.OriginallyNoVat, 65.42m, true,
            7m, depositOutputVatDeferred: true);
        Assert.Equal(DepositForfeitVatAction.ReclassifyUndueToDue, pending.Action);
        Assert.True(pending.RequestIgnored);
        // ใบเดิมที่ธงเลื่อน = true: ถามสองข้อเหมือนเดิม (ไม่มีข้อ "ไม่มี VAT มาแต่แรก")
        var opts = DepositKindDocumentRules.ForfeitOptions(null, 0m, true, 7m, depositOutputVatDeferred: true);
        Assert.Equal(2, opts!.Count);
        Assert.DoesNotContain(opts, o => o.Value == nameof(DepositForfeitAs.OriginallyNoVat));
    }

    [Fact]
    public void R22_รับรู้ตามปกติ_ใบเดิมกำกวม_ปฏิเสธพร้อมทางไปต่อของใบที่ไม่มีVATจริง()
    {
        var p = DepositPolicyResolver.PlainRealizeProblem(null, 0m, false, 7m);
        Assert.NotNull(p);
        Assert.Contains(DepositPolicyResolver.PlainRealizeRuleCode, p);
        Assert.Contains("ไม่มี VAT มาแต่แรก", p);
        Assert.Contains("หักมัดจำ", p);
        // ใบที่รู้แน่ว่า VAT 0 ไม่ถูกปฏิเสธ
        Assert.Null(DepositPolicyResolver.PlainRealizeProblem(DepositNature.PartOfPrice, 0m, false, 7m));
        Assert.Null(DepositPolicyResolver.PlainRealizeProblem(null, 0m, false, 7m, channelVatRate: 0m));
    }

    // ═════════════════ R2-3 — เงินประกัน "ส่งมอบแล้ว" ═════════════════

    [Fact]
    public void R23_เงินประกัน5000_ส่งมอบแล้ว_ปฏิเสธพร้อมสามทางไปต่อ_ทุกโหมดVAT()
    {
        // เดิม: Dr 21530 5,000 / Cr 41000 5,000 (รายได้ขายไม่มี VAT)
        foreach (var (vat, deferred) in new (decimal, bool)[] { (0m, true), (0m, false), (327.10m, false), (327.10m, true) })
        {
            var p = DepositPolicyResolver.PlainRealizeProblem(DepositNature.RefundableSecurity, vat, deferred, 7m);
            Assert.NotNull(p);
            Assert.Contains(DepositPolicyResolver.SecurityPlainRealizeRuleCode, p);
            Assert.Contains("คืนเงินประกัน", p);                 // ทางไปต่อ ①
            Assert.Contains("ตัดชำระด้วยเงินประกัน", p);         // ทางไปต่อ ②
            Assert.Contains("ค่าเสียหายแท้", p);                  // ทางไปต่อ ③
        }
        Assert.False(DepositPolicyResolver.PlainRealizeOffered(DepositNature.RefundableSecurity));
    }

    [Fact]
    public void R23_ทิศตรงข้าม_มัดจำอื่นยังเสนอส่งมอบแล้ว_และริบเงินประกันเป็นค่าเสียหายยังได้()
    {
        Assert.True(DepositPolicyResolver.PlainRealizeOffered(null));
        Assert.True(DepositPolicyResolver.PlainRealizeOffered(DepositNature.PartOfPrice));
        Assert.True(DepositPolicyResolver.PlainRealizeOffered(DepositNature.NonVatSupply));
        Assert.Equal(DepositForfeitVatAction.CompensationNoVat, DepositPolicyResolver.ForfeitVatDecision(
            DepositNature.RefundableSecurity, DepositForfeitAs.Compensation, 0m, true, 7m, depositOutputVatDeferred: true).Action);
    }

    // ═════════════════ R2-4 — ยกเลิกใบมัดจำที่ตัดชำระหลายใบ ═════════════════

    [Fact]
    public void R24_มัดจำ10000_F3000_X7000_ยกเลิกมัดจำ_คืนยอดจ่ายรายใบ()
    {
        var byTarget = DepositApplyJournals.GrossByTarget(new (string?, decimal)[] { ("TIV-F", 3000m), ("TIV-X", 7000m) });
        Assert.Equal(3000m, byTarget["TIV-F"]);
        Assert.Equal(7000m, byTarget["TIV-X"]);
        var x = DepositApplyJournals.AfterRestore(7000m, 7000m, byTarget["TIV-X"]);
        Assert.Equal((0m, 7000m, DocumentStatus.Approved), x);
        var fRestored = DepositApplyJournals.AfterRestore(3000m, 3000m, byTarget["TIV-F"]);
        Assert.Equal((0m, 3000m, DocumentStatus.Approved), fRestored);   // เดิม F ค้าง Paid 3,000 ขณะ GL เปิดลูกหนี้ F 3,000
        // เดิม: หักรวม 10,000 จาก X ใบเดียว
        Assert.Equal(0m, Math.Max(0m, 7000m - 10000m));
    }

    [Fact]
    public void R24_ทิศตรงข้าม_JVหลายขาของใบเดียวรวมกัน_ขาว่างไม่นับ_ใบที่จ่ายอื่นด้วยเหลือยอดจริง()
    {
        var byTarget = DepositApplyJournals.GrossByTarget(new (string?, decimal)[]
            { ("INV-1", 1000m), ("INV-1", 500m), (null, 99m), ("", 99m), ("INV-2", 0m) });
        Assert.Single(byTarget);
        Assert.Equal(1500m, byTarget["INV-1"]);
        // ใบ 10,000 จ่ายด้วยมัดจำ 3,000 + โอน 7,000 ⇒ คืนเฉพาะ 3,000 · สถานะเป็นจ่ายบางส่วน
        Assert.Equal((7000m, 3000m, DocumentStatus.PartiallyPaid), DepositApplyJournals.AfterRestore(10000m, 10000m, 3000m));
        // ไม่มีอะไรคืน ⇒ คงจ่ายครบ
        Assert.Equal((5000m, 0m, DocumentStatus.Paid), DepositApplyJournals.AfterRestore(5000m, 5000m, 0m));
    }

    // ═════════════════ R2-5 — void แล้ว purge ═════════════════

    private static JournalEntry Je(string reference, Guid? source = null, Guid? company = null, Guid? original = null,
        Guid? reversedBy = null, bool deleted = false, JournalEntryStatus status = JournalEntryStatus.Posted) => new()
    {
        CompanyId = company ?? Co, Reference = reference, SourceDocumentId = source ?? Dep, OriginalEntryId = original,
        ReversedByEntryId = reversedBy, IsDeleted = deleted, Status = status, EntryNumber = "JV-1",
    };

    [Fact]
    public void R25_ใบที่เคยvoid_JVต้นฉบับถูกกลับแล้ว_purgeไม่เจอ_ไม่ลบต้นฉบับ_ไม่หักรับรู้ซ้ำ()
    {
        var original = Je("TIV-X", reversedBy: Guid.NewGuid());
        var reversal = Je("TIV-X", original: original.Id);
        var appliedFrom = DepositApplyJournals.AppliedFromDepositTo(Co, Dep, "TIV-X").Compile();
        var appliedTo = DepositApplyJournals.AppliedTo(Co, "TIV-X").Compile();
        Assert.False(appliedFrom(original));
        Assert.False(appliedFrom(reversal));
        Assert.False(appliedTo(original));   // DepositsAppliedToAsync ไม่หาใบมัดจำจาก JV ที่ถูกกลับแล้ว
        Assert.False(appliedTo(reversal));
        Assert.Empty(new[] { original, reversal }.Where(appliedFrom));   // ⇒ ไม่มีอะไรถูกลบ · restoredBase = 0
    }

    [Fact]
    public void R25_ทิศตรงข้าม_JVที่ยังมีผลยังถูกพบ_และตัวกรองแยกบริษัท_เลขใบ_ลบ_ร่าง()
    {
        var live = Je("TIV-X");
        var appliedFrom = DepositApplyJournals.AppliedFromDepositTo(Co, Dep, "TIV-X").Compile();
        var appliedTo = DepositApplyJournals.AppliedTo(Co, "TIV-X").Compile();
        var liveOfSource = DepositApplyJournals.LiveOfSource(Co, Dep).Compile();
        Assert.True(appliedFrom(live));
        Assert.True(appliedTo(live));
        Assert.True(liveOfSource(live));
        Assert.False(appliedFrom(Je("TIV-X", company: OtherCo)));
        Assert.False(appliedTo(Je("TIV-X", company: OtherCo)));
        Assert.False(appliedFrom(Je("TIV-Y")));
        Assert.False(appliedFrom(Je("TIV-X", source: Guid.NewGuid())));
        Assert.False(appliedFrom(Je("TIV-X", deleted: true)));
        Assert.False(appliedFrom(Je("TIV-X", status: JournalEntryStatus.Draft)));
        // ขั้น 2 ของการยกเลิก: JE ที่ถูกกลับไปแล้วไม่ถูกส่งไปกลับซ้ำ (เดิมโยน "ถูกกลับรายการไปแล้ว" ⇒ ยกเลิกใบมัดจำไม่ได้)
        Assert.False(liveOfSource(Je("TIV-X", reversedBy: Guid.NewGuid())));
        Assert.False(liveOfSource(Je("TIV-X", original: Guid.NewGuid())));
    }

    // ═════════════════ R2-6 — ข้อความปฏิเสธ one-shot ต้องมีทางไปต่อ ═════════════════

    [Fact]
    public void R26_ข้อความปฏิเสธมีทางไปต่อ_ไม่ใช่ทางตัน_ข้อความเดียวทุกเส้น()
    {
        var m = DepositKindDocumentRules.AppliedElsewhereMessage("RV-1", "TIV-9", "หักมัดจำแบบขับ JE");
        Assert.StartsWith("หักมัดจำแบบขับ JE: ", m);
        Assert.Contains("RV-1", m);
        Assert.Contains("TIV-9", m);
        Assert.Contains("ทางไปต่อ", m);
        Assert.Contains("ยกเลิกใบนั้นก่อน", m);
        Assert.Contains("ริบมัดจำ", m);
        Assert.StartsWith("มัดจำ RV-1", DepositKindDocumentRules.AppliedElsewhereMessage("RV-1", "TIV-9"));   // ไม่มีชื่อเส้น = ไม่มีคำนำหน้า
        // ผ่อนเฉพาะใบกำกับของการริบของมัดจำใบนี้ (ตัวเดียวกับตัดชำระ) — ใบสุดท้ายสองใบยังเป็น one-shot
        Assert.True(DepositKindDocumentRules.ApplyToAnotherTargetAllowed(priorIsForfeitInvoiceOfDeposit: true, targetIsForfeitInvoiceOfDeposit: false));
        Assert.False(DepositKindDocumentRules.ApplyToAnotherTargetAllowed(false, false));
    }

    // ═════════════════ P-a — คีย์ล็อกเดียวของทุกทางเข้า ═════════════════

    [Fact]
    public void Pa_คีย์ล็อกยอดใบมัดจำ_ตรงกับsessionlockของปุ่ม_แยกใบแยกบริษัท()
    {
        var key = AdvisoryLockKey.DepositRealizeKey(Co, Dep);
        Assert.Equal(AdvisoryLockKey.For(Co, AdvisoryLockKey.DepositRealize, Dep.ToString()), key);   // JobLock.RunExclusiveAsync ใช้สูตรนี้
        Assert.NotEqual(key, AdvisoryLockKey.DepositRealizeKey(OtherCo, Dep));
        Assert.NotEqual(key, AdvisoryLockKey.DepositRealizeKey(Co, Guid.NewGuid()));
        Assert.Equal(key, AdvisoryLockKey.DepositRealizeKey(Co, Dep));   // deterministic (ห้ามสุ่มต่อ process)
        Assert.Contains("รอสักครู่", DepositKindDocumentRules.DepositBusyMessage);
    }

    // ═════════════════ RevertTrackedChanges — ถอยโดยไม่ดึง entity ใหม่กลับมา ═════════════════

    private static AccountingDbContext NewContext()
        => new(new DbContextOptionsBuilder<AccountingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);

    [Fact]
    public void Revert_entityใหม่ที่ผูกผ่านnavigationแต่ยังไม่ถูกตรวจ_ถูกปลดและตัด_ไม่กลับมาเป็นแถวใหม่()
    {
        using var db = NewContext();
        var dep = new Document { Id = Dep, CompanyId = Co, DocumentNumber = "RV-1", IsDeposit = true };
        db.Attach(dep);
        var baseline = new HashSet<object>(db.ChangeTracker.Entries().Select(e => e.Entity), ReferenceEqualityComparer.Instance);
        // ขั้นที่ล้มทิ้งไว้: บรรทัดใหม่ที่ผูกผ่าน collection (ยังไม่เคยถูกตรวจ) + JE ใหม่ที่ Add แล้ว
        var line = new DocumentLine { DocumentId = Dep, Description = "x", Quantity = 1m, UnitPrice = 1m };
        dep.Lines.Add(line);
        var je = new JournalEntry { CompanyId = Co, EntryNumber = "JV-9", SourceDocumentId = Dep };
        db.JournalEntries.Add(je);

        var kept = TrackedChangeRevert.DetachSince(db, baseline);

        Assert.Same(dep, Assert.Single(kept).Entity);
        Assert.Equal(EntityState.Detached, db.Entry(je).State);
        Assert.Empty(dep.Lines);
        db.ChangeTracker.DetectChanges();   // ที่ SaveChanges ของขั้นล้มดังทำ
        Assert.Single(db.ChangeTracker.Entries());
        Assert.Equal(0, TrackedChangeRevert.DetachStrays(db, baseline));
    }

    [Fact]
    public void Revert_ทิศตรงข้าม_entityของผู้เรียกยังถูกติดตาม_และตัวตรวจซ้ำปลดของที่ถูกดึงกลับ()
    {
        using var db = NewContext();
        var dep = new Document { Id = Dep, CompanyId = Co, DocumentNumber = "RV-1", IsDeposit = true };
        db.Attach(dep);
        var baseline = new HashSet<object>(db.ChangeTracker.Entries().Select(e => e.Entity), ReferenceEqualityComparer.Instance);
        TrackedChangeRevert.DetachSince(db, baseline);
        Assert.NotEqual(EntityState.Detached, db.Entry(dep).State);   // M1(ก): ห้ามปลด entity ของผู้เรียก (เดิม Clear ทั้ง context)
        // ของที่ถูกดึงกลับหลัง reload (จำลอง) ⇒ ตัวตรวจซ้ำปลดและนับ
        db.JournalEntries.Add(new JournalEntry { CompanyId = Co, EntryNumber = "JV-10" });
        Assert.Equal(1, TrackedChangeRevert.DetachStrays(db, baseline));
        Assert.Single(db.ChangeTracker.Entries());
    }
}
