using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>แถวที่อาจผิดจากบั๊กที่แก้แล้ว — ให้ผู้ทำบัญชีตรวจทีละรายการ (คำตัดสินเจ้าของ 2026-10-08: รายงานอ่านอย่างเดียวก่อน ซ่อมรายตัวหลังอนุมัติ)</summary>
/// <param name="LinkUrl">ลิงก์ไปรายการต้นทาง (เซิร์ฟเวอร์ประกอบ — หน้าแสดงอย่างเดียว) · null = ไม่มี</param>
/// <param name="LinkLabel">ข้อความของลิงก์</param>
/// <param name="FixPath">เส้นทาง POST (ต่อท้าย <c>api/companies/{companyId}/</c>) ของปุ่ม "แก้" รายแถว · null = แถวอ่านอย่างเดียว</param>
/// <param name="FixConfirm">ข้อความยืนยันก่อนกด "แก้" (เซิร์ฟเวอร์ประกอบ — หน้าไม่มีข้อความกติกาเอง · null = ข้อความเดิมของหมวดส่วนลดสแกน)</param>
public sealed record DataIntegritySuspect(string Kind, Guid Id, string Number, DateTime Date, string Detail, decimal? Stored, decimal? Expected,
    string? LinkUrl = null, string? LinkLabel = null, string? FixPath = null, string? FixConfirm = null);

public sealed record DataIntegrityReport(
    List<DataIntegritySuspect> PosOrderTotals,
    List<DataIntegritySuspect> RollupBillingNoteChildren,
    List<DataIntegritySuspect> PurchaseInvoicesBilledFromPoWithGrn,
    List<DataIntegritySuspect> PayrollRunTotals,
    // รอบ 203 — ใบเสร็จที่ออกแล้วของผู้จด VAT ซึ่งพิมพ์ VAT แต่หัวไม่ใช่ใบกำกับ (ตรึง IsTaxInvoiceByLaw=false) · อ่านอย่างเดียว
    List<DataIntegritySuspect> PlainReceiptsWithVat,
    // รอบ 204 (คำตัดสินข้อ 148) — ดอกเบี้ย/ปันผล ม.40(4) จ่ายบุคคลธรรมดาที่บันทึกเป็น ภ.ง.ด.3 ก่อนมีแบบ ภ.ง.ด.2 (50 ทวิ · เอกสารที่ลง 21916 · ใบปน) · อ่านอย่างเดียว
    List<DataIntegritySuspect> Section40_4RecordedAsPnd3,
    // รอบ 204 — JE จ่ายทิปเดิมที่ลงภาษีหักไว้ที่ 21915 (ผัง ภ.ง.ด.2) ทั้งที่ 50 ทวิ ของทิปเป็น ภ.ง.ด.3 (21916) · อ่านอย่างเดียว
    List<DataIntegritySuspect> TipWhtPostedToPnd2Account,
    // ทีม E 2026-10-13 — เอกสารจากสแกนที่ "เปิดแก้แล้วบันทึก ยอดขยับ" (ส่วนลด % ซ้อนบนราคาที่ลดแล้ว · ก่อนทีม D B5) · มีปุ่ม "แก้" รายใบ (metadata อย่างเดียว)
    List<DataIntegritySuspect> OcrLineDiscountWouldChangeOnResave,
    // DECISIONS ข้อ 111 — รอบเงินเดือนที่ยกเลิกแล้วแต่ 50 ทวิ ยังสถานะออกแล้ว · อ่านอย่างเดียว (ไม่ยกเลิกให้ — อาจยื่นไปแล้ว)
    List<DataIntegritySuspect> VoidedPayrollRunsWithIssuedWhtCerts,
    // ฝ่ายค้านรอบส่งออก 2026-10-15 P1-1 — ยอดยกมาจากการปิดปีที่เก็บผิด (ปีเก่านับซ้ำตั้งแต่ปิดปีครั้งที่สอง) · ปุ่ม "แก้" ต่อปี (เจ้าของบริษัท)
    List<DataIntegritySuspect> OpeningRollMismatches,
    // ฝ่ายค้านรอบส่งออก 2026-10-15 P1-2 — ผังที่นำเข้าแล้วไม่มีบัญชีแม่ (หัวบัญชีถูกหยิบเป็นผังตั้งต้น) · ปุ่ม "แก้" ต่อผัง (เจ้าของบริษัท)
    List<DataIntegritySuspect> UnlinkedImportedAccounts,
    // ฝ่ายค้านรอบส่งออก รอบ 3 P2-7 — ผังที่การผูกรอบแรก (825f5c0e) วางใต้ผังลงรายการของแม่แบบ (ผังลงรายการกลายเป็นบัญชีคุม) · ปุ่ม "แก้" ต่อผัง (เจ้าของบริษัท)
    List<DataIntegritySuspect> TemplateLeafImportedChildren,
    // ทีม F (ฝ่ายค้านข้อ 3) — 50 ทวิ ของใบต่างสกุลที่ออกก่อนแก้ ยอดเป็นสกุลเอกสาร ไม่ใช่บาท · อ่านอย่างเดียว (ไม่แก้ใบที่ออกแล้วให้)
    List<DataIntegritySuspect>? ForeignCurrencyWhtCertsNotInBaht = null)
{
    /// <summary>หัวข้อ + คำอธิบายของทุกหมวด (ตัวตั้ง <see cref="Accounting.Helpers.DataIntegritySections"/>) — หน้าแสดงตามลำดับนี้ ·
    /// ไม่มีชื่อหมวดใน JS อีกต่อไป (ทีม X5 2026-10-15)</summary>
    public IReadOnlyList<Accounting.Helpers.DataIntegritySectionInfo> Sections => Accounting.Helpers.DataIntegritySections.All;

    public int Total => PosOrderTotals.Count + RollupBillingNoteChildren.Count + PurchaseInvoicesBilledFromPoWithGrn.Count
        + PayrollRunTotals.Count + PlainReceiptsWithVat.Count + Section40_4RecordedAsPnd3.Count + TipWhtPostedToPnd2Account.Count
        + OcrLineDiscountWouldChangeOnResave.Count + VoidedPayrollRunsWithIssuedWhtCerts.Count
        + OpeningRollMismatches.Count + UnlinkedImportedAccounts.Count + TemplateLeafImportedChildren.Count
        + (ForeignCurrencyWhtCertsNotInBaht?.Count ?? 0);
}

/// <summary>
/// รายงานตรวจข้อมูลที่บันทึกไว้แล้วจากบั๊กที่แก้วันที่ 2026-10-08 (ทีมตรวจงานค้าง) — <b>อ่านอย่างเดียว ไม่ซ่อมอะไร</b>
/// <list type="bullet">
/// <item>บิล POS ที่ยอดบิลไม่เท่าผลรวมรายการ (เดิมรายการถูกนับสองครั้ง — D3)</item>
/// <item>ใบเสร็จ/ใบแจ้งหนี้/ใบกำกับที่แปลงจากใบวางบิลรวมใบแจ้งหนี้ (รายได้ซ้ำ — C-02)</item>
/// <item>ใบแจ้งหนี้ซื้อที่ผูกใบสั่งซื้อตรงทั้งที่ใบสั่งซื้อมีใบรับสินค้า (สต็อกเข้าสองรอบ — C-03 · PO ผสมบริการ/สินค้าอาจถูกต้อง ต้องดูรายตัว)</item>
/// <item>รอบเงินเดือนที่ยอดรวมไม่เท่าผลรวมแถว (เดิมเพิ่มพนักงานแล้วยอดเบิ้ล — EfNewChild)</item>
/// </list>
/// ทุก query กรองบริษัท · ไม่มีงานไหนเรียกอัตโนมัติ
/// </summary>
public class DataIntegrityReportService
{
    private readonly AccountingDbContext _db;
    private readonly Interfaces.IAccountingService _accounting;
    public DataIntegrityReportService(AccountingDbContext db, Interfaces.IAccountingService? accounting = null)
    {
        _db = db;
        _accounting = accounting ?? new AccountingService(db);
    }

    private const decimal Tol = 0.01m;

    public async Task<DataIntegrityReport> GetAsync(Guid companyId)
    {
        var pos = (await _db.PosOrders.AsNoTracking()
                .Where(o => o.CompanyId == companyId && !o.IsDeleted && o.Status != PosOrderStatus.Voided)   // บิลยกเลิกไม่มีผลทางบัญชี — ไม่รบกวนผู้ตรวจ
                .Select(o => new
                {
                    o.Id, o.OrderNumber, o.CreatedAt, o.Status, o.SubTotal,
                    Items = o.Items.Where(i => !i.IsDeleted).Sum(i => i.SubTotal),
                })
                .ToListAsync())
            .Where(o => Math.Abs(o.SubTotal - o.Items) > Tol)
            .Select(o => new DataIntegritySuspect("PosOrderTotal", o.Id, o.OrderNumber, o.CreatedAt,
                $"สถานะ {o.Status} · ยอดบิลไม่เท่าผลรวมรายการ", o.SubTotal, o.Items))
            .ToList();

        var rollupIds = _db.DocumentLines.AsNoTracking()
            .Where(l => !l.IsDeleted && l.SourceDocumentId != null && l.Document.CompanyId == companyId
                && l.Document.DocumentType == DocumentType.BillingNote)
            .Select(l => l.DocumentId);
        var bnKids = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.RelatedDocumentId != null
                && rollupIds.Contains(d.RelatedDocumentId.Value)
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && (d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.ReceiptVoucher
                    || d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice))
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentDate, d.DocumentType, d.TotalAmount,
                Bn = _db.Documents.Where(b => b.Id == d.RelatedDocumentId && b.CompanyId == companyId).Select(b => b.DocumentNumber).FirstOrDefault(),
            })
            .ToListAsync();
        var bnList = bnKids.Select(d => new DataIntegritySuspect("RollupBillingNoteChild", d.Id, d.DocumentNumber, d.DocumentDate,
            $"{d.DocumentType} แปลงจากใบวางบิลรวม {d.Bn} — อาจรับรู้รายได้ซ้ำกับใบแจ้งหนี้ในใบวางบิล", d.TotalAmount, null)).ToList();

        var piRows = await _db.Documents.AsNoTracking()
            .Where(pi => pi.CompanyId == companyId && !pi.IsDeleted
                && (pi.DocumentType == DocumentType.PurchaseInvoice || pi.DocumentType == DocumentType.Expense)
                && pi.Status != DocumentStatus.Draft && pi.Status != DocumentStatus.Voided && pi.Status != DocumentStatus.Rejected
                && pi.RelatedDocumentId != null
                && _db.Documents.Any(po => po.Id == pi.RelatedDocumentId && po.CompanyId == companyId
                    && po.DocumentType == DocumentType.PurchaseOrder)
                && _db.Documents.Any(g => g.CompanyId == companyId && !g.IsDeleted && g.RelatedDocumentId == pi.RelatedDocumentId
                    && g.DocumentType == DocumentType.GoodsReceiptNote
                    && g.Status != DocumentStatus.Voided && g.Status != DocumentStatus.Rejected))
            .Select(pi => new
            {
                pi.Id, pi.DocumentNumber, pi.DocumentDate, pi.TotalAmount,
                Po = _db.Documents.Where(po => po.Id == pi.RelatedDocumentId && po.CompanyId == companyId).Select(po => po.DocumentNumber).FirstOrDefault(),
            })
            .ToListAsync();
        var piList = piRows.Select(p => new DataIntegritySuspect("PurchaseInvoiceFromPoWithGrn", p.Id, p.DocumentNumber, p.DocumentDate,
            $"ผูกใบสั่งซื้อ {p.Po} ตรง ทั้งที่ใบสั่งซื้อมีใบรับสินค้า — ตรวจว่าสต็อก/สินค้าคงเหลือลงซ้ำไหม (PO ผสมบริการอาจถูกต้อง)", p.TotalAmount, null)).ToList();

        var runs = (await _db.Set<PayrollRun>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.Status != "Voided")
                .Select(r => new
                {
                    r.Id, r.PayrollNumber, r.PayDate, r.Status, r.TotalNetPay, r.EmployeeCount,
                    Net = _db.Set<PayrollDetail>().Where(d => d.PayrollRunId == r.Id && d.CompanyId == companyId && !d.IsDeleted).Sum(d => d.NetPay),
                    Count = _db.Set<PayrollDetail>().Count(d => d.PayrollRunId == r.Id && d.CompanyId == companyId && !d.IsDeleted),
                })
                .ToListAsync())
            .Where(r => Math.Abs(r.TotalNetPay - r.Net) > Tol || r.EmployeeCount != r.Count)
            .Select(r => new DataIntegritySuspect("PayrollRunTotal", r.Id, r.PayrollNumber, r.PayDate,
                $"สถานะ {r.Status} · จำนวนคนที่บันทึก {r.EmployeeCount} / แถวจริง {r.Count} — กด \"คำนวณใหม่\" (ถ้ายังไม่จ่าย) หรือให้ผู้ทำบัญชีตรวจ JE", r.TotalNetPay, r.Net))
            .ToList();

        // ── รอบ 203 (คำถามเจ้าของ 2026-10-09): ใบเสร็จ/ใบสำคัญรับที่ออกแล้วของผู้จด VAT ซึ่งพิมพ์ VAT แต่หัวไม่ใช่ใบกำกับ ──
        // เกณฑ์ = ค่าที่ตรึงตอนอนุมัติ IsTaxInvoiceByLaw == false (หัวไม่มีคำว่าใบกำกับ) + VAT > 0 · ไม่นับ: มัดจำที่ VAT พักรอ
        // (ยังไม่ใช่จุดความรับผิด — นโยบายบริษัท) · ใบรับชำระของใบกำกับที่ออกแล้ว (VAT รายงานที่ต้นทาง) · ใบที่ไม่เคยตรึง (NULL ≠ false — G3)
        // ห้ามแก้ใบย้อนหลัง (เลขออกแล้ว §86/4) ⇒ ผู้ทำบัญชีเดินเส้น "ยกเลิกและออกใบแทน" รายใบ · ใบใหม่: คำเตือนตอนอนุมัติ (ข้อ 140 — ไม่บล็อก) แล้วมาโผล่ที่นี่
        var issuerVat = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => (bool?)c.IsVatRegistered).FirstOrDefaultAsync() ?? false;
        var plainVatReceipts = new List<DataIntegritySuspect>();
        if (issuerVat)
        {
            var rows = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && (d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.ReceiptVoucher)
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                    && d.VatAmount > 0.005m
                    && d.IsTaxInvoiceByLaw == false
                    && !(d.IsDeposit && d.DepositOutputVatDeferred)
                    && !(d.RelatedDocumentId != null && _db.Documents.Any(s => s.Id == d.RelatedDocumentId
                        && s.CompanyId == companyId && s.DocumentType == DocumentType.TaxInvoice)))
                .Select(d => new { d.Id, d.DocumentNumber, d.DocumentDate, d.DocumentType, d.VatAmount, d.TotalAmount, d.IsDeposit })
                .OrderBy(d => d.DocumentDate)
                .ToListAsync();
            plainVatReceipts = rows.Select(d => new DataIntegritySuspect("PlainReceiptWithVat", d.Id, d.DocumentNumber, d.DocumentDate,
                $"{(d.IsDeposit ? "ใบมัดจำ" : d.DocumentType.ToString())} พิมพ์ VAT {d.VatAmount:N2} แต่หัวเป็น \"ใบเสร็จรับเงิน\" (ไม่ใช่ใบกำกับ) — "
                + "ผู้จด VAT ต้องออกใบกำกับภาษี (เต็มรูป/อย่างย่อ) · ยกเลิกและออกใบแทนหลังเติมที่อยู่ผู้ซื้อ หรือตั้งค่า \"ประกอบกิจการขายปลีก\" §86/6",
                d.TotalAmount, null)).ToList();
        }

        var (pnd3Section40_4, tipOn21915) = await LoadPnd2SuspectsAsync(companyId);
        var ocrLineDiscount = await LoadOcrLineDiscountSuspectsAsync(companyId);
        var voidedRunCerts = await LoadVoidedPayrollRunCertsAsync(companyId);
        var (rollRows, unlinkedRows) = await LoadChartRepairSuspectsAsync(companyId);
        var leafRows = await LoadTemplateLeafChildSuspectsAsync(companyId);
        var foreignCerts = await LoadForeignCurrencyWhtCertsAsync(companyId);
        return new DataIntegrityReport(pos, bnList, piList, runs, plainVatReceipts, pnd3Section40_4, tipOn21915,
            ocrLineDiscount, voidedRunCerts, rollRows, unlinkedRows, leafRows, foreignCerts);
    }

    /// <summary>ทีม F (ฝ่ายค้านข้อ 3) — 50 ทวิ (ไม่ยกเลิก) ของเอกสารต่างสกุลที่ยอดภาษีดูเป็นสกุลเอกสาร (ตัวตัดสิน <see cref="Accounting.Helpers.WhtCertCurrencyCheck"/>) ·
    /// <b>อ่านอย่างเดียว</b> — ใบที่ส่งผู้รับ/ยื่นแล้วห้ามแก้ย้อนหลัง ⇒ ผู้ทำบัญชียกเลิกแล้วออกใบใหม่ (ระบบออกเป็นบาทแล้ว) หรือยื่นแบบเพิ่มเติม</summary>
    private async Task<List<DataIntegritySuspect>> LoadForeignCurrencyWhtCertsAsync(Guid companyId)
    {
        var rows = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && !w.IsDeleted && w.DocumentId != null
                && w.Status != Models.DTOs.Tax.WithholdingTaxCertStatus.Voided)
            .Join(_db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && d.Currency != "THB" && d.ExchangeRate != 1m),
                w => w.DocumentId, d => (Guid?)d.Id,
                (w, d) => new
                {
                    w.Id, w.CertificateNumber, w.IssuedDate, w.CreatedAt, w.Status, w.TaxYear, w.TaxMonth, w.TotalTaxAmount, w.SourcePaymentId,
                    d.DocumentNumber, d.Currency, d.ExchangeRate, d.WithholdingTaxAmount,
                    PaymentWht = w.SourcePaymentId == null ? (decimal?)null
                        : _db.Payments.Where(p => p.Id == w.SourcePaymentId && p.CompanyId == companyId).Select(p => (decimal?)p.WithholdingTaxAmount).FirstOrDefault(),
                })
            .OrderBy(w => w.TaxYear).ThenBy(w => w.TaxMonth).ThenBy(w => w.CertificateNumber)
            .ToListAsync();
        return rows
            .Where(w => Accounting.Helpers.WhtCertCurrencyCheck.LooksDocumentCurrency(w.TotalTaxAmount,
                w.PaymentWht is > 0m ? w.PaymentWht.Value : w.WithholdingTaxAmount, w.ExchangeRate))
            .Select(w => new DataIntegritySuspect(Accounting.Helpers.WhtCertCurrencyCheck.Kind, w.Id, w.CertificateNumber, w.IssuedDate ?? w.CreatedAt,
                $"50 ทวิ {w.CertificateNumber} (งวด {w.TaxMonth:D2}/{w.TaxYear} · สถานะ {w.Status}) ของเอกสาร {w.DocumentNumber} สกุล {w.Currency} @ {w.ExchangeRate:0.######} "
                + "ยอดภาษีบนใบเป็นตัวเลขสกุลเอกสาร ไม่ใช่บาท — ระบบไม่แก้ใบที่ออกแล้วให้ · ยังไม่ยื่น ⇒ ยกเลิกใบนี้แล้วออกใหม่ (ออกเป็นบาท) · ยื่นแล้ว ⇒ ยื่นแบบเพิ่มเติม",
                w.TotalTaxAmount, Accounting.Helpers.PerLineWht.DocumentWhtThb(w.PaymentWht is > 0m ? w.PaymentWht.Value : w.WithholdingTaxAmount, w.ExchangeRate),
                "/pages/wht.html", $"เปิดหน้า 50 ทวิ (ค้นเลข {w.CertificateNumber})"))
            .ToList();
    }

    /// <summary>เส้นทางปุ่ม "แก้" ของยอดยกมาปีหนึ่งปี — ตัวเดียวกับ route ของ <c>DataIntegrityController.RepairOpeningRoll</c></summary>
    public static string OpeningRollFixPath(int year) => $"data-integrity/opening-roll/{year}/repair";

    /// <summary>เส้นทางปุ่ม "แก้" ของผังที่ถูกลบแต่ยังมียอด (กู้คืน) — ตัวเดียวกับ route ของ <c>AccountingController.RestoreAccount</c> (รอบ 4 P2-b)</summary>
    public static string AccountRestoreFixPath(Guid accountId, Guid? newParentId = null)
        => newParentId is Guid p ? $"accounting/accounts/{accountId}/restore?newParentId={p}" : $"accounting/accounts/{accountId}/restore";

    /// <summary>เส้นทางปุ่ม "แก้" ของผังที่อยู่ใต้ผังลงรายการของแม่แบบ — ตัวเดียวกับ route ของ <c>DataIntegrityController.RescueTemplateLeafChild</c></summary>
    public static string TemplateLeafRescueFixPath(Guid accountId) => $"data-integrity/account-leaf/{accountId}/rescue";

    /// <summary>ฝ่ายค้านรอบส่งออก รอบ 3 P2-7 — ผังที่การผูกบัญชีแม่รอบแรก (825f5c0e) วางใต้ผังลงรายการของแม่แบบ (ตัวตัดสินเดียว
    /// <c>ChartParentLinker.FindTemplateLeafChildrenAsync</c> → <c>ChartParentLink.PlanTemplateLeafRescue</c>) · แถวที่ย้ายได้มีปุ่ม "แก้"</summary>
    private async Task<List<DataIntegritySuspect>> LoadTemplateLeafChildSuspectsAsync(Guid companyId)
        => (await _accounting.FindTemplateLeafChildrenAsync(companyId))
            .Select(u => new DataIntegritySuspect("AccountTemplateLeaf", u.AccountId, u.Code, DateTime.UtcNow.Date,
                $"{u.Code} {u.Name} อยู่ใต้ผังลงรายการของแม่แบบ {u.LeafCode} ⇒ {u.LeafCode} กลายเป็นบัญชีคุม (ลงรายการไม่ได้ · ผังตั้งต้นพลิกไปผังอื่น) · "
                    + (u.BlockedReason ?? $"หัวบัญชีที่ควรอยู่: {u.ProposedParentCode}"),
                null, null, "/pages/accounts.html", "ผังบัญชี",
                u.BlockedReason == null ? TemplateLeafRescueFixPath(u.AccountId) : null,
                u.BlockedReason == null
                    ? $"ย้าย {u.Code} ไปอยู่ใต้หัวบัญชี {u.ProposedParentCode} (ผังลูกหลานปรับระดับตาม · บันทึกประวัติ) ⇒ {u.LeafCode} กลับเป็นผังลงรายการ\n\nดำเนินการต่อหรือไม่?"
                    : null))
            .ToList();

    /// <summary>เส้นทางปุ่ม "แก้" ของผังที่ยังไม่ผูกบัญชีแม่ — ตัวเดียวกับ route ของ <c>DataIntegrityController.LinkAccountParent</c></summary>
    public static string AccountParentFixPath(Guid accountId) => $"data-integrity/account-parent/{accountId}/link";

    /// <summary>ฝ่ายค้านรอบส่งออก 2026-10-15 P1-1/P1-2 — ข้อมูลที่ถูกเก็บผิดไปแล้ว (F2 ข้อ 9): ยอดยกมาจากการปิดปี (ตัวตัดสินเดียว
    /// <c>FindOpeningRollMismatchesAsync</c>) และผังที่นำเข้าแต่ยังไม่มีบัญชีแม่ (<c>ChartParentLinker.FindUnlinkedAsync</c>) ·
    /// แถวที่ซ่อมได้มีปุ่ม "แก้" (เซิร์ฟเวอร์ส่งเส้นทาง + ข้อความยืนยัน) · แถวที่ซ่อมอัตโนมัติไม่ได้ = อ่านอย่างเดียวพร้อมเหตุผล</summary>
    private async Task<(List<DataIntegritySuspect> Roll, List<DataIntegritySuspect> Unlinked)> LoadChartRepairSuspectsAsync(Guid companyId)
    {
        var roll = new List<DataIntegritySuspect>();
        foreach (var y in await _accounting.FindOpeningRollMismatchesAsync(companyId))
        {
            var jan = new DateTime(y.Year, 1, 1);
            if (y.SkipReason != null && y.HasImportedRows)
            {
                // งวดที่ผู้ใช้ลงยอดนำเข้าเอง — ยอดไม่ใช่ข้อมูลจากบั๊ก (ไม่รบกวนผู้ตรวจ) · รอบ 4 P2-d: แต่แถวยกยอดรุ่นเก่าไป ม.ค. ยังต้องแสดง + ยกเลิกได้
                // (เดิมข้ามทั้งปี ⇒ แถวรุ่นเก่าซ่อนอยู่ · ด่านปิดปีบล็อกโดยไม่มีปุ่ม) — ปุ่มยกเลิกเฉพาะแถวรุ่นเก่า แถวนำเข้าไม่ถูกแตะ
                if (y.LegacyRolledRows > 0)
                    roll.Add(new DataIntegritySuspect("OpeningRoll", y.FiscalPeriodId ?? Guid.Empty, $"ยอดยกมารุ่นเก่า ม.ค. ปี {y.Year + 543}", jan,
                        y.Note ?? "", null, null, "/pages/reports.html", "รายงานบัญชี", OpeningRollFixPath(y.Year),
                        $"ยกเลิกแถวยกยอดรุ่นเก่าของงวด ม.ค. {y.LegacyRolledRows} แถว (เฉพาะแถวที่การปิดปีเขียน · แถวนำเข้าของงวดแรกของรอบไม่ถูกแตะ) — บันทึกประวัติทุกแถว\n\nดำเนินการต่อหรือไม่?"));
                continue;
            }
            if (y.SkipReason != null && (y.FiscalPeriodId != null || y.LegacyRolledRows > 0 || y.DeletedAccounts is { Count: > 0 }))
            {
                // รอบ 3 P2-6: ซ่อมไม่ได้เพราะเหตุที่ผู้ใช้ต้องแก้ก่อน (ผังที่ถูกลบยังมียอด ฯลฯ) — แสดงเหตุผล + ทางไปต่อ ไม่ข้ามเงียบ
                roll.Add(new DataIntegritySuspect("OpeningRoll", y.FiscalPeriodId ?? Guid.Empty, $"ยอดยกมาปี {y.Year + 543}", jan,
                    y.SkipReason + (y.Note == null ? "" : " · " + y.Note), null, null, "/pages/reports.html", "รายงานบัญชี"));
                // รอบ 4 P2-b: ทางไปต่อที่มีจริง — ปุ่มกู้คืนรายผัง (เจ้าของ · audit) แล้วปิดปี/ซ่อมอีกครั้ง
                foreach (var d in y.DeletedAccounts ?? Array.Empty<Accounting.Models.DTOs.Accounting.OpeningRollDeletedAccount>())
                {
                    // รอบ 5 P2-3: ผังแม่เดิมกลายเป็นผังลงรายการแล้ว ⇒ ปุ่มกู้คืนไปไว้ใต้หัวบัญชีที่เสนอ (ตัวตัดสินเดียวกับ RestoreAccountAsync) ·
                    // ไม่มีหัวบัญชีที่เสนอ = ไม่มีปุ่ม + เหตุผล (เลือกหัวบัญชีเอง)
                    var plan = await _accounting.PlanAccountRestoreAsync(companyId, d.AccountId);
                    var restoreNote = plan.BlockedReason == null ? ""
                        : plan.SuggestedParentId != null
                            ? $" · ผังแม่เดิมรับไม่ได้ ({plan.BlockedReason}) ⇒ ปุ่มนี้กู้คืนไว้ใต้หัวบัญชี {plan.SuggestedParentCode}"
                            : $" · ผังแม่เดิมรับไม่ได้ ({plan.BlockedReason}) และไม่มีหัวบัญชีที่เสนอ — กู้คืนพร้อมเลือกหัวบัญชีเอง (POST accounting/accounts/{d.AccountId}/restore?newParentId=…)";
                    var canFix = plan.BlockedReason == null || plan.SuggestedParentId != null;
                    roll.Add(new DataIntegritySuspect("AccountRestore", d.AccountId, $"ผังที่ถูกลบ {d.AccountCode}", jan,
                        $"{d.AccountCode} {d.AccountName} ถูกลบแล้วแต่ยังมียอด{(d.NetDebit >= 0 ? "เดบิต" : "เครดิต")} {Math.Abs(d.NetDebit):N2} ⇒ ปิดปี/ยกยอดปี {y.Year + 543} ไม่ได้ · "
                            + "กด \"แก้\" เพื่อกู้คืนผัง (เจ้าของบริษัท · บันทึกประวัติ) แล้วปิดปี/ซ่อมอีกครั้ง — ยอดยกไปตามปกติ" + restoreNote,
                        d.NetDebit, null, "/pages/accounts.html", "ผังบัญชี",
                        canFix ? AccountRestoreFixPath(d.AccountId, plan.BlockedReason == null ? null : plan.SuggestedParentId) : null,
                        canFix
                            ? $"กู้คืนผัง {d.AccountCode} {d.AccountName} (ยกเลิกการลบ · บันทึกประวัติ)"
                                + (plan.BlockedReason == null ? "" : $" ไว้ใต้หัวบัญชี {plan.SuggestedParentCode} (ผังแม่เดิมรับบัญชีย่อยไม่ได้แล้ว)")
                                + "\n\nดำเนินการต่อหรือไม่?"
                            : null));
                }
                continue;
            }
            if (y.Mismatches.Count == 0 && y.LegacyRolledRows > 0 && y.SkipReason == null)
                // รอบ 3 P2-4: ยอดงวดแรกของรอบถูกแล้ว แต่ยังมีแถวยกยอดรุ่นเก่าไป ม.ค. ที่งบทดลองอ่าน
                roll.Add(new DataIntegritySuspect("OpeningRoll", y.FiscalPeriodId ?? Guid.Empty, $"ยอดยกมารุ่นเก่า ม.ค. ปี {y.Year + 543}", jan,
                    y.Note ?? "", null, null, "/pages/reports.html", "รายงานบัญชี", OpeningRollFixPath(y.Year),
                    $"ยกเลิกแถวยกยอดรุ่นเก่าของงวด ม.ค. {y.LegacyRolledRows} แถว (เฉพาะแถวที่การปิดปีเขียน) — บันทึกประวัติทุกแถว\n\nดำเนินการต่อหรือไม่?"));
            foreach (var m in y.Mismatches)
                roll.Add(new DataIntegritySuspect("OpeningRoll", m.AccountId, $"ยอดยกมา ม.ค. {y.Year + 543} · {m.AccountCode}", jan,
                    $"{m.AccountCode} {m.AccountName} — ยอดยกมาจากการปิดปี {y.Year + 542} ที่เก็บไว้ ≠ ยอดที่ควรเป็น (ปีเก่าถูกนับซ้ำตั้งแต่ปิดปีครั้งที่สอง) · "
                    + "งบทดลอง/บัญชีแยกประเภท/แดชบอร์ดของปีนี้อ่านยอดนี้ · กด “แก้” เพื่อคำนวณยอดยกมาทั้งปีใหม่ (บันทึกประวัติทุกบัญชี · เจ้าของบริษัทเท่านั้น)"
                    + (y.Note == null ? "" : " · " + y.Note),
                    m.StoredNet, m.ExpectedNet, "/pages/reports.html", "รายงานบัญชี", OpeningRollFixPath(y.Year),
                    $"คำนวณยอดยกมา ม.ค. {y.Year + 543} ใหม่ทุกบัญชีที่ไม่ตรง (ยอดยกมานำเข้า + รายการตั้งแต่วันเริ่มฐาน) — บันทึกประวัติการแก้ทุกบัญชี\n\nดำเนินการต่อหรือไม่?"));
        }
        var unlinked = (await ChartParentLinker.FindUnlinkedAsync(_db, companyId))
            .Select(u => new DataIntegritySuspect("AccountParent", u.AccountId, u.Code, DateTime.UtcNow.Date,
                u.BlockedReason ?? $"{u.Code} {u.Name} — ผังที่นำเข้ายังไม่มีบัญชีแม่ ⇒ ระบบมองว่า “ไม่มีบัญชีย่อย” และอาจหยิบเป็นผังตั้งต้นของการลงบัญชีอัตโนมัติ · "
                    + $"บัญชีแม่ที่ควรเป็น: {u.ProposedParentCode}",
                null, null, "/pages/accounts.html", "ผังบัญชี",
                u.BlockedReason == null ? AccountParentFixPath(u.AccountId) : null,
                u.BlockedReason == null ? $"ผูก {u.Code} ใต้บัญชีแม่ {u.ProposedParentCode} (บันทึกประวัติ)\n\nดำเนินการต่อหรือไม่?" : null))
            .ToList();
        return (roll, unlinked);
    }

    /// <summary>เส้นทางปุ่ม "แก้" ของแถวส่วนลดบรรทัดจากสแกน — ตัวเดียวกับ route ของ <c>DataIntegrityController.FixOcrLineDiscount</c></summary>
    public static string OcrLineDiscountFixPath(Guid documentId) => $"data-integrity/ocr-line-discount/{documentId}/fix";

    /// <summary>
    /// ทีม E 2026-10-13 — เอกสารที่สร้างจากสแกน (ไม่ยกเลิก) ที่มีบรรทัด <c>DiscountPercent &gt; 0</c> + <c>DiscountAmount = 0</c> และการบันทึกซ้ำ
    /// (ตัวคำนวณจริงของ <c>UpdateDocumentAsync</c>) ได้ยอดก่อน VAT ต่างจากที่เก็บเกิน 0.01 — ตัวตัดสินเดียว
    /// <see cref="DocumentService.FindOcrLineDiscountIssues"/> (ตัวเดียวกับปุ่ม "แก้") · ไม่แก้อะไรเอง
    /// </summary>
    private async Task<List<DataIntegritySuspect>> LoadOcrLineDiscountSuspectsAsync(Guid companyId)
    {
        var scanDocIds = _db.Set<OcrScanResult>().AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.CreatedDocumentId != null)
            .Select(s => s.CreatedDocumentId!.Value);
        var docs = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                && scanDocIds.Contains(d.Id)
                && d.Lines.Any(l => !l.IsDeleted && l.DiscountPercent > 0m && l.DiscountAmount == 0m))
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();
        var result = new List<DataIntegritySuspect>();
        foreach (var d in docs)
        {
            var lines = d.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder).ToList();
            List<OcrLineDiscountFinding> issues;
            try { issues = DocumentService.FindOcrLineDiscountIssues(d, lines); }
            catch (Accounting.Helpers.BusinessRuleException ex)
            {
                // ส่วนหักท้ายบิลเกินยอด (ข้อมูลเก่า) — จำลองไม่ได้ ⇒ บอกตรง ๆ ไม่ข้ามเงียบ (F2 ข้อ 7)
                result.Add(new DataIntegritySuspect("OcrLineDiscountWouldChangeOnResave", d.Id, d.DocumentNumber, d.DocumentDate,
                    $"จำลองการบันทึกซ้ำไม่ได้: {ex.Message} — ตรวจในฟอร์มเอกสาร", null, null,
                    $"/pages/documents.html?openDoc={d.Id}", "เปิดเอกสาร"));
                continue;
            }
            if (issues.Count == 0) continue;
            var detail = string.Join(" · ", issues.Select(i =>
                $"บรรทัดที่ {i.LineOrder} \"{i.Description}\" ส่วนลด {i.DiscountPercent:0.##}% ซ้อนบนราคาที่ลดแล้ว: เก็บ {i.Stored:N2} ⇒ บันทึกซ้ำได้ {i.AfterResave:N2}"));
            // ฝ่ายค้านรอบ 204 L2: ใบขายที่ออกแล้ว ⇒ ไม่มีปุ่ม "แก้" (FixPath null = แถวอ่านอย่างเดียว) + เหตุผล/ทางไปต่อ — ตัวตัดสินเดียวกับตัวแก้
            var fixRefusal = await DocumentService.OcrLineDiscountFixRefusalAsync(_db, companyId, d);
            result.Add(new DataIntegritySuspect("OcrLineDiscountWouldChangeOnResave", d.Id, d.DocumentNumber, d.DocumentDate,
                $"สถานะ {d.Status} · {detail} — ยอดที่เก็บไว้ถูกตามกระดาษ · "
                    + (fixRefusal ?? "กด \"แก้\" เพื่อตั้งส่วนลด % = 0 (ยอด/VAT/ยอดรวมไม่เปลี่ยน)"),
                issues.Sum(i => i.Stored), issues.Sum(i => i.AfterResave),
                $"/pages/documents.html?openDoc={d.Id}", "เปิดเอกสาร", fixRefusal == null ? OcrLineDiscountFixPath(d.Id) : null));
        }
        return result;
    }

    /// <summary>ปุ่ม "แก้" ของแถวส่วนลดบรรทัดจากสแกน — ส่งต่อให้ <see cref="DocumentService.FixOcrLineDiscountAsync"/> (ตัวตัดสินเดียวกับรายงาน)</summary>
    public Task<OcrLineDiscountFixResult> FixOcrLineDiscountAsync(Guid companyId, Guid documentId, Guid? actorUserId)
        => DocumentService.FixOcrLineDiscountAsync(_db, companyId, documentId, actorUserId);

    /// <summary>ชนิด/สถานะของเอกสาร (ของบริษัทนี้) — ให้ controller ตรวจสิทธิ์ตามทิศเอกสารก่อนแก้ · null = ไม่พบ</summary>
    public async Task<(DocumentType Type, DocumentStatus Status)?> DocumentTypeAsync(Guid companyId, Guid documentId)
    {
        var row = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.DocumentType, d.Status })
            .FirstOrDefaultAsync();
        return row == null ? null : (row.DocumentType, row.Status);
    }

    /// <summary>
    /// DECISIONS ข้อ 111 (+ ข้อ 113) — รอบเงินเดือนที่ยกเลิกแล้ว (<c>Status == "Voided"</c>) แต่ 50 ทวิ ที่ออกจากรอบนั้น (<c>SourcePayrollRunId</c>)
    /// ยังสถานะออกแล้ว/พิมพ์แล้ว · <b>อ่านอย่างเดียว</b>: ใบอาจยื่นไปแล้ว ⇒ เครื่องยกเลิกเอง = ประทับสถานะปลายทางเอง (R1) — ผู้ทำบัญชีตัดสินรายใบ
    /// (ยังไม่ยื่น ⇒ ยกเลิกที่หน้า 50 ทวิ · ยื่นแล้ว ⇒ ยื่นแบบเพิ่มเติม)
    /// </summary>
    private async Task<List<DataIntegritySuspect>> LoadVoidedPayrollRunCertsAsync(Guid companyId)
    {
        var rows = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && !w.IsDeleted && w.SourcePayrollRunId != null
                && (w.Status == Models.DTOs.Tax.WithholdingTaxCertStatus.Issued
                    || w.Status == Models.DTOs.Tax.WithholdingTaxCertStatus.Printed)
                && _db.Set<PayrollRun>().Any(r => r.Id == w.SourcePayrollRunId && r.CompanyId == companyId && r.Status == "Voided"))
            .Select(w => new
            {
                w.Id, w.CertificateNumber, w.IssuedDate, w.CreatedAt, w.Status, w.TaxFormType, w.TaxYear, w.TaxMonth, w.TotalTaxAmount,
                Payee = w.PayeeContact != null ? w.PayeeContact.Name : null,
                Run = _db.Set<PayrollRun>().Where(r => r.Id == w.SourcePayrollRunId && r.CompanyId == companyId)
                    .Select(r => r.PayrollNumber).FirstOrDefault(),
            })
            .OrderBy(w => w.TaxYear).ThenBy(w => w.TaxMonth).ThenBy(w => w.CertificateNumber)
            .ToListAsync();
        return rows.Select(w => new DataIntegritySuspect("VoidedPayrollRunIssuedWhtCert", w.Id, w.CertificateNumber, w.IssuedDate ?? w.CreatedAt,
            $"รอบเงินเดือน {w.Run} ยกเลิกแล้ว แต่ 50 ทวิ {w.CertificateNumber} ({w.TaxFormType} · งวด {w.TaxMonth:D2}/{w.TaxYear} · ผู้รับ {w.Payee ?? "-"}) "
            + $"ยังสถานะ {w.Status} — ระบบไม่ยกเลิกให้ (อาจยื่นไปแล้ว) · ยังไม่ยื่น ⇒ ยกเลิกใบนี้ที่หน้า 50 ทวิ · ยื่นแล้ว ⇒ ยื่นแบบเพิ่มเติมลดยอด",
            w.TotalTaxAmount, null,
            "/pages/wht.html", $"เปิดหน้า 50 ทวิ (ค้นเลข {w.CertificateNumber})")).ToList();
    }

    /// <summary>
    /// รอบ 204 (คำตัดสินเจ้าของ DECISIONS ข้อ 148) — ข้อมูลที่บันทึกไว้ก่อนมีแบบ ภ.ง.ด.2 · <b>อ่านอย่างเดียว ไม่ย้ายแบบ/ไม่ย้าย GL ให้</b>
    /// (50 ทวิ ที่ออกแล้วส่งมอบผู้รับไปแล้ว + ภ.ง.ด.3 ที่ยื่นแล้วแก้ย้อนหลังไม่ได้ ⇒ ผู้ทำบัญชีตัดสินรายใบ: ยื่นเพิ่มเติม/ยกเลิกออกใบแทน/โอนผัง)
    /// <list type="bullet">
    /// <item>50 ทวิ แบบ ภ.ง.ด.3 (ไม่ยกเลิก) ที่มีบรรทัดเงินได้ ม.40(4) — ตัวตัดสินเดียว <see cref="Accounting.Helpers.ThaiWhtRateTable.IsSection40_4"/></item>
    /// <item>เอกสารฝั่งจ่ายที่บรรทัดหักทั้งหมดเป็น ม.40(4) แต่ JE ลง Cr 21916 (ก่อนรอบ 204) — ตอนนี้ตัวตัดสินจัดเป็น ภ.ง.ด.2 ⇒ หน้านำส่ง ภ.ง.ด.2 จะ Dr 21915
    /// ขณะที่ยอดค้างอยู่ 21916</item>
    /// <item>เอกสารของผู้รับบุคคลธรรมดาที่ปน ม.40(4) กับเงินได้อื่น — <see cref="Accounting.Helpers.WhtPayeeKind.Pnd2MixWarning"/> (ต้องแยกใบ)</item>
    /// <item>JE จ่ายทิป (<see cref="Accounting.Helpers.TipShareAllocation.JournalReferencePrefix"/>) ที่ Cr 21915</item>
    /// </list>
    /// </summary>
    private async Task<(List<DataIntegritySuspect> Section40_4, List<DataIntegritySuspect> TipOn21915)> LoadPnd2SuspectsAsync(Guid companyId)
    {
        var result = new List<DataIntegritySuspect>();

        // ① 50 ทวิ ภ.ง.ด.3 ที่มีเงินได้ ม.40(4) — กรองหยาบใน SQL (รหัสขึ้นต้น "4" = 4a/4b/40(4)…) แล้วตัดสินด้วยตารางกลางในหน่วยความจำ
        var certRows = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && !w.IsDeleted
                && w.TaxFormType == TaxType.WithholdingTax3
                && w.Status != Models.DTOs.Tax.WithholdingTaxCertStatus.Voided
                && w.Lines.Any(l => !l.IsDeleted && l.IncomeTypeCode.StartsWith("4")))
            .Select(w => new
            {
                w.Id, w.CertificateNumber, w.TaxYear, w.TaxMonth, w.IssuedDate, w.CreatedAt, w.Status, w.TotalTaxAmount,
                Lines = w.Lines.Where(l => !l.IsDeleted).Select(l => new { l.IncomeTypeCode, l.TaxAmount }).ToList(),
            })
            .ToListAsync();
        foreach (var c in certRows)
        {
            var hit = c.Lines.Where(l => Accounting.Helpers.ThaiWhtRateTable.IsSection40_4(l.IncomeTypeCode)).ToList();
            if (hit.Count == 0) continue;
            var all = hit.Count == c.Lines.Count;
            result.Add(new DataIntegritySuspect("Pnd3CertWithSection40_4", c.Id, c.CertificateNumber,
                c.IssuedDate ?? c.CreatedAt,
                $"50 ทวิ แบบ ภ.ง.ด.3 (สถานะ {c.Status} · งวด {c.TaxMonth:D2}/{c.TaxYear}) มีดอกเบี้ย/เงินปันผล ม.40(4) "
                + (all ? "ทั้งใบ" : $"{hit.Count} จาก {c.Lines.Count} บรรทัด")
                + " — ม.50(2) ให้ยื่น ภ.ง.ด.2 · ใบที่ยื่นแล้วห้ามแก้ย้อนหลัง: ให้ผู้ทำบัญชีตัดสิน (ยื่น ภ.ง.ด.3 เพิ่มเติมลดยอด + ยื่น ภ.ง.ด.2 · ยกเลิกแล้วออกใบแทน)",
                hit.Sum(l => l.TaxAmount), null));
        }

        // ② เอกสารฝั่งจ่ายที่บรรทัดหักเป็น ม.40(4) (ทั้งใบ = ลง 21916 ไปแล้ว · ปน = ต้องแยกใบ)
        var payerSide = Accounting.Helpers.WhtRemitScope.PayerSideTypes;
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && payerSide.Contains(d.DocumentType)
                && d.WithholdingTaxAmount > 0
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && d.Lines.Any(l => !l.IsDeleted && l.WithholdingTaxAmount > 0 && l.IncomeTypeCode != null && l.IncomeTypeCode.StartsWith("4")))
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentDate, d.PaymentDate, d.WithholdingTaxAmount, d.IsForeignService, d.ContactId, d.WhtPayeeContactId,
                Codes = d.Lines.Where(l => !l.IsDeleted && l.WithholdingTaxAmount > 0).Select(l => l.IncomeTypeCode).ToList(),
                // รอบ 204 (ข้อ 143): ผู้ถูกหักรายบรรทัด — ใบหลายผู้ถูกหักตรวจ "ปน" ต่อราย (ลำดับ/เกณฑ์เดียวกับ Codes)
                WhtLines = d.Lines.Where(l => !l.IsDeleted && l.WithholdingTaxAmount > 0).OrderBy(l => l.LineOrder)
                    .Select(l => new { l.WhtPayeeContactId, l.WithholdingTaxAmount, l.IncomeTypeCode }).ToList(),
                PostedTo21916 = _db.JournalEntryLines.Any(jl => jl.JournalEntry.CompanyId == companyId
                    && jl.JournalEntry.SourceDocumentId == d.Id
                    && jl.JournalEntry.Status == JournalEntryStatus.Posted
                    && jl.Account != null && jl.Account.AccountCode == Accounting.Helpers.WhtPayableAccount.Pnd3Code
                    && jl.CreditAmount > 0),
            })
            .ToListAsync();
        // ฝ่ายค้าน P1 (รอบ 204): ผู้ถูกหักที่มีผลจริงของใบรายเดียว (ทุกบรรทัดที่หักเป็นของรายบรรทัดรายเดียว ⇒ รายนั้น)
        Guid EffectivePayee(Guid contactId, Guid? payeeOverride, IEnumerable<(Guid? Payee, decimal Wht)> lines)
            => Accounting.Helpers.WhtPayeeSplit.EffectiveSinglePayee(contactId, payeeOverride,
                lines.Select(l => new Accounting.Helpers.WhtPayeeLine(l.Payee, l.Wht)).ToList());
        var contactIds = docs.Select(d => EffectivePayee(d.ContactId, d.WhtPayeeContactId, d.WhtLines.Select(l => (l.WhtPayeeContactId, l.WithholdingTaxAmount))))
            .Concat(docs.SelectMany(d => d.WhtLines.Where(l => l.WhtPayeeContactId.HasValue).Select(l => l.WhtPayeeContactId!.Value)))
            .Distinct().ToList();
        var contacts = contactIds.Count == 0
            ? new Dictionary<Guid, Contact>()
            : await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id);
        // ฝ่ายค้านรอบ 204 L7b: ค่าตั้งค่าสิทธิโหลดครั้งเดียว (เดิมโหลดต่อใบในลูป) · วันตัดสินจากตัวเดียวกับ JE (PolicyDate — เดิมใช้ DocumentDate)
        var royaltySetting = await Accounting.Helpers.WhtRoyaltyPolicy.LoadAsync(_db, companyId);
        foreach (var d in docs)
        {
            contacts.TryGetValue(EffectivePayee(d.ContactId, d.WhtPayeeContactId, d.WhtLines.Select(l => (l.WhtPayeeContactId, l.WithholdingTaxAmount))), out var payee);
            var mix = Accounting.Helpers.ThaiWhtRateTable.ClassifySection40_4(d.Codes, royaltySetting.AppliesTo(Accounting.Helpers.WhtRoyaltyPolicy.PolicyDate(null, d.PaymentDate, d.DocumentDate)));   // รอบ 204 c2 (ทีม C): ค่าสิทธิตามค่าตั้ง
            if (mix == Accounting.Helpers.ThaiWhtRateTable.Section40_4Mix.AllSection40_4 && d.PostedTo21916)
            {
                result.Add(new DataIntegritySuspect("Pnd2DocPostedToPnd3Account", d.Id, d.DocumentNumber, d.DocumentDate,
                    $"ดอกเบี้ย/เงินปันผล ม.40(4) ทั้งใบ จ่าย {payee?.Name ?? "(ไม่พบผู้ถูกหัก)"} — ลงภาษีค้างจ่ายไว้ที่ 21916 (ภ.ง.ด.3) ก่อนมีแบบ ภ.ง.ด.2 · "
                    + "ตอนนี้ระบบจัดใบนี้เป็น ภ.ง.ด.2 (นำส่งจาก 21915) ⇒ โอนยอด 21916 → 21915 ด้วยรายการปรับปรุง หรือให้ผู้ทำบัญชีตัดสินถ้า ภ.ง.ด.3 งวดนั้นยื่นแล้ว",
                    d.WithholdingTaxAmount, null));
            }
            else if (mix == Accounting.Helpers.ThaiWhtRateTable.Section40_4Mix.Mixed)
            {
                // ใบหลายผู้ถูกหัก: แบบตัดสินต่อราย ⇒ "ปน" ต้องปนภายในผู้ถูกหักรายเดียวกัน (ดอกเบี้ยของ ก + ค่าบริการของ ข ไม่ใช่ใบปน)
                var portions = Accounting.Helpers.WhtPayeeSplit.Portions(d.ContactId, d.WhtPayeeContactId,
                    d.WhtLines.Select(l => new Accounting.Helpers.WhtPayeeLine(l.WhtPayeeContactId, l.WithholdingTaxAmount)).ToList(),
                    d.WithholdingTaxAmount);
                var lineCodes = d.WhtLines.Select(l => l.IncomeTypeCode).ToList();
                foreach (var portion in portions)
                {
                    var portionPayee = portions.Count <= 1 ? payee : contacts.GetValueOrDefault(portion.PayeeContactId);
                    var portionCodes = portions.Count <= 1 ? (IReadOnlyList<string?>)d.Codes
                        : Accounting.Helpers.WhtIncomeCodes.At(lineCodes, portion.LineIndexes);
                    if (Accounting.Helpers.WhtPayeeKind.Pnd2MixWarning(d.IsForeignService, portionPayee?.CountryCode, portionPayee?.TaxId,
                            portionPayee?.ContactType ?? ContactType.Unknown, portionPayee?.Name, portionCodes) is string mixWarn)
                        result.Add(new DataIntegritySuspect("Section40_4MixedDocument", d.Id, d.DocumentNumber, d.DocumentDate,
                            portions.Count <= 1 ? mixWarn : $"{portionPayee?.Name ?? "ผู้ถูกหัก"}: {mixWarn}",
                            portions.Count <= 1 ? d.WithholdingTaxAmount : portion.WithholdingTaxAmount, null));
                }
            }
        }

        // ③ JE จ่ายทิปที่ลงภาษีหักไว้ 21915
        var tipPrefix = Accounting.Helpers.TipShareAllocation.JournalReferencePrefix;
        var pnd2Code = Accounting.Helpers.WhtPayableAccount.Pnd2Code;
        var tipRows = await _db.JournalEntryLines.AsNoTracking()
            .Where(jl => jl.JournalEntry.CompanyId == companyId
                && jl.JournalEntry.Status == JournalEntryStatus.Posted
                && jl.JournalEntry.Reference != null && jl.JournalEntry.Reference.StartsWith(tipPrefix)
                && jl.Account != null && jl.Account.AccountCode == pnd2Code
                && jl.CreditAmount > 0)
            .Select(jl => new { jl.JournalEntryId, jl.JournalEntry.EntryNumber, jl.JournalEntry.EntryDate, jl.CreditAmount })
            .ToListAsync();
        var tips = tipRows.GroupBy(t => new { t.JournalEntryId, t.EntryNumber, t.EntryDate })
            .Select(g => new DataIntegritySuspect("TipWhtOnPnd2Account", g.Key.JournalEntryId, g.Key.EntryNumber, g.Key.EntryDate,
                $"จ่ายทิปลงภาษีหัก ณ ที่จ่ายไว้ที่ {pnd2Code} (ผัง ภ.ง.ด.2) แต่ 50 ทวิ ของทิปเป็น ภ.ง.ด.3 — โอนยอดไป "
                + $"{Accounting.Helpers.WhtPayableAccount.Pnd3Code} ด้วยรายการปรับปรุง ก่อนนำส่ง ภ.ง.ด.3 ของงวดนั้น (ระบบแก้ทางลงบัญชีของทิปแล้วในรอบ 204)",
                g.Sum(x => x.CreditAmount), null))
            .OrderBy(x => x.Date).ToList();

        return (result.OrderBy(x => x.Date).ToList(), tips);
    }
}
