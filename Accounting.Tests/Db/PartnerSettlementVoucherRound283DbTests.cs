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
            var r = await F.Docs(c, spy).IssueSettlementPaymentVoucherAsync(s.Buyer.Id, payId, s.OwnerB.ToString());
            Assert.Equal(SettlementPaymentVoucher.IssueAction.Issue, r.Action);
            Assert.NotNull(r.VoucherNumber);
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
        // ทิศตรงข้าม: การจ่ายที่ "คน" บันทึก (หมายเหตุไม่ใช่ของท่อ) ไม่ขึ้นรายงาน แม้ไม่มีใบสำคัญจ่าย
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            await c.Documents.Where(d => d.CompanyId == s.Buyer.Id && d.DocumentType == DocumentType.PaymentVoucher)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DocumentStatus.Voided));
            Assert.Single(await F.Docs(c, spy).ListPartnerPaymentsWithoutVoucherAsync(s.Buyer.Id));   // ใบเดิมยกเลิก ⇒ กลับมาขึ้น
            await c.Payments.Where(p => p.Id == payId && p.CompanyId == s.Buyer.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Notes, "บันทึกจ่ายตามที่ผู้ขายรับแล้ว (คนกด)"));
            Assert.Empty(await F.Docs(c, spy).ListPartnerPaymentsWithoutVoucherAsync(s.Buyer.Id));
        }
    }
}
