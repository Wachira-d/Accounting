using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 ทีม S5 — แก้ผลฝ่ายค้าน review198-S4 (S4-1 · S4-7 · S4-8) · ตัวตัดสินบริสุทธิ์ทุกตัว
/// มีสองครึ่งทุกข้อ: เคสที่พังกลับมาถูก และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ (F2 ข้อ 8) · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class SettlementReview198S5Tests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DeadBatch = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid DocB = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid PayA = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<(TaxType, int, int)> SepVatDeclared = new() { (TaxType.VAT, 2026, 9) };
    private static readonly HashSet<(TaxType, int, int)> NothingFiled = new();

    /// <summary>ใบค่าธรรมเนียมกำพร้า (ภาษีซื้อ 7 เคลมเดือนเอกสาร ก.ย.) ของรอบโอน PO-OLD ที่ถูกยกเลิกไปแล้ว</summary>
    private static SettlementUnpostDocument FeeDoc(bool etaxAccepted = false, bool inLockedReport = false, string? voidBlock = null,
        DateTime? taxPoint = null)
        => new(DocB, "PV-0009", DocumentType.PaymentVoucher, SettlementPostingKeys.FeeComponent(SettlementFeeVatTreatment.InputVatPending),
            Day, 7m, false, etaxAccepted, inLockedReport, false, null, voidBlock, taxPoint);

    private static SettlementOrphanArtifact Orphan(Guid id, bool isPayment, string number) => new(id, DeadBatch, "PO-OLD", isPayment, number);

    private static SettlementOrphanTriageResult Triage(IReadOnlyList<SettlementUnpostDocument> docs, IReadOnlyList<SettlementUnpostPayment> pays,
        IReadOnlyCollection<(TaxType, int, int)> filed, IReadOnlyList<SettlementUnpostCertificate>? certs = null)
    {
        var refusals = SettlementUnpostGate.Evaluate(docs, certs ?? Array.Empty<SettlementUnpostCertificate>(), filed, pays);
        var artifacts = docs.Select(d => Orphan(d.Id, false, d.Number)).Concat(pays.Select(p => Orphan(p.Id, true, p.Number ?? ""))).ToList();
        return SettlementOrphanTriage.Split(artifacts, refusals, docs);
    }

    /// <summary>รอบโอนใหม่ของช่องทางเดียวกัน (ฉากเดียวกับ SettlementReview198S4Tests: ขายจับคู่ใบ A 1,000 · ใบสรุป 1,070 · ค่าคอมมิชชั่น −107 =
    /// 1,963 · ลงบัญชีได้เมื่อไม่มีของกำพร้า) — ใช้ถามด่านลงบัญชีว่าบล็อกไหม</summary>
    private static (SettlementPostingPlan Plan, SettlementPostingFacts Facts) NewBatch()
    {
        var batch = new SettlementBatch { CompanyId = Co, PayoutRef = "PO-NEW", PayoutDate = Day, NetPayout = 1963m, BankAccountId = Guid.NewGuid() };
        var channel = new SettlementChannel
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Shopee", ClearingAccountId = Clearing,
            CounterpartyContactId = Guid.NewGuid(),
        };
        var lines = new[]
        {
            new SettlementLine { CompanyId = Co, Seq = 1, LineType = SettlementLineType.Sale, Amount = 1000m, MatchedDocumentId = DocA, TxnDate = Day,
                ExternalOrderId = "SP-0", MatchStatus = SettlementMatchStatus.Matched },
            new SettlementLine { CompanyId = Co, Seq = 2, LineType = SettlementLineType.Sale, Amount = 1070m, TxnDate = Day, ExternalOrderId = "SP-1",
                MatchStatus = SettlementMatchStatus.AutoSummary },
            new SettlementLine { CompanyId = Co, Seq = 3, LineType = SettlementLineType.Commission, Amount = -107m, TxnDate = Day,
                MatchStatus = SettlementMatchStatus.NotRequired },
        };
        var plan = SettlementBatchMath.Plan(batch, lines, channel, true);
        var facts = new SettlementPostingFacts(
            SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
            new HashSet<(int, int)>(), new HashSet<(int, int)>(), Array.Empty<string>(),
            true, true, Clearing,
            plan.Receipts.Select(r => new SettlementReceiptTarget(r.DocumentId, true, "INV-A", DocumentType.Invoice, DocumentStatus.Approved, 1000m, false))
                .ToList(),
            Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, 0);
        return (plan, facts);
    }

    private static SettlementPostingFacts WithOrphans(SettlementPostingFacts f, SettlementOrphanTriageResult t) => f with
    {
        OrphanArtifacts = t.Voidable, UnvoidableOrphans = t.Unvoidable, OrphanNeedsAction = t.NeedsUserAction,
        AcknowledgedOrphans = t.Acknowledged,
    };

    // ═════════════ S4-1: "ด่านยกเลิกการลงบัญชีปฏิเสธ" ≠ "ระบบยกเลิกไม่ได้" ═════════════

    [Fact]
    public void S41_ฉากผู้ตรวจ_ใบค่าธรรมเนียมกำพร้าในเดือนที่ประกาศว่ายื่นภพ30แต่ไม่ล็อก_ยังบล็อก_ทางไปต่อคือยกเลิกแล้วยื่นเพิ่มเติม()
    {
        var t = Triage(new[] { FeeDoc() }, Array.Empty<SettlementUnpostPayment>(), SepVatDeclared);
        Assert.Empty(t.Unvoidable);                                   // เดิม (S4) ตกกองนี้ ⇒ คำเตือน ℹ️ ⇒ ค่าธรรมเนียม/ภาษีซื้อ/50 ทวิ ซ้ำ
        Assert.Empty(t.Voidable);
        var block = Assert.Single(t.NeedsUserAction);
        Assert.Contains("PV-0009", block.Why);
        Assert.Contains("ประกาศว่ายื่น ภ.พ.30", block.Why);
        Assert.Contains("ยื่นแบบเพิ่มเติม", block.NextStep);
        Assert.DoesNotContain("ยกเลิกการลงบัญชีทั้งรอบไม่ได้", block.NextStep);  // ทางไปต่อของด่าน Unpost ไม่ใช่ของกำพร้า

        var (plan, facts) = NewBatch();
        var gated = SettlementPostingGate.Evaluate(plan, WithOrphans(facts, t));
        Assert.False(gated.CanPost);
        var issue = Assert.Single(gated.Issues, i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts);
        Assert.True(issue.Blocking);
        Assert.Equal(block.NextStep, issue.NextStep);
    }

    /// <summary>รอบ 200 (DECISIONS ข้อ 10) เปลี่ยนความหมาย: เดิม "ยกเลิกไม่ได้จริง = เตือนไม่บล็อก" (กดลงบัญชีทับได้โดยไม่มีใครตรวจรายการซ้ำ) ⇒
    /// ยังไม่มีคนรับรู้ = บล็อก ทางไปต่อ = "รับรู้ของกำพร้า" · รับรู้แล้ว = ไม่บล็อก ⇒ ช่องทางไม่ถูกล็อกตลอดไป (เจตนาเดิมของเทสต์นี้ยังอยู่)</summary>
    [Fact]
    public void S41_ทิศตรงข้าม_ใบกำพร้าที่eTaxตอบรับแล้ว_บล็อกจนรับรู้_รับรู้แล้วช่องทางไม่ถูกล็อกตลอดไป()
    {
        var t = Triage(new[] { FeeDoc(etaxAccepted: true) }, Array.Empty<SettlementUnpostPayment>(), NothingFiled);
        Assert.Empty(t.NeedsUserAction);
        Assert.Empty(t.Voidable);
        var warn = Assert.Single(t.Unvoidable);
        Assert.Contains("ยกเลิกในระบบไม่ได้แล้ว", warn);
        Assert.Contains("Accepted", warn);

        var (plan, facts) = NewBatch();
        Assert.True(SettlementPostingGate.Evaluate(plan, facts).CanPost);           // ฐาน: ไม่มีของกำพร้า ลงได้
        var gated = SettlementPostingGate.Evaluate(plan, WithOrphans(facts, t));
        Assert.False(gated.CanPost);
        var issue = Assert.Single(gated.Issues, i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts);
        Assert.True(issue.Blocking);
        Assert.Contains("รับรู้ของกำพร้า", issue.NextStep);

        // รอบ 201 ทีม ST (A-ST5): การรับรู้ประทับลายนิ้วมือเหตุที่รายการแสดงตอนกด (ตัวเดียวกับ service) — ไม่มีลายนิ้วมือ = ไม่ครอบ
        var ack = new SettlementOrphanAck(Guid.NewGuid(), "ผู้ทำบัญชี ก", Day, "ตรวจแล้วรอบใหม่ไม่ซ้ำ", Assert.Single(t.Items!).ReasonHash);
        var acked = SettlementOrphanTriage.Split(new[] { Orphan(DocB, false, "PV-0009") with { Ack = ack } },
            SettlementUnpostGate.Evaluate(new[] { FeeDoc(etaxAccepted: true) }, Array.Empty<SettlementUnpostCertificate>(), NothingFiled),
            new[] { FeeDoc(etaxAccepted: true) });
        Assert.Empty(acked.Unvoidable);
        var open = SettlementPostingGate.Evaluate(plan, WithOrphans(facts, acked));
        Assert.True(open.CanPost);
        Assert.False(Assert.Single(open.Issues, i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts).Blocking);
    }

    [Fact]
    public void S41_เหตุที่ยกเลิกไม่ได้จริงชนะ_รายงานล็อกหรือ50ทวิยื่นแล้วพร้อมเดือนประกาศ_เตือนเฉพาะเหตุที่ยกเลิกไม่ได้()
    {
        // รายงานล็อก + เดือนประกาศแล้ว ⇒ ยกเลิกไม่ได้จริง (ข้อความไม่ปนเหตุ "ประกาศว่ายื่น" ที่ไม่ใช่เหตุของการยกเลิกไม่ได้)
        var locked = Triage(new[] { FeeDoc(inLockedReport: true) }, Array.Empty<SettlementUnpostPayment>(), SepVatDeclared);
        var warn = Assert.Single(locked.Unvoidable);
        Assert.Contains("ล็อกการยื่น", warn);
        Assert.DoesNotContain("ประกาศว่ายื่น", warn);
        Assert.Empty(locked.NeedsUserAction);
        // 50 ทวิ ใน ภ.ง.ด.53 ที่ยื่นแล้ว ⇒ เอกสารของมันยกเลิกไม่ได้ (VoidDocumentAsync ปฏิเสธด้วย WhtCertVoidGuard ตัวเดียวกัน)
        var cert = new SettlementUnpostCertificate(Guid.NewGuid(), DocB, "WHT-1", TaxType.WithholdingTax53, 2026, 9, WithholdingTaxCertStatus.Issued);
        var wht = Triage(new[] { FeeDoc() }, Array.Empty<SettlementUnpostPayment>(),
            new HashSet<(TaxType, int, int)> { (TaxType.WithholdingTax53, 2026, 9) }, new[] { cert });
        Assert.Single(wht.Unvoidable);
        Assert.Empty(wht.NeedsUserAction);
    }

    [Fact]
    public void S41_ใบกำพร้าที่มีใบลดหนี้อ้าง_บล็อก_ทางไปต่อคือยกเลิกใบที่อ้างก่อน_ไม่มีเหตุเลย_บล็อกแบบยกเลิกได้ทันที()
    {
        var reason = DocumentVoidPreconditions.Reason(new DocumentVoidChildFact(DocB, DocumentType.CreditNote, "CN-0009", true));
        var t = Triage(new[] { FeeDoc(voidBlock: reason) }, Array.Empty<SettlementUnpostPayment>(), NothingFiled);
        var block = Assert.Single(t.NeedsUserAction);
        Assert.Contains("CN-0009", block.Why);
        Assert.Contains("ยกเลิกเอกสารที่อ้างใบนี้ก่อน", block.NextStep);
        Assert.DoesNotContain("ยื่นแบบเพิ่มเติม", block.NextStep);           // ไม่มีเหตุภาษี ⇒ ไม่พูดถึงการยื่นเพิ่มเติม
        Assert.Empty(t.Unvoidable);
        // ทั้งสองเหตุ ⇒ ทางไปต่อครบทั้งสองขั้น
        var both = Assert.Single(Triage(new[] { FeeDoc(voidBlock: reason) }, Array.Empty<SettlementUnpostPayment>(), SepVatDeclared).NeedsUserAction);
        Assert.Contains("ยกเลิกเอกสารที่อ้างใบนี้ก่อน", both.NextStep);
        Assert.Contains("ยื่นแบบเพิ่มเติม", both.NextStep);
        // ไม่มีเหตุ ⇒ กองยกเลิกได้ทันที (รวมเลขที่ต่อรอบโอน)
        var free = Triage(new[] { FeeDoc(), FeeDoc() with { Id = DocA, Number = "PV-0010" } }, Array.Empty<SettlementUnpostPayment>(), NothingFiled);
        var line = Assert.Single(free.Voidable);
        Assert.Contains("PV-0009, PV-0010", line);
        Assert.Empty(free.NeedsUserAction);
        Assert.Empty(free.Unvoidable);
    }

    [Fact]
    public void S41_การรับชำระกำพร้า_S78_1ในเดือนที่ประกาศแล้ว_บล็อกพร้อมทางยื่นเพิ่มเติม_ใบเสร็จคู่กันeTaxตอบรับแล้ว_เตือน()
    {
        var pay = new SettlementUnpostPayment(PayA, "RV-0001", DocA, "INV-0001", Day, 1070m, 1070m);
        var soft = Assert.Single(Triage(Array.Empty<SettlementUnpostDocument>(), new[] { pay }, SepVatDeclared).NeedsUserAction);
        Assert.Contains("RV-0001", soft.Why);
        Assert.Contains("ยกเลิกการรับชำระนั้น", soft.NextStep);
        Assert.Contains("ภ.พ.30 เพิ่มเติม", soft.NextStep);
        var hard = Triage(Array.Empty<SettlementUnpostDocument>(), new[] { pay with { ReceiptEtaxAccepted = true, ReceiptNumber = "RE-0001" } },
            SepVatDeclared);
        Assert.Empty(hard.NeedsUserAction);
        Assert.Contains("RE-0001", Assert.Single(hard.Unvoidable));
    }

    [Fact]
    public void S41_ทุกเหตุของด่านระบุชนิด_ยกเลิกไม่ได้จริงเฉพาะeTax_รายงานล็อก_50ทวิยื่นแล้ว_ใบเสร็จeTax()
    {
        var hard = SettlementUnpostRefusalKind.Unvoidable;
        var soft = SettlementUnpostRefusalKind.NeedsUserAction;
        var none = Array.Empty<SettlementUnpostCertificate>();
        Assert.Equal(hard, Assert.Single(SettlementUnpostGate.Evaluate(new[] { FeeDoc(etaxAccepted: true) }, none, NothingFiled)).Kind);
        Assert.Equal(hard, Assert.Single(SettlementUnpostGate.Evaluate(new[] { FeeDoc(inLockedReport: true) }, none, NothingFiled)).Kind);
        Assert.Equal(soft, Assert.Single(SettlementUnpostGate.Evaluate(new[] { FeeDoc() }, none, SepVatDeclared)).Kind);
        Assert.Equal(soft, Assert.Single(SettlementUnpostGate.Evaluate(new[] { FeeDoc(voidBlock: "มีใบลดหนี้อ้าง") }, none, NothingFiled)).Kind);
        var sale = new SettlementUnpostDocument(DocA, "TIV-1", DocumentType.TaxInvoice, SettlementPostingKeys.SummaryComponent(Day), Day, 70m,
            false, false, false);
        Assert.Equal(soft, Assert.Single(SettlementUnpostGate.Evaluate(new[] { sale }, none, SepVatDeclared)).Kind);
        var foreign = FeeDoc() with { IsForeignService = true, VatAmount = 0m };
        Assert.Equal(soft, Assert.Single(SettlementUnpostGate.Evaluate(new[] { foreign }, none,
            new HashSet<(TaxType, int, int)> { (TaxType.VatPp36, 2026, 9) })).Kind);
        var cert = new SettlementUnpostCertificate(Guid.NewGuid(), DocB, "WHT-1", TaxType.WithholdingTax53, 2026, 9, WithholdingTaxCertStatus.Issued);
        Assert.Equal(hard, Assert.Single(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), new[] { cert },
            new HashSet<(TaxType, int, int)> { (TaxType.WithholdingTax53, 2026, 9) })).Kind);
        var pay = new SettlementUnpostPayment(PayA, "RV-0001", DocA, "INV-0001", Day, 1070m, 1070m);
        Assert.Equal(soft, Assert.Single(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), none, SepVatDeclared,
            new[] { pay })).Kind);
        // ค่าเริ่มต้นของ record = ทิศบล็อก (ผู้สร้างที่ลืมระบุ ไม่ทำให้ของกำพร้าหลุดเป็นคำเตือน)
        Assert.Equal(soft, new SettlementUnpostRefusal("X", "เหตุ", "ทาง").Kind);
    }

    // ═════════════ S4-8: เดือนภาษีของด่าน = สูตรของรายงาน · ใบเสร็จอัตโนมัติคู่การรับชำระ ═════════════

    [Fact]
    public void S48_จุดความรับผิดคนละเดือนกับวันที่เอกสาร_ด่านใช้เดือนเดียวกับภพ30()
    {
        var none = Array.Empty<SettlementUnpostCertificate>();
        var octPoint = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        var octDeclared = new HashSet<(TaxType, int, int)> { (TaxType.VAT, 2026, 10) };
        // วันที่เอกสาร ก.ย. แต่จุดความรับผิด ต.ค. ⇒ รายงาน ต.ค. นับใบนี้ ⇒ ต.ค. ประกาศแล้ว = ปฏิเสธ (เดิมดู ก.ย. ⇒ หลุด)
        var refusal = Assert.Single(SettlementUnpostGate.Evaluate(new[] { FeeDoc(taxPoint: octPoint) }, none, octDeclared));
        Assert.Contains("10/2569", refusal.Reason);
        var sale = new SettlementUnpostDocument(DocA, "TIV-1", DocumentType.TaxInvoice, SettlementPostingKeys.SummaryComponent(Day), Day, 70m,
            false, false, false, TaxPointDate: octPoint);
        Assert.Single(SettlementUnpostGate.Evaluate(new[] { sale }, none, octDeclared));
        // ทิศตรงข้าม: ก.ย. ประกาศอย่างเดียว ⇒ ใบที่จุดความรับผิด ต.ค. ไม่อยู่ในแบบ ก.ย. ⇒ ไม่ปฏิเสธ · ไม่มีจุดความรับผิด ⇒ วันที่เอกสาร (เดิม)
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { FeeDoc(taxPoint: octPoint), sale }, none, SepVatDeclared));
        Assert.Single(SettlementUnpostGate.Evaluate(new[] { FeeDoc() }, none, SepVatDeclared));
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { FeeDoc() }, none, octDeclared));
    }

    [Fact]
    public void S48_ยกเลิกการรับชำระที่ใบเสร็จอัตโนมัติeTaxตอบรับแล้ว_ปฏิเสธ_ยังไม่ตอบรับ_ไม่ปฏิเสธ()
    {
        var pay = new SettlementUnpostPayment(PayA, "RV-0001", DocA, "INV-0001", null, 1070m, 1070m, ReceiptEtaxAccepted: true, ReceiptNumber: "RE-0001");
        var refusal = Assert.Single(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), Array.Empty<SettlementUnpostCertificate>(),
            NothingFiled, new[] { pay }));
        Assert.Equal(PayA, refusal.ArtifactId);
        Assert.Contains("RE-0001", refusal.Reason);
        Assert.Equal(SettlementUnpostRefusalKind.Unvoidable, refusal.Kind);
        Assert.Empty(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), Array.Empty<SettlementUnpostCertificate>(),
            NothingFiled, new[] { pay with { ReceiptEtaxAccepted = false } }));
    }

    // ═════════════ S4-7: ข้อความ "มีงานอื่นกำลังทำอยู่" เป็นกลาง (ผู้ถือล็อกเป็นได้ทุกเส้น) ═════════════

    [Fact]
    public void S47_ข้อความล็อกช่องทางไม่อ้างว่ากำลังลงบัญชี_ครอบการนำเข้าและแก้บรรทัด_บอกทางไปต่อ()
    {
        Assert.DoesNotContain("มีการลงบัญชี/ยกเลิก/จับคู่ของช่องทางนี้กำลังทำอยู่", SettlementChannelLock.BusyMessage);
        Assert.Contains("นำเข้า", SettlementChannelLock.BusyMessage);
        Assert.Contains("แก้/จับคู่บรรทัด", SettlementChannelLock.BusyMessage);
        Assert.Contains("ลงบัญชี", SettlementChannelLock.BusyMessage);
        Assert.Contains("รอสักครู่แล้วกดใหม่", SettlementChannelLock.BusyMessage);
    }
}
