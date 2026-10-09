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
    List<DataIntegritySuspect> PlainReceiptsWithVat)
{
    public int Total => PosOrderTotals.Count + RollupBillingNoteChildren.Count + PurchaseInvoicesBilledFromPoWithGrn.Count
        + PayrollRunTotals.Count + PlainReceiptsWithVat.Count;
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

        return new DataIntegrityReport(pos, bnList, piList, runs, plainVatReceipts);
    }
}
