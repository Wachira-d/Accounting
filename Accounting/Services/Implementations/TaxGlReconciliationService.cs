using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// กระทบยอด **บัญชีแยกประเภท (GL) ↔ รายงานภาษี** ต่องวด — เครื่องมือนักบัญชี
/// สำหรับตอบคำถามเดียวที่สำคัญที่สุดก่อนยื่นแบบ: *"ยอดที่จะยื่น ตรงกับที่ลง
/// บัญชีไหม ถ้าไม่ตรง เพราะอะไร"*
///
/// ทำไมต้องมี: ระบบมีสองโลกที่ต้องเท่ากันเสมอ
///   • **GL** — สิ่งที่ลงบัญชีจริงตอนอนุมัติเอกสาร (Cr 21911 ภาษีขาย,
///     Dr 11610 ภาษีซื้อ, Cr 21916/21917 ภาษีหัก ณ ที่จ่ายค้างจ่าย …)
///   • **รายงานภาษี** — สิ่งที่จะยื่นสรรพากร (ภ.พ.30 / ภ.ง.ด.3 / 53 / 54 / ภ.พ.36)
/// ทั้งคู่สร้างจากคนละเส้นทาง (JE posting vs report generator) จึง drift ได้
/// จากหลายสาเหตุ — และ **ผู้ใช้จะรู้ตอนโดนประเมินย้อนหลัง** ถ้าไม่มีตัวจับ
///
/// จุดต่างจาก <see cref="SubLedgerReconciliationService"/>: ตัวนั้นเทียบ
/// GL ↔ ทะเบียนย่อย (AR/AP/สต๊อก) ตัวนี้เทียบ GL ↔ **แบบที่จะยื่น**
///
/// ทุกบรรทัดที่ไม่ตรงมี <see cref="ReconLine.Causes"/> = รายการ "สาเหตุที่
/// เป็นไปได้" ที่ระบบไล่ตรวจให้จริง ไม่ใช่ข้อความ generic — นักบัญชีกดตามไป
/// แก้ได้ทันที
/// </summary>
public class TaxGlReconciliationService
{
    private readonly AccountingDbContext _db;
    private readonly ITaxService _tax;

    public TaxGlReconciliationService(AccountingDbContext db, ITaxService tax)
    {
        _db = db;
        _tax = tax;
    }

    /// <summary>สาเหตุที่ทำให้ยอดไม่ตรง — ระบบสืบให้จริง 1 รายการ = 1 สาเหตุ</summary>
    /// <param name="Code">รหัสสาเหตุ (ใช้ผูก UI/เอกสารอ้างอิง)</param>
    /// <param name="Message">คำอธิบายภาษาไทยพร้อมตัวเลข</param>
    /// <param name="Amount">ยอดที่สาเหตุนี้อธิบายได้ (ถ้าคำนวณได้)</param>
    /// <param name="DocumentIds">เอกสารที่เกี่ยวข้อง — UI เปิดต่อได้</param>
    /// <param name="Fix">สิ่งที่ต้องทำ</param>
    public record ReconCause(
        string Code,
        string Message,
        decimal? Amount,
        List<Guid> DocumentIds,
        string Fix);

    public record ReconLine(
        string Form,               // "ภ.พ.30 — ภาษีขาย"
        string GlAccountCodes,     // "21911"
        decimal GlAmount,          // ยอดเคลื่อนไหวใน GL งวดนี้
        decimal ReportAmount,      // ยอดในรายงาน/แบบ
        decimal Variance,          // GL − Report
        bool IsMatched,
        string? ReportStatus,      // Draft / Filed / (ยังไม่สร้างรายงาน)
        List<ReconCause> Causes);

    public record ReconResult(
        int Year,
        int Month,
        decimal Tolerance,
        List<ReconLine> Lines,
        int MatchedCount,
        int VarianceCount,
        List<string> GlobalWarnings);

    public async Task<ReconResult> ReconcileAsync(
        Guid companyId, int year, int month, decimal tolerance = 1m)
    {
        var start = new DateTime(year, month, 1);
        var endExclusive = start.AddMonths(1);
        var lines = new List<ReconLine>();
        var warnings = new List<string>();

        // ══ ยอดเคลื่อนไหวใน GL ต่อ "เลขผังบัญชี" ในงวด ══
        // ใช้ JE ที่ Posted และไม่ใช่ตัวกลับรายการ/ถูกกลับ (คู่ reversal หักล้าง
        // กันเองอยู่แล้ว — ถ้านับทั้งคู่จะได้ 0 ปนกับรายการจริง อ่านยาก)
        var glRows = await (
            from l in _db.JournalEntryLines.AsNoTracking()
            join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
            join a in _db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
            where j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && !j.IsDeleted && !l.IsDeleted
                && j.EntryDate >= start && j.EntryDate < endExclusive
            select new { a.AccountCode, l.DebitAmount, l.CreditAmount, j.SourceDocumentId })
            .ToListAsync();

        decimal GlCredit(params string[] codes) => glRows
            .Where(r => codes.Contains(r.AccountCode))
            .Sum(r => r.CreditAmount - r.DebitAmount);
        decimal GlDebit(params string[] codes) => glRows
            .Where(r => codes.Contains(r.AccountCode))
            .Sum(r => r.DebitAmount - r.CreditAmount);

        // ══════════════ ภ.พ.30 — ภาษีขาย / ภาษีซื้อ ══════════════
        var vatReport = await _db.TaxReports.AsNoTracking()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.TaxType == TaxType.VAT
                && r.Year == year && r.Month == month);

        var glOutputVat = GlCredit("21911");
        var glInputVat = GlDebit("11610");

        lines.Add(await BuildLineAsync(companyId, "ภ.พ.30 — ภาษีขาย (21911)", "21911",
            glOutputVat, vatReport?.OutputVat ?? 0m, vatReport?.Status.ToString(),
            tolerance, start, endExclusive, isOutputVat: true, vatReport));
        lines.Add(await BuildLineAsync(companyId, "ภ.พ.30 — ภาษีซื้อ (11610)", "11610",
            glInputVat, vatReport?.InputVat ?? 0m, vatReport?.Status.ToString(),
            tolerance, start, endExclusive, isOutputVat: false, vatReport));

        // ══════════════ ภ.ง.ด.3 / 53 / 54 — ภาษีหัก ณ ที่จ่าย ══════════════
        // GL: Cr 21916 (ภ.ง.ด.3) / 21917 (ภ.ง.ด.53) / 21918 (ภ.ง.ด.54)
        foreach (var (form, code, taxType) in new[]
        {
            ("ภ.ง.ด.3", "21916", TaxType.WithholdingTax3),
            ("ภ.ง.ด.53", "21917", TaxType.WithholdingTax53),
            ("ภ.ง.ด.54", "21918", TaxType.WithholdingTax54),
        })
        {
            var glWht = GlCredit(code);
            var rpt = await _db.TaxReports.AsNoTracking()
                .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.TaxType == taxType
                    && r.Year == year && r.Month == month);
            // ไม่มีทั้ง GL และรายงาน → ไม่ต้องรกหน้าจอ
            if (glWht == 0m && rpt == null) continue;
            lines.Add(await BuildWhtLineAsync(companyId, form, code, taxType,
                glWht, rpt?.TotalTaxWithheld ?? 0m, rpt?.Status.ToString(),
                tolerance, year, month));
        }

        // ══════════════ ภ.พ.36 — VAT ประเมินเองแทนผู้ขายต่างประเทศ ══════════════
        var glPp36 = GlCredit("21912");
        var pp36 = await _db.TaxReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.TaxType == TaxType.VatPp36
                && r.Year == year && r.Month == month);
        if (glPp36 != 0m || pp36 != null)
        {
            var variance = glPp36 - (pp36?.OutputVat ?? 0m);
            var causes = new List<ReconCause>();
            if (pp36 == null && glPp36 != 0m)
                causes.Add(new ReconCause("PP36_NO_REPORT",
                    $"GL มีเจ้าหนี้ ภ.พ.36 (21912) {glPp36:N2} บาท แต่ยังไม่ได้สร้างรายงาน ภ.พ.36 ของงวดนี้",
                    glPp36, new List<Guid>(),
                    "ไปที่ รายงานภาษี → แท็บ ภ.พ.36 → สร้างรายงาน"));
            lines.Add(new ReconLine("ภ.พ.36 — VAT ประเมินเอง (21912)", "21912",
                glPp36, pp36?.OutputVat ?? 0m, variance,
                Math.Abs(variance) <= tolerance, pp36?.Status.ToString() ?? "(ยังไม่สร้างรายงาน)",
                causes));
        }

        // ══ คำเตือนระดับงวด ══
        var unapproved = await _db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted
                && d.Status == DocumentStatus.Draft && d.VatAmount != 0
                && d.DocumentDate >= start && d.DocumentDate < endExclusive);
        if (unapproved > 0)
            warnings.Add($"มีเอกสารที่มี VAT ค้างเป็น \"ร่าง\" {unapproved} ใบในงวดนี้ — "
                + "ยังไม่ลงบัญชีและไม่เข้ารายงาน (อนุมัติก่อนปิดงวด ไม่งั้นทั้ง GL และแบบขาดทั้งคู่พร้อมกัน จับไม่ได้ด้วยการกระทบยอด)");

        var glOnlyJe = glRows.Count(r => r.SourceDocumentId == null
            && (r.AccountCode == "21911" || r.AccountCode == "11610"));
        if (glOnlyJe > 0)
            warnings.Add($"มีบรรทัด JE ภาษีที่ไม่ได้มาจากเอกสาร {glOnlyJe} บรรทัด (คีย์มือ/นำเข้า) — "
                + "รายงานดึงเข้าให้ผ่าน fallback แต่ควรตรวจว่าเลขผู้เสียภาษี/ฐานภาษีถูกต้อง");

        return new ReconResult(year, month, tolerance, lines,
            lines.Count(l => l.IsMatched), lines.Count(l => !l.IsMatched), warnings);
    }

    /// <summary>สร้างบรรทัด ภ.พ.30 + ไล่หาสาเหตุจริงเมื่อไม่ตรง</summary>
    private async Task<ReconLine> BuildLineAsync(
        Guid companyId, string form, string glCode, decimal glAmount, decimal reportAmount,
        string? status, decimal tolerance, DateTime start, DateTime endExclusive,
        bool isOutputVat, TaxReport? report)
    {
        var variance = glAmount - reportAmount;
        var matched = Math.Abs(variance) <= tolerance;
        var causes = new List<ReconCause>();

        if (!matched)
        {
            if (report == null)
            {
                causes.Add(new ReconCause("NO_REPORT",
                    $"ยังไม่ได้สร้างรายงาน ภ.พ.30 ของงวด {start:MM/yyyy} — GL มียอด {glAmount:N2} บาท",
                    glAmount, new List<Guid>(),
                    "ไปที่ รายงานภาษี → สร้างรายงาน → ภาษีมูลค่าเพิ่ม (ภ.พ.30)"));
            }
            else
            {
                // (1) บรรทัดในรายงานที่ถูก "ติ๊กออก" — อยู่ใน GL แต่ไม่นับในแบบ
                var excluded = report.Lines
                    .Where(l => l.IsExcluded && l.IncomeTypeCode != "SUMMARY")
                    .Where(l => isOutputVat
                        ? l.IncomeTypeCode is "OUTPUT" or "JE_OUTPUT" or null or ""
                        : l.IncomeTypeCode is "INPUT" or "JE_INPUT")
                    .ToList();
                if (excluded.Count > 0)
                    causes.Add(new ReconCause("EXCLUDED_LINES",
                        $"มีบรรทัดที่ติ๊ก \"ไม่ใช้\" ในรายงาน {excluded.Count} รายการ รวมภาษี {excluded.Sum(l => l.TaxAmount):N2} บาท "
                        + "(เช่น เกินกรอบ §82/3 · รอใบกำกับ · ยื่นงวดอื่นแล้ว) — อยู่ใน GL แต่ไม่นับในแบบ",
                        excluded.Sum(l => l.TaxAmount),
                        excluded.Where(l => l.DocumentId.HasValue).Select(l => l.DocumentId!.Value).ToList(),
                        "เปิดรายงานแล้วดูบรรทัดที่ขีดฆ่า — ถ้าตั้งใจไม่เคลม ผลต่างนี้อธิบายได้ ไม่ต้องแก้"));

                // (2) ภาษีซื้อที่ยังพัก 11640 (ใบกำกับไม่ครบ §86/4) — ไม่อยู่ทั้งสองฝั่ง
                //     แต่ผู้ใช้มักคิดว่าหาย จึงบอกไว้
                if (!isOutputVat)
                {
                    var undueDocs = await _db.Documents.AsNoTracking()
                        .Where(d => d.CompanyId == companyId && !d.IsDeleted
                            && d.InputVatPostedAsUndue && d.InputVatBecameClaimableAt == null
                            && d.DocumentDate >= start && d.DocumentDate < endExclusive)
                        .Select(d => new { d.Id, d.VatAmount })
                        .ToListAsync();
                    if (undueDocs.Count > 0)
                        causes.Add(new ReconCause("UNDUE_INPUT_VAT",
                            $"ภาษีซื้อ {undueDocs.Sum(d => d.VatAmount):N2} บาท จาก {undueDocs.Count} ใบ ยังพักที่ 11640 "
                            + "(ข้อมูลใบกำกับผู้ขายไม่ครบ §86/4) — ยังเคลมไม่ได้ตามกฎหมาย",
                            undueDocs.Sum(d => d.VatAmount),
                            undueDocs.Select(d => d.Id).ToList(),
                            "เปิดเอกสาร → กรอกเลขที่/วันที่ใบกำกับผู้ขายให้ครบ → ระบบย้าย 11640 → 11610 ให้เอง"));
                }

                // (3) เอกสารในงวดที่มี VAT แต่ "ไม่มีบรรทัดในรายงานเลย"
                var reportDocIds = report.Lines.Where(l => l.DocumentId.HasValue)
                    .Select(l => l.DocumentId!.Value).ToHashSet();
                var salesTypes = new[] { DocumentType.TaxInvoice, DocumentType.Invoice,
                    DocumentType.Receipt, DocumentType.ReceiptVoucher };
                var purchaseTypes = new[] { DocumentType.PurchaseInvoice, DocumentType.Expense,
                    DocumentType.PaymentVoucher };
                var wanted = isOutputVat ? salesTypes : purchaseTypes;
                var missing = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && !d.IsDeleted
                        && d.VatAmount != 0 && wanted.Contains(d.DocumentType)
                        && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                        && d.Status != DocumentStatus.Rejected
                        && (d.TaxPointDate ?? d.DocumentDate) >= start
                        && (d.TaxPointDate ?? d.DocumentDate) < endExclusive)
                    .Select(d => new { d.Id, d.DocumentNumber, d.VatAmount })
                    .ToListAsync();
                var notInReport = missing.Where(d => !reportDocIds.Contains(d.Id)).ToList();
                if (notInReport.Count > 0)
                    causes.Add(new ReconCause("DOC_NOT_IN_REPORT",
                        $"มีเอกสาร {notInReport.Count} ใบ (ภาษีรวม {notInReport.Sum(d => d.VatAmount):N2} บาท) "
                        + "อยู่ในงวดนี้และลงบัญชีแล้ว แต่ไม่มีบรรทัดในรายงาน — "
                        + "มักเกิดเมื่อบันทึกเอกสารเพิ่ม/แก้ไข **หลัง** สร้างรายงาน (รายงานเป็น snapshot ไม่อัปเดตเอง)",
                        notInReport.Sum(d => d.VatAmount),
                        notInReport.Select(d => d.Id).ToList(),
                        "กดปุ่ม \"สร้างใหม่\" ที่รายงานงวดนี้ (ถ้ายังไม่ยื่น) — ระบบจะดึงเอกสารทั้งงวดเข้ามาใหม่"));

                // (4) รายงานมีบรรทัดที่ GL ไม่มี (เอกสารถูกยกเลิกหลังสร้างรายงาน)
                var voidedInReport = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && reportDocIds.Contains(d.Id)
                        && (d.Status == DocumentStatus.Voided || d.IsDeleted))
                    .Select(d => new { d.Id, d.DocumentNumber, d.VatAmount })
                    .ToListAsync();
                if (voidedInReport.Count > 0)
                    causes.Add(new ReconCause("VOIDED_IN_REPORT",
                        $"รายงานอ้างเอกสารที่ถูกยกเลิก/ลบไปแล้ว {voidedInReport.Count} ใบ "
                        + $"(ภาษีรวม {voidedInReport.Sum(d => d.VatAmount):N2} บาท) — GL กลับรายการไปแล้วแต่แบบยังนับอยู่",
                        voidedInReport.Sum(d => d.VatAmount),
                        voidedInReport.Select(d => d.Id).ToList(),
                        "กด \"สร้างใหม่\" ที่รายงาน — ห้ามยื่นก่อนแก้ (ยื่นเกินจริง)"));

                if (causes.Count == 0)
                    causes.Add(new ReconCause("UNKNOWN",
                        $"ผลต่าง {variance:N2} บาท ยังหาสาเหตุอัตโนมัติไม่ได้",
                        variance, new List<Guid>(),
                        "ตรวจสมุดรายวันของบัญชี " + glCode + " งวดนี้ เทียบกับบรรทัดในรายงานทีละรายการ "
                        + "(อาจมี JE คีย์มือที่ลงบัญชีภาษีโดยไม่มีเอกสาร)"));
            }
        }

        return new ReconLine(form, glCode, glAmount, reportAmount, variance, matched,
            status ?? "(ยังไม่สร้างรายงาน)", causes);
    }

    /// <summary>บรรทัด ภ.ง.ด. — สาเหตุหลักคือหนังสือรับรอง 50 ทวิ ไม่ครบ/ไม่ตรงเดือน</summary>
    private async Task<ReconLine> BuildWhtLineAsync(
        Guid companyId, string form, string glCode, TaxType taxType,
        decimal glAmount, decimal reportAmount, string? status,
        decimal tolerance, int year, int month)
    {
        var variance = glAmount - reportAmount;
        var matched = Math.Abs(variance) <= tolerance;
        var causes = new List<ReconCause>();

        if (!matched)
        {
            if (status == null)
                causes.Add(new ReconCause("NO_REPORT",
                    $"GL มีภาษีหัก ณ ที่จ่ายค้างจ่าย {glAmount:N2} บาท แต่ยังไม่ได้สร้างรายงาน {form} ของงวดนี้",
                    glAmount, new List<Guid>(),
                    $"ไปที่ รายงานภาษี → แท็บ {form} → สร้างรายงาน"));

            // (1) หนังสือรับรองที่ยังเป็น "ร่าง" — ไม่นับเข้าแบบ (และไม่เข้าไฟล์ยื่น)
            var draftCerts = await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.TaxFormType == taxType
                    && c.TaxYear == year && c.TaxMonth == month
                    && c.Status == Models.DTOs.Tax.WithholdingTaxCertStatus.Draft)
                .Select(c => new { c.Id, c.CertificateNumber, c.TotalTaxAmount, c.DocumentId })
                .ToListAsync();
            if (draftCerts.Count > 0)
                causes.Add(new ReconCause("DRAFT_CERTS",
                    $"หนังสือรับรองหัก ณ ที่จ่ายยังเป็น \"ร่าง\" {draftCerts.Count} ใบ "
                    + $"รวม {draftCerts.Sum(c => c.TotalTaxAmount):N2} บาท — หักเงินไปแล้ว (อยู่ใน GL) แต่ยังไม่ออกใบให้ผู้ถูกหัก",
                    draftCerts.Sum(c => c.TotalTaxAmount),
                    draftCerts.Where(c => c.DocumentId.HasValue).Select(c => c.DocumentId!.Value).ToList(),
                    "ไปที่หน้า \"หนังสือรับรองหัก ณ ที่จ่าย\" → กดออกใบ → กลับมากด \"สร้างใหม่\" ที่รายงาน"));

            // (2) เอกสารที่หักภาษีแล้วแต่ยังไม่มีหนังสือรับรองเลย
            var purchaseSide = new[] { DocumentType.PurchaseInvoice, DocumentType.Expense,
                DocumentType.PaymentVoucher, DocumentType.CertificateInLieu };
            var start = new DateTime(year, month, 1);
            var endExclusive = start.AddMonths(1);
            var withheldDocs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && purchaseSide.Contains(d.DocumentType)
                    && d.WithholdingTaxAmount != 0
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                    && d.Status != DocumentStatus.Rejected
                    && d.DocumentDate >= start && d.DocumentDate < endExclusive)
                .Select(d => new { d.Id, d.DocumentNumber, d.WithholdingTaxAmount })
                .ToListAsync();
            var coveredDocIds = (await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.DocumentId != null
                    && c.Status != Models.DTOs.Tax.WithholdingTaxCertStatus.Voided)
                .Select(c => c.DocumentId!.Value)
                .ToListAsync()).ToHashSet();
            var noCert = withheldDocs.Where(d => !coveredDocIds.Contains(d.Id)).ToList();
            if (noCert.Count > 0)
                causes.Add(new ReconCause("NO_CERT",
                    $"มีเอกสารที่หักภาษี ณ ที่จ่ายแล้ว {noCert.Count} ใบ รวม {noCert.Sum(d => d.WithholdingTaxAmount):N2} บาท "
                    + "แต่ยังไม่ได้ออกหนังสือรับรอง 50 ทวิ — แบบนำส่งอ่านจากทะเบียนหนังสือรับรอง จึงยังไม่นับ",
                    noCert.Sum(d => d.WithholdingTaxAmount),
                    noCert.Select(d => d.Id).ToList(),
                    "หน้า \"หนังสือรับรองหัก ณ ที่จ่าย\" → ปุ่ม \"รอออกใบ\" → ออกใบให้ครบ → กด \"สร้างใหม่\" ที่รายงาน"));

            // (3) เดือน GL ≠ เดือนบนหนังสือรับรอง (จ่ายข้ามเดือน)
            var certOtherMonth = await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.TaxFormType == taxType
                    && c.DocumentId != null
                    && (c.TaxYear != year || c.TaxMonth != month)
                    && c.Status != Models.DTOs.Tax.WithholdingTaxCertStatus.Voided
                    && _db.Documents.Any(d => d.Id == c.DocumentId!.Value
                        && d.DocumentDate >= start && d.DocumentDate < endExclusive))
                .Select(c => new { c.CertificateNumber, c.TaxYear, c.TaxMonth, c.TotalTaxAmount })
                .ToListAsync();
            if (certOtherMonth.Count > 0)
                causes.Add(new ReconCause("CERT_OTHER_MONTH",
                    $"หนังสือรับรอง {certOtherMonth.Count} ใบ (รวม {certOtherMonth.Sum(c => c.TotalTaxAmount):N2} บาท) "
                    + "ออกเป็นเดือนอื่นทั้งที่เอกสารอยู่งวดนี้ — ปกติถูกต้องเมื่อจ่ายเงินข้ามเดือน "
                    + "(กฎหมายให้นำส่งตามเดือนที่จ่ายจริง) แต่ GL อาจตั้งหนี้คนละเดือน",
                    certOtherMonth.Sum(c => c.TotalTaxAmount), new List<Guid>(),
                    "ตรวจว่าเดือนบนหนังสือรับรอง = เดือนที่จ่ายเงินจริง ถ้าใช่ ผลต่างนี้อธิบายได้"));

            if (causes.Count == 0)
                causes.Add(new ReconCause("UNKNOWN",
                    $"ผลต่าง {variance:N2} บาท ยังหาสาเหตุอัตโนมัติไม่ได้",
                    variance, new List<Guid>(),
                    $"ตรวจสมุดรายวันบัญชี {glCode} งวดนี้ เทียบกับทะเบียนหนังสือรับรองของเดือนเดียวกัน"));
        }

        return new ReconLine(form + $" — ภาษีหัก ณ ที่จ่าย ({glCode})", glCode,
            glAmount, reportAmount, variance, matched,
            status ?? "(ยังไม่สร้างรายงาน)", causes);
    }
}
