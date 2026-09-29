using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 ทีม S4 — แก้ผลฝ่ายค้าน review198-S3 (S3-1 · S3-2 · S3-3 · S3-4 · S3-6 · S3-7) · ตัวตัดสินบริสุทธิ์ทุกตัว
/// มีสองครึ่งทุกข้อ: เคสที่พังกลับมาถูก และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ (F2 ข้อ 8) · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class SettlementReview198S4Tests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid DocB = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid PayA = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    // ═════════════ S3-1: รอบที่ลงค้างครึ่งทาง + ชิ้นที่ยกเลิกไม่ได้ ต้องมีทางไปต่อ ═════════════

    /// <summary>รอบโอน 1,963: ขายจับคู่ใบ A 1,000 · ขายเข้าใบสรุป 1,070 · ค่าคอมมิชชั่น −107 — บรรทัดเป็น object เดิมให้ "แก้แล้วคิดแผนใหม่" ได้</summary>
    private sealed class Scenario
    {
        public readonly SettlementBatch Batch = new() { CompanyId = Co, PayoutRef = "PO-001", PayoutDate = Day, NetPayout = 1963m, BankAccountId = Guid.NewGuid() };
        public readonly SettlementChannel Channel = new()
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Shopee", ClearingAccountId = Clearing,
            CounterpartyContactId = Guid.NewGuid(),
        };
        public readonly SettlementLine Receipt = new() { CompanyId = Co, Seq = 1, LineType = SettlementLineType.Sale, Amount = 1000m,
            MatchedDocumentId = DocA, TxnDate = Day, ExternalOrderId = "SP-0", MatchStatus = SettlementMatchStatus.Matched };
        public readonly SettlementLine Summary = new() { CompanyId = Co, Seq = 2, LineType = SettlementLineType.Sale, Amount = 1070m,
            TxnDate = Day, ExternalOrderId = "SP-1", MatchStatus = SettlementMatchStatus.AutoSummary };
        public readonly SettlementLine Fee = new() { CompanyId = Co, Seq = 3, LineType = SettlementLineType.Commission, Amount = -107m,
            TxnDate = Day, MatchStatus = SettlementMatchStatus.NotRequired };

        public IReadOnlyList<SettlementLine> Lines => new[] { Receipt, Summary, Fee };
        public SettlementPostingPlan Plan() => SettlementBatchMath.Plan(Batch, Lines, Channel, true);

        public string FeeComponent => SettlementPostingKeys.FeeComponent(Assert.Single(Plan().FeeDocuments).VatTreatment);
        public string SumComponent => SettlementPostingKeys.SummaryComponent(Assert.Single(Plan().SummarySales).Date);
    }

    private static SettlementPostingFacts Facts(SettlementPostingPlan plan, Func<Guid, SettlementReceiptTarget> target, int partial) => new(
        SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
        new HashSet<(int, int)>(), new HashSet<(int, int)>(), Array.Empty<string>(),
        true, true, Clearing,
        plan.Receipts.Select(r => target(r.DocumentId)).ToList(),
        Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, partial);

    [Fact]
    public void S31_ฉากผู้ตรวจ_ค้างครึ่งทาง_ใบสรุปeTaxตอบรับแล้ว_ใบที่จับคู่ถูกรับชำระไประหว่างนั้น_เลือกใบใหม่ได้แล้วลงต่อได้()
    {
        var s = new Scenario();
        var before = s.Plan();
        // ลงค้างครึ่งทาง: ใบค่าธรรมเนียม + ใบขายสรุป (e-Tax Accepted — ยกเลิกไม่ได้) ออกแล้ว · ขั้นรับชำระล้ม (ยังไม่มีการรับชำระ)
        var frozen = new SettlementFrozenParts(new[] { s.FeeComponent, s.SumComponent }, Array.Empty<Guid>());
        // ใบ A ถูกผู้ใช้รับชำระทางอื่นไปแล้ว ⇒ ด่านใบขายที่จะรับชำระบล็อก (ทางไปต่อที่ด่านบอก = เลือกใบใหม่)
        SettlementReceiptTarget Paid(Guid id) => new(id, true, "INV-A", DocumentType.Invoice, DocumentStatus.Paid, 0m, false);
        var blocked = SettlementPostingGate.Evaluate(before, Facts(before, Paid, 2));
        Assert.False(blocked.CanPost);
        Assert.Contains("ใบที่ถูกต้อง", Assert.Single(blocked.Issues, i => i.Code == SettlementPlanIssueCode.ReceiptDocumentNotPayable).NextStep);
        // ปุ่ม: รอบค้างครึ่งทางยังเปิด "ตัดสินการจับคู่" ให้บรรทัดที่ยังไม่มีชิ้นที่ออกแล้ว
        Assert.True(SettlementBatchActions.For(SettlementBatchStatus.Matched, Array.Empty<(Guid, SettlementLineType)>(), postingArtifacts: 2).CanRedecideLines);

        // ผู้ใช้เลือกใบ B ให้บรรทัดที่รับชำระไม่สำเร็จ ⇒ ชิ้นที่ออกแล้วไม่เปลี่ยน ⇒ แก้ได้
        s.Receipt.MatchedDocumentId = DocB;
        var after = s.Plan();
        Assert.Null(SettlementPartialEdit.Refusal(before, after, frozen));
        SettlementReceiptTarget Open(Guid id) => new(id, true, "INV-B", DocumentType.Invoice, DocumentStatus.Approved, 1000m, false);
        var resumed = SettlementPostingGate.Evaluate(after, Facts(after, Open, 2));
        Assert.True(resumed.CanPost);                                                          // ลงต่อได้ (ทำต่อจากที่ค้าง)
        Assert.Contains(resumed.Issues, i => i.Code == SettlementPlanIssueCode.PartialProgress && !i.Blocking);
    }

    [Fact]
    public void S31_ทิศตรงข้าม_แก้ที่เปลี่ยนชิ้นที่ออกแล้ว_ปฏิเสธพร้อมทางไปต่อ_ไม่มีชิ้นที่ออก_แก้ได้ทุกอย่าง()
    {
        // (1) ย้ายบรรทัดออกจากใบสรุปที่ออกแล้ว (ไปจับคู่ใบ B) ⇒ ใบสรุปเปลี่ยน ⇒ ปฏิเสธ
        var s1 = new Scenario();
        var before1 = s1.Plan();
        var frozen1 = new SettlementFrozenParts(new[] { s1.FeeComponent, s1.SumComponent }, Array.Empty<Guid>());
        s1.Summary.MatchStatus = SettlementMatchStatus.Matched;
        s1.Summary.MatchedDocumentId = DocB;
        var why = SettlementPartialEdit.Refusal(before1, s1.Plan(), frozen1);
        Assert.NotNull(why);
        Assert.Contains("ใบขายสรุปรายวัน", why);
        Assert.Contains("ทางไปต่อ", why);

        // (2) บรรทัดที่รับชำระไปแล้ว (มีการรับชำระที่มีป้ายบนใบ A) ⇒ ย้ายไปใบ B ⇒ ปฏิเสธ
        var s2 = new Scenario();
        var before2 = s2.Plan();
        var frozen2 = new SettlementFrozenParts(new[] { s2.FeeComponent, s2.SumComponent }, new[] { DocA });
        s2.Receipt.MatchedDocumentId = DocB;
        Assert.Contains("การรับชำระ", SettlementPartialEdit.Refusal(before2, s2.Plan(), frozen2));

        // (3) จัดประเภทบรรทัดค่าธรรมเนียมที่ออกใบแล้วใหม่ ⇒ ใบค่าธรรมเนียมเปลี่ยน ⇒ ปฏิเสธ
        var s3 = new Scenario();
        var before3 = s3.Plan();
        var frozen3 = new SettlementFrozenParts(new[] { s3.FeeComponent }, Array.Empty<Guid>());
        s3.Fee.LineType = SettlementLineType.Adjustment;
        s3.Fee.OverrideAccountId = Guid.NewGuid();
        s3.Fee.AdjustmentReason = "ทดสอบ";
        Assert.Contains("ใบค่าธรรมเนียม", SettlementPartialEdit.Refusal(before3, s3.Plan(), frozen3));

        // ทิศตรงข้าม: รอบที่ยังไม่มีชิ้นใดออก ⇒ ไม่มีอะไรถูกแช่แข็ง ⇒ แก้ได้ทุกอย่าง (พฤติกรรมเดิม)
        var s4 = new Scenario();
        var before4 = s4.Plan();
        s4.Summary.MatchStatus = SettlementMatchStatus.Matched;
        s4.Summary.MatchedDocumentId = DocB;
        Assert.Null(SettlementPartialEdit.Refusal(before4, s4.Plan(), new SettlementFrozenParts(Array.Empty<string>(), Array.Empty<Guid>())));
        // ไม่แก้อะไรเลย ⇒ ผ่านเสมอ
        var s5 = new Scenario();
        Assert.Null(SettlementPartialEdit.Refusal(s5.Plan(), s5.Plan(),
            new SettlementFrozenParts(new[] { s5.FeeComponent, s5.SumComponent }, new[] { DocA })));
    }

    [Fact]
    public void S31_ปุ่ม_ค้างครึ่งทาง_ตัดสินบรรทัดได้แต่ยกเลิกรอบไม่ได้_ไม่มีสิทธิ์นำเข้า_ซ่อนพร้อมเหตุผล_รอบปกติ_ไม่มีธงนี้()
    {
        var none = Array.Empty<(Guid, SettlementLineType)>();
        var half = SettlementBatchActions.For(SettlementBatchStatus.Matched, none, postingArtifacts: 2);
        Assert.True(half.CanRedecideLines);
        Assert.False(half.CanEditLines);
        Assert.False(half.CanVoid);
        Assert.Contains("ตัดสินการจับคู่", half.LockedReason);
        var noImport = SettlementBatchActions.For(SettlementBatchStatus.Matched, none, 2, null, null,
            new SettlementActionPermissions(false, true, true));
        Assert.False(noImport.CanRedecideLines);
        Assert.Contains("ตัดสินการจับคู่", noImport.PermissionNote);
        // ทิศตรงข้าม: รอบที่ยังไม่มีของ = แก้ได้ตามปกติ (ไม่ใช่เส้นพิเศษ) · ลงบัญชีแล้ว = ไม่มีทั้งคู่
        var clean = SettlementBatchActions.For(SettlementBatchStatus.Matched, none, postingArtifacts: 0);
        Assert.True(clean.CanEditLines);
        Assert.False(clean.CanRedecideLines);
        Assert.False(SettlementBatchActions.For(SettlementBatchStatus.Posted, none, 0).CanRedecideLines);
    }

    // ═════════════ S3-2: ภาษีซื้อของใบค่าธรรมเนียมในเดือน ภ.พ.30 ที่ประกาศว่ายื่นแล้ว ═════════════

    private static SettlementUnpostDocument FeeDoc(bool undue, DateTime? claimableAt, decimal vat = 7m)
        => new(DocB, "PV-0001", DocumentType.PaymentVoucher, SettlementPostingKeys.FeeComponent(SettlementFeeVatTreatment.InputVatPending),
            Day, vat, false, false, false, undue, claimableAt, null);

    [Fact]
    public void S32_ภาษีซื้อที่ถึงกำหนดในเดือนที่ยื่นภพ30แล้ว_ปฏิเสธ_พักอยู่หรือเดือนยังไม่ยื่น_ไม่ปฏิเสธ()
    {
        var sepDeclared = new HashSet<(TaxType, int, int)> { (TaxType.VAT, 2026, 9) };
        var none = Array.Empty<SettlementUnpostCertificate>();
        // ไม่พัก (เคลมเดือนเอกสาร) ⇒ ก.ย. ยื่นแล้ว ⇒ ปฏิเสธ (เดิมตรวจเฉพาะฝั่งขาย)
        var r = SettlementUnpostGate.Evaluate(new[] { FeeDoc(false, null) }, none, sepDeclared);
        var refusal = Assert.Single(r);
        Assert.Contains("ภาษีซื้อ", refusal.Reason);
        Assert.Equal(DocB, refusal.ArtifactId);
        // พักแล้วถึงกำหนดเดือน ต.ค. (เติมใบกำกับรายเดือน) ⇒ ต.ค. ยื่นแล้ว ⇒ ปฏิเสธ · ก.ย. ยื่นแล้วอย่างเดียว ⇒ ไม่ปฏิเสธ
        var octDeclared = new HashSet<(TaxType, int, int)> { (TaxType.VAT, 2026, 10) };
        Assert.Single(SettlementUnpostGate.Evaluate(new[] { FeeDoc(true, new DateTime(2026, 10, 5)) }, none, octDeclared));
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { FeeDoc(true, new DateTime(2026, 10, 5)) }, none, sepDeclared));
        // ทิศตรงข้าม: ยังพักรอใบกำกับ (ไม่อยู่ใน ภ.พ.30 เดือนใด) · ไม่มี VAT · ไม่มีเดือนที่ยื่น ⇒ ยกเลิกได้
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { FeeDoc(true, null) }, none, sepDeclared));
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { FeeDoc(false, null, vat: 0m) }, none, sepDeclared));
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { FeeDoc(false, null) }, none, new HashSet<(TaxType, int, int)>()));
    }

    // ═════════════ S3-3: เหตุที่ VoidDocumentAsync ปฏิเสธเอง ต้องถูกตรวจก่อนแตะชิ้นแรก ═════════════

    [Fact]
    public void S33_ตัวตัดสินเดียว_เอกสารลูกมาก่อนใบลดหนี้อ้างเลขที่_ใบที่ไม่มีอะไรอ้าง_ไม่อยู่ในผล()
    {
        var decided = DocumentVoidPreconditions.Decide(new[]
        {
            new DocumentVoidChildFact(DocA, DocumentType.CreditNote, "CN-0009", ByTextReference: true),
            new DocumentVoidChildFact(DocA, DocumentType.Receipt, "RE-0001", ByTextReference: false),
            new DocumentVoidChildFact(DocB, DocumentType.CreditNote, "CN-0010", ByTextReference: true),
        });
        Assert.Contains("เอกสารลูก", decided[DocA]);
        Assert.Contains("RE-0001", decided[DocA]);
        Assert.Contains("ใบลดหนี้", decided[DocB]);
        Assert.Contains("§86/9-10", decided[DocB]);
        Assert.Contains("ใบเพิ่มหนี้", DocumentVoidPreconditions.Reason(new DocumentVoidChildFact(DocB, DocumentType.DebitNote, "DN-1", true)));
        Assert.False(DocumentVoidPreconditions.Decide(Array.Empty<DocumentVoidChildFact>()).ContainsKey(DocA));
    }

    [Fact]
    public void S33_ใบสรุปที่มีใบลดหนี้อ้าง_ปฏิเสธทั้งรอบก่อนแตะชิ้นแรก_ไม่มีเอกสารอ้าง_ยกเลิกได้()
    {
        var reason = DocumentVoidPreconditions.Reason(new DocumentVoidChildFact(DocA, DocumentType.CreditNote, "CN-0009", true));
        var sum2 = new SettlementUnpostDocument(DocA, "TIV-0002", DocumentType.TaxInvoice, SettlementPostingKeys.SummaryComponent(Day.AddDays(1)),
            Day.AddDays(1), 70m, false, false, false, false, null, reason);
        var sum1 = sum2 with { Id = DocB, Number = "TIV-0001", Component = SettlementPostingKeys.SummaryComponent(Day), VoidBlock = null };
        var none = new HashSet<(TaxType, int, int)>();
        var r = SettlementUnpostGate.Evaluate(new[] { sum1, sum2 }, Array.Empty<SettlementUnpostCertificate>(), none);
        var refusal = Assert.Single(r);
        Assert.Equal("TIV-0002", refusal.Subject);
        Assert.Contains("CN-0009", refusal.Reason);
        Assert.Equal(DocA, refusal.ArtifactId);
        // ทิศตรงข้าม
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { sum1 }, Array.Empty<SettlementUnpostCertificate>(), none));
    }

    // ═════════════ S3-7: ยกเลิกการรับชำระใบบริการที่ภาษีขายถึงกำหนดในเดือนที่ยื่นแล้ว ═════════════

    [Fact]
    public void S37_การรับชำระทำให้ภาษีขายถึงกำหนดในเดือนที่ยื่นแล้วและไม่เหลือเงินรับอื่น_ปฏิเสธ_มีเงินรับอื่นหรือเดือนยังไม่ยื่น_ไม่ปฏิเสธ()
    {
        var declared = new HashSet<(TaxType, int, int)> { (TaxType.VAT, 2026, 9) };
        var pay = new SettlementUnpostPayment(PayA, "RV-0001", DocA, "INV-0001", new DateTime(2026, 9, 20), 1070m, 1070m);
        var r = SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), Array.Empty<SettlementUnpostCertificate>(), declared, new[] { pay });
        var refusal = Assert.Single(r);
        Assert.Contains("§78/1", refusal.Reason);
        Assert.Equal(PayA, refusal.ArtifactId);
        // ทิศตรงข้าม: ใบยังมีเงินรับจากทางอื่น (ยกเลิกแล้วภาษีขายไม่ถูกกลับ) · ใบที่ไม่ใช่ VAT พัก (ไม่มีวันถึงกำหนด) · เดือนยังไม่ยื่น ·
        // ผู้เรียกเก่าที่ไม่ส่งการรับชำระ (null = ไม่ได้ตรวจ)
        Assert.Empty(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), Array.Empty<SettlementUnpostCertificate>(), declared,
            new[] { pay with { DocumentPaidAmount = 2000m } }));
        Assert.Empty(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), Array.Empty<SettlementUnpostCertificate>(), declared,
            new[] { pay with { OutputVatDueAt = null } }));
        Assert.Empty(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), Array.Empty<SettlementUnpostCertificate>(),
            new HashSet<(TaxType, int, int)>(), new[] { pay }));
        Assert.Empty(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), Array.Empty<SettlementUnpostCertificate>(), declared));
    }

    // ═════════════ S3-6: ของกำพร้าที่ยกเลิกไม่ได้ ไม่บล็อกทั้งช่องทางตลอดไป ═════════════

    /// <summary>รอบ 200 (DECISIONS ข้อ 10): ของกำพร้าที่ยกเลิกไม่ได้ — ยังไม่รับรู้ = บล็อก (ทางไปต่อ = รับรู้ของกำพร้า) · รับรู้แล้ว = เตือนไม่บล็อก ⇒ ไม่ล็อกช่องทางตลอดไป</summary>
    [Fact]
    public void S36_ของกำพร้าที่ยกเลิกไม่ได้_รับรู้แล้วเตือนไม่บล็อก_ยังไม่รับรู้บล็อก_ของกำพร้าที่ยกเลิกได้_ยังบล็อก()
    {
        var s = new Scenario();
        var plan = s.Plan();
        SettlementReceiptTarget Open(Guid id) => new(id, true, "INV-A", DocumentType.Invoice, DocumentStatus.Approved, 1000m, false);
        const string Hard = "เอกสาร TIV-9 ที่ลงบัญชีให้รอบโอน PO-X (ถูกยกเลิกแล้ว) ยกเลิกในระบบไม่ได้แล้ว: e-Tax ตอบรับแล้ว";
        var warned = SettlementPostingGate.Evaluate(plan, Facts(plan, Open, 0) with { AcknowledgedOrphans = new[] { Hard + " — รับรู้แล้วโดย ก" } });
        Assert.True(warned.CanPost);
        var issue = Assert.Single(warned.Issues, i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts);
        Assert.False(issue.Blocking);
        Assert.Contains("ซ้ำ", issue.NextStep);
        var unacked = SettlementPostingGate.Evaluate(plan, Facts(plan, Open, 0) with { UnvoidableOrphans = new[] { Hard } });
        Assert.False(unacked.CanPost);
        Assert.Contains("รับรู้ของกำพร้า", Assert.Single(unacked.Issues, i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts).NextStep);
        // ทิศตรงข้าม: ของกำพร้าที่ยังยกเลิกได้ ⇒ บล็อกเหมือนเดิม (ทางไปต่อ = ยกเลิกทีละใบ)
        var blocked = SettlementPostingGate.Evaluate(plan, Facts(plan, Open, 0) with { OrphanArtifacts = new[] { "เอกสาร PV-9 ยังไม่ถูกยกเลิก" } });
        Assert.False(blocked.CanPost);
        Assert.True(Assert.Single(blocked.Issues, i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts).Blocking);
        Assert.DoesNotContain(SettlementPostingGate.Evaluate(plan, Facts(plan, Open, 0)).Issues,
            i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts);
    }

    [Fact]
    public void S36_ตัวแยกของกำพร้าใช้ArtifactIdจากด่านยกเลิกการลงบัญชีตัวเดียว_ทุกเหตุระบุชิ้น()
    {
        var accepted = new SettlementUnpostDocument(DocA, "TIV-9", DocumentType.TaxInvoice, SettlementPostingKeys.SummaryComponent(Day), Day, 70m,
            false, true, false, false, null, null);
        var cert = new SettlementUnpostCertificate(Guid.NewGuid(), DocB, "WHT-1", TaxType.WithholdingTax53, 2026, 9, WithholdingTaxCertStatus.Issued);
        var filed = new HashSet<(TaxType, int, int)> { (TaxType.WithholdingTax53, 2026, 9) };
        var r = SettlementUnpostGate.Evaluate(new[] { accepted }, new[] { cert }, filed);
        Assert.All(r, x => Assert.NotNull(x.ArtifactId));
        Assert.Contains(r, x => x.ArtifactId == DocA);
        Assert.Contains(r, x => x.ArtifactId == DocB);                                    // 50 ทวิ ยื่นแล้ว ⇒ เอกสารของมันยกเลิกไม่ได้
    }

    // ═════════════ S3-4: คีย์กันซ้ำไม่ขึ้นกับเลขรอบโอนที่ผู้ใช้พิมพ์ ═════════════

    private static readonly SettlementTxnKeyInput WithdrawFee = new(null, "Withdrawal fee", null, -10m, Day);
    private static readonly SettlementTxnKeyInput SaleNoId = new(null, "Sale", "O-1", 500m, Day);

    [Fact]
    public void S34_ไฟล์เดิมไม่มีคอลัมน์รอบโอน_นำเข้าด้วยเลขที่พิมพ์ต่าง_ได้คีย์เดิม_คีย์รุ่นก่อนต่างกันจึงเก็บไว้เทียบ()
    {
        var file = new[] { WithdrawFee, SaleNoId };
        var keys = SettlementTxnKey.Assign(file);
        Assert.All(keys, k => Assert.StartsWith(SettlementTxnKey.Version + "rowc:", k));
        Assert.Equal(keys, SettlementTxnKey.Assign(new[] { SaleNoId, WithdrawFee }).Reverse().ToList());   // สลับลำดับแถวได้คีย์เดิม
        // เลขที่พิมพ์ไม่เป็นอินพุตของคีย์แล้ว — มีแค่ในคีย์รุ่นก่อน (ใช้เทียบของที่นำเข้าไว้ก่อนแก้)
        var typedA = SettlementTxnKey.LegacyKeys(file, "PO-2609-01");
        var typedB = SettlementTxnKey.LegacyKeys(file, "PO-2609-1");
        Assert.NotEqual(typedA[0], typedB[0]);
        Assert.Contains(typedA[0], k => k.StartsWith(SettlementTxnKey.Version + "row:", StringComparison.Ordinal));
        Assert.Contains(typedA[0], k => k.StartsWith("row:", StringComparison.Ordinal));                     // v1
        Assert.DoesNotContain(keys[0], typedA[0]);
    }

    [Fact]
    public void S34_ทิศตรงข้าม_RB5_สองรอบโอนไฟล์ต่างกันที่มีแถวหน้าตาเหมือนกัน_ไม่ชน_ไฟล์มีคอลัมน์รอบโอน_คีย์เดิมทุกตัวอักษร()
    {
        var payout1 = SettlementTxnKey.Assign(new[] { WithdrawFee, SaleNoId });
        var payout2 = SettlementTxnKey.Assign(new[] { WithdrawFee, SaleNoId with { Amount = 700m } });
        Assert.NotEqual(payout1[0], payout2[0]);                                                              // ค่าธรรมเนียมถอน −10 ของสองรอบไม่ชน
        // ไฟล์มีคอลัมน์รอบโอน ⇒ v2:row: + รอบโอนของแถว (เหมือนรุ่นก่อน) · คีย์รุ่นก่อนแบบพิมพ์เลขเท่ากับคีย์ปัจจุบันจึงไม่ถูกใส่ซ้ำ
        var col = new[] { WithdrawFee with { PayoutRef = "PO-1" } };
        var k = Assert.Single(SettlementTxnKey.Assign(col));
        Assert.StartsWith(SettlementTxnKey.Version + "row:", k);
        var legacy = Assert.Single(SettlementTxnKey.LegacyKeys(col, "พิมพ์อะไรก็ได้"));
        Assert.DoesNotContain(k, legacy);
        Assert.Single(legacy);                                                                                 // เหลือแค่ v1
        // แถวที่มี id: คีย์รุ่นปัจจุบันไม่ขึ้นกับรอบโอน · v1 = id|ป้าย
        var idRow = new[] { new SettlementTxnKeyInput("T1", "Commission Fee", null, -5m, Day) };
        Assert.Equal(SettlementTxnKey.Assign(idRow), SettlementTxnKey.Assign(new[] { idRow[0] with { PayoutRef = "PO-9" } }));
        Assert.Contains(SettlementTxnKey.LegacyKeys(idRow, "PO-1")[0], x => x.StartsWith("T1|", StringComparison.Ordinal));
    }

    [Fact]
    public void S34_ไฟล์ฉบับแก้ของรอบเดิม_เทียบเนื้อหาแบบนับจำนวน_แถวเหมือนกัน3กับบรรทัดเดิม2_เพิ่ม1()
    {
        var a = SettlementTxnKey.ContentKey("O-9", "ค่าบริการ", -10m, Day);
        var b = SettlementTxnKey.ContentKey("O-9", "ค่าบริการ", -11m, Day);
        Assert.Equal(a, SettlementTxnKey.ContentKey(" O-9 ", "  ค่าบริการ ", -10.001m, Day));              // ตัดช่องว่าง/ปัดเศษแบบเดียวกับที่เก็บ
        Assert.NotEqual(a, SettlementTxnKey.ContentKey("O-9", "ค่าบริการ", -10m, Day.AddDays(1)));
        var matched = SettlementTxnKey.MatchByContent(new string?[] { a, a, a, b, null }, new[] { a, a });
        Assert.Equal(new[] { 0, 1 }, matched.OrderBy(x => x).ToArray());
        Assert.Empty(SettlementTxnKey.MatchByContent(new string?[] { a }, Array.Empty<string>()));
        Assert.True(SettlementTxnKey.IsRowKey(SettlementTxnKey.Assign(new[] { WithdrawFee })[0]));
        Assert.True(SettlementTxnKey.IsRowKey("row:abc"));
        Assert.False(SettlementTxnKey.IsRowKey(SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput("T1", "fee", null, 1m, Day) })[0]));
        Assert.False(SettlementTxnKey.IsRowKey(SettlementTxnKey.ForPaymentIntent(DocA, "sale")));
        Assert.False(SettlementTxnKey.IsRowKey(null));
    }

    // ═════════════ S3-9: ข้อความ "กำลังทำอยู่" ตัวเดียวของทั้งสองฝั่ง ═════════════

    [Fact]
    public void S39_ล็อกฝั่งนำเข้าลองล็อกไม่รอ_ข้อความเดียวกับฝั่งลงบัญชี_บอกทางไปต่อ()
    {
        Assert.Contains("รอสักครู่แล้วกดใหม่", SettlementChannelLock.BusyMessage);
        Assert.Equal(AdvisoryLockKey.For(Co, SettlementChannelLock.Scope, SettlementChannelLock.Part(DocA)), SettlementChannelLock.Key(Co, DocA));
    }
}
