using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 264 P5 (team-264-a2b-design §6.3 · C-02 · C-10 · B8/B10/B12 · คำตัดสิน 264-5-1…264-5-5) — ขากลับ 50 ทวิ ระหว่างคู่ค้า NextAcc บน PostgreSQL จริง
/// (ประตูจริงทั้งสองฝั่ง: <see cref="PartnerWhtCertExchangeService"/> ของผู้จ่าย → <see cref="WhtCertIntakeService.SubmitAsync"/> ของผู้ขาย):
/// ปิด provider ทุกตัว ⇒ ใบจริงจากคู่ค้ายืนยันเอง (orchestrator ไม่ถูกเรียก · ไม่มีแถว feedback) · ยอด/เลขภาษีถูกแก้ ⇒ ไม่ยืนยัน ·
/// ผู้จ่ายยกเลิกก่อนรับ ⇒ ไม่ยืนยัน · ใบอัปโหลดยังผ่านการตรวจเดิม · ผู้ติดต่อผูกระบบอื่น/ที่อยู่ว่าง ⇒ เหตุบนใบ + แถว Autopilot · ม.82/3 ใกล้ครบ (5 เดือน แสดง · 2 เดือน ไม่แสดง)
/// <para>seed pitfalls: บริษัทใหม่ทุกเทสต์ · เลขภาษีสุ่มที่ผ่าน checksum (ด่านของกล่องรับตรวจ mod-11 · เลขคงที่ชนเทสต์อื่นในฐานร่วม)</para>
/// </summary>
[Trait("Category", "Db")]
public class WhtCertPartnerReturnRound264DbTests
{
    private readonly ITestOutputHelper _out;
    public WhtCertPartnerReturnRound264DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static readonly Random Rng = new();

    /// <summary>เลขนิติบุคคลสุ่มที่ผ่าน checksum (สูตรเดียวกับ <see cref="ThaiTaxId.HasValidChecksum"/>)</summary>
    private static string ValidTax()
    {
        string head;
        lock (Rng) head = "0" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (head[i] - '0') * (13 - i);
        var t = head + (char)('0' + (11 - sum % 11) % 10);
        Assert.True(ThaiTaxId.IsValid(t));
        return t;
    }

    /// <summary>orchestrator ทดแทน = ปิด provider ทุกตัว (ไม่มีคำตอบ · นับครั้งที่ถูกถาม)</summary>
    private sealed class KillSwitchAi
    {
        public int Calls;
        public IAiOrchestrator Proxy() => F4Proxy<IAiOrchestrator>.Create((name, _) =>
        {
            if (name != nameof(IAiOrchestrator.AskAsync)) return null;
            Calls++;
            return new AiResponse { Status = AiCallStatus.NoProvider, UsedAi = false, PrimaryAnswer = null, Confidence = null, FeedbackId = null };
        });
    }

    private static IAiFeedbackRecorder NoRecorder() => F4Proxy<IAiFeedbackRecorder>.Create((_, _) => null);

    private static WhtCertIntakeService Intake(AccountingDbContext db, IAiOrchestrator ai)
        => new(db, ai, NoRecorder(), new WhtCreditService(db, NullLogger<WhtCreditService>.Instance), NullLogger<WhtCertIntakeService>.Instance);

    private static PartnerWhtCertExchangeService Exchange(AccountingDbContext db, IAiOrchestrator ai)
        => new(db, Intake(db, ai), NullLogger<PartnerWhtCertExchangeService>.Instance);

    private sealed record Seed(Company Payer, Company Payee, Contact PayeeAtPayer, WithholdingTaxCert Cert, Guid PayeeCreditId, DateTime Since);

    /// <param name="contactSystem">ระบบที่ผู้ติดต่อผู้ถูกหักผูกไว้ (null = NextAccTenant ของผู้ขาย)</param>
    private static async Task<Seed> SeedAsync(AccountingDbContext db, string? contactSystem = null, string? address = "99 ถนนสีลม บางรัก กรุงเทพฯ 10500",
        decimal creditWht = 300m)
    {
        var payee = new Company { Name = "ผู้ขาย 264 " + Guid.NewGuid().ToString("N")[..6], TaxId = ValidTax(), BranchCode = "00000", FiscalYearStartMonth = 1 };
        var payer = new Company { Name = "ผู้จ่าย 264 " + Guid.NewGuid().ToString("N")[..6], TaxId = ValidTax(), BranchCode = "00000", FiscalYearStartMonth = 1 };
        db.Companies.AddRange(payee, payer);
        await db.SaveChangesAsync();
        var (a, b) = payee.Id.CompareTo(payer.Id) < 0 ? (payee.Id, payer.Id) : (payer.Id, payee.Id);
        db.TradingPartnerships.Add(new TradingPartnership { CompanyAId = a, CompanyBId = b, InvitedByCompanyId = payer.Id, Status = TradingPartnershipStatus.Accepted });
        var since = DateTime.UtcNow.AddDays(-5);
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig { CompanyId = payer.Id, SendWhtCertsToPartners = true, SendWhtCertsToPartnersSince = since });
        var contact = new Contact
        {
            CompanyId = payer.Id, Name = payee.Name, TaxId = payee.TaxId, BranchCode = "00000", IsSupplier = true, Address = address,
            ExternalSystem = contactSystem ?? PartnerWhtCertExchangeService.ExternalSystem,
            ExternalId = contactSystem == null ? payee.Id.ToString() : "EXT-" + Guid.NewGuid().ToString("N")[..6],
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var paid = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-2), DateTimeKind.Utc);
        var cert = new WithholdingTaxCert
        {
            CompanyId = payer.Id, CertificateNumber = "WHT264-" + Guid.NewGuid().ToString("N")[..6], PayeeContactId = contact.Id,
            TaxFormType = TaxType.WithholdingTax53, TaxYear = paid.Year, TaxMonth = paid.Month, CertificateType = WithholdingTaxCertType.Withhold,
            Status = WithholdingTaxCertStatus.Issued, TotalIncomeAmount = 10_000m, TotalTaxAmount = 300m, IssuedDate = DateTime.UtcNow.AddDays(-1),
        };
        cert.Lines.Add(new WithholdingTaxCertLine { LineOrder = 1, IncomeTypeCode = "40(2)", IncomeDescription = "ค่าบริการ", PaymentDate = paid, IncomeAmount = 10_000m, TaxRate = 3m, TaxAmount = 300m });
        db.WithholdingTaxCerts.Add(cert);
        // ฝั่งผู้ขาย: แถวรอ 50 ทวิ ของผู้จ่ายรายนี้ (ถูกหักตอนรับชำระ)
        var credit = new WhtCreditReceived
        {
            CompanyId = payee.Id, TaxYear = WhtCertIntakeVerdict.FiscalYearOf(paid, 1), PayerName = payer.Name, PayerTaxId = payer.TaxId,
            IncomeAmount = Math.Round(creditWht / 0.03m, 2, MidpointRounding.AwayFromZero), WhtRate = 3m, WhtAmount = creditWht,
            Status = WhtCreditStatus.Pending, FirstWithheldAt = paid,
        };
        db.WhtCreditsReceived.Add(credit);
        await db.SaveChangesAsync();
        return new Seed(payer, payee, contact, cert, credit.Id, since);
    }

    /// <summary>ใบช่องคู่ค้าที่ประกอบเหมือนฝั่งส่ง — ให้เทสต์แก้ทีละช่อง (จำลองข้อมูลถูกแก้ระหว่างทาง)</summary>
    private static WhtCertSubmission PartnerSub(Seed s, decimal tax = 300m, decimal income = 10_000m, string? payerTax = null, string? payeeTax = null)
        => new(WhtCertIntakeChannel.NextAccLink, WhtEvidenceKind.ElectronicCertificate, s.Cert.CertificateNumber,
            PartnerExchangeRules.WhtCertificateDate(s.Cert.Lines.Select(l => l.PaymentDate), s.Cert.IssuedDate),
            s.Payer.Name, payerTax ?? s.Payer.TaxId, "00000", null, payeeTax ?? s.Payee.TaxId, null, "40(2)", income, 3m, tax,
            WhtPayerFormType.Pnd53, null, null, null, null, null, null, "คู่ค้า NextAcc: " + s.Payer.Name, null);

    [Fact]
    public async Task R264_ปิดproviderทุกตัว_ใบจริงจากคู่ค้า_ยืนยันเองด้วยที่มา_ไม่เรียกorchestrator_เครดิตเข้า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var ai = new KillSwitchAi();
        using (var run = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Exchange(run, ai.Proxy()).DeliverForPayerAsync(s.Payer.Id, s.Since, DateTime.UtcNow));
        Assert.Equal(0, ai.Calls);   // กฎ #1 ข้อ 1/5: ไม่มีการเรียก AI ในเส้นนี้เลย

        using var read = DbTestDatabase.TryCreateContext()!;
        var cert = await read.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == s.Cert.Id && w.CompanyId == s.Payer.Id);
        Assert.NotNull(cert.PartnerDeliveredAt);
        var intake = await read.WhtCertIntakes.AsNoTracking().SingleAsync(i => i.Id == cert.PartnerIntakeId && i.CompanyId == s.Payee.Id);
        Assert.Equal(WhtCertIntakeRecordStatus.Confirmed, intake.Status);
        Assert.True(intake.AutoConfirmed);
        Assert.True(intake.AuthenticityByProvenance);
        Assert.False(intake.WhtCertAuthenticityUsedAi);
        Assert.Null(intake.WhtCertAuthenticityAiFeedbackId);   // ไม่มีแถว AiFeedback ใหม่
        var credit = await read.WhtCreditsReceived.AsNoTracking().SingleAsync(w => w.Id == s.PayeeCreditId && w.CompanyId == s.Payee.Id);
        Assert.Equal(WhtCreditStatus.Received, credit.Status);
        Assert.Equal(300m, credit.ReceivedAmount);
        Assert.Contains(await read.AuditLogs.AsNoTracking().Where(x => x.CompanyId == s.Payee.Id && x.EntityId == intake.Id.ToString()).ToListAsync(),
            x => x.NewValues != null && x.NewValues.Contains("wht-cert-intake-provenance-confirmed") && x.RowHash != null);
        // ป้ายซื่อสัตย์บนกล่องรับ
        var dto = (await Intake(read, ai.Proxy()).ListInboxAsync(s.Payee.Id, null)).Single(i => i.Id == intake.Id);
        Assert.Equal(WhtCertIntakeVerdict.ProvenanceLabel, dto.AuthenticityLabel);
    }

    [Fact]
    public async Task R264_ทิศตรงข้าม_ยอดถูกแก้_เลขผู้จ่ายถูกแก้_ไม่ยืนยัน_ไม่ถามAI_เหตุบนใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        // แถวรอใบฝั่งผู้ขาย = 290 (ตรงยอดที่ถูกแก้) ⇒ จับคู่ได้พอดี — ด่านที่กันคือ "ที่มา" ตัวเดียว
        var s = await SeedAsync(db, creditWht: 290m);
        var ai = new KillSwitchAi();
        WhtCertSubmitResult r;
        using (var run = DbTestDatabase.TryCreateContext()!)
            r = await Intake(run, ai.Proxy()).SubmitAsync(s.Payee.Id, PartnerSub(s, tax: 290m, income: Math.Round(290m / 0.03m, 2, MidpointRounding.AwayFromZero)));
        Assert.False(r.AutoConfirmed);
        Assert.Equal(0, ai.Calls);   // ใบช่องคู่ค้าที่ยอดไม่ตรงต้นทาง ห้ามให้ AI รับรอง
        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            var i = await read.WhtCertIntakes.AsNoTracking().SingleAsync(x => x.Id == r.IntakeId && x.CompanyId == s.Payee.Id);
            Assert.Equal(WhtCertIntakeRecordStatus.AwaitingConfirm, i.Status);
            Assert.False(i.AuthenticityByProvenance);
            Assert.Contains(WhtCertIntakeVerdict.ProvenanceGatePrefix, i.GateProblems);
            Assert.Contains("ยอดภาษี", i.GateProblems);
            Assert.Equal(WhtCreditStatus.Pending, (await read.WhtCreditsReceived.AsNoTracking().SingleAsync(w => w.Id == s.PayeeCreditId)).Status);
        }

        // เลขผู้จ่ายถูกแก้เป็นบริษัทที่ไม่ใช่คู่ค้า ⇒ ไม่ยืนยัน (พิสูจน์ที่มาไม่ได้)
        var s2 = await SeedAsync(db);
        var fake = ValidTax();
        using (var run = DbTestDatabase.TryCreateContext()!)
        {
            await run.WhtCreditsReceived.Where(w => w.Id == s2.PayeeCreditId).ExecuteUpdateAsync(x => x.SetProperty(w => w.PayerTaxId, fake));
            r = await Intake(run, ai.Proxy()).SubmitAsync(s2.Payee.Id, PartnerSub(s2, payerTax: fake));
        }
        Assert.False(r.AutoConfirmed);
        Assert.Equal(0, ai.Calls);
        using var read2 = DbTestDatabase.TryCreateContext()!;
        Assert.Contains("คู่ค้า", (await read2.WhtCertIntakes.AsNoTracking().SingleAsync(x => x.Id == r.IntakeId && x.CompanyId == s2.Payee.Id)).GateProblems);
    }

    [Fact]
    public async Task R264_ผู้จ่ายยกเลิกก่อนรับ_ไม่ยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        await db.WithholdingTaxCerts.Where(w => w.Id == s.Cert.Id && w.CompanyId == s.Payer.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(w => w.Status, WithholdingTaxCertStatus.Voided));
        var ai = new KillSwitchAi();
        WhtCertSubmitResult r;
        using (var run = DbTestDatabase.TryCreateContext()!)
            r = await Intake(run, ai.Proxy()).SubmitAsync(s.Payee.Id, PartnerSub(s));
        Assert.False(r.AutoConfirmed);
        using var read = DbTestDatabase.TryCreateContext()!;
        Assert.Contains("ยกเลิก", (await read.WhtCertIntakes.AsNoTracking().SingleAsync(x => x.Id == r.IntakeId && x.CompanyId == s.Payee.Id)).GateProblems);
    }

    [Fact]
    public async Task R264_ทิศตรงข้าม_ใบอัปโหลดที่ไม่ใช่คู่ค้า_ยังถามการตรวจของจริงตามเดิม_ปิดAIรอคน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var ai = new KillSwitchAi();
        var manual = PartnerSub(s) with { Channel = WhtCertIntakeChannel.Manual, EvidenceKind = WhtEvidenceKind.PaperCertificate, ExtractionConfidence = 0.95m };
        WhtCertSubmitResult r;
        using (var run = DbTestDatabase.TryCreateContext()!)
            r = await Intake(run, ai.Proxy()).SubmitAsync(s.Payee.Id, manual);
        Assert.Equal(1, ai.Calls);          // เส้นเดิม: ด่านผ่าน ⇒ ถามตรวจของจริงผ่าน orchestrator
        Assert.False(r.AutoConfirmed);      // ปิด provider ⇒ รอคน (ไม่ยืนยันด้วยที่มา แม้ข้อมูลตรงแถวในบริษัทผู้จ่าย — ช่องอัปโหลดพิสูจน์ที่มาไม่ได้)
        using var read = DbTestDatabase.TryCreateContext()!;
        Assert.False((await read.WhtCertIntakes.AsNoTracking().SingleAsync(x => x.Id == r.IntakeId && x.CompanyId == s.Payee.Id)).AuthenticityByProvenance);
    }

    [Fact]
    public async Task R264_C10_ผู้ติดต่อผูกระบบอื่น_ไม่ข้ามเงียบ_เหตุบนใบ_ไม่นับครั้ง_แถวAutopilot()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, contactSystem: "Peak");
        var ai = new KillSwitchAi();
        using (var run = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(0, await Exchange(run, ai.Proxy()).DeliverForPayerAsync(s.Payer.Id, s.Since, DateTime.UtcNow));
            Assert.Equal(0, await Exchange(run, ai.Proxy()).DeliverForPayerAsync(s.Payer.Id, s.Since, DateTime.UtcNow));   // รอบซ้ำ: ไม่นับครั้ง
        }
        using var read = DbTestDatabase.TryCreateContext()!;
        var cert = await read.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == s.Cert.Id && w.CompanyId == s.Payer.Id);
        Assert.Null(cert.PartnerDeliveredAt);
        Assert.Equal(0, cert.PartnerDeliveryAttempts);
        Assert.Contains("ผูกกับระบบ Peak", cert.PartnerDeliveryError);
        Assert.False(await read.WhtCertIntakes.AsNoTracking().AnyAsync(i => i.CompanyId == s.Payee.Id));   // ไม่มีอะไรไปถึงผู้ขาย
        var issues = await new AutopilotService(read, F4Proxy<IBankReceiptSettleService>.Create((_, _) => null)).IssuesAsync(s.Payer.Id, DateTime.UtcNow);
        Assert.Contains(issues, i => i.Kind == "PartnerWhtCertUndelivered" && i.Detail.Contains("Peak"));

        // ทางไปต่อ: ผู้ใช้ผูกผู้ติดต่อกับคู่ค้า ⇒ รอบถัดไปส่งเอง + ล้างเหตุ
        await read.Contacts.Where(c => c.Id == s.PayeeAtPayer.Id && c.CompanyId == s.Payer.Id).ExecuteUpdateAsync(x => x
            .SetProperty(c => c.ExternalSystem, PartnerWhtCertExchangeService.ExternalSystem).SetProperty(c => c.ExternalId, s.Payee.Id.ToString()));
        using (var run = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Exchange(run, ai.Proxy()).DeliverForPayerAsync(s.Payer.Id, s.Since, DateTime.UtcNow));
        using var after = DbTestDatabase.TryCreateContext()!;
        var sent = await after.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == s.Cert.Id && w.CompanyId == s.Payer.Id);
        Assert.NotNull(sent.PartnerDeliveredAt);
        Assert.Null(sent.PartnerDeliveryError);
    }

    [Fact]
    public async Task R264_B12_ที่อยู่ผู้ถูกหักว่าง_เหตุก่อนส่ง_ไม่ส่ง_ไม่เติมเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, address: null);
        var ai = new KillSwitchAi();
        using (var run = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Exchange(run, ai.Proxy()).DeliverForPayerAsync(s.Payer.Id, s.Since, DateTime.UtcNow));
        using var read = DbTestDatabase.TryCreateContext()!;
        var cert = await read.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == s.Cert.Id && w.CompanyId == s.Payer.Id);
        Assert.Null(cert.PartnerDeliveredAt);
        Assert.Contains("ที่อยู่ผู้ถูกหักว่าง", cert.PartnerDeliveryError);
        Assert.Null((await read.Contacts.AsNoTracking().SingleAsync(c => c.Id == s.PayeeAtPayer.Id && c.CompanyId == s.Payer.Id)).Address);
    }

    [Fact]
    public async Task R264_Autopilot_ม82_3_ใบจากคู่ค้ารอต้นฉบับ_ห้าเดือนแสดง_สองเดือนไม่แสดง_ยืนยันแล้วไม่แสดง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var co = new Company { Name = "ผู้ซื้อ 264 ม.82/3 " + Guid.NewGuid().ToString("N")[..6], TaxId = ValidTax(), BranchCode = "00000" };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        var vendor = new Contact { CompanyId = co.Id, Name = "ผู้ขายคู่ค้า", TaxId = ValidTax(), BranchCode = "00000", IsSupplier = true };
        db.Contacts.Add(vendor);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        Document Pi(int monthsAgo, string evidence) => new()
        {
            CompanyId = co.Id, ContactId = vendor.Id, DocumentType = DocumentType.PurchaseInvoice, DocumentNumber = $"PI264-{monthsAgo}-{evidence[..3]}-" + Guid.NewGuid().ToString("N")[..4],
            Status = DocumentStatus.Approved, DocumentDate = now.Date.AddMonths(-monthsAgo), SupplierTaxInvoiceDate = now.AddHours(7).Date.AddMonths(-monthsAgo),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB", PartnerInputVatEvidence = evidence,
        };
        var five = Pi(5, PartnerInputVatEvidence.PaperRequired);
        var two = Pi(2, PartnerInputVatEvidence.PaperRequired);
        var confirmed = Pi(5, PartnerInputVatEvidence.PaperConfirmed);
        db.Documents.AddRange(five, two, confirmed);
        await db.SaveChangesAsync();
        using var read = DbTestDatabase.TryCreateContext()!;
        var rows = (await new AutopilotService(read, F4Proxy<IBankReceiptSettleService>.Create((_, _) => null)).IssuesAsync(co.Id, now))
            .Where(i => i.Kind == "PartnerInputVatWindow").ToList();
        var row = Assert.Single(rows);
        Assert.Equal(five.Id, row.DocumentId);
        Assert.Contains("82/3", row.Title);
    }
}
