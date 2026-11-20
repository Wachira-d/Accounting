using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Background;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 274-10 — บน PostgreSQL จริง: เลขบัตรที่ถูก<b>เข้ารหัสซ้อน</b> (ชั้นใน dev key · ชั้นนอกคีย์ปัจจุบัน) ⇒ EF อ่านได้เลขจริง 13 หลัก ·
/// รายงานไม่บอกให้กรอกใหม่ · งานย้ายคีย์เขียนใหม่เป็นชั้นเดียวบนคีย์ปัจจุบัน + audit (ไม่มีค่าเต็ม) · ทิศตรงข้าม: ซ้อนที่ชั้นในเป็นเลขผิดยังถูกรายงานและไม่ถูกแตะ ·
/// แถวปกติไม่ถูกแตะ
/// </summary>
[Trait("Category", "Db")]
public class PiiNestedCipherDbTests
{
    private readonly ITestOutputHelper _out;
    public PiiNestedCipherDbTests(ITestOutputHelper output) => _out = output;

    private const string InnerKey = "r274-10-db-inner-dev-like-key";
    private const string ValidA = "1101700001237";
    private const string ValidB = "3100500123458";
    private const string BadChecksum = "1101700001238";

    private static Task<string> Raw(Accounting.Data.AccountingDbContext db, string col, Guid id, Guid cid)
        => db.Database.SqlQueryRaw<string>($"SELECT \"{col}\" AS \"Value\" FROM \"Employees\" WHERE \"Id\" = {{0}} AND \"CompanyId\" = {{1}}", id, cid).SingleAsync();

    [Fact]
    public async Task ซ้อน_devkeyในคีย์ปัจจุบันนอก_อ่านได้เลขจริง_งานย้ายคีย์เขียนชั้นเดียว_ชั้นในผิดยังรายงาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var current = EncryptedColumnConverter.Keys.Current;
        var previous = new[] { InnerKey };

        var company = new Company { Name = "บริษัททดสอบเข้ารหัสซ้อน", TaxId = "0105561012346", BranchCode = "00000" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cid = company.Id;
        Employee Emp(string code) => new() { CompanyId = cid, EmployeeCode = code, TitleTh = "นาย", FirstNameTh = "ซ้อน", LastNameTh = code,
            CitizenId = "placeholder", StartDate = DateTime.UtcNow.Date.AddYears(-1) };
        var nested = Emp("N001"); var badInner = Emp("N002"); var normal = Emp("N003");
        normal.CitizenId = ValidB;
        db.Set<Employee>().AddRange(nested, badInner, normal);
        await db.SaveChangesAsync();

        // จำลองข้อมูลที่เกิดขึ้นจริง: ciphertext dev key ถูกส่งกลับมาเป็นค่าแล้วเข้ารหัสอีกชั้นด้วยคีย์ปัจจุบัน (รุ่นก่อนรอบนี้ — ไม่มีเครื่องหมาย)
        var nestedRaw = EncryptionHelper.Encrypt(EncryptionHelper.Encrypt(ValidA, InnerKey), current);
        var badRaw = EncryptionHelper.Encrypt(EncryptionHelper.Encrypt(BadChecksum, InnerKey), current);
        var nestedBank = EncryptionHelper.Encrypt(EncryptionHelper.Encrypt("123-4-56789-0", InnerKey), current);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Employees\" SET \"CitizenId\" = {0}, \"BankAccountNumber\" = {1} WHERE \"Id\" = {2} AND \"CompanyId\" = {3}",
            nestedRaw, nestedBank, nested.Id, cid);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Employees\" SET \"CitizenId\" = {0} WHERE \"Id\" = {1} AND \"CompanyId\" = {2}", badRaw, badInner.Id, cid);
        var normalRaw = await Raw(db, "CitizenId", normal.Id, cid);

        // ── รายงาน: ซ้อนที่แกะได้ = รอย้ายคีย์ (ไม่กันยื่น) · ชั้นในเลขผิด = ต้องกรอกใหม่ · แถวปกติไม่อยู่ในรายงาน ──
        using (var rdb = DbTestDatabase.TryCreateContext()!)
        {
            var rep = await EmployeeIdentityHealth.BuildAsync(rdb, cid, currentKey: current, previousKeys: previous);
            var n1 = rep.Rows.Single(r => r.Ref == "N001" && r.Field == "เลขบัตรประชาชน");
            Assert.Equal(EmployeeIdentityHealth.Issue.PendingRekey, n1.Issue);
            Assert.False(n1.BlocksFiling);
            Assert.DoesNotContain(ValidA, n1.Masked);
            Assert.Equal(EmployeeIdentityHealth.Issue.PendingRekey, rep.Rows.Single(r => r.Ref == "N001" && r.Field == "เลขบัญชีธนาคาร").Issue);
            var n2 = rep.Rows.Single(r => r.Ref == "N002");
            Assert.Equal(EmployeeIdentityHealth.Issue.NestedCipher, n2.Issue);
            Assert.True(n2.BlocksFiling);
            Assert.DoesNotContain(rep.Rows, r => r.Ref == "N003");
        }

        // ── งานย้ายคีย์: แกะเป็นชั้นเดียว + audit · ชั้นในผิดไม่ถูกแตะ · แถวปกติไม่ถูกแตะ ──
        using (var jdb = DbTestDatabase.TryCreateContext()!)
        {
            var r = await PiiReprotectPass.RunAsync(jdb, currentKey: current, previousKeys: previous, onlyCompanyId: cid);
            Assert.Equal(2, r.Unwrapped);          // N001 เลขบัตร + เลขบัญชี
            Assert.True(r.NestedUnresolved >= 1);  // N002
            var again = await PiiReprotectPass.RunAsync(jdb, currentKey: current, previousKeys: previous, onlyCompanyId: cid);
            Assert.Equal(0, again.Unwrapped);      // idempotent
            Assert.Equal(0, again.Rekeyed);
        }
        using (var vdb = DbTestDatabase.TryCreateContext()!)
        {
            var single = await Raw(vdb, "CitizenId", nested.Id, cid);
            Assert.NotEqual(nestedRaw, single);
            Assert.Equal(ValidA, EncryptionHelper.Decrypt(single, current));   // ชั้นเดียวบนคีย์ปัจจุบัน — ไม่ต้องใช้คีย์เดิมแล้ว
            Assert.Equal("123-4-56789-0", EncryptionHelper.Decrypt(await Raw(vdb, "BankAccountNumber", nested.Id, cid), current));
            Assert.Equal(badRaw, await Raw(vdb, "CitizenId", badInner.Id, cid));      // ห้ามแต่งค่า
            Assert.Equal(normalRaw, await Raw(vdb, "CitizenId", normal.Id, cid));
            Assert.True(await vdb.AuditLogs.AnyAsync(l => l.CompanyId == cid && l.EntityId == nested.Id.ToString()
                && l.NewValues != null && l.NewValues.Contains("PiiNestedUnwrapped")));
            Assert.False(await vdb.AuditLogs.AnyAsync(l => l.CompanyId == cid && l.NewValues != null && l.NewValues.Contains(ValidA)));

            // EF (converter ของโมเดล) อ่านได้เลขจริงโดยไม่ต้องมีคีย์เดิมแล้ว · N002 ยังเป็น ciphertext (ผู้อ่านปลายทางเห็นถอดไม่ได้)
            var emps = await vdb.Set<Employee>().AsNoTracking().Where(e => e.CompanyId == cid).ToDictionaryAsync(e => e.EmployeeCode);
            Assert.Equal(ValidA, emps["N001"].CitizenId);
            Assert.Equal(ValidB, emps["N003"].CitizenId);
            Assert.True(EncryptedColumnConverter.IsUnreadable(emps["N002"].CitizenId));
        }

        // ── EF อ่านแถวซ้อน (ก่อนย้ายคีย์) ได้เลขจริงตรงจาก converter — ซ้อนสองชั้นบนคีย์ของ converter ──
        var efNested = EncryptionHelper.Encrypt(EncryptionHelper.Encrypt(ValidA, current), current);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Employees\" SET \"TaxId\" = {0} WHERE \"Id\" = {1} AND \"CompanyId\" = {2}", efNested, normal.Id, cid);
        using (var edb = DbTestDatabase.TryCreateContext()!)
        {
            var e = await edb.Set<Employee>().AsNoTracking().SingleAsync(x => x.Id == normal.Id && x.CompanyId == cid);
            Assert.Equal(ValidA, e.TaxId);
            Assert.Equal(ValidA, EmployeeTaxIdentity.Resolve(e.TaxId, e.CitizenId));
        }
    }
}
