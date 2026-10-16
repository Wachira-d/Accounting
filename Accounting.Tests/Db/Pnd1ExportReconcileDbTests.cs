using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 2026-10-14 ทีม X1 (คำถามเจ้าของ "จะ export csv, excel เพื่อ import ส่ง ภงด 1 ได้ยังไง ตรวจสอบ column ให้ตรงให้ครบ") —
/// <b>ยอดไฟล์ ภ.ง.ด.1 (.txt / CSV / Excel) = ยอดหน้ารายงาน ภ.ง.ด.1 = ยอดนำส่ง/หนี้ 21914 ของงวด</b> บน PostgreSQL จริง
///
/// <para>ชุดข้อมูล: รอบจ่ายแล้ว 3 คน — คนที่มีสวัสดิการยกเว้น (เงินได้ในแบบ = TaxableGross ไม่ใช่ Gross) · คนภาษี 0 (ต้องอยู่ในแบบ) ·
/// คนปกติ + ใบ 50 ทวิ ที่ระบบออกจากรอบ (ห้ามนับซ้ำ) + ใบ 50 ทวิ ภ.ง.ด.1 ที่ออกด้วยมือให้ผู้รับ 40(2) นอกรอบเงินเดือน
/// (ต้องอยู่ในไฟล์ ประเภท (4) — เดิมหายเงียบ)</para>
/// <para>ทิศตรงข้าม: งวดที่มีแต่รอบ "คำนวณแล้ว" (ยังไม่อนุมัติ) ⇒ ไฟล์ถูกปฏิเสธ (PAYROLL-RUN-NOT-APPROVED · ห้ามยื่นยอดที่ยังเปลี่ยนได้) ·
/// ปุ่ม e-Filing ในหน้ารายงานภาษี (ทางออกที่สอง) ปฏิเสธ PND.1 พร้อมชี้ทาง — ไม่มีไฟล์ ภ.ง.ด.1 สองหน้าตา</para>
/// </summary>
[Trait("Category", "Db")]
public class Pnd1ExportReconcileDbTests
{
    private readonly ITestOutputHelper _out;
    public Pnd1ExportReconcileDbTests(ITestOutputHelper output) => _out = output;

    // เดือนที่แล้ว (ไม่ฝังปี — หน้านำส่งดูย้อนหลังจากวันนี้)
    private static readonly DateTime Period = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);

    private sealed record Seed(Guid CompanyId, decimal PayrollTax, decimal PayrollIncome, decimal CertTax, decimal CertIncome);

    /// <summary>เลขผู้เสียภาษีบริษัทที่ผ่าน mod-11 (ฝ่ายค้าน ภ.ง.ด.1 P2-9: เดิม 0105556000001 ไม่ผ่าน checksum ⇒ เส้นหลักติดคำเตือนผู้ยื่นตลอด
    /// โดยไม่มีใคร assert — ตอนนี้เลขผิดกั้นไฟล์นำเข้า จึงต้องเป็นเลขถูก)</summary>
    private const string CompanyTaxId = "0105561012346";

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string runStatus, string companyTaxId = CompanyTaxId,
        string companyBranchCode = "00000")
    {
        var company = new Company { Name = "บริษัททดสอบ ภ.ง.ด.1", TaxId = companyTaxId, BranchCode = companyBranchCode };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cid = company.Id;

        var start = Period;
        var end = Period.AddMonths(1).AddDays(-1);
        var a = new Employee { CompanyId = cid, EmployeeCode = "E001", TitleTh = "นาย", FirstNameTh = "สมชาย", LastNameTh = "ใจดี",
            CitizenId = "1101700001237", StartDate = start.AddYears(-2), Address = "1 ถนนสุขุมวิท กรุงเทพฯ" };
        var b = new Employee { CompanyId = cid, EmployeeCode = "E002", TitleTh = "นางสาว", FirstNameTh = "สมหญิง", LastNameTh = "รักงาน",
            CitizenId = "3100500123458", StartDate = start.AddYears(-1), Address = "2 ถนนพหลโยธิน กรุงเทพฯ" };
        var c = new Employee { CompanyId = cid, EmployeeCode = "E003", TitleTh = "นาย", FirstNameTh = "ประหยัด", LastNameTh = "เงินน้อย",
            CitizenId = "1509900045673", StartDate = start.AddYears(-1), Address = "3 ถนนนิมมาน เชียงใหม่" };
        db.Set<Employee>().AddRange(a, b, c);

        var run = new PayrollRun
        {
            CompanyId = cid, PayrollNumber = "PR-T-PND1", Name = "เงินเดือนทดสอบ ภ.ง.ด.1",
            Year = Period.Year, Month = Period.Month, PeriodStart = start, PeriodEnd = end, PayDate = end, Status = runStatus,
        };
        // a: เงินเดือน 50,000 + ค่ารักษาพยาบาลยกเว้น 2,000 ⇒ เงินได้ในแบบ 50,000 (TaxableGross) ไม่ใช่ 52,000
        run.Details.Add(new PayrollDetail { CompanyId = cid, EmployeeId = a.Id, BaseSalary = 50000m, OtherIncome = 2000m,
            GrossIncome = 52000m, TaxableGross = 50000m, WithholdingTax = 2291.67m, NetPay = 49708.33m });
        // b: เงินได้ 30,000 ภาษี 375
        run.Details.Add(new PayrollDetail { CompanyId = cid, EmployeeId = b.Id, BaseSalary = 30000m,
            GrossIncome = 30000m, TaxableGross = 30000m, WithholdingTax = 375m, NetPay = 29625m });
        // c: เงินได้ต่ำกว่าเกณฑ์ ภาษี 0 — ต้องอยู่ในแบบ (ภ.ง.ด.1 แสดงผู้มีเงินได้ทุกคน) · ข้อมูลเก่า TaxableGross = 0 ⇒ ใช้ Gross
        run.Details.Add(new PayrollDetail { CompanyId = cid, EmployeeId = c.Id, BaseSalary = 12000m,
            GrossIncome = 12000m, TaxableGross = 0m, WithholdingTax = 0m, NetPay = 12000m });
        PayrollDetailAmounts.RecomputeRunTotals(run);
        db.Set<PayrollRun>().Add(run);
        await db.SaveChangesAsync();

        // ใบ 50 ทวิ ที่ระบบออกจากรอบ (SourcePayrollRunId) — แถวเงินเดือนคือตัวตั้ง ห้ามนับซ้ำ
        var empContact = new Contact { CompanyId = cid, Name = "นาย สมชาย ใจดี", TaxId = "1101700001237", ContactType = ContactType.Individual };
        // ผู้รับ 40(2) นอกรอบเงินเดือน (ค่านายหน้า) — ใบ ภ.ง.ด.1 ที่ออกด้วยมือ
        var broker = new Contact { CompanyId = cid, Name = "นายวิชัย นายหน้า", TitleTh = "นาย", TaxId = "3700600009996",
            ContactType = ContactType.Individual, CountryCode = "TH", Address = "9 ถนนพระราม 4 กรุงเทพฯ" };
        db.Contacts.AddRange(empContact, broker);
        await db.SaveChangesAsync();
        var auto = new WithholdingTaxCert
        {
            CompanyId = cid, CertificateNumber = $"PND1-{Period:yyyyMM}-E001", PayeeContactId = empContact.Id,
            TaxFormType = TaxType.WithholdingTax1, TaxYear = Period.Year, TaxMonth = Period.Month,
            CertificateType = WithholdingTaxCertType.Withhold, Status = WithholdingTaxCertStatus.Issued,
            TotalIncomeAmount = 50000m, TotalTaxAmount = 2291.67m, IssuedDate = end, SourcePayrollRunId = run.Id,
        };
        auto.Lines.Add(new WithholdingTaxCertLine { LineOrder = 1, IncomeTypeCode = "1", IncomeDescription = "เงินเดือน",
            PaymentDate = end, IncomeAmount = 50000m, TaxRate = 4.58m, TaxAmount = 2291.67m });
        var manual = new WithholdingTaxCert
        {
            CompanyId = cid, CertificateNumber = "WHT-T-0001", PayeeContactId = broker.Id,
            TaxFormType = TaxType.WithholdingTax1, TaxYear = Period.Year, TaxMonth = Period.Month,
            CertificateType = WithholdingTaxCertType.Withhold, Status = WithholdingTaxCertStatus.Issued,
            TotalIncomeAmount = 20000m, TotalTaxAmount = 600m, IssuedDate = start.AddDays(14),
        };
        manual.Lines.Add(new WithholdingTaxCertLine { LineOrder = 1, IncomeTypeCode = "2", IncomeDescription = "ค่านายหน้า",
            PaymentDate = start.AddDays(14), IncomeAmount = 20000m, TaxRate = 3m, TaxAmount = 600m });
        db.WithholdingTaxCerts.AddRange(auto, manual);

        // หนี้ภาษีหัก ณ ที่จ่ายค้างจ่าย ภ.ง.ด.1 (21914) — ทรงเดียวกับ JE ตอนกด "จ่าย" รอบเงินเดือน
        // (ProcessPaymentAsync: Cr 21914 = run.TotalWithholdingTax)
        var acc21914 = new ChartOfAccount { CompanyId = cid, AccountCode = "21914", AccountName = "ภาษีหัก ณ ที่จ่ายค้างจ่าย (ภ.ง.ด.1)",
            AccountType = AccountType.Liability, Level = 4, IsActive = true };
        var acc54111 = new ChartOfAccount { CompanyId = cid, AccountCode = "54111", AccountName = "เงินเดือน",
            AccountType = AccountType.Expense, Level = 4, IsActive = true };
        db.ChartOfAccounts.AddRange(acc21914, acc54111);
        await db.SaveChangesAsync();
        if (runStatus == PayrollRunFilingScope.Paid)
        {
            var je = new JournalEntry
            {
                CompanyId = cid, EntryNumber = "JV-PR-T-PND1", EntryDate = end, Status = JournalEntryStatus.Posted,
                JournalType = JournalType.CashPayments, TotalDebit = run.TotalWithholdingTax, TotalCredit = run.TotalWithholdingTax,
            };
            je.Lines.Add(new JournalEntryLine { AccountId = acc54111.Id, DebitAmount = run.TotalWithholdingTax, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = acc21914.Id, CreditAmount = run.TotalWithholdingTax, LineOrder = 2 });
            db.JournalEntries.Add(je);
            await db.SaveChangesAsync();
        }
        return new Seed(cid, PayrollTax: 2291.67m + 375m, PayrollIncome: 50000m + 30000m + 12000m, CertTax: 600m, CertIncome: 20000m);
    }

    private static T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;

    [Fact]
    public async Task ไฟล์ภงด1ทุกรูปแบบ_เท่ากับหน้ารายงาน_และหนี้21914ของงวด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db, PayrollRunFilingScope.Paid);

        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var export = new TaxFilingExportService(reqDb, new TaxService(reqDb));
        var txt = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "txt");
        var csv = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "csv");
        var xlsx = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "xlsx");
        var review = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "review");

        // ── ไฟล์: เงินเดือน 3 แถว (รวมคนภาษี 0) + 50 ทวิ นอกรอบ 1 แถว · ใบที่ออกจากรอบไม่ซ้ำ ──
        foreach (var r in new[] { txt, csv, xlsx, review })
        {
            Assert.Equal(4, r.RecordCount);
            Assert.Equal(s.PayrollTax + s.CertTax, r.TotalTax);
            Assert.Equal(s.PayrollIncome + s.CertIncome, r.TotalIncome);
            var cats = Assert.IsAssignableFrom<IReadOnlyList<Pnd1FileFormat.CategoryTotal>>(r.Categories);
            Assert.Equal(5, cats.Count);
            Assert.Equal(3, cats[0].Count);                    // (1) 40(1) ทั่วไป
            Assert.Equal(s.PayrollTax, cats[0].Tax);
            Assert.Equal(1, cats[3].Count);                    // (4) 40(2) ผู้รับอยู่ในไทย
            Assert.Equal(s.CertTax, cats[3].Tax);
        }
        // P2-9: เส้นหลัก (เลขบริษัทถูก) ต้องไม่มีคำเตือนผู้ยื่น · วันที่จ่ายอยู่ในเดือนภาษี ⇒ ไม่มีคำเตือนงวด (P2-6)
        foreach (var r in new[] { txt, csv, xlsx, review })
        {
            Assert.DoesNotContain("เลขประจำตัวผู้เสียภาษีของบริษัท", r.Summary);
            Assert.DoesNotContain("อยู่นอกเดือนภาษี", r.Summary);
        }
        // P1-2: .txt UTF-8 บอกว่า encoding ยังไม่ยืนยัน + แนะนำ Excel · TIS-620 ออกได้ ค่าเดียวกันทุกช่อง
        Assert.Contains("แนะนำให้นำเข้าด้วยไฟล์ Excel", txt.Summary);
        var tis = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "txt-tis620");
        Assert.Equal("PND1.txt", tis.FileName);
        Assert.Contains("windows-874", tis.ContentType);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(txt.FileData),
            System.Text.CodePagesEncodingProvider.Instance.GetEncoding(874)!.GetString(tis.FileData));
        // P1-3: ไม่มีสิทธิ์ Pii.View ⇒ รายงานตรวจทาน + ข้อความดูตัวอย่างปิดบังเลข · ไฟล์นำเข้าเลขเต็มเสมอ
        var maskedReview = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "review", includePii: false);
        using (var rms = new MemoryStream(maskedReview.FileData))
        {
            var detail = MiniExcelLibs.MiniExcel.Query(rms, useHeaderRow: false, sheetName: Pnd1FileFormat.ReviewRowsSheetName)
                .Cast<IDictionary<string, object>>().ToList();
            Assert.Equal("1-XXXX-XXXXX-XX-7", detail[1]["E"]?.ToString());
        }
        var maskedPreview = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, includePii: false);
        Assert.DoesNotContain("1101700001237", maskedPreview.PreviewText);
        Assert.Contains("1101700001237", System.Text.Encoding.UTF8.GetString(maskedPreview.FileData));
        Assert.Contains("1101700001237", txt.PreviewText);

        // รอบ 2026-10-16 สเปก WHTSVS: ไฟล์เดียว PND1.txt ทุกประเภทเงินได้ (รหัสเงินได้ช่อง 15) — เดิม zip ไฟล์ละประเภท
        Assert.Equal("PND1.txt", txt.FileName);
        Assert.StartsWith("text/plain", txt.ContentType);
        Assert.EndsWith(".csv", csv.FileName);
        Assert.EndsWith(".xlsx", xlsx.FileName);
        Assert.EndsWith("_review.xlsx", review.FileName);
        var lines = System.Text.Encoding.UTF8.GetString(txt.FileData).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.Equal(20, l.Split('|').Length));
        Assert.DoesNotContain(lines, l => l.StartsWith("H|") || l.StartsWith("T|") || l.StartsWith("D|"));
        var payDate = Pnd1FileFormat.ThaiDate(Period.AddMonths(1).AddDays(-1));
        // E001 — ผู้หัก (ช่อง 1–4) · เงินได้ = TaxableGross 50,000 (ไม่ใช่ Gross 52,000) · วันที่ DDMMYYYY พ.ศ. · คำนำหน้าข้อความไทย
        var e1 = lines[0].Split('|');
        Assert.Equal(new[] { "00", CompanyTaxId, "0000000000", "0000", "1101700001237" }, e1.Take(5).ToArray());
        Assert.Equal("นาย", e1[6]);
        Assert.Equal("สมชาย", e1[7]);
        Assert.Equal(Period.Month.ToString("00"), e1[12]);
        Assert.Equal((Period.Year + 543).ToString(), e1[13]);
        Assert.Equal("1", e1[14]);
        Assert.Equal(payDate, e1[15]);
        Assert.Equal("50000.00", e1[17]);
        // E003 — ภาษี 0 ยังอยู่ในแบบ · ข้อมูลเก่า TaxableGross 0 ⇒ Gross
        var e3 = lines[2].Split('|');
        Assert.Equal("12000.00", e3[17]);
        Assert.Equal("0.00", e3[18]);
        // ใบมือ 40(2) ผู้รับอยู่ในไทย ⇒ รหัสเงินได้ 4 · อัตรา 3 จากบรรทัด 50 ทวิ
        var broker = lines[3].Split('|');
        Assert.Equal("3700600009996", broker[4]);
        Assert.Equal("4", broker[14]);
        Assert.Equal("3", broker[16]);
        Assert.Equal("600.00", broker[18]);
        // Excel นำเข้า = Sheet1 ไม่มีแถวหัว ค่าเท่ากับ .txt ทุกเซลล์
        using (var ms = new MemoryStream(xlsx.FileData))
        {
            Assert.Equal(new[] { "Sheet1" }, MiniExcelLibs.MiniExcel.GetSheetNames(ms).ToArray());
            ms.Position = 0;
            var read = MiniExcelLibs.MiniExcel.Query(ms, useHeaderRow: false, sheetName: "Sheet1").Cast<IDictionary<string, object>>().ToList();
            Assert.Equal(4, read.Count);
            for (var i = 0; i < 4; i++)
                Assert.Equal(lines[i].Split('|'),
                    Enumerable.Range(0, 20).Select(c => read[i][((char)('A' + c)).ToString()]?.ToString() ?? "").ToArray());
        }

        // ── หน้ารายงาน ภ.ง.ด.1 (หน้าเงินเดือน) — ตัวโหลดเดียวกับไฟล์ ──
        var report = await new PayrollService(reqDb).GeneratePnd1Async(s.CompanyId, Period.Year, Period.Month, includePii: false);
        Assert.Equal(txt.TotalTax, Prop<decimal>(report, "FiledTaxWithheld"));
        Assert.Equal(txt.TotalIncome, Prop<decimal>(report, "FiledIncome"));
        Assert.Equal(txt.RecordCount, Prop<int>(report, "FiledEmployees"));
        // จอ "รายได้รวม" = ฐานภาษี (เดิม GrossIncome 52,000 ⇒ จอ ≠ ไฟล์)
        Assert.Equal(s.PayrollIncome + s.CertIncome, Prop<decimal>(report, "TotalTaxableIncome"));

        // ── หนี้ 21914 ของงวด = ภาษีส่วนเงินเดือนในไฟล์ · หน้านำส่ง ภ.ง.ด.1 = ภาษีส่วนเงินเดือน ──
        var gl21914 = await (from l in reqDb.JournalEntryLines.AsNoTracking()
                             join j in reqDb.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                             join a in reqDb.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                             where j.CompanyId == s.CompanyId && a.AccountCode == "21914" && j.Status == JournalEntryStatus.Posted
                             select l.CreditAmount - l.DebitAmount).SumAsync();
        Assert.Equal(s.PayrollTax, gl21914);
        Assert.Equal(gl21914, txt.TotalTax - s.CertTax);
        var remit = new StatutoryRemittanceService(reqDb, new AccountingService(reqDb),
            NullLogger<StatutoryRemittanceService>.Instance, new TaxService(reqDb));
        var dash = await remit.GetDashboardAsync(s.CompanyId, 24);
        var pnd1 = Assert.Single(dash.Pending.Where(p => p.RemittanceType == "WhtPnd1"
            && p.PeriodYear == Period.Year && p.PeriodMonth == Period.Month));
        Assert.Equal(gl21914, pnd1.Amount);
    }

    [Fact]
    public async Task ทิศตรงข้าม_รอบยังไม่อนุมัติ_ไฟล์ถูกปฏิเสธ_และปุ่มeFilingปฏิเสธPND1()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, PayrollRunFilingScope.Calculated);

        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var export = new TaxFilingExportService(reqDb, new TaxService(reqDb));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "xlsx"));
        Assert.Equal("PAYROLL-RUN-NOT-APPROVED", ex.RuleCode);

        var ef = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new TaxService(reqDb).GenerateEFilingAsync(s.CompanyId, "PND.1", Period.Year, Period.Month, "db-test"));
        Assert.Equal("PND1-USE-PAYROLL-EXPORT", ef.RuleCode);
        // ไม่มีแถว EFilingExport ค้าง (ไม่บันทึกไฟล์ที่ไม่ได้สร้าง)
        Assert.False(await reqDb.EFilingExports.AnyAsync(x => x.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task ทิศตรงข้าม_เลขผู้เสียภาษีบริษัทผิด_ไฟล์นำเข้าถูกกั้นพร้อมทางไปต่อ_รายงานตรวจทานยังออกได้พร้อมคำเตือน()
    {
        // P2-4: เดิมออกไฟล์ได้ (ช่อง 2 = เลขผิด) แล้วเตือนว่า "สรรพากรจะไม่รับทั้งไฟล์" · fixture เดิมของเทสต์นี้คือเลขชุดนี้
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, PayrollRunFilingScope.Paid, companyTaxId: "0105556000001");

        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var export = new TaxFilingExportService(reqDb, new TaxService(reqDb));
        foreach (var fmt in new[] { "txt", "txt-tis620", "xlsx", "csv" })
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, fmt));
            Assert.Equal("PND1-WITHHOLDER-TAXID", ex.RuleCode);
            Assert.Contains("ตั้งค่า → ข้อมูลบริษัท", ex.Message);
        }
        var review = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "review");
        Assert.Equal(4, review.RecordCount);
        Assert.Contains("เลขประจำตัวผู้เสียภาษีของบริษัท", review.Summary);
    }

    // ── ฝ่ายค้าน W1c P2-4 (ทีม W1d): ด่านของไฟล์นำเข้าห้ามกั้นพรีวิว — แสดงเป็นคำเตือนแทน ──

    [Theory]
    [InlineData("0105556000001", "00000", "PND1-WITHHOLDER-TAXID", "ตั้งค่า → ข้อมูลบริษัท")]   // เลขผู้หักไม่ผ่าน checksum
    [InlineData("", "00000", "PND1-WITHHOLDER-TAXID", "ยังไม่ได้กรอก")]
    [InlineData(CompanyTaxId, "10001", "PND1-BRANCH-4DIGIT", "รายงานตรวจทาน")]                // สาขาผู้หัก ≥ 10000
    public async Task พรีวิว_ด่านไฟล์นำเข้าของผู้หัก_ไม่กั้นพรีวิว_และแสดงคำเตือน(string taxId, string branch, string rule, string wayForward)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, PayrollRunFilingScope.Paid, companyTaxId: taxId, companyBranchCode: branch);

        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var export = new TaxFilingExportService(reqDb, new TaxService(reqDb));
        // เดิม: พรีวิว = txt ⇒ โยน PND1-WITHHOLDER-TAXID ⇒ ยอดรวม/ตัวอย่างบนหน้า tax-export ใช้ไม่ได้ และคำเตือนไม่มีทางแสดง
        var preview = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, includePii: false, previewOnly: true);
        Assert.Equal(4, preview.RecordCount);
        Assert.Equal(s.PayrollTax + s.CertTax, preview.TotalTax);
        Assert.Empty(preview.FileData);
        Assert.Contains("⚠️ ไฟล์นำเข้า (Excel/.txt/CSV) ถูกกั้นจนกว่าจะแก้", preview.Summary);
        Assert.Contains(wayForward, preview.Summary);
        Assert.Equal(4, preview.PreviewText!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.NotNull(preview.Categories);
        var annual = await export.ExportPnd1kAsync(s.CompanyId, Period.Year, includePii: false, previewOnly: true);
        Assert.Contains("⚠️ ไฟล์นำเข้า", annual.Summary);

        // ทิศตรงข้าม: ไฟล์นำเข้าจริงยังถูกกั้นด้วยด่านเดียวกัน (รหัสเดียวกัน)
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "xlsx"));
        Assert.Equal(rule, ex.RuleCode);
    }

    [Fact]
    public async Task ทิศตรงข้าม_พรีวิวของบริษัทที่ข้อมูลผู้หักถูก_ไม่มีคำเตือนด่านไฟล์นำเข้า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, PayrollRunFilingScope.Paid);
        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var export = new TaxFilingExportService(reqDb, new TaxService(reqDb));
        var preview = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, previewOnly: true);
        Assert.DoesNotContain("ไฟล์นำเข้า (Excel/.txt/CSV) ถูกกั้น", preview.Summary);
        // ตัวอย่าง = ไฟล์ .txt จริงทุกบรรทัด (สาขา 4 หลักเท่ากัน)
        var txt = await export.ExportPnd1Async(s.CompanyId, Period.Year, Period.Month, "txt");
        Assert.Equal(System.Text.Encoding.UTF8.GetString(txt.FileData), preview.PreviewText);
    }

    // ── ฝ่ายค้าน W1c P2-5 (ทีม W1d): ไฟล์แนบ ภ.ง.ด.1 ของรอบเงินเดือนที่สร้างไม่ได้ต้องดัง (ข้อมูล · คำตอบ) ──

    private sealed class RecordingFiles : Accounting.Services.Interfaces.IFileAttachmentService
    {
        public List<string> Uploaded { get; } = new();
        public Task<Accounting.Models.DTOs.FileAttachment.FileAttachmentResponse> UploadBytesAsync(Guid companyId, string entityType, Guid entityId,
            string originalFileName, string contentType, byte[] content, Guid uploadedByUserId)
        {
            Uploaded.Add(originalFileName);
            return Task.FromResult(new Accounting.Models.DTOs.FileAttachment.FileAttachmentResponse(Guid.NewGuid(), originalFileName, originalFileName,
                contentType, content.LongLength, "/dev/null", null, entityType, entityId, "ผู้ทดสอบ", DateTime.UtcNow));
        }
        public Task<Accounting.Models.DTOs.FileAttachment.FileAttachmentResponse> UploadAsync(Guid companyId, string entityType, Guid entityId,
            string fileName, string originalFileName, string contentType, long fileSize, string storagePath, Guid uploadedByUserId)
            => throw new NotSupportedException();
        public Task<List<Accounting.Models.DTOs.FileAttachment.FileAttachmentResponse>> GetByEntityAsync(Guid companyId, string entityType, Guid entityId)
            => throw new NotSupportedException();
        public Task<Accounting.Models.DTOs.FileAttachment.FileAttachmentResponse?> GetByIdAsync(Guid companyId, Guid attachmentId)
            => throw new NotSupportedException();
        public Task<byte[]?> ReadBytesAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid companyId, Guid attachmentId, bool keepPhysicalFile = false) => throw new NotSupportedException();
    }

    private async Task<(Guid RunId, RecordingFiles Files, Accounting.Data.AccountingDbContext Db)?> PayAttachAsync(string taxId)
    {
        var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return null; }
        var s = await SeedAsync(db, PayrollRunFilingScope.Paid, companyTaxId: taxId);
        var user = new User { Email = $"w1d-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ทดสอบ W1d" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var runId = await db.Set<PayrollRun>().Where(r => r.CompanyId == s.CompanyId).Select(r => r.Id).SingleAsync();
        db.Dispose();

        var reqDb = DbTestDatabase.TryCreateContext()!;
        var files = new RecordingFiles();
        var svc = new PayrollService(reqDb, taxFilingExport: new TaxFilingExportService(reqDb, new TaxService(reqDb)), attachments: files);
        await svc.GeneratePostPaymentArtifactsAsync(s.CompanyId, runId, user.Id.ToString());
        return (runId, files, reqDb);
    }

    [Fact]
    public async Task รอบเงินเดือน_ไฟล์แนบภงด1สร้างไม่ได้_เหตุและทางไปต่ออยู่บนรอบ_และคำตอบของรอบ()
    {
        var r = await PayAttachAsync("0105556000001");
        if (r == null) return;
        var (runId, files, db) = r.Value;
        using var _ = db;
        // เดิม: LogWarning อย่างเดียว ⇒ รอบที่จ่ายแล้วไม่มีไฟล์ ภ.ง.ด.1 โดยไม่มีใครรู้เหตุ
        Assert.DoesNotContain(files.Uploaded, f => f.Contains("PND1", StringComparison.OrdinalIgnoreCase));
        using var check = DbTestDatabase.TryCreateContext()!;
        var run = await check.Set<PayrollRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
        Assert.NotNull(run.FilingAttachmentWarning);
        Assert.Contains("ไม่มีไฟล์แนบ ภ.ง.ด.1", run.FilingAttachmentWarning);
        Assert.Contains("ตั้งค่า → ข้อมูลบริษัท", run.FilingAttachmentWarning);
        Assert.Contains("Export ยื่นภาษี", run.FilingAttachmentWarning);
        // เก็บแล้วต้อง echo — หน้ารอบเงินเดือนอ่านจากคำตอบนี้
        var resp = await new PayrollService(check).GetPayrollRunAsync(run.CompanyId, runId);
        Assert.Equal(run.FilingAttachmentWarning, resp.FilingAttachmentWarning);
    }

    [Fact]
    public async Task ทิศตรงข้าม_รอบเงินเดือน_เลขผู้หักถูก_ได้ไฟล์แนบภงด1_และไม่มีคำเตือน()
    {
        var r = await PayAttachAsync(CompanyTaxId);
        if (r == null) return;
        var (runId, files, db) = r.Value;
        using var _ = db;
        Assert.Contains(Pnd1FileFormat.TextFileName, files.Uploaded);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Null(await check.Set<PayrollRun>().AsNoTracking().Where(x => x.Id == runId).Select(x => x.FilingAttachmentWarning).SingleAsync());
    }
}
