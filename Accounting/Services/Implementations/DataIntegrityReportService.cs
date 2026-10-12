using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>แถวที่อาจผิดจากบั๊กที่แก้แล้ว — ให้ผู้ทำบัญชีตรวจทีละรายการ (คำตัดสินเจ้าของ 2026-10-08: รายงานอ่านอย่างเดียวก่อน ซ่อมรายตัวหลังอนุมัติ)</summary>
public sealed record DataIntegritySuspect(string Kind, Guid Id, string Number, DateTime Date, string Detail, decimal? Stored, decimal? Expected);

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
    List<DataIntegritySuspect> TipWhtPostedToPnd2Account)
{
    public int Total => PosOrderTotals.Count + RollupBillingNoteChildren.Count + PurchaseInvoicesBilledFromPoWithGrn.Count
        + PayrollRunTotals.Count + PlainReceiptsWithVat.Count + Section40_4RecordedAsPnd3.Count + TipWhtPostedToPnd2Account.Count;
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
    public DataIntegrityReportService(AccountingDbContext db) => _db = db;

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
        return new DataIntegrityReport(pos, bnList, piList, runs, plainVatReceipts, pnd3Section40_4, tipOn21915);
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
                d.Id, d.DocumentNumber, d.DocumentDate, d.WithholdingTaxAmount, d.IsForeignService, d.ContactId, d.WhtPayeeContactId,
                Codes = d.Lines.Where(l => !l.IsDeleted && l.WithholdingTaxAmount > 0).Select(l => l.IncomeTypeCode).ToList(),
                PostedTo21916 = _db.JournalEntryLines.Any(jl => jl.JournalEntry.CompanyId == companyId
                    && jl.JournalEntry.SourceDocumentId == d.Id
                    && jl.JournalEntry.Status == JournalEntryStatus.Posted
                    && jl.Account != null && jl.Account.AccountCode == Accounting.Helpers.WhtPayableAccount.Pnd3Code
                    && jl.CreditAmount > 0),
            })
            .ToListAsync();
        var contactIds = docs.Select(d => Accounting.Helpers.WhtCertIssuePlan.PayeeOf(d.ContactId, d.WhtPayeeContactId)).Distinct().ToList();
        var contacts = contactIds.Count == 0
            ? new Dictionary<Guid, Contact>()
            : await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id);
        foreach (var d in docs)
        {
            contacts.TryGetValue(Accounting.Helpers.WhtCertIssuePlan.PayeeOf(d.ContactId, d.WhtPayeeContactId), out var payee);
            var mix = Accounting.Helpers.ThaiWhtRateTable.ClassifySection40_4(d.Codes);
            if (mix == Accounting.Helpers.ThaiWhtRateTable.Section40_4Mix.AllSection40_4 && d.PostedTo21916)
            {
                result.Add(new DataIntegritySuspect("Pnd2DocPostedToPnd3Account", d.Id, d.DocumentNumber, d.DocumentDate,
                    $"ดอกเบี้ย/เงินปันผล ม.40(4) ทั้งใบ จ่าย {payee?.Name ?? "(ไม่พบผู้ถูกหัก)"} — ลงภาษีค้างจ่ายไว้ที่ 21916 (ภ.ง.ด.3) ก่อนมีแบบ ภ.ง.ด.2 · "
                    + "ตอนนี้ระบบจัดใบนี้เป็น ภ.ง.ด.2 (นำส่งจาก 21915) ⇒ โอนยอด 21916 → 21915 ด้วยรายการปรับปรุง หรือให้ผู้ทำบัญชีตัดสินถ้า ภ.ง.ด.3 งวดนั้นยื่นแล้ว",
                    d.WithholdingTaxAmount, null));
            }
            else if (mix == Accounting.Helpers.ThaiWhtRateTable.Section40_4Mix.Mixed
                && Accounting.Helpers.WhtPayeeKind.Pnd2MixWarning(d.IsForeignService, payee?.CountryCode, payee?.TaxId,
                    payee?.ContactType ?? ContactType.Unknown, payee?.Name, d.Codes) is string mixWarn)
            {
                result.Add(new DataIntegritySuspect("Section40_4MixedDocument", d.Id, d.DocumentNumber, d.DocumentDate,
                    mixWarn, d.WithholdingTaxAmount, null));
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
