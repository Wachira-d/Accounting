using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 271 — ตัวตัดสินเดียว "ผูกใบสำคัญจ่ายกับ 50 ทวิ ที่ออกไว้แล้ว" (<see cref="WhtCertLinkPlan"/>) · ทุกกติกาสองทิศ (ผ่าน/ล้ม) ·
/// คำตัดสินเจ้าของ 271-0-1…4 + ผู้ประสานงาน 271-1-1…6 · ต่อสาย <see cref="WhtCertIssuePlan.Decide"/> (LinkExisting) + ทิศตรงข้ามของเทสต์เดิม
/// </summary>
public class WhtCertLinkPlanTests
{
    private static readonly Guid Pv = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherDoc = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Payee = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid PayeeDup = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private const string TaxId = "0105556000017";      // checksum ถูก
    private const string TaxIdOther = "0107537000017"; // checksum ถูก คนละเลข
    private const string TaxIdBadChecksum = "0105556000018";

    private static WhtCertLinkPvFacts P(decimal wht = 300m, decimal income = 10_000m, TaxType form = TaxType.WithholdingTax53,
        WithholdingTaxCertType ct = WithholdingTaxCertType.Withhold, string[]? codes = null, int y = 2026, int m = 11,
        DocumentType type = DocumentType.PaymentVoucher, DocumentStatus status = DocumentStatus.Paid,
        bool multi = false, bool eWht = false, string? taxId = TaxId, string? branch = "00000", string? problem = null)
        => new(Pv, type, status, "PV-1", Payee, taxId, branch, multi, eWht, wht, income, form, ct, codes ?? new[] { "8" }, y, m, problem);

    private static WhtCertLinkCandidateFacts C(decimal tax = 300m, decimal income = 10_000m, TaxType form = TaxType.WithholdingTax53,
        WithholdingTaxCertType ct = WithholdingTaxCertType.Withhold, string[]? codes = null, int y = 2026, int m = 11,
        WithholdingTaxCertStatus st = WithholdingTaxCertStatus.Issued, Guid? doc = null, Guid? payroll = null,
        Guid? payee = null, string? taxId = TaxId, string? branch = "00000", bool deleted = false)
        => new(Guid.NewGuid(), "WHT-202611-0007", st, deleted, doc, payroll, payee ?? Payee, taxId, branch, tax, income, form, ct,
            codes ?? new[] { "8" }, y, m);

    private static WhtCertLinkDecision D(WhtCertLinkPvFacts? p = null, WhtCertLinkCandidateFacts? c = null, WhtCertLinkPeriodFacts? per = null)
        => WhtCertLinkPlan.Decide(p ?? P(), c ?? C(), per ?? WhtCertLinkPeriodFacts.NoneFiled);

    private static bool Blocks(WhtCertLinkDecision d, string code) => d.Blocks.Any(b => b.Code == code);
    private static bool Warns(WhtCertLinkDecision d, string code) => d.Warnings.Any(w => w.Code == code);

    // ── ทิศผ่าน ──

    [Fact]
    public void ตรงทุกข้อ_Ok_ไม่มีคำเตือน()
    {
        var d = D();
        Assert.Equal(WhtCertLinkVerdict.Ok, d.Verdict);
        Assert.Empty(d.Blocks);
        Assert.Empty(d.Warnings);
    }

    // ── R1 ฝั่งใบสำคัญจ่าย ──

    [Theory]
    [InlineData("receive")]
    [InlineData("nowht")]
    [InlineData("voided")]
    [InlineData("ewht")]
    [InlineData("multi")]
    [InlineData("compose")]
    public void R1_ฝั่งการจ่ายไม่เข้าเงื่อนไข_บล็อก(string kind)
    {
        var p = kind switch
        {
            "receive" => P(type: DocumentType.Invoice),
            "nowht" => P(wht: 0m),
            "voided" => P(status: DocumentStatus.Voided),
            "ewht" => P(eWht: true),
            "multi" => P(multi: true),
            _ => P(problem: "ยอดออกแทนไม่ตรง"),
        };
        var d = D(p);
        Assert.True(d.IsBlocked);
        Assert.True(Blocks(d, WhtCertLinkPlan.CodePv));
    }

    // ── R2 สถานะใบ ──

    [Fact]
    public void R2_ใบยกเลิกหรือลบแล้ว_บล็อก_ใบออกแล้ว_ผ่าน()
    {
        Assert.True(Blocks(D(c: C(st: WithholdingTaxCertStatus.Voided)), WhtCertLinkPlan.CodeCertStatus));
        Assert.True(Blocks(D(c: C(deleted: true)), WhtCertLinkPlan.CodeCertStatus));
        Assert.False(D(c: C(st: WithholdingTaxCertStatus.Printed)).IsBlocked);
    }

    // ── R3 ผูกเอกสารอื่น / รอบเงินเดือน (271-0-3) ──

    [Fact]
    public void R3_ใบผูกเอกสารอื่นแล้ว_บล็อก_ใบผูกเอกสารนี้_ผ่าน()
    {
        Assert.True(Blocks(D(c: C(doc: OtherDoc)), WhtCertLinkPlan.CodeCertLinkedElsewhere));
        Assert.True(Blocks(D(c: C(payroll: Guid.NewGuid())), WhtCertLinkPlan.CodeCertLinkedElsewhere));
        Assert.False(D(c: C(doc: Pv)).IsBlocked);
    }

    // ── R4 ผู้ถูกหัก (271-1-5 = D8) ──

    [Fact]
    public void R4_ผู้ติดต่อซ้ำ_เลขภาษีและสาขาเดียวกัน_ผ่านพร้อมคำเตือน()
    {
        var d = D(c: C(payee: PayeeDup, taxId: "0-1055-56000-01-7"));
        Assert.Equal(WhtCertLinkVerdict.OkWithWarnings, d.Verdict);
        Assert.True(Warns(d, WhtCertLinkPlan.CodeDuplicateContact));
    }

    [Fact]
    public void R4_เลขเดียวคนละสาขา_บล็อก()
        => Assert.True(Blocks(D(c: C(payee: PayeeDup, branch: "00001")), WhtCertLinkPlan.CodePayee));

    [Fact]
    public void R4_คนละเลข_บล็อก_และเลขผิดหลักท้าย_บล็อก()
    {
        Assert.True(Blocks(D(c: C(payee: PayeeDup, taxId: TaxIdOther)), WhtCertLinkPlan.CodePayee));
        Assert.True(Blocks(D(p: P(taxId: TaxIdBadChecksum), c: C(payee: PayeeDup, taxId: TaxIdBadChecksum)), WhtCertLinkPlan.CodePayee));
    }

    // ── R5 ยอดตรงทุกสตางค์ (271-1-3 = D4) ──

    [Fact]
    public void R5_ภาษีต่างหนึ่งสตางค์_บล็อก_ตรงกัน_ผ่าน()
    {
        Assert.True(Blocks(D(c: C(tax: 299.99m)), WhtCertLinkPlan.CodeAmount));
        Assert.False(D(c: C(tax: 300.00m)).IsBlocked);
    }

    [Fact]
    public void R5_เงินได้ต่างหนึ่งสตางค์_บล็อก()
        => Assert.True(Blocks(D(c: C(income: 10_000.01m)), WhtCertLinkPlan.CodeAmount));

    // ── R11 ใบรวมหลายการจ่าย (271-0-3) ──

    [Fact]
    public void R11_ใบยอดมากกว่าการจ่าย_บล็อกด้วยข้อความใบรวม_ไม่ใช่ข้อความยอดไม่ตรง()
    {
        var d = D(c: C(tax: 1_200m, income: 40_000m));
        Assert.True(Blocks(d, WhtCertLinkPlan.CodeCombined));
        Assert.Contains("หนึ่ง 50 ทวิ ผูกได้หนึ่งการจ่าย", d.Summary);
    }

    // ── R6 แบบ + เงื่อนไข ──

    [Fact]
    public void R6_แบบต่าง_บล็อก_เงื่อนไขต่าง_บล็อก()
    {
        Assert.True(Blocks(D(c: C(form: TaxType.WithholdingTax3)), WhtCertLinkPlan.CodeForm));
        Assert.True(Blocks(D(c: C(ct: WithholdingTaxCertType.PayAlways)), WhtCertLinkPlan.CodeForm));
    }

    // ── R7 เดือน · R8 ประเภทเงินได้ · R10 ร่าง (เตือน + รับทราบ) ──

    [Fact]
    public void R7_คนละเดือน_เตือน_เดือนเดียวกัน_ไม่เตือน()
    {
        Assert.True(Warns(D(c: C(m: 10)), WhtCertLinkPlan.CodeMonth));
        Assert.False(Warns(D(), WhtCertLinkPlan.CodeMonth));
    }

    [Fact]
    public void R8_ประเภทเงินได้ต่าง_เตือน_ชุดเดียวกันต่างลำดับ_ไม่เตือน()
    {
        Assert.True(Warns(D(c: C(codes: new[] { "2" })), WhtCertLinkPlan.CodeIncomeType));
        Assert.False(Warns(D(p: P(codes: new[] { "8", "2" }), c: C(codes: new[] { "2", "8", "8" })), WhtCertLinkPlan.CodeIncomeType));
    }

    [Fact]
    public void R10_ใบร่าง_เป็นผู้สมัครได้พร้อมคำเตือน()
    {
        var d = D(c: C(st: WithholdingTaxCertStatus.Draft));
        Assert.Equal(WhtCertLinkVerdict.OkWithWarnings, d.Verdict);
        Assert.True(Warns(d, WhtCertLinkPlan.CodeDraft));
    }

    [Fact]
    public void คำเตือนที่ยังไม่รับทราบ_นับครบ_รับทราบครบ_ว่าง()
    {
        var d = D(c: C(st: WithholdingTaxCertStatus.Draft, m: 10));
        Assert.Equal(2, d.Unacknowledged(null).Count);
        Assert.Single(d.Unacknowledged(new[] { WhtCertLinkPlan.CodeDraft }));
        Assert.Empty(d.Unacknowledged(new[] { WhtCertLinkPlan.CodeDraft, WhtCertLinkPlan.CodeMonth }));
    }

    // ── R9 เดือนที่ยื่นแล้ว (271-0-2 · 271-1-2 = D10) ──

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void R9_เปลี่ยนการผูก_เดือนใดยื่นแล้ว_บล็อกเด็ดขาด(bool aFiled, bool bFiled, bool payFiled)
    {
        var per = new WhtCertLinkPeriodFacts(true, aFiled ? "ภ.ง.ด.53 เดือน 10/2569 ยื่นแล้ว" : null,
            bFiled ? "ภ.ง.ด.53 เดือน 11/2569 ยื่นแล้ว" : null, payFiled ? "ภ.ง.ด.53 เดือน 11/2569 นำส่งแล้ว" : null);
        var d = D(per: per);
        Assert.True(d.IsBlocked);
        Assert.True(d.BlockedByFiledPeriod);
        // ไม่มีการรับทราบใดทำให้ผ่าน (ไม่มีฟิลด์ ack สำหรับ R9)
        Assert.True(d.Blocks.Any(b => b.Code == WhtCertLinkPlan.CodeFiled));
    }

    [Fact]
    public void R9_ผูกครั้งแรกที่ไม่ยกเลิกใบใด_เดือนยื่นแล้ว_ผ่านพร้อมคำเตือนผูกอย่างเดียว()
    {
        var per = new WhtCertLinkPeriodFacts(false, null, "ภ.ง.ด.53 เดือน 11/2569 ยื่นแล้ว", null);
        var d = D(per: per);
        Assert.False(d.IsBlocked);
        var w = Assert.Single(d.Warnings);
        Assert.Equal(WhtCertLinkPlan.CodeFiledLinkOnly, w.Code);
        Assert.Contains("ตัวเลขในแบบ ภ.ง.ด. ที่ยื่นแล้วไม่เปลี่ยน", w.Message);
        Assert.Contains("ไม่ยกเลิกใบใด", w.Message);
    }

    [Fact]
    public void R9_ทิศตรงข้าม_ทุกเดือนยังไม่ยื่น_เปลี่ยนการผูกได้()
        => Assert.Equal(WhtCertLinkVerdict.Ok, D(per: new WhtCertLinkPeriodFacts(true, null, null, null)).Verdict);

    // ── ใบที่หลุดการผูก (271-0-1) · ด่านลบ (271-1-1 = D1b) ──

    [Fact]
    public void ใบที่หลุดการผูก_มีเลข_ยกเลิก_เดือนยื่นแล้ว_บล็อก_ไม่มีผลลบในชุด()
    {
        Assert.Equal(WhtCertUnlinkedOutcome.Void, WhtCertLinkPlan.UnlinkedOutcome("WHT-202611-0012", false));
        Assert.Equal(WhtCertUnlinkedOutcome.Blocked, WhtCertLinkPlan.UnlinkedOutcome("WHT-202611-0012", true));
        Assert.Equal(WhtCertUnlinkedOutcome.LegacyNoNumber, WhtCertLinkPlan.UnlinkedOutcome("", false));
        Assert.DoesNotContain(Enum.GetNames<WhtCertUnlinkedOutcome>(), n => n.Contains("Delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ลบถาวร_ใบมีเลขไม่ได้ทุกสถานะ_ใบไม่มีเลขได้()
    {
        Assert.False(WhtCertLinkPlan.DeleteAllowed("WHT-202611-0001"));
        Assert.True(WhtCertLinkPlan.DeleteAllowed(null));
        Assert.True(WhtCertLinkPlan.DeleteAllowed("  "));
        Assert.Contains("ยกเลิก", WhtCertLinkPlan.DeleteRefusal("WHT-1", WithholdingTaxCertStatus.Issued));
        // ทิศตรงข้าม: ยกเลิกยังทำได้ทุกสถานะที่ยังไม่ยกเลิก
        Assert.True(WhtCertLinkPlan.VoidAllowed(WithholdingTaxCertStatus.Draft));
        Assert.True(WhtCertLinkPlan.VoidAllowed(WithholdingTaxCertStatus.Printed));
        Assert.False(WhtCertLinkPlan.VoidAllowed(WithholdingTaxCertStatus.Voided));
    }

    // ── ป้าย "ผูกกับ" + นิยาม "ยังไม่ผูก" (ตัวเดียวกับตัวกรอง) ──

    [Fact]
    public void สถานะการผูก_payroll_ไม่ใช่ยังไม่ผูก_ยกเลิกไม่นับ_ยืนยันไม่มีใบสำคัญจ่าย_เทา()
    {
        Assert.Equal(WhtCertLinkState.Payroll, WhtCertLinkPlan.StateOf(WithholdingTaxCertStatus.Issued, null, Guid.NewGuid(), null));
        Assert.Equal(WhtCertLinkState.Voided, WhtCertLinkPlan.StateOf(WithholdingTaxCertStatus.Voided, null, null, null));
        Assert.Equal(WhtCertLinkState.Unlinked, WhtCertLinkPlan.StateOf(WithholdingTaxCertStatus.Issued, null, null, null));
        Assert.Equal(WhtCertLinkState.NoSourceConfirmed, WhtCertLinkPlan.StateOf(WithholdingTaxCertStatus.Issued, null, null, "จ่ายนอกระบบ"));
        Assert.Equal(WhtCertLinkState.Linked, WhtCertLinkPlan.StateOf(WithholdingTaxCertStatus.Draft, Pv, null, null));

        var isUnlinked = WhtCertLinkPlan.IsUnlinked.Compile();
        Assert.True(isUnlinked(new WithholdingTaxCert { Status = WithholdingTaxCertStatus.Issued }));
        Assert.False(isUnlinked(new WithholdingTaxCert { Status = WithholdingTaxCertStatus.Issued, SourcePayrollRunId = Guid.NewGuid() }));
        Assert.False(isUnlinked(new WithholdingTaxCert { Status = WithholdingTaxCertStatus.Voided }));
        Assert.False(isUnlinked(new WithholdingTaxCert { Status = WithholdingTaxCertStatus.Issued, NoSourceDocumentReason = "x" }));
    }

    [Fact]
    public void ป้ายผูกกับ_ตามชนิด_งวด_แทนด้วย()
    {
        Assert.Equal("ใบสำคัญจ่าย PV-202611-0044",
            WhtCertLinkPlan.LinkLabel(WhtCertLinkState.Linked, DocumentType.PaymentVoucher, "PV-202611-0044", null, 2026, 11, null, null));
        Assert.Contains("งวด 05/11/2569",
            WhtCertLinkPlan.LinkLabel(WhtCertLinkState.Linked, DocumentType.PaymentVoucher, "PV-1", new DateTime(2026, 11, 5), 2026, 11, null, null));
        Assert.Equal("ยกเลิก · แทนด้วย WHT-202611-0007",
            WhtCertLinkPlan.LinkLabel(WhtCertLinkState.Voided, null, null, null, 2026, 11, "WHT-202611-0007", null));
        Assert.Equal("รอบเงินเดือน 11/2569", WhtCertLinkPlan.LinkLabel(WhtCertLinkState.Payroll, null, null, null, 2026, 11, null, null));
        Assert.Equal("ยังไม่ผูกเอกสาร", WhtCertLinkPlan.LinkLabel(WhtCertLinkState.Unlinked, null, null, null, 2026, 11, null, null));
    }

    [Fact]
    public void รหัสรับทราบ_อ่านเขียนกลับได้_ตัดค่าแปลก()
    {
        Assert.Null(WhtCertLinkPlan.JoinAckCodes(null));
        Assert.Equal("R10,R7", WhtCertLinkPlan.JoinAckCodes(new[] { "R7", " R10 ", "R7", "", "a,b" }));
        Assert.Equal(new[] { "R10", "R7" }, WhtCertLinkPlan.ParseAckCodes("R10,R7"));
        Assert.Empty(WhtCertLinkPlan.ParseAckCodes(null));
    }

    [Fact]
    public void เรียงผู้สมัคร_ผ่านก่อน_ผู้ถูกหักตรงก่อน()
    {
        var pv = P();
        var ok = C();
        var warn = C(m: 10);
        var bad = C(tax: 299m);
        int S(WhtCertLinkCandidateFacts c) => WhtCertLinkPlan.CandidateScore(pv, c, WhtCertLinkPlan.Decide(pv, c, WhtCertLinkPeriodFacts.NoneFiled));
        Assert.True(S(ok) > S(warn));
        Assert.True(S(warn) > S(bad));
    }

    // ── ต่อสาย WhtCertIssuePlan.Decide (LinkExisting) ──

    private static WhtCertIssueFacts F(WhtCertIssueTrigger trigger = WhtCertIssueTrigger.Approve, bool link = false, bool paid = true,
        int any = 0, bool eWht = false, bool auto = true)
        => new(trigger, DocumentType.PaymentVoucher, DocumentStatus.Paid, 300m, auto, Payee, null, paid, false, 0, 0, any,
            ViaEWithholding: eWht, LinkCertRequested: link);

    [Fact]
    public void Decide_ไม่ตั้งใจผูก_ผลเดิมทุกทาง()
    {
        Assert.Equal(WhtCertIssueAction.IssueNow, WhtCertIssuePlan.Decide(F()).Action);
        Assert.Equal(WhtCertIssueAction.ManualPending, WhtCertIssuePlan.Decide(F(auto: false)).Action);
        Assert.Equal(WhtCertIssueAction.WaitForPayment, WhtCertIssuePlan.Decide(F(paid: false)).Action);
    }

    [Fact]
    public void Decide_ตั้งใจผูก_LinkExisting_ชนะติ๊กปิดอัตโนมัติ()
    {
        Assert.Equal(WhtCertIssueAction.LinkExisting, WhtCertIssuePlan.Decide(F(link: true)).Action);
        Assert.Equal(WhtCertIssueAction.LinkExisting, WhtCertIssuePlan.Decide(F(link: true, auto: false)).Action);
        Assert.Equal(WhtCertIssueAction.LinkExisting, WhtCertIssuePlan.Decide(F(WhtCertIssueTrigger.Payment, link: true)).Action);
        Assert.Equal(WhtCertIssueAction.LinkExisting, WhtCertIssuePlan.Decide(F(WhtCertIssueTrigger.Bulk, link: true)).Action);
    }

    [Fact]
    public void Decide_ตั้งใจผูก_มีใบแล้ว_AlreadyIssued_eWht_NotApplicable_กดเอง_IssueNow_ยังไม่จ่าย_รอ()
    {
        Assert.Equal(WhtCertIssueAction.AlreadyIssued, WhtCertIssuePlan.Decide(F(link: true, any: 1)).Action);
        Assert.Equal(WhtCertIssueAction.NotApplicable, WhtCertIssuePlan.Decide(F(link: true, eWht: true)).Action);
        Assert.Equal(WhtCertIssueAction.IssueNow, WhtCertIssuePlan.Decide(F(WhtCertIssueTrigger.Manual, link: true)).Action);
        Assert.Equal(WhtCertIssueAction.WaitForPayment, WhtCertIssuePlan.Decide(F(link: true, paid: false)).Action);
    }

    [Fact]
    public void งวดยื่นแล้ว_ข้อความระดับงวด_ทั้งรายงานและนำส่ง_ยังไม่ยื่น_null()
    {
        Assert.Contains("ยื่นแล้ว", WhtCertVoidGuard.PeriodReason(TaxType.WithholdingTax53, 2026, 11, true, null));
        Assert.Contains("นำส่งแล้ว", WhtCertVoidGuard.PeriodReason(TaxType.WithholdingTax53, 2026, 11, false, DateTime.UtcNow));
        Assert.Null(WhtCertVoidGuard.PeriodReason(TaxType.WithholdingTax53, 2026, 11, false, null));
    }
    // ── ฝ่ายค้าน M5: "อาจนับซ้ำ" เจ้าของเดียว WhtCertDuplicateSuspect.IsPeer (ตัวเดียวกับตัวกรอง/การ์ด/ป้าย) ──

    private static WithholdingTaxCert Cert(Guid company, Guid payee, Guid? doc = null, Guid? pay = null, int m = 11, decimal tax = 300m,
        WithholdingTaxCertStatus st = WithholdingTaxCertStatus.Issued, string? noSource = null)
        => new() { Id = Guid.NewGuid(), CompanyId = company, PayeeContactId = payee, DocumentId = doc, SourcePaymentId = pay, TaxYear = 2026,
            TaxMonth = m, TotalTaxAmount = tax, TaxFormType = TaxType.WithholdingTax53, Status = st, NoSourceDocumentReason = noSource };

    [Fact]
    public void อาจนับซ้ำ_สองทิศ()
    {
        var co = Guid.NewGuid();
        var dupContact = new Contact { Id = PayeeDup, CompanyId = co, TaxId = TaxId, BranchCode = null };
        var mainContact = new Contact { Id = Payee, CompanyId = co, TaxId = TaxId, BranchCode = "00000" };
        var otherBranch = new Contact { Id = Guid.NewGuid(), CompanyId = co, TaxId = TaxId, BranchCode = "00001" };
        var peer = WhtCertDuplicateSuspect.IsPeer(new[] { dupContact, mainContact, otherBranch }.AsQueryable()).Compile();
        var linked = Cert(co, Payee, doc: Pv);
        Assert.True(peer(Cert(co, Payee), linked));                          // (ก) ใบมือ ↔ ใบผูก
        Assert.True(peer(Cert(co, PayeeDup), linked));                       // ผู้ติดต่อซ้ำ เลขภาษี+สาขาเดียวกัน
        Assert.False(peer(Cert(co, otherBranch.Id), linked));                // คนละสาขา
        Assert.True(peer(Cert(co, Payee, m: 10), linked));                   // เดือนติดกัน
        Assert.False(peer(Cert(co, Payee, m: 8), linked));                   // ห่างเกิน 1 เดือน
        Assert.False(peer(Cert(co, Payee, noSource: "จ่ายนอกระบบ"), linked)); // D5 ปิดป้าย
        Assert.True(peer(Cert(co, Payee, doc: Pv, tax: 10m), linked));       // (ข) สองใบบนการจ่ายเดียวกัน (ยอดใดก็ได้)
        Assert.False(peer(Cert(co, Payee, doc: OtherDoc), linked));          // การจ่ายคนละใบสำคัญจ่าย ⇒ ไม่ขึ้น
        Assert.False(peer(Cert(co, Payee, doc: Pv, pay: Guid.NewGuid()), linked));   // คนละงวดจ่าย
        Assert.False(peer(Cert(co, Payee), Cert(co, Payee, doc: Pv, st: WithholdingTaxCertStatus.Voided)));   // ใบยกเลิกไม่นับ
    }
}

