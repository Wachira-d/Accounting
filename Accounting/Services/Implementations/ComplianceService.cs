using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Compliance;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ComplianceService : IComplianceService
{
    private readonly AccountingDbContext _db;

    public ComplianceService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<ComplianceFilingResponse> CreateFilingAsync(Guid companyId, CreateComplianceFilingRequest request)
    {
        var filing = new ComplianceFiling
        {
            CompanyId = companyId,
            FilingType = request.FilingType,
            FormCode = request.FormCode,
            Year = request.Year,
            Month = request.Month,
            DueDate = request.DueDate,
            Notes = request.Notes,
            Status = "NotStarted"
        };

        _db.Set<ComplianceFiling>().Add(filing);
        await _db.SaveChangesAsync();

        return MapToResponse(filing);
    }

    public async Task<ComplianceFilingResponse> GetFilingAsync(Guid companyId, Guid filingId)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        return MapToResponse(filing);
    }

    public async Task<PagedResponse<ComplianceFilingResponse>> GetFilingsAsync(Guid companyId, int? year, string? filingType, PagedRequest request)
    {
        var query = _db.Set<ComplianceFiling>()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted);

        if (year.HasValue)
            query = query.Where(f => f.Year == year.Value);

        if (!string.IsNullOrWhiteSpace(filingType))
            query = query.Where(f => f.FilingType == filingType);

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(f => f.FormCode.Contains(request.Search)
                                  || f.FilingType.Contains(request.Search)
                                  || (f.Notes != null && f.Notes.Contains(request.Search)));

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderBy(f => f.DueDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(f => MapToResponse(f))
            .ToListAsync();

        return new PagedResponse<ComplianceFilingResponse>(
            items, totalCount, request.Page, request.PageSize,
            (int)Math.Ceiling(totalCount / (double)request.PageSize));
    }

    public async Task<ComplianceFilingResponse> UpdateFilingAsync(Guid companyId, Guid filingId, UpdateComplianceFilingRequest request)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        if (request.DueDate.HasValue) filing.DueDate = request.DueDate.Value;
        if (request.Notes != null) filing.Notes = request.Notes;
        if (request.Status != null) filing.Status = request.Status;

        filing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToResponse(filing);
    }

    public async Task DeleteFilingAsync(Guid companyId, Guid filingId)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        if (filing.Status == "Filed" || filing.Status == "Accepted")
            throw new InvalidOperationException("Cannot delete a filed or accepted compliance filing.");

        filing.IsDeleted = true;
        filing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<ComplianceFilingResponse> ValidateFilingAsync(Guid companyId, Guid filingId)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        var errors = new List<string>();

        // Validate based on filing type
        if (filing.FilingType == "RD_VAT")
        {
            // Check that VAT tax reports exist for the period
            var vatReport = await _db.TaxReports
                .FirstOrDefaultAsync(t => t.CompanyId == companyId
                                       && t.TaxType == TaxType.VAT
                                       && t.Year == filing.Year
                                       && t.Month == (filing.Month ?? 0));

            if (vatReport == null)
                errors.Add($"VAT report for {filing.Year}/{filing.Month} has not been generated.");

            // Check for unposted journal entries in the period
            if (filing.Month.HasValue)
            {
                var unpostedCount = await _db.JournalEntries
                    .CountAsync(j => j.CompanyId == companyId
                                  && j.Status == JournalEntryStatus.Draft
                                  && j.EntryDate.Year == filing.Year
                                  && j.EntryDate.Month == filing.Month.Value);

                if (unpostedCount > 0)
                    errors.Add($"There are {unpostedCount} unposted journal entries for the filing period.");
            }
        }
        else if (filing.FilingType == "RD_WHT")
        {
            // Check that WHT certificates exist for the period
            if (filing.Month.HasValue)
            {
                var whtCertCount = await _db.WithholdingTaxCerts
                    .CountAsync(w => w.CompanyId == companyId
                                  && w.CreatedAt.Year == filing.Year
                                  && w.CreatedAt.Month == filing.Month.Value);

                if (whtCertCount == 0)
                    errors.Add("No withholding tax certificates found for the filing period.");
            }
        }
        else if (filing.FilingType == "RD_CIT")
        {
            // Corporate income tax: check fiscal periods are closed
            var openPeriods = await _db.Set<FiscalPeriod>()
                .CountAsync(p => p.CompanyId == companyId
                              && p.Year == filing.Year
                              && p.Status == FiscalPeriodStatus.Open);

            if (openPeriods > 0)
                errors.Add($"There are {openPeriods} open fiscal periods for the year. Close them before filing CIT.");
        }
        else if (filing.FilingType == "SSO_Contribution")
        {
            // Social security: check payroll has been processed
            // Simplified validation
            if (filing.Month.HasValue)
            {
                var hasPayroll = await _db.Set<PayrollRun>()
                    .AnyAsync(p => p.CompanyId == companyId
                                && p.Year == filing.Year
                                && p.Month == filing.Month.Value);

                if (!hasPayroll)
                    errors.Add("Payroll has not been processed for the filing period.");
            }
        }

        // Prepare tax amount from relevant data
        if (errors.Count == 0 && filing.FilingType == "RD_VAT" && filing.Month.HasValue)
        {
            var vatReport = await _db.TaxReports
                .FirstOrDefaultAsync(t => t.CompanyId == companyId
                                       && t.TaxType == TaxType.VAT
                                       && t.Year == filing.Year
                                       && t.Month == filing.Month.Value);

            if (vatReport != null)
            {
                filing.TaxAmount = vatReport.NetVat;
                filing.FileDataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    vatReport.OutputVat,
                    vatReport.InputVat,
                    vatReport.NetVat,
                    vatReport.TotalIncome
                });
            }
        }

        if (errors.Count > 0)
        {
            filing.ValidationErrors = string.Join("; ", errors);
            filing.Status = "InProgress";
        }
        else
        {
            filing.ValidationErrors = null;
            filing.Status = "Validated";
        }

        await _db.SaveChangesAsync();

        return MapToResponse(filing);
    }

    public async Task<ComplianceFilingResponse> SubmitFilingAsync(Guid companyId, Guid filingId, string filedBy)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        if (filing.Status != "Validated")
            throw new InvalidOperationException("Filing must be validated before submission. Current status: " + filing.Status);

        // ═══ "บันทึกการยื่นด้วยมือ" — ไม่ใช่การส่งแบบไปหน่วยงาน (D2-B1b · ราก R1) ═══
        // เดิมบล็อกนี้คอมเมนต์ตัวเองว่า "Simulate submission to government
        // e-filing system" แล้ว **แต่งเลขอ้างอิง/เลขยืนยันจาก GUID** ⇒ ผู้ใช้เห็น
        // เลข "CONF-PP30-2026-05-A1B2C3D4" บนจอและเข้าใจว่าเป็นเลขของราชการ
        // ทั้งที่ไม่มีอะไรถูกส่งไปไหนเลย (หลักการ 10 ข้อ #3: ค่าที่แต่งขึ้น
        // อันตรายกว่าการไม่ตอบ · สถานะปลายทางตั้งได้เฉพาะเมื่อภายนอกตอบกลับจริง)
        //
        // ตอนนี้: บันทึกว่า "ผู้ใช้แจ้งว่ายื่นแล้ว" เท่านั้น — เลขอ้างอิงมาจาก
        // ผู้ใช้กรอกเองผ่าน UpdateFilingAsync/endpoint บันทึกเลขรับ หรือ**ไม่มีเลย**
        filing.Status = "Filed";
        filing.FiledDate = DateTime.UtcNow;
        filing.FiledBy = filedBy;
        filing.SubmissionReference = null;   // ห้ามแต่ง — ไม่มีการส่งจริง
        // ConfirmationNumber: คงค่าที่ผู้ใช้เคยกรอกไว้ (ถ้ามี) ไม่เขียนทับด้วยของปลอม

        // เงินเพิ่มยื่นช้า ม.27 — 1.5%/เดือน **แต่ไม่เกินจำนวนภาษี** (ม.27 วรรคสาม)
        // เดิมไม่มีเพดาน ⇒ ยื่นช้าเกิน 66 เดือนได้ตัวเลขเกิน 100% ของภาษี ซึ่งสูง
        // กว่าที่กรมสรรพากรเรียกเก็บจริง. สูตรอยู่ใน Helpers/RevenueCodeSurcharge
        // ตัวเดียว (เศษของเดือนนับเป็น 1 เดือน · AwayFromZero)
        if (filing.TaxAmount is > 0 && filing.FiledDate.HasValue)
        {
            filing.PenaltyAmount = Accounting.Helpers.RevenueCodeSurcharge.Compute(
                filing.TaxAmount.Value, filing.DueDate, filing.FiledDate.Value);
            // ชนเพดานแล้วต้องบอก ไม่งั้นผู้ใช้เห็นตัวเลขหยุดโตแล้วคิดว่าระบบคำนวณผิด
            if (Accounting.Helpers.RevenueCodeSurcharge.IsCapped(
                    filing.TaxAmount.Value, filing.DueDate, filing.FiledDate.Value))
            {
                var capNote = $"[เงินเพิ่ม ม.27] ชนเพดานแล้ว — 1.5%/เดือน คิดได้ไม่เกินจำนวนภาษี "
                    + $"({filing.TaxAmount.Value:N2} บาท)";
                filing.Notes = string.IsNullOrWhiteSpace(filing.Notes)
                    ? capNote : filing.Notes + "\n" + capNote;
            }
        }

        await _db.SaveChangesAsync();

        return MapToResponse(filing);
    }

    public async Task<List<ComplianceFilingResponse>> GetPendingFilingsAsync(Guid companyId)
    {
        return await _db.Set<ComplianceFiling>()
            .Where(f => f.CompanyId == companyId
                      && !f.IsDeleted
                      && f.Status != "Filed"
                      && f.Status != "Accepted")
            .OrderBy(f => f.DueDate)
            .Select(f => MapToResponse(f))
            .ToListAsync();
    }

    public async Task InitializeFilingCalendarAsync(Guid companyId, int year)
    {
        // Check if filings already exist for this year
        var existingCount = await _db.Set<ComplianceFiling>()
            .CountAsync(f => f.CompanyId == companyId && f.Year == year && !f.IsDeleted);

        if (existingCount > 0)
            throw new InvalidOperationException($"Filing calendar for {year} already exists with {existingCount} entries.");

        var filings = new List<ComplianceFiling>();

        // ── รายเดือน: กำหนดยื่นทุกแบบมาจาก Helpers/TaxFilingDeadline ที่เดียว ──
        // เดิมเมธอดนี้พิมพ์ตารางเอง 4 ลูป (`AddMonths(1).AddDays(14)` / `AddDays(6)`) =
        // ตารางกำหนดยื่น**ชุดที่ 4** ที่ไม่เลื่อนวันหยุด ป.พ.พ. §193/8 และไม่รู้จัก e-Filing
        // ⇒ ปฏิทิน compliance บอกกำหนดคนละวันกับหน้านำส่งภาษี (ซ้ำรอย ภ.พ.36 ที่เคยได้
        // วันที่ 23 แทน 15). checker `filing_deadline_single_source_check` มองไม่เห็น
        // เพราะจับจากชื่อเมธอด — แก้ checker ให้จับจากรูปทรงบอดี้แล้วในรอบเดียวกัน
        //
        // ใช้วัน "กระดาษ" เป็น DueDate (เข้มกว่า — ผู้ที่ยื่นออนไลน์ยังทันเสมอ)
        // เพราะ ComplianceFiling ไม่มีช่อง e-Filing แยก
        var monthlyForms = new (string FilingType, string FormCode, string RemitKey)[]
        {
            ("RD_VAT",           "PP30",    "VatPp30"),
            ("RD_WHT",           "PND3",    "WhtPnd3"),
            ("RD_WHT",           "PND53",   "WhtPnd53"),
            ("RD_WHT",           "PND1",    "WhtPnd1"),
            ("SSO_Contribution", "SSO1-10", "SsoSps110"),
        };
        foreach (var (filingType, formCode, remitKey) in monthlyForms)
        {
            for (int month = 1; month <= 12; month++)
            {
                var (paper, _) = Accounting.Helpers.TaxFilingDeadline.For(remitKey, year, month);
                filings.Add(new ComplianceFiling
                {
                    CompanyId = companyId,
                    FilingType = filingType,
                    FormCode = formCode,
                    Year = year,
                    Month = month,
                    DueDate = paper,
                    Status = "NotStarted"
                });
            }
        }

        // Half-year CIT (ภ.ง.ด.51) - due within 2 months of half-year end
        filings.Add(new ComplianceFiling
        {
            CompanyId = companyId,
            FilingType = "RD_CIT",
            FormCode = "PND51",
            Year = year,
            Month = 6,
            DueDate = new DateTime(year, 8, 31),
            Status = "NotStarted"
        });

        // Annual CIT (ภ.ง.ด.50) - due within 150 days of fiscal year end
        filings.Add(new ComplianceFiling
        {
            CompanyId = companyId,
            FilingType = "RD_CIT",
            FormCode = "PND50",
            Year = year,
            Month = null,
            DueDate = new DateTime(year + 1, 5, 31),
            Status = "NotStarted"
        });

        // Annual DBD Financial Statement - due within 5 months of fiscal year end
        filings.Add(new ComplianceFiling
        {
            CompanyId = companyId,
            FilingType = "DBD_FinancialStatement",
            FormCode = "SBC3",
            Year = year,
            Month = null,
            DueDate = new DateTime(year + 1, 5, 31),
            Status = "NotStarted"
        });

        _db.Set<ComplianceFiling>().AddRange(filings);
        await _db.SaveChangesAsync();
    }

    /// <summary>สถานะที่แปลว่า "ประกาศว่ายื่นแล้ว" ของ <c>ComplianceFiling</c>
    /// (ฝั่งนี้เก็บสถานะเป็นข้อความ ไม่ใช่ enum เดียวกับ <c>TaxReport</c>)</summary>
    private static bool IsDeclaredFiled(string? status)
        => status is "Filed" or "Accepted";

    private static ComplianceFilingResponse MapToResponse(ComplianceFiling f)
    {
        var daysUntilDue = (f.DueDate - DateTime.UtcNow.Date).Days;

        // ── ระดับหลักฐาน: "มีเลขยืนยันจริงไหม" ไม่ใช่ "สถานะเป็น Filed ไหม" ──
        // ใช้เกณฑ์ตัวเดียวกับฝั่งรายงานภาษี (Helpers/TaxFilingLockPolicy) เพื่อ
        // ไม่ให้เกิดนิยาม "มีเลขรับ" ชุดที่สอง (ช่องว่างล้วน = ยังไม่มี)
        var hasNumber = Accounting.Helpers.TaxFilingLockPolicy
            .HasFilingNumber(f.ConfirmationNumber);
        var declared = IsDeclaredFiled(f.Status);
        var evidence = !declared
            ? Accounting.Helpers.TaxFilingEvidence.NotFiled
            : hasNumber
                ? Accounting.Helpers.TaxFilingEvidence.ConfirmedByFilingNumber
                : Accounting.Helpers.TaxFilingEvidence.DeclaredByUser;
        var evidenceDetail = evidence switch
        {
            Accounting.Helpers.TaxFilingEvidence.ConfirmedByFilingNumber =>
                $"ยืนยันด้วยเลขที่ {f.ConfirmationNumber!.Trim()} ที่ผู้ใช้บันทึกไว้",
            Accounting.Helpers.TaxFilingEvidence.DeclaredByUser =>
                "ระบบบันทึกตามที่ผู้ใช้แจ้งว่ายื่นแล้วเท่านั้น — ยังไม่มีเลขยืนยันจากหน่วยงาน "
                + "(ถ้ามีใบเสร็จ/เลขรับแล้ว ให้กรอกที่ช่อง \"เลขยืนยัน\")",
            _ => "ยังไม่ได้ยื่น",
        };

        return new ComplianceFilingResponse(
            f.Id, f.FilingType, f.FormCode, f.Year, f.Month,
            f.DueDate, f.FiledDate, f.Status,
            f.SubmissionReference, f.ConfirmationNumber,
            f.TaxAmount, f.PenaltyAmount,
            f.ValidationErrors, daysUntilDue,
            evidence.ToString(), evidenceDetail);
    }
}
