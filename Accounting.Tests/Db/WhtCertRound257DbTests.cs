using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 257 ชุด 2 (erp-review/2026-11-10/team-B.md · คำตัดสิน 257-2-1…257-2-4) — 50 ทวิ ขาเข้าบน PostgreSQL จริง:
/// B-H1 ได้ใบบางส่วน → ตัดสูญส่วนที่เหลือ → ใบถัดไปเข้ากล่องรับได้ (กลับ JE ตัดสูญ · ทะเบียน = GL 11910 ทุกขั้น) ·
/// B-H2 ถอนการยืนยัน (ทั้งสองทิศ: ถอนได้ / Claimed ถอนไม่ได้) · B-M1 ประทับ/ถอน Claimed ·
/// B-M2 ใบเลขเดียวกันของผู้จ่ายที่อ่านเลขไม่ได้ ไม่ถูกกลืน · B-M3 แจ้ง e-WHT รอหลักฐาน
/// </summary>
[Trait("Category", "Db")]
public class WhtCertRound257DbTests
{
    private readonly ITestOutputHelper _out;
    public WhtCertRound257DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test";

    private const string Ours = "0105556123453";
    private const string Payer = "0107556000116";
    private const string OtherPayer = "0105556987652";
    private const string Reason = "ทวงแล้ว 3 ครั้ง ลูกค้าไม่ออกใบส่วนที่เหลือ";
    private static readonly WhtCreditService.WriteOffActor Actor = new(Guid.NewGuid(), "ผู้ทำบัญชีทดสอบ 257");

    private sealed record Seed(Guid CompanyId, Guid CreditId, int TaxYear, DateTime PaidOn);

    /// <summary>บริษัท + ผัง 11910/11310/กลุ่ม 580 + JE รับชำระ Dr 11910 1,000 + แถวรอใบ 1,000 ของผู้จ่ายรายเดียว</summary>
    private static async Task<Seed> SeedAsync(AccountingDbContext db, decimal wht = 1_000m)
    {
        var paidOn = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-30), DateTimeKind.Utc);
        var company = new Company { Name = "บริษัททดสอบ 257 " + Guid.NewGuid().ToString("N")[..6], TaxId = Ours, FiscalYearStartMonth = 1 };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        ChartOfAccount A(string code, string name, AccountType t)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = t, Level = 3, IsActive = true };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        var w = A("11910", "ภาษีถูกหัก ณ ที่จ่าย", AccountType.Asset);
        var ar = A("11310", "ลูกหนี้การค้า", AccountType.Asset);
        A("580", "ภาษีเงินได้", AccountType.Expense);
        await db.SaveChangesAsync();
        var je = new JournalEntry { CompanyId = company.Id, EntryNumber = "RV-T257-0001", EntryDate = paidOn, Status = JournalEntryStatus.Posted,
            Description = "รับชำระ ลูกค้าหัก ณ ที่จ่าย", TotalDebit = wht, TotalCredit = wht };
        je.Lines.Add(new JournalEntryLine { AccountId = w.Id, DebitAmount = wht, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = ar.Id, CreditAmount = wht, LineOrder = 2 });
        db.JournalEntries.Add(je);
        var credit = new WhtCreditReceived
        {
            CompanyId = company.Id, TaxYear = paidOn.Year, PayerName = "ลูกค้าผู้หัก จำกัด", PayerTaxId = Payer,
            IncomeAmount = Math.Round(wht / 0.03m, 2, MidpointRounding.AwayFromZero), WhtRate = 3m, WhtAmount = wht,
            Status = WhtCreditStatus.Pending, FirstWithheldAt = paidOn,
        };
        db.WhtCreditsReceived.Add(credit);
        await db.SaveChangesAsync();
        return new Seed(company.Id, credit.Id, paidOn.Year, paidOn);
    }

    private static WhtCertSubmission Sub(decimal tax, string certNo, DateTime date, string payer = Payer)
        => new(WhtCertIntakeChannel.Manual, WhtEvidenceKind.PaperCertificate, certNo, date, "ลูกค้าผู้หัก จำกัด", payer, "00000", null,
            Ours, null, "40(2)", Math.Round(tax / 0.03m, 2, MidpointRounding.AwayFromZero), 3m, tax, WhtPayerFormType.Pnd53, 0.95m, null, null, null, null,
            "หนังสือรับรองการหักภาษี ณ ที่จ่าย ตามมาตรา 50 ทวิ", "ผู้ทดสอบ", null);

    private static IAiOrchestrator NoAi() => F4Proxy<IAiOrchestrator>.Create((_, _) =>
        new AiResponse { Status = AiCallStatus.NoProvider, UsedAi = false });

    private static WhtCreditService Credits(AccountingDbContext db) => new(db, NullLogger<WhtCreditService>.Instance);

    private static WhtCertIntakeService Intake(AccountingDbContext db)
        => new(db, NoAi(), F4Proxy<IAiFeedbackRecorder>.Create((_, _) => null), Credits(db), NullLogger<WhtCertIntakeService>.Instance);

    /// <summary>ส่งใบเข้ากล่องรับ แล้วยืนยันจัดสรรเข้าแถวเดียว (คนละ context ต่อคำขอ เหมือนของจริง)</summary>
    private static async Task<WhtCertIntakeDto> SubmitConfirmAsync(Seed s, decimal tax, string certNo, WhtCreditService.WriteOffActor? journal = null)
    {
        WhtCertSubmitResult r;
        using (var c = DbTestDatabase.TryCreateContext()!)
            r = await Intake(c).SubmitAsync(s.CompanyId, Sub(tax, certNo, s.PaidOn));
        Assert.False(r.Duplicate, r.Message);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        return await Intake(c2).ConfirmAsync(s.CompanyId, r.IntakeId,
            new WhtCertConfirmRequest(Allocations: new[] { new WhtCertAllocationInput(s.CreditId, tax) }), Guid.NewGuid(), "ผู้ทำบัญชี",
            holdsTaxFile: true, default, journal);
    }

    private static async Task<WhtCreditReceived> RowAsync(Guid companyId, Guid id)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await c.WhtCreditsReceived.AsNoTracking().IgnoreQueryFilters().SingleAsync(w => w.Id == id && w.CompanyId == companyId);
    }

    private static async Task<WhtCreditService.WhtCreditSummary> SummaryAsync(Seed s)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await Credits(c).SummaryAsync(s.CompanyId, s.TaxYear);
    }

    private static async Task<decimal> Gl11910Async(Guid companyId)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await Credits(c).GlWhtBalanceAsync(companyId, new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow.Date.AddDays(1));
    }

    // ═══════════ B-H1 ═══════════

    [Fact]
    public async Task H1_ได้ใบบางส่วน_ตัดสูญส่วนที่เหลือ_ใบถัดไปเข้ากล่องรับ_กลับJEตัดสูญ_ทะเบียนเท่าGL11910ทุกขั้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);

        await SubmitConfirmAsync(s, 400m, "2569/H1-1");
        Assert.Equal(400m, (await RowAsync(s.CompanyId, s.CreditId)).ReceivedAmount);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(600m, (await Credits(c).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor)).Amount);   // ตัดเฉพาะส่วนที่ขาด
        Assert.Equal(0m, (await SummaryAsync(s)).Difference);
        Assert.Equal(400m, await Gl11910Async(s.CompanyId));

        // ใบถัดไป 600 — กล่องรับเสนอแถวที่ตัดสูญแล้ว (เดิมเสนอเฉพาะ Pending = ทางตัน)
        WhtCertSubmitResult second;
        using (var c = DbTestDatabase.TryCreateContext()!)
            second = await Intake(c).SubmitAsync(s.CompanyId, Sub(600m, "2569/H1-2", s.PaidOn));
        Assert.False(second.AutoConfirmed);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var cand = Assert.Single(await Intake(c).CandidatesAsync(s.CompanyId, second.IntakeId), x => x.WhtCreditId == s.CreditId);
            Assert.True(cand.WrittenOff);
            Assert.Equal(600m, cand.Outstanding);
        }
        // ไม่มีสิทธิ์ลงบัญชี ⇒ 403 · แถวไม่ขยับ
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Intake(c).ConfirmAsync(s.CompanyId, second.IntakeId,
                new WhtCertConfirmRequest(Allocations: new[] { new WhtCertAllocationInput(s.CreditId, 600m) }), Guid.NewGuid(), "ผู้ไม่มีสิทธิ์",
                holdsTaxFile: true, default, journalActor: null));
            Assert.Equal(403, ex.StatusCode);
        }
        var still = await RowAsync(s.CompanyId, s.CreditId);
        Assert.Equal(WhtCreditStatus.Expired, still.Status);
        Assert.Null(still.WriteOffReversalJournalEntryId);

        // ผู้ลงบัญชีกดยืนยัน ⇒ กลับ JE ตัดสูญ → ลงใบ → ได้ครบ
        WhtCertIntakeDto dto;
        using (var c = DbTestDatabase.TryCreateContext()!)
            dto = await Intake(c).ConfirmAsync(s.CompanyId, second.IntakeId,
                new WhtCertConfirmRequest(Allocations: new[] { new WhtCertAllocationInput(s.CreditId, 600m) }), Guid.NewGuid(), "ผู้ทำบัญชี",
                holdsTaxFile: true, default, Actor);
        Assert.Contains("ได้ใบครบแล้ว", dto.FollowUpNote);
        var row = await RowAsync(s.CompanyId, s.CreditId);
        Assert.Equal(WhtCreditStatus.Received, row.Status);
        Assert.Equal(1_000m, row.ReceivedAmount);
        Assert.NotNull(row.WriteOffReversalJournalEntryId);
        Assert.Equal(1_000m, await Gl11910Async(s.CompanyId));
        var sum = await SummaryAsync(s);
        Assert.Equal(0m, sum.Difference);                              // ทะเบียน = GL 11910 (เดิม: รายการใหม่ ⇒ ทะเบียนเกิน GL ถาวร)
        Assert.Equal(0m, sum.WrittenOffAmount);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(1_000m, await Credits(c).CreditableAmountAsync(s.CompanyId, s.TaxYear));
            Assert.True(await c.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == s.CreditId.ToString()
                && a.NewValues!.Contains("wht-credit-writeoff-reversed-intake")));
            var listed = Assert.Single(await Credits(c).ListAsync(s.CompanyId, s.TaxYear, null));
            Assert.False(listed.WrittenOffWithJournal);
        }
    }

    [Fact]
    public async Task H1_ใบถัดไปยังไม่ครบ_เสนอตัดสูญส่วนที่เหลือ_ตัดอีกครั้งได้_ทะเบียนเท่าGL_ทิศตรงข้ามปุ่มได้รับใบแล้วซ่อน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        await SubmitConfirmAsync(s, 400m, "2569/H1P-1");
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Credits(c).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var listed = Assert.Single(await Credits(c).ListAsync(s.CompanyId, s.TaxYear, null));
            Assert.True(listed.WrittenOffWithJournal);
            Assert.False(listed.CanMarkReceived);                      // ปุ่ม "ได้รับใบแล้ว" ตาย ⇒ ไม่แสดง (เดิมแสดงแล้วถูกปฏิเสธ)
            // ทางเดิมยังปฏิเสธ 409 (ใบถัดไปเข้ากล่องรับ)
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Credits(c).MarkReceivedAsync(s.CompanyId, s.CreditId, "2569/H1P-X", s.PaidOn, null, null, Actor));
            Assert.Equal(409, ex.StatusCode);
        }

        var dto = await SubmitConfirmAsync(s, 500m, "2569/H1P-2", Actor);
        Assert.Contains("100.00", dto.FollowUpNote);                  // ยังขาด 100 ⇒ เสนอตัดสูญ
        var row = await RowAsync(s.CompanyId, s.CreditId);
        Assert.Equal(WhtCreditStatus.Pending, row.Status);
        Assert.Equal(900m, row.ReceivedAmount);
        Assert.Equal(0m, (await SummaryAsync(s)).Difference);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(100m, (await Credits(c).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor)).Amount);
        var sum = await SummaryAsync(s);
        Assert.Equal(0m, sum.Difference);
        Assert.Equal(100m, sum.WrittenOffAmount);
        Assert.Equal(900m, await Gl11910Async(s.CompanyId));
    }

    // ═══════════ B-H2 ═══════════

    [Fact]
    public async Task H2_ถอนการยืนยัน_ลดยอดได้ใบ_สถานะใหม่_ใบเป็นตีกลับ_audit_ส่งใบใหม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var first = await SubmitConfirmAsync(s, 400m, "2569/H2-1");
        var second = await SubmitConfirmAsync(s, 600m, "2569/H2-2");
        Assert.Equal(WhtCreditStatus.Received, (await RowAsync(s.CompanyId, s.CreditId)).Status);

        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Intake(c).UnconfirmAsync(s.CompanyId, second.Id, new WhtCertUnconfirmRequest("สั้น"), Guid.NewGuid(), "ผู้ทำบัญชี"));
            Assert.Equal(400, ex.StatusCode);
        }
        WhtCertIntakeDto un;
        using (var c = DbTestDatabase.TryCreateContext()!)
            un = await Intake(c).UnconfirmAsync(s.CompanyId, second.Id, new WhtCertUnconfirmRequest("ผู้จ่ายยกเลิกใบนี้แล้ว"), Guid.NewGuid(), "ผู้ทำบัญชี");
        Assert.Equal("Rejected", un.Status);
        Assert.All(un.Allocations, a => Assert.Equal("Reversed", a.Status));
        var row = await RowAsync(s.CompanyId, s.CreditId);
        Assert.Equal(WhtCreditStatus.Pending, row.Status);            // StatusAfterUnconfirm: เหลือใบแรก 400
        Assert.Equal(400m, row.ReceivedAmount);
        Assert.Equal("2569/H2-1", row.CertificateNumber);             // ใบแรกยังเป็นเลขที่ของแถว
        Assert.Equal(0m, (await SummaryAsync(s)).Difference);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(400m, await Credits(c).CreditableAmountAsync(s.CompanyId, s.TaxYear));
            Assert.True(await c.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == second.Id.ToString()
                && a.NewValues!.Contains("wht-cert-intake-unconfirmed") && a.RowHash != null));
        }
        // กดซ้ำ = ผลเดิม
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("Rejected", (await Intake(c).UnconfirmAsync(s.CompanyId, second.Id, new WhtCertUnconfirmRequest("ผู้จ่ายยกเลิกใบนี้แล้ว"),
                Guid.NewGuid(), "ผู้ทำบัญชี")).Status);
        // ถอนใบแรกด้วย ⇒ ไม่เหลือใบ ⇒ รอใบ · เลขที่ใบถูกล้าง (ส่งใบที่ถูกต้องใหม่ได้ ไม่ชนด่านกันซ้ำ)
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Intake(c).UnconfirmAsync(s.CompanyId, first.Id, new WhtCertUnconfirmRequest("ยืนยันผิดใบ"), Guid.NewGuid(), "ผู้ทำบัญชี");
        row = await RowAsync(s.CompanyId, s.CreditId);
        Assert.Equal((WhtCreditStatus.Pending, 0m, (string?)null), (row.Status, row.ReceivedAmount, row.CertificateNumber));
        var again = await SubmitConfirmAsync(s, 1_000m, "2569/H2-1");  // เลขเดิมของใบที่ถูกถอน = ใช้ได้อีก
        Assert.Equal("Confirmed", again.Status);
    }

    [Fact]
    public async Task H2_ทิศตรงข้าม_เครดิตใช้ในแบบที่ยื่นแล้ว_ถอนไม่ได้_409_ไม่มีอะไรขยับ_ปลดแบบแล้วถอนได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var dto = await SubmitConfirmAsync(s, 1_000m, "2569/H2C-1");
        var reportId = Guid.NewGuid();
        var fy = FiscalYear.RangeFor(s.TaxYear, 1);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(1, await WhtCreditClaimStamp.StampAsync(c, s.CompanyId, reportId,
                DateTime.SpecifyKind(fy.Start, DateTimeKind.Utc), DateTime.SpecifyKind(fy.EndInclusive, DateTimeKind.Utc), DateTime.UtcNow));
            await c.SaveChangesAsync();
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Intake(c).UnconfirmAsync(s.CompanyId, dto.Id, new WhtCertUnconfirmRequest("ผู้จ่ายยกเลิกใบนี้แล้ว"), Guid.NewGuid(), "ผู้ทำบัญชี"));
            Assert.Equal(409, ex.StatusCode);
        }
        var row = await RowAsync(s.CompanyId, s.CreditId);
        Assert.Equal((WhtCreditStatus.Claimed, 1_000m), (row.Status, row.ReceivedAmount));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("Confirmed", (await Intake(c).ListInboxAsync(s.CompanyId, null)).Single(i => i.Id == dto.Id).Status);

        // ปลดแบบ (กลับเป็นร่าง) ⇒ ถอนได้
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(1, await WhtCreditClaimStamp.ReleaseAsync(c, s.CompanyId, reportId, DateTime.UtcNow));
            await c.SaveChangesAsync();
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Intake(c).UnconfirmAsync(s.CompanyId, dto.Id, new WhtCertUnconfirmRequest("ผู้จ่ายยกเลิกใบนี้แล้ว"), Guid.NewGuid(), "ผู้ทำบัญชี");
        Assert.Equal(WhtCreditStatus.Pending, (await RowAsync(s.CompanyId, s.CreditId)).Status);
    }

    // ═══════════ B-M1 ═══════════

    [Fact]
    public async Task M1_ประทับ_Claimed_เฉพาะแถวได้ใบครบของรอบ_ถอนเฉพาะของรายงานนั้น_ไม่แตะบริษัทอื่น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var other = await SeedAsync(db);
        await SubmitConfirmAsync(s, 1_000m, "2569/M1-1");             // ได้ใบครบ
        await SubmitConfirmAsync(other, 1_000m, "2569/M1-O");
        var partial = await SeedAsync(db);
        await SubmitConfirmAsync(partial, 400m, "2569/M1-P");         // ได้บางส่วน ⇒ ไม่ประทับ
        var fy = FiscalYear.RangeFor(s.TaxYear, 1);
        var start = DateTime.SpecifyKind(fy.Start, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(fy.EndInclusive, DateTimeKind.Utc);
        var report = Guid.NewGuid();
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(1, await WhtCreditClaimStamp.StampAsync(c, s.CompanyId, report, start, end, DateTime.UtcNow));
            Assert.Equal(0, await WhtCreditClaimStamp.StampAsync(c, partial.CompanyId, Guid.NewGuid(), start, end, DateTime.UtcNow));
            await c.SaveChangesAsync();
        }
        var row = await RowAsync(s.CompanyId, s.CreditId);
        Assert.Equal((WhtCreditStatus.Claimed, (Guid?)report), (row.Status, row.ClaimedInTaxReportId));
        Assert.NotNull(row.ClaimedAt);
        Assert.Equal(WhtCreditStatus.Received, (await RowAsync(other.CompanyId, other.CreditId)).Status);
        Assert.Equal(WhtCreditStatus.Pending, (await RowAsync(partial.CompanyId, partial.CreditId)).Status);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var (unclaimed, claimed) = await Credits(c).CreditableSplitAsync(s.CompanyId, s.TaxYear);
            Assert.Equal((0m, 1_000m), (unclaimed, claimed));        // "ใช้เครดิตแล้ว" ไม่เป็น 0 อีก
            Assert.Equal(1_000m, await Credits(c).CreditableAmountAsync(s.CompanyId, s.TaxYear));   // เครดิตเท่าเดิม (ภ.ง.ด.50 สร้างใหม่ได้ยอดเดิม)
            Assert.Equal(0m, (await Credits(c).SummaryAsync(s.CompanyId, s.TaxYear)).Difference);
            // ด่านเดิมทำงานแล้ว: แก้/ตัดสูญแถวที่ใช้เครดิตแล้วไม่ได้
            await Assert.ThrowsAsync<BusinessRuleException>(() => Credits(c).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor));
            Assert.Equal(0, await WhtCreditClaimStamp.ReleaseAsync(c, s.CompanyId, Guid.NewGuid(), DateTime.UtcNow));   // รายงานอื่น ⇒ ไม่แตะ
            Assert.Equal(1, await WhtCreditClaimStamp.ReleaseAsync(c, s.CompanyId, report, DateTime.UtcNow));
            await c.SaveChangesAsync();
        }
        row = await RowAsync(s.CompanyId, s.CreditId);
        Assert.Equal((WhtCreditStatus.Received, (Guid?)null), (row.Status, row.ClaimedInTaxReportId));
    }

    // ═══════════ B-M2 ═══════════

    [Fact]
    public async Task M2_ใบเลขเดียวกันที่อ่านเลขผู้จ่ายไม่ได้_ไม่ถูกกลืน_ด่านอาจซ้ำ_ยืนยันต้องกรอกเลขผู้จ่าย_ทิศตรงข้ามผู้จ่ายเดียวกันยังซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        WhtCertSubmitResult a, b, dup;
        using (var c = DbTestDatabase.TryCreateContext()!)
            a = await Intake(c).SubmitAsync(s.CompanyId, Sub(300m, "001", s.PaidOn));
        using (var c = DbTestDatabase.TryCreateContext()!)
            b = await Intake(c).SubmitAsync(s.CompanyId, Sub(300m, "001", s.PaidOn, payer: ""));   // OCR อ่านเลขผู้จ่ายไม่ได้
        Assert.False(b.Duplicate);                                    // เดิม: คืนใบ a ว่า "ได้รับแล้ว" (ใบของลูกค้าอื่นหาย)
        Assert.NotEqual(a.IntakeId, b.IntakeId);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var bDto = (await Intake(c).ListInboxAsync(s.CompanyId, null)).Single(i => i.Id == b.IntakeId);
            Assert.Equal("AwaitingConfirm", bDto.Status);
            Assert.Contains(bDto.GateProblems, g => g.Contains("อาจเป็นใบซ้ำ"));
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Intake(c).ConfirmAsync(s.CompanyId, b.IntakeId,
                new WhtCertConfirmRequest(CreateStandalone: true, StandaloneReason: "รับชำระนอกระบบจากลูกค้าอีกราย"), Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true));
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("เลขผู้จ่าย", ex.Message);
        }
        // ทิศตรงข้าม: ใบเลขเดียวกันของผู้จ่ายเดียวกัน (เลขอ่านได้) ⇒ ยังเป็นใบเดิม (ไม่สร้างซ้ำ)
        using (var c = DbTestDatabase.TryCreateContext()!)
            dup = await Intake(c).SubmitAsync(s.CompanyId, Sub(300m, "001", s.PaidOn));
        Assert.True(dup.Duplicate);
        Assert.Equal(a.IntakeId, dup.IntakeId);
        // ผู้จ่ายอื่น (เลขอ่านได้ ต่างกัน) ⇒ ใบใหม่ · ฝ่ายค้าน L5: ใบ b ไม่มีเลขผู้จ่าย ⇒ แยกไม่ได้ ⇒ ยังมีด่านอาจซ้ำ
        WhtCertSubmitResult o;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            o = await Intake(c).SubmitAsync(s.CompanyId, Sub(300m, "001", s.PaidOn, payer: OtherPayer));
            Assert.False(o.Duplicate);
            var oDto = (await Intake(c).ListInboxAsync(s.CompanyId, null)).Single(i => i.Id == o.IntakeId);
            Assert.Contains(oDto.GateProblems, g => g.Contains("อาจเป็นใบซ้ำ"));
        }
        // ยืนยัน o โดยไม่ติ๊ก "ไม่ใช่ใบซ้ำ" ⇒ 409 · ติ๊กแล้ว (ใบนี้มีเลขผู้จ่ายถูกต้อง) ⇒ ไปต่อได้ (ทางไปต่อของผู้ใช้)
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Intake(c).ConfirmAsync(s.CompanyId, o.IntakeId,
                new WhtCertConfirmRequest(CreateStandalone: true, StandaloneReason: "รับชำระนอกระบบจากลูกค้าอีกราย"), Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true));
            Assert.Equal(409, ex.StatusCode);
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("Confirmed", (await Intake(c).ConfirmAsync(s.CompanyId, o.IntakeId,
                new WhtCertConfirmRequest(CreateStandalone: true, StandaloneReason: "รับชำระนอกระบบจากลูกค้าอีกราย", NotDuplicate: true),
                Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true)).Status);
    }

    // ═══════════ ฝ่ายค้าน H1 · L2 · L7 ═══════════

    private static async Task FileCitAsync(Guid companyId, int year, TaxReportStatus status = TaxReportStatus.Submitted)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        c.TaxReports.Add(new TaxReport { CompanyId = companyId, TaxType = TaxType.CorporateIncomeTax, Year = year, Month = 12, Status = status });
        await c.SaveChangesAsync();
    }

    [Fact]
    public async Task H1fix_แถวได้ใบบางส่วนในปีที่ยื่น_ภงด50_แล้ว_ถอนใบ_ตัดสูญ_แก้_ได้รับใบ_409_ทิศตรงข้ามปีที่ยังไม่ยื่นทำได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var filed = await SeedAsync(db);
        var open = await SeedAsync(db);
        var fDto = await SubmitConfirmAsync(filed, 400m, "2569/FY-1");
        var oDto = await SubmitConfirmAsync(open, 400m, "2569/FY-2");
        await FileCitAsync(filed.CompanyId, filed.TaxYear);              // ยื่นแล้ว (รอเลขรับ) ⇒ ล็อกปี · แถวยัง Pending (ไม่มี Claimed)
        Assert.Equal(WhtCreditStatus.Pending, (await RowAsync(filed.CompanyId, filed.CreditId)).Status);

        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Intake(c).UnconfirmAsync(filed.CompanyId, fDto.Id, new WhtCertUnconfirmRequest("ผู้จ่ายยกเลิกใบนี้แล้ว"), Guid.NewGuid(), "ผู้ทำบัญชี"));
            Assert.Equal((409, WhtCreditFiledYearGuard.RuleCode), (ex.StatusCode, ex.RuleCode));
            Assert.Contains("ปลดล็อก", ex.Message);
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Credits(c).ExpireAsync(filed.CompanyId, filed.CreditId, Reason, null, Actor))).StatusCode);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.NotNull((await Credits(c).WriteOffPreviewAsync(filed.CompanyId, filed.CreditId)).BlockedReason);
        var row = await RowAsync(filed.CompanyId, filed.CreditId);
        Assert.Equal((WhtCreditStatus.Pending, 400m, "2569/FY-1"), (row.Status, row.ReceivedAmount, row.CertificateNumber));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(WhtCertIntakeRecordStatus.Confirmed, (await c.WhtCertIntakes.AsNoTracking().SingleAsync(i => i.Id == fDto.Id && i.CompanyId == filed.CompanyId)).Status);

        // ทิศตรงข้าม: แถวเดียวกันแบบในบริษัทที่ยังไม่ยื่นปีนั้น ⇒ ถอนได้ แล้วตัดสูญได้
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("Rejected", (await Intake(c).UnconfirmAsync(open.CompanyId, oDto.Id, new WhtCertUnconfirmRequest("ยืนยันผิดใบ"),
                Guid.NewGuid(), "ผู้ทำบัญชี")).Status);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1_000m, (await Credits(c).ExpireAsync(open.CompanyId, open.CreditId, Reason, null, Actor)).Amount);

        // ปลดล็อก (กลับเป็นร่าง) ⇒ ปีนั้นทำได้อีก
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            await c.TaxReports.Where(t => t.CompanyId == filed.CompanyId).ExecuteUpdateAsync(x => x.SetProperty(t => t.Status, TaxReportStatus.Draft));
            Assert.Equal(600m, (await Credits(c).ExpireAsync(filed.CompanyId, filed.CreditId, Reason, null, Actor)).Amount);
        }
    }

    [Fact]
    public async Task H1fix_กลับรายการตัดสูญตอนยืนยันใบในปีที่ยื่นแล้ว_409_แถวไม่ขยับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        await SubmitConfirmAsync(s, 400m, "2569/FYR-1");
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Credits(c).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor);
        await FileCitAsync(s.CompanyId, s.TaxYear, TaxReportStatus.Filed);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => SubmitConfirmAsync(s, 600m, "2569/FYR-2", Actor));
        Assert.Equal(409, ex.StatusCode);
        var row = await RowAsync(s.CompanyId, s.CreditId);
        Assert.Equal(WhtCreditStatus.Expired, row.Status);
        Assert.Null(row.WriteOffReversalJournalEntryId);
        Assert.Equal(0m, (await SummaryAsync(s)).Difference);
    }

    [Fact]
    public async Task L2_L7_ถอนใบที่สร้างรายการใหม่_ถอดแถวตามคอลัมน์_ไฟล์กลับเข้าถัง_ชนิดหลักฐานคืน_ทิศตรงข้ามหมายเหตุอย่างเดียวไม่ถอด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        // ทิศตรงข้าม L2: แถวกรอกมือที่บังเอิญมีหมายเหตุขึ้นต้นเหมือนกัน (ไม่มี CreatedFromIntakeId) ⇒ ไม่ถูกถอด
        var manual = new WhtCreditReceived { CompanyId = s.CompanyId, TaxYear = s.TaxYear, PayerName = "ลูกค้าผู้หัก จำกัด", PayerTaxId = Payer,
            WhtAmount = 300m, IncomeAmount = 10_000m, WhtRate = 3m, Status = WhtCreditStatus.Pending, FirstWithheldAt = s.PaidOn,
            Notes = WhtCertIntakeService.StandaloneNotePrefix + " (กรอกมือ)" };
        var file = new FileAttachment { CompanyId = s.CompanyId, EntityType = "WhtCredit", EntityId = Guid.Empty, FileName = "f", OriginalFileName = "50tawi.pdf",
            ContentType = "application/pdf", FileSize = 10, StoragePath = "mem/" + Guid.NewGuid() };
        db.WhtCreditsReceived.Add(manual);
        db.FileAttachments.Add(file);
        await db.SaveChangesAsync();
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Credits(c).SetEWhtPendingAsync(s.CompanyId, manual.Id, true, null, "ผู้ทำบัญชี");   // หลักฐานเดิม = e-WHT

        // ใบที่ "บันทึกเป็นรายการใหม่" + มีไฟล์จากถัง
        WhtCertSubmitResult r;
        using (var c = DbTestDatabase.TryCreateContext()!)
            r = await Intake(c).SubmitAsync(s.CompanyId, Sub(250m, "2569/L2-1", s.PaidOn) with { FileAttachmentId = file.Id });
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Intake(c).ConfirmAsync(s.CompanyId, r.IntakeId, new WhtCertConfirmRequest(CreateStandalone: true,
                StandaloneReason: "รับชำระนอกระบบจากสาขาต่างจังหวัด"), Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true);
        Guid createdId;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var created = await c.WhtCreditsReceived.AsNoTracking().SingleAsync(w => w.CompanyId == s.CompanyId && w.CreatedFromIntakeId == r.IntakeId);
            createdId = created.Id;
            Assert.Equal(createdId, (await c.FileAttachments.AsNoTracking().SingleAsync(f => f.Id == file.Id)).EntityId);   // ผูกแล้ว
        }
        // ใบที่ลงแถวกรอกมือ (e-WHT เดิม)
        var m = await SubmitConfirmToAsync(s, manual.Id, 300m, "2569/L7-1");
        Assert.Equal(WhtEvidenceKind.PaperCertificate, (await RowAsync(s.CompanyId, manual.Id)).EvidenceKind);

        using (var c = DbTestDatabase.TryCreateContext()!)
            await Intake(c).UnconfirmAsync(s.CompanyId, r.IntakeId, new WhtCertUnconfirmRequest("ยืนยันผิดใบ"), Guid.NewGuid(), "ผู้ทำบัญชี");
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Intake(c).UnconfirmAsync(s.CompanyId, m.Id, new WhtCertUnconfirmRequest("ยืนยันผิดใบ"), Guid.NewGuid(), "ผู้ทำบัญชี");

        Assert.True((await RowAsync(s.CompanyId, createdId)).IsDeleted);                  // L2: ตามคอลัมน์
        var man = await RowAsync(s.CompanyId, manual.Id);
        Assert.False(man.IsDeleted);                                                         // ทิศตรงข้าม: หมายเหตุอย่างเดียวไม่ถอด
        Assert.Equal(WhtEvidenceKind.EWithholding, man.EvidenceKind);                       // L7: คืนชนิดหลักฐานเดิม
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(Guid.Empty, (await c.FileAttachments.AsNoTracking().SingleAsync(f => f.Id == file.Id)).EntityId);   // L7: กลับเข้าถัง
    }

    /// <summary>ส่งใบแล้วยืนยันจัดสรรเข้าแถวที่ระบุ</summary>
    private static async Task<WhtCertIntakeDto> SubmitConfirmToAsync(Seed s, Guid creditId, decimal tax, string certNo)
    {
        WhtCertSubmitResult r;
        using (var c = DbTestDatabase.TryCreateContext()!)
            r = await Intake(c).SubmitAsync(s.CompanyId, Sub(tax, certNo, s.PaidOn));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        return await Intake(c2).ConfirmAsync(s.CompanyId, r.IntakeId,
            new WhtCertConfirmRequest(Allocations: new[] { new WhtCertAllocationInput(creditId, tax) }), Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true);
    }

    // ═══════════ B-M3 ═══════════

    [Fact]
    public async Task M3_แจ้ง_eWHT_รอหลักฐาน_ป้ายไปถึง_ไม่เพิ่มเครดิต_ทิศตรงข้ามแถวได้ใบแล้ว409()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.True(Assert.Single(await Credits(c).ListAsync(s.CompanyId, s.TaxYear, null)).CanMarkEWht);
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Credits(c).SetEWhtPendingAsync(s.CompanyId, s.CreditId, true, null, "ผู้ทำบัญชี");
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var row = Assert.Single(await Credits(c).ListAsync(s.CompanyId, s.TaxYear, null));
            Assert.Equal(nameof(WhtCertIntakeStatus.State.EWhtAwaitingEvidence), row.WhtCertState);   // เดิมไปไม่ถึง
            Assert.True(row.EWhtPending);
            Assert.Equal("Pending", row.Status);
            Assert.Equal(0m, await Credits(c).CreditableAmountAsync(s.CompanyId, s.TaxYear));         // ไม่ใช่หลักฐาน — ไม่เพิ่มเครดิต
            await Credits(c).SetEWhtPendingAsync(s.CompanyId, s.CreditId, false, null, "ผู้ทำบัญชี");
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(nameof(WhtCertIntakeStatus.State.AwaitingCert), Assert.Single(await Credits(c).ListAsync(s.CompanyId, s.TaxYear, null)).WhtCertState);
        await SubmitConfirmAsync(s, 1_000m, "2569/M3-1");
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(Assert.Single(await Credits(c).ListAsync(s.CompanyId, s.TaxYear, null)).CanMarkEWht);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Credits(c).SetEWhtPendingAsync(s.CompanyId, s.CreditId, true, null, "ผู้ทำบัญชี"));
            Assert.Equal(409, ex.StatusCode);
        }
    }
}
