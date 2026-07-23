using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>หน้านำส่งภาษี/ประกันสังคมรวม (สปส.1-10 + ภงด.1/3/53 + ภพ.30).
/// แหล่งหนี้ต่องวด:
///   • SSO   — PayrollRun ที่ Paid + ยังไม่ settle (SsoSettledAt == null)
///   • ภงด.1 — PayrollRun.TotalWithholdingTax (ภาษีเงินเดือน)
///   • ภงด.3/53 — เอกสารที่มี WithholdingTaxAmount แยกตามชนิดผู้ติดต่อ
///   • ภพ.30 — อ่านจาก TaxReports ที่ generate ไว้แล้ว (NetVat > 0) — ไม่คำนวณสด
///     ในหน้านี้เพื่อกันหน้าค้างจากการคำนวณ VAT หลายเดือน
/// ยอดค้าง = หนี้ − ที่นำส่งแล้ว (StatutoryRemittance; SSO ใช้ SsoSettledAt).</summary>
public class StatutoryRemittanceService : IStatutoryRemittanceService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService _accounting;
    private readonly ILogger<StatutoryRemittanceService> _logger;

    public StatutoryRemittanceService(AccountingDbContext db, IAccountingService accounting,
        ILogger<StatutoryRemittanceService> logger)
    {
        _db = db; _accounting = accounting; _logger = logger;
    }

    // ===== ป้ายชื่อ + ผังหนี้ค้างจ่าย ต่อประเภท =====
    private static (string Label, string Form, string PayableCode) Meta(string type) => type switch
    {
        "SsoSps110" => ("ประกันสังคม", "สปส.1-10", "21815"),
        "WhtPnd1"   => ("ภาษีหัก ณ ที่จ่าย (เงินเดือน)", "ภ.ง.ด.1", "21914"),
        "WhtPnd3"   => ("ภาษีหัก ณ ที่จ่าย (บุคคลธรรมดา)", "ภ.ง.ด.3", "21916"),
        "WhtPnd53"  => ("ภาษีหัก ณ ที่จ่าย (นิติบุคคล)", "ภ.ง.ด.53", "21917"),
        "VatPp30"   => ("ภาษีมูลค่าเพิ่ม", "ภ.พ.30", "21911"),
        // §83/6 reverse charge — VAT ประเมินเองจากจ่ายค่าบริการ ตปท. (ตั้งหนี้
        // Cr 21912 ตอนบันทึกเอกสาร IsForeignService → นำส่ง Dr 21912/Cr ธนาคาร)
        "VatPp36"   => ("ภาษีมูลค่าเพิ่ม (บริการต่างประเทศ §83/6)", "ภ.พ.36", "21912"),
        _           => ("ไม่ทราบ", type, "")
    };

    // กำหนดยื่น: (กระดาษวันที่, e-Filing วันที่) ของเดือนถัดจากงวด
    private static (DateTime Paper, DateTime EFiling) DueDates(string type, int year, int month)
    {
        var next = new DateTime(year, month, 1).AddMonths(1);
        DateTime D(int day) => new DateTime(next.Year, next.Month, day);
        return type switch
        {
            "SsoSps110" => (D(15), D(15)),                 // สปส. วันที่ 15 ทั้งคู่
            "VatPp30"   => (D(15), D(23)),                 // ภพ.30 กระดาษ 15 / e-Filing +8 = 23
            _           => (D(7), D(15)),                  // ภงด. กระดาษ 7 / e-Filing 15
        };
    }

    public async Task<RemittanceDashboardResponse> GetDashboardAsync(Guid companyId, int monthsBack = 12)
    {
        var today = DateTime.UtcNow.Date;
        var start = new DateTime(today.Year, today.Month, 1).AddMonths(-Math.Max(1, monthsBack));
        bool InRange(int y, int m) { var d = new DateTime(y, m, 1); return d >= start && d <= new DateTime(today.Year, today.Month, 1); }

        var pending = new List<PendingRemittanceItem>();

        // ── โหลด remittance ที่นำส่งแล้ว (สำหรับ netting) — กัน table ยังไม่ถูกสร้าง ──
        var remits = new List<StatutoryRemittance>();
        try
        {
            remits = await _db.Set<StatutoryRemittance>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted).ToListAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลด StatutoryRemittances ไม่สำเร็จ (table ยังไม่ถูกสร้าง?)"); }
        decimal Remitted(string type, int y, int m) =>
            remits.Where(r => r.RemittanceType == type && r.PeriodYear == y && r.PeriodMonth == m)
                  .Sum(r => r.Amount);

        // ── SSO + ภงด.1 จาก PayrollRun ──
        try
        {
            var runs = await _db.Set<PayrollRun>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.Status == "Paid")
                .Select(r => new { r.Id, r.Year, r.Month,
                    Emp = r.TotalSocialSecurityEmployee, Empr = r.TotalSocialSecurityEmployer,
                    Wht = r.TotalWithholdingTax, r.SsoSettledAt })
                .ToListAsync();
            foreach (var g in runs.Where(r => r.SsoSettledAt == null && InRange(r.Year, r.Month))
                                   .GroupBy(r => (r.Year, r.Month)))
            {
                var emp = g.Sum(x => x.Emp); var empr = g.Sum(x => x.Empr);
                var total = emp + empr;
                if (total <= 0.009m) continue;
                pending.Add(BuildItem("SsoSps110", g.Key.Year, g.Key.Month, total, today,
                    employee: emp, employer: empr, relatedRunId: g.First().Id));
            }
            foreach (var g in runs.Where(r => InRange(r.Year, r.Month)).GroupBy(r => (r.Year, r.Month)))
            {
                var outstanding = g.Sum(x => x.Wht) - Remitted("WhtPnd1", g.Key.Year, g.Key.Month);
                if (outstanding <= 0.009m) continue;
                pending.Add(BuildItem("WhtPnd1", g.Key.Year, g.Key.Month, outstanding, today));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "คำนวณ SSO/ภงด.1 ไม่สำเร็จ"); }

        // ── ภงด.3 / 53 จากเอกสารหัก ณ ที่จ่าย (bound ช่วงวันที่ใน SQL) ──
        try
        {
            var whtDocs = await _db.Documents.AsNoTracking()
                // เฉพาะเอกสารที่ post WHT payable เข้า GL แล้ว (อนุมัติขึ้นไป)
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.WithholdingTaxAmount > 0
                    && (d.PaymentDate ?? d.DocumentDate) >= start
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.WithholdingTaxAmount, d.PaymentDate, d.DocumentDate,
                    CType = d.Contact != null ? d.Contact.ContactType : ContactType.Individual })
                .ToListAsync();
            foreach (var typ in new[] { "WhtPnd3", "WhtPnd53" })
            {
                bool juristic = typ == "WhtPnd53";
                var grouped = whtDocs
                    .Where(d => juristic ? d.CType == ContactType.JuristicPerson : d.CType != ContactType.JuristicPerson)
                    .Select(d => new { Date = (d.PaymentDate ?? d.DocumentDate), d.WithholdingTaxAmount })
                    .Where(d => InRange(d.Date.Year, d.Date.Month))
                    .GroupBy(d => (d.Date.Year, d.Date.Month));
                foreach (var g in grouped)
                {
                    var outstanding = g.Sum(x => x.WithholdingTaxAmount) - Remitted(typ, g.Key.Year, g.Key.Month);
                    if (outstanding <= 0.009m) continue;
                    pending.Add(BuildItem(typ, g.Key.Year, g.Key.Month, outstanding, today, payeeCount: g.Count()));
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "คำนวณ ภงด.3/53 ไม่สำเร็จ"); }

        // ── ภพ.30 — ใช้รายงานที่ generate/บันทึกไว้แล้ว (เร็ว — ไม่คำนวณ VAT สดหลาย
        //    เดือนในหน้านี้ ซึ่งเคยทำให้หน้าค้างโหลด). ผู้ใช้ generate ภ.พ.30 หน้า
        //    "รายงานภาษี" ก่อน แล้วยอดสุทธิจะมาขึ้นที่นี่. ──
        try
        {
            var vatReports = await _db.TaxReports.AsNoTracking()
                .Where(t => t.CompanyId == companyId && t.TaxType == TaxType.VAT
                    && (t.Year > start.Year || (t.Year == start.Year && t.Month >= start.Month)))
                .Select(t => new { t.Year, t.Month, t.NetVat, t.OutputVat, t.InputVat })
                .ToListAsync();
            // dedup กันเคสมีหลายรายงานต่อเดือน (draft + regenerate)
            foreach (var v in vatReports.GroupBy(x => (x.Year, x.Month)).Select(grp => grp.First()))
            {
                if (!InRange(v.Year, v.Month)) continue;
                var outstanding = v.NetVat - Remitted("VatPp30", v.Year, v.Month);
                if (outstanding <= 0.009m) continue;
                pending.Add(BuildItem("VatPp30", v.Year, v.Month, outstanding, today,
                    outputVat: v.OutputVat, inputVat: v.InputVat));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลด ภพ.30 ไม่สำเร็จ"); }

        // ── ภพ.36 — VAT ประเมินเองจากบริการต่างประเทศ (§83/6) ──
        // แหล่งหนี้: เอกสาร IsForeignService ที่อนุมัติแล้ว (JE ตั้ง Cr 21912 ไว้)
        try
        {
            var fsDocs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.IsForeignService && d.VatAmount > 0
                    && (d.PaymentDate ?? d.DocumentDate) >= start
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.VatAmount, d.PaymentDate, d.DocumentDate })
                .ToListAsync();
            foreach (var g in fsDocs
                .Select(d => new { Date = d.PaymentDate ?? d.DocumentDate, d.VatAmount })
                .Where(d => InRange(d.Date.Year, d.Date.Month))
                .GroupBy(d => (d.Date.Year, d.Date.Month)))
            {
                var outstanding = g.Sum(x => x.VatAmount) - Remitted("VatPp36", g.Key.Year, g.Key.Month);
                if (outstanding <= 0.009m) continue;
                pending.Add(BuildItem("VatPp36", g.Key.Year, g.Key.Month, outstanding, today,
                    payeeCount: g.Count()));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "คำนวณ ภพ.36 ไม่สำเร็จ"); }

        // ── ประวัติที่นำส่งล่าสุด ──
        var history = remits.OrderByDescending(r => r.PayDate).Take(30)
            .Select(r => new RemittanceHistoryItem(r.Id, r.RemittanceType, Meta(r.RemittanceType).Form,
                r.PeriodYear, r.PeriodMonth, r.Amount, r.LateFee, r.PayDate, r.FilingNumber,
                r.JournalEntryId, r.ReceiptAttachmentId, r.CreatedBy, r.CreatedAt))
            .ToList();

        pending = pending.OrderByDescending(p => p.IsOverdue)
            .ThenBy(p => p.EFilingDueDate).ToList();

        return new RemittanceDashboardResponse(
            TotalPending: pending.Sum(p => p.Amount),
            TotalOverdue: pending.Where(p => p.IsOverdue).Sum(p => p.Amount),
            OverdueCount: pending.Count(p => p.IsOverdue),
            Pending: pending,
            RecentHistory: history);
    }

    private PendingRemittanceItem BuildItem(string type, int year, int month, decimal amount,
        DateTime today, decimal? employee = null, decimal? employer = null, int? payeeCount = null,
        decimal? outputVat = null, decimal? inputVat = null, Guid? relatedRunId = null)
    {
        var (label, form, code) = Meta(type);
        var (paper, efiling) = DueDates(type, year, month);
        var overdue = today > efiling.Date;
        // เงินเพิ่ม preview เฉพาะ ปกส. (§49 2%/เดือน)
        var lateFee = type == "SsoSps110"
            ? PayrollService.ComputeSsoLateFee(year, month, today, amount)
            : 0m;
        return new PendingRemittanceItem(type, label, form, year, month, amount,
            paper, efiling, overdue, lateFee, code,
            employee, employer, payeeCount, outputVat, inputVat, relatedRunId);
    }

    public async Task<PendingRemittanceItem?> PreviewAsync(Guid companyId, string remittanceType,
        int periodYear, int periodMonth, DateTime payDate)
    {
        var dash = await GetDashboardAsync(companyId, 18);
        var item = dash.Pending.FirstOrDefault(p => p.RemittanceType == remittanceType
            && p.PeriodYear == periodYear && p.PeriodMonth == periodMonth);
        if (item == null) return null;
        // คำนวณเงินเพิ่มใหม่ตาม payDate ที่เลือก (ปกส.)
        if (remittanceType == "SsoSps110")
        {
            var lf = PayrollService.ComputeSsoLateFee(periodYear, periodMonth, payDate, item.Amount);
            item = item with { LateFeePreview = lf };
        }
        return item;
    }

    public async Task<RemitResult> RemitAsync(Guid companyId, RemitRequest req, string performedBy)
    {
        var type = req.RemittanceType;
        var (label, form, payableCode) = Meta(type);
        if (string.IsNullOrEmpty(payableCode))
            throw new InvalidOperationException($"ประเภทนำส่งไม่ถูกต้อง: {type}");

        // กันนำส่งซ้ำงวดเดิม
        var dup = await _db.Set<StatutoryRemittance>().AnyAsync(r => r.CompanyId == companyId
            && !r.IsDeleted && r.RemittanceType == type
            && r.PeriodYear == req.PeriodYear && r.PeriodMonth == req.PeriodMonth);
        if (dup)
            throw new InvalidOperationException(
                $"{form} งวด {req.PeriodMonth:D2}/{req.PeriodYear} นำส่งไปแล้ว — ดูในประวัติ");

        // หายอดค้าง + breakdown จาก dashboard (source of truth เดียวกับที่แสดง)
        var dash = await GetDashboardAsync(companyId, 24);
        var item = dash.Pending.FirstOrDefault(p => p.RemittanceType == type
            && p.PeriodYear == req.PeriodYear && p.PeriodMonth == req.PeriodMonth)
            ?? throw new InvalidOperationException(
                $"ไม่มียอดค้างนำส่งของ {form} งวด {req.PeriodMonth:D2}/{req.PeriodYear}");

        var amount = item.Amount;
        var lateFee = (type == "SsoSps110" && req.IncludeLateFee)
            ? PayrollService.ComputeSsoLateFee(req.PeriodYear, req.PeriodMonth, req.PayDate, amount)
            : 0m;

        // ผัง Cr (แหล่งเงิน)
        var bankGlId = await ResolveBankGlAsync(companyId, req.BankAccountId, req.BankGlAccountId);
        var payable = await ResolveAccountAsync(companyId, payableCode)
            ?? throw new InvalidOperationException($"ไม่พบผังบัญชี {payableCode} ({label}) — กรุณาสร้างก่อน");

        // ── สร้าง JE ──
        var lines = new List<JournalLineRequest>();
        if (type == "VatPp30")
        {
            // Dr ภาษีขาย (21911 = output) / Cr ภาษีซื้อ (11610 = input) / Cr ธนาคาร (net)
            var output = item.OutputVat ?? amount;
            var input = item.InputVat ?? 0m;
            lines.Add(new JournalLineRequest(payable.Id, output, 0, "ล้างภาษีขาย ภพ.30 (Dr 21911)"));
            if (input > 0)
            {
                var inputAcc = await ResolveAccountAsync(companyId, "11610");
                if (inputAcc != null)
                    lines.Add(new JournalLineRequest(inputAcc.Id, 0, input, "ล้างภาษีซื้อ ภพ.30 (Cr 11610)"));
                else // ไม่พบ 11610 → ลง net ตรง ๆ
                    amount = output;
            }
            lines.Add(new JournalLineRequest(bankGlId, 0, amount, $"จ่ายภาษีมูลค่าเพิ่ม {req.PeriodMonth:D2}/{req.PeriodYear}"));
        }
        else
        {
            lines.Add(new JournalLineRequest(payable.Id, amount, 0, $"ล้าง{label}ค้างจ่าย (Dr {payableCode})"));
            if (lateFee > 0)
            {
                var feeAcc = await ResolveLateFeeAccountAsync(companyId);
                if (feeAcc != null)
                    lines.Add(new JournalLineRequest(feeAcc.Id, lateFee, 0, "เงินเพิ่มนำส่งช้า (§49 2%/เดือน)"));
                else lateFee = 0m;
            }
            lines.Add(new JournalLineRequest(bankGlId, 0, amount + lateFee, $"นำส่ง{form} {req.PeriodMonth:D2}/{req.PeriodYear}"));
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var jeReq = new CreateJournalEntryRequest(
                EntryDate: req.PayDate,
                Description: $"นำส่ง{form} งวด {req.PeriodMonth:D2}/{req.PeriodYear}"
                    + (string.IsNullOrWhiteSpace(req.FilingNumber) ? "" : $" — เลขรับ {req.FilingNumber}"),
                Reference: $"{form}-{req.PeriodYear}{req.PeriodMonth:D2}",
                Lines: lines,
                JournalType: JournalType.General);
            var je = await _accounting.CreateJournalEntryAsync(companyId, jeReq, performedBy);
            await _accounting.PostJournalEntryAsync(companyId, je.Id);

            var rec = new StatutoryRemittance
            {
                CompanyId = companyId,
                RemittanceType = type,
                PeriodYear = req.PeriodYear,
                PeriodMonth = req.PeriodMonth,
                Amount = amount,
                LateFee = lateFee,
                PayDate = req.PayDate,
                BankGlAccountId = bankGlId,
                JournalEntryId = je.Id,
                FilingNumber = req.FilingNumber,
                ReceiptAttachmentId = req.ReceiptAttachmentId,
                Note = req.Note,
                CreatedBy = performedBy,
            };
            _db.Set<StatutoryRemittance>().Add(rec);

            // SSO — stamp รอบเงินเดือนที่ผูก (ให้หน้า payroll แสดง settled ด้วย)
            if (type == "SsoSps110")
            {
                var runs = await _db.Set<PayrollRun>()
                    .Where(r => r.CompanyId == companyId && r.Status == "Paid"
                        && r.Year == req.PeriodYear && r.Month == req.PeriodMonth
                        && r.SsoSettledAt == null)
                    .ToListAsync();
                foreach (var run in runs)
                {
                    run.SsoSettledAt = req.PayDate;
                    run.SsoSettlementJournalEntryId = je.Id;
                    run.SsoFilingNumber = req.FilingNumber;
                    run.SsoLateFeeAmount = lateFee;
                }
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation("Remitted {Type} {Month}/{Year} amount {Amt} (+fee {Fee}) → JE {Je}",
                type, req.PeriodMonth, req.PeriodYear, amount, lateFee, je.Id);

            return new RemitResult(rec.Id, je.Id, amount, lateFee,
                $"นำส่ง{form} งวด {req.PeriodMonth:D2}/{req.PeriodYear} สำเร็จ ({amount + lateFee:N2} บาท)");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>รับรู้ภาษีซื้อ ภ.พ.36 หลังได้ใบเสร็จกรมสรรพากร (§77/2: เคลมได้เดือน
    /// ที่นำส่ง) — JE: Dr 11610 ภาษีซื้อ ภ.พ.30 / Cr 11640 ยังไม่ถึงกำหนด + stamp
    /// InputVatBecameClaimableAt ลงเอกสาร → ภ.พ.30 เดือนที่รับรู้ include ให้เอง.
    /// เรียกได้หลังนำส่ง (มี remittance VatPp36 งวดนั้น). idempotent ผ่าน JE
    /// Reference ภ.พ.36R-YYYYMM + เอกสารที่ stamp แล้วไม่นับซ้ำ.</summary>
    public async Task<RemitResult> RecognizePp36InputVatAsync(Guid companyId, int periodYear,
        int periodMonth, DateTime recognizeDate, string performedBy)
    {
        // ต้องนำส่งงวดนั้นก่อน (Excel flow: 15/6 นำส่ง → 16/6 ได้ใบเสร็จ → รับรู้)
        var remitted = await _db.Set<StatutoryRemittance>().AnyAsync(r => r.CompanyId == companyId
            && !r.IsDeleted && r.RemittanceType == "VatPp36"
            && r.PeriodYear == periodYear && r.PeriodMonth == periodMonth);
        if (!remitted)
            throw new InvalidOperationException(
                $"ยังไม่ได้นำส่ง ภ.พ.36 งวด {periodMonth:D2}/{periodYear} — นำส่งก่อนแล้วค่อยรับรู้ภาษีซื้อ");

        var refNo = $"ภ.พ.36R-{periodYear}{periodMonth:D2}";
        var dupJe = await _db.JournalEntries.AnyAsync(j => j.CompanyId == companyId
            && j.Reference == refNo && !j.IsDeleted && j.Status == JournalEntryStatus.Posted);
        if (dupJe)
            throw new InvalidOperationException($"งวด {periodMonth:D2}/{periodYear} รับรู้ภาษีซื้อไปแล้ว (JE {refNo})");

        // เอกสารบริการ ตปท. ของงวด ที่ VAT ยังพักอยู่ 11640 (ยังไม่เคยรับรู้)
        var docs = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.IsForeignService && d.VatAmount > 0
                && d.InputVatBecameClaimableAt == null
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .ToListAsync();
        docs = docs.Where(d =>
        {
            var dt = d.PaymentDate ?? d.DocumentDate;
            return dt.Year == periodYear && dt.Month == periodMonth;
        }).ToList();
        var vatTotal = docs.Sum(d => d.VatAmount);
        if (vatTotal <= 0.009m)
            throw new InvalidOperationException(
                $"ไม่มีภาษีซื้อ ภ.พ.36 ค้างรับรู้ในงวด {periodMonth:D2}/{periodYear}");

        var claimAcc = await ResolveAccountAsync(companyId, "11610")
            ?? throw new InvalidOperationException("ไม่พบผังบัญชี 11610 (ภาษีซื้อ ภ.พ.30)");
        var undueAcc = await ResolveAccountAsync(companyId, "11640")
            ?? await ResolveAccountAsync(companyId, "11630")
            ?? throw new InvalidOperationException("ไม่พบผังบัญชี 11640 (ภาษีซื้อยังไม่ถึงกำหนด)");

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var jeReq = new CreateJournalEntryRequest(
                EntryDate: recognizeDate,
                Description: $"รับรู้ภาษีซื้อ ภ.พ.36 งวด {periodMonth:D2}/{periodYear} (ได้ใบเสร็จกรมสรรพากร §77/2)",
                Reference: refNo,
                Lines: new List<JournalLineRequest>
                {
                    new(claimAcc.Id, vatTotal, 0, "ภาษีซื้อ ภ.พ.30 (จาก ภ.พ.36 ที่นำส่งแล้ว)"),
                    new(undueAcc.Id, 0, vatTotal, "ล้างภาษีซื้อยังไม่ถึงกำหนด (ภ.พ.36)"),
                },
                JournalType: JournalType.General);
            var je = await _accounting.CreateJournalEntryAsync(companyId, jeReq, performedBy);
            await _accounting.PostJournalEntryAsync(companyId, je.Id);

            // stamp เอกสาร → ภ.พ.30 เดือนที่รับรู้จะ include ภาษีซื้อก้อนนี้
            foreach (var d in docs)
            {
                d.InputVatBecameClaimableAt = recognizeDate;
                d.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation("Recognized PP36 input VAT {Month}/{Year} {Amt} ({Docs} docs) → JE {Je}",
                periodMonth, periodYear, vatTotal, docs.Count, je.Id);
            return new RemitResult(Guid.Empty, je.Id, vatTotal, 0m,
                $"รับรู้ภาษีซื้อ ภ.พ.36 งวด {periodMonth:D2}/{periodYear} จำนวน {vatTotal:N2} บาท ({docs.Count} เอกสาร) — จะเข้า ภ.พ.30 เดือน {recognizeDate:MM/yyyy}");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task AttachReceiptAsync(Guid companyId, Guid remittanceId, Guid attachmentId)
    {
        var rec = await _db.Set<StatutoryRemittance>()
            .FirstOrDefaultAsync(r => r.Id == remittanceId && r.CompanyId == companyId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการนำส่ง");
        rec.ReceiptAttachmentId = attachmentId;
        rec.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ===== helpers =====
    private async Task<ChartOfAccount?> ResolveAccountAsync(Guid companyId, string code) =>
        await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode == code && a.Level >= 4 && !a.IsDeleted);

    private async Task<ChartOfAccount?> ResolveLateFeeAccountAsync(Guid companyId) =>
        await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.IsActive && !a.IsDeleted && a.Level >= 4
            && a.AccountCode.StartsWith("54")
            && (a.AccountName.Contains("เงินเพิ่ม") || a.AccountName.Contains("ค่าปรับ")))
        ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.IsActive && !a.IsDeleted && a.Level >= 4
            && a.AccountCode.StartsWith("58"));

    private async Task<Guid> ResolveBankGlAsync(Guid companyId, Guid? bankAccountId, Guid? bankGlAccountId = null)
    {
        Guid? bankGlId = null;
        // 1) เลือก GL เงินสด/ธนาคาร/ช่องจ่ายอื่นโดยตรง (payment channel) — ต้องเป็น
        //    ผังของบริษัทนี้ + active + posting level (anti-spoof: validate ตัวตน)
        if (bankGlAccountId.HasValue && bankGlAccountId.Value != Guid.Empty)
        {
            bankGlId = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == bankGlAccountId.Value && a.CompanyId == companyId
                    && a.IsActive && !a.IsDeleted && a.Level >= 4)
                .Select(a => (Guid?)a.Id).FirstOrDefaultAsync();
            if (!bankGlId.HasValue)
                throw new InvalidOperationException("บัญชีแหล่งเงินที่เลือกไม่ถูกต้อง — เลือกใหม่อีกครั้ง");
        }
        // 2) เลือกผ่าน BankAccount (map → LinkedAccountId)
        if (!bankGlId.HasValue && bankAccountId.HasValue)
        {
            bankGlId = await _db.BankAccounts.AsNoTracking()
                .Where(b => b.Id == bankAccountId.Value && b.CompanyId == companyId)
                .Select(b => b.LinkedAccountId).FirstOrDefaultAsync();
            if (!bankGlId.HasValue)
                throw new InvalidOperationException("บัญชีธนาคารที่เลือกยังไม่ผูกผังบัญชี (LinkedAccountId)");
        }
        if (!bankGlId.HasValue)
        {
            var cs = await _db.Set<CompanySettings>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted)
                .Select(c => c.DefaultPaymentAccountId).FirstOrDefaultAsync();
            if (cs.HasValue) bankGlId = cs;
        }
        if (!bankGlId.HasValue)
            bankGlId = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                    && a.AccountCode.StartsWith("111") && a.Level >= 4)
                .OrderBy(a => a.AccountCode).Select(a => (Guid?)a.Id).FirstOrDefaultAsync();
        return bankGlId ?? throw new InvalidOperationException("ไม่พบบัญชีเงินสด/ธนาคาร (111x)");
    }
}
