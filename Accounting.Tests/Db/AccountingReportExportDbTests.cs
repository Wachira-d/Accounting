using System.Globalization;
using System.Text;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบส่งออก 2026-10-14 (ทีม X3 · erp-review/2026-10-14/EXPORT_AUDIT.md §X3) — ไฟล์ส่งออก "แถว/ยอด = ผลของ service ตัวเดียวกับหน้า"
/// · บัญชีแยกประเภท: ทุกรายการ + ยอดคงเหลือตรงทีละแถว · ยอดยกมากรองสาขาเดียวกับรายการ (เดิมไม่กรอง) · ยอดยกมานำเข้า (OpeningBalances)
/// ถูกนับเหมือนงบทดลอง · บัญชีที่มีแต่ยอดยกมาไม่หาย · งบทดลอง: ยอดยกไปเท่ากับหน้า + รวมสมดุล + ต่อเนื่อง ·
/// สมุดรายวัน: เกิน 200 ใบ (เพดาน pageSize) ไม่ถูกตัด
/// </summary>
[Trait("Category", "Db")]
public class AccountingReportExportDbTests
{
    private readonly ITestOutputHelper _out;
    public AccountingReportExportDbTests(ITestOutputHelper output) => _out = output;

    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar1 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar31 = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(Guid CompanyId, Dictionary<string, Guid> Acc, Guid B1, Guid B2);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, bool withOpeningBalances)
    {
        var company = new Company { Name = "บริษัททดสอบส่งออกรายงาน", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var acc = new Dictionary<string, Guid>();
        void Add(string code, string name, AccountType type)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type, Level = 4 };
            db.ChartOfAccounts.Add(a);
            acc[code] = a.Id;
        }
        Add("11111", "เงินสด", AccountType.Asset);
        Add("11300", "ลูกหนี้การค้า", AccountType.Asset);
        Add("11500", "เงินมัดจำจ่าย", AccountType.Asset);        // มีแต่ยอดยกมา ไม่เคลื่อนไหว
        Add("31000", "ทุนจดทะเบียน", AccountType.Equity);
        Add("41000", "รายได้จากการขาย", AccountType.Revenue);
        Add("51000", "ค่าใช้จ่ายสำนักงาน", AccountType.Expense);
        var b1 = new Branch { CompanyId = company.Id, Code = "B1", Name = "สาขาหนึ่ง" };
        var b2 = new Branch { CompanyId = company.Id, Code = "B2", Name = "สาขาสอง" };
        db.Branches.AddRange(b1, b2);
        await db.SaveChangesAsync();

        if (withOpeningBalances)
        {
            // ยอดยกมาที่นำเข้าจากระบบเดิม ณ ต้นปี (ไม่มีประวัติ JE ก่อนหน้า) — งบทดลองอ่านตัวนี้
            var p = new FiscalPeriod { CompanyId = company.Id, Name = "2569/01", Year = 2026, Month = 1, StartDate = Jan1, EndDate = Jan1.AddMonths(1).AddDays(-1) };
            db.FiscalPeriods.Add(p);
            await db.SaveChangesAsync();
            db.OpeningBalances.AddRange(
                new OpeningBalance { CompanyId = company.Id, FiscalPeriodId = p.Id, AccountId = acc["11111"], OpeningDebit = 100_000m },
                new OpeningBalance { CompanyId = company.Id, FiscalPeriodId = p.Id, AccountId = acc["11500"], OpeningDebit = 5_000m },
                new OpeningBalance { CompanyId = company.Id, FiscalPeriodId = p.Id, AccountId = acc["31000"], OpeningCredit = 105_000m });
            await db.SaveChangesAsync();
        }

        var n = 0;
        void Je(DateTime date, Guid? branch, string dr, string cr, decimal amt, string desc)
        {
            n++;
            var je = new JournalEntry
            {
                CompanyId = company.Id, EntryNumber = $"JV-X3-{n:D4}", EntryDate = date, Status = JournalEntryStatus.Posted,
                Description = desc, Reference = $"REF-{n}", BranchId = branch, TotalDebit = amt, TotalCredit = amt,
            };
            je.Lines.Add(new JournalEntryLine { AccountId = acc[dr], DebitAmount = amt, LineOrder = 1, Description = desc });
            je.Lines.Add(new JournalEntryLine { AccountId = acc[cr], CreditAmount = amt, LineOrder = 2, Description = desc });
            db.JournalEntries.Add(je);
        }
        // ก่อนช่วง (ก.พ.) — สาขาหนึ่ง/สอง
        Je(new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc), b1.Id, "11300", "41000", 1_000m, "ขาย ก.พ. สาขาหนึ่ง");
        Je(new DateTime(2026, 2, 12, 0, 0, 0, DateTimeKind.Utc), b2.Id, "11300", "41000", 7_000m, "ขาย ก.พ. สาขาสอง");
        // ในช่วง (มี.ค.)
        Je(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc), b1.Id, "11111", "11300", 600m, "รับชำระ สาขาหนึ่ง");
        Je(new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc), b2.Id, "51000", "11111", 250.55m, "=ค่าเครื่องเขียน, สาขาสอง");
        Je(new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc), null, "11300", "41000", 3_000m, "ขาย มี.ค. สำนักงาน");
        await db.SaveChangesAsync();
        return new Seed(company.Id, acc, b1.Id, b2.Id);
    }

    private static AccountingReportExportService Export(AccountingDbContext db) => new(db, new AccountingService(db));

    /// <summary>CSV → แถว (ตัด BOM · CRLF · อัญประกาศคู่) — ตัวอ่านขั้นต่ำสำหรับเทสต์</summary>
    private static List<string[]> ParseCsv(byte[] bytes)
    {
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "ต้องมี BOM");
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var rows = new List<string[]>();
        var cur = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (q)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') q = false;
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { cur.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { cur.Add(sb.ToString()); sb.Clear(); rows.Add(cur.ToArray()); cur.Clear(); i++; }
            else sb.Append(c);
        }
        if (sb.Length > 0 || cur.Count > 0) { cur.Add(sb.ToString()); rows.Add(cur.ToArray()); }
        return rows;
    }

    private static decimal Dec(string s) => s == "" ? 0m : decimal.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public async Task บัญชีแยกประเภท_ไฟล์CSV_ทุกแถวและยอดคงเหลือ_เท่ากับ_service_ที่หน้าเรียก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db, withOpeningBalances: false);

        using var req = DbTestDatabase.TryCreateContext()!;
        var gl = await new AccountingService(req).GetGeneralLedgerAsync(s.CompanyId, Mar1, Mar31);
        var file = await Export(req).GeneralLedgerAsync(s.CompanyId, Guid.NewGuid(), Mar1, Mar31, null, null, null, null, null, null, TabularFormat.Csv);
        var rows = ParseCsv(file.Content);
        var h = rows[0];
        int Col(string name) { var i = Array.IndexOf(h, name); Assert.True(i >= 0, "ไม่มีคอลัมน์ " + name); return i; }
        var (cKind, cNo, cDr, cCr, cBal, cRef, cBranch) = (Col("ชนิดแถว"), Col("เลขที่ใบสำคัญ"), Col("เดบิต"), Col("เครดิต"), Col("ยอดคงเหลือ"), Col("อ้างอิง"), Col("สาขา"));

        var data = rows.Skip(1).ToList();
        Assert.Equal(gl.Accounts.Sum(a => a.Transactions.Count + 2), data.Count);   // ยกมา + ทุกรายการ + ยกไป ต่อบัญชี
        var i = 0;
        foreach (var a in gl.Accounts)
        {
            Assert.Equal("ยอดยกมา", data[i][cKind]);
            Assert.Equal(a.OpeningBalance, Dec(data[i][cBal]));
            i++;
            foreach (var t in a.Transactions)
            {
                Assert.Equal(t.EntryNumber, data[i][cNo]);
                Assert.Equal(t.DebitAmount, Dec(data[i][cDr]));
                Assert.Equal(t.CreditAmount, Dec(data[i][cCr]));
                Assert.Equal(t.RunningBalance, Dec(data[i][cBal]));
                Assert.Equal(t.EntryReference, data[i][cRef]);
                i++;
            }
            Assert.Equal("ยอดยกไป", data[i][cKind]);
            Assert.Equal(a.TotalDebit, Dec(data[i][cDr]));
            Assert.Equal(a.TotalCredit, Dec(data[i][cCr]));
            Assert.Equal(a.ClosingBalance, Dec(data[i][cBal]));
            i++;
        }
        // ยอดติดลบ (ลูกหนี้: ยกมา 8,000 − รับ 600 + ขาย 3,000 ไม่ติดลบ · เงินสด: −600+... ) — ตัวเลขไม่ถูกเติม ' · ข้อความสูตรถูกกัน
        Assert.DoesNotContain(data, r => r[cBal].StartsWith("'"));
        Assert.Contains(data, r => r.Any(c => c.StartsWith("'=ค่าเครื่องเขียน")));
        Assert.Contains(data, r => r[cBranch] == "B2 สาขาสอง");
    }

    [Fact]
    public async Task บัญชีแยกประเภท_กรองสาขา_ยอดยกมานับเฉพาะสาขานั้น_ไม่ใช่ทั้งบริษัท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, withOpeningBalances: true);
        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new AccountingService(req);

        // สาขาหนึ่ง: ลูกหนี้ยกมา = ขาย ก.พ. ของสาขาหนึ่ง 1,000 เท่านั้น (เดิม 8,000 = ทั้งบริษัท) · ไม่บวกยอดนำเข้า (ไม่มีสาขา)
        var b1 = await svc.GetGeneralLedgerAsync(s.CompanyId, Mar1, Mar31, branchId: s.B1);
        var ar = b1.Accounts.Single(a => a.AccountCode == "11300");
        Assert.Equal(1_000m, ar.OpeningBalance);
        Assert.Equal(400m, ar.ClosingBalance);   // 1,000 − รับชำระ 600
        Assert.DoesNotContain(b1.Accounts, a => a.AccountCode == "11500");   // ยอดนำเข้าไม่ถูกยัดเข้าสาขา

        // ทิศตรงข้าม: ไม่กรอง ⇒ ลูกหนี้ยกมาทั้งบริษัท 8,000 · เงินสดยกมาจากยอดนำเข้า 100,000 ·
        // บัญชีที่มีแต่ยอดยกมา (มัดจำ 5,000) ต้องปรากฏ (เดิมหาย)
        var all = await svc.GetGeneralLedgerAsync(s.CompanyId, Mar1, Mar31);
        Assert.Equal(8_000m, all.Accounts.Single(a => a.AccountCode == "11300").OpeningBalance);
        Assert.Equal(100_000m, all.Accounts.Single(a => a.AccountCode == "11111").OpeningBalance);
        var deposit = all.Accounts.Single(a => a.AccountCode == "11500");
        Assert.Equal(5_000m, deposit.OpeningBalance);
        Assert.Equal(5_000m, deposit.ClosingBalance);
        Assert.Empty(deposit.Transactions);
    }

    [Fact]
    public async Task งบทดลอง_ไฟล์_ยอดยกไปเท่าหน้างบทดลอง_รวมสมดุล_และต่อเนื่องกับบัญชีแยกประเภท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, withOpeningBalances: true);
        using var req = DbTestDatabase.TryCreateContext()!;
        var tb = await new AccountingService(req).GetTrialBalanceAsync(s.CompanyId, Mar31);
        var gl = await new AccountingService(req).GetGeneralLedgerAsync(s.CompanyId, Mar1, Mar31);

        // บัญชีแยกประเภท (ไม่กรอง) ยอดยกไป = งบทดลองสุทธิ ทุกบัญชี — เดิมต่างกันเพราะ GL ไม่อ่านยอดนำเข้า
        foreach (var t in tb.Items)
        {
            var a = gl.Accounts.Single(x => x.AccountCode == t.AccountCode);
            var debitNet = a.AccountType is AccountType.Asset or AccountType.Expense ? a.ClosingBalance : -a.ClosingBalance;
            Assert.Equal(t.DebitBalance - t.CreditBalance, debitNet);
        }

        var file = await Export(req).TrialBalanceAsync(s.CompanyId, Mar1, Mar31, null, null, null, TabularFormat.Csv);
        var rows = ParseCsv(file.Content);
        var h = rows[0];
        int Col(string name) => Array.IndexOf(h, name);
        var total = rows.Single(r => r.Length > 1 && r[1] == "รวมทั้งสิ้น");
        Assert.Equal(tb.TotalDebit, Dec(total[Col("ยอดเดบิตสะสม (ตามหน้างบทดลอง)")]));
        Assert.Equal(tb.TotalCredit, Dec(total[Col("ยอดเครดิตสะสม (ตามหน้างบทดลอง)")]));
        Assert.Equal(Dec(total[Col("ยอดยกมา เดบิต")]), Dec(total[Col("ยอดยกมา เครดิต")]));
        Assert.Equal(Dec(total[Col("เคลื่อนไหว เดบิต")]), Dec(total[Col("เคลื่อนไหว เครดิต")]));
        Assert.Equal(Dec(total[Col("ยอดยกไป เดบิต")]), Dec(total[Col("ยอดยกไป เครดิต")]));
        Assert.Equal(113_000m, Dec(total[Col("ยอดยกมา เดบิต")]));      // เงินสด 100,000 + ลูกหนี้ 8,000 + มัดจำ 5,000
        var gapCol = Col("ผลต่างความต่อเนื่อง (ยกมา+เคลื่อนไหว−ยกไป)");
        Assert.All(rows.Skip(1), r => Assert.Equal("", r[gapCol]));    // ไม่มีบัญชีที่ไม่ต่อเนื่อง
        foreach (var t in tb.Items)
        {
            var r = rows.Single(x => x[0] == t.AccountCode);
            var net = t.DebitBalance - t.CreditBalance;
            Assert.Equal(net > 0 ? net : 0m, Dec(r[Col("ยอดยกไป เดบิต")]));
            Assert.Equal(net < 0 ? -net : 0m, Dec(r[Col("ยอดยกไป เครดิต")]));
        }
    }

    [Fact]
    public async Task สมุดรายวัน_เกิน200ใบ_ไฟล์มีครบทุกบรรทัด_ไม่ถูกตัดที่เพดานหน้า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, withOpeningBalances: false);
        for (var k = 0; k < 205; k++)
        {
            var je = new JournalEntry
            {
                CompanyId = s.CompanyId, EntryNumber = $"JV-BULK-{k:D4}", EntryDate = Mar1.AddDays(k % 28), Status = JournalEntryStatus.Posted,
                Description = "รายการจำนวนมาก", TotalDebit = 10m, TotalCredit = 10m,
            };
            je.Lines.Add(new JournalEntryLine { AccountId = s.Acc["51000"], DebitAmount = 10m, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = s.Acc["11111"], CreditAmount = 10m, LineOrder = 2 });
            db.JournalEntries.Add(je);
        }
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var file = await Export(req).JournalsAsync(s.CompanyId, Guid.NewGuid(), null, null, Mar1, Mar31, null, null, null, null, null, null, TabularFormat.Csv);
        var rows = ParseCsv(file.Content);
        var data = rows.Skip(1).Where(r => r[0] != "").ToList();
        Assert.Equal((205 + 3) * 2, data.Count);   // 205 ใบจำนวนมาก + 3 ใบ มี.ค. จาก seed · ใบละ 2 บรรทัด
        Assert.Equal(205 + 3, data.Select(r => r[0]).Distinct().Count());
        var total = rows.Last();
        Assert.Equal("รวมทั้งสิ้น", total[4]);
        Assert.Equal(205 * 10m + 600m + 250.55m + 3_000m, Dec(total[13]));
        Assert.Equal(Dec(total[13]), Dec(total[14]));

        // ทิศตรงข้าม: เลือก 1 ใบ ⇒ ได้แค่ใบนั้น (ไม่ใช่ทั้งบริษัท)
        var one = req.JournalEntries.Where(j => j.CompanyId == s.CompanyId && j.EntryNumber == "JV-BULK-0007").Select(j => j.Id).Single();
        var sel = ParseCsv((await Export(req).JournalsAsync(s.CompanyId, Guid.NewGuid(), null, null, null, null, null, null, null, null, null,
            new[] { one }, TabularFormat.Csv)).Content);
        Assert.Equal(new[] { "JV-BULK-0007" }, sel.Skip(1).Where(r => r[0] != "").Select(r => r[0]).Distinct().ToArray());
    }

    [Fact]
    public async Task บันทึกการใช้งาน_ถึงวันที่แบบวันล้วน_รวมทั้งวันสุดท้าย_และไฟล์มีทุกแถวเกินหน้าเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบส่งออก audit", TaxId = "0105556000003" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        AuditLog Row(DateTime ts, string tag) => new()
        {
            CompanyId = company.Id, UserEmail = "x3@example.com", Action = AuditAction.Update,
            EntityType = "X3Export", EntityId = tag, NewValues = "{\"t\":\"" + tag + "\"}", Timestamp = ts,
        };
        // 14 ต.ค. 10:00 UTC (ในวันสุดท้าย) · 15 ต.ค. 01:00 UTC (นอกช่วง) · อีก 1,005 แถวต้นเดือน (เกินหน้า 1,000 ของตัวส่งออก)
        db.AddChainedAuditLog(Row(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc), "last-day"));
        db.AddChainedAuditLog(Row(new DateTime(2026, 10, 15, 1, 0, 0, DateTimeKind.Utc), "next-day"));
        for (var k = 0; k < 1005; k++)
            db.AddChainedAuditLog(Row(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc).AddSeconds(k), "bulk-" + k));
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new AuditTrailService(req);
        var from = new DateTime(2026, 10, 1);
        var dayOnly = await svc.GetLogsAsync(company.Id, new Accounting.Models.DTOs.Audit.AuditLogQueryRequest(from, new DateTime(2026, 10, 14), PageSize: 50));
        Assert.Equal(1006, dayOnly.TotalCount);   // เดิม 1,005 — "ถึง 14 ต.ค." ตัดรายการของวันที่ 14 ทิ้ง
        // ทิศตรงข้าม: ระบุเวลา ⇒ ตามเวลานั้นตรงตัว (ผู้เรียกแบบ API ไม่เปลี่ยนพฤติกรรม)
        var withTime = await svc.GetLogsAsync(company.Id, new Accounting.Models.DTOs.Audit.AuditLogQueryRequest(from, new DateTime(2026, 10, 14, 9, 0, 0), PageSize: 50));
        Assert.Equal(1005, withTime.TotalCount);

        var export = new AccountingReportExportService(req, new AccountingService(req), audit: svc);
        var rows = ParseCsv((await export.AuditLogsAsync(company.Id, from, new DateTime(2026, 10, 14), null, null, TabularFormat.Csv)).Content);
        Assert.Equal(1006, rows.Count - 1);
        var idCol = Array.IndexOf(rows[0], "ลำดับ");
        Assert.Equal(1006, rows.Skip(1).Select(r => r[idCol]).Distinct().Count());   // ไม่มีแถวซ้ำ/หายระหว่างหน้า
        Assert.Contains(rows, r => r.Contains("last-day"));
        Assert.DoesNotContain(rows, r => r.Contains("next-day"));
    }
}
