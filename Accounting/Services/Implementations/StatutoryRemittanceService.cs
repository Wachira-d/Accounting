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

    // กำหนดยื่น: (กระดาษ, e-Filing) — ตารางอยู่ที่ Helpers/TaxFilingDeadline ตัวเดียว
    // (เดิมเรพมี 3 ตาราง ตัวหนึ่งให้ ภ.พ.36 = วันที่ 23 ⇒ เตือนช้ากว่ากำหนดจริง 8 วัน)
    // ตัวกลางเลื่อนพ้นเสาร์/อาทิตย์ให้แล้วตาม ป.พ.พ. §193/8 — เดิมที่นี่ไม่เลื่อนเลย
    // ⇒ งวดที่วันที่ 7/15 ตรงวันหยุด ขึ้น "เลยกำหนด" สีแดงทั้งที่ยังไม่เลย
    internal static (DateTime Paper, DateTime EFiling) DueDates(string type, int year, int month)
        => Accounting.Helpers.TaxFilingDeadline.For(type, year, month);

    /// <summary>รอบ 201 ทีม PL (B-9): เลื่อนพ้นวันหยุดราชการของแพลตฟอร์มด้วย (ตาราง <c>PlatformHolidays</c> · ว่าง = เสาร์/อาทิตย์ตามเดิม)</summary>
    internal static (DateTime Paper, DateTime EFiling) DueDates(string type, int year, int month, IReadOnlySet<DateTime>? holidays)
        => Accounting.Helpers.TaxFilingDeadline.For(type, year, month, holidays);

    /// <summary>วันหยุดราชการที่โหลดต้นคำขอ (service เป็น scoped) — null = ยังไม่โหลด ⇒ เสาร์/อาทิตย์อย่างเดียว</summary>
    private HashSet<DateTime>? _holidays;

    private async Task EnsureHolidaysAsync(DateTime today)
        => _holidays ??= await PlatformHolidayStore.LoadSetAsync(_db, today.Year - 3, today.Year + 1, _logger);

    public async Task<RemittanceDashboardResponse> GetDashboardAsync(Guid companyId, int monthsBack = 12)
    {
        // ⚠️ ต้องเป็นวันตามเวลาไทย (UTC+7) ไม่ใช่ UTC — ช่วง 00:00–07:00 ของไทย
        // UTC ยังเป็นเมื่อวาน ⇒ ธง "เลยกำหนด" และเงินเพิ่ม §49 คลาดไป 1 วัน และ
        // **ไม่ตรงกับปฏิทินยื่นในไฟล์เดียวกัน** ซึ่งใช้ +7 อยู่แล้ว
        // (สองจอของ service เดียวกันบอกคนละวัน)
        var today = DateTime.UtcNow.AddHours(7).Date;
        await EnsureHolidaysAsync(today);   // B-9 วันหยุดราชการ (ว่าง = พฤติกรรมเดิม)
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

        // ── "ยื่นแบบแล้วหรือยัง" — คนละเหตุการณ์กับ "จ่ายเงินแล้วหรือยัง" ──
        // เดิมหน้านี้อ่านแต่ StatutoryRemittance (เงินที่จ่าย) ⇒ งวดที่ผู้ใช้กด
        // "ยื่นแบบ" ที่หน้ารายงานภาษีแล้ว ยังขึ้น "เลยกำหนด N วัน" สีแดงตลอดไป
        // (ปฏิทินยื่นในไฟล์เดียวกันอ่าน TaxReport.Status อยู่แล้ว — สองจอของ
        // service เดียวกันจึงเล่าคนละเรื่อง)
        var filed = new List<(TaxType Type, int Year, int Month, DateTime? At)>();
        try
        {
            filed = (await _db.TaxReports.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.Status != TaxReportStatus.Draft
                    && (r.Year > start.Year || (r.Year == start.Year && r.Month >= start.Month)))
                .Select(r => new { r.TaxType, r.Year, r.Month, r.FiledDate })
                .ToListAsync())
                .Select(r => (r.TaxType, r.Year, r.Month, r.FiledDate)).ToList();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลดสถานะยื่นแบบ (TaxReports) ไม่สำเร็จ"); }
        DateTime? FiledAt(string type, int y, int m)
        {
            var rt = ReportTypeOf(type);
            if (rt == null) return null;
            // ⚠️ ข้อจำกัดที่รู้ตัว: `TaxService.CreateTaxReport` **ปฏิเสธ**
            // WithholdingTax1 และ SocialSecurity (บอกให้ไปใช้เมนูส่งออกไฟล์ยื่น)
            // ⇒ สองแบบนี้ไม่มีแถว TaxReport ให้ค้นเลย ⇒ สถานะ "ยื่นแบบแล้ว ·
            // รอบันทึกการนำส่งเงิน" (สีส้ม) **ไปไม่ถึง** — แถวจะเป็นแดงจนกว่าจะ
            // บันทึกการนำส่งเงิน ซึ่งปิดแถวทั้งใบอยู่แล้ว
            // (คอมเมนต์เดิมเขียนว่า "สปส.1-10 ไม่ได้ยื่นผ่าน TaxReport จึงคืน null
            //  ที่บรรทัดนี้" — **ผิด**: ReportTypeOf("SsoSps110") คืน
            //  TaxType.SocialSecurity ไม่ใช่ null; ตัวที่ทำให้ได้ null คือ "ไม่มีแถว"
            //  ไม่ใช่ "แปลงชนิดไม่ได้")
            // ถ้าจะเปิดสถานะส้มให้สองแบบนี้ ต้องมีที่ให้ผู้ใช้ทำเครื่องหมาย
            // "ยื่นแบบแล้ว" ก่อน — ห้าม infer จากการกดดาวน์โหลดไฟล์ (ดาวน์โหลด
            // ≠ ยื่น) ตามกฎ "สถานะที่แปลว่าระบบภายนอกรับไปแล้ว ต้องตั้งได้เฉพาะ
            // เมื่อระบบภายนอกตอบกลับจริง"
            var hit = filed.Where(f => f.Type == rt.Value && f.Year == y && f.Month == m)
                           .Select(f => (DateTime?)(f.At ?? DateTime.UtcNow)).ToList();
            return hit.Count > 0 ? hit[0] : null;
        }

        // ── SSO + ภงด.1 จาก PayrollRun ──
        try
        {
            var runs = await _db.Set<PayrollRun>().AsNoTracking()
                .Where(r => r.CompanyId == companyId
                    && r.Status == Accounting.Helpers.PayrollRunFilingScope.Paid)
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
                    employee: emp, employer: empr, relatedRunId: g.First().Id,
                    reportFiledAt: FiledAt("SsoSps110", g.Key.Year, g.Key.Month)));
            }
            // ── ช่องโหว่ 50 ทวิ ของ ภ.ง.ด.1 (D6-4) ──
            // ยอดบนแถวนี้มาจาก PayrollRun.TotalWithholdingTax (ภาษีที่หักไปจริง)
            // แต่ใบ 50 ทวิ อาจออกไม่ครบ (พนักงานไม่มีเลขผู้เสียภาษี / งวดที่การ
            // ออกใบล้มทั้งก้อน) ⇒ เทียบสองแหล่งสด ๆ ทุกครั้ง แล้วให้ด่านเดียวกับ
            // ภ.ง.ด.3/53/54 บล็อกการนำส่ง — ห้ามนับเงียบ (คำตัดสินเจ้าของรอบ 170)
            var pnd1Payroll = (await _db.PayrollDetails.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && !d.IsDeleted
                        && d.WithholdingTax > 0
                        && d.PayrollRun.Status == Accounting.Helpers.PayrollRunFilingScope.Paid)
                    .Select(d => new { d.PayrollRun.Year, d.PayrollRun.Month, d.WithholdingTax })
                    .ToListAsync())
                .GroupBy(x => (x.Year, x.Month))
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Tax: g.Sum(x => x.WithholdingTax)));
            var pnd1Filed = Accounting.Helpers.WhtCertFilingScope.Filed;
            var pnd1Certs = (await _db.WithholdingTaxCerts.AsNoTracking()
                    .Where(c => c.CompanyId == companyId && !c.IsDeleted
                        && c.TaxFormType == TaxType.WithholdingTax1
                        && pnd1Filed.Contains(c.Status))
                    .Select(c => new { c.TaxYear, c.TaxMonth, c.TotalTaxAmount })
                    .ToListAsync())
                .GroupBy(x => (Year: x.TaxYear, Month: x.TaxMonth))
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Tax: g.Sum(x => x.TotalTaxAmount)));

            foreach (var g in runs.Where(r => InRange(r.Year, r.Month)).GroupBy(r => (r.Year, r.Month)))
            {
                var outstanding = g.Sum(x => x.Wht) - Remitted("WhtPnd1", g.Key.Year, g.Key.Month);
                var pay = pnd1Payroll.TryGetValue(g.Key, out var pv) ? pv : (Count: 0, Tax: 0m);
                var cer = pnd1Certs.TryGetValue(g.Key, out var cv) ? cv : (Count: 0, Tax: 0m);
                var gap = Accounting.Helpers.Pnd1CertCoverage.Evaluate(
                    pay.Count, pay.Tax, cer.Count, cer.Tax);
                // ยอดค้าง 0 แต่ยังมีใบไม่ครบ = ยังต้องขึ้นแถวเตือน (ผู้ใช้ต้องเห็น
                // ว่ามีอะไรค้างอยู่ ไม่ใช่หายไปจากจอ)
                if (outstanding <= 0.009m && !gap.Any) continue;
                pending.Add(BuildItem("WhtPnd1", g.Key.Year, g.Key.Month,
                    Math.Max(0m, outstanding), today,
                    reportFiledAt: FiledAt("WhtPnd1", g.Key.Year, g.Key.Month),
                    // ส่งจำนวนจริง (0 ได้) — ห้ามแต่งเป็น 1 ให้ดูมีเหตุผล; ด่านฝั่ง
                    // RemitAsync ใช้เกณฑ์ OR กับยอดเงินอยู่แล้ว
                    unissuedCount: gap.Any ? gap.EmployeeCount : null,
                    unissuedAmount: gap.Any ? gap.TaxAmount : null));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "คำนวณ SSO/ภงด.1 ไม่สำเร็จ"); }

        // ── ภงด.3 / 53 จากเอกสารหัก ณ ที่จ่าย (bound ช่วงวันที่ใน SQL) ──
        try
        {
            // ⚠️ ต้องกรอง **ฝั่งซื้อ** เท่านั้น — เดิมไม่กรองชนิดเอกสารเลย ⇒ ใบขาย
            // (Invoice/TaxInvoice/DebitNote) ที่ "ลูกค้าหักเราไว้" ซึ่งเป็น
            // **เครดิตภาษีของเรา** (Dr 11910 → ภ.ง.ด.50) ถูกนับเป็นเงินที่เรา
            // ต้องนำส่ง ⇒ ยอดค้างพองเกินจริง และไม่ตรงกับหน้ารายงานภาษีที่กรอง
            // ถูกมาตลอด. ลิสต์อยู่ที่ Helpers/WhtRemitScope ตัวเดียว
            var payerSide = Accounting.Helpers.WhtRemitScope.PayerSideTypes;
            var whtDocs = await _db.Documents.AsNoTracking()
                // เฉพาะเอกสารที่ post WHT payable เข้า GL แล้ว (อนุมัติขึ้นไป)
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.WithholdingTaxAmount > 0
                    && payerSide.Contains(d.DocumentType)
                    && (d.PaymentDate ?? d.DocumentDate) >= start
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.Id, d.DocumentType, d.RelatedDocumentId,
                    d.WithholdingTaxAmount, d.PaymentDate, d.DocumentDate,
                    d.IsForeignService,
                    CType = d.Contact != null ? d.Contact.ContactType : ContactType.Individual,
                    CTaxId = d.Contact != null ? d.Contact.TaxId : null,
                    CName = d.Contact != null ? d.Contact.Name : null,
                    CCountry = d.Contact != null ? d.Contact.CountryCode : null })
                .ToListAsync();

            // ⚠️ ตัดใบตั้งหนี้ที่มีใบสำคัญจ่ายคลุมแล้ว — ทั้งคู่ถือ
            // WithholdingTaxAmount ⇒ เดิมนับสองครั้ง (และข้ามเดือนถ้าจ่ายคนละเดือน)
            // ท.ป.4/2528: ภาระนำส่งเกิดที่ "การจ่าย" ⇒ ใบสำคัญจ่ายคือแถวจริง
            // (กติกาเดียวกับ settledSourceIds ของ TaxService.GenerateWhtReport)
            var settledByPv = Accounting.Helpers.WhtRemitScope.SettledSourceIds(
                whtDocs, d => d.DocumentType, d => d.RelatedDocumentId, d => d.WithholdingTaxAmount);
            whtDocs = whtDocs
                .Where(d => d.DocumentType == DocumentType.PaymentVoucher || !settledByPv.Contains(d.Id))
                .ToList();
            // ── ตัวตั้งของยอดนำส่ง ภ.ง.ด.3/53/54 = หนังสือรับรอง 50 ทวิ ที่ออกจริง (รอบ 170) ──
            // เดิมนับจาก Documents.WithholdingTaxAmount ขณะที่รายงาน/ไฟล์ยื่น/แดชบอร์ด
            // (TaxService.GenerateWhtReport · TaxFilingExportService) นับจาก certs Issued/Printed
            // ⇒ JE นำส่ง ≠ ไฟล์ที่ยื่นทุกครั้งที่มีเอกสารที่ยังไม่ออก 50 ทวิ (REGRESSION_ROOT_CAUSE §4 #2)
            // นโยบายเจ้าของรอบ 170: 50 ทวิ ออกอัตโนมัติตอนจ่าย ⇒ สองแหล่งเท่ากันโดยโครงสร้าง — ส่วนเอกสาร
            // ที่หลุด (ออกไม่สำเร็จ · ใบเก่าที่ยังเป็นร่าง · ผู้ใช้ยกเลิกใบ) ขึ้นเป็น **คำเตือนบนแถว** และ
            // RemitAsync บล็อกจนกว่าจะออกครบ — ห้ามนับเงียบ ๆ และห้ามหายเงียบ ๆ
            var filedStatuses = Accounting.Helpers.WhtCertFilingScope.Filed;
            var certs = await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted
                    && filedStatuses.Contains(c.Status)
                    && (c.TaxYear > start.Year || (c.TaxYear == start.Year && c.TaxMonth >= start.Month)))
                .Select(c => new { c.TaxFormType, c.TaxYear, c.TaxMonth, c.TotalTaxAmount, c.PayeeContactId })
                .ToListAsync();
            // เอกสารที่หัก WHT แล้วแต่ยังไม่มี 50 ทวิ ที่ออกจริง — ไม่กรองเดือนของ cert เพราะผู้ใช้แก้
            // TaxMonth ให้ต่างจากเดือนจ่ายได้ (ใบยังคุมเอกสารนั้นอยู่ ไม่ใช่ช่องโหว่)
            var coveredDocIds = (await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.DocumentId != null
                    && filedStatuses.Contains(c.Status))
                .Select(c => c.DocumentId!.Value)
                .ToListAsync()).ToHashSet();

            // ม.70: WHT จ่ายต่างประเทศ (ใบ ภ.พ.36) ยื่น **ภ.ง.ด.54** — ห้ามนับปน ภงด.3/53
            // certs แบ่งแบบด้วย TaxFormType ที่ตั้งตอนออกใบ (ResolveWhtFormType) · เอกสารที่หลุด
            // แบ่งด้วย WhtPayeeKind.ResolveForm ตัวเดียวกับทะเบียน 50 ทวิ
            foreach (var typ in new[] { "WhtPnd3", "WhtPnd53", "WhtPnd54" })
            {
                var wantForm = typ switch
                {
                    "WhtPnd53" => TaxType.WithholdingTax53,
                    "WhtPnd54" => TaxType.WithholdingTax54,
                    _ => TaxType.WithholdingTax3,
                };
                var certGroups = certs
                    .Where(c => c.TaxFormType == wantForm && InRange(c.TaxYear, c.TaxMonth))
                    .GroupBy(c => (c.TaxYear, c.TaxMonth))
                    .ToDictionary(g => (Year: g.Key.TaxYear, Month: g.Key.TaxMonth),
                        g => (Amount: g.Sum(x => x.TotalTaxAmount),
                              Payees: g.Select(x => x.PayeeContactId).Distinct().Count()));
                var gapGroups = whtDocs
                    .Where(d => !coveredDocIds.Contains(d.Id)
                        && wantForm == Accounting.Helpers.WhtPayeeKind.ResolveForm(
                            d.IsForeignService, d.CCountry, d.CTaxId, d.CType, d.CName))
                    .Select(d => new { Date = (d.PaymentDate ?? d.DocumentDate), d.WithholdingTaxAmount })
                    .Where(d => InRange(d.Date.Year, d.Date.Month))
                    .GroupBy(d => (d.Date.Year, d.Date.Month))
                    .ToDictionary(g => (Year: g.Key.Year, Month: g.Key.Month),
                        g => (Count: g.Count(), Amount: g.Sum(x => x.WithholdingTaxAmount)));
                foreach (var key in certGroups.Keys.Union(gapGroups.Keys).OrderBy(k => k.Year).ThenBy(k => k.Month))
                {
                    var hasCerts = certGroups.TryGetValue(key, out var cg);
                    var hasGap = gapGroups.TryGetValue(key, out var gap);
                    var outstanding = (hasCerts ? cg.Amount : 0m) - Remitted(typ, key.Year, key.Month);
                    // ไม่มียอดค้างและไม่มีใบหลุด = งวดปิดแล้ว · มีใบหลุดแต่ยอด 0 = ยังต้องขึ้นแถวเตือน
                    if (outstanding <= 0.009m && !hasGap) continue;
                    pending.Add(BuildItem(typ, key.Year, key.Month, Math.Max(0m, outstanding), today,
                        payeeCount: hasCerts ? cg.Payees : 0,
                        reportFiledAt: FiledAt(typ, key.Year, key.Month),
                        unissuedCount: hasGap ? gap.Count : null,
                        unissuedAmount: hasGap ? gap.Amount : null));
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
                    outputVat: v.OutputVat, inputVat: v.InputVat,
                    reportFiledAt: FiledAt("VatPp30", v.Year, v.Month)));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลด ภพ.30 ไม่สำเร็จ"); }

        // ── ภพ.36 — VAT ประเมินเองจากบริการต่างประเทศ (§83/6) ──
        // รอบ 203 ทีม F3 (E-3/E-4/E-5/E-8): แหล่งหนี้ = ใบที่ "เป็นเจ้าของ ภ.พ.36" (ForeignServiceVat.OwnsPp36 · ชุดชนิดเดียว) ที่ **GL มี Cr 21912 จริง**
        // (Helpers/Pp36Ledger — ยอดเป็นบาทตามที่ JE ลง) และ **ยังไม่อยู่ในรายการนำส่งใด** (ตารางผูก Pp36RemittanceDocuments · คำตัดสินข้อ 133) ·
        // เดิมคัดจากธง+สถานะ แล้วหักยอดที่นำส่งแล้วต่องวด ⇒ ใบอนุมัติแล้วไม่มี JE ถูกนับ · ใบอนุมัติหลังนำส่งนำส่งเพิ่มไม่ได้ · ยอดติดลบ continue เงียบ
        // ใบที่ไม่มี JE / นำส่งเกิน ⇒ ขึ้นเป็นรายการ "ต้องตรวจ" (Pp36Issues) ไม่ใช่นับเงียบ · ไม่มี catch — ล้มดัง (กฎ F2 ข้อ 7)
        var pp36Rows = await Accounting.Helpers.Pp36Ledger.LoadDocsAsync(_db, companyId, start, null);
        foreach (var g in pp36Rows
            .Where(r => r.State == Accounting.Helpers.Pp36DocState.AwaitingRemittance && InRange(r.PeriodDate.Year, r.PeriodDate.Month))
            .GroupBy(r => (r.PeriodDate.Year, r.PeriodDate.Month)))
        {
            var outstanding = g.Sum(x => x.CountedVat);
            if (outstanding <= 0.009m) continue;
            pending.Add(BuildItem("VatPp36", g.Key.Year, g.Key.Month, outstanding, today,
                payeeCount: g.Count(), reportFiledAt: FiledAt("VatPp36", g.Key.Year, g.Key.Month)));
        }
        var pp36Issues = BuildPp36Issues(pp36Rows, remits.Where(r => r.RemittanceType == "VatPp36"
            && InRange(r.PeriodYear, r.PeriodMonth)).ToList());

        // ── ภ.พ.36: นำส่งแล้ว แต่ยังไม่ได้ "รับรู้ภาษีซื้อ" (ขั้นที่ 2) ──
        // ผู้ใช้เจอจริง: นำส่งเสร็จแล้วไปหาใบใน ภ.พ.30 ไม่เจอ — เพราะภาษีซื้อยังพักที่ 11640 จนกว่าจะกดรับรู้ (ได้ใบเสร็จกรมสรรพากร §82/4) ·
        // รอบ 203: ต่อ "รายการนำส่ง" (งวดหนึ่งนำส่งได้หลายครั้ง แต่ละครั้งมีใบเสร็จของตัวเอง) · ยอด = ภาษีซื้อที่พัก 11640 จริงของใบ (ไม่ใช่ VatAmount)
        var pp36Remits = remits.Where(r => r.RemittanceType == "VatPp36").ToList();
        var pp36Awaiting = pp36Rows
            .Where(r => r.State == Accounting.Helpers.Pp36DocState.RemittedAwaitingRecognition && r.Link != null)
            .GroupBy(r => r.Link!.StatutoryRemittanceId)
            .Select(g =>
            {
                var rem = pp36Remits.FirstOrDefault(x => x.Id == g.Key);
                var first = g.First().Link!;
                return new Pp36AwaitingRecognitionItem(first.PeriodYear, first.PeriodMonth,
                    g.Sum(x => x.Ledger.UndueInputVat), g.Count(), rem?.PayDate ?? first.CreatedAt,
                    RemittanceId: g.Key, RdReceiptNumber: rem?.FilingNumber);
            })
            .OrderBy(a => a.PeriodYear).ThenBy(a => a.PeriodMonth).ToList();
        var awaitingRemitIds = pp36Awaiting.Where(a => a.RemittanceId != null).Select(a => a.RemittanceId!.Value).ToHashSet();
        var recognizedRemitIds = pp36Rows
            .Where(r => r.State == Accounting.Helpers.Pp36DocState.Recognized && r.Link != null)
            .Select(r => r.Link!.StatutoryRemittanceId).ToHashSet();
        // งวดเก่ากว่าช่วงที่โหลด: กติกา idempotent เดิม (JE Reference ภ.พ.36R-YYYYMM)
        var pp36RecognizedRefs = pp36Remits.Count == 0
            ? new HashSet<string>()
            : (await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && !j.IsDeleted
                    && j.Status == JournalEntryStatus.Posted
                    && j.Reference != null && j.Reference.StartsWith("ภ.พ.36R-"))
                .Select(j => j.Reference!)
                .ToListAsync()).ToHashSet();
        bool Pp36RecognizedOf(StatutoryRemittance r)
            => !awaitingRemitIds.Contains(r.Id)
               && (recognizedRemitIds.Contains(r.Id)
                   || pp36RecognizedRefs.Any(x => x.StartsWith($"ภ.พ.36R-{r.PeriodYear}{r.PeriodMonth:D2}")));

        // ── ประวัติที่นำส่งล่าสุด ──
        var history = remits.OrderByDescending(r => r.PayDate).Take(30)
            .Select(r => new RemittanceHistoryItem(r.Id, r.RemittanceType, Meta(r.RemittanceType).Form,
                r.PeriodYear, r.PeriodMonth, r.Amount, r.LateFee, r.PayDate, r.FilingNumber,
                r.JournalEntryId, r.ReceiptAttachmentId, r.CreatedBy, r.CreatedAt,
                Pp36Recognized: r.RemittanceType == "VatPp36" ? Pp36RecognizedOf(r) : (bool?)null))
            .ToList();

        pending = pending.OrderByDescending(p => p.IsOverdue)
            .ThenBy(p => p.WarnDueDate ?? p.EFilingDueDate).ToList();   // RTX-6: เรียงตามวันที่ใช้เตือน (ภ.พ.36/ภ.ง.ด.54 = วันกระดาษ)

        return new RemittanceDashboardResponse(
            TotalPending: pending.Sum(p => p.Amount),
            TotalOverdue: pending.Where(p => p.IsOverdue).Sum(p => p.Amount),
            OverdueCount: pending.Count(p => p.IsOverdue),
            Pending: pending,
            RecentHistory: history,
            Pp36AwaitingRecognition: pp36Awaiting,
            Pp36Issues: pp36Issues);
    }

    /// <summary>
    /// รายการ ภ.พ.36 ที่ "ต้องตรวจ" — ไม่นับเงียบ (PP36_REVIEW E-3/E-6): (1) ใบอนุมัติแล้วแต่ GL ไม่มี Cr 21912 (ซ่อม JE ก่อน ·
    /// เครื่องมือ "ลงบัญชีให้ใบที่อนุมัติแล้วแต่ไม่มี JE" ของทีม F1) · (2) รายการนำส่งที่ยอดมากกว่า 21912 ของใบที่ผูกอยู่ (นำส่งเกิน)
    /// </summary>
    private static List<Pp36IssueItem> BuildPp36Issues(List<Accounting.Helpers.Pp36DocRow> rows, List<StatutoryRemittance> pp36Remits)
    {
        var issues = rows
            .Where(r => r.State == Accounting.Helpers.Pp36DocState.NoJournal)
            .OrderBy(r => r.PeriodDate)
            .Select(r => new Pp36IssueItem("NoJournal", r.PeriodDate.Year, r.PeriodDate.Month, r.Id, r.DocumentNumber, 0m,
                $"ใบ {r.DocumentNumber} อนุมัติแล้วแต่บัญชีแยกประเภทไม่มีหนี้ ภ.พ.36 (Cr 21912) — ยังไม่นับในยอดนำส่ง/รายงาน · "
                + "ลงบัญชีให้ใบนี้ก่อน (เครื่องมือ \"ลงบัญชีให้ใบที่อนุมัติแล้วแต่ไม่มี JE\") แล้วยอดจะขึ้นเอง"))
            .ToList();
        foreach (var rem in pp36Remits)
        {
            var linkedLedger = rows.Where(r => r.Link?.StatutoryRemittanceId == rem.Id).Sum(r => r.Ledger.Pp36Payable);
            var over = rem.Amount - linkedLedger;
            if (over > 0.01m)
                issues.Add(new Pp36IssueItem("OverRemitted", rem.PeriodYear, rem.PeriodMonth, null, null, over,
                    $"ภ.พ.36 งวด {rem.PeriodMonth:D2}/{rem.PeriodYear + 543} นำส่งไป {rem.Amount:N2} บาท แต่หนี้ 21912 ของใบที่ผูกกับการนำส่งนี้เหลือ "
                    + $"{linkedLedger:N2} บาท — นำส่งเกิน {over:N2} บาท (ใบถูกยกเลิก/ไม่มี JE หลังนำส่ง) · ตรวจกับใบเสร็จกรมสรรพากร แล้วขอคืน/บันทึกปรับปรุงด้วยใบสำคัญทั่วไป"));
            else if (over < -0.01m)
                // ข้อมูลก่อนรอบ 203 (ผูกใบเข้ารายการนำส่งเดิมด้วย migration) — ใบที่ผูกมีหนี้มากกว่าที่จ่ายจริง
                issues.Add(new Pp36IssueItem("UnderRemitted", rem.PeriodYear, rem.PeriodMonth, null, null, -over,
                    $"ภ.พ.36 งวด {rem.PeriodMonth:D2}/{rem.PeriodYear + 543} นำส่งไป {rem.Amount:N2} บาท แต่หนี้ 21912 ของใบที่ผูกกับการนำส่งนี้ {linkedLedger:N2} บาท — "
                    + $"ขาด {-over:N2} บาท · ตรวจกับใบเสร็จกรมสรรพากร ถ้าจ่ายขาดจริงให้ยื่นเพิ่มเติมและบันทึกด้วยใบสำคัญทั่วไป (Dr 21912 / Cr ธนาคาร)"));
        }
        return issues;
    }

    private PendingRemittanceItem BuildItem(string type, int year, int month, decimal amount,
        DateTime today, decimal? employee = null, decimal? employer = null, int? payeeCount = null,
        decimal? outputVat = null, decimal? inputVat = null, Guid? relatedRunId = null,
        DateTime? reportFiledAt = null, int? unissuedCount = null, decimal? unissuedAmount = null)
    {
        var (label, form, code) = Meta(type);
        var (paper, efiling) = DueDates(type, year, month, _holidays);
        // รอบ 201 B-7: เตือนตามวันที่จาก TaxFilingDeadline.WarnBy ตัวเดียว (ภ.พ.36/ภ.ง.ด.54 = วันกระดาษ จนกว่าจะยืนยันมาตรการ e-Filing) · วันหยุดราชการชุดเดียวกับ DueDates (B-9)
        var warnBy = Accounting.Helpers.TaxFilingDeadline.WarnBy(type, year, month, _holidays);
        // "ยื่นแบบแล้ว" ตัดธงเลยกำหนดออก — ยอดยังค้างได้ (ยังไม่จ่ายเงิน) แต่ผู้ใช้
        // ไม่ได้ทำผิดกำหนดยื่น จึงห้ามขึ้นสีแดง/นับใน OverdueCount
        var overdue = today > warnBy.Date && reportFiledAt == null;
        // เงินเพิ่ม preview: ปกส. (§49 2%/เดือน) · ภ.พ.36 (§89/1 1.5%/เดือนหรือเศษ — คำตัดสินข้อ 135 · ค่าแนะนำ แก้ได้ตอนนำส่ง)
        var lateFee = type switch
        {
            "SsoSps110" => PayrollService.ComputeSsoLateFee(year, month, today, amount),
            "VatPp36" => Accounting.Helpers.Pp36Lifecycle.SuggestedSurcharge(amount, warnBy, today),
            _ => 0m,
        };
        return new PendingRemittanceItem(type, label, form, year, month, amount,
            paper, efiling, overdue, lateFee, code,
            employee, employer, payeeCount, outputVat, inputVat, relatedRunId,
            reportFiledAt, unissuedCount, unissuedAmount,
            WarnDueDate: warnBy, WarnNote: Accounting.Helpers.TaxFilingDeadline.EFilingCaveat(type, (paper, efiling), today));
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
        "WhtPnd54"  => (false, "ยื่นเฉพาะเดือนที่จ่ายเงินได้ให้ผู้รับในต่างประเทศ (ป.รัษฎากร ม.70)"),
        "VatPp36"   => (false, "ยื่นเฉพาะเดือนที่จ่ายค่าบริการต่างประเทศ (ป.รัษฎากร §83/6)"),
        _           => (false, "")
    };

    // snapshot ที่อ่านมาครั้งเดียวแล้วส่งต่อให้ BuildCell — ใช้ record แทน tuple
    // ยาว ๆ เพื่อให้ชื่อฟิลด์ถูกบังคับตอน compile (tuple ชื่อไม่ตรงจะเงียบ)
    private sealed record ReportSnap(TaxType Type, int Year, int Month, bool Filed,
        DateTime? FiledAt, decimal NetVat);
    private sealed record RunSnap(int Year, int Month, decimal Emp, decimal Empr, decimal Wht,
        DateTime? SsoSettledAt, string? SsoFiling);
    // เก็บ **แบบที่ต้องยื่น** ไม่ใช่ธง juristic — เดิมตัดสินจาก `ContactType`
    // ดิบ ๆ ตรงนี้ที่เดียว ⇒ ปฏิทินแบ่ง 3/53 คนละแบบกับทะเบียน 50 ทวิ/รายงาน
    // และ WHT จ่ายต่างประเทศ (ม.70) ตกไปอยู่ 53 แทนที่จะเป็น 54
    private sealed record WhtSnap(int Year, int Month, decimal Wht, TaxType Form);
    /// <summary>เอกสารหัก WHT ที่ยังไม่มี 50 ทวิ ที่ออกจริง — ช่องโหว่ที่ปฏิทินต้องเตือน (ไม่รวมในยอด)</summary>
    private sealed record WhtGapSnap(int Year, int Month, TaxType Form, decimal Wht);
    private sealed record FsSnap(int Year, int Month, decimal Vat);

    /// <summary>map ชนิดนำส่ง → TaxType ของรายงานภาษีในระบบ (ใช้เช็ค "ยื่นแบบแล้ว")</summary>
    private static TaxType? ReportTypeOf(string type) => type switch
    {
        "VatPp30"   => TaxType.VAT,
        "WhtPnd1"   => TaxType.WithholdingTax1,
        "WhtPnd3"   => TaxType.WithholdingTax3,
        "WhtPnd53"  => TaxType.WithholdingTax53,
        // ม.70 — เดิมตกหล่นจาก map นี้ ⇒ ปฏิทินยื่นไม่รู้จัก ภ.ง.ด.54 เลย
        "WhtPnd54"  => TaxType.WithholdingTax54,
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
        await EnsureHolidaysAsync(today);   // B-9 วันหยุดราชการ (ว่าง = พฤติกรรมเดิม)
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
                // "ยื่นแล้ว" บนแดชบอร์ดนำส่ง = ผู้ใช้ประกาศว่ายื่นแล้ว (Submitted)
                // หรือยืนยันด้วยเลขรับ (Filed) — ตัดสินที่ Helpers/TaxFilingLockPolicy
                // ตัวเดียว ไม่เทียบ == Filed เอง (D2-B1a)
                .Select(t => new ReportSnap(t.TaxType, t.Year, t.Month,
                    Accounting.Helpers.TaxFilingLockPolicy.DeclaredOrFiled(t.Status),
                    t.FiledDate, t.NetVat))
                .ToList();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลด TaxReports ไม่สำเร็จ"); }

        // ── ฐานยอดที่ต้องนำส่ง ──
        // งวดที่ "อนุมัติแล้วแต่ยังไม่กดจ่าย" — ไฟล์ยื่น ภ.ง.ด.1/สปส.1-10 ดาวน์โหลด
        // ได้แล้ว แต่ยังไม่มี JE ตั้งหนี้ 21815/21914 จึงยังนำส่งไม่ได้ ⇒ ต้องไม่บอก
        // ว่า "ยังไม่ได้รันเงินเดือนงวดนี้" ซึ่งเป็นความเท็จและพาผู้ใช้ไปผิดที่
        var approvedNotPaid = new HashSet<(int, int)>();
        try
        {
            approvedNotPaid = (await _db.PayrollRuns.AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted
                    && r.Status == Accounting.Helpers.PayrollRunFilingScope.Approved)
                .Select(r => new { r.Year, r.Month }).ToListAsync())
                .Select(r => (r.Year, r.Month)).ToHashSet();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลดรอบเงินเดือนที่อนุมัติแล้วไม่สำเร็จ"); }

        var runs = new List<RunSnap>();
        try
        {
            runs = (await _db.PayrollRuns.AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted
                    && r.Status == Accounting.Helpers.PayrollRunFilingScope.Paid)
                .Select(r => new { r.Year, r.Month, r.TotalSocialSecurityEmployee,
                    r.TotalSocialSecurityEmployer, r.TotalWithholdingTax, r.SsoSettledAt, r.SsoFilingNumber })
                .ToListAsync())
                .Select(r => new RunSnap(r.Year, r.Month, r.TotalSocialSecurityEmployee,
                    r.TotalSocialSecurityEmployer, r.TotalWithholdingTax, r.SsoSettledAt, r.SsoFilingNumber))
                .ToList();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลด PayrollRuns ไม่สำเร็จ"); }

        var whtDocs = new List<WhtSnap>();
        var whtGaps = new List<WhtGapSnap>();
        var fsDocs = new List<FsSnap>();
        try
        {
            // ยอด WHT ของปฏิทิน = certs ที่ออกจริง (ตัวตั้งเดียวกับ GetDashboardAsync / รายงาน / ไฟล์ยื่น — รอบ 170)
            var calFiled = Accounting.Helpers.WhtCertFilingScope.Filed;
            whtDocs = (await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted && calFiled.Contains(c.Status)
                    && (c.TaxYear > startMonth.Year || (c.TaxYear == startMonth.Year && c.TaxMonth >= startMonth.Month)))
                .Select(c => new { c.TaxYear, c.TaxMonth, c.TotalTaxAmount, c.TaxFormType })
                .ToListAsync())
                .Select(c => new WhtSnap(c.TaxYear, c.TaxMonth, c.TotalTaxAmount, c.TaxFormType))
                .ToList();
            // ── ช่องโหว่ 50 ทวิ ของ ภ.ง.ด.1 (D6-4) — เทียบแถวเงินเดือนกับใบที่ออกจริง
            // ตัวตัดสินเดียวกับหน้านำส่ง (Helpers/Pnd1CertCoverage) ห้ามเขียนสูตรซ้ำ
            var pnd1PayrollCal = (await _db.PayrollDetails.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && !d.IsDeleted
                        && d.WithholdingTax > 0
                        && d.PayrollRun.Status == Accounting.Helpers.PayrollRunFilingScope.Paid
                        && (d.PayrollRun.Year > startMonth.Year
                            || (d.PayrollRun.Year == startMonth.Year && d.PayrollRun.Month >= startMonth.Month)))
                    .Select(d => new { d.PayrollRun.Year, d.PayrollRun.Month, d.WithholdingTax })
                    .ToListAsync())
                .GroupBy(x => (x.Year, x.Month))
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Tax: g.Sum(x => x.WithholdingTax)));
            var pnd1CertsCal = (await _db.WithholdingTaxCerts.AsNoTracking()
                    .Where(c => c.CompanyId == companyId && !c.IsDeleted
                        && c.TaxFormType == TaxType.WithholdingTax1
                        && calFiled.Contains(c.Status)
                        && (c.TaxYear > startMonth.Year
                            || (c.TaxYear == startMonth.Year && c.TaxMonth >= startMonth.Month)))
                    .Select(c => new { c.TaxYear, c.TaxMonth, c.TotalTaxAmount })
                    .ToListAsync())
                .GroupBy(x => (Year: x.TaxYear, Month: x.TaxMonth))
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Tax: g.Sum(x => x.TotalTaxAmount)));
            foreach (var key in pnd1PayrollCal.Keys)
            {
                var cer = pnd1CertsCal.TryGetValue(key, out var cv) ? cv : (Count: 0, Tax: 0m);
                var g1 = Accounting.Helpers.Pnd1CertCoverage.Evaluate(
                    pnd1PayrollCal[key].Count, pnd1PayrollCal[key].Tax, cer.Count, cer.Tax);
                if (g1.Any)
                    whtGaps.Add(new WhtGapSnap(key.Year, key.Month,
                        TaxType.WithholdingTax1, g1.TaxAmount));
            }

            var coveredCalDocIds = (await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.DocumentId != null
                    && calFiled.Contains(c.Status))
                .Select(c => c.DocumentId!.Value)
                .ToListAsync()).ToHashSet();

            // กติกาชุดเดียวกับ GetDashboardAsync (สองจอของ service เดียวกัน เคย
            // คำนวณคนละแบบ): ฝั่งซื้อเท่านั้น · กันใบตั้งหนี้ + ใบสำคัญจ่ายนับซ้ำ ·
            // แบ่ง 3/53/54 ด้วย Helpers/WhtPayeeKind ตัวเดียวกับทะเบียน 50 ทวิ
            var payerSide = Accounting.Helpers.WhtRemitScope.PayerSideTypes;
            var docs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.WithholdingTaxAmount > 0
                    && payerSide.Contains(d.DocumentType)
                    && (d.PaymentDate ?? d.DocumentDate) >= startMonth
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.Id, d.DocumentType, d.RelatedDocumentId,
                    d.WithholdingTaxAmount, d.VatAmount, d.IsForeignService,
                    d.PaymentDate, d.DocumentDate,
                    CType = d.Contact != null ? d.Contact.ContactType : ContactType.Individual,
                    CTaxId = d.Contact != null ? d.Contact.TaxId : null,
                    CName = d.Contact != null ? d.Contact.Name : null,
                    CCountry = d.Contact != null ? d.Contact.CountryCode : null })
                .ToListAsync();
            // ⚠️ กรอง `wht != 0` ใน helper (PV ที่ไม่มี WHT ไม่ได้คลุมภาระของใบตั้งหนี้ — เดิม query นี้ดึง PV ภ.พ.36 เข้ามาด้วย ⇒ ยอด ภ.ง.ด.53 หาย · รอบ 203 ภ.พ.36 ย้ายไป Pp36Ledger แล้ว)
            var settledByPv = Accounting.Helpers.WhtRemitScope.SettledSourceIds(
                docs, d => d.DocumentType, d => d.RelatedDocumentId, d => d.WithholdingTaxAmount);
            foreach (var d in docs)
            {
                if (d.DocumentType != DocumentType.PaymentVoucher && settledByPv.Contains(d.Id))
                    continue;
                var dt = d.PaymentDate ?? d.DocumentDate;
                // ยอดจริงมาจาก certs (ข้างบน) — เอกสารที่ยังไม่มีใบออกจริงเก็บเป็น "ช่องโหว่" ให้ช่องปฏิทินเตือน
                if (d.WithholdingTaxAmount > 0 && !coveredCalDocIds.Contains(d.Id))
                    whtGaps.Add(new WhtGapSnap(dt.Year, dt.Month,
                        Accounting.Helpers.WhtPayeeKind.ResolveForm(
                            d.IsForeignService, d.CCountry, d.CTaxId, d.CType, d.CName),
                        d.WithholdingTaxAmount));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "โหลดเอกสารหัก ณ ที่จ่าย ไม่สำเร็จ"); }

        // ── ภ.พ.36 — ตัวโหลด/ตัวตัดสินเดียวกับหน้านำส่ง (รอบ 203 ทีม F3 · E-8): ชุดชนิดเดียว · เจ้าของหนี้ · GL มี Cr 21912 จริง · บาท ·
        // ใบไม่มี JE ไม่นับ (หน้านำส่งแสดงเป็นรายการ "ต้องซ่อม") · ไม่มี catch — ล้มดัง
        foreach (var r in await Accounting.Helpers.Pp36Ledger.LoadDocsAsync(_db, companyId, startMonth, null))
            if (r.CountedVat > 0m)
                fsDocs.Add(new FsSnap(r.PeriodDate.Year, r.PeriodDate.Month, r.CountedVat));

        // ── ประกอบเป็นตาราง ──
        var rows = new List<FilingCalendarRow>();
        var order = new[] { "VatPp30", "WhtPnd1", "SsoSps110", "WhtPnd3", "WhtPnd53",
            "WhtPnd54", "VatPp36" };
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
                        runs, whtDocs, whtGaps, fsDocs, activeEmployees, approvedNotPaid)
                    : new FilingCalendarCell(p.Year, p.Month, "NotRequired", 0, 0,
                        DueDates(type, p.Year, p.Month, _holidays).Paper, DueDates(type, p.Year, p.Month, _holidays).EFiling,
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
            .OrderBy(x => x.Cell.WarnDueDate ?? x.Cell.EFilingDueDate).ThenBy(x => x.Row.FormCode)
            .FirstOrDefault();
        string? nextLabel = next == null ? null
            : $"{next.Row.FormCode} งวด {next.Cell.Month:D2}/{next.Cell.Year} — ครบกำหนด {(next.Cell.WarnDueDate ?? next.Cell.EFilingDueDate):dd/MM/yyyy}"
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
            NextDueDate: next == null ? null : (next.Cell.WarnDueDate ?? next.Cell.EFilingDueDate),
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
        List<WhtGapSnap> whtGaps,
        List<FsSnap> fsDocs,
        int activeEmployees,
        HashSet<(int Year, int Month)> approvedNotPaid)
    {
        int y = period.Year, m = period.Month;
        var (paper, efiling) = DueDates(type, y, m, _holidays);
        // รอบ 201 B-7: นับวัน/เลยกำหนดจาก TaxFilingDeadline.WarnBy ตัวเดียว (ภ.พ.36/ภ.ง.ด.54 = วันกระดาษ จนกว่าจะยืนยันมาตรการ e-Filing) · วันหยุดราชการชุดเดียวกับ DueDates (B-9)
        var warnBy = Accounting.Helpers.TaxFilingDeadline.WarnBy(type, y, m, _holidays);
        var efilingCaveat = Accounting.Helpers.TaxFilingDeadline.EFilingCaveat(type, (paper, efiling), today);
        var daysToDue = (int)(warnBy.Date - today).TotalDays;
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
        string? whtGapHint = null;
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
                unknownHint = Accounting.Helpers.PayrollRunFilingScope
                    .PendingReason(approvedNotPaid.Contains((y, m)));
                unknownUrl = "/pages/payroll.html";
                break;
            case "WhtPnd1":
                amount = monthRuns.Sum(r => r.Wht);
                known = monthRuns.Count > 0 || activeEmployees == 0;
                unknownHint = Accounting.Helpers.PayrollRunFilingScope
                    .PendingReason(approvedNotPaid.Contains((y, m)));
                unknownUrl = "/pages/payroll.html";
                {
                    // ช่องโหว่ 50 ทวิ ของเงินเดือน — ต้องเตือนเหมือน ภ.ง.ด.3/53/54
                    // (เดิมมีเฉพาะสามแบบนั้น ⇒ ภ.ง.ด.1 นับเงียบ · D6-4)
                    var p1 = whtGaps.Where(d => d.Year == y && d.Month == m
                        && d.Form == TaxType.WithholdingTax1).ToList();
                    if (p1.Count > 0)
                        whtGapHint = $"⚠️ ภาษีที่หักจากเงินเดือน {p1.Sum(x => x.Wht):N2} บาท "
                            + "ยังไม่มีหนังสือรับรอง 50 ทวิ ที่ออกแล้วรองรับ — "
                            + "ตรวจว่าพนักงานกรอกเลขประจำตัวผู้เสียภาษีครบ แล้วกด “สร้างเอกสารใหม่” ที่รอบเงินเดือน";
                }
                break;
            case "WhtPnd3":
            case "WhtPnd53":
            case "WhtPnd54":
                {
                    var want = ReportTypeOf(type);
                    amount = whtDocs.Where(d => d.Year == y && d.Month == m && d.Form == want)
                        .Sum(d => d.Wht);
                    var gaps = whtGaps.Where(d => d.Year == y && d.Month == m && d.Form == want).ToList();
                    if (gaps.Count > 0)
                        whtGapHint = $"⚠️ เอกสารหัก ณ ที่จ่าย {gaps.Count} ใบ ({gaps.Sum(g => g.Wht):N2} บาท) "
                            + "ยังไม่มี 50 ทวิ ที่ออกแล้ว — ยอดนี้ยังไม่รวม ต้องออกใบก่อนนำส่ง";
                }
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
                hint = $"ต้องนำส่ง {amount:N2} บาท ภายใน {efiling:dd/MM/yyyy} (e-Filing) / {paper:dd/MM/yyyy} (กระดาษ)"
                    + (efilingCaveat != null ? " · " + efilingCaveat : "");
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

        // ช่องโหว่ 50 ทวิ ต้องดังบนช่องปฏิทิน: ช่องที่ "ไม่มีรายการ"/"นำส่งแล้ว" แต่มีเอกสารหัก WHT ที่ยังไม่มีใบ
        // = ระบบยังบอกยอดที่แท้จริงไม่ได้ ⇒ Unknown พร้อมลิงก์ไปออกใบ (ไม่ใช่ปล่อยเขียวทั้งที่นำส่งขาด)
        if (whtGapHint != null)
        {
            if (status is "NotRequired" or "Filed")
            { status = "Unknown"; url = type == "WhtPnd1" ? "/pages/payroll.html" : "/pages/wht.html"; hint = whtGapHint; }
            else hint += " · " + whtGapHint;
        }

        var incomplete = status is "Pending" or "Partial" or "Unknown";
        var overdueFlag = incomplete && today > warnBy.Date;
        var lateFee = (overdueFlag && type == "SsoSps110" && amount > 0)
            ? PayrollService.ComputeSsoLateFee(y, m, today, amount)
            : 0m;
        if (overdueFlag)
            hint += $" · เลยกำหนดมาแล้ว {Math.Abs(daysToDue)} วัน";

        return new FilingCalendarCell(y, m, status, amount, lateFee, paper, efiling,
            overdueFlag, daysToDue, formFiled, filedAt, remitted, remittedAt,
            filingNumber, hasReceipt, isNil, hint, url, WarnDueDate: warnBy);
    }

    public async Task<PendingRemittanceItem?> PreviewAsync(Guid companyId, string remittanceType,
        int periodYear, int periodMonth, DateTime payDate)
    {
        var dash = await GetDashboardAsync(companyId, 18);
        var item = dash.Pending.FirstOrDefault(p => p.RemittanceType == remittanceType
            && p.PeriodYear == periodYear && p.PeriodMonth == periodMonth);
        if (item == null) return null;
        // คำนวณเงินเพิ่มใหม่ตาม payDate ที่เลือก (ปกส. §49 · ภ.พ.36 §89/1)
        if (remittanceType == "SsoSps110")
        {
            var lf = PayrollService.ComputeSsoLateFee(periodYear, periodMonth, payDate, item.Amount);
            item = item with { LateFeePreview = lf };
        }
        else if (remittanceType == "VatPp36")
            item = item with { LateFeePreview = Pp36SurchargeFor(periodYear, periodMonth, item.Amount, payDate) };
        return item;
    }

    public async Task<RemitResult> RemitAsync(Guid companyId, RemitRequest req, string performedBy)
    {
        var type = req.RemittanceType;
        var (label, form, payableCode) = Meta(type);
        if (string.IsNullOrEmpty(payableCode))
            throw new InvalidOperationException($"ประเภทนำส่งไม่ถูกต้อง: {type}");

        // กันนำส่งซ้ำงวดเดิม — ภ.พ.36 ยกเว้น (คำตัดสินข้อ 133): ยื่นได้หลายครั้งต่อเดือน (ต่อการจ่ายเงิน) ⇒ ด่านเปลี่ยนเป็น
        // "ห้ามนับใบเดิมซ้ำ" (ใบที่อยู่ในรายการนำส่งแล้วไม่ถูกนับในยอดค้าง · ตรวจซ้ำใต้ล็อกแถวเอกสารข้างล่าง)
        var isPp36 = type == "VatPp36";
        if (!isPp36)
        {
            var dup = await _db.Set<StatutoryRemittance>().AnyAsync(r => r.CompanyId == companyId
                && !r.IsDeleted && r.RemittanceType == type
                && r.PeriodYear == req.PeriodYear && r.PeriodMonth == req.PeriodMonth);
            if (dup)
                throw new InvalidOperationException(
                    $"{form} งวด {req.PeriodMonth:D2}/{req.PeriodYear} นำส่งไปแล้ว — ดูในประวัติ");
        }

        // หายอดค้าง + breakdown จาก dashboard (source of truth เดียวกับที่แสดง)
        var dash = await GetDashboardAsync(companyId, 24);
        var item = dash.Pending.FirstOrDefault(p => p.RemittanceType == type
            && p.PeriodYear == req.PeriodYear && p.PeriodMonth == req.PeriodMonth);
        if (item == null)
        {
            // ภ.พ.36: งวดที่มีแต่ใบไม่มี JE ต้องบอกเหตุผลจริง (ไม่ใช่ "ไม่มียอดค้าง" เฉย ๆ)
            var noJe = (dash.Pp36Issues ?? new List<Pp36IssueItem>()).Where(x => isPp36 && x.Kind == "NoJournal"
                && x.PeriodYear == req.PeriodYear && x.PeriodMonth == req.PeriodMonth).ToList();
            throw new InvalidOperationException(
                $"ไม่มียอดค้างนำส่งของ {form} งวด {req.PeriodMonth:D2}/{req.PeriodYear}"
                + (noJe.Count > 0
                    ? $" — มีใบ {string.Join(", ", noJe.Select(x => x.DocumentNumber))} ที่อนุมัติแล้วแต่ไม่มี JE (Cr 21912) ต้องลงบัญชีให้ใบก่อน"
                    : ""));
        }

        // ภ.พ.36 — ใบที่จะผูกกับการนำส่งครั้งนี้ (ตัวโหลด/ตัวตัดสินเดียวกับยอดค้าง) · ยอด = Cr 21912 ใน GL (บาท)
        List<Accounting.Helpers.Pp36DocRow>? pp36Docs = null;
        if (isPp36)
        {
            var from = new DateTime(req.PeriodYear, req.PeriodMonth, 1);
            pp36Docs = (await Accounting.Helpers.Pp36Ledger.LoadDocsAsync(_db, companyId, from, from.AddMonths(1)))
                .Where(r => r.State == Accounting.Helpers.Pp36DocState.AwaitingRemittance).ToList();
            if (pp36Docs.Count == 0)
                throw new InvalidOperationException($"ไม่มีใบ ภ.พ.36 ค้างนำส่งในงวด {req.PeriodMonth:D2}/{req.PeriodYear}");
            item = item with { Amount = pp36Docs.Sum(r => r.CountedVat) };
        }

        // ยอดนำส่ง WHT ต้องมาจาก 50 ทวิ ที่ออกแล้ว **ครบ** — ถ้ายังมีเอกสารหัก WHT ที่ไม่มีใบ ยอดที่จะจ่าย
        // กรมสรรพากรจะน้อยกว่าที่หักจริง และไฟล์ ภ.ง.ด. ก็ประกาศไม่ครบ ⇒ บล็อกพร้อมทางไปต่อ (ไม่เงียบ ไม่เดา)
        // ⚠️ เกณฑ์เป็น **OR** ระหว่างจำนวนใบกับยอดเงิน — ภ.ง.ด.1 มีทรงที่
        // "จำนวนใบครบแต่ยอดขาด" ได้ (HR แก้ยอดบนใบรายคน) ถ้าดูแค่จำนวนจะหลุด
        if (item.UnissuedWhtCount is > 0 || item.UnissuedWhtAmount is > 0.009m)
            throw new Accounting.Helpers.BusinessRuleException(
                type == "WhtPnd1"
                    // เงินเดือน: "ใบ" ผูกกับพนักงาน ไม่ใช่เอกสารซื้อ ⇒ ทางไปต่อคนละทาง
                    ? Accounting.Helpers.Pnd1CertCoverage.GapMessage(
                        req.PeriodMonth, req.PeriodYear,
                        new Accounting.Helpers.Pnd1CoverageGap(
                            item.UnissuedWhtCount ?? 0, item.UnissuedWhtAmount ?? 0m))
                    : $"{form} งวด {req.PeriodMonth:D2}/{req.PeriodYear} มีเอกสารหัก ณ ที่จ่าย {item.UnissuedWhtCount} ใบ "
                      + $"(รวม {item.UnissuedWhtAmount:N2} บาท) ที่ยังไม่มีหนังสือรับรอง 50 ทวิ ที่ออกแล้ว — "
                      + "ออกใบให้ครบที่หน้า “หนังสือรับรองหัก ณ ที่จ่าย” (แท็บรอออกใบ) แล้วกลับมานำส่ง "
                      + "หรือกด “ข้าม” ใบที่ไม่ใช่การจ่ายจริง",
                Accounting.Helpers.WhtUnissuedCertGate.RuleCode);

        var amount = item.Amount;
        var lateFee = !req.IncludeLateFee ? 0m : type switch
        {
            "SsoSps110" => PayrollService.ComputeSsoLateFee(req.PeriodYear, req.PeriodMonth, req.PayDate, amount),
            // §89/1 (คำตัดสินข้อ 135) — ค่าที่ระบบเสนอ แก้ได้ (ผู้ใช้เห็นยอดก่อนกด) · ไม่บังคับ
            "VatPp36" => req.LateSurcharge ?? Pp36SurchargeFor(req.PeriodYear, req.PeriodMonth, amount, req.PayDate),
            _ => 0m,
        };
        if (lateFee < 0m)
            throw new Accounting.Helpers.BusinessRuleException("เงินเพิ่มต้องไม่ติดลบ", "REMIT-LATEFEE-NEGATIVE");
        lateFee = Math.Round(lateFee, 2, MidpointRounding.AwayFromZero);

        // ผัง Cr (แหล่งเงิน)
        var bankGlId = await ResolveBankGlAsync(companyId, req.BankAccountId, req.BankGlAccountId);
        var payable = await ResolveAccountAsync(companyId, payableCode)
            ?? throw new InvalidOperationException($"ไม่พบผังบัญชี {payableCode} ({label}) — กรุณาสร้างก่อน");

        // ── สร้าง JE ──
        var lines = new List<JournalLineRequest>();
        if (type == "VatPp30")
        {
            // Dr 21911 ภาษีขาย / Cr 11610 ภาษีซื้อ (+ เครดิตยกมา §82/3) / Cr ธนาคาร
            //
            // ⚠️ เดิม Cr 11610 ลงเฉพาะ "ภาษีซื้อของงวด" แต่ Cr ธนาคารลง NetVat ซึ่ง
            // หัก **เครดิตยกมา** ไว้แล้ว ⇒ ทุกงวดที่มีเครดิตยกมา Dr − Cr = เครดิตยกมา
            // ⇒ CreateJournalEntryAsync โยน "ยอดเดบิตไม่เท่ากับยอดเครดิต" ⇒ นำส่งงวด
            // นั้นไม่ได้เลย และข้อความนั้นผู้ใช้ไม่มีทางแก้เองได้
            // ตรรกะอยู่ที่ Helpers/VatRemittanceJournal (pure + มีเทสต์ล็อก Dr = Cr)
            var plan = Accounting.Helpers.VatRemittanceJournal.Build(
                item.OutputVat ?? amount, item.InputVat ?? 0m, amount);

            lines.Add(new JournalLineRequest(payable.Id, plan.ClearOutputVat, 0,
                "ล้างภาษีขาย ภพ.30 (Dr 21911)"));

            if (plan.ClearInputVat > 0)
            {
                // ⚠️ เดิมสาขา "ไม่พบผัง 11610" ตั้ง amount = output **เงียบ ๆ**
                // ⇒ บริษัทจ่ายภาษีขายเต็มจำนวนแทนยอดสุทธิ (เคสตัวอย่าง: จ่ายเกิน
                // 4,200 บาทโดยไม่มีอะไรเตือน). ต้องปฏิเสธพร้อมบอกทางไปต่อ —
                // "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้ อันตรายกว่าการไม่ตอบ"
                var inputAcc = await ResolveAccountAsync(companyId, "11610")
                    ?? throw new Accounting.Helpers.BusinessRuleException(
                        "ไม่พบผังบัญชี 11610 (ภาษีซื้อ) — งวดนี้มีภาษีซื้อ/เครดิตยกมา "
                        + $"{plan.ClearInputVat:N2} บาทที่ต้องล้างออกจากบัญชีนั้น "
                        + "กรุณาสร้างผังบัญชี 11610 ที่หน้า “ผังบัญชี” ก่อนบันทึกการนำส่ง",
                        "VAT-REMIT-NO-11610");

                var desc = plan.CarryForwardUsed > 0.005m
                    ? $"ล้างภาษีซื้อ ภพ.30 (Cr 11610) — งวดนี้ {plan.ClearInputVat - plan.CarryForwardUsed:N2}"
                      + $" + เครดิตยกมา §82/3 {plan.CarryForwardUsed:N2}"
                    : "ล้างภาษีซื้อ ภพ.30 (Cr 11610)";
                lines.Add(new JournalLineRequest(inputAcc.Id, 0, plan.ClearInputVat, desc));
            }

            lines.Add(new JournalLineRequest(bankGlId, 0, plan.PayFromBank,
                $"จ่ายภาษีมูลค่าเพิ่ม {req.PeriodMonth:D2}/{req.PeriodYear}"));
        }
        else
        {
            lines.Add(new JournalLineRequest(payable.Id, amount, 0, $"ล้าง{label}ค้างจ่าย (Dr {payableCode})"));
            if (lateFee > 0)
            {
                var feeAcc = await ResolveLateFeeAccountAsync(companyId);
                if (feeAcc != null)
                    lines.Add(new JournalLineRequest(feeAcc.Id, lateFee, 0, isPp36
                        // ผังค่าปรับ/เงินเพิ่ม (54820) = รายจ่ายต้องห้าม §65 ตรี(6) — บวกกลับ ภ.ง.ด.50
                        ? "เงินเพิ่มนำส่ง ภ.พ.36 ช้า §89/1 (1.5%/เดือนหรือเศษ) — รายจ่ายต้องห้าม §65 ตรี(6)"
                        : "เงินเพิ่มนำส่งช้า (§49 2%/เดือน)"));
                else if (isPp36)
                    // ห้ามทิ้งยอดเงียบ (เดิมเส้น ปกส. ตั้ง 0 เมื่อไม่พบผัง) — ผู้ใช้กรอก/เห็นยอดแล้ว ต้องบอกทางไปต่อ
                    throw new Accounting.Helpers.BusinessRuleException(
                        $"ไม่พบผังค่าปรับ/เงินเพิ่ม (54xxx ชื่อมีคำว่า \"เงินเพิ่ม\" หรือ \"ค่าปรับ\" เช่น 54820) สำหรับเงินเพิ่ม §89/1 {lateFee:N2} บาท — "
                        + "สร้างผังที่หน้า “ผังบัญชี” ก่อน หรือนำส่งโดยไม่ลงเงินเพิ่ม (ใส่ 0)", "PP36-LATEFEE-NO-ACCOUNT");
                else lateFee = 0m;
            }
            lines.Add(new JournalLineRequest(bankGlId, 0, amount + lateFee, $"นำส่ง{form} {req.PeriodMonth:D2}/{req.PeriodYear}"));
        }

        // ══ ด่านคู่ (ค่าจ้าง, เงินสมทบ) ก่อนเงินออก — ม.33/ม.46 ══
        //
        // ⚠️ ไฟล์ สปส.1-10 **กรองแถวที่ฝั่งลูกจ้าง = 0 ทิ้งเงียบ ๆ** (ทั้ง txt และ xlsx)
        // ขณะที่หน้านี้นับ `Emp + Empr` ของทั้งรอบ ⇒ กดนำส่งตามยอดบนจอ
        // แล้วอัปโหลดไฟล์ที่ประกาศน้อยกว่า = **นำส่งไม่ตรงกับที่ประกาศ** ⇒ สปส.
        // ตีกลับ/นำส่งขาด + เงินเพิ่ม §49 2%/เดือน
        //
        // doc-comment ของ `SsoWageBase.Normalize` เขียนไว้เองว่า "ด่านตอนนำส่งจะ
        // บล็อกให้เองอยู่แล้ว — เงินไม่ออกไปผิด" แต่ `grep` ทั้งไฟล์นี้ไม่มีคำว่า
        // `Conflict`/`IsConsistent`/`SsoWageBase` เลยสักคำ ⇒ **ด่านนั้นไม่เคยมีอยู่จริง**
        //
        // ม.46 ให้ลูกจ้างและนายจ้างสมทบ **ในอัตราเดียวกันจากฐานเดียวกัน** ⇒ ฝั่งหนึ่ง
        // เป็น 0 อีกฝั่งไม่เป็น = ข้อมูลผิดเสมอ ไม่ใช่สถานะที่กฎหมายรองรับ
        if (type == "SsoSps110")
        {
            // ⚠️ ธง "อยู่ในระบบประกันสังคม" อยู่บน **Employee** ไม่ใช่ PayrollDetail
            // (เคยเขียน d.IsSubjectToSocialSecurity ⇒ CS1061 ล้มทั้ง solution)
            // ดึงเฉพาะแถวที่ "มีเงิน" มาก่อน แล้วตัดสินในหน่วยความจำ — ตัวตัดสิน
            // เป็น pure helper ที่ EF แปลเป็น SQL ไม่ได้ และแถวที่มีเงินในหนึ่ง
            // งวดมีจำนวนจำกัดอยู่แล้ว
            var funded = await _db.PayrollDetails.AsNoTracking()
                .Where(d => d.CompanyId == companyId
                    && d.PayrollRun.Year == req.PeriodYear && d.PayrollRun.Month == req.PeriodMonth
                    && d.PayrollRun.Status == Accounting.Helpers.PayrollRunFilingScope.Paid
                    && (d.SocialSecurityEmployee > 0 || d.SocialSecurityEmployer > 0))
                .Select(d => new { d.Employee.EmployeeCode,
                    Name = d.Employee.FirstNameTh + " " + d.Employee.LastNameTh,
                    Subject = d.Employee.IsSubjectToSocialSecurity,
                    d.SocialSecurityEmployee, d.SocialSecurityEmployer })
                .ToListAsync();
            // เทียบกับ **กติกาเดียวกับที่ exporter ใช้ตัดแถว** (Helpers/SsoFilingScope)
            // ⇒ ครอบทั้งสองทรงที่ทำให้ "เงินที่โอน ≠ ยอดที่ประกาศ": ธง=false แต่มี
            // ยอด · และฝั่งลูกจ้าง=0 ขณะฝั่งนายจ้าง>0 (ม.46 บอกว่าเป็นไปไม่ได้)
            var mismatched = funded
                .Where(x => Accounting.Helpers.SsoFilingScope.IsFundedButUndeclared(
                    x.Subject, x.SocialSecurityEmployee, x.SocialSecurityEmployer))
                .ToList();
            if (mismatched.Count > 0)
            {
                // ยอดที่นำส่ง = ผลรวมของ **ทุกแถว** (PayrollRun.TotalSocialSecurity*)
                // ส่วนไฟล์ประกาศเฉพาะแถวที่ผ่าน IsDeclared ⇒ ส่วนต่างคือยอดทั้งคู่
                // ของแถวเหล่านี้ ไม่ใช่เฉพาะฝั่งนายจ้าง
                var dropped = mismatched.Sum(x => x.SocialSecurityEmployee + x.SocialSecurityEmployer);
                var who = string.Join(" · ", mismatched.Take(5)
                    .Select(x => $"{x.EmployeeCode} {x.Name} "
                        + $"({x.SocialSecurityEmployee + x.SocialSecurityEmployer:N2} — "
                        + Accounting.Helpers.SsoFilingScope.ReasonOf(
                            x.Subject, x.SocialSecurityEmployee, x.SocialSecurityEmployer) + ")"))
                    + (mismatched.Count > 5 ? $" และอีก {mismatched.Count - 5} คน" : "");
                throw new Accounting.Helpers.BusinessRuleException(
                    $"พนักงาน {mismatched.Count} คนมียอดสมทบอยู่ในยอดนำส่ง แต่ไฟล์ สปส.1-10 "
                    + $"จะไม่ประกาศ รวม {dropped:N2} บาท — {who} · "
                    + "เงินที่โอนจะไม่ตรงกับที่ประกาศ แล้ว สปส. ตีกลับทั้งไฟล์ · "
                    + "กรุณาแก้ยอดรายคนที่หน้าเงินเดือน (กลับรายการจ่าย → แก้ยอด → จ่ายใหม่) "
                    + "หรือติ๊ก/ปลดธง “อยู่ในระบบประกันสังคม” ให้ตรงกับความจริง",
                    "SSO-PAIR-CONFLICT");
            }
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

            // ภ.พ.36 — ผูกใบเข้ารายการนำส่งนี้ (คำตัดสินข้อ 133/134): ล็อกแถวเอกสารแล้วตรวจซ้ำว่ายังไม่มีรายการนำส่งอื่นนับไปแล้ว
            // (กดพร้อมกันสองแท็บ = ตัวที่สองถูกปฏิเสธ ไม่ใช่นับซ้ำ) · unique index ในฐานเป็นด่านชั้นที่สอง
            if (pp36Docs != null)
            {
                var docIds = pp36Docs.Select(d => d.Id).ToArray();
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"Documents\" WHERE \"CompanyId\" = {0} AND \"Id\" = ANY({1}) FOR UPDATE",
                    companyId, docIds);
                var taken = await _db.Pp36RemittanceDocuments.AsNoTracking()
                    .Where(x => x.CompanyId == companyId && !x.IsDeleted && docIds.Contains(x.DocumentId))
                    .Select(x => x.DocumentId).ToListAsync();
                if (taken.Count > 0)
                    throw new Accounting.Helpers.BusinessRuleException(
                        $"มีใบ {taken.Count} ใบในงวดนี้ถูกนับในรายการนำส่ง ภ.พ.36 อื่นไปแล้วระหว่างนี้ — โหลดหน้าใหม่แล้วตรวจยอดค้างอีกครั้ง",
                        "PP36-DOC-ALREADY-REMITTED", 409);
                foreach (var d in pp36Docs)
                    _db.Pp36RemittanceDocuments.Add(new Pp36RemittanceDocument
                    {
                        CompanyId = companyId,
                        StatutoryRemittanceId = rec.Id,
                        DocumentId = d.Id,
                        PeriodYear = req.PeriodYear,
                        PeriodMonth = req.PeriodMonth,
                        VatAmount = d.Ledger.Pp36Payable,
                        CreatedBy = performedBy,
                    });
            }

            // SSO — stamp รอบเงินเดือนที่ผูก (ให้หน้า payroll แสดง settled ด้วย)
            if (type == "SsoSps110")
            {
                var runs = await _db.Set<PayrollRun>()
                    .Where(r => r.CompanyId == companyId
                        && r.Status == Accounting.Helpers.PayrollRunFilingScope.Paid
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
            var reportNote = isPp36
                ? await TryEnsurePp36ReportAsync(companyId, req.PeriodYear, req.PeriodMonth)
                : "";

            return new RemitResult(rec.Id, je.Id, amount, lateFee,
                $"นำส่ง{form} งวด {req.PeriodMonth:D2}/{req.PeriodYear} สำเร็จ ({amount + lateFee:N2} บาท){reportNote}");
        }
        catch
        {
            await tx.RollbackAsync();
            // P0-2 คลาสเดียวกัน: ของที่แก้/เพิ่มค้างใน change tracker (รายการนำส่ง · แถวผูก · ธงบนเอกสาร) ต้องไม่ถูก SaveChanges ถัดไปของผู้เรียกบันทึกครึ่งเดียว
            _db.ChangeTracker.Clear();
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
            // นำส่งเพิ่มเติมงวดเดิม (คำตัดสินข้อ 133) — รายงานเป็น snapshot ที่สร้างก่อนใบชุดนี้ ⇒ บอกให้สร้างใหม่ (ไม่ regenerate เองเพราะล้างการแก้ของผู้ใช้)
            if (exists)
                return await _db.Set<StatutoryRemittance>().CountAsync(r => r.CompanyId == companyId && !r.IsDeleted
                        && r.RemittanceType == "VatPp36" && r.PeriodYear == year && r.PeriodMonth == month) > 1
                    ? " · งวดนี้มีรายงาน ภ.พ.36 อยู่แล้ว — กด “สร้างใหม่” ที่หน้ารายงานภาษีเพื่อรวมใบที่นำส่งเพิ่มเติมครั้งนี้"
                    : "";
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

    /// <summary>เงินเพิ่ม §89/1 ที่ระบบเสนอ — วันครบกำหนดจากตาราง <c>TaxFilingDeadline.WarnBy</c> ตัวเดียว (ภ.พ.36 = วันกระดาษ เลื่อนพ้นวันหยุด)</summary>
    private decimal Pp36SurchargeFor(int year, int month, decimal amount, DateTime payDate)
        => Accounting.Helpers.Pp36Lifecycle.SuggestedSurcharge(amount,
            Accounting.Helpers.TaxFilingDeadline.WarnBy("VatPp36", year, month, _holidays), payDate);

    /// <summary>
    /// <b>รับรู้ภาษีซื้อ ภ.พ.36</b> หลังได้ใบเสร็จกรมสรรพากร — JE: Dr 11610 ภาษีซื้อ / Cr 11640 ยังไม่ถึงกำหนด + ประทับวันเคลมลงเอกสาร
    /// (ภ.พ.30 เดือนนั้นดึงเข้าเอง) · หลักกฎหมาย §82/4 ประกอบใบเสร็จ RD (คำตัดสินข้อ 129 — เลิกอ้าง §77/2)
    /// <para>═══ รอบ 203 ทีม F3 (E-2/E-3/E-5 · คำตัดสินข้อ 129/133/136) ═══
    /// (1) <b>เฉพาะใบที่อยู่ในรายการนำส่งแล้ว</b> (ตารางผูก) — เดิมคัด "ใบของงวด" ทุกใบ ⇒ ใบที่อนุมัติหลังนำส่งถูกเคลมทั้งที่ VAT ยังไม่ได้นำส่ง ·
    /// (2) <b>ยอด = ภาษีซื้อที่พัก 11640 จริงในบัญชีแยกประเภทของใบ</b> (บาท) — ไม่ใช่ <c>VatAmount</c> (บริษัทไม่จด VAT / บรรทัดต้องห้าม ⇒ 0 ·
    /// ใบไม่มี JE ⇒ ไม่มีอะไรให้ย้าย) · (3) <b>เลข + วันที่ใบเสร็จ RD บังคับ</b> (ไม่ส่งวันที่ = วันที่จ่ายที่บันทึกตอนนำส่ง) ·
    /// (4) <b>วันเคลมค่าเริ่มต้น = วันที่ใบเสร็จ</b> ผ่าน <c>TaxService.ClaimBasisDate</c> ตัวเดียว (เดิมใช้วันใบผู้ขาย = เคลมก่อนมีหลักฐาน) ·
    /// ผู้ใช้เลือกวันอื่นได้แต่ห้ามก่อนใบเสร็จ · JE ลงวันเดียวกัน · (5) ห้ามเคลมเข้างวด ภ.พ.30 ที่ยื่น/ล็อกแล้ว ·
    /// (6) idempotent ใต้ล็อกแถวรายการนำส่ง (ใบที่รับรู้แล้วมี <c>RecognizedJournalEntryId</c>)</para>
    /// </summary>
    public async Task<RemitResult> RecognizePp36InputVatAsync(Guid companyId, int periodYear,
        int periodMonth, DateTime? recognizeDate, string performedBy,
        string? rdReceiptNumber = null, DateTime? rdReceiptDate = null, Guid? remittanceId = null)
    {
        var periodRemits = await _db.Set<StatutoryRemittance>()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.RemittanceType == "VatPp36"
                && r.PeriodYear == periodYear && r.PeriodMonth == periodMonth)
            .ToListAsync();
        if (periodRemits.Count == 0)
            throw new InvalidOperationException(
                $"ยังไม่ได้นำส่ง ภ.พ.36 งวด {periodMonth:D2}/{periodYear} — นำส่งก่อนแล้วค่อยรับรู้ภาษีซื้อ");

        // ใบที่อยู่ในรายการนำส่งของงวด + ยังไม่รับรู้ + มีภาษีซื้อพัก 11640 จริง (ตัวโหลด/ตัวตัดสินเดียวกับหน้านำส่ง)
        var from = new DateTime(periodYear, periodMonth, 1);
        var periodRows = await Accounting.Helpers.Pp36Ledger.LoadDocsAsync(_db, companyId, from, from.AddMonths(1));
        var candidates = periodRows
            .Where(r => r.State == Accounting.Helpers.Pp36DocState.RemittedAwaitingRecognition && r.Link != null
                && (remittanceId == null || r.Link.StatutoryRemittanceId == remittanceId))
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"ไม่มีภาษีซื้อ ภ.พ.36 ค้างรับรู้ในงวด {periodMonth:D2}/{periodYear} — "
                + "รับรู้ได้เฉพาะใบที่อยู่ในรายการนำส่งแล้วและยังมีภาษีซื้อพัก 11640 (บริษัทไม่จด VAT / ภาษีซื้อต้องห้าม ⇒ VAT เป็นต้นทุนแล้ว ไม่มีอะไรให้รับรู้)");
        var remitIds = candidates.Select(r => r.Link!.StatutoryRemittanceId).Distinct().ToList();
        if (remitIds.Count > 1)
            throw new Accounting.Helpers.BusinessRuleException(
                $"งวด {periodMonth:D2}/{periodYear} นำส่ง ภ.พ.36 หลายครั้งและยังรอรับรู้ {remitIds.Count} รายการ — แต่ละครั้งมีใบเสร็จกรมสรรพากรของตัวเอง "
                + "เลือกรายการนำส่งที่จะรับรู้ (ปุ่มรับรู้ในแถบฟ้าแยกตามรายการนำส่ง)", "PP36-RECOGNIZE-PICK-REMITTANCE");
        var remittance = periodRemits.FirstOrDefault(r => r.Id == remitIds[0])
            ?? throw new InvalidOperationException("ไม่พบรายการนำส่ง ภ.พ.36 ที่ผูกกับใบ");

        // ── คำตัดสินข้อ 136: เลข + วันที่ใบเสร็จกรมสรรพากรบังคับ (ใบเสร็จคือ "ใบกำกับ" ของภาษีซื้อก้อนนี้ §86/14) ──
        var receiptNo = !string.IsNullOrWhiteSpace(rdReceiptNumber) ? rdReceiptNumber.Trim() : remittance.FilingNumber?.Trim();
        if (string.IsNullOrWhiteSpace(receiptNo))
            throw new Accounting.Helpers.BusinessRuleException(
                "ต้องกรอกเลขที่ใบเสร็จกรมสรรพากรของการนำส่ง ภ.พ.36 ก่อนรับรู้ภาษีซื้อ — ไม่มีหลักฐาน = เคลมไม่ได้ (§82/4 ประกอบใบเสร็จ) · "
                + "ดูเลขบนใบเสร็จที่ได้จากการชำระ แล้วกรอกในช่อง “เลขที่ใบเสร็จ”", Accounting.Helpers.Pp36Lifecycle.RuleNoReceipt);
        var receiptDate = (rdReceiptDate ?? remittance.PayDate).Date;
        if (receiptDate < remittance.PayDate.Date)
            throw new Accounting.Helpers.BusinessRuleException(
                $"วันที่ใบเสร็จ {receiptDate:dd/MM/yyyy} อยู่ก่อนวันที่ชำระ ภ.พ.36 ที่บันทึกไว้ ({remittance.PayDate:dd/MM/yyyy}) — ใบเสร็จออกเมื่อชำระแล้วเสมอ ตรวจวันที่อีกครั้ง",
                Accounting.Helpers.Pp36Lifecycle.RuleNoReceipt);

        var claimAcc = await ResolveAccountAsync(companyId, "11610")
            ?? throw new InvalidOperationException("ไม่พบผังบัญชี 11610 (ภาษีซื้อ ภ.พ.30)");
        var undueAcc = await ResolveAccountAsync(companyId, Accounting.Helpers.ForeignServiceVat.Pp36InputVatCode)
            ?? await ResolveAccountAsync(companyId, "11630")
            ?? throw new InvalidOperationException("ไม่พบผังบัญชี 11640 (ภาษีซื้อยังไม่ถึงกำหนด)");

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // ล็อกแถวรายการนำส่ง แล้วอ่านแถวผูกใหม่ใต้ล็อก — กดซ้ำ/สองแท็บพร้อมกัน ⇒ ตัวที่สองไม่เจออะไรค้าง (ไม่ลง JE ซ้ำ)
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"StatutoryRemittances\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
                remittance.Id, companyId);
            var candIds = candidates.Select(c => c.Id).ToList();
            var links = await _db.Pp36RemittanceDocuments
                .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.StatutoryRemittanceId == remittance.Id
                    && candIds.Contains(x.DocumentId) && x.RecognizedJournalEntryId == null)
                .ToListAsync();
            var linkedIds = links.Select(l => l.DocumentId).ToHashSet();
            var docs = (await _db.Documents
                    .Where(d => d.CompanyId == companyId && candIds.Contains(d.Id) && d.InputVatBecameClaimableAt == null)
                    .ToListAsync())
                .Where(d => linkedIds.Contains(d.Id))
                .OrderBy(d => d.DocumentNumber)
                .ToList();
            var undueByDoc = candidates.ToDictionary(c => c.Id, c => c.Ledger.UndueInputVat);
            var vatTotal = docs.Sum(d => undueByDoc[d.Id]);
            if (docs.Count == 0 || vatTotal <= 0.009m)
                throw new InvalidOperationException(
                    $"รายการนำส่ง ภ.พ.36 งวด {periodMonth:D2}/{periodYear} รับรู้ภาษีซื้อไปแล้ว (หรือมีคนกดพร้อมกัน) — โหลดหน้าใหม่");

            // ── คำตัดสินข้อ 129: วันเคลม = วันใบเสร็จ (ตัวตัดสินเดียว TaxService.ClaimBasisDate อ่าน Pp36RdReceiptDate ที่ประทับ) ──
            foreach (var d in docs)
            {
                d.Pp36RdReceiptNumber = receiptNo;
                d.Pp36RdReceiptDate = receiptDate;
            }
            var (claimDate, claimError) = Accounting.Helpers.Pp36Lifecycle.ResolveClaimDate(
                TaxService.ClaimBasisDate(docs[0]), recognizeDate);
            if (claimError != null)
                throw new Accounting.Helpers.BusinessRuleException(claimError, Accounting.Helpers.Pp36Lifecycle.RuleClaimBeforeReceipt);
            var claimAt = claimDate!.Value;

            // ห้ามเคลมเข้างวด ภ.พ.30 ที่ยื่น/ล็อกแล้ว (ปฏิเสธพร้อมทางไปต่อ)
            var filedStatuses = Accounting.Helpers.TaxFilingLockPolicy.DeclaredOrFiledStatuses;
            var claimPeriodClosed = await _db.TaxReports.AsNoTracking().AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted
                && t.TaxType == TaxType.VAT && t.Year == claimAt.Year && t.Month == claimAt.Month
                && (filedStatuses.Contains(t.Status) || t.FilingLockedAt != null));
            if (claimPeriodClosed)
                throw new Accounting.Helpers.BusinessRuleException(
                    Accounting.Helpers.Pp36Lifecycle.ClaimPeriodFiledMessage(claimAt), Accounting.Helpers.Pp36Lifecycle.RuleClaimPeriodFiled);

            if (string.IsNullOrWhiteSpace(remittance.FilingNumber))
            {
                remittance.FilingNumber = receiptNo;
                remittance.UpdatedAt = DateTime.UtcNow;
            }

            var refNo = $"ภ.พ.36R-{periodYear}{periodMonth:D2}"
                + (periodRemits.Count > 1 ? $"-{remittance.Id.ToString()[..8]}" : "");
            var jeReq = new CreateJournalEntryRequest(
                EntryDate: claimAt,
                Description: $"รับรู้ภาษีซื้อ ภ.พ.36 งวด {periodMonth:D2}/{periodYear} — ใบเสร็จกรมสรรพากร {receiptNo} ลว. {receiptDate:dd/MM/yyyy} (§82/4)",
                Reference: refNo,
                Lines: new List<JournalLineRequest>
                {
                    new(claimAcc.Id, vatTotal, 0, "ภาษีซื้อ ภ.พ.30 (จาก ภ.พ.36 ที่นำส่งแล้ว)"),
                    new(undueAcc.Id, 0, vatTotal, "ล้างภาษีซื้อยังไม่ถึงกำหนด (ภ.พ.36)"),
                },
                JournalType: JournalType.General);
            var je = await _accounting.CreateJournalEntryAsync(companyId, jeReq, performedBy);
            await _accounting.PostJournalEntryAsync(companyId, je.Id);

            foreach (var d in docs)
            {
                d.InputVatBecameClaimableAt = claimAt;
                // ⚠️ ตัวบล็อกที่ทำให้ "รับรู้แล้วแต่ไม่โผล่ใน ภ.พ.30/รายการดึงเอกสาร": GenerateVatReport และ GetPullableDocuments รับ PV
                // เข้าฝั่งภาษีซื้อเฉพาะที่ HasTaxInvoiceReference=true — ใบเสร็จ RD คือใบกำกับ §86/14 ⇒ เปิดธงเหมือนเส้น §86/4
                if (d.DocumentType == DocumentType.PaymentVoucher)
                    d.HasTaxInvoiceReference = true;
                d.UpdatedAt = DateTime.UtcNow;
            }
            foreach (var l in links.Where(l => docs.Any(d => d.Id == l.DocumentId)))
            {
                l.RecognizedJournalEntryId = je.Id;
                l.RecognizedAmount = undueByDoc[l.DocumentId];
                l.UpdatedAt = DateTime.UtcNow;
                l.UpdatedBy = performedBy;
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation("Recognized PP36 input VAT {Month}/{Year} {Amt} ({Docs} docs, remittance {Rem}) → JE {Je}",
                periodMonth, periodYear, vatTotal, docs.Count, remittance.Id, je.Id);

            // ── sync รายงานร่างที่มีอยู่แล้วทันที (single source of truth) — นอก transaction: รับรู้สำเร็จแล้ว การ sync รายงานล้มต้องไม่ทำให้ล้มย้อนหลัง ──
            var pullNotes = new List<string>();
            foreach (var d in docs)
            {
                var note = await _tax.TryPullIntoDraftReportAsync(companyId, d.Id);
                _logger.LogInformation("PP36 auto-pull {Doc}: {Note}", d.DocumentNumber, note);
                pullNotes.Add(note);
            }
            var pulled = pullNotes.Count(n => n.StartsWith("ดึงใบ"));
            var pullSummary = pulled > 0 ? $" · ดึงเข้ารายงานร่างที่มีอยู่แล้ว {pulled} ใบ" : "";
            var skipped = periodRows.Count(r => r.State == Accounting.Helpers.Pp36DocState.RemittedNoInputVat
                && r.Link?.StatutoryRemittanceId == remittance.Id);
            var skippedNote = skipped > 0 ? $" · {skipped} ใบไม่มีภาษีซื้อพัก 11640 (VAT เป็นต้นทุนแล้ว) ไม่ต้องรับรู้" : "";

            return new RemitResult(Guid.Empty, je.Id, vatTotal, 0m,
                $"รับรู้ภาษีซื้อ ภ.พ.36 งวด {periodMonth:D2}/{periodYear} จำนวน {vatTotal:N2} บาท ({docs.Count} เอกสาร) — "
                + $"เข้า ภ.พ.30 เดือน {claimAt:MM}/{claimAt.Year + 543} (วันที่ใบเสร็จกรมสรรพากร {receiptDate:dd/MM/yyyy}){pullSummary}{skippedNote}");
        }
        catch
        {
            await tx.RollbackAsync();
            // P0-2 คลาสเดียวกัน: ของที่แก้/เพิ่มค้างใน change tracker (รายการนำส่ง · แถวผูก · ธงบนเอกสาร) ต้องไม่ถูก SaveChanges ถัดไปของผู้เรียกบันทึกครึ่งเดียว
            _db.ChangeTracker.Clear();
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
