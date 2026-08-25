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
        ILogger<StatutoryRemittanceService> logger, ITaxService tax)
    {
        _db = db; _accounting = accounting; _logger = logger; _tax = tax;
    }
    private readonly ITaxService _tax;

    // ===== ป้ายชื่อ + ผังหนี้ค้างจ่าย ต่อประเภท =====
    private static (string Label, string Form, string PayableCode) Meta(string type) => type switch
    {
        "SsoSps110" => ("ประกันสังคม", "สปส.1-10", "21815"),
        "WhtPnd1"   => ("ภาษีหัก ณ ที่จ่าย (เงินเดือน)", "ภ.ง.ด.1", "21914"),
        "WhtPnd3"   => ("ภาษีหัก ณ ที่จ่าย (บุคคลธรรมดา)", "ภ.ง.ด.3", "21916"),
        "WhtPnd53"  => ("ภาษีหัก ณ ที่จ่าย (นิติบุคคล)", "ภ.ง.ด.53", "21917"),
        // ม.70 — WHT จ่ายนิติบุคคลต่างประเทศ (คู่กับ ภ.พ.36 บนใบเดียวกัน):
        // 15% ทั่วไป / 10% เงินปันผล / DTA อาจลด-ยกเว้น. กำหนดยื่น 7/15 ตาม default
        "WhtPnd54"  => ("ภาษีหัก ณ ที่จ่าย (จ่ายต่างประเทศ ม.70)", "ภ.ง.ด.54", "21918"),
        "VatPp30"   => ("ภาษีมูลค่าเพิ่ม", "ภ.พ.30", "21911"),
        // §83/6 reverse charge — VAT ประเมินเองจากจ่ายค่าบริการ ตปท. (ตั้งหนี้
        // Cr 21912 ตอนบันทึกเอกสาร IsForeignService → นำส่ง Dr 21912/Cr ธนาคาร)
        "VatPp36"   => ("ภาษีมูลค่าเพิ่ม (บริการต่างประเทศ §83/6)", "ภ.พ.36", "21912"),
        _           => ("ไม่ทราบ", type, "")
    };

    // กำหนดยื่น: (กระดาษวันที่, e-Filing วันที่) ของเดือนถัดจากงวด
    internal static (DateTime Paper, DateTime EFiling) DueDates(string type, int year, int month)
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
                    d.IsForeignService,
                    CType = d.Contact != null ? d.Contact.ContactType : ContactType.Individual })
                .ToListAsync();
            // ม.70: WHT จ่ายต่างประเทศ (ใบ ภ.พ.36) ยื่น **ภ.ง.ด.54** — ห้ามนับปน
            // ภงด.3/53 (เดิมตกใน 53 ตาม ContactType → ยื่นผิดแบบ + 21918 ว่างตลอด)
            foreach (var typ in new[] { "WhtPnd3", "WhtPnd53", "WhtPnd54" })
            {
                bool juristic = typ == "WhtPnd53";
                var grouped = whtDocs
                    .Where(d => typ == "WhtPnd54"
                        ? d.IsForeignService
                        : !d.IsForeignService
                          && (juristic ? d.CType == ContactType.JuristicPerson : d.CType != ContactType.JuristicPerson))
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

        // ── ภ.พ.36: นำส่งแล้ว แต่ยังไม่ได้ "รับรู้ภาษีซื้อ" (ขั้นที่ 2) ──
        // ผู้ใช้เจอจริง: นำส่งเสร็จแล้วไปหาใบใน ภ.พ.30 ไม่เจอ — เพราะภาษีซื้อ
        // ยังพักที่ 11640 จนกว่าจะกดรับรู้ (ได้ใบเสร็จ RD §77/2) และปุ่มรับรู้
        // ซ่อนอยู่ในแท็บประวัติโดยไม่มีสถานะบอก. ตรวจจาก JE รับรู้ (Reference
        // ภ.พ.36R-YYYYMM — กติกา idempotent เดิมของ RecognizePp36InputVatAsync)
        // + นับใบที่ยังพักจริง ๆ ในงวดนั้น
        var pp36Awaiting = new List<Pp36AwaitingRecognitionItem>();
        var pp36Remits = remits.Where(r => r.RemittanceType == "VatPp36").ToList();
        var pp36RecognizedRefs = pp36Remits.Count == 0
            ? new HashSet<string>()
            : (await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && !j.IsDeleted
                    && j.Status == JournalEntryStatus.Posted
                    && j.Reference != null && j.Reference.StartsWith("ภ.พ.36R-"))
                .Select(j => j.Reference!)
                .ToListAsync()).ToHashSet();
        if (pp36Remits.Count > 0)
        {
            // ใบที่ยังพัก 11640 (เงื่อนไขชุดเดียวกับ RecognizePp36InputVatAsync)
            var awaitingDocs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.IsForeignService && d.VatAmount > 0
                    && d.InputVatBecameClaimableAt == null
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.VatAmount, d.PaymentDate, d.DocumentDate })
                .ToListAsync();
            foreach (var r in pp36Remits)
            {
                if (pp36RecognizedRefs.Contains($"ภ.พ.36R-{r.PeriodYear}{r.PeriodMonth:D2}")) continue;
                var inPeriod = awaitingDocs.Where(d =>
                {
                    var dt = d.PaymentDate ?? d.DocumentDate;
                    return dt.Year == r.PeriodYear && dt.Month == r.PeriodMonth;
                }).ToList();
                if (inPeriod.Count == 0) continue;
                pp36Awaiting.Add(new Pp36AwaitingRecognitionItem(
                    r.PeriodYear, r.PeriodMonth,
                    inPeriod.Sum(d => d.VatAmount), inPeriod.Count, r.PayDate));
            }
            pp36Awaiting = pp36Awaiting
                .OrderBy(a => a.PeriodYear).ThenBy(a => a.PeriodMonth).ToList();
        }

        // ── ประวัติที่นำส่งล่าสุด ──
        var history = remits.OrderByDescending(r => r.PayDate).Take(30)
            .Select(r => new RemittanceHistoryItem(r.Id, r.RemittanceType, Meta(r.RemittanceType).Form,
                r.PeriodYear, r.PeriodMonth, r.Amount, r.LateFee, r.PayDate, r.FilingNumber,
                r.JournalEntryId, r.ReceiptAttachmentId, r.CreatedBy, r.CreatedAt,
                Pp36Recognized: r.RemittanceType == "VatPp36"
                    ? pp36RecognizedRefs.Contains($"ภ.พ.36R-{r.PeriodYear}{r.PeriodMonth:D2}")
                    : (bool?)null))
            .ToList();

        pending = pending.OrderByDescending(p => p.IsOverdue)
            .ThenBy(p => p.EFilingDueDate).ToList();

        return new RemittanceDashboardResponse(
            TotalPending: pending.Sum(p => p.Amount),
            TotalOverdue: pending.Where(p => p.IsOverdue).Sum(p => p.Amount),
            OverdueCount: pending.Count(p => p.IsOverdue),
            Pending: pending,
            RecentHistory: history,
            Pp36AwaitingRecognition: pp36Awaiting);
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

    // ══════════════════════════════════════════════════════════════════════
    //  ปฏิทินนำส่ง — "เดือนไหนยื่นแล้ว/ยัง" (ใช้บน dashboard)
    //
    //  ทำไมไม่ reuse GetDashboardAsync: อันนั้นตอบ "ค้างเท่าไร" จึง `continue`
    //  ทุกงวดที่ยอด ≤ 0 ผลคือเดือนที่ไม่มีรายการ **หายไปจากจอ** ทั้งที่ยังต้อง
    //  ยื่นแบบเปล่า (ภ.พ.30 §83 / สปส.1-10 / ภ.ง.ด.1) และเดือนที่ยังไม่ได้กด
    //  "สร้างรายงาน ภ.พ.30" ก็หายไปเหมือนกัน — ผู้ใช้เห็นจอว่างแล้วเข้าใจว่า
    //  "ไม่มีอะไรต้องทำ" ซึ่งเป็นความเข้าใจผิดที่มีค่าปรับตามมา. ปฏิทินนี้จึง
    //  ไล่ "ทุกเดือน × ทุกแบบ" แล้วให้สถานะครบ 5 แบบ:
    //     Filed / Partial / Pending / Unknown / NotRequired
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>แบบที่ต้องยื่นทุกเดือนแม้ยอด 0 + มาตราที่บังคับ</summary>
    internal static (bool Always, string Legal) FilingRule(string type) => type switch
    {
        "VatPp30"   => (true,  "ผู้ประกอบการจด VAT ต้องยื่นทุกเดือนแม้ไม่มีรายรับ (ป.รัษฎากร §83)"),
        "SsoSps110" => (true,  "นายจ้างที่ขึ้นทะเบียนต้องยื่นทุกเดือนแม้ไม่มีค่าจ้าง (พ.ร.บ.ประกันสังคม §47)"),
        "WhtPnd1"   => (true,  "ยื่นทุกเดือนที่มีการจ่ายเงินได้ ม.40(1)(2) แม้ภาษีหัก = 0 (ท.ป.4/2528)"),
        "WhtPnd3"   => (false, "ยื่นเฉพาะเดือนที่มีการหักภาษีบุคคลธรรมดา (ท.ป.4/2528)"),
        "WhtPnd53"  => (false, "ยื่นเฉพาะเดือนที่มีการหักภาษีนิติบุคคล (ท.ป.4/2528)"),
        "VatPp36"   => (false, "ยื่นเฉพาะเดือนที่จ่ายค่าบริการต่างประเทศ (ป.รัษฎากร §83/6)"),
        _           => (false, "")
    };

    // snapshot ที่อ่านมาครั้งเดียวแล้วส่งต่อให้ BuildCell — ใช้ record แทน tuple
    // ยาว ๆ เพื่อให้ชื่อฟิลด์ถูกบังคับตอน compile (tuple ชื่อไม่ตรงจะเงียบ)
    private sealed record ReportSnap(TaxType Type, int Year, int Month, bool Filed,
        DateTime? FiledAt, decimal NetVat);
    private sealed record RunSnap(int Year, int Month, decimal Emp, decimal Empr, decimal Wht,
        DateTime? SsoSettledAt, string? SsoFiling);
    private sealed record WhtSnap(int Year, int Month, decimal Wht, bool Juristic);
    private sealed record FsSnap(int Year, int Month, decimal Vat);

    /// <summary>map ชนิดนำส่ง → TaxType ของรายงานภาษีในระบบ (ใช้เช็ค "ยื่นแบบแล้ว")</summary>
    private static TaxType? ReportTypeOf(string type) => type switch
    {
        "VatPp30"   => TaxType.VAT,
        "WhtPnd1"   => TaxType.WithholdingTax1,
        "WhtPnd3"   => TaxType.WithholdingTax3,
        "WhtPnd53"  => TaxType.WithholdingTax53,
        "SsoSps110" => TaxType.SocialSecurity,
        "VatPp36"   => TaxType.VatPp36,
        _           => null
    };

    public async Task<FilingCalendarResponse> GetFilingCalendarAsync(Guid companyId, int months = 12)
    {
        months = Math.Clamp(months, 3, 24);
        // กำหนดยื่นเป็นเวลาไทย — ใช้ UTC ตรง ๆ จะเพี้ยน 1 วันช่วงเย็น (UTC+7)
        // และวันที่ 15 คือเส้นตายจริง คลาดเคลื่อนวันเดียว = แจ้งเตือนผิด
        var today = DateTime.UtcNow.AddHours(7).Date;
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        var startMonth = thisMonth.AddMonths(-(months - 1));
        var periods = Enumerable.Range(0, months).Select(i => startMonth.AddMonths(i)).ToList();

        // ── บริษัทอยู่ในข่ายต้องยื่นแบบไหนบ้าง ──
        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.IsVatRegistered, c.IsSocialSecurityRegistered, c.CreatedAt })
            .FirstOrDefaultAsync();
        var vatRegistered = company?.IsVatRegistered ?? false;
        var ssoRegistered = company?.IsSocialSecurityRegistered ?? false;
        // เดือนก่อนเปิดบริษัทในระบบ ระบบไม่มีทางรู้ว่ายื่นหรือยัง — ขึ้น "เลยกำหนด"
        // ทั้งแถวจะเป็นการเตือนเท็จที่ทำให้ผู้ใช้เลิกเชื่อปฏิทินนี้ทั้งอัน
        var systemStart = company == null
            ? startMonth
            : new DateTime(company.CreatedAt.Year, company.CreatedAt.Month, 1);

        var activeEmployees = 0;
        try
        {
            activeEmployees = await _db.Employees.AsNoTracking()
                .CountAsync(e => e.CompanyId == companyId && !e.IsDeleted && e.IsActive);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "นับพนักงาน active ไม่สำเร็จ"); }

        // ── หลักฐาน "จ่ายเงินแล้ว" ──
        var remits = new List<StatutoryRemittance>();
        try
        {
            remits = await _db.Set<StatutoryRemittance>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted
                    && (r.PeriodYear > startMonth.Year
                        || (r.PeriodYear == startMonth.Year && r.PeriodMonth >= startMonth.Month)))
                .ToListAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลด StatutoryRemittances ไม่สำเร็จ"); }

        // ── หลักฐาน "ยื่นแบบแล้ว" (คนละเหตุการณ์กับจ่ายเงิน — งวดขอคืน/ยอด 0
        //    ยื่นแต่ไม่ได้จ่าย) ──
        var reports = new List<ReportSnap>();
        try
        {
            reports = (await _db.TaxReports.AsNoTracking()
                .Where(t => t.CompanyId == companyId && !t.IsDeleted
                    && (t.Year > startMonth.Year || (t.Year == startMonth.Year && t.Month >= startMonth.Month)))
                .Select(t => new { t.TaxType, t.Year, t.Month, t.Status, t.FiledDate, t.NetVat })
                .ToListAsync())
                .Select(t => new ReportSnap(t.TaxType, t.Year, t.Month,
                    t.Status == TaxReportStatus.Filed, t.FiledDate, t.NetVat))
                .ToList();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลด TaxReports ไม่สำเร็จ"); }

        // ── ฐานยอดที่ต้องนำส่ง ──
        var runs = new List<RunSnap>();
        try
        {
            runs = (await _db.PayrollRuns.AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.Status == "Paid")
                .Select(r => new { r.Year, r.Month, r.TotalSocialSecurityEmployee,
                    r.TotalSocialSecurityEmployer, r.TotalWithholdingTax, r.SsoSettledAt, r.SsoFilingNumber })
                .ToListAsync())
                .Select(r => new RunSnap(r.Year, r.Month, r.TotalSocialSecurityEmployee,
                    r.TotalSocialSecurityEmployer, r.TotalWithholdingTax, r.SsoSettledAt, r.SsoFilingNumber))
                .ToList();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลด PayrollRuns ไม่สำเร็จ"); }

        var whtDocs = new List<WhtSnap>();
        var fsDocs = new List<FsSnap>();
        try
        {
            var docs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && (d.WithholdingTaxAmount > 0 || (d.IsForeignService && d.VatAmount > 0))
                    && (d.PaymentDate ?? d.DocumentDate) >= startMonth
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.WithholdingTaxAmount, d.VatAmount, d.IsForeignService,
                    d.PaymentDate, d.DocumentDate,
                    CType = d.Contact != null ? d.Contact.ContactType : ContactType.Individual })
                .ToListAsync();
            foreach (var d in docs)
            {
                var dt = d.PaymentDate ?? d.DocumentDate;
                if (d.WithholdingTaxAmount > 0)
                    whtDocs.Add(new WhtSnap(dt.Year, dt.Month, d.WithholdingTaxAmount,
                        d.CType == ContactType.JuristicPerson));
                if (d.IsForeignService && d.VatAmount > 0)
                    fsDocs.Add(new FsSnap(dt.Year, dt.Month, d.VatAmount));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลดเอกสารหัก ณ ที่จ่าย/บริการต่างประเทศ ไม่สำเร็จ"); }

        // ── ประกอบเป็นตาราง ──
        var rows = new List<FilingCalendarRow>();
        var order = new[] { "VatPp30", "WhtPnd1", "SsoSps110", "WhtPnd3", "WhtPnd53", "VatPp36" };
        foreach (var type in order)
        {
            var (label, form, _) = Meta(type);
            var (always, legal) = FilingRule(type);
            var reportType = ReportTypeOf(type);

            // บริษัทอยู่ในข่ายไหม
            bool applicable = true; string? naReason = null;
            if (type == "VatPp30" && !vatRegistered)
            { applicable = false; naReason = "บริษัทยังไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม"; }
            else if (type == "SsoSps110" && !ssoRegistered)
            { applicable = false; naReason = "บริษัทยังไม่ได้ขึ้นทะเบียนนายจ้างกับประกันสังคม"; }
            else if (type == "WhtPnd1" && activeEmployees == 0 && runs.Count == 0)
            { applicable = false; naReason = "ยังไม่มีพนักงานในระบบ"; }

            var cells = new List<FilingCalendarCell>();
            foreach (var p in periods)
            {
                cells.Add(applicable
                    ? BuildCell(type, form, always, p, today, systemStart, remits, reports, reportType,
                        runs, whtDocs, fsDocs, activeEmployees)
                    : new FilingCalendarCell(p.Year, p.Month, "NotRequired", 0, 0,
                        DueDates(type, p.Year, p.Month).Paper, DueDates(type, p.Year, p.Month).EFiling,
                        false, 0, false, null, false, null, null, false, false,
                        naReason ?? "ไม่อยู่ในข่ายต้องยื่น", null));
            }

            // ซ่อนแถวที่ไม่เกี่ยวเลย (ไม่มีเดือนไหนต้องยื่น + ไม่เคยยื่น) — ลด noise
            var meaningful = applicable
                && cells.Any(c => c.Status != "NotRequired" || c.Remitted || c.FormFiled);
            if (!meaningful && !always) continue;

            rows.Add(new FilingCalendarRow(type, form, label, legal, always,
                applicable, naReason, cells));
        }

        // ── สรุปหัวข้อ ──
        var all = rows.SelectMany(r => r.Cells).ToList();
        var overdue = all.Where(c => c.Overdue).ToList();
        var required = all.Where(c => c.Status is "Filed" or "Partial" or "Pending" or "Unknown").ToList();
        var filed = all.Count(c => c.Status == "Filed");
        var dueSoon = all.Count(c => !c.Overdue && (c.Status is "Pending" or "Partial")
            && c.DaysToDue >= 0 && c.DaysToDue <= 7);
        var unknown = all.Count(c => c.Status == "Unknown");

        // จับคู่ cell กับแถวเจ้าของไว้ตั้งแต่แรก — FilingCalendarCell เป็น record
        // (value equality) การไล่หาแถวย้อนหลังด้วย Contains จะจับแถวผิดได้เมื่อสอง
        // แบบมีช่องที่ค่าเท่ากันทุกฟิลด์ (เช่น NotRequired เดือนเดียวกัน)
        var next = rows
            .SelectMany(r => r.Cells.Select(c => new { Row = r, Cell = c }))
            .Where(x => (x.Cell.Status is "Pending" or "Partial" or "Unknown") && x.Cell.DaysToDue >= 0)
            .OrderBy(x => x.Cell.EFilingDueDate).ThenBy(x => x.Row.FormCode)
            .FirstOrDefault();
        string? nextLabel = next == null ? null
            : $"{next.Row.FormCode} งวด {next.Cell.Month:D2}/{next.Cell.Year} — ครบกำหนด {next.Cell.EFilingDueDate:dd/MM/yyyy}"
              + (next.Cell.DaysToDue == 0 ? " (วันนี้!)" : $" (อีก {next.Cell.DaysToDue} วัน)");

        var headline = overdue.Count > 0
            ? $"⚠️ เลยกำหนดยื่น {overdue.Count} งวด — ยอด {overdue.Sum(c => c.Amount):N2} บาท"
              + (overdue.Sum(c => c.LateFee) > 0 ? $" + เงินเพิ่มประมาณ {overdue.Sum(c => c.LateFee):N2} บาท" : "")
            : unknown > 0
                ? $"มี {unknown} งวดที่ระบบยังไม่ทราบยอด — สร้างรายงาน/รันเงินเดือนก่อนถึงจะรู้ว่าต้องยื่นเท่าไร"
                : dueSoon > 0
                    ? $"ครบกำหนดยื่นภายใน 7 วัน {dueSoon} งวด" + (nextLabel != null ? $" — {nextLabel}" : "")
                    : required.Count == 0
                        ? "ยังไม่มีภาระยื่นแบบในช่วงนี้"
                        : $"✓ ยื่นครบทุกงวดที่ถึงกำหนด ({filed}/{required.Count})";

        return new FilingCalendarResponse(
            Periods: periods.Select(p => $"{p.Year}-{p.Month:D2}").ToList(),
            Rows: rows,
            OverdueCount: overdue.Count,
            OverdueAmount: overdue.Sum(c => c.Amount),
            OverdueLateFee: overdue.Sum(c => c.LateFee),
            DueSoonCount: dueSoon,
            UnknownCount: unknown,
            FiledCount: filed,
            RequiredCount: required.Count,
            NextDueDate: next?.Cell.EFilingDueDate,
            NextDueLabel: nextLabel,
            Headline: headline);
    }

    private FilingCalendarCell BuildCell(string type, string form, bool alwaysRequired,
        DateTime period, DateTime today, DateTime systemStart,
        List<StatutoryRemittance> remits,
        List<ReportSnap> reports,
        TaxType? reportType,
        List<RunSnap> runs,
        List<WhtSnap> whtDocs,
        List<FsSnap> fsDocs,
        int activeEmployees)
    {
        int y = period.Year, m = period.Month;
        var (paper, efiling) = DueDates(type, y, m);
        var daysToDue = (int)(efiling.Date - today).TotalDays;
        var isCurrentPeriod = period.Year == today.Year && period.Month == today.Month;

        // ── หลักฐานที่มี ──
        var myRemits = remits.Where(r => r.RemittanceType == type && r.PeriodYear == y && r.PeriodMonth == m).ToList();
        var remittedAmount = myRemits.Sum(r => r.Amount);
        // regenerate ทำให้มีได้หลายรายงานต่อเดือน — เอาใบที่ "ยื่นแล้ว" ก่อนเสมอ
        // ไม่งั้นหยิบ Draft ที่สร้างทีหลังมาแล้วรายงานว่ายังไม่ได้ยื่น
        var rep = reportType.HasValue
            ? reports.Where(r => r.Type == reportType.Value && r.Year == y && r.Month == m)
                     .OrderByDescending(r => r.Filed).FirstOrDefault()
            : null;
        var hasReport = rep != null;
        var monthRuns = runs.Where(r => r.Year == y && r.Month == m).ToList();

        // ── ยอดที่ต้องนำส่ง + "รู้ยอดหรือยัง" ──
        decimal amount; bool known = true; string unknownHint = ""; string? unknownUrl = null;
        switch (type)
        {
            case "VatPp30":
                // ยอดมาจากรายงาน ภ.พ.30 ที่ผู้ใช้กด "สร้างรายงาน" ไว้ — ไม่คำนวณสด
                // หลายเดือนในหน้านี้ (เคยทำให้หน้าค้าง)
                amount = rep?.NetVat ?? 0m;
                known = hasReport;
                unknownHint = "ยังไม่ได้สร้างรายงาน ภ.พ.30 ของงวดนี้ — สร้างก่อนถึงจะรู้ยอดที่ต้องชำระ";
                unknownUrl = $"/pages/tax.html?type=VAT&year={y}&month={m}";
                break;
            case "SsoSps110":
                amount = monthRuns.Sum(r => r.Emp + r.Empr);
                known = monthRuns.Count > 0 || activeEmployees == 0;
                unknownHint = "ยังไม่ได้รันเงินเดือนงวดนี้ — เงินสมทบยังคำนวณไม่ได้";
                unknownUrl = "/pages/payroll.html";
                break;
            case "WhtPnd1":
                amount = monthRuns.Sum(r => r.Wht);
                known = monthRuns.Count > 0 || activeEmployees == 0;
                unknownHint = "ยังไม่ได้รันเงินเดือนงวดนี้ — ภาษีหัก ณ ที่จ่ายยังคำนวณไม่ได้";
                unknownUrl = "/pages/payroll.html";
                break;
            case "WhtPnd3":
                amount = whtDocs.Where(d => d.Year == y && d.Month == m && !d.Juristic).Sum(d => d.Wht);
                break;
            case "WhtPnd53":
                amount = whtDocs.Where(d => d.Year == y && d.Month == m && d.Juristic).Sum(d => d.Wht);
                break;
            case "VatPp36":
                amount = fsDocs.Where(d => d.Year == y && d.Month == m).Sum(d => d.Vat);
                break;
            default:
                amount = 0m;
                break;
        }

        var formFiled = rep?.Filed == true;
        var filedAt = formFiled ? rep!.FiledAt : null;
        // ปกส. นำส่งจากหน้า payroll จะ stamp PayrollRun.SsoSettledAt โดยไม่สร้างแถว
        // StatutoryRemittance — ถ้าเช็คแค่ตารางเดียวจะรายงานว่า "ยังไม่นำส่ง" ทั้งที่จ่ายแล้ว
        var ssoStamped = type == "SsoSps110" && monthRuns.Any(r => r.SsoSettledAt != null);
        var remitted = myRemits.Count > 0 || ssoStamped;
        DateTime? remittedAt = myRemits.Count > 0
            ? myRemits.Max(r => r.PayDate)
            : (ssoStamped ? monthRuns.Where(r => r.SsoSettledAt != null).Max(r => r.SsoSettledAt) : null);
        var filingNumber = myRemits.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.FilingNumber))?.FilingNumber
            ?? monthRuns.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SsoFiling))?.SsoFiling;
        var hasReceipt = myRemits.Any(r => r.ReceiptAttachmentId != null);

        // รอบที่ปิดจากหน้า payroll ไม่มียอดในตาราง remittance — ถ้าไม่บวกกลับ ช่องจะ
        // ขึ้น "นำส่งแล้ว 0.00 จาก X" (Partial) ทั้งที่จ่ายครบแล้ว. ใช้ MAX ไม่ใช่ผลรวม
        // เพราะ RemitAsync สร้างแถว remittance *และ* stamp run พร้อมกัน = นับซ้ำ
        if (ssoStamped)
        {
            var settledFromRuns = monthRuns.Where(r => r.SsoSettledAt != null).Sum(r => r.Emp + r.Empr);
            remittedAmount = Math.Max(remittedAmount, settledFromRuns);
        }

        var isNil = known && Math.Abs(amount) <= 0.009m;
        var requiredThisMonth = alwaysRequired || amount > 0.009m || !known || remitted || formFiled;

        // งวดก่อนเปิดบริษัทในระบบ + ไม่มีร่องรอยใด ๆ → ไม่เตือน (ระบบไม่รู้จริง ๆ
        // ว่ายื่นหรือยัง การขึ้นแดงคือเดาแล้วเดาผิด) แต่ยังบอกให้ไปตรวจย้อนหลังเอง
        var beforeSystem = period < systemStart && !remitted && !formFiled && !hasReport && monthRuns.Count == 0;
        if (beforeSystem)
            return new FilingCalendarCell(y, m, "NotRequired", 0, 0, paper, efiling,
                false, daysToDue, false, null, false, null, null, false, false,
                "งวดก่อนเริ่มใช้ระบบ — ระบบไม่มีข้อมูล กรุณาตรวจการยื่นย้อนหลังจากเอกสารเดิม", null);

        // ── ตัดสินสถานะ ──
        string status, hint; string? url;
        var remittanceUrl = $"/pages/tax-remittance.html?type={type}&year={y}&month={m}";

        if (!requiredThisMonth)
        {
            status = "NotRequired";
            hint = $"เดือนนี้ไม่มีรายการที่ต้องยื่น {form}";
            url = null;
        }
        else if (!known)
        {
            // ต้องยื่นแน่ ๆ แต่ระบบยังบอกยอดไม่ได้ — งวดปัจจุบันถือว่าปกติ
            status = "Unknown";
            hint = isCurrentPeriod ? unknownHint + " (งวดยังไม่ปิด ถือว่าปกติ)" : unknownHint;
            url = unknownUrl;
        }
        else if (amount > 0.009m)
        {
            // มียอดต้องจ่าย → ถือว่าเสร็จเมื่อ "จ่ายครบ" (การจ่ายเกิดพร้อมการยื่น)
            if (remitted && remittedAmount + 0.009m >= amount)
            {
                status = "Filed";
                hint = $"นำส่งแล้ว {remittedAmount:N2} บาท"
                    + (remittedAt.HasValue ? $" เมื่อ {remittedAt:dd/MM/yyyy}" : "")
                    + (string.IsNullOrWhiteSpace(filingNumber) ? "" : $" · เลขรับ {filingNumber}");
                url = remittanceUrl;
            }
            else if (remitted || formFiled)
            {
                status = "Partial";
                hint = formFiled && !remitted
                    ? $"ยื่นแบบแล้วแต่ยังไม่ได้บันทึกการจ่าย {amount:N2} บาท"
                    : $"นำส่งแล้วบางส่วน {remittedAmount:N2} จาก {amount:N2} บาท — ยังค้าง {amount - remittedAmount:N2}";
                url = remittanceUrl;
            }
            else
            {
                status = "Pending";
                hint = $"ต้องนำส่ง {amount:N2} บาท ภายใน {efiling:dd/MM/yyyy} (e-Filing) / {paper:dd/MM/yyyy} (กระดาษ)";
                url = remittanceUrl;
            }
        }
        else
        {
            // ยอด 0 หรือขอคืน → ไม่มีเงินจ่าย แต่ยัง "ต้องยื่นแบบ"
            if (formFiled || remitted)
            {
                status = "Filed";
                hint = amount < -0.009m
                    ? $"ยื่นแล้ว — งวดนี้ขอคืน/ยกไป {Math.Abs(amount):N2} บาท"
                    : "ยื่นแบบเปล่าแล้ว (ไม่มียอดต้องชำระ)";
                url = remittanceUrl;
            }
            else
            {
                status = "Pending";
                hint = amount < -0.009m
                    ? $"งวดนี้ภาษีซื้อมากกว่าภาษีขาย {Math.Abs(amount):N2} บาท — ยังต้องยื่นแบบเพื่อขอคืน/ยกไปงวดหน้า"
                    : $"ไม่มียอดต้องชำระ แต่ยังต้อง “ยื่นแบบเปล่า” ภายใน {efiling:dd/MM/yyyy}";
                url = remittanceUrl;
            }
        }

        var incomplete = status is "Pending" or "Partial" or "Unknown";
        var overdueFlag = incomplete && today > efiling.Date;
        var lateFee = (overdueFlag && type == "SsoSps110" && amount > 0)
            ? PayrollService.ComputeSsoLateFee(y, m, today, amount)
            : 0m;
        if (overdueFlag)
            hint += $" · เลยกำหนดมาแล้ว {Math.Abs(daysToDue)} วัน";

        return new FilingCalendarCell(y, m, status, amount, lateFee, paper, efiling,
            overdueFlag, daysToDue, formFiled, filedAt, remitted, remittedAt,
            filingNumber, hasReceipt, isNil, hint, url);
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

            // ── ภ.พ.36: สร้าง "รายงานภาษี" ของงวดให้อัตโนมัติ ───────────────
            // ผู้ใช้เจอจริง: นำส่งไปแล้วหลายเดือน แต่เปิดแท็บ ภ.พ.36 ในหน้ารายงาน
            // ภาษีเห็นแค่เดือนเดียว → เข้าใจว่าเดือนอื่น "ไม่มียอด" ทั้งที่นำส่ง
            // ไปแล้ว. งวดที่นำส่งแล้ว = ยืนยันตัวเลขแล้ว จึงต้องมีรายงานคู่เสมอ
            // ⚠️ อยู่ **นอก** transaction ของการนำส่งโดยตั้งใจ (commit ไปแล้ว) —
            // สร้างรายงานพลาดต้องไม่ทำให้การนำส่ง (JE เงินจริง) ล้มตาม
            var reportNote = type == "VatPp36"
                ? await TryEnsurePp36ReportAsync(companyId, req.PeriodYear, req.PeriodMonth)
                : "";

            return new RemitResult(rec.Id, je.Id, amount, lateFee,
                $"นำส่ง{form} งวด {req.PeriodMonth:D2}/{req.PeriodYear} สำเร็จ ({amount + lateFee:N2} บาท){reportNote}");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>สร้างรายงานภาษี ภ.พ.36 ของงวดให้ถ้ายังไม่มี — idempotent
    /// (มีอยู่แล้วก็เงียบ) และ **ห้าม throw**: การนำส่งสำเร็จ+commit ไปแล้ว
    /// ห้ามล้มย้อนหลังเพราะสร้างรายงานไม่ผ่าน. คืนข้อความต่อท้ายผลนำส่ง
    /// (ค่าว่าง = ไม่ได้สร้างอะไรใหม่).</summary>
    private async Task<string> TryEnsurePp36ReportAsync(Guid companyId, int year, int month)
    {
        try
        {
            var exists = await _db.TaxReports.AnyAsync(t => t.CompanyId == companyId
                && t.TaxType == TaxType.VatPp36 && t.Year == year && t.Month == month);
            if (exists) return "";
            await _tax.GenerateTaxReportAsync(companyId,
                new Models.DTOs.Tax.CreateTaxReportRequest(TaxType.VatPp36, year, month));
            _logger.LogInformation("Auto-generated ภ.พ.36 tax report {Month}/{Year} after remit", month, year);
            return " · สร้างรายงาน ภ.พ.36 งวดนี้ให้อัตโนมัติแล้ว (ดูที่หน้ารายงานภาษี)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "สร้างรายงาน ภ.พ.36 อัตโนมัติไม่สำเร็จ {Month}/{Year} — นำส่งสำเร็จแล้ว ไม่กระทบ",
                month, year);
            return "";
        }
    }

    /// <summary>รับรู้ภาษีซื้อ ภ.พ.36 หลังได้ใบเสร็จกรมสรรพากร (§77/2: เคลมได้เดือน
    /// ที่นำส่ง) — JE: Dr 11610 ภาษีซื้อ ภ.พ.30 / Cr 11640 ยังไม่ถึงกำหนด + stamp
    /// InputVatBecameClaimableAt ลงเอกสาร → ภ.พ.30 เดือนที่รับรู้ include ให้เอง.
    /// เรียกได้หลังนำส่ง (มี remittance VatPp36 งวดนั้น). idempotent ผ่าน JE
    /// Reference ภ.พ.36R-YYYYMM + เอกสารที่ stamp แล้วไม่นับซ้ำ.</summary>
    public async Task<RemitResult> RecognizePp36InputVatAsync(Guid companyId, int periodYear,
        int periodMonth, DateTime? recognizeDate, string performedBy,
        string? rdReceiptNumber = null)
    {
        // ต้องนำส่งงวดนั้นก่อน (Excel flow: 15/6 นำส่ง → 16/6 ได้ใบเสร็จ → รับรู้)
        var remittance = await _db.Set<StatutoryRemittance>().FirstOrDefaultAsync(r =>
            r.CompanyId == companyId
            && !r.IsDeleted && r.RemittanceType == "VatPp36"
            && r.PeriodYear == periodYear && r.PeriodMonth == periodMonth);
        if (remittance == null)
            throw new InvalidOperationException(
                $"ยังไม่ได้นำส่ง ภ.พ.36 งวด {periodMonth:D2}/{periodYear} — นำส่งก่อนแล้วค่อยรับรู้ภาษีซื้อ");

        // §86/14 — ใบเสร็จ RD คือ "ใบกำกับภาษี" ของภาษีซื้อก้อนนี้: เลขที่ใบเสร็จ
        // (เลขรับจากการยื่น) ต้องมี เพื่อขึ้นเป็นเลขใบกำกับในรายงานภาษีซื้อ ภ.พ.30.
        // รับจาก request ก่อน (ผู้ใช้เพิ่งได้ใบเสร็จ อาจยังไม่เคยกรอก) → backfill
        // ลง remittance; ไม่ส่งมาก็ใช้เลขรับที่กรอกตอนนำส่ง
        if (!string.IsNullOrWhiteSpace(rdReceiptNumber))
        {
            rdReceiptNumber = rdReceiptNumber.Trim();
            if (string.IsNullOrWhiteSpace(remittance.FilingNumber))
            {
                remittance.FilingNumber = rdReceiptNumber;
                remittance.UpdatedAt = DateTime.UtcNow;
            }
        }
        var rdReceiptNo = !string.IsNullOrWhiteSpace(rdReceiptNumber)
            ? rdReceiptNumber : remittance.FilingNumber;

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

        // วันเคลม ภ.พ.30 ต่อใบ = "วันที่ใบกำกับผู้ขาย" ของใบนั้น (§82/3 เคลมตามวัน
        // ใบกำกับ) — ผู้ใช้เลือก default นี้. ถ้าผู้ใช้ระบุ recognizeDate มา = ใช้วันนั้น
        // ทั้งชุด (override). วันที่ JE = recognizeDate หรือ ใบกำกับล่าสุดในชุด
        DateTime ClaimDateOf(Document d) => recognizeDate ?? d.SupplierTaxInvoiceDate ?? d.PaymentDate ?? d.DocumentDate;
        var jeDate = recognizeDate ?? docs.Max(d => d.SupplierTaxInvoiceDate ?? d.PaymentDate ?? d.DocumentDate);

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var jeReq = new CreateJournalEntryRequest(
                EntryDate: jeDate,
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

            // stamp เอกสาร → ภ.พ.30 เดือน "วันที่ใบกำกับ" (หรือ recognizeDate ถ้าระบุ)
            // จะ include ภาษีซื้อก้อนนี้
            foreach (var d in docs)
            {
                d.InputVatBecameClaimableAt = ClaimDateOf(d);
                // §86/14: เลข/วันที่ใบเสร็จ RD = เลข/วันที่ใบกำกับของภาษีซื้อก้อนนี้
                // ในรายงาน ภ.พ.30 (ไม่ใช่เลข invoice ผู้ขาย ตปท.) — วันที่ใบเสร็จ
                // = วันจ่ายจริงของการนำส่ง (recognizeDate override ได้)
                d.Pp36RdReceiptNumber = rdReceiptNo;
                d.Pp36RdReceiptDate = recognizeDate ?? remittance.PayDate;
                // ⚠️ ตัวบล็อกที่ทำให้ "รับรู้แล้วแต่ไม่โผล่ใน ภ.พ.30/รายการดึงเอกสาร":
                // ทั้ง GenerateVatReport และ GetPullableDocuments รับ PV เข้าฝั่ง
                // ภาษีซื้อ **เฉพาะที่ HasTaxInvoiceReference=true** (นิยามเดิม =
                // "อ้างใบกำกับซื้อเพื่อขอเครดิต") — ใบ ภ.พ.36 ไม่มีใบกำกับไทยจึง
                // ไม่เคยติ๊ก ⇒ GL มี Dr 11610 แต่รายงานไม่มีแถว ไม่ reconcile.
                // หลังนำส่ง+ได้ใบเสร็จ RD ใบเสร็จนั้น**คือใบกำกับภาษี §86/14**
                // สิทธิ์เครดิตจึงสมบูรณ์ → เปิดธงเหมือนเส้น §86/4
                // (ReclassifyUndueInputVatAsync ทำแบบเดียวกันอยู่แล้ว)
                if (d.DocumentType == DocumentType.PaymentVoucher)
                    d.HasTaxInvoiceReference = true;
                d.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation("Recognized PP36 input VAT {Month}/{Year} {Amt} ({Docs} docs) → JE {Je}",
                periodMonth, periodYear, vatTotal, docs.Count, je.Id);

            // ── sync รายงานที่มีอยู่แล้วทันที (single source of truth) ──
            // ถ้างวดเคลมมีรายงานร่างอยู่ก่อน (สร้างก่อนกดรับรู้) บรรทัดใบพวกนี้
            // จะไม่มีทางโผล่จนกว่าจะ regenerate — ซึ่งล้างการติ๊ก/แก้ยอดของ
            // บรรทัดอื่นทั้งงวด (ผู้ใช้ปฏิเสธจะกดถูกแล้ว). ดึงทีละใบเข้ารายงาน
            // เดิมแทน (กติกาเต็มของ PullDocumentIntoReport — best-effort:
            // รับรู้สำเร็จไปแล้ว การ sync รายงานล้มต้องไม่ทำให้ transaction พัง)
            var pullNotes = new List<string>();
            foreach (var d in docs)
            {
                var note = await _tax.TryPullIntoDraftReportAsync(companyId, d.Id);
                _logger.LogInformation("PP36 auto-pull {Doc}: {Note}", d.DocumentNumber, note);
                pullNotes.Add(note);
            }
            var pulled = pullNotes.Count(n => n.StartsWith("ดึงใบ"));
            var pullSummary = pulled > 0 ? $" · ดึงเข้ารายงานร่างที่มีอยู่แล้ว {pulled} ใบ" : "";

            var claimMonths = string.Join(", ", docs.Select(ClaimDateOf).Select(dt => dt.ToString("MM/yyyy")).Distinct());
            return new RemitResult(Guid.Empty, je.Id, vatTotal, 0m,
                $"รับรู้ภาษีซื้อ ภ.พ.36 งวด {periodMonth:D2}/{periodYear} จำนวน {vatTotal:N2} บาท ({docs.Count} เอกสาร) — เข้า ภ.พ.30 เดือน {claimMonths} (ตามวันที่ใบกำกับ){pullSummary}");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>ใบของงวด ภ.พ.36 ที่รับรู้ภาษีซื้อแล้ว + สถานะใน ภ.พ.30 ของงวด
    /// เคลมแต่ละใบ — ตอบ "เข้า ภ.พ.30 แล้ว...แต่เปิดรายงานไม่เจอ": รายงานเป็น
    /// snapshot ถ้าสร้างไว้ก่อนกดรับรู้ บรรทัดใบนี้จะยังไม่อยู่จนกด "สร้างใหม่"
    /// (InReport=false + ReportStatus=Draft คือเคสนั้นพอดี)</summary>
    public async Task<List<Pp36RecognizedDocItem>> GetPp36RecognizedDocsAsync(
        Guid companyId, int periodYear, int periodMonth)
    {
        // ใบของงวด (ตามเดือนจ่าย — เกณฑ์เดียวกับ recognize) ที่ stamp งวดเคลมแล้ว
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.IsForeignService && d.VatAmount > 0
                && d.InputVatBecameClaimableAt != null
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .Select(d => new { d.Id, d.DocumentNumber, d.ContactId, d.VatAmount,
                d.PaymentDate, d.DocumentDate, d.InputVatBecameClaimableAt, d.Pp36RdReceiptNumber })
            .ToListAsync();
        docs = docs.Where(d =>
        {
            var dt = d.PaymentDate ?? d.DocumentDate;
            return dt.Year == periodYear && dt.Month == periodMonth;
        }).ToList();
        if (docs.Count == 0) return new List<Pp36RecognizedDocItem>();

        var contactIds = docs.Select(d => d.ContactId).Distinct().ToList();
        var contactNames = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        // รายงาน ภ.พ.30 ของทุกงวดเคลมที่เกี่ยว + บรรทัดของใบชุดนี้
        var claimKeys = docs.Select(d => (d.InputVatBecameClaimableAt!.Value.Year,
            d.InputVatBecameClaimableAt.Value.Month)).Distinct().ToList();
        var years = claimKeys.Select(k => k.Item1).Distinct().ToList();
        var reports = await _db.TaxReports.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.TaxType == TaxType.VAT
                && years.Contains(r.Year))
            .Select(r => new { r.Id, r.Year, r.Month, r.Status })
            .ToListAsync();
        var docIds = docs.Select(d => d.Id).ToList();
        var reportIds = reports.Select(r => r.Id).ToList();
        var linesInReports = await _db.TaxReportLines.AsNoTracking()
            .Where(l => l.DocumentId != null && docIds.Contains(l.DocumentId.Value)
                && reportIds.Contains(l.TaxReportId)
                && !l.IsExcluded && !l.IsDeleted && l.IncomeTypeCode == "INPUT")
            .Select(l => new { l.DocumentId, l.TaxReportId })
            .ToListAsync();

        return docs.Select(d =>
        {
            var cy = d.InputVatBecameClaimableAt!.Value.Year;
            var cm = d.InputVatBecameClaimableAt.Value.Month;
            var rep = reports.FirstOrDefault(r => r.Year == cy && r.Month == cm);
            var inReport = rep != null
                && linesInReports.Any(l => l.DocumentId == d.Id && l.TaxReportId == rep.Id);
            return new Pp36RecognizedDocItem(
                d.Id, d.DocumentNumber,
                contactNames.GetValueOrDefault(d.ContactId, ""),
                d.VatAmount, cy, cm, d.Pp36RdReceiptNumber,
                rep == null ? "none" : rep.Status.ToString(),
                inReport);
        }).OrderBy(x => x.DocumentNumber).ToList();
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
