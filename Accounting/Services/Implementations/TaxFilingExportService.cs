using Accounting.Helpers;
using System.Text;
using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class TaxFilingExportService : ITaxFilingExportService
{
    private readonly AccountingDbContext _db;
    private readonly ITaxService _taxService;

    public TaxFilingExportService(AccountingDbContext db, ITaxService taxService)
    {
        _db = db;
        _taxService = taxService;
    }

    // ภาษาไทยใน RD/SSO e-Filing portal ใช้ TIS-620 หรือ UTF-8 ไม่มี BOM —
    // BOM บางครั้ง trip parser ของพวกเขาให้เห็น phantom char ในคอลัมน์แรก.
    private static byte[] AsBytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>คำนำหน้าตามรหัสกรมสรรพากร: 1=นาย, 2=นาง, 3=น.ส., 4=บริษัท,
    /// 5=ห้างหุ้นส่วน, 6=คณะบุคคล, 7=มูลนิธิ/สมาคม, 9=อื่นๆ.</summary>
    private static string TitleCode(string? title) => (title ?? "").Trim() switch
    {
        "นาย" or "Mr." or "Mr" => "1",
        "นาง" or "Mrs." or "Mrs" => "2",
        "นางสาว" or "น.ส." or "Miss" or "Ms." or "Ms" => "3",
        "บริษัท" or "บจก." or "บมจ." or "Co., Ltd." or "Co.,Ltd." => "4",
        "หจก." or "ห้างหุ้นส่วนจำกัด" => "5",
        "คณะบุคคล" => "6",
        "มูลนิธิ" or "สมาคม" => "7",
        _ => "9",
    };

    // =====================================================================
    // ภ.ง.ด.1 — Monthly Salary Withholding Tax (e-Filing text format)
    // Format: H + D… + T (trailer with totals); pipe-delimited
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd1Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var payrollRuns = await _db.PayrollRuns
            .Include(p => p.Details).ThenInclude(d => d.Employee)
            .Where(p => p.CompanyId == companyId && p.Year == year && p.Month == month
                && p.Status != "Draft" && p.Status != "Voided")
            .ToListAsync();

        var sb = new StringBuilder();
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";

        // ใช้ TaxableGross ถ้ามี (รายได้ที่นำมาคำนวณ WHT — Gross −
        // สวัสดิการยกเว้นภาษี). Fallback GrossIncome สำหรับข้อมูลเก่า
        // ที่ยังไม่ migrate.
        decimal IncomeForTax(PayrollDetail d) => d.TaxableGross > 0 ? d.TaxableGross : d.GrossIncome;
        // ภ.ง.ด.1 แสดงพนักงานทุกคนที่ได้รับเงินได้ ม.40(1) ในงวด — รวมคนที่ภาษี
        // หัก ณ ที่จ่าย = 0 (เงินเดือนต่ำกว่าเกณฑ์) ด้วย. เดิมกรอง WithholdingTax > 0
        // → บริษัทที่ทุกคนเงินเดือนต่ำกว่าเกณฑ์ได้ไฟล์ว่าง (T|0) ทั้งที่มีพนักงานจริง
        // และไม่ตรงกับหน้ารายงาน (GeneratePnd1Async แสดงทุกคน). แสดงเงินได้ + ภาษี 0
        // = เปิดเผยครบถ้วน ปลอดภัยกับสรรพากร (ยอดภาษีนำส่งยังถูก = ผลรวมที่หักจริง)
        var allDetails = payrollRuns.SelectMany(p => p.Details)
            .Where(d => IncomeForTax(d) > 0).ToList();
        var totalIncome = allDetails.Sum(IncomeForTax);
        var totalTax = allDetails.Sum(d => d.WithholdingTax);

        // Header: H|TaxId|BranchCode|FormCode|Period(YYYYMM)|TotalRecords|TotalIncome|TotalTax
        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.1|{period}|{allDetails.Count}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var detail in allDetails.OrderBy(d => d.Employee.EmployeeCode))
        {
            var emp = detail.Employee;
            var payDate = payrollRuns.First(p => p.Details.Contains(detail)).PayDate;
            var payDateThai = $"{payDate.Day:D2}/{payDate.Month:D2}/{payDate.Year + 543}";
            // D|Seq|TitleCode|FirstName|LastName|CitizenId|PaymentDate|IncomeType(1=§40(1))|Income|Tax|Condition(1=หักภาษี ณ ที่จ่าย)
            var income = IncomeForTax(detail);
            sb.AppendLine($"D|{seq++}|{TitleCode(emp.TitleTh)}|{emp.FirstNameTh}|{emp.LastNameTh}|{emp.CitizenId ?? emp.TaxId}|{payDateThai}|1|{income:F2}|{detail.WithholdingTax:F2}|1");
        }

        // Trailer: T|TotalRecords|TotalIncome|TotalTax — required by RD parser
        sb.AppendLine($"T|{allDetails.Count}|{totalIncome:F2}|{totalTax:F2}");

        return new TaxFilingExportResult(
            "PND1", "ภ.ง.ด.1", $"PND1_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            allDetails.Count, totalIncome, totalTax,
            $"ภ.ง.ด.1 เดือน {month}/{year} จำนวน {allDetails.Count} คน ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.ง.ด.3 — Service WHT for individuals
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd3Async(Guid companyId, int year, int month)
    {
        // ไฟล์นำเข้าเว็บสรรพากรเป็น detail rows ล้วน — ไม่ต้องใช้ข้อมูลบริษัท/
        // งวดใน body (อยู่บนหน้าเว็บที่ผู้ใช้เลือกก่อน upload อยู่แล้ว)
        var certs = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)   // ไม่ Include PayeeContact — hydrate แยก (กัน INNER JOIN ตัดpayee ภ.ง.ด.3/1ก)
            .Where(w => w.CompanyId == companyId
                && w.TaxFormType == TaxType.WithholdingTax3
                && w.TaxYear == year && w.TaxMonth == month
                // เฉพาะใบที่ "ออกแล้ว" — เดิม != Voided ทำให้ใบร่าง (ยังไม่ออกให้
                // ผู้ถูกหัก) หลุดเข้าไฟล์ยื่น RD ⇒ นำส่งภาษีของใบที่อาจถูกทิ้ง +
                // ยอดไฟล์ไม่ตรงรายงานบนจอ (รายงานนับเฉพาะ Issued/Printed)
                && (w.Status == WithholdingTaxCertStatus.Issued
                    || w.Status == WithholdingTaxCertStatus.Printed))
            .ToListAsync();
        await _db.HydratePayeeContactsAsync(companyId, certs);

        var totalIncome = certs.Sum(c => c.TotalIncomeAmount);
        var totalTax = certs.Sum(c => c.TotalTaxAmount);
        var rows = BuildPndRows(certs, juristicPayee: false);
        var body = PndTextFileFormat.Build(rows);

        return new TaxFilingExportResult(
            "PND3", "ภ.ง.ด.3", $"PND3_{year}{month:D2}.txt", "text/plain", AsBytes(body),
            certs.Count, totalIncome, totalTax,
            $"ภ.ง.ด.3 เดือน {month}/{year} จำนวน {certs.Count} ราย ภาษีรวม {totalTax:N2} บาท "
            + $"· {rows.Count} บรรทัด (ไม่มี header — นำเข้าเว็บสรรพากรได้ทันที)");
    }

    // =====================================================================
    // ภ.ง.ด.53 — Service WHT for companies
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd53Async(Guid companyId, int year, int month)
    {
        // ไฟล์นำเข้าเว็บสรรพากรเป็น detail rows ล้วน — ไม่ต้องใช้ข้อมูลบริษัท/
        // งวดใน body (อยู่บนหน้าเว็บที่ผู้ใช้เลือกก่อน upload อยู่แล้ว)
        var certs = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)   // ไม่ Include PayeeContact — hydrate แยก (กัน INNER JOIN ตัดpayee ภ.ง.ด.53/3ก)
            .Where(w => w.CompanyId == companyId
                && w.TaxFormType == TaxType.WithholdingTax53
                && w.TaxYear == year && w.TaxMonth == month
                // เฉพาะใบที่ "ออกแล้ว" — เดิม != Voided ทำให้ใบร่างหลุดเข้าไฟล์ยื่น
                // RD + ยอดไฟล์ไม่ตรงรายงานบนจอ (รายงานนับเฉพาะ Issued/Printed)
                && (w.Status == WithholdingTaxCertStatus.Issued
                    || w.Status == WithholdingTaxCertStatus.Printed))
            .ToListAsync();
        await _db.HydratePayeeContactsAsync(companyId, certs);

        var totalIncome = certs.Sum(c => c.TotalIncomeAmount);
        var totalTax = certs.Sum(c => c.TotalTaxAmount);
        var rows = BuildPndRows(certs, juristicPayee: true);
        var body = PndTextFileFormat.Build(rows);

        return new TaxFilingExportResult(
            "PND53", "ภ.ง.ด.53", $"PND53_{year}{month:D2}.txt", "text/plain", AsBytes(body),
            certs.Count, totalIncome, totalTax,
            $"ภ.ง.ด.53 เดือน {month}/{year} จำนวน {certs.Count} ราย ภาษีรวม {totalTax:N2} บาท "
            + $"· {rows.Count} บรรทัด (ไม่มี header — นำเข้าเว็บสรรพากรได้ทันที)");
    }

    // =====================================================================
    // ภ.ง.ด.1ก — Annual Salary Summary (พร้อมวันเริ่ม/สิ้นสุดงาน)
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd1kAsync(Guid companyId, int year)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var yearStart = new DateTime(year, 1, 1);
        var yearEnd = new DateTime(year, 12, 31);

        var payrollDetails = await _db.PayrollDetails
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .Where(d => d.CompanyId == companyId && d.PayrollRun.Year == year
                && d.PayrollRun.Status != "Draft" && d.PayrollRun.Status != "Voided")
            .ToListAsync();

        // ใช้ TaxableGross ถ้ามี (รายได้ที่ใช้คำนวณ WHT จริง — Gross
        // หัก สวัสดิการยกเว้นภาษี). Fallback GrossIncome สำหรับข้อมูลเก่า.
        var empGroups = payrollDetails
            .GroupBy(d => d.EmployeeId)
            .Select(g => new
            {
                Employee = g.First().Employee,
                TotalIncome = g.Sum(d => d.TaxableGross > 0 ? d.TaxableGross : d.GrossIncome),
                TotalTax = g.Sum(d => d.WithholdingTax),
                TotalSSO = g.Sum(d => d.SocialSecurityEmployee),
                TotalPVD = g.Sum(d => d.ProvidentFundEmployee),
                MonthCount = g.Select(d => d.PayrollRun.Month).Distinct().Count(),
            })
            .Where(e => e.TotalIncome > 0)
            .OrderBy(e => e.Employee.EmployeeCode)
            .ToList();

        var sb = new StringBuilder();
        var totalIncome = empGroups.Sum(e => e.TotalIncome);
        var totalTax = empGroups.Sum(e => e.TotalTax);

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.1ก|{thaiYear:D4}|{empGroups.Count}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var emp in empGroups)
        {
            var e = emp.Employee;
            // คอลัมน์ครบตาม RD template 2566: TitleCode, FirstName, LastName,
            // CitizenId, StartDate, EndDate, MonthsEmployed, Income, PVD, SSO,
            // OtherExempt(ตามมาตรา 42), TaxableBase, Tax, Condition
            var start = e.StartDate < yearStart ? yearStart : e.StartDate;
            var end = e.EndDate.HasValue && e.EndDate.Value < yearEnd ? e.EndDate.Value : yearEnd;
            var startThai = $"{start.Day:D2}/{start.Month:D2}/{start.Year + 543}";
            var endThai = $"{end.Day:D2}/{end.Month:D2}/{end.Year + 543}";
            var taxableBase = Math.Max(0, emp.TotalIncome - emp.TotalSSO - emp.TotalPVD);
            sb.AppendLine($"D|{seq++}|{TitleCode(e.TitleTh)}|{e.FirstNameTh}|{e.LastNameTh}|{e.CitizenId ?? e.TaxId}|{startThai}|{endThai}|{emp.MonthCount}|1|{emp.TotalIncome:F2}|{emp.TotalPVD:F2}|{emp.TotalSSO:F2}|0.00|{taxableBase:F2}|{emp.TotalTax:F2}|1");
        }
        sb.AppendLine($"T|{empGroups.Count}|{totalIncome:F2}|{totalTax:F2}");

        return new TaxFilingExportResult(
            "PND1K", "ภ.ง.ด.1ก", $"PND1K_{year}.txt", "text/plain", AsBytes(sb.ToString()),
            empGroups.Count, totalIncome, totalTax,
            $"ภ.ง.ด.1ก ปี {year} จำนวน {empGroups.Count} คน รายได้รวม {totalIncome:N2} ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.พ.30 — VAT Filing → คืน 2 CSV (Sale + Purchase) ในไฟล์ Zip
    // RD ภ.พ.30 upload format = strict CSV ไม่ใช่ narrative report.
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPp30Async(Guid companyId, int year, int month)
    {
        var thaiYear = year + 543;

        // ⬇️ Single source of truth: **รายงานที่ผู้ใช้บันทึก/ติ๊กไว้จริง** ก่อน —
        // เดิมเรียก ComputeVatReportAsync (คำนวณสดจากศูนย์) เสมอ ⇒ สิ่งที่ผู้ใช้
        // ทำบนจอ (ติ๊ก "ใช้" บรรทัดยกมา §82/3, ดึงเอกสารเข้า, ติ๊กใบซ้ำออก)
        // **ไม่มีผลต่อไฟล์ที่ยื่น RD เลย**: บรรทัดยกมากลับเป็น excluded → ภาษีซื้อ
        // ขาด, ใบที่ดึงเข้าหายทั้งใบ, ใบที่ติ๊กออกกลับมา. compute สดเฉพาะเมื่อ
        // งวดนั้นยังไม่เคยสร้างรายงาน
        var persisted = await _db.TaxReports.AsNoTracking()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.CompanyId == companyId
                && r.TaxType == TaxType.VAT && r.Year == year && r.Month == month);
        var report = persisted ?? await _taxService.ComputeVatReportAsync(companyId, year, month);
        var lines = report.Lines.OrderBy(l => l.LineOrder).ToList();

        // เติมเลขใบกำกับผู้ขาย + สาขา จาก Document (TaxReportLine เก็บแต่ DocumentId).
        var docIds = lines.Where(l => l.DocumentId.HasValue).Select(l => l.DocumentId!.Value).Distinct().ToList();
        var docInfo = docIds.Count == 0
            ? new Dictionary<Guid, (string? SupplierInvoiceNo, string? BranchCode)>()
            : await (from d in _db.Documents.AsNoTracking()
                     where d.CompanyId == companyId && docIds.Contains(d.Id)
                     join c in _db.Contacts.AsNoTracking() on d.ContactId equals c.Id into cj
                     from c in cj.DefaultIfEmpty()
                     select new { d.Id, d.SupplierInvoiceNumber, Snapshot = d.SupplierBranchCode, ContactBranch = c != null ? c.BranchCode : null,
                                  d.IsForeignService, d.Pp36RdReceiptNumber })
                .ToDictionaryAsync(x => x.Id, x => (
                    // §86/14 — ใบ ภ.พ.36: เลขใบกำกับในไฟล์ยื่น = เลขใบเสร็จ RD
                    // (ไม่ใช่ invoice ผู้ขาย ตปท.) — ชุดเดียวกับจอ (GetTaxReportAsync)
                    SupplierInvoiceNo: (string?)(x.IsForeignService && !string.IsNullOrWhiteSpace(x.Pp36RdReceiptNumber)
                        ? x.Pp36RdReceiptNumber : x.SupplierInvoiceNumber),
                    BranchCode: (string?)(x.Snapshot ?? x.ContactBranch)));

        // CSV escape: ห่อ "..." ถ้ามี , หรือ " หรือขึ้นบรรทัด — RD parser ปฏิบัติตาม RFC 4180.
        static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        // ใช้ปีของ "วันที่เอกสารจริง" ไม่ใช่ปีของงวด — ใบกำกับยกมาข้ามปี
        // (§82/3 ภายใน 6 เดือน) เช่นใบ ธ.ค. 2568 เคลมงวด ม.ค. 2569 ต้องพิมพ์
        // 15/12/2568 ไม่ใช่ 15/12/2569
        string Date(DateTime t) => $"{t.Day:D2}/{t.Month:D2}/{t.Year + 543}";

        // แยกฝั่งด้วยตัวจัด side ตัวเดียวกับ Excel (TaxService.LineSide) — กัน
        // CN/DN ฝั่งซื้อหลุดไปฝั่งขาย. ตัด IsExcluded ออกจากไฟล์ยื่นทั้งสองฝั่ง:
        // ฝั่งขายมีบรรทัดเตือน/audit (เช่น "ยื่นงวดก่อนแล้ว") ที่ไม่ใช่รายการขาย
        // ของงวด — เดิมกรองแค่ฝั่งซื้อ ทำให้ Sale.csv มีแถวเกินและไม่ reconcile
        // กับยอดสรุป ภ.พ.30
        var saleLines = lines.Where(l => TaxService.LineSide(l) == "output" && !l.IsExcluded).ToList();
        var purchaseLines = lines.Where(l => TaxService.LineSide(l) == "input" && !l.IsExcluded).ToList();

        var sale = new StringBuilder();
        sale.AppendLine("ลำดับ,วันที่,เลขที่ใบกำกับ,ชื่อผู้ซื้อ,เลขประจำตัวผู้เสียภาษี,สาขา,มูลค่าสินค้า/บริการ,จำนวนภาษี");
        int s1 = 1;
        foreach (var l in saleLines)
        {
            var branch = (l.DocumentId.HasValue && docInfo.TryGetValue(l.DocumentId.Value, out var di) ? di.BranchCode : null) ?? "00000";
            // เลขที่ใบกำกับ = Description (มีเลขเอกสาร + ป้าย CN/DN) เพื่อให้ตรงกับจอ
            sale.AppendLine($"{s1++},{Date(l.TransactionDate)},{Csv(l.Description ?? "")},{Csv(l.TaxPayerName ?? "")},{l.TaxPayerId ?? ""},{branch},{l.IncomeAmount:F2},{l.TaxAmount:F2}");
        }

        var purchase = new StringBuilder();
        purchase.AppendLine("ลำดับ,วันที่,เลขที่ใบกำกับ,ชื่อผู้ขาย,เลขประจำตัวผู้เสียภาษี,สาขา,มูลค่าสินค้า/บริการ,จำนวนภาษี");
        int p1 = 1;
        foreach (var l in purchaseLines)
        {
            string? supplierNo = null, branch = null;
            if (l.DocumentId.HasValue && docInfo.TryGetValue(l.DocumentId.Value, out var di))
            { supplierNo = di.SupplierInvoiceNo; branch = di.BranchCode; }
            // เลขที่ใบกำกับ = เลขบนใบผู้ขาย (RD ต้องการเลขจริง) — fallback Description
            var invNo = !string.IsNullOrWhiteSpace(supplierNo) ? supplierNo! : (l.Description ?? "");
            purchase.AppendLine($"{p1++},{Date(l.TransactionDate)},{Csv(invNo)},{Csv(l.TaxPayerName ?? "")},{l.TaxPayerId ?? ""},{branch ?? "00000"},{l.IncomeAmount:F2},{l.TaxAmount:F2}");
        }

        // ยอดรวม = ค่าจาก report (รวม CN/DN, หัก §82/5, รวมเครดิตยกมา) → ตรงกับจอ.
        var summary = new StringBuilder();
        summary.AppendLine("รายการ,จำนวน");
        summary.AppendLine($"ภาษีขาย (Output VAT),{report.OutputVat:F2}");
        summary.AppendLine($"ภาษีซื้อ (Input VAT),{report.InputVat:F2}");
        summary.AppendLine($"ภาษีที่ต้องชำระ/ขอคืน (Net VAT),{report.NetVat:F2}");

        // §87(3) Chronological — flag เอกสารที่ tax point ย้อนกลับ
        DateTime? prevDate = null;
        int outOfOrderCount = 0;
        foreach (var l in saleLines.Concat(purchaseLines))
        {
            if (prevDate.HasValue && l.TransactionDate < prevDate.Value) outOfOrderCount++;
            prevDate = l.TransactionDate;
        }
        summary.AppendLine($"เอกสารเรียงเวลาย้อนกลับ (§87(3) chronological),{outOfOrderCount}");

        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteZipEntryAsync(archive, "Sale.csv", sale.ToString());
            await WriteZipEntryAsync(archive, "Purchase.csv", purchase.ToString());
            await WriteZipEntryAsync(archive, "Summary.csv", summary.ToString());
        }

        var netVat = report.NetVat;
        var totalBase = saleLines.Sum(l => l.IncomeAmount) + purchaseLines.Sum(l => l.IncomeAmount);
        return new TaxFilingExportResult(
            "PP30", "ภ.พ.30", $"PP30_{year}{month:D2}.zip", "application/zip", zipStream.ToArray(),
            saleLines.Count + purchaseLines.Count, totalBase, netVat,
            $"ภ.พ.30 เดือน {month}/{year} ภาษีขาย {report.OutputVat:N2} ภาษีซื้อ {report.InputVat:N2} สุทธิ {netVat:N2} บาท (Sale.csv + Purchase.csv + Summary.csv)");
    }

    private static async Task WriteZipEntryAsync(System.IO.Compression.ZipArchive archive, string name, string content)
    {
        var e = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        await using var stream = e.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
    }

    // =====================================================================
    // สปส.1-10 — SSO Monthly Contribution Report
    // RD spec: H|TaxId|BranchCode|CompanyName|BranchSeq|RatePercent|Period|TotalRecords
    // D|Seq|CitizenId|SSNumber|TitleCode|FirstName|LastName|WageBase|EmpContrib|ErContrib
    // T|TotalRecords|TotalWages|TotalEmpContrib|TotalErContrib|TotalContrib|RatePercent
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportSso110Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";

        var payrollRuns = await _db.PayrollRuns
            .Include(p => p.Details).ThenInclude(d => d.Employee)
            .Where(p => p.CompanyId == companyId && p.Year == year && p.Month == month
                && p.Status != "Draft" && p.Status != "Voided")
            .ToListAsync();

        var allDetails = payrollRuns
            .SelectMany(p => p.Details)
            .Where(d => d.Employee.IsSubjectToSocialSecurity && d.SocialSecurityEmployee > 0)
            .OrderBy(d => d.Employee.EmployeeCode)
            .ToList();

        // SSO wage ceiling/rate per year (15,000 → 17,500 ปี 2026 → ...)
        var ssoCfg = await _db.SsoYearConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Year == year && !c.IsDeleted);
        var wageCeiling = ssoCfg?.WageCeiling
            ?? Accounting.Helpers.SsoRateSchedule.GetDefault(year).WageCeiling;
        var ratePercent = ssoCfg?.RatePercent
            ?? (Accounting.Helpers.SsoRateSchedule.GetDefault(year).Rate * 100m);

        var sb = new StringBuilder();
        var totalWages = allDetails.Sum(d => Math.Min(d.GrossIncome, wageCeiling));
        var totalEmpContrib = allDetails.Sum(d => d.SocialSecurityEmployee);
        var totalErContrib = allDetails.Sum(d => d.SocialSecurityEmployer);
        var branchSeq = company.BranchCode ?? "00000";

        // Header (SSO portal spec): TaxId | BranchCode | CompanyName | BranchSeq | RatePercent | Period(YYYYMM) | TotalRecords
        sb.AppendLine($"H|{company.TaxId}|{branchSeq}|{company.Name}|{branchSeq}|{ratePercent:F2}|{period}|{allDetails.Count}");

        int seq = 1;
        foreach (var detail in allDetails)
        {
            var emp = detail.Employee;
            var wageBase = Math.Min(detail.GrossIncome, wageCeiling);
            sb.AppendLine($"D|{seq++}|{emp.CitizenId}|{emp.SocialSecurityNumber}|{TitleCode(emp.TitleTh)}|{emp.FirstNameTh}|{emp.LastNameTh}|{wageBase:F2}|{detail.SocialSecurityEmployee:F2}|{detail.SocialSecurityEmployer:F2}");
        }

        // Trailer with rate echo + total contribution (เดิมขาด rate echo)
        sb.AppendLine($"T|{allDetails.Count}|{totalWages:F2}|{totalEmpContrib:F2}|{totalErContrib:F2}|{totalEmpContrib + totalErContrib:F2}|{ratePercent:F2}");

        return new TaxFilingExportResult(
            "SSO110", "สปส.1-10", $"SSO110_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            allDetails.Count, totalWages, totalEmpContrib + totalErContrib,
            $"สปส.1-10 เดือน {month}/{year} จำนวน {allDetails.Count} คน สมทบรวม {totalEmpContrib + totalErContrib:N2} บาท (ลูกจ้าง {totalEmpContrib:N2} + นายจ้าง {totalErContrib:N2}) อัตรา {ratePercent:F2}%");
    }

    // =====================================================================
    // สปส.1-10 (Excel) — ไฟล์แนบ "ส่งข้อมูลเงินสมทบ" ใน SSO e-Service
    //
    // โครงสร้างถอดแบบจากไฟล์จริงที่อัปโหลดผ่านระบบ สปส. สำเร็จ (ก.ค. 2569):
    //   • sheet เดียว ชื่อ sheet = ลำดับที่สาขา 6 หลัก (สำนักงานใหญ่ = "000000")
    //   • 6 คอลัมน์: เลขประจำตัวประชาชน (text) | คำนำหน้าชื่อ | ชื่อผู้ประกันตน |
    //     นามสกุลผู้ประกันตน | ค่าจ้าง (ตัวเลข, ค่าจ้างจริงไม่ cap เพดาน) |
    //     จำนวนเงินสมทบ (ตัวเลข, เฉพาะฝั่งลูกจ้าง ปัดเป็นบาทถ้วนตามที่หักจริง)
    //   • ไม่มีแถวรวม/ข้อมูลบริษัท — หน้าเว็บ สปส. กรอกเลขบัญชีนายจ้าง/งวด/อัตราเอง
    //
    // หมายเหตุ: ไฟล์ .txt แบบ fixed-width (126/135 ตัวอักษร) เป็นคนละรูปแบบ —
    // Excel เป็นช่องทางที่ระบบ e-Service รองรับและทดสอบผ่านแล้ว จึงใช้เป็นหลัก
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportSso110ExcelAsync(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;

        var payrollRuns = await _db.PayrollRuns
            .Include(p => p.Details).ThenInclude(d => d.Employee)
            .Where(p => p.CompanyId == companyId && p.Year == year && p.Month == month
                && p.Status != "Draft" && p.Status != "Voided")
            .ToListAsync();

        var allDetails = payrollRuns
            .SelectMany(p => p.Details)
            .Where(d => d.Employee.IsSubjectToSocialSecurity && d.SocialSecurityEmployee > 0)
            .OrderBy(d => d.Employee.EmployeeCode)
            .ToList();

        // ลำดับที่สาขา สปส. = 6 หลัก (RD BranchCode 5 หลัก pad ซ้ายด้วย 0)
        var branchSeq = (company.BranchCode ?? "00000").Trim().PadLeft(6, '0');
        if (branchSeq.Length > 6) branchSeq = branchSeq[^6..];

        // ป้องกันโดนปฏิเสธรายแถวจาก e-Service — ตรวจ + ซ่อมข้อมูลก่อนเขียนไฟล์:
        //   • คำนำหน้าอังกฤษ (Mrs./Mr./Miss หลุดมาจาก import) → แปลงไทย,
        //     เดาจากเพศเมื่อจำเป็น; ยังไม่เข้าชุดที่ สปส. รับ → แจ้งเตือน
        //   • เลขบัตร: ตัดขีด/ช่องว่างเหลือแต่ตัวเลข; ไม่ครบ 13 หลัก → แจ้งเตือน
        //   • ชื่อ/นามสกุลว่าง → แจ้งเตือน
        var issues = new List<string>();
        var rows = new List<Dictionary<string, object?>>();
        var rowNo = 1; // แถวข้อมูลใน Excel เริ่มที่ 2 (แถว 1 = หัวตาราง)
        foreach (var d in allDetails)
        {
            rowNo++;
            var emp = d.Employee;
            var title = Accounting.Helpers.ThaiTitleHelper.NormalizeForSso(emp.TitleTh, emp.Gender);
            var citizenId = new string((emp.CitizenId ?? "").Where(char.IsDigit).ToArray());
            var firstName = (emp.FirstNameTh ?? "").Trim();
            var lastName = (emp.LastNameTh ?? "").Trim();

            if (!Accounting.Helpers.ThaiTitleHelper.IsValidForSso(title))
                issues.Add($"แถว {rowNo} ({firstName}): คำนำหน้า \"{(string.IsNullOrWhiteSpace(title) ? "(ว่าง)" : title)}\" ไม่อยู่ในชุดที่ สปส. รับ (นาย/นาง/นางสาว) — แก้ที่ข้อมูลพนักงาน");
            if (citizenId.Length != 13)
                issues.Add($"แถว {rowNo} ({firstName}): เลขบัตร {citizenId.Length} หลัก (ต้อง 13 หลัก)");
            if (firstName.Length == 0 || lastName.Length == 0)
                issues.Add($"แถว {rowNo}: ชื่อหรือนามสกุลว่าง");

            rows.Add(new Dictionary<string, object?>
            {
                ["เลขประจำตัวประชาชน"] = citizenId,
                ["คำนำหน้าชื่อ"] = title,
                ["ชื่อผู้ประกันตน"] = firstName,
                ["นามสกุลผู้ประกันตน"] = lastName,
                ["ค่าจ้าง"] = Math.Round(d.GrossIncome, 2),
                ["จำนวนเงินสมทบ"] = Math.Round(d.SocialSecurityEmployee, 2),
            });
        }

        using var ms = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, rows, sheetName: branchSeq);

        var totalWages = allDetails.Sum(d => d.GrossIncome);
        var totalEmpContrib = allDetails.Sum(d => d.SocialSecurityEmployee);
        var summary = $"ไฟล์ Excel แนบ e-Service เดือน {month}/{year} ผู้ประกันตน {allDetails.Count} คน " +
            $"เงินสมทบลูกจ้าง {totalEmpContrib:N2} บาท (sheet: {branchSeq})";
        if (issues.Count > 0)
            summary += "\n⚠ ควรแก้ก่อนยื่น มิฉะนั้น สปส. จะปฏิเสธรายแถว:\n• " +
                string.Join("\n• ", issues.Take(8)) +
                (issues.Count > 8 ? $"\n• …และอีก {issues.Count - 8} รายการ" : "");

        return new TaxFilingExportResult(
            "SSO110X", "สปส.1-10 (Excel)",
            $"SocialSecurity_{thaiYear}_{month:D2}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ms.ToArray(),
            allDetails.Count, totalWages, totalEmpContrib,
            summary);
    }

    // =====================================================================
    // สปส.1-03 — ขึ้นทะเบียนผู้ประกันตน (พนักงานเข้าใหม่ภายในเดือนนั้น)
    // กฎหมาย: นายจ้างต้องแจ้งภายใน 30 วันนับจากวันเริ่มงาน (พ.ร.บ.ประกันสังคม §34)
    // Layout (pipe-delimited, อ้างอิงโครงสร้าง portal e-Service):
    //   H|TaxId|BranchCode|CompanyName|Period(YYYYMM)|TotalRecords
    //   D|Seq|CitizenId|TitleCode|FirstName|LastName|StartDate(yyyyMMdd)|Salary|HospitalPref
    //   T|TotalRecords
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportSps103Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";
        var periodStart = new DateTime(year, month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        // พนักงานที่ "เริ่มงาน" ภายในเดือนนั้น + อยู่ในระบบประกันสังคม
        var newEmployees = await _db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted
                && e.IsSubjectToSocialSecurity
                && e.StartDate >= periodStart && e.StartDate <= periodEnd)
            .OrderBy(e => e.StartDate).ThenBy(e => e.EmployeeCode)
            .ToListAsync();

        var sb = new StringBuilder();
        var branchSeq = company.BranchCode ?? "00000";
        sb.AppendLine($"H|{company.TaxId}|{branchSeq}|{company.Name}|{period}|{newEmployees.Count}");
        int seq = 1;
        foreach (var emp in newEmployees)
        {
            sb.AppendLine($"D|{seq++}|{emp.CitizenId}|{TitleCode(emp.TitleTh)}|{emp.FirstNameTh}|{emp.LastNameTh}" +
                $"|{emp.StartDate:yyyyMMdd}|{emp.BaseSalary:F2}|{emp.SocialSecurityHospital}");
        }
        sb.AppendLine($"T|{newEmployees.Count}");

        return new TaxFilingExportResult(
            "SPS103", "สปส.1-03", $"SPS103_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            newEmployees.Count, newEmployees.Sum(e => e.BaseSalary), 0,
            $"สปส.1-03 (ขึ้นทะเบียนผู้ประกันตน) เดือน {month}/{year} จำนวน {newEmployees.Count} คน" +
            (newEmployees.Count > 0 ? $" — แจ้งภายใน 30 วันนับจากวันเริ่มงาน (§34)" : " — ไม่มีพนักงานเข้าใหม่"));
    }

    // =====================================================================
    // สปส.6-09 — แจ้งสิ้นสุดความเป็นผู้ประกันตน (พนักงานออกภายในเดือนนั้น)
    // กฎหมาย: นายจ้างต้องแจ้งภายในวันที่ 15 ของเดือนถัดจากเดือนที่ลาออก
    // Layout:
    //   H|TaxId|BranchCode|CompanyName|Period(YYYYMM)|TotalRecords
    //   D|Seq|CitizenId|SSNumber|TitleCode|FirstName|LastName|EndDate(yyyyMMdd)|ReasonCode
    //   T|TotalRecords
    // ReasonCode: 1=ลาออก, 2=เลิกจ้าง, 3=เกษียณ, 4=เสียชีวิต, 9=อื่นๆ (default 1)
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportSps609Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";
        var periodStart = new DateTime(year, month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        // พนักงานที่ "สิ้นสุดการจ้าง" (EndDate) ภายในเดือนนั้น + เคยอยู่ในระบบ สปส.
        var leavers = await _db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted
                && e.IsSubjectToSocialSecurity
                && e.EndDate != null && e.EndDate >= periodStart && e.EndDate <= periodEnd)
            .OrderBy(e => e.EndDate).ThenBy(e => e.EmployeeCode)
            .ToListAsync();

        var sb = new StringBuilder();
        var branchSeq = company.BranchCode ?? "00000";
        sb.AppendLine($"H|{company.TaxId}|{branchSeq}|{company.Name}|{period}|{leavers.Count}");
        int seq = 1;
        foreach (var emp in leavers)
        {
            sb.AppendLine($"D|{seq++}|{emp.CitizenId}|{emp.SocialSecurityNumber}|{TitleCode(emp.TitleTh)}" +
                $"|{emp.FirstNameTh}|{emp.LastNameTh}|{emp.EndDate:yyyyMMdd}|1");
        }
        sb.AppendLine($"T|{leavers.Count}");

        return new TaxFilingExportResult(
            "SPS609", "สปส.6-09", $"SPS609_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            leavers.Count, 0, 0,
            $"สปส.6-09 (แจ้งออก) เดือน {month}/{year} จำนวน {leavers.Count} คน" +
            (leavers.Count > 0 ? " — แจ้งภายในวันที่ 15 ของเดือนถัดไป" : " — ไม่มีพนักงานออก"));
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task<Company> GetCompanyAsync(Guid companyId)
    {
        return await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
    }

    /// <summary>Map internal income type code to กรมสรรพากร standard code.
    /// §40(7) ค่ารับเหมา / §40(8) ค่าบริการ → "6" (ค่าจ้างทำของ/รับเหมา
    /// ภายใต้มาตรา 3 เตรส). §40(5) ค่าเช่าทรัพย์สิน → "5". เดิมรวมทั้ง 3
    /// เป็น "5" ทำให้ RD audit mismatch.</summary>
    /// <summary>cert (+ บรรทัด) → แถวไฟล์ ภ.ง.ด. — ตัวแปลงเดียวใช้ทั้ง 3/53/54
    /// เพื่อให้ทุกแบบได้ layout เดียวกัน (PndTextFileFormat)</summary>
    private static List<PndTextFileFormat.Row> BuildPndRows(
        IEnumerable<WithholdingTaxCert> certs, bool juristicPayee)
    {
        var rows = new List<PndTextFileFormat.Row>();
        foreach (var cert in certs.OrderBy(c => c.CertificateNumber))
        {
            var contact = cert.PayeeContact;
            // เงื่อนไขการหักภาษี (Col11): 1 = หัก ณ ที่จ่าย, 2 = ออกให้ตลอดไป
            var condition = (int)cert.CertificateType is 2 ? 2 : 1;
            var certLines = cert.Lines?.OrderBy(l => l.LineOrder).ToList()
                ?? new List<WithholdingTaxCertLine>();

            if (certLines.Count == 0)
            {
                // ⚠️ ใบที่ไม่มีบรรทัดย่อย (เช่นที่ระบบอื่นสร้างจากยอดรวม) เดิม
                // **ไม่ได้แถวในไฟล์เลย** ทั้งที่หัวสรุปยังนับใบนี้เข้า "จำนวนราย/
                // ภาษีรวม" และรายงานบนจอ (TaxService) ก็แสดงเป็นบรรทัดปกติ ⇒
                // ผู้ใช้เห็นยอดตรงบนจอ แต่ไฟล์ที่อัปโหลดเข้าเว็บสรรพากรนำส่งขาด
                // เงียบ ๆ (T-5). ลงเป็นแถวเดียวจากยอดรวมของใบ ให้ จอ = ไฟล์ = หัวสรุป
                rows.Add(new PndTextFileFormat.Row(
                    PayeeTaxId: contact?.TaxId,
                    BranchCode: contact?.BranchCode,
                    PayeeName: contact?.Name,
                    IsJuristic: juristicPayee,
                    PayDate: cert.IssuedDate ?? new DateTime(cert.TaxYear, cert.TaxMonth, 1),
                    IncomeTypeCode: MapIncomeTypeCode(null),
                    IncomeAmount: cert.TotalIncomeAmount,
                    TaxRate: cert.TotalIncomeAmount > 0
                        ? Math.Round(cert.TotalTaxAmount / cert.TotalIncomeAmount * 100m,
                            2, MidpointRounding.AwayFromZero)
                        : 0m,
                    TaxAmount: cert.TotalTaxAmount,
                    Condition: condition));
                continue;
            }

            foreach (var line in certLines)
                rows.Add(new PndTextFileFormat.Row(
                    PayeeTaxId: contact?.TaxId,
                    BranchCode: contact?.BranchCode,
                    PayeeName: contact?.Name,
                    IsJuristic: juristicPayee,
                    PayDate: line.PaymentDate,
                    IncomeTypeCode: MapIncomeTypeCode(line.IncomeTypeCode),
                    IncomeAmount: line.IncomeAmount,
                    TaxRate: line.TaxRate,
                    TaxAmount: line.TaxAmount,
                    Condition: condition));
        }
        return rows;
    }

    private static string MapIncomeTypeCode(string? code) => code switch
    {
        "1" or "40(1)" => "1",
        "2" or "40(2)" => "2",
        "3" or "40(3)" => "3",
        "4a" or "40(4)a" or "40(4)(a)" => "4A",
        "4b" or "40(4)b" or "40(4)(b)" => "4B",
        "5" or "40(5)" => "5",
        "6" or "40(6)" => "6",
        "7" or "40(7)" => "6",  // ค่ารับเหมา → §3เตรส ค่าจ้างทำของ
        "8" or "40(8)" => "6",  // ค่าบริการ → §3เตรส ค่าจ้างทำของ
        _ => "6"
    };

    // =====================================================================
    // ภ.ง.ด.91 — Annual personal income tax summary per employee
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd91Async(Guid companyId, int year)
    {
        var company = await GetCompanyAsync(companyId);
        // B10: ประชากรต้องตรงกับรายงานบนจอ (GeneratePnd91Report ใช้
        // Approved/Paid เท่านั้น) — เดิมไฟล์ใช้ "ไม่ใช่ Draft/Voided" ⇒ รอบที่
        // ถูกปฏิเสธ/รออนุมัติหลุดเข้าไฟล์ยื่นทั้งที่จอไม่นับ = จอ ≠ ไฟล์
        var runs = await _db.PayrollRuns
            .Include(p => p.Details).ThenInclude(d => d.Employee)
            .Where(p => p.CompanyId == companyId && p.Year == year
                        && (p.Status == "Approved" || p.Status == "Paid"))
            .ToListAsync();
        if (runs.Count == 0)
            throw new InvalidOperationException($"ไม่มี PayrollRun สำหรับปี {year}");

        var sb = new StringBuilder();
        var thaiYear = year + 543;

        var byEmployee = runs.SelectMany(r => r.Details)
            .GroupBy(d => d.EmployeeId)
            .Select(g =>
            {
                var emp = g.First().Employee;
                return new
                {
                    Employee = emp,
                    YtdGross = g.Sum(d => d.GrossIncome),
                    YtdWht = g.Sum(d => d.WithholdingTax),
                    YtdSso = g.Sum(d => d.SocialSecurityEmployee),
                    YtdPf = g.Sum(d => d.ProvidentFundEmployee),
                    Months = g.Count(),
                };
            })
            .Where(x => x.YtdGross > 0)
            .OrderBy(x => x.Employee.EmployeeCode)
            .ToList();

        var totalIncome = byEmployee.Sum(x => x.YtdGross);
        var totalWht = byEmployee.Sum(x => x.YtdWht);

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.91|{thaiYear:D4}|{byEmployee.Count}|{totalIncome:F2}|{totalWht:F2}");

        int seq = 1;
        foreach (var x in byEmployee)
        {
            var emp = x.Employee;
            sb.AppendLine($"D|{seq++}|{TitleCode(emp.TitleTh)}|{emp.FirstNameTh}|{emp.LastNameTh}|{emp.CitizenId ?? emp.TaxId}|{x.Months}|{x.YtdGross:F2}|{x.YtdSso:F2}|{x.YtdPf:F2}|{x.YtdWht:F2}");
        }
        sb.AppendLine($"T|{byEmployee.Count}|{totalIncome:F2}|{totalWht:F2}");

        return new TaxFilingExportResult(
            "PND91", "ภ.ง.ด.91", $"PND91_{year}.txt", "text/plain", AsBytes(sb.ToString()),
            byEmployee.Count, totalIncome, totalWht,
            $"ภ.ง.ด.91 ประจำปี {year} จำนวน {byEmployee.Count} คน รายได้รวม {totalIncome:N2} บาท ภาษีรวม {totalWht:N2} บาท");
    }

    /// <summary>ภ.ง.ด.2 — Monthly dividend WHT. Multi-tier detection:
    ///   (1) JournalEntry.Tags ที่มี "DIVIDEND" / "PND2" / "ปันผล"
    ///       (CSV tag-based — แม่นที่สุด, ผู้ใช้/system ตั้งได้)
    ///   (2) JournalEntry.Description มีคำว่า "เงินปันผล" (heuristic)
    ///   (3) Account.AccountCode "21915" หรือ AccountName มี "ปันผล"
    ///       (ผังบัญชี-based — รองรับ custom code)
    /// รวม union ของทั้ง 3 → ลด miss สำหรับบริษัทที่ใช้ผังบัญชี custom.</summary>
    public async Task<TaxFilingExportResult> ExportPnd2Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";
        var monthStart = new DateTime(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        // หาบรรทัด JE ที่เกี่ยวกับเงินปันผล — multi-tier detection
        var lines = await _db.JournalEntryLines.AsNoTracking()
            .Include(l => l.JournalEntry).Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == Models.Enums.JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= monthStart && l.JournalEntry.EntryDate <= monthEnd
                && (
                    // (1) JE Tags
                    (l.JournalEntry.Tags != null && (
                        l.JournalEntry.Tags.Contains("DIVIDEND")
                        || l.JournalEntry.Tags.Contains("PND2")
                        || l.JournalEntry.Tags.Contains("ปันผล")))
                    // (2) Description heuristic
                    || (l.JournalEntry.Description != null && l.JournalEntry.Description.Contains("เงินปันผล"))
                    // (3) Account code/name
                    || (l.Account!.AccountCode == "21915"
                        || (l.Account.AccountName != null && l.Account.AccountName.Contains("ปันผล")))
                ))
            .ToListAsync();

        // Group by JE → 1 dividend payment per shareholder
        var byJe = lines.GroupBy(l => l.JournalEntryId)
            .Select(g => new {
                EntryId = g.Key,
                Date = g.First().JournalEntry.EntryDate,
                Description = g.First().JournalEntry.Description ?? "เงินปันผล",
                // WHT line = บัญชี WHT (21915 หรือ name มี "ปันผล" / "หัก ณ ที่จ่าย")
                WhtAmount = g.Where(x => x.Account != null && (
                        x.Account.AccountCode == "21915"
                        || (x.Account.AccountName != null && (x.Account.AccountName.Contains("ปันผล")
                            || x.Account.AccountName.Contains("หัก ณ ที่จ่าย")))))
                    .Sum(x => x.CreditAmount),
                // Dividend amount = expense/equity side (ไม่ใช่ WHT, ไม่ใช่ cash 111)
                DividendAmount = g.Where(x => x.Account != null
                        && x.Account.AccountCode != "21915"
                        && !x.Account.AccountCode.StartsWith("111")
                        && (x.Account.AccountName == null || !x.Account.AccountName.Contains("หัก ณ ที่จ่าย")))
                                  .Sum(x => Math.Abs(x.DebitAmount - x.CreditAmount))
            })
            .Where(x => x.WhtAmount > 0)
            .ToList();

        var sb = new System.Text.StringBuilder();
        var totalIncome = byJe.Sum(x => x.DividendAmount);
        var totalWht = byJe.Sum(x => x.WhtAmount);
        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.2|{period}|{byJe.Count}|{totalIncome:F2}|{totalWht:F2}");
        int seq = 1;
        foreach (var je in byJe)
        {
            var payDate = $"{je.Date.Day:D2}/{je.Date.Month:D2}/{je.Date.Year + 543}";
            // §40(4)(ข) เงินปันผล — IncomeType=4, Condition=1 (หัก ณ ที่จ่าย)
            sb.AppendLine($"D|{seq++}|—|—|—|—|{payDate}|4|{je.DividendAmount:F2}|{je.WhtAmount:F2}|1|{Esc(je.Description)}");
        }
        sb.AppendLine($"T|{byJe.Count}|{totalIncome:F2}|{totalWht:F2}");

        return new TaxFilingExportResult(
            "PND2", "ภ.ง.ด.2", $"PND2_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            byJe.Count, totalIncome, totalWht,
            $"ภ.ง.ด.2 เดือน {month}/{year} เงินปันผล {byJe.Count} รายการ ภาษีรวม {totalWht:N2} บาท");
    }

    /// <summary>ภ.พ.36 — Foreign service VAT self-assessment per §83/6.
    /// ผู้รับบริการในไทยที่ซื้อจาก supplier ต่างประเทศ (ที่ไม่ได้จด VAT
    /// ในไทย) ต้องนำส่ง VAT 7% เอง. รวมจาก PurchaseInvoice/Expense ที่
    /// IsForeignService=true. กำหนดยื่นภายในวันที่ 7 ของเดือนถัดไป.</summary>
    public async Task<TaxFilingExportResult> ExportPp36Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";

        // ⬇️ Single source: ใช้ตัวคำนวณเดียวกับรายงานบนจอ (GeneratePp36Report ผ่าน
        // ComputePp36ReportAsync) — เดิม export คัดเอกสารเองด้วยเงื่อนไขคนละชุด
        // (DocumentDate แทน tax point / ไม่มี PV·CIL / ไม่กัน Rejected / ไม่ dedup
        // งวดอื่น) ⇒ "ไฟล์ที่ยื่น ≠ ที่ผู้ใช้เห็น" — defect class เดียวกับที่
        // ภ.พ.30 เคยแก้ (ดู ExportPp30Async)
        var report = await _taxService.ComputePp36ReportAsync(companyId, year, month);
        var lines = report.Lines.Where(l => !l.IsExcluded).OrderBy(l => l.LineOrder).ToList();

        // hydrate เลขเอกสาร + ประเทศผู้ขาย สำหรับ D-row (line เก็บ DocumentId)
        var pp36DocIds = lines.Where(l => l.DocumentId.HasValue)
            .Select(l => l.DocumentId!.Value).Distinct().ToList();
        var pp36Docs = pp36DocIds.Count == 0
            ? new Dictionary<Guid, (string No, string? Country, string? Note)>()
            : await (from d in _db.Documents.AsNoTracking()
                     where d.CompanyId == companyId && pp36DocIds.Contains(d.Id)
                     join c in _db.Contacts.AsNoTracking() on d.ContactId equals c.Id into cj
                     from c in cj.DefaultIfEmpty()
                     select new { d.Id, d.DocumentNumber, Country = c != null ? c.Province : null, d.Notes, d.Reference })
                .ToDictionaryAsync(x => x.Id, x => (
                    No: x.DocumentNumber, Country: (string?)x.Country,
                    Note: (string?)(x.Notes ?? x.Reference)));

        var sb = new System.Text.StringBuilder();
        var totalServiceAmount = report.TotalIncome;
        var totalSelfVat = report.OutputVat;

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.พ.36|{period}|{lines.Count}|{totalServiceAmount:F2}|{totalSelfVat:F2}");
        int seq = 1;
        foreach (var l in lines)
        {
            var info = l.DocumentId.HasValue && pp36Docs.TryGetValue(l.DocumentId.Value, out var x)
                ? x : (No: "", Country: null, Note: null);
            var docDate = $"{l.TransactionDate:dd/MM/}{l.TransactionDate.Year + 543}";
            // D|Seq|SupplierName|SupplierCountry|InvoiceDate|InvoiceNumber|ServiceAmount|VatAmount|Description
            sb.AppendLine($"D|{seq++}|{Esc(l.TaxPayerName)}|{Esc(info.Country ?? "Foreign")}|{docDate}|{Esc(info.No)}|{l.IncomeAmount:F2}|{l.TaxAmount:F2}|{Esc(info.Note ?? "")}");
        }
        sb.AppendLine($"T|{lines.Count}|{totalServiceAmount:F2}|{totalSelfVat:F2}");

        return new TaxFilingExportResult(
            "PP36", "ภ.พ.36", $"PP36_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            lines.Count, totalServiceAmount, totalSelfVat,
            $"ภ.พ.36 เดือน {month}/{year} ซื้อบริการต่างประเทศ {lines.Count} รายการ VAT self-assess {totalSelfVat:N2} บาท");
    }

    /// <summary>ภ.ง.ด.54 — Foreign-vendor WHT. รวม PI/Expense ที่
    /// IsForeignService=true + WithholdingTaxAmount > 0 ในเดือน → text format
    /// ตาม RD spec. Income type 6 = §40(3)(4) ค่าสิทธิ์/ดอกเบี้ย/ปันผล.
    /// cert-primary เหมือน ภ.ง.ด.3/53 — แหล่งเดียวกับรายงานบนจอ (B5)</summary>
    public async Task<TaxFilingExportResult> ExportPnd54Async(Guid companyId, int year, int month)
    {
        // ⬇️ cert-primary เหมือน ภ.ง.ด.3/53 (B5) — เดิม mine เอกสารเองด้วยเงื่อนไข
        // คนละชุดกับรายงานบนจอ 13 จุด (นิยาม "ต่างประเทศ" ใช้ IsForeignService ซึ่ง
        // เป็นธง ภ.พ.36 แทน CountryCode, ไม่รวม CIL, ไม่กัน Rejected, ไม่ dedup
        // PI↔PV, ไม่รู้จัก cert/IsExcluded, income type hardcode "6") ⇒ ยื่นซ้ำ/
        // ยื่นให้ใบที่ 50 ทวิ ยังไม่ออก. ตอนนี้อ่านทะเบียนหนังสือรับรองชุดเดียว
        // กับที่รายงานบนจอใช้ (GenerateWhtReport cert-primary)
        var certs = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)   // ไม่ Include PayeeContact — hydrate แยก
            .Where(w => w.CompanyId == companyId
                && w.TaxFormType == TaxType.WithholdingTax54
                && w.TaxYear == year && w.TaxMonth == month
                && (w.Status == WithholdingTaxCertStatus.Issued
                    || w.Status == WithholdingTaxCertStatus.Printed))
            .ToListAsync();
        await _db.HydratePayeeContactsAsync(companyId, certs);

        var totalIncome = certs.Sum(c => c.TotalIncomeAmount);
        var totalWht = certs.Sum(c => c.TotalTaxAmount);
        // ผู้รับเงินต่างประเทศตาม ม.70 ส่วนใหญ่เป็นนิติบุคคล → ชื่อเต็มช่องเดียว
        var rows = BuildPndRows(certs, juristicPayee: true);
        var body = PndTextFileFormat.Build(rows);

        return new TaxFilingExportResult(
            "PND54", "ภ.ง.ด.54", $"PND54_{year}{month:D2}.txt", "text/plain", AsBytes(body),
            certs.Count, totalIncome, totalWht,
            $"ภ.ง.ด.54 เดือน {month}/{year} จ่ายต่างประเทศ {certs.Count} ราย WHT {totalWht:N2} บาท "
            + $"· {rows.Count} บรรทัด (ไม่มี header — นำเข้าเว็บสรรพากรได้ทันที)");
    }

    // =====================================================================
    // ภ.ง.ด.51 — Half-year CIT (รอบครึ่งปี)
    // กฎหมาย: ประมวลรัษฎากร §67 ทวิ — นิติบุคคลต้องประมาณการกำไรสุทธิทั้งรอบ
    // แล้วชำระภาษีครึ่งปี = (ประมาณการกำไรสุทธิทั้งปี × อัตรา) ÷ 2.
    // ยื่นภายใน 2 เดือนนับจากวันสุดท้ายของ 6 เดือนแรก (รอบ ม.ค.-ธ.ค. →
    // ยื่น 31 ส.ค.). รอบ < 12 เดือน (ปีแรก / สุดท้าย / เปลี่ยนรอบ) ยกเว้น
    // ไม่ต้องยื่น ภ.ง.ด.51.
    // ระบบทำ:
    //   • คำนวณ "กำไรสุทธิจริง 6 เดือนแรก" จาก JE Revenue/Expense
    //   • Estimate ทั้งปี = ครึ่งปี × 2 (วิธี simple — user แก้ได้ใน portal)
    //   • คำนวณภาษีตาม CitRateBracket (SME / ทั่วไป)
    //   • Half-year tax = annual tax ÷ 2
    //   • Layout: H|TaxId|BranchCode|ภ.ง.ด.51|ปี|รอบ(6เดือน)|HalfRevenue|HalfNetProfit|EstimatedAnnualProfit|EstimatedCit|HalfCit
    // หมายเหตุ: ประมาณการต่ำกว่าจริง > 25% → เงินเพิ่ม 20% ของส่วนต่าง (§67 ทวิ
    // วรรคสอง) — ระบบไม่คำนวณตอนยื่น ภ.ง.ด.51 (ใช้ตอนยื่น ภ.ง.ด.50 ค่อยตรวจย้อน).
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd51Async(Guid companyId, int year)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var startMonth = company.FiscalYearStartMonth is >= 1 and <= 12 ? company.FiscalYearStartMonth : 1;
        var fyStart = new DateTime(year, startMonth, 1);
        var fyEnd = fyStart.AddYears(1).AddDays(-1);
        var halfEnd = fyStart.AddMonths(6).AddDays(-1);   // 6 เดือนแรก

        // First-year ยกเว้น ภ.ง.ด.51 — ใช้ Company.CreatedAt เป็น proxy
        // ของวันเริ่มจัดตั้งระบบ (best-effort; user override ผ่าน portal ได้).
        // ถ้า CreatedAt อยู่ในช่วง fyStart..fyEnd → ปีแรก รอบ < 12 เดือน.
        var incorporatedAt = company.CreatedAt;
        var isFirstYear = incorporatedAt > fyStart && incorporatedAt < fyEnd;
        if (isFirstYear)
        {
            return new TaxFilingExportResult(
                "PND51", "ภ.ง.ด.51", $"PND51_{year}.txt", "text/plain", AsBytes(""),
                0, 0, 0,
                $"ภ.ง.ด.51 ปี {year}: รอบบัญชี < 12 เดือน (ปีแรก/สุดท้าย/เปลี่ยนรอบ) — ยกเว้นไม่ต้องยื่น (§67 ทวิ)");
        }

        // กำไรสุทธิ 6 เดือนแรก = Revenue − Expense (Posted JE)
        var revenueHalf = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fyStart
                && l.JournalEntry.EntryDate <= halfEnd
                && l.Account!.AccountType == AccountType.Revenue)
            .SumAsync(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m;
        var expenseHalf = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fyStart
                && l.JournalEntry.EntryDate <= halfEnd
                && l.Account!.AccountType == AccountType.Expense)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
        // B6: บวกกลับ §65 ตรี ของครึ่งปีแรก — ฐานเดียวกับ ภ.ง.ด.50 (เดิม 51
        // คำนวณจาก JE ล้วน ไม่มีบวกกลับเลย ⇒ ประมาณการกำไรต่ำกว่าจริง เสี่ยง
        // เงินเพิ่ม 20% ตาม §67 ตรี เมื่อประมาณการขาดเกิน 25%)
        var addBackHalf = await _taxService.ComputeSection65TerAddBackAsync(
            companyId, fyStart, halfEnd);
        var netProfitHalf = revenueHalf - expenseHalf + addBackHalf;

        // ประมาณการทั้งปี — วิธี simple × 2 (user แก้ใน portal ก่อนยื่น)
        var estimatedAnnualProfit = netProfitHalf * 2m;

        // อัตราภาษี — SME (ทุน ≤ 5 ล. + รายได้ ≤ 30 ล.) ใช้ขั้นบันได, อื่น ๆ 20%.
        // ใช้ totalRevenue ทั้งปีจริง (ถ้ามี) หรือประมาณ × 2 ตัดสินว่า SME.
        var revenueAnnualEst = revenueHalf * 2m;
        var paidUpCapital = company.PaidUpCapital;
        var isSme = paidUpCapital <= 5_000_000m && revenueAnnualEst <= 30_000_000m;
        var estimatedAnnualCit = ComputeCit(estimatedAnnualProfit, isSme);
        var halfYearCit = Math.Round(estimatedAnnualCit / 2m, 2);   // §67 ทวิ

        var sb = new System.Text.StringBuilder();
        var branchSeq = company.BranchCode ?? "00000";
        sb.AppendLine($"H|{company.TaxId}|{branchSeq}|ภ.ง.ด.51|{thaiYear:D4}|6M|{revenueHalf:F2}|{netProfitHalf:F2}|{estimatedAnnualProfit:F2}|{estimatedAnnualCit:F2}|{halfYearCit:F2}");
        sb.AppendLine($"T|isSME={(isSme ? 1 : 0)}|due={halfEnd.AddMonths(2):yyyy-MM-dd}");

        return new TaxFilingExportResult(
            "PND51", "ภ.ง.ด.51", $"PND51_{year}.txt", "text/plain", AsBytes(sb.ToString()),
            1, revenueHalf, halfYearCit,
            $"ภ.ง.ด.51 ปี {year} ({(isSme ? "SME" : "ทั่วไป")}) — กำไรครึ่งปี {netProfitHalf:N2}, " +
            $"ประมาณการทั้งปี {estimatedAnnualProfit:N2}, ภาษีครึ่งปี {halfYearCit:N2}. " +
            $"กำหนดยื่น: {halfEnd.AddMonths(2):dd/MM/yyyy} (§67 ทวิ — 2 เดือนนับจาก {halfEnd:dd/MM/yyyy})");
    }

    /// <summary>คำนวณ CIT ตามอัตรา SME / ทั่วไป. SME (ทุน ≤ 5 ล. + รายได้ ≤ 30 ล.):
    /// 0–300k = 0%, 300k–3M = 15%, > 3M = 20%. ทั่วไป: 20% flat.
    /// กฎ: ประมวลรัษฎากร §65 + พระราชกฤษฎีกา #530/595.</summary>
    private static decimal ComputeCit(decimal netProfit, bool isSme)
    {
        if (netProfit <= 0) return 0;
        if (!isSme) return Math.Round(netProfit * 0.20m, 2);
        // SME ขั้นบันได
        decimal tax = 0;
        var remain = netProfit;
        var b1 = Math.Min(remain, 300_000m); tax += b1 * 0m; remain -= b1;
        if (remain <= 0) return Math.Round(tax, 2);
        var b2 = Math.Min(remain, 2_700_000m); tax += b2 * 0.15m; remain -= b2;
        if (remain <= 0) return Math.Round(tax, 2);
        tax += remain * 0.20m;
        return Math.Round(tax, 2);
    }

    private static string Esc(string? s) => s == null ? "" : s.Replace("|", "/").Replace("\n", " ").Trim();
}
