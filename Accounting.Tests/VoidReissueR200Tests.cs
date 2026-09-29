using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม V1 — ยกเลิก-ออกใบแทน (คำตัดสินข้อ 9 · review198-S3 S3-5) · ใบเสร็จอัตโนมัติ vs e-Tax ตอนยกเลิกการชำระ/เช็คเด้ง
/// (คำตัดสินข้อ 11 · review198-S4 S4-8 ค้าง) · ทุกข้อมีสองครึ่ง: เคสที่พังกลับมาถูก และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ (F2 ข้อ 8) ·
/// จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class VoidReissueR200Tests
{
    private static readonly DateTime Day = new(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid BuyerA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid BuyerB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // ═════════════ ข้อ 11: ใบเสร็จอัตโนมัติคู่การชำระ vs e-Tax ═════════════

    [Theory]
    [InlineData(null)]
    [InlineData(EtaxStatus.Generated)]
    [InlineData(EtaxStatus.Signed)]
    [InlineData(EtaxStatus.Error)]
    [InlineData(EtaxStatus.Rejected)]
    public void R200_V1_ใบเสร็จที่ยังไม่ถึงกรมสรรพากร_ยกเลิกการชำระได้เหมือนเดิมทุกทางเข้า(EtaxStatus? status)
    {
        foreach (var cause in new[] { PaymentVoidCause.User, PaymentVoidCause.SettlementUnpost, PaymentVoidCause.ChequeBounce })
        {
            var d = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(status, "RE-0001", cause);
            Assert.Equal(AutoReceiptEtaxAction.Void, d.Action);
            Assert.Null(d.Message);
        }
    }

    [Theory]
    [InlineData(EtaxStatus.Submitted)]
    [InlineData(EtaxStatus.Accepted)]
    public void R200_V1_ผู้ใช้กดยกเลิกการชำระ_ใบเสร็จถึงกรมสรรพากรแล้ว_ปฏิเสธพร้อมทางไปต่อ(EtaxStatus status)
    {
        var d = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(status, "RE-0001", PaymentVoidCause.User);
        Assert.Equal(AutoReceiptEtaxAction.Refuse, d.Action);
        Assert.Contains("RE-0001", d.Message);
        Assert.Contains("ระบบยังไม่ได้แตะอะไร", d.Message);
        // ทางไปต่อตรงเหตุ: ส่งแล้วยังไม่ตอบรับ = ยกเลิก e-Tax ก่อน · ตอบรับแล้ว = ยกเลิกที่กรมสรรพากร (+ เช็คเด้งมีเส้นของตัวเอง)
        Assert.Contains(status == EtaxStatus.Accepted ? "เช็คเด้ง" : "กดยกเลิก e-Tax", d.Message);
    }

    [Theory]
    [InlineData(EtaxStatus.Submitted)]
    [InlineData(EtaxStatus.Accepted)]
    public void R200_V1_ยกเลิกการลงบัญชีรอบโอน_ใบเสร็จถึงกรมสรรพากรแล้ว_ตาข่ายชั้นสองปฏิเสธ(EtaxStatus status)
    {
        var d = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(status, "RE-0001", PaymentVoidCause.SettlementUnpost);
        Assert.Equal(AutoReceiptEtaxAction.Refuse, d.Action);
    }

    [Theory]
    [InlineData(EtaxStatus.Submitted)]
    [InlineData(EtaxStatus.Accepted)]
    public void R200_V1_เช็คเด้ง_ห้ามบล็อก_ใบเสร็จที่ถึงกรมสรรพากรแล้วติดธงแทนการประทับVoidedเงียบ(EtaxStatus status)
    {
        var d = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(status, "RE-0009", PaymentVoidCause.ChequeBounce);
        Assert.Equal(AutoReceiptEtaxAction.FlagEtaxCancellation, d.Action);
        Assert.StartsWith("ต้องยกเลิกทาง e-Tax", d.Message);
        Assert.Contains("RE-0009", d.Message);
    }

    [Fact]
    public void R200_V1_สถานะeTaxที่ไปไกลที่สุด_Acceptedชนะ_ไม่นับVoided()
    {
        Assert.Equal(EtaxStatus.Accepted, DocumentVoidPreconditions.StrongestEtax(new[] { EtaxStatus.Error, EtaxStatus.Accepted, EtaxStatus.Voided }));
        Assert.Equal(EtaxStatus.Submitted, DocumentVoidPreconditions.StrongestEtax(new[] { EtaxStatus.Error, EtaxStatus.Submitted }));
        Assert.Equal(EtaxStatus.Error, DocumentVoidPreconditions.StrongestEtax(new[] { EtaxStatus.Error }));
        Assert.Null(DocumentVoidPreconditions.StrongestEtax(new[] { EtaxStatus.Voided }));
        Assert.Null(DocumentVoidPreconditions.StrongestEtax(Array.Empty<EtaxStatus>()));
        Assert.True(DocumentVoidPreconditions.EtaxReachedRd(EtaxStatus.Submitted));
        Assert.False(DocumentVoidPreconditions.EtaxReachedRd(EtaxStatus.Rejected));
        Assert.False(DocumentVoidPreconditions.EtaxReachedRd(null));
    }

    // ด่านยกเลิกการลงบัญชีรอบโอนต้องเห็นใบเสร็จที่ส่งแล้วก่อนแตะชิ้นแรก (ไม่งั้น VoidPaymentAsync ปฏิเสธกลางทาง = ครึ่งกลับครึ่งค้าง)
    [Fact]
    public void R200_V1_ด่านยกเลิกการลงบัญชี_ใบเสร็จส่งeTaxแล้วยังไม่ตอบรับ_ปฏิเสธแบบให้คนทำก่อน_ตอบรับแล้วยังยกเลิกไม่ได้จริง()
    {
        var payId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var submitted = new SettlementUnpostPayment(payId, "RV-0001", docId, "INV-0001", null, 1070m, 1070m,
            ReceiptEtaxAccepted: false, ReceiptNumber: "RE-0001", ReceiptEtaxSubmitted: true);
        var r = Assert.Single(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(),
            Array.Empty<SettlementUnpostCertificate>(), new HashSet<(TaxType, int, int)>(), new[] { submitted }));
        Assert.Equal(SettlementUnpostRefusalKind.NeedsUserAction, r.Kind);
        Assert.Equal(payId, r.ArtifactId);
        Assert.Contains("ยกเลิก e-Tax", r.NextStep);

        var accepted = submitted with { ReceiptEtaxAccepted = true, ReceiptEtaxSubmitted = false };
        var a = Assert.Single(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(),
            Array.Empty<SettlementUnpostCertificate>(), new HashSet<(TaxType, int, int)>(), new[] { accepted }));
        Assert.Equal(SettlementUnpostRefusalKind.Unvoidable, a.Kind);

        // ทิศตรงข้าม: ใบเสร็จที่ยังไม่ถึงกรมสรรพากร ไม่ถูกด่านนี้แตะ
        var clean = submitted with { ReceiptEtaxSubmitted = false };
        Assert.Empty(SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(),
            Array.Empty<SettlementUnpostCertificate>(), new HashSet<(TaxType, int, int)>(), new[] { clean }));
    }

    // ═════════════ ข้อ 9: ข้อความ 409 ของใบขายที่รอบโอนรับชำระ ต้องพาไปทางที่ถูก ═════════════

    [Fact]
    public void R200_V1_ยกเลิกใบขายที่รอบโอนPostedรับชำระ_ข้อความชี้ยกเลิกและออกใบแทน_ใบลดหนี้_ยกเลิกการลงบัญชี()
    {
        var why = SettlementArtifactGuard.PaidDocumentVoidReason(SettlementBatchStatus.Posted, false, false, "PO-777");
        Assert.NotNull(why);
        Assert.Contains("PO-777", why);
        Assert.Contains("ยกเลิกและออกใบแทน", why);
        Assert.Contains("ใบลดหนี้", why);
        Assert.Contains("ยกเลิกการลงบัญชี", why);
        Assert.NotNull(SettlementArtifactGuard.PaidDocumentVoidReason(SettlementBatchStatus.BankMatched, false, false, "PO-777"));
    }

    [Fact]
    public void R200_V1_รอบโอนยังไม่ลงบัญชี_ถูกลบ_หรือกำลังยกเลิกการลงบัญชี_ยกเลิกใบขายได้ตามเดิม()
    {
        Assert.Null(SettlementArtifactGuard.PaidDocumentVoidReason(SettlementBatchStatus.Matched, false, false, "PO-1"));
        Assert.Null(SettlementArtifactGuard.PaidDocumentVoidReason(SettlementBatchStatus.Posted, true, false, "PO-1"));
        Assert.Null(SettlementArtifactGuard.PaidDocumentVoidReason(SettlementBatchStatus.Posted, false, true, "PO-1"));
        Assert.Null(SettlementArtifactGuard.PaidDocumentVoidReason(null, false, false, null));
    }

    // ═════════════ ข้อ 9: ด่านเข้า "ยกเลิกและออกใบแทน" ═════════════

    private static SettlementPaidReissueFacts OkFacts() => new(
        DocumentType.TaxInvoice, DocumentStatus.Paid, IsSettlementReceipt: false, AlreadyReplaced: false, CreatedBySettlementBatch: false,
        PostedBatchPaymentBlock: "ใบนี้รับชำระจากรอบโอน PO-1 ที่ลงบัญชีแล้ว", IsDeposit: false, HasDepositApplied: false,
        EtaxAccepted: false, FilingLocked: false, WhtFiledBlock: null, ChildBlock: null, ClosedPeriodName: null,
        SharedPaymentNumber: null, ActiveWhtCertificates: 0, HasVatDeferral: false,
        Receipts: new[] { new ReissueReceiptFact("RE-0001", null) });

    [Fact]
    public void R200_V1_ใบขายที่รอบโอนPostedรับชำระ_ด่านเดิมผ่านทุกตัว_กดยกเลิกและออกใบแทนได้()
    {
        var v = SettlementPaidReissue.Decide(OkFacts());
        Assert.True(v.Relevant);
        Assert.True(v.Allowed);
        Assert.Null(v.Reason);
        // ใบเสร็จอัตโนมัติที่ยังไม่ถึงกรมสรรพากร (สร้าง/ผิดพลาด) ไม่บล็อก — ระบบยกเลิกแล้วออกใหม่ให้
        Assert.True(SettlementPaidReissue.Decide(OkFacts() with { Receipts = new[] { new ReissueReceiptFact("RE-1", EtaxStatus.Error) } }).Allowed);
        Assert.True(SettlementPaidReissue.Decide(OkFacts() with { Type = DocumentType.Invoice, Status = DocumentStatus.PartiallyPaid }).Allowed);
    }

    [Fact]
    public void R200_V1_ใบที่ไม่เกี่ยว_ไม่แสดงปุ่มเลย()
    {
        // ไม่มีการรับชำระจากรอบโอนที่ลงบัญชีแล้ว = ใช้ยกเลิกปกติได้ (ทางนี้ไม่เกี่ยว)
        Assert.False(SettlementPaidReissue.Decide(OkFacts() with { PostedBatchPaymentBlock = null }).Relevant);
        Assert.False(SettlementPaidReissue.Decide(OkFacts() with { Type = DocumentType.Quotation }).Relevant);
        Assert.False(SettlementPaidReissue.Decide(OkFacts() with { Type = DocumentType.PurchaseInvoice }).Relevant);
        Assert.False(SettlementPaidReissue.Decide(OkFacts() with { Status = DocumentStatus.Voided }).Relevant);
        Assert.False(SettlementPaidReissue.Decide(OkFacts() with { Status = DocumentStatus.Draft }).Relevant);
        Assert.False(SettlementPaidReissue.Decide(OkFacts() with { AlreadyReplaced = true }).Relevant);
        Assert.False(SettlementPaidReissue.Decide(OkFacts() with { IsSettlementReceipt = true }).Relevant);
        Assert.False(SettlementPaidReissue.Decide(OkFacts() with { CreatedBySettlementBatch = true }).Relevant);
        // ด่านชั้นแรกตัวเดียวกัน (หน้าเอกสารไม่อ่านฐานเพิ่มสำหรับใบที่ไม่เกี่ยว)
        Assert.Null(SettlementPaidReissue.QuickRelevance(DocumentType.TaxInvoice, DocumentStatus.Approved, false, false, false));
        Assert.NotNull(SettlementPaidReissue.QuickRelevance(DocumentType.CreditNote, DocumentStatus.Approved, false, false, false));
    }

    [Theory]
    [InlineData("etax", "REISSUE-ETAX-ACCEPTED")]
    [InlineData("locked", "REISSUE-FILING-LOCKED")]
    [InlineData("wht", "RD-50TWI-FILED")]
    [InlineData("child", "REISSUE-CHILD")]
    [InlineData("period", "REISSUE-PERIOD-CLOSED")]
    [InlineData("shared", "REISSUE-SHARED-PAYMENT")]
    [InlineData("deposit", "REISSUE-DEPOSIT")]
    [InlineData("applied", "REISSUE-DEPOSIT")]
    [InlineData("cert", "REISSUE-WHT-CERT")]
    [InlineData("deferral", "REISSUE-VAT-DEFERRAL")]
    [InlineData("receiptSubmitted", "REISSUE-RECEIPT-ETAX")]
    [InlineData("receiptAccepted", "REISSUE-RECEIPT-ETAX")]
    public void R200_V1_ด่านเดิมของการยกเลิกเอกสารยังบล็อก_พร้อมทางไปต่อและไม่แตะอะไร(string which, string rule)
    {
        var f = which switch
        {
            "etax" => OkFacts() with { EtaxAccepted = true },
            "locked" => OkFacts() with { FilingLocked = true },
            "wht" => OkFacts() with { WhtFiledBlock = "50 ทวิ อยู่ในแบบที่ยื่นแล้ว" },
            "child" => OkFacts() with { ChildBlock = "ยกเลิกไม่ได้ — มีใบลดหนี้ CN-1 อ้างเลขที่ใบนี้อยู่" },
            "period" => OkFacts() with { ClosedPeriodName = "ส.ค. 2569" },
            "shared" => OkFacts() with { SharedPaymentNumber = "RV-0009" },
            "deposit" => OkFacts() with { IsDeposit = true },
            "applied" => OkFacts() with { HasDepositApplied = true },
            "cert" => OkFacts() with { ActiveWhtCertificates = 1 },
            "deferral" => OkFacts() with { HasVatDeferral = true },
            "receiptSubmitted" => OkFacts() with { Receipts = new[] { new ReissueReceiptFact("RE-0001", EtaxStatus.Submitted) } },
            _ => OkFacts() with { Receipts = new[] { new ReissueReceiptFact("RE-0001", EtaxStatus.Accepted) } },
        };
        var v = SettlementPaidReissue.Decide(f);
        Assert.True(v.Relevant);    // ปุ่มแสดงแบบปิดพร้อมเหตุผล (ไม่หายเงียบ)
        Assert.False(v.Allowed);
        Assert.Equal(rule, v.RuleCode);
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", v.Reason);
    }

    // ═════════════ ข้อ 9: ใบใหม่ต้องเท่าใบเดิม (ต่างได้เฉพาะผู้ซื้อ/หมายเหตุ/คำบรรยาย) ═════════════

    private static (Document Doc, List<DocumentLine> Lines) Invoice()
    {
        var doc = new Document
        {
            CompanyId = Guid.NewGuid(), DocumentNumber = "TIV-20260805-0003", DocumentType = DocumentType.TaxInvoice,
            Status = DocumentStatus.Paid, DocumentDate = Day, TaxPointDate = Day, ContactId = BuyerA,
            SubTotal = 1000m, VatAmount = 70m, TotalAmount = 1070m, PaidAmount = 1070m, BalanceDue = 0m,
            IsTaxInvoiceByLaw = true, IssuerBranchCode = "00000", Notes = "ขอบคุณที่ใช้บริการ",
        };
        var lines = new List<DocumentLine>
        {
            new() { DocumentId = doc.Id, LineOrder = 1, Description = "เสื้อยืด", Quantity = 2m, Unit = "ตัว", UnitPrice = 300m,
                Amount = 600m, VatRate = 7m, VatAmount = 42m },
            new() { DocumentId = doc.Id, LineOrder = 2, Description = "กางเกง", Quantity = 1m, Unit = "ตัว", UnitPrice = 400m,
                Amount = 400m, VatRate = 7m, VatAmount = 28m },
        };
        return (doc, lines);
    }

    [Fact]
    public void R200_V1_ใบแทนที่เปลี่ยนเฉพาะผู้ซื้อ_หมายเหตุ_คำบรรยาย_ผ่านด่านและจดสิ่งที่เปลี่ยน()
    {
        var (doc, lines) = Invoice();
        var before = ReissueDocumentSnapshot.Of(doc, lines);
        var after = before with
        {
            ContactId = BuyerB,
            Notes = "หมายเหตุใหม่",
            Lines = new[] { before.Lines[0] with { Description = "เสื้อยืดคอกลม" }, before.Lines[1] },
        };
        Assert.Empty(SettlementPaidReissue.ForbiddenChanges(before, after));
        Assert.Equal(new[] { "ผู้ซื้อ", "หมายเหตุ", "คำบรรยายบรรทัด 1" }, SettlementPaidReissue.AllowedChanges(before, after));
        // ออกใบแทนด้วยข้อมูลเดิมทุกตัว (เช่น แก้ทะเบียนผู้ติดต่อแล้ว) ก็ผ่าน
        Assert.Empty(SettlementPaidReissue.ForbiddenChanges(before, before));
        Assert.Empty(SettlementPaidReissue.AllowedChanges(before, before));
    }

    [Fact]
    public void R200_V1_ยอด_อัตราVAT_taxpoint_จำนวนบรรทัด_คำบรรยายว่าง_ต่างจากใบเดิม_ด่านปฏิเสธ()
    {
        var (doc, lines) = Invoice();
        var before = ReissueDocumentSnapshot.Of(doc, lines);
        Assert.Contains(SettlementPaidReissue.ForbiddenChanges(before, before with { TotalAmount = 1080m }), d => d.StartsWith("ยอดรวมทั้งสิ้น"));
        Assert.Contains(SettlementPaidReissue.ForbiddenChanges(before, before with { VatAmount = 0m }), d => d.StartsWith("ภาษีมูลค่าเพิ่ม"));
        Assert.Contains(SettlementPaidReissue.ForbiddenChanges(before, before with { TaxPointDate = Day.AddDays(3) }),
            d => d.StartsWith("จุดความรับผิด"));
        Assert.Contains(SettlementPaidReissue.ForbiddenChanges(before, before with { DocumentDate = Day.AddMonths(1) }),
            d => d.StartsWith("วันที่เอกสาร"));
        Assert.Contains(SettlementPaidReissue.ForbiddenChanges(before, before with
            { Lines = new[] { before.Lines[0] with { VatRate = 0m }, before.Lines[1] } }), d => d.Contains("อัตรา VAT"));
        Assert.Contains(SettlementPaidReissue.ForbiddenChanges(before, before with
            { Lines = new[] { before.Lines[0] with { Quantity = 3m }, before.Lines[1] } }), d => d.Contains("จำนวน"));
        Assert.Contains(SettlementPaidReissue.ForbiddenChanges(before, before with { Lines = new[] { before.Lines[0] } }),
            d => d.StartsWith("จำนวนบรรทัด"));
        Assert.Contains(SettlementPaidReissue.ForbiddenChanges(before, before with
            { Lines = new[] { before.Lines[0] with { Description = "  " }, before.Lines[1] } }), d => d.Contains("คำบรรยายว่าง"));
        Assert.Contains(SettlementPaidReissue.ForbiddenChanges(before, before with { Type = DocumentType.Invoice }),
            d => d.StartsWith("ชนิดเอกสาร"));
    }

    /// <summary>ทั้งเส้นแบบที่ service ทำ: โคลนทุกช่องค่า → ตั้งเฉพาะช่องที่ต่าง → ด่าน "ใบใหม่เท่าใบเดิม" ต้องผ่าน · navigation ไม่ถูกลากตาม</summary>
    [Fact]
    public void R200_V1_โคลนทุกช่องค่าของเอกสารและบรรทัด_แล้วด่านเทียบใบใหม่กับใบเดิมผ่าน()
    {
        var (doc, lines) = Invoice();
        doc.Contact = new Contact { Name = "ผู้ซื้อ ก" };
        var neo = new Document();
        SettlementPaidReissue.CopyScalars(doc, neo);
        Assert.Equal(doc.Id, neo.Id);                       // ผู้เรียกต้องตั้ง Id ใหม่เอง (service ทำ)
        Assert.Equal(1070m, neo.TotalAmount);
        Assert.Equal(DocumentType.TaxInvoice, neo.DocumentType);
        Assert.Equal(Day, neo.TaxPointDate);
        Assert.True(neo.IsTaxInvoiceByLaw == true);
        Assert.Equal("00000", neo.IssuerBranchCode);
        Assert.Null(neo.Contact);                           // navigation ไม่ถูกคัดลอก
        Assert.Empty(neo.Lines);                            // คอลเลกชันไม่ถูกคัดลอก
        neo.Id = Guid.NewGuid();
        neo.ContactId = BuyerB;
        var newLines = lines.Select(l =>
        {
            var nl = new DocumentLine();
            SettlementPaidReissue.CopyScalars(l, nl);
            nl.Id = Guid.NewGuid();
            nl.DocumentId = neo.Id;
            return nl;
        }).ToList();
        newLines[1].Description = "กางเกงขายาว";
        var before = ReissueDocumentSnapshot.Of(doc, lines);
        var after = ReissueDocumentSnapshot.Of(neo, newLines);
        Assert.Empty(SettlementPaidReissue.ForbiddenChanges(before, after));
        Assert.Equal(new[] { "ผู้ซื้อ", "คำบรรยายบรรทัด 2" }, SettlementPaidReissue.AllowedChanges(before, after));
        // บรรทัดที่ถูกลบ (soft) ไม่นับในภาพย่อ
        lines[1].IsDeleted = true;
        Assert.Single(ReissueDocumentSnapshot.Of(doc, lines).Lines);
    }

    [Fact]
    public void R200_V1_หมายเหตุบนใบใหม่อ้างเลขที่วันที่และเหตุผลของใบเดิม_ไม่ซ้ำเมื่อมีอยู่แล้ว()
    {
        var note = SettlementPaidReissue.ReplacementNote("TIV-20260805-0003", Day, "ชื่อผู้ซื้อไม่ถูกต้อง");
        Assert.Contains("TIV-20260805-0003", note);
        Assert.Contains("ยกเลิกและออกฉบับใหม่แทนฉบับเดิม", note);
        Assert.Contains("ชื่อผู้ซื้อไม่ถูกต้อง", note);
        Assert.Equal(note, SettlementPaidReissue.ComposeNotes(null, "TIV-20260805-0003", Day, "ชื่อผู้ซื้อไม่ถูกต้อง"));
        Assert.Equal(note, SettlementPaidReissue.ComposeNotes("  ", "TIV-20260805-0003", Day, "ชื่อผู้ซื้อไม่ถูกต้อง"));
        var composed = SettlementPaidReissue.ComposeNotes("ขอบคุณ", "TIV-20260805-0003", Day, "ชื่อผู้ซื้อไม่ถูกต้อง");
        Assert.Equal("ขอบคุณ · " + note, composed);
        Assert.Equal(composed, SettlementPaidReissue.ComposeNotes(composed, "TIV-20260805-0003", Day, "ชื่อผู้ซื้อไม่ถูกต้อง"));
    }
}
