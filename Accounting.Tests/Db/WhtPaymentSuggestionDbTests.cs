using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีม A (2a · 2026-10-13) — ภาษีหัก ณ ที่จ่ายตอนบันทึกจ่ายผ่านเมธอดจริง (<c>CreatePaymentAsync</c> · endpoint แนะนำยอด · คำเตือนก่อนอนุมัติ)
/// บน PostgreSQL: ไม่มีใบลดหนี้ = ยอดเดิมทุกสตางค์ · ใบลดหนี้ซื้ออนุมัติแล้ว = แนะนำยอดสุทธิ (300 − 30 = 270) · ผู้ใช้ระบุ 300 = รับ + คำเตือน + audit ·
/// เกินเพดานใบเดิม = บล็อกเหมือนเดิม · ใบสำคัญจ่ายหักเต็มใบเดิมหลังใบลดหนี้ = คำเตือน [WHT-CN-NET]
/// <para>บริษัททดสอบไม่มีผังบัญชี ⇒ <c>CreatePaymentJournalAsync</c> ไม่ลง JE (คืนก่อนเมื่อไม่พบผังเงินสด) — เทสต์นี้ล็อก "ยอดภาษีหักของการจ่าย" ไม่ใช่ JE</para>
/// </summary>
[Trait("Category", "Db")]
public class WhtPaymentSuggestionDbTests
{
    private readonly ITestOutputHelper _out;
    public WhtPaymentSuggestionDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid SupplierId, Guid PiId);

    /// <summary>ใบแจ้งหนี้ซื้อ ค่าบริการ 10,000 หัก 3% = 300 · จ่ายผู้รับ 9,700 · (มีใบลดหนี้ = ลด 1,000 หัก 30 ⇒ ยอดค้าง 8,730 แบบที่การอนุมัติใบลดหนี้ทำ)</summary>
    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string tag, bool withCreditNote)
    {
        var company = new Company { Name = "บริษัททดสอบภาษีหักตอนจ่าย " + tag, TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var supplier = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ผู้ให้บริการ จำกัด", TaxId = "0105556000017",
            ContactType = ContactType.JuristicPerson, IsSupplier = true,
        };
        db.Contacts.Add(supplier);
        await db.SaveChangesAsync();

        var pi = new Document
        {
            CompanyId = company.Id, ContactId = supplier.Id, DocumentType = DocumentType.PurchaseInvoice, DocumentNumber = "PI-" + tag,
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date.AddDays(-5),
            SubTotal = 10_000m, WithholdingTaxAmount = 300m, TotalAmount = 9_700m,
            PaidAmount = withCreditNote ? 970m : 0m, BalanceDue = withCreditNote ? 8_730m : 9_700m,
            Currency = "THB", WhtCertAutoIssue = false,
        };
        pi.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 10_000m, Amount = 10_000m,
            WithholdingTaxRate = 3m, WithholdingTaxAmount = 300m, IncomeTypeCode = "8",
        });
        db.Documents.Add(pi);
        await db.SaveChangesAsync();

        if (withCreditNote)
        {
            var cn = new Document
            {
                CompanyId = company.Id, ContactId = supplier.Id, DocumentType = DocumentType.CreditNote, DocumentNumber = "CN-" + tag,
                Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date.AddDays(-2), RelatedDocumentId = pi.Id,
                CnDnPurchaseSideOverride = true,
                SubTotal = 1_000m, WithholdingTaxAmount = 30m, TotalAmount = 970m, Currency = "THB",
            };
            cn.Lines.Add(new DocumentLine
            {
                LineOrder = 1, Description = "ลดค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
                WithholdingTaxRate = 3m, WithholdingTaxAmount = 30m, IncomeTypeCode = "8",
            });
            db.Documents.Add(cn);
            await db.SaveChangesAsync();
        }
        return new Seed(company.Id, supplier.Id, pi.Id);
    }

    private static DocumentService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static CreatePaymentRequest Pay(Guid docId, decimal amount, decimal? wht)
        => new(docId, DateTime.UtcNow.Date, amount, PaymentMethod.BankTransfer, null, null, null, WithholdingTaxAmount: wht);

    private static Task<List<Payment>> PaymentsAsync(Accounting.Data.AccountingDbContext db, Seed s)
        => db.Payments.AsNoTracking().Where(p => p.CompanyId == s.CompanyId && p.DocumentId == s.PiId && !p.IsDeleted)
            .OrderBy(p => p.PaymentNumber).ToListAsync();

    [Fact]
    public async Task ไม่มีใบลดหนี้_ยอดแนะนำและยอดที่บันทึกเท่าเดิมทุกสตางค์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db, "NOCN-" + Guid.NewGuid().ToString("N")[..6], withCreditNote: false);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var sg = await Svc(req).GetPaymentWhtSuggestionAsync(s.CompanyId, s.PiId, 4_850m);
            Assert.Equal(150m, sg.Suggested);            // 4,850 × 300 ÷ 9,700 — สูตรเดิม
            Assert.Equal(0, sg.CreditNoteCount);
            Assert.Equal(sg.GrossCap, sg.NetCap);
        }
        PaymentResponse first;
        using (var req = DbTestDatabase.TryCreateContext()!)
            first = await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(s.PiId, 4_850m, null), "tester");
        Assert.Null(first.Warnings);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            // งวดสุดท้าย = ที่เหลือทั้งหมด (300 − 150)
            var last = await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(s.PiId, 4_850m, null), "tester");
            Assert.Null(last.Warnings);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(new[] { 150m, 150m }, (await PaymentsAsync(check, s)).Select(p => p.WithholdingTaxAmount).ToArray());
    }

    [Fact]
    public async Task ใบลดหนี้ซื้ออนุมัติแล้ว_ไม่ระบุยอด_ระบบหักยอดสุทธิ270_ไม่ใช่300()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "CN-" + Guid.NewGuid().ToString("N")[..6], withCreditNote: true);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var sg = await Svc(req).GetPaymentWhtSuggestionAsync(s.CompanyId, s.PiId, 8_730m);
            Assert.True(sg.IsFinal);
            Assert.Equal(270m, sg.Suggested);
            Assert.Equal(270m, sg.NetCap);
            Assert.Equal(300m, sg.GrossCap);
            Assert.Equal(30m, sg.CreditNoteWht);
            Assert.Contains("ใบเดิม 300.00 − ใบลดหนี้ 30.00", sg.ReasonText);
            // หน้าเว็บถามล่วงหน้าด้วยยอดที่ผู้ใช้กรอก — ข้อความมาจากตัวเดียวกับตอนบันทึก
            var asked = await Svc(req).GetPaymentWhtSuggestionAsync(s.CompanyId, s.PiId, 8_730m, entered: 300m);
            Assert.Contains("ระบบแนะนำ 270.00", asked.AboveNetWarning);
            Assert.Null(asked.AboveGrossError);
            Assert.NotNull((await Svc(req).GetPaymentWhtSuggestionAsync(s.CompanyId, s.PiId, 8_730m, entered: 300.02m)).AboveGrossError);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var paid = await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(s.PiId, 8_730m, null), "tester");
            Assert.Null(paid.Warnings);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(270m, Assert.Single(await PaymentsAsync(check, s)).WithholdingTaxAmount);
    }

    [Fact]
    public async Task รายละเอียดเอกสาร_แสดงภาษีหักหลังใบลดหนี้_ไม่มีใบลดหนี้เป็นnull()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var withCn = await SeedAsync(db, "DSP-" + Guid.NewGuid().ToString("N")[..6], withCreditNote: true);
        var noCn = await SeedAsync(db, "DSN-" + Guid.NewGuid().ToString("N")[..6], withCreditNote: false);

        using var req = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(270m, (await Svc(req).GetDocumentAsync(withCn.CompanyId, withCn.PiId)).WhtNetOfAdjustments);
        Assert.Null((await Svc(req).GetDocumentAsync(noCn.CompanyId, noCn.PiId)).WhtNetOfAdjustments);
    }

    [Fact]
    public async Task ใบลดหนี้ซื้อ_ผู้ใช้ระบุ300_รับตามผู้ใช้พร้อมคำเตือนและaudit_เกินเพดานใบเดิมยังบล็อก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "OVR-" + Guid.NewGuid().ToString("N")[..6], withCreditNote: true);

        // เกินเพดานใบเดิม (300) = บล็อกเหมือนเดิม — ยังไม่มีการจ่ายใดถูกบันทึก
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Svc(req).CreatePaymentAsync(s.CompanyId, Pay(s.PiId, 8_730m, 301m), "tester"));

        PaymentResponse paid;
        using (var req = DbTestDatabase.TryCreateContext()!)
            paid = await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(s.PiId, 8_730m, 300m), "tester");
        var warn = Assert.Single(paid.Warnings!);
        Assert.Contains("ระบบแนะนำ 270.00 (ใบเดิม 300.00 − ใบลดหนี้ 30.00 = 270.00)", warn);
        Assert.Contains("บันทึก 300.00 ตามที่ผู้ใช้ระบุ", warn);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(300m, Assert.Single(await PaymentsAsync(check, s)).WithholdingTaxAmount);   // ผู้ใช้ตัดสิน — ระบบไม่แก้ทับ
        var audit = await check.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.CompanyId && a.EntityType == "Payment" && a.EntityId == paid.Id.ToString())
            .Select(a => a.NewValues).ToListAsync();
        Assert.Contains(audit, v => v != null && v.Contains(WhtPaymentSuggestion.AboveNetRuleCode) && v.Contains("ท.ป.4/2528"));
    }

    [Fact]
    public async Task ทิศตรงข้าม_ใบลดหนี้ซื้อ_ผู้ใช้ระบุต่ำกว่ายอดแนะนำ_รับเงียบไม่มีคำเตือนไม่มีaudit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "LOW-" + Guid.NewGuid().ToString("N")[..6], withCreditNote: true);

        PaymentResponse paid;
        using (var req = DbTestDatabase.TryCreateContext()!)
            paid = await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(s.PiId, 8_730m, 250m), "tester");
        Assert.Null(paid.Warnings);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(250m, Assert.Single(await PaymentsAsync(check, s)).WithholdingTaxAmount);
        Assert.False(await check.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == s.CompanyId
            && a.NewValues != null && a.NewValues.Contains(WhtPaymentSuggestion.AboveNetRuleCode)));
    }

    [Fact]
    public async Task ใบสำคัญจ่ายอ้างใบซื้อหักเต็มใบเดิมหลังใบลดหนี้_คำเตือนก่อนอนุมัติ_หักยอดสุทธิไม่เตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "PV-" + Guid.NewGuid().ToString("N")[..6], withCreditNote: true);

        async Task<Guid> PvAsync(string no, decimal wht)
        {
            using var w = DbTestDatabase.TryCreateContext()!;
            var pv = new Document
            {
                CompanyId = s.CompanyId, ContactId = s.SupplierId, DocumentType = DocumentType.PaymentVoucher, DocumentNumber = no,
                Status = DocumentStatus.Draft, DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = s.PiId,
                SubTotal = wht / 0.03m, WithholdingTaxAmount = wht, TotalAmount = wht / 0.03m - wht, Currency = "THB",
                WhtCertAutoIssue = false,
            };
            pv.Lines.Add(new DocumentLine
            {
                LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = wht / 0.03m, Amount = wht / 0.03m,
                WithholdingTaxRate = 3m, WithholdingTaxAmount = wht, IncomeTypeCode = "8",
            });
            w.Documents.Add(pv);
            await w.SaveChangesAsync();
            return pv.Id;
        }
        var full = await PvAsync("PV-FULL-" + s.PiId.ToString("N")[..6], 300m);
        var net = await PvAsync("PV-NET-" + s.PiId.ToString("N")[..6], 270m);

        using var req = DbTestDatabase.TryCreateContext()!;
        var fullWarnings = await Svc(req).PreviewApprovalWarningsAsync(s.CompanyId, full);
        var cnNet = Assert.Single(fullWarnings, WhtPaymentSuggestion.IsApprovalWarning);
        Assert.Contains("ระบบแนะนำให้ใบนี้หัก 270.00", cnNet);
        Assert.DoesNotContain(await Svc(req).PreviewApprovalWarningsAsync(s.CompanyId, net), WhtPaymentSuggestion.IsApprovalWarning);
    }
}
