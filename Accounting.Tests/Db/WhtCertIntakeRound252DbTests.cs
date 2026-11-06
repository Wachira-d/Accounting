using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Distillation;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §9 เฟส 243 + 244a · คำตัดสินเจ้าของ §11 ข้อ 1–2 · AUDIT_249 §2 #31) — กล่องรับ 50 ทวิ บน PostgreSQL จริง:
/// ประตูเดียว (สแกน/มือ ได้แถวเดียวกัน · ซ้ำ = idempotent) · ใบรวม 1:n · ผ่อนงวด ⇒ เครดิตเฉพาะใบที่ได้ (ตัวรวมเดียวของ ภ.ง.ด.50) ·
/// K0 ใบของบริษัทอื่น = ตีกลับ ไม่ถาม AI · ปิด provider ทุกตัว (kill-switch) ⇒ รอผู้ใช้ยืนยัน ไม่ auto · ยืนยัน/ตีกลับ ⇒ ปิดลูป feedback
/// </summary>
[Trait("Category", "Db")]
public class WhtCertIntakeRound252DbTests
{
    private readonly ITestOutputHelper _out;
    public WhtCertIntakeRound252DbTests(ITestOutputHelper output) => _out = output;

    private const string Ours = "0105556123453";
    private const string Payer = "0107556000116";
    private const string OtherCo = "0105556987652";
    private static readonly DateTime CertDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-2), DateTimeKind.Utc);

    private sealed record Seed(Guid CompanyId, Guid OtherCompanyId, List<Guid> Credits, Guid OtherCredit, int Year);

    /// <summary>บริษัทเรา (เลขผู้เสียภาษีถูก checksum) + แถวรอใบของผู้จ่ายรายเดียว ตามยอดที่ให้ · บริษัทอื่นมีแถวยอดเดียวกันของผู้จ่ายเดียวกัน (ห้ามปน)</summary>
    private static async Task<Seed> SeedAsync(AccountingDbContext db, params decimal[] amounts)
    {
        var year = WhtCertIntakeVerdict.FiscalYearOf(CertDate, 1);
        var company = new Company { Name = "บริษัททดสอบกล่องรับ 50 ทวิ", TaxId = Ours, FiscalYearStartMonth = 1 };
        var other = new Company { Name = "บริษัทอื่น (ห้ามปน)", TaxId = OtherCo, FiscalYearStartMonth = 1 };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        var ids = new List<Guid>();
        var i = 0;
        foreach (var a in amounts)
        {
            var w = new WhtCreditReceived
            {
                CompanyId = company.Id, TaxYear = year, PayerName = "ลูกค้าผู้หัก จำกัด", PayerTaxId = Payer,
                IncomeAmount = Math.Round(a / 0.03m, 2, MidpointRounding.AwayFromZero), WhtRate = 3m, WhtAmount = a,
                Status = WhtCreditStatus.Pending, FirstWithheldAt = CertDate.AddDays(-(10 + i++)),
            };
            db.WhtCreditsReceived.Add(w);
            ids.Add(w.Id);
        }
        var o = new WhtCreditReceived
        {
            CompanyId = other.Id, TaxYear = year, PayerName = "ลูกค้าผู้หัก จำกัด", PayerTaxId = Payer,
            IncomeAmount = 10_000m, WhtRate = 3m, WhtAmount = amounts.FirstOrDefault(), Status = WhtCreditStatus.Pending,
        };
        db.WhtCreditsReceived.Add(o);
        await db.SaveChangesAsync();
        return new Seed(company.Id, other.Id, ids, o.Id, year);
    }

    private static WhtCertSubmission Sub(decimal tax, string? certNo, string? payee = Ours, string? sha = null,
        WhtCertIntakeChannel ch = WhtCertIntakeChannel.Manual, decimal? conf = 0.95m)
    {
        var income = Math.Round(tax / 0.03m, 2, MidpointRounding.AwayFromZero);
        return new(ch, WhtEvidenceKind.PaperCertificate, certNo, CertDate, "ลูกค้าผู้หัก จำกัด", Payer, "00000", null,
            payee, null, "40(2)", income, 3m, tax, WhtPayerFormType.Pnd53, conf, null, sha, null, null,
            "หนังสือรับรองการหักภาษี ณ ที่จ่าย ตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร", "ผู้ทดสอบ", null);
    }

    /// <summary>orchestrator ทดแทน — ตอบคำถามตรวจของจริงตามที่เทสต์กำหนด + นับจำนวนครั้งที่ถูกถาม</summary>
    private sealed class AiProbe
    {
        public int Calls;
        public IAiOrchestrator For(string? answer, decimal? confidence, bool usedAi, Guid? feedbackId = null)
            => F4Proxy<IAiOrchestrator>.Create((name, args) =>
            {
                if (name != nameof(IAiOrchestrator.AskAsync)) return null;
                Calls++;
                var req = (AiRequest)args[0]!;
                Assert.Equal(AiFeatureKey.WhtCertAuthenticity, req.FeatureKey);
                return new AiResponse
                {
                    Status = usedAi ? AiCallStatus.Success : AiCallStatus.NoProvider, PrimaryAnswer = answer, Confidence = confidence,
                    UsedAi = usedAi, FromLocalModel = !usedAi && answer != null, FeedbackId = feedbackId,
                };
            });
    }

    private sealed class RecorderProbe
    {
        public readonly List<(Guid Id, string Answer, bool Accepted)> Choices = new();
        public IAiFeedbackRecorder Create() => F4Proxy<IAiFeedbackRecorder>.Create((name, args) =>
        {
            if (name == nameof(IAiFeedbackRecorder.RecordUserChoiceAsync))
                Choices.Add(((Guid)args[0]!, (string)args[1]!, (bool)args[2]!));
            return null;
        });
    }

    private static WhtCertIntakeService Svc(AccountingDbContext db, IAiOrchestrator ai, IAiFeedbackRecorder? rec = null)
        => new(db, ai, rec ?? new RecorderProbe().Create(), new WhtCreditService(db, NullLogger<WhtCreditService>.Instance),
            NullLogger<WhtCertIntakeService>.Instance);

    private static Task<decimal> CreditableAsync(AccountingDbContext db, Guid companyId, int year)
        => db.WhtCreditsReceived.AsNoTracking()
            .CreditableInPeriod(companyId, new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc))
            .CreditableSumAsync();

    [Fact]
    public async Task ใบตรงทุกด่าน_ตรวจของจริงมั่นใจ_ยืนยันอัตโนมัติ_เครดิตเต็ม_audit_ไม่แตะบริษัทอื่น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db, 300m);
        var ai = new AiProbe();
        WhtCertSubmitResult r;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r = await Svc(req, ai.For("Genuine", 0.95m, usedAi: true, Guid.NewGuid())).SubmitAsync(s.CompanyId, Sub(300m, "2569/0456"));
        Assert.True(r.AutoConfirmed, r.Message);
        Assert.Equal(1, ai.Calls);

        using var read = DbTestDatabase.TryCreateContext()!;
        var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.Credits[0]);
        Assert.Equal(WhtCreditStatus.Received, row.Status);
        Assert.Equal(300m, row.ReceivedAmount);
        Assert.Equal("2569/0456", row.CertificateNumber);
        Assert.Equal(300m, await CreditableAsync(read, s.CompanyId, s.Year));
        var intake = await read.WhtCertIntakes.AsNoTracking().FirstAsync(i => i.Id == r.IntakeId && i.CompanyId == s.CompanyId);
        Assert.Equal(WhtCertIntakeRecordStatus.Confirmed, intake.Status);
        Assert.True(intake.WhtCertAuthenticityUsedAi);
        Assert.NotNull(intake.WhtCertAuthenticityAiFeedbackId);
        Assert.Contains(await read.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.CompanyId && a.EntityId == r.IntakeId.ToString()).ToListAsync(),
            a => a.NewValues!.Contains("RD-50BIS-INTAKE") && a.RowHash != null);
        // tenant: แถวบริษัทอื่น (ผู้จ่ายเดียวกัน ยอดเดียวกัน) ไม่ถูกแตะ
        var other = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.OtherCredit);
        Assert.Equal(WhtCreditStatus.Pending, other.Status);
        Assert.Equal(0m, other.ReceivedAmount);
    }

    [Fact]
    public async Task KillSwitch_ปิดproviderทุกตัว_นักเรียนcoldstart_รอผู้ใช้ยืนยัน_ไม่ลงเครดิต_แล้วผู้ใช้ยืนยันได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db, 300m);
        var providersOff = new List<AiProviderConfig>
        {
            new() { ProviderType = AiProviderType.DeepSeek, IsActive = false, IsEnabled = true },
            new() { ProviderType = AiProviderType.OpenAi, IsActive = false, IsEnabled = true },
        };
        // นักเรียนจริง (ตัวที่ Program.cs ลงทะเบียน) — tenant ใหม่ยังไม่มี feedback ⇒ ไม่มีคำตอบ
        var student = new GenericFeedbackDistillationModel(AiFeatureKey.WhtCertAuthenticity, new F4ServiceMap(),
            NullLogger<GenericFeedbackDistillationModel>.Instance);
        WhtCertSubmitResult r;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r = await Svc(req, new KillSwitchRig.Orchestrator(new ILocalDistillationModel[] { student }, providersOff))
                .SubmitAsync(s.CompanyId, Sub(300m, "2569/0457"));
        Assert.False(r.AutoConfirmed);
        Assert.Equal(nameof(WhtCertIntakeRecordStatus.AwaitingConfirm), r.Status);

        // นักเรียนตอบแต่ไม่มั่นใจ (0.50) ⇒ ก็ไม่ auto
        var s2 = await SeedAsync(db, 300m);
        WhtCertSubmitResult r2;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r2 = await Svc(req, new KillSwitchRig.Orchestrator(new[] { new KillSwitchRig.Student(AiFeatureKey.WhtCertAuthenticity, "Genuine") }, providersOff))
                .SubmitAsync(s2.CompanyId, Sub(300m, "2569/0458"));
        Assert.False(r2.AutoConfirmed);

        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.Credits[0]);
            Assert.Equal(WhtCreditStatus.Pending, row.Status);
            Assert.Equal(0m, row.ReceivedAmount);
            Assert.Equal(0m, await CreditableAsync(read, s.CompanyId, s.Year));
            var alloc = await read.WhtCertAllocations.AsNoTracking().SingleAsync(a => a.IntakeId == r.IntakeId && a.CompanyId == s.CompanyId);
            Assert.Equal(WhtCertAllocationStatus.Proposed, alloc.Status);
            var intake = await read.WhtCertIntakes.AsNoTracking().FirstAsync(i => i.Id == r2.IntakeId && i.CompanyId == s2.CompanyId);
            Assert.False(intake.WhtCertAuthenticityUsedAi);   // ป้ายซื่อสัตย์: นักเรียนตอบ ไม่ใช่ AI
        }

        // ผู้ใช้กดยืนยัน (ฟอร์มเติมครบ) ⇒ เครดิตได้ — feature ทำงานครบโดยไม่มี AI
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var dto = await Svc(req, new AiProbe().For(null, null, false)).ConfirmAsync(s.CompanyId, r.IntakeId, new WhtCertConfirmRequest(),
                Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true);
            Assert.Equal(nameof(WhtCertIntakeRecordStatus.Confirmed), dto.Status);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(300m, await CreditableAsync(check, s.CompanyId, s.Year));
    }

    [Fact]
    public async Task ใบรวม450_ครอบ300และ150_ได้รับทั้งสองแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, 300m, 150m, 1_000m);
        WhtCertSubmitResult r;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r = await Svc(req, new AiProbe().For("Genuine", 0.95m, usedAi: true)).SubmitAsync(s.CompanyId, Sub(450m, "2569/0500"));
        Assert.True(r.AutoConfirmed, r.Message);
        using var read = DbTestDatabase.TryCreateContext()!;
        var rows = await read.WhtCreditsReceived.AsNoTracking().Where(w => s.Credits.Contains(w.Id)).ToListAsync();
        Assert.Equal(WhtCreditStatus.Received, rows.Single(w => w.WhtAmount == 300m).Status);
        Assert.Equal(WhtCreditStatus.Received, rows.Single(w => w.WhtAmount == 150m).Status);
        Assert.Equal(WhtCreditStatus.Pending, rows.Single(w => w.WhtAmount == 1_000m).Status);   // ไม่ถูกแตะ
        Assert.Equal(2, await read.WhtCertAllocations.AsNoTracking().CountAsync(a => a.IntakeId == r.IntakeId && a.CompanyId == s.CompanyId
            && a.Status == WhtCertAllocationStatus.Confirmed));
        Assert.Equal(450m, await CreditableAsync(read, s.CompanyId, s.Year));
    }

    [Fact]
    public async Task ผ่อน3งวด_ได้ใบงวดแรก_ยืนยันแล้ว_เครดิตภงด50เฉพาะงวดแรก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, 300m);
        WhtCertSubmitResult r;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r = await Svc(req, new AiProbe().For("Genuine", 0.99m, usedAi: true)).SubmitAsync(s.CompanyId, Sub(100m, "2569/0601"));
        Assert.False(r.AutoConfirmed);   // Partial ⇒ ด่านไม่ผ่าน ⇒ ผู้ใช้ยืนยัน
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req, new AiProbe().For(null, null, false)).ConfirmAsync(s.CompanyId, r.IntakeId, new WhtCertConfirmRequest(),
                Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true);
        using var read = DbTestDatabase.TryCreateContext()!;
        var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.Credits[0]);
        Assert.Equal(WhtCreditStatus.Pending, row.Status);
        Assert.Equal(100m, row.ReceivedAmount);
        // ตัวรวมเดียวของ ภ.ง.ด.50 (TaxService) + เครื่องคำนวณ (FinancialManagementService) — เฉพาะใบที่ได้ ไม่ใช่ทั้งแถว
        Assert.Equal(100m, await CreditableAsync(read, s.CompanyId, s.Year));
        var credits = new WhtCreditService(read, NullLogger<WhtCreditService>.Instance);
        Assert.Equal(100m, await credits.CreditableAmountAsync(s.CompanyId, s.Year));
        var sum = await credits.SummaryAsync(s.CompanyId, s.Year);
        Assert.Equal(200m, sum.PendingAmount);
        Assert.Equal(100m, sum.ReceivedAmount);
    }

    [Fact]
    public async Task ไฟล์เดิมซ้ำ_idempotent_และสแกนใบเดิมหลังอัปโหลด_ไม่มีเครดิตซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, 300m, 300m);
        var sha = new string('a', 64);
        WhtCertSubmitResult first, again, scanned;
        using (var req = DbTestDatabase.TryCreateContext()!)
            first = await Svc(req, new AiProbe().For("Genuine", 0.95m, usedAi: true)).SubmitAsync(s.CompanyId, Sub(300m, "2569/0700", sha: sha));
        using (var req = DbTestDatabase.TryCreateContext()!)
            again = await Svc(req, new AiProbe().For("Genuine", 0.95m, usedAi: true)).SubmitAsync(s.CompanyId, Sub(300m, "2569/0700", sha: sha));
        // ใบเดียวกันจากหน้าสแกน (ไฟล์คนละ hash — รูปถ่าย) ⇒ เลขที่ใบ + ผู้จ่ายเดิม = แถวเดิม
        using (var req = DbTestDatabase.TryCreateContext()!)
            scanned = await Svc(req, new AiProbe().For("Genuine", 0.95m, usedAi: true))
                .SubmitAsync(s.CompanyId, Sub(300m, "2569/0700", sha: new string('b', 64), ch: WhtCertIntakeChannel.Scan));
        Assert.True(again.Duplicate);
        Assert.True(scanned.Duplicate);
        Assert.Equal(first.IntakeId, again.IntakeId);
        Assert.Equal(first.IntakeId, scanned.IntakeId);
        using var read = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await read.WhtCertIntakes.AsNoTracking().CountAsync(i => i.CompanyId == s.CompanyId));
        // สองแถวยอดเท่ากัน ⇒ เลือกแถวที่ถูกหักใกล้วันที่บนใบที่สุด (K3) — เครดิตครั้งเดียว 300 ไม่ใช่ 600 ทั้งที่ส่งมาสามครั้ง
        Assert.Equal(300m, await CreditableAsync(read, s.CompanyId, s.Year));
        Assert.Equal(1, await read.WhtCreditsReceived.AsNoTracking().CountAsync(w => w.CompanyId == s.CompanyId && w.ReceivedAmount > 0m));
    }

    [Fact]
    public async Task ใบออกในนามบริษัทอื่น_ตีกลับK0_ไม่ถามAI_ไม่มีallocation()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, 300m);
        var ai = new AiProbe();
        WhtCertSubmitResult r;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r = await Svc(req, ai.For("Genuine", 0.99m, usedAi: true)).SubmitAsync(s.CompanyId, Sub(300m, "2569/0800", payee: OtherCo));
        Assert.Equal(nameof(WhtCertIntakeRecordStatus.Rejected), r.Status);
        Assert.Equal(0, ai.Calls);   // ด่านเชิงกำหนดไม่ผ่าน ⇒ ไม่เสีย token
        using var read = DbTestDatabase.TryCreateContext()!;
        Assert.False(await read.WhtCertAllocations.AsNoTracking().AnyAsync(a => a.IntakeId == r.IntakeId && a.CompanyId == s.CompanyId));
        Assert.Equal(0m, (await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.Credits[0])).ReceivedAmount);
    }

    [Fact]
    public async Task ยืนยัน_ปิดลูปGenuine_ตีกลับเพราะไม่ใช่ของจริง_ปิดลูปSuspect_ตีกลับเหตุอื่น_ไม่สอน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, 300m, 210m, 120m);
        var fb1 = Guid.NewGuid(); var fb2 = Guid.NewGuid(); var fb3 = Guid.NewGuid();
        WhtCertSubmitResult a, b, c;
        // มั่นใจไม่พอ (0.60) ⇒ รอผู้ใช้ทั้งสามใบ
        using (var req = DbTestDatabase.TryCreateContext()!)
            a = await Svc(req, new AiProbe().For("Genuine", 0.60m, usedAi: true, fb1)).SubmitAsync(s.CompanyId, Sub(300m, "2569/0901"));
        using (var req = DbTestDatabase.TryCreateContext()!)
            b = await Svc(req, new AiProbe().For("Genuine", 0.60m, usedAi: true, fb2)).SubmitAsync(s.CompanyId, Sub(210m, "2569/0902"));
        using (var req = DbTestDatabase.TryCreateContext()!)
            c = await Svc(req, new AiProbe().For("Genuine", 0.60m, usedAi: true, fb3)).SubmitAsync(s.CompanyId, Sub(120m, "2569/0903"));
        Assert.False(a.AutoConfirmed || b.AutoConfirmed || c.AutoConfirmed);

        var rec = new RecorderProbe();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req, new AiProbe().For(null, null, false), rec.Create()).ConfirmAsync(s.CompanyId, a.IntakeId, new WhtCertConfirmRequest(),
                Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req, new AiProbe().For(null, null, false), rec.Create()).RejectAsync(s.CompanyId, b.IntakeId,
                new WhtCertRejectRequest("ตัวเลขบนใบถูกแก้ไข", NotGenuine: true), Guid.NewGuid(), "ผู้ทำบัญชี");
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req, new AiProbe().For(null, null, false), rec.Create()).RejectAsync(s.CompanyId, c.IntakeId,
                new WhtCertRejectRequest("ลูกค้าส่งใบของเดือนอื่นมา", NotGenuine: false), Guid.NewGuid(), "ผู้ทำบัญชี");

        Assert.Contains((fb1, WhtCertIntakeVerdict.Genuine, true), rec.Choices);
        Assert.Contains((fb2, WhtCertIntakeVerdict.Suspect, false), rec.Choices);
        Assert.DoesNotContain(rec.Choices, x => x.Id == fb3);   // ตีกลับเหตุอื่น ⇒ ไม่บอกอะไรเรื่องของจริง ⇒ ไม่สอน
        using var read = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(300m, await CreditableAsync(read, s.CompanyId, s.Year));   // เฉพาะใบที่ยืนยัน
        Assert.True(await read.WhtCertAllocations.AsNoTracking().Where(x => x.IntakeId == b.IntakeId && x.CompanyId == s.CompanyId)
            .AllAsync(x => x.Status == WhtCertAllocationStatus.Reversed));
        // ใบที่ตีกลับยืนยันซ้ำไม่ได้ (ทางไปต่อ: ให้ลูกค้าส่งใบใหม่)
        using var again = DbTestDatabase.TryCreateContext()!;
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(again, new AiProbe().For(null, null, false))
            .ConfirmAsync(s.CompanyId, b.IntakeId, new WhtCertConfirmRequest(), Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true));
    }

    [Fact]
    public async Task ไม่มีรายการรอใบ_ผู้ใช้เลือกบันทึกเป็นรายการใหม่_ได้เครดิตหนึ่งแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, 77m);   // ยอดไม่ตรงใบใด ๆ
        WhtCertSubmitResult r;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r = await Svc(req, new AiProbe().For("Genuine", 0.99m, usedAi: true)).SubmitAsync(s.CompanyId, Sub(900m, "2569/1001"));
        Assert.False(r.AutoConfirmed);
        // ไม่เลือกอะไร + ไม่ติ๊กรายการใหม่ ⇒ ปฏิเสธพร้อมทางไปต่อ (ไม่ silent no-op)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req, new AiProbe().For(null, null, false))
                .ConfirmAsync(s.CompanyId, r.IntakeId, new WhtCertConfirmRequest(), Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req, new AiProbe().For(null, null, false)).ConfirmAsync(s.CompanyId, r.IntakeId,
                new WhtCertConfirmRequest(CreateStandalone: true), Guid.NewGuid(), "ผู้ทำบัญชี", holdsTaxFile: true);
        using var read = DbTestDatabase.TryCreateContext()!;
        var created = await read.WhtCreditsReceived.AsNoTracking()
            .SingleAsync(w => w.CompanyId == s.CompanyId && w.CertificateNumber == "2569/1001");
        Assert.Equal(WhtCreditStatus.Received, created.Status);
        Assert.Equal(900m, created.WhtAmount);
        Assert.Equal(900m, await CreditableAsync(read, s.CompanyId, s.Year));
    }
}
