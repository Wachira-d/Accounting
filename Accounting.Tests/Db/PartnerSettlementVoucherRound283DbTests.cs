using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;
using F = Accounting.Tests.Db.PartnerFlowRound281DbTests;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 283 (คำสั่งเจ้าของ · คำตัดสิน 283-1…283-6) — ท่ออัตโนมัติคู่ค้าของผู้ซื้อ (B) ออก<b>ใบสำคัญจ่ายเป็นเอกสาร</b>ด้วย บน PostgreSQL จริง สองบริษัท ·
/// เส้น 10,700 (VAT 700 · หัก 3% = 300) ⇒ B ได้ PV 1 ใบอ้างใบซื้อ · 50 ทวิ 300 ใบเดียวผูก PV (กุญแจการจ่ายเดียวกัน) · GL ไม่ซ้ำ (เงินออก 10,400 ครั้งเดียว) ·
/// ยกเลิก PV ⇒ ถอยถูก (ยกเลิกการจ่ายเส้นเดิม) · ใบที่ท่อบันทึกก่อนรอบนี้ (ไม่มี PV) ⇒ รายงาน + ออกย้อนหลังเฉพาะแถวที่เลือก ไม่ลง JE ซ้ำ ·
/// ทิศตรงข้าม: การจ่ายที่คนบันทึกเอง (ไม่ใช่ท่อ) ไม่ขึ้นรายงาน · ออกซ้ำไม่ได้ใบที่สอง
/// </summary>
[Trait("Category", "Db")]
public class PartnerSettlementVoucherRound283DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerSettlementVoucherRound283DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    /// <summary>ใบแจ้งหนี้ 10,700 หัก 300 ⇒ ใบซื้อร่าง B ⇒ B จ่าย 10,400 ผ่านลิงก์ ⇒ ท่อของ B บันทึกจ่ายเอง — คืน (seed, ใบซื้อ, การจ่ายของ B)</summary>
    private static async Task<(F.Seed S, Guid PurchaseId, Guid BuyerPaymentId, F.Spy Spy)> FlowAsync(Accounting.Data.AccountingDbContext db)
    {
        var spy = new F.Spy();
        var s = await F.SeedAsync(db);
        var inv = await F.InvoiceAsync(db, s);
        var purchaseId = await F.RouteAndApproveAsync(s, inv, spy);
        await F.BuyerPaysThroughLinkAsync(s, inv.Id, 10_400m, 300m, spy);
        Assert.Equal(1, await F.BuyerPipelineAsync(s, spy));
        using var c = DbTestDatabase.TryCreateContext()!;
        var bp = await c.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.Buyer.Id && p.DocumentId == purchaseId);
        return (s, purchaseId, bp.Id, spy);
    }

    /// <summary>ยอดเครดิตสุทธิของบัญชีธนาคารผู้ซื้อ (11210) ใน JE ที่ลงแล้วทั้งหมด — ใบเดิมที่ถูกกลับ (Reversed) + ใบกลับรายการ (Posted) นับทั้งคู่</summary>
    private static async Task<decimal> BankNetCreditAsync(Accounting.Data.AccountingDbContext c, Guid buyerId)
    {
        var bankGl = await c.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == buyerId && a.AccountCode == "11210").Select(a => a.Id).SingleAsync();
        var lines = await c.JournalEntryLines.AsNoTracking()
            .Where(l => l.AccountId == bankGl && l.JournalEntry.CompanyId == buyerId && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed))
            .Select(l => new { l.DebitAmount, l.CreditAmount }).ToListAsync();
        return lines.Sum(l => l.CreditAmount) - lines.Sum(l => l.DebitAmount);
    }

    [Fact]
    public async Task ท่อคู่ค้า_10700หัก300_ได้ใบสำคัญจ่าย1ใบอ้างใบซื้อ_50ทวิใบเดียวผูกPV_เงินออก10400ครั้งเดียว_ยกเลิกPVถอยถูก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, purchaseId, payId, spy) = await FlowAsync(db);

        Guid pvId;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var pv = await c.Documents.AsNoTracking().Include(d => d.Lines)
                .SingleAsync(d => d.CompanyId == s.Buyer.Id && d.DocumentType == DocumentType.PaymentVoucher);
            pvId = pv.Id;
            _out.WriteLine($"PV {pv.DocumentNumber} {pv.Status} {pv.TotalAmount} · {pv.Lines.Single().Description}");
            Assert.Equal(purchaseId, pv.RelatedDocumentId);
            Assert.Equal(payId, pv.SettlementPaymentId);
            Assert.True(pv.IsSettlementReceipt);
            Assert.Equal(SettlementPaymentVoucher.PartnerOrigin, pv.OriginModule);
            Assert.Equal(SettlementPaymentVoucher.PartnerOriginLabel, SettlementPaymentVoucher.OriginLabel(pv.OriginModule));
            Assert.Equal(DocumentStatus.Paid, pv.Status);
            Assert.False(pv.DocumentNumber.StartsWith("DRAFT-"));   // ผู้ให้ความยินยอม (Owner) มีสิทธิ์อนุมัติ ⇒ เลขจริงจากเล่ม PV
            Assert.Equal(10_400m, pv.TotalAmount);
            Assert.Equal(0m, pv.VatAmount);
            Assert.Equal(0m, pv.WithholdingTaxAmount);   // ภาษีหักไม่อยู่บนใบหลักฐาน ⇒ ผู้อ่าน ภ.ง.ด./ยอดนำส่ง/ลิสต์รอออกใบไม่นับซ้ำ
            Assert.Equal(0m, pv.BalanceDue);

            // 50 ทวิ ใบเดียว 300 ของการจ่ายนี้ — ผูกกับ PV ด้วยกุญแจการจ่าย (ไม่ออกใบที่สอง) และเลขอยู่บนบรรทัดของ PV
            var cert = await c.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.CompanyId == s.Buyer.Id && w.Status != WithholdingTaxCertStatus.Voided);
            Assert.Equal(300m, cert.TotalTaxAmount);
            Assert.Equal(pv.SettlementPaymentId, cert.SourcePaymentId);
            Assert.Equal(purchaseId, cert.DocumentId);
            Assert.Contains(cert.CertificateNumber, pv.Lines.Single().Description);
            Assert.Contains("300.00", pv.Lines.Single().Description);

            // GL ไม่ซ้ำ: ใบสำคัญจ่ายไม่มี JE ของตัวเอง · เงินออกจากธนาคาร 10,400 ครั้งเดียว · ยอดหนี้ใบซื้อปิดครั้งเดียว
            Assert.False(await c.JournalEntries.AnyAsync(j => j.CompanyId == s.Buyer.Id && j.SourceDocumentId == pv.Id));
            Assert.Equal(10_400m, await BankNetCreditAsync(c, s.Buyer.Id));
            var pur = await c.Documents.AsNoTracking().SingleAsync(d => d.Id == purchaseId && d.CompanyId == s.Buyer.Id);
            Assert.Equal(0m, pur.BalanceDue);
            Assert.Equal(DocumentStatus.Paid, pur.Status);
            Assert.True(await c.AuditLogs.AnyAsync(a => a.CompanyId == s.Buyer.Id && a.EntityId == pv.Id.ToString() && a.NewValues!.Contains("SettlementPaymentVoucherIssued")));
        }
        Assert.True(spy.Has(s.Buyer.Id, Accounting.Models.Constants.NotificationEvents.PartnerPaymentAutoRecorded));

        // ฝ่ายค้าน MED-A1: ใบหลักฐานแก้ได้เฉพาะหมายเหตุ · ตัวออก 50 ทวิ ข้ามใบหลักฐานเสมอ
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var docs = F.Docs(c, spy);
            var pv = await c.Documents.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == pvId && d.CompanyId == s.Buyer.Id);
            var line = pv.Lines.Single();
            var same = new Accounting.Models.DTOs.Document.DocumentLineRequest(line.Description, line.Quantity, line.Unit, line.UnitPrice, line.DiscountPercent,
                line.VatRate, line.WithholdingTaxRate, line.AccountId);
            var notesOnly = await docs.UpdateDocumentAsync(s.Buyer.Id, pvId, new Accounting.Models.DTOs.Document.UpdateDocumentRequest(
                pv.DocumentDate, null, pv.ContactId, pv.Reference, "หมายเหตุใหม่", new() { same }));
            Assert.Equal("หมายเหตุใหม่", notesOnly.Notes);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => docs.UpdateDocumentAsync(s.Buyer.Id, pvId, new Accounting.Models.DTOs.Document.UpdateDocumentRequest(
                pv.DocumentDate, null, pv.ContactId, pv.Reference, "x", new() { same with { VatRate = 7m, WithholdingTaxRate = 3m } })));
            Assert.Equal(SettlementPaymentVoucher.EvidenceEditRuleCode, ex.RuleCode);
            var wht = await new Accounting.Services.Implementations.WithholdingTaxCertService(c, null!, null!,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<Accounting.Services.Implementations.WithholdingTaxCertService>.Instance)
                .TryAutoIssueForDocumentAsync(s.Buyer.Id, pvId, WhtCertIssueTrigger.Manual, s.OwnerB.ToString(), DateTime.UtcNow.Date);
            Assert.Equal(WhtCertIssueAction.NotApplicable, wht.Action);
            Assert.Equal(1, await c.WithholdingTaxCerts.CountAsync(w => w.CompanyId == s.Buyer.Id));
            var after = await c.Documents.AsNoTracking().SingleAsync(d => d.Id == pvId && d.CompanyId == s.Buyer.Id);
            Assert.Equal(0m, after.VatAmount);
            Assert.Equal(0m, after.WithholdingTaxAmount);

            // ฝ่ายค้าน LOW-A3: หน้ากระทบยอดธนาคาร — เงินออกนับครั้งเดียว (แถวการจ่าย) ไม่นับใบหลักฐานซ้ำ · ฝั่งผู้ขาย (ใบเสร็จหลักฐาน) เช่นกัน
            var bank = new Accounting.Services.Implementations.BankService(c);
            Assert.Equal(-10_400m, (await bank.GetReconciliationSummaryAsync(s.Buyer.Id, s.BankB)).DocumentBalance);
            Assert.Equal(10_400m, (await bank.GetReconciliationSummaryAsync(s.Seller.Id, s.BankA)).DocumentBalance);
        }

        // รันซ้ำ ⇒ ไม่มีการจ่าย/ใบที่สอง
        Assert.Equal(0, await F.BuyerPipelineAsync(s, spy));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await c.Documents.CountAsync(d => d.CompanyId == s.Buyer.Id && d.DocumentType == DocumentType.PaymentVoucher));

        // ยกเลิกใบสำคัญจ่าย ⇒ ยกเลิกการจ่าย (เส้นเดิม): JE กลับ · ใบซื้อกลับค้าง 10,400 · PV ยกเลิกแต่ยังเห็น (เลขไม่หาย) · 50 ทวิ ที่ออกแล้วติดป้ายให้คนตัดสิน
        using (var c = DbTestDatabase.TryCreateContext()!)
            await F.Docs(c, spy).VoidDocumentAsync(s.Buyer.Id, pvId);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var pv = await c.Documents.AsNoTracking().IgnoreQueryFilters().SingleAsync(d => d.Id == pvId && d.CompanyId == s.Buyer.Id);
            Assert.Equal(DocumentStatus.Voided, pv.Status);
            Assert.False(pv.IsDeleted);
            var pay = await c.Payments.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.Id == payId && p.CompanyId == s.Buyer.Id);
            Assert.True(pay.IsDeleted);
            var pur = await c.Documents.AsNoTracking().SingleAsync(d => d.Id == purchaseId && d.CompanyId == s.Buyer.Id);
            Assert.Equal(10_400m, pur.BalanceDue);
            Assert.Equal(DocumentStatus.Approved, pur.Status);
            Assert.Equal(0m, await BankNetCreditAsync(c, s.Buyer.Id));
            Assert.Equal(1, await c.WithholdingTaxCerts.CountAsync(w => w.CompanyId == s.Buyer.Id));   // ไม่มีใบใหม่จากการยกเลิก
            Assert.True(await c.AuditLogs.AnyAsync(a => a.CompanyId == s.Buyer.Id && a.EntityId == pvId.ToString() && a.NewValues!.Contains("SettlementPaymentVoucherVoidedWithPayment")));
        }
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.False(AuditHashChain.Analyze(await chk.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.Buyer.Id && a.RowHash != null).OrderBy(a => a.Id).ToListAsync())
            .HasIntegrityFindings);
    }

    /// <summary>ใบแจ้งหนี้ → ใบซื้อร่าง → ผู้ซื้อจ่ายผ่านลิงก์ของผู้ขาย · ผู้ซื้อ<b>ไม่มีความยินยอม</b> ⇒ ท่อไม่บันทึก (คนต้องกด)</summary>
    private static async Task<(F.Seed S, Guid PurchaseId, F.Spy Spy)> HumanFlowAsync(Accounting.Data.AccountingDbContext db)
    {
        var spy = new F.Spy();
        var s = await F.SeedAsync(db, consent: false);
        var inv = await F.InvoiceAsync(db, s);
        var purchaseId = await F.RouteAndApproveAsync(s, inv, spy);
        await F.BuyerPaysThroughLinkAsync(s, inv.Id, 10_400m, 300m, spy);
        Assert.Equal(0, await F.BuyerPipelineAsync(s, spy));
        return (s, purchaseId, spy);
    }

    private static async Task AssertOnePvAsync(F.Seed s, Guid purchaseId)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var bp = await c.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.Buyer.Id && p.DocumentId == purchaseId);
        var pv = await c.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == s.Buyer.Id && d.DocumentType == DocumentType.PaymentVoucher);
        Assert.Equal(bp.Id, pv.SettlementPaymentId);
        Assert.Equal(purchaseId, pv.RelatedDocumentId);
        Assert.Equal(0m, pv.WithholdingTaxAmount);
        Assert.False(await c.JournalEntries.AnyAsync(j => j.CompanyId == s.Buyer.Id && j.SourceDocumentId == pv.Id));
        Assert.Equal(10_400m, await BankNetCreditAsync(c, s.Buyer.Id));
        Assert.Equal(1, await c.WithholdingTaxCerts.CountAsync(w => w.CompanyId == s.Buyer.Id && w.Status != WithholdingTaxCertStatus.Voided));
    }

    [Fact]
    public async Task ปุ่มเดียว_บันทึกจ่ายตามที่ผู้ขายรับแล้ว_ได้ใบสำคัญจ่ายแบบเดียวกับท่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, purchaseId, spy) = await HumanFlowAsync(db);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var notice = await c.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters().SingleAsync(n => n.CompanyId == s.Buyer.Id && n.TargetDocumentId == purchaseId);
            var svc = new Accounting.Services.Implementations.PartnerPaymentNoticeService(c, F.Docs(c, spy),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Accounting.Services.Implementations.PartnerPaymentNoticeService>.Instance, spy);
            await svc.RecordAsync(s.Buyer.Id, notice.Id, s.OwnerB, s.BankB, null);
        }
        await AssertOnePvAsync(s, purchaseId);
    }

    [Fact]
    public async Task บันทึกชำระมือบนใบซื้อจากคู่ค้า_ทางเดียวกับแจ้งโอนP4_ได้ใบสำคัญจ่าย_ใบซื้อทั่วไปไม่มี()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, purchaseId, spy) = await HumanFlowAsync(db);
        // ทางมือ/แจ้งโอน P4: CreatePaymentAsync(Manual) ไม่ส่งคำขอ PV — ตัวตัดสินเดียวเห็นว่าใบมาจากคู่ค้า ⇒ ออก
        using (var c = DbTestDatabase.TryCreateContext()!)
            await F.Docs(c, spy).CreatePaymentAsync(s.Buyer.Id, new Accounting.Models.DTOs.Document.CreatePaymentRequest(purchaseId, DateTime.UtcNow.Date, 10_400m,
                PaymentMethod.BankTransfer, "MANUAL-283", null, null, WithholdingTaxAmount: 300m, OverrideBankAccountId: s.BankB), s.OwnerB.ToString(), PaymentEntrySource.Manual);
        await AssertOnePvAsync(s, purchaseId);

        // ทิศตรงข้าม: ใบซื้อทั่วไป (ไม่ได้มาจากคู่ค้า) — การจ่ายเส้นเดียวกันไม่มีใบสำคัญจ่าย (พฤติกรรมเดิม) · ข้อความขั้นต่อไปบอกตรงตามนั้น
        var spy2 = new F.Spy();
        var s2 = await F.SeedAsync(db, consent: false);
        var inv2 = await F.InvoiceAsync(db, s2);
        var pur2 = await F.RouteAndApproveAsync(s2, inv2, spy2);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            await c.Documents.Where(d => d.Id == pur2 && d.CompanyId == s2.Buyer.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.PartnerSourceLinkId, (Guid?)null));
            await F.Docs(c, spy2).CreatePaymentAsync(s2.Buyer.Id, new Accounting.Models.DTOs.Document.CreatePaymentRequest(pur2, DateTime.UtcNow.Date, 10_400m,
                PaymentMethod.BankTransfer, "MANUAL-283B", null, null, WithholdingTaxAmount: 300m, OverrideBankAccountId: s2.BankB), s2.OwnerB.ToString(), PaymentEntrySource.Manual);
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.True(await c.Payments.AnyAsync(p => p.CompanyId == s2.Buyer.Id && p.DocumentId == pur2));
            Assert.False(await c.Documents.AnyAsync(d => d.CompanyId == s2.Buyer.Id && d.DocumentType == DocumentType.PaymentVoucher));
            var view = await F.Docs(c, spy2).GetDocumentAsync(s2.Buyer.Id, pur2);
            Assert.Contains("ไม่ออกใบสำคัญจ่ายแยก", view.PayNextStepHint);
            var view1 = await F.Docs(c, spy).GetDocumentAsync(s.Buyer.Id, purchaseId);
            Assert.Contains("ออกใบสำคัญจ่าย (หลักฐานคู่การจ่าย", view1.PayNextStepHint);
        }
    }

    [Fact]
    public async Task ใบที่ท่อบันทึกก่อนรอบ283_ไม่มีPV_รายงานเห็น_ออกย้อนหลังไม่ลงJEซ้ำ_ไม่ออก50ทวิใหม่_กดซ้ำไม่ได้ใบที่สอง_การจ่ายมือไม่ขึ้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, purchaseId, payId, spy) = await FlowAsync(db);
        // จำลองข้อมูลก่อนรอบ 283: การจ่ายของท่อมีอยู่ แต่ไม่มีใบสำคัญจ่าย
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var pvIds = await c.Documents.Where(d => d.CompanyId == s.Buyer.Id && d.DocumentType == DocumentType.PaymentVoucher).Select(d => d.Id).ToListAsync();
            await c.DocumentLines.Where(l => pvIds.Contains(l.DocumentId)).ExecuteDeleteAsync();
            await c.Documents.Where(d => pvIds.Contains(d.Id) && d.CompanyId == s.Buyer.Id).ExecuteDeleteAsync();
        }
        int jeBefore; int certsBefore;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            jeBefore = await c.JournalEntries.CountAsync(j => j.CompanyId == s.Buyer.Id);
            certsBefore = await c.WithholdingTaxCerts.CountAsync(w => w.CompanyId == s.Buyer.Id);
            var rows = await F.Docs(c, spy).ListPartnerPaymentsWithoutVoucherAsync(s.Buyer.Id);
            var row = Assert.Single(rows);
            Assert.Equal(payId, row.PaymentId);
            Assert.Equal(10_400m, row.Amount);
            Assert.Equal(300m, row.WithholdingTaxAmount);
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            c.DocumentDraftOnlyRequest = true;   // ฝ่ายค้าน MED-A2: คำขอ "เฉพาะร่าง" (รอบ 276) ⇒ ไม่ออกเลข เป็นร่างรออนุมัติ
            var r = await F.Docs(c, spy).IssueSettlementPaymentVoucherAsync(s.Buyer.Id, payId, s.OwnerB.ToString());
            Assert.Equal(SettlementPaymentVoucher.IssueAction.Issue, r.Action);
            Assert.StartsWith("DRAFT-", r.VoucherNumber);
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(jeBefore, await c.JournalEntries.CountAsync(j => j.CompanyId == s.Buyer.Id));   // ไม่ลง JE ซ้ำ
            Assert.Equal(certsBefore, await c.WithholdingTaxCerts.CountAsync(w => w.CompanyId == s.Buyer.Id));   // ไม่ออก 50 ทวิ ใหม่
            Assert.Equal(10_400m, await BankNetCreditAsync(c, s.Buyer.Id));
            var pv = await c.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == s.Buyer.Id && d.DocumentType == DocumentType.PaymentVoucher);
            Assert.Equal(payId, pv.SettlementPaymentId);
            Assert.Contains("ออกย้อนหลัง", pv.InternalNotes);
            Assert.True(await c.AuditLogs.AnyAsync(a => a.CompanyId == s.Buyer.Id && a.EntityId == pv.Id.ToString() && a.NewValues!.Contains("SettlementPaymentVoucherBackfilled")));
            Assert.Empty(await F.Docs(c, spy).ListPartnerPaymentsWithoutVoucherAsync(s.Buyer.Id));
            var again = await F.Docs(c, spy).IssueSettlementPaymentVoucherAsync(s.Buyer.Id, payId, s.OwnerB.ToString());
            Assert.Equal(SettlementPaymentVoucher.IssueAction.AlreadyIssued, again.Action);
            Assert.Equal(pv.Id, again.VoucherId);
            Assert.Equal(1, await c.Documents.CountAsync(d => d.CompanyId == s.Buyer.Id && d.DocumentType == DocumentType.PaymentVoucher));
        }
        // ทิศตรงข้าม: การจ่ายของใบซื้อทั่วไป (ไม่ได้มาจากคู่ค้า) ไม่ขึ้นรายงาน แม้ไม่มีใบสำคัญจ่าย
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            await c.Documents.Where(d => d.CompanyId == s.Buyer.Id && d.DocumentType == DocumentType.PaymentVoucher)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DocumentStatus.Voided));
            Assert.Single(await F.Docs(c, spy).ListPartnerPaymentsWithoutVoucherAsync(s.Buyer.Id));   // ใบเดิมยกเลิก ⇒ กลับมาขึ้น
            await c.Documents.Where(d => d.Id == purchaseId && d.CompanyId == s.Buyer.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.PartnerSourceLinkId, (Guid?)null));
            Assert.Empty(await F.Docs(c, spy).ListPartnerPaymentsWithoutVoucherAsync(s.Buyer.Id));
        }
    }
}
