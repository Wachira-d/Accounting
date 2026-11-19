using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Background;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 274 — บน PostgreSQL จริง: แถวพนักงานที่เข้ารหัสด้วย<b>คีย์เดิม</b> (ผู้ใช้เปลี่ยน ENCRYPTION_KEY) ⇒
/// (1) ไฟล์ ภ.ง.ด.1 ไม่เขียนตัวเลขที่ตัดจาก ciphertext — กันแถวนั้นพร้อมเหตุ แถวอื่น 13 หลักทุกแถว ·
/// (2) รายงานอ่านอย่างเดียวชี้แถวที่ต้องแก้ (ปิดบัง) · (3) งานย้ายคีย์ (ได้คีย์เดิม) เข้ารหัสใหม่ด้วยคีย์ปัจจุบัน + audit chain ต่อแถว ·
/// แล้วไฟล์มีครบทุกคน 13 หลัก · (4) ค่าที่ถอดไม่ออกทุกคีย์ไม่ถูกแตะ (ห้ามแต่งค่า)
/// </summary>
[Trait("Category", "Db")]
public class PiiKeyRotationDbTests
{
    private readonly ITestOutputHelper _out;
    public PiiKeyRotationDbTests(ITestOutputHelper output) => _out = output;

    private const string OldKey = "r274-db-old-key-0123456789";
    private const string ValidA = "1101700001237";
    private const string ValidB = "3100500123458";
    private static readonly DateTime Period = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);

    [Fact]
    public async Task คีย์เดิม_กันแถวในไฟล์_รายงานชี้แถว_งานย้ายคีย์ซ่อม_แล้วไฟล์13หลักครบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var current = EncryptedColumnConverter.Keys.Current;

        var company = new Company { Name = "บริษัททดสอบหมุนคีย์", TaxId = "0105561012346", BranchCode = "00000" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cid = company.Id;
        var a = new Employee { CompanyId = cid, EmployeeCode = "K001", TitleTh = "นาย", FirstNameTh = "หมุน", LastNameTh = "คีย์",
            CitizenId = ValidA, StartDate = Period.AddYears(-1), Address = "1 ถนนสุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110" };
        var b = new Employee { CompanyId = cid, EmployeeCode = "K002", TitleTh = "นาง", FirstNameTh = "คีย์", LastNameTh = "เดิม",
            CitizenId = "placeholder", StartDate = Period.AddYears(-1), Address = "2 ถนนพหลโยธิน แขวงจตุจักร เขตจตุจักร กรุงเทพมหานคร 10900" };
        var lost = new Employee { CompanyId = cid, EmployeeCode = "K003", TitleTh = "นาย", FirstNameTh = "คีย์", LastNameTh = "หาย",
            CitizenId = "placeholder", StartDate = Period.AddYears(-1), Address = "3 ถนนนิมมาน ตำบลสุเทพ อำเภอเมือง เชียงใหม่ 50200" };
        db.Set<Employee>().AddRange(a, b, lost);
        var end = Period.AddMonths(1).AddDays(-1);
        var run = new PayrollRun { CompanyId = cid, PayrollNumber = "PR-R274", Name = "หมุนคีย์", Year = Period.Year, Month = Period.Month,
            PeriodStart = Period, PeriodEnd = end, PayDate = end, Status = PayrollRunFilingScope.Paid };
        foreach (var e in new[] { a, b, lost })
            run.Details.Add(new PayrollDetail { CompanyId = cid, EmployeeId = e.Id, BaseSalary = 30000m, GrossIncome = 30000m, TaxableGross = 30000m,
                WithholdingTax = 375m, NetPay = 29625m });
        PayrollDetailAmounts.RecomputeRunTotals(run);
        db.Set<PayrollRun>().Add(run);
        await db.SaveChangesAsync();

        // จำลองข้อมูลที่บันทึกก่อนเปลี่ยนคีย์ — เขียนค่าดิบข้าม converter
        var oldCipher = EncryptionHelper.Encrypt(ValidB, OldKey);
        var lostCipher = EncryptionHelper.Encrypt("1509900045673", "r274-key-nobody-has");
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Employees\" SET \"CitizenId\" = {0} WHERE \"Id\" = {1} AND \"CompanyId\" = {2}", oldCipher, b.Id, cid);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Employees\" SET \"CitizenId\" = {0} WHERE \"Id\" = {1} AND \"CompanyId\" = {2}", lostCipher, lost.Id, cid);
        // ฝ่ายค้าน MED-1: ciphertext รุ่นก่อน (ไม่มีคำนำหน้า) บนคีย์เดิม ในคอลัมน์ที่ไม่ใช่เลขผู้เสียภาษี (เลขบัญชี) — ต้องถูกย้ายและอยู่ในรายงานด้วย
        var legacyBank = EncryptionHelper.Encrypt("1234567890", OldKey)[EncryptionHelper.CipherPrefix.Length..];
        Assert.True(EncryptionHelper.LooksLikeLegacyCiphertext(legacyBank));
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Employees\" SET \"BankAccountNumber\" = {0} WHERE \"Id\" = {1} AND \"CompanyId\" = {2}", legacyBank, a.Id, cid);

        // ── (1) ก่อนซ่อม: converter (ไม่มีคีย์เดิมนี้) คืน ciphertext ⇒ ฝ่ายค้าน HIGH-1: ไม่ออกไฟล์ (ทุกรูปแบบ) พร้อมรายชื่อ/เหตุ · พรีวิวยังดูได้ ──
        using (var reqDb = DbTestDatabase.TryCreateContext()!)
        {
            var export = new TaxFilingExportService(reqDb, new TaxService(reqDb));
            foreach (var fmt in new[] { "txt", "csv", "xlsx" })
            {
                var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => export.ExportPnd1Async(cid, Period.Year, Period.Month, fmt));
                Assert.Equal(Pnd1FilingData.BlockedRuleCode, ex.RuleCode);
                Assert.Contains("K002", ex.Message);
                Assert.Contains("K003", ex.Message);
                Assert.Contains(EmployeeTaxIdentity.UndecryptableText, ex.Message);
                Assert.DoesNotContain(oldCipher, ex.Message);
            }
            await Assert.ThrowsAsync<BusinessRuleException>(() => export.ExportPnd1kAsync(cid, Period.Year, "txt"));
            // ทางแนบอัตโนมัติของรอบเงินเดือนได้ข้อความเดียวกันเข้าแบนเนอร์ (ดัง 3 ที่)
            var notice = PayrollFilingAttachmentNotice.Pnd1Missing(
                await Assert.ThrowsAsync<BusinessRuleException>(() => export.ExportPnd1Async(cid, Period.Year, Period.Month)));
            Assert.Contains("K003", notice);
            // พรีวิว: ไม่กั้น · ยอดไม่รวมแถวที่ถูกกัน · เหตุอยู่ในสรุป
            var preview = await export.ExportPnd1Async(cid, Period.Year, Period.Month, previewOnly: true);
            Assert.Contains("K002", preview.Summary);
        }

        // ── (2) รายงานอ่านอย่างเดียว: คีย์เดิมที่ยังไม่ได้ตั้ง = ถอดไม่ได้ทั้งสอง · ตั้งคีย์เดิมแล้ว = K002 รอย้ายคีย์ · K003 ยังถอดไม่ได้ ──
        using (var rdb = DbTestDatabase.TryCreateContext()!)
        {
            var before = await EmployeeIdentityHealth.BuildAsync(rdb, cid, currentKey: current, previousKeys: Array.Empty<string>());
            Assert.Equal(new[] { "K002", "K003" }, before.Rows.Where(r => r.BlocksFiling).Select(r => r.Ref).ToArray());
            Assert.All(before.Rows, r => Assert.Equal(EmployeeIdentityHealth.Issue.Undecryptable, r.Issue));
            // MED-1: คอลัมน์เลขบัญชี (ciphertext รุ่นก่อน) อยู่ในรายงานด้วย — ไม่กันการยื่นแบบ
            Assert.Contains(before.Rows, r => r.Ref == "K001" && r.Field == "เลขบัญชีธนาคาร" && !r.BlocksFiling);
            var withOld = await EmployeeIdentityHealth.BuildAsync(rdb, cid, currentKey: current, previousKeys: new[] { OldKey });
            Assert.Equal(EmployeeIdentityHealth.Issue.PendingRekey, withOld.Rows.Single(r => r.Ref == "K002").Issue);
            Assert.Equal(EmployeeIdentityHealth.Issue.PendingRekey, withOld.Rows.Single(r => r.Ref == "K001").Issue);
            Assert.Equal(EmployeeIdentityHealth.Issue.Undecryptable, withOld.Rows.Single(r => r.Ref == "K003").Issue);
            Assert.DoesNotContain(withOld.Rows, r => r.Masked.Contains(ValidB));
        }

        // ── (3) งานย้ายคีย์ ──
        using (var jdb = DbTestDatabase.TryCreateContext()!)
        {
            var r = await PiiReprotectPass.RunAsync(jdb, currentKey: current, previousKeys: new[] { OldKey }, onlyCompanyId: cid);
            Assert.Equal(2, r.Rekeyed);   // K002 เลขบัตร (enc1) + K001 เลขบัญชี (รุ่นก่อน ไม่มีคำนำหน้า)
            Assert.True(r.Undecryptable >= 1);
            // รอบสอง = idempotent
            var again = await PiiReprotectPass.RunAsync(jdb, currentKey: current, previousKeys: new[] { OldKey }, onlyCompanyId: cid);
            Assert.Equal(0, again.Rekeyed);
        }
        using (var vdb = DbTestDatabase.TryCreateContext()!)
        {
            var raw = await vdb.Database.SqlQueryRaw<string>("SELECT \"CitizenId\" AS \"Value\" FROM \"Employees\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1}", b.Id, cid).SingleAsync();
            Assert.NotEqual(oldCipher, raw);
            Assert.Equal(ValidB, EncryptionHelper.Decrypt(raw, current));            // อยู่บนคีย์ปัจจุบันแล้ว
            var rawLost = await vdb.Database.SqlQueryRaw<string>("SELECT \"CitizenId\" AS \"Value\" FROM \"Employees\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1}", lost.Id, cid).SingleAsync();
            Assert.Equal(lostCipher, rawLost);                                       // ถอดไม่ออก = ไม่แตะ (ห้ามแต่งค่า)
            Assert.True(await vdb.AuditLogs.AnyAsync(l => l.CompanyId == cid && l.EntityId == b.Id.ToString()
                && l.NewValues != null && l.NewValues.Contains("PiiReprotected")));
            Assert.False(await vdb.AuditLogs.AnyAsync(l => l.CompanyId == cid && l.NewValues != null && l.NewValues.Contains(ValidB)));   // audit ไม่มีเลขเต็ม

            var rawBank = await vdb.Database.SqlQueryRaw<string>("SELECT \"BankAccountNumber\" AS \"Value\" FROM \"Employees\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1}", a.Id, cid).SingleAsync();
            Assert.Equal("1234567890", EncryptionHelper.Decrypt(rawBank, current));

            // K002 ซ่อมแล้ว แต่ K003 ยังใช้ไม่ได้ ⇒ ยังไม่ออกไฟล์ (เหลือ K003 คนเดียวในเหตุ)
            var export = new TaxFilingExportService(vdb, new TaxService(vdb));
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => export.ExportPnd1Async(cid, Period.Year, Period.Month, "txt"));
            Assert.Contains("K003", ex.Message);
            Assert.DoesNotContain("K002", ex.Message);
        }
        // ทิศตรงข้าม: ผู้ใช้กรอกเลขบัตรของ K003 ใหม่ ⇒ ทุกแถวดี ⇒ ได้ไฟล์ครบสามคน 13 หลัก (เหมือนเดิม)
        using (var fdb = DbTestDatabase.TryCreateContext()!)
        {
            var k3 = await fdb.Set<Employee>().SingleAsync(e => e.Id == lost.Id && e.CompanyId == cid);
            k3.CitizenId = "1509900045673";
            await fdb.SaveChangesAsync();
            var export = new TaxFilingExportService(fdb, new TaxService(fdb));
            var txt = await export.ExportPnd1Async(cid, Period.Year, Period.Month, "txt");
            var ids = System.Text.Encoding.UTF8.GetString(txt.FileData).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('|')[4]).ToArray();
            Assert.Equal(new[] { ValidA, ValidB, "1509900045673" }, ids);
        }
    }
}
