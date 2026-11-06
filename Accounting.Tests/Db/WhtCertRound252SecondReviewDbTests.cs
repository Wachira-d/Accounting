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
/// รอบ 252 ฝ่ายค้าน WP-09 รอบสอง (บน PostgreSQL จริง) — ทะเบียนเครดิต ↔ กล่องรับ 50 ทวิ:
/// H-A แก้แถวที่ได้ใบบางส่วนไม่ประกาศ "ครบ" · H-B ได้รับใบแล้ว/แก้เลขที่ใบแรก บนแถวที่ได้ใบบางส่วน = 409 (ใบถัดไปเข้ากล่องรับ) ·
/// M-A กันซ้ำสองทาง (ทะเบียน + ใบที่ยืนยันแล้ว) · M-D ปีเดียวกันเท่านั้น · M-B หมดสิทธิ์ที่ไม่ใช่ตัดสูญ ⇒ ส่วนที่ได้ใบไม่เป็นเครดิต ·
/// สรุปไม่นับซ้ำ · ปุ่มตัดสูญ/ล็อกใบแรกตัดสินที่เซิร์ฟเวอร์ · L4 วันที่ · L7 งานล้าง feedback เก่า · ทุกข้อมีทิศตรงข้าม
/// </summary>
[Trait("Category", "Db")]
public class WhtCertRound252SecondReviewDbTests
{
    private readonly ITestOutputHelper _out;
    public WhtCertRound252SecondReviewDbTests(ITestOutputHelper output) => _out = output;

    private const string Ours = "0105556123453";
    private const string Payer = "0107556000116";
    private static readonly DateTime CertDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-2), DateTimeKind.Utc);
    private static readonly int Year = WhtCertIntakeVerdict.FiscalYearOf(CertDate, 1);

    private static async Task<Guid> CompanyAsync(AccountingDbContext db)
    {
        var c = new Company { Name = "บริษัททดสอบ 50 ทวิ รอบสอง", TaxId = Ours, FiscalYearStartMonth = 1 };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static async Task<Guid> RowAsync(AccountingDbContext db, Guid companyId, decimal wht, decimal received = 0m,
        WhtCreditStatus status = WhtCreditStatus.Pending, string? certNo = null, DateTime? certDate = null)
    {
        var w = new WhtCreditReceived
        {
            CompanyId = companyId, TaxYear = Year, PayerName = "ลูกค้าผู้หัก จำกัด", PayerTaxId = Payer,
            IncomeAmount = Math.Round(wht / 0.03m, 2, MidpointRounding.AwayFromZero), WhtRate = 3m, WhtAmount = wht,
            ReceivedAmount = received, Status = status, CertificateNumber = certNo, CertificateDate = certDate, FirstWithheldAt = CertDate.AddDays(-10),
        };
        db.WhtCreditsReceived.Add(w);
        await db.SaveChangesAsync();
        return w.Id;
    }

    private static WhtCreditService Credits(AccountingDbContext db) => new(db, NullLogger<WhtCreditService>.Instance);

    private static WhtCreditService.UpsertRequest Upsert(decimal wht, string? certNo, DateTime? certDate, string? notes = null, int? year = null)
        => new(year ?? Year, certNo, certDate, null, "ลูกค้าผู้หัก จำกัด", Payer, WhtPayerFormType.Pnd53, "40(2)",
            Math.Round(wht / 0.03m, 2, MidpointRounding.AwayFromZero), 3m, wht, null, null, notes);

    private static WhtCertIntakeService Intake(AccountingDbContext db)
        => new(db, F4Proxy<IAiOrchestrator>.Create((name, args) => name == nameof(IAiOrchestrator.AskAsync)
                ? new AiResponse { Status = AiCallStatus.NoProvider, UsedAi = false }
                : null),
            F4Proxy<IAiFeedbackRecorder>.Create((_, _) => null), Credits(db), NullLogger<WhtCertIntakeService>.Instance);

    private static WhtCertSubmission Sub(decimal tax, string certNo, DateTime date)
        => new(WhtCertIntakeChannel.Manual, WhtEvidenceKind.PaperCertificate, certNo, date, "ลูกค้าผู้หัก จำกัด", Payer, "00000", null,
            Ours, null, "40(2)", Math.Round(tax / 0.03m, 2, MidpointRounding.AwayFromZero), 3m, tax, WhtPayerFormType.Pnd53, 0.95m, null, null, null, null,
            "หนังสือรับรองการหักภาษี ณ ที่จ่าย ตามมาตรา 50 ทวิ", "ผู้ทดสอบ", null);

    private static DateTime SameFyOtherDay => CertDate.Day > 1 ? CertDate.AddDays(-1) : CertDate.AddDays(1);

    [Fact]
    public async Task HA_HB_แถวได้ใบบางส่วน_แก้หมายเหตุไม่ประกาศครบ_แก้เลขที่ใบแรกหรือได้รับใบแล้ว409_ทิศตรงข้ามแถวยังไม่มีใบกรอกเลขที่ได้รับแล้ว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await CompanyAsync(db);
        var part = await RowAsync(db, cid, 300m, 100m, WhtCreditStatus.Pending, "2569/0100", CertDate);
        var fresh = await RowAsync(db, cid, 200m);
        var fresh2 = await RowAsync(db, cid, 150m);

        // H-A: แก้หมายเหตุบนแถวที่ได้ใบบางส่วน ⇒ สถานะ/ยอดที่ได้ใบเดิม (ไม่ "ครบ" เพราะมีเลขที่ใบ)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).UpdateAsync(cid, part, Upsert(300m, "2569/0100", CertDate, "โทรตามงวดสองแล้ว"));
        // วันที่ใบแรกแก้ได้ในรอบเดียวกัน
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).UpdateAsync(cid, part, Upsert(300m, "2569/0100", SameFyOtherDay, "แก้วันที่พิมพ์ผิด"));
        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == part);
            Assert.Equal(WhtCreditStatus.Pending, row.Status);
            Assert.Equal(100m, row.ReceivedAmount);
            Assert.Equal("แก้วันที่พิมพ์ผิด", row.Notes);
        }
        // H-B: เปลี่ยนเลขที่ใบแรก / วันที่ข้ามรอบ / ได้รับใบแล้ว ⇒ 409
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Credits(req).UpdateAsync(cid, part, Upsert(300m, "2569/0199", SameFyOtherDay)))).StatusCode);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Credits(req).UpdateAsync(cid, part, Upsert(300m, "2569/0100", CertDate.AddYears(-1))))).StatusCode);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Credits(req).MarkReceivedAsync(cid, part, "2569/0101", CertDate, null))).StatusCode);

        // ทิศตรงข้าม: แถวที่ยังไม่มีใบ — กรอกเลขที่ + วันที่ = ได้รับใบแล้ว · ได้รับใบแล้ว (ปุ่ม) ก็ได้
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).UpdateAsync(cid, fresh, Upsert(200m, "2569/0110", CertDate));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).MarkReceivedAsync(cid, fresh2, "2569/0111", CertDate, null);
        using var r2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(WhtCreditStatus.Received, (await r2.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == fresh)).Status);
        Assert.Equal(WhtCreditStatus.Received, (await r2.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == fresh2)).Status);
        Assert.Equal(100m, (await r2.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == part)).ReceivedAmount);
    }

    [Fact]
    public async Task MA_MD_L4_กันซ้ำสองทาง_ทะเบียนและใบยืนยันแล้ว_ปีถัดไปเลขเดียวกันได้_ไม่มีวันที่ไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await CompanyAsync(db);
        await RowAsync(db, cid, 300m, 0m, WhtCreditStatus.Received, "2569/0200", CertDate);
        var pending = await RowAsync(db, cid, 300m);
        var other = await RowAsync(db, cid, 120m);

        // ทะเบียนมีใบนี้แล้ว (ปีเดียวกัน) ⇒ สร้าง/ได้รับใบแล้ว ด้วยเลขเดียวกัน = 409 พร้อมทางไปต่อ
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Credits(req).CreateAsync(cid, Upsert(300m, "2569/0200", CertDate)));
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("ปีอื่นบันทึกได้", ex.Message);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Credits(req).MarkReceivedAsync(cid, other, "2569/0200", CertDate, null))).StatusCode);
        // M-D ทิศตรงข้าม: เลขที่เดียวกันของปีก่อน (ผู้จ่ายเริ่มนับเลขใหม่ทุกปี) ⇒ บันทึกได้
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).CreateAsync(cid, Upsert(300m, "2569/0200", CertDate.AddYears(-1), year: Year - 1));

        // ใบที่ยืนยันแล้วในกล่องรับ (ใบงวดสอง — เลขที่ไม่อยู่บนแถว) ⇒ กรอกมือซ้ำ = 409
        WhtCertSubmitResult first, second;
        using (var req = DbTestDatabase.TryCreateContext()!)
            first = await Intake(req).SubmitAsync(cid, Sub(100m, "2569/0301", CertDate));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Intake(req).ConfirmAsync(cid, first.IntakeId, new WhtCertConfirmRequest(Allocations: new[] { new WhtCertAllocationInput(pending, 100m) }),
                Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true);
        using (var req = DbTestDatabase.TryCreateContext()!)
            second = await Intake(req).SubmitAsync(cid, Sub(100m, "2569/0302", CertDate));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Intake(req).ConfirmAsync(cid, second.IntakeId, new WhtCertConfirmRequest(Allocations: new[] { new WhtCertAllocationInput(pending, 100m) }),
                Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Credits(req).CreateAsync(cid, Upsert(100m, "2569/0302", CertDate)))).StatusCode);
        // ยืนยันใบแล้วลงแถว ⇒ แก้หมายเหตุแถวนั้นไม่ชนใบของตัวเอง
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).UpdateAsync(cid, pending, Upsert(300m, "2569/0301", CertDate, "หมายเหตุใหม่"));

        // L4: มีเลขที่ไม่มีวันที่ ⇒ ปฏิเสธ (สร้าง · ได้รับใบแล้ว)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Credits(req).CreateAsync(cid, Upsert(50m, "2569/0400", null)));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Credits(req).MarkReceivedAsync(cid, other, "2569/0401", null, null));
        // ทิศตรงข้าม: ไม่มีเลขที่ (ยังรอใบ) ไม่ต้องมีวันที่
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).CreateAsync(cid, Upsert(50m, null, null));
    }

    [Fact]
    public async Task MB_คืนเงินหมดสิทธิ์_ส่วนที่ได้ใบไม่เป็นเครดิต_ทิศตรงข้ามแถวอื่นไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await CompanyAsync(db);
        var refunded = await RowAsync(db, cid, 300m, 100m, WhtCreditStatus.Pending, "2569/0500", CertDate);
        var kept = await RowAsync(db, cid, 300m, 100m, WhtCreditStatus.Pending, "2569/0501", CertDate);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).CancelAfterRefundAsync(cid, refunded, "คืนเงินเต็มจำนวนอ้างใบลดหนี้");
        using var read = DbTestDatabase.TryCreateContext()!;
        var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == refunded);
        Assert.Equal(WhtCreditStatus.Expired, row.Status);
        Assert.Equal(0m, row.ReceivedAmount);
        Assert.Equal(0m, WhtCreditableAmount.Of(row.Status, row.WhtAmount, row.ReceivedAmount));
        Assert.Equal(100m, (await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == kept)).ReceivedAmount);
    }

    [Fact]
    public async Task สรุปไม่นับซ้ำ_เศษเห็นเป็นรอใบ_ปุ่มตัดสูญและล็อกใบแรกจากเซิร์ฟเวอร์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await CompanyAsync(db);
        var partialOff = await RowAsync(db, cid, 300m, 100m, WhtCreditStatus.Expired, "2569/0600", CertDate);   // ตัดสูญส่วนขาด (ไม่มี JE ในเทสต์)
        var shortRow = await RowAsync(db, cid, 300.50m, 300m, WhtCreditStatus.Received, "2569/0601", CertDate);
        var full = await RowAsync(db, cid, 200m, 200m, WhtCreditStatus.Received, "2569/0602", CertDate);
        var legacy = await RowAsync(db, cid, 90m, 0m, WhtCreditStatus.Received, "2569/0603", CertDate);
        using var read = DbTestDatabase.TryCreateContext()!;
        var sum = await Credits(read).SummaryAsync(cid, Year);
        Assert.Equal(200m, sum.ExpiredAmount);                    // ไม่ใช่ 300 (ส่วนที่ได้ใบ 100 อยู่ใน received)
        Assert.Equal(100m + 300m + 200m + 90m, sum.ReceivedAmount);
        Assert.Equal(0.50m, sum.PendingAmount);                   // เศษของใบที่ต่ำกว่า ≤ ฿1
        var rows = (await Credits(read).ListAsync(cid, Year, null)).ToDictionary(r => r.Id);
        Assert.True(rows[shortRow].CanWriteOff);
        Assert.False(rows[full].CanWriteOff);                     // ได้ใบครบ ⇒ ไม่มีปุ่มตัดสูญ
        Assert.True(rows[legacy].CanWriteOff);                    // รุ่นเก่า (พฤติกรรมเดิม)
        Assert.False(rows[partialOff].CanWriteOff);
        Assert.NotNull(rows[full].CertificateLockReason);         // ได้ใบผ่านกล่องรับ ⇒ ล็อกใบแรก
        Assert.Null(rows[legacy].CertificateLockReason);
    }

    [Fact]
    public async Task L7_งานล้างfeedbackเก่าครั้งเดียว_เลขบุคคลหาย_นิติบุคคลคงไว้_รันซ้ำไม่ทำอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await CompanyAsync(db);
        var legacyJson = "{\"payer_key\": \"pt-dbaabaabcdefb\", \"payer_tax_id\": \"3100100123451\"}";
        var juristicJson = "{\"payer_key\": \"pt-abafffgbcdefd\", \"payer_tax_id\": \"0105556123453\"}";
        var a = new AiSuggestionFeedback { CompanyId = cid, FeatureKey = AiFeatureKey.WhtCertAuthenticity.ToString(), PromptHash = "h1", PromptJson = legacyJson };
        var b = new AiSuggestionFeedback { CompanyId = cid, FeatureKey = AiFeatureKey.WhtCertAuthenticity.ToString(), PromptHash = "h2", PromptJson = juristicJson };
        db.AiSuggestionFeedbacks.AddRange(a, b);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"OneShotDataFixes\" WHERE \"Key\" = {0}", WhtCertFeedbackPiiMigration.MarkerKey);

        using (var run = DbTestDatabase.TryCreateContext()!)
            Assert.True(await WhtCertFeedbackPiiMigration.RunAsync(run) >= 1);
        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            var ra = await read.AiSuggestionFeedbacks.AsNoTracking().FirstAsync(f => f.Id == a.Id);
            Assert.DoesNotContain("3100100123451", ra.PromptJson);
            Assert.DoesNotContain("pt-dbaabaabcdefb", ra.PromptJson);
            var rb = await read.AiSuggestionFeedbacks.AsNoTracking().FirstAsync(f => f.Id == b.Id);
            Assert.Contains("0105556123453", rb.PromptJson);
        }
        // marker ⇒ รันซ้ำไม่ทำอะไร
        using (var run = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await WhtCertFeedbackPiiMigration.RunAsync(run));
    }
}
