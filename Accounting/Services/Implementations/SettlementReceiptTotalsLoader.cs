using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 273 — ตัวโหลด<b>ตัวเดียว</b>ของ <see cref="Document.SettlementTotals"/> (ใบเสร็จรับชำระที่ไม่ถือ VAT ของใบต้นทาง) ใช้ร่วมกันโดย
/// <c>PdfGenerationService.ResolveServedAsReceiptAsync</c> (HTML + QuestPDF + หัวเอกสารของช่องทางอีเมล/LINE) ·
/// <c>DocumentService.GetDocumentAsync</c> (หน้ารายละเอียด + ฟอร์มแก้ไขร่าง) · หน้าเอกสารของลูกค้า (<c>DocumentPayLinkService.CustomerPage</c>) —
/// ห้ามมีสำเนาเงื่อนไขที่สอง (กฎเหล็ก #4 A · ตัวตัดสินอยู่ที่ <see cref="SettlementReceiptTotals"/>)
/// <para>ใบต้นทาง = <c>RelatedDocumentId</c> (ทุกทางเข้าที่ออกใบเสร็จรับชำระ — บันทึกรับชำระ · ออกใบย้อนหลัง/รายใบของการรับหลายใบ · ช่องทางชำระออนไลน์ ·
/// สลิป LINE · คู่ค้า · ย้ายการรับชำระ · ออกใบแทน — ล้วนผ่าน <c>CreateSettlementReceiptAsync</c> ที่ตั้งช่องนี้ · ใบเสร็จที่ผู้ใช้สร้างจากฟอร์ม "ผูกกับเอกสารต้นทาง" ก็ตั้งช่องนี้)</para>
/// <para>ชุดแก้ฝ่ายค้าน: MED-1 การรับครั้งนี้ (<c>SettlementPaymentId</c> — เงิน · ค่าธรรมเนียม · บรรทัดปรับ · ภาษีหักของงวดนี้ / การจัดสรรของการรับหลายใบ) +
/// รายการตัดยอดอื่นของใบต้นทาง (การรับ · การจัดสรร · ใบลดหนี้ · ใบเสร็จที่ผูกเอง) + ยอดค้างปัจจุบัน ⇒ ตัวตัดสินไล่ยอดคงค้างก่อนรับ ·
/// L4 ใบเสร็จรุ่นเก่าจากใบวางบิลรวมใบแจ้งหนี้ ⇒ ใบในบิล (<see cref="BillingNoteChildren"/>) เป็นใบต้นทาง</para>
/// </summary>
internal static class SettlementReceiptTotalsLoader
{
    private static readonly DocumentStatus[] NotLive = { DocumentStatus.Voided, DocumentStatus.Rejected, DocumentStatus.Draft };

    /// <summary>เติม <see cref="Document.SettlementTotals"/> (null เมื่อไม่เข้า) · tenant: ใบต้นทางต้องเป็นบริษัทเดียวกัน</summary>
    public static async Task ResolveAsync(AccountingDbContext db, Guid companyId, Document doc)
    {
        doc.SettlementTotals = null;
        if (!SettlementReceiptTotals.IsReceiptType(doc.DocumentType) || doc.RelatedDocumentId is not Guid srcId) return;
        // เงื่อนไข "ใบถือ VAT เอง" อยู่ที่ตัวตัดสินตัวเดียว (ไม่คัดลอกมากรองล่วงหน้าที่นี่ — กัน drift)
        // ใบต้นทางอาจถูกยกเลิก/ซ่อนแล้ว — อ่านข้าม query filter ให้เลขที่ยังพิมพ์ได้ (เหมือน ResolveReplacementReferenceAsync)
        var parent = await db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.Id == srcId && d.CompanyId == companyId)
            .Select(d => new
            {
                Src = new SettlementReceiptTotals.Source(d.DocumentType, d.DocumentNumber, d.DocumentDate,
                    d.SubTotal, d.VatAmount, d.WithholdingTaxAmount, d.TotalAmount),
                d.BalanceDue,
            })
            .FirstOrDefaultAsync();
        if (parent == null) return;

        SettlementReceiptTotals.Facts Facts(IReadOnlyList<SettlementReceiptTotals.Source> sources) => new(
            doc.DocumentType, doc.IsTaxInvoiceByLaw == true, doc.IsDeposit, doc.VatAmount,
            doc.WithholdingTaxAmount, doc.TotalAmount, sources);

        // L4: ใบเสร็จรุ่นเก่าที่แปลงจากใบวางบิลรวมใบแจ้งหนี้ (ปัจจุบันแปลงไม่ได้แล้ว — BillingNoteKind) ⇒ อ้างใบในบิล ·
        // ใบรุ่นนั้นลงแบบขายสด (ไม่ได้ตัดหนี้ใบในบิล) ⇒ ไม่มีลำดับการรับให้ไล่ — เทียบยอดรับกับยอดใบในบิลรวมแบบเดิม
        if (parent.Src.Type == DocumentType.BillingNote)
        {
            var kids = (await BillingNoteChildren.MapAsync(db, companyId, new[] { srcId }, CancellationToken.None))
                .TryGetValue(srcId, out var k) ? k : new List<Guid>();
            if (kids.Count == 0) return;
            var rows = await db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => kids.Contains(d.Id) && d.CompanyId == companyId)
                .Select(d => new { d.Id, Src = new SettlementReceiptTotals.Source(d.DocumentType, d.DocumentNumber, d.DocumentDate,
                    d.SubTotal, d.VatAmount, d.WithholdingTaxAmount, d.TotalAmount) })
                .ToListAsync();
            var ordered = kids.Select(id => rows.FirstOrDefault(r => r.Id == id)?.Src).Where(s => s != null).Select(s => s!).ToList();
            doc.SettlementTotals = SettlementReceiptTotals.Decide(Facts(ordered) with
            {
                ViaBillingNote = new SettlementReceiptTotals.BillingNoteRef(parent.Src.Number, parent.Src.Date),
            });
            return;
        }

        var sourcesOne = new[] { parent.Src };
        // ไม่เข้าเงื่อนไข (ใบถือ VAT เอง · ต้นทางไม่มี VAT ฯลฯ) ⇒ ไม่ต้องโหลดรายการตัดยอด
        if (SettlementReceiptTotals.Decide(Facts(sourcesOne)) == null) return;

        var me = await ThisSettlementAsync(db, companyId, doc, srcId);
        var events = await LedgerEventsAsync(db, companyId, doc.Id, srcId, me.PaymentId, me.ExcludeAllocationIds);
        doc.SettlementTotals = SettlementReceiptTotals.Decide(Facts(sourcesOne) with
        {
            This = me.This,
            Events = events,
            SourcesOutstandingNow = parent.BalanceDue,
        });
    }

    /// <summary>การรับครั้งนี้: การชำระของใบเสร็จ (รายใบของการรับหลายใบ = การจัดสรรให้ใบต้นทางนี้) · ไม่มีการชำระ = ใบเสร็จเอง (ผูกจากฟอร์ม)</summary>
    private static async Task<(SettlementReceiptTotals.ThisSettlement This, Guid? PaymentId, List<Guid> ExcludeAllocationIds)> ThisSettlementAsync(
        AccountingDbContext db, Guid companyId, Document doc, Guid srcId)
    {
        if (doc.SettlementPaymentId is Guid pid)
        {
            var p = await db.Payments.AsNoTracking()
                .Where(x => x.Id == pid && x.CompanyId == companyId)
                .Select(x => new { x.PaymentDate, x.CreatedAt, x.Amount, x.FeeAmount, x.SettlementAdjustmentAmount, x.WithholdingTaxAmount })
                .FirstOrDefaultAsync();
            if (p != null)
            {
                var mine = await db.PaymentAllocations.AsNoTracking()
                    .Where(a => a.PaymentId == pid && a.DocumentId == srcId && a.CompanyId == companyId && a.ReleasedAt == null)
                    .Select(a => new { a.Id, a.AllocatedAmount, a.WithholdingTaxAmount, a.CreatedAt })
                    .ToListAsync();
                if (mine.Count > 0)
                    return (new SettlementReceiptTotals.ThisSettlement(doc.DocumentDate, mine.Min(a => a.CreatedAt),
                        mine.Sum(a => a.AllocatedAmount), 0m, 0m, mine.Sum(a => a.WithholdingTaxAmount)), pid, mine.Select(a => a.Id).ToList());
                return (new SettlementReceiptTotals.ThisSettlement(p.PaymentDate, p.CreatedAt, p.Amount, p.FeeAmount,
                    p.SettlementAdjustmentAmount, p.WithholdingTaxAmount), pid, new List<Guid>());
            }
        }
        return (new SettlementReceiptTotals.ThisSettlement(doc.DocumentDate, doc.CreatedAt, doc.TotalAmount, 0m, 0m, doc.WithholdingTaxAmount),
            null, new List<Guid>());
    }

    /// <summary>รายการตัดยอดอื่นของใบต้นทาง (บริษัทเดียวกัน · ยังมีผล) — การรับตรง · การจัดสรรของการรับหลายใบ · ใบลดหนี้ · ใบเสร็จที่ผูกเอง (ไม่ใช่ใบรับชำระอัตโนมัติ — กันนับซ้ำกับการรับ)
    /// <para>ตัวโหลดเดียวของแถวยอดใบเสร็จ และตัวตัดสินบทบาทใบกำกับ ณ วันรับเงิน (<see cref="SettlementTaxInvoiceLoader"/> · รอบ 275 แพ็ก C) ·
    /// รอบ 275: การรับชำระที่ถูกยกเลิก (<c>Payment.IsDeleted</c> — ตาราง Payments ไม่มี query filter) ไม่นับ (เดิมนับเป็น "รับชำระก่อนหน้า")</para></summary>
    internal static async Task<List<SettlementReceiptTotals.LedgerEvent>> LedgerEventsAsync(
        AccountingDbContext db, Guid companyId, Guid? exceptDocumentId, Guid srcId, Guid? thisPaymentId, IReadOnlyCollection<Guid> excludeAllocationIds)
    {
        var events = new List<SettlementReceiptTotals.LedgerEvent>();
        var direct = await db.Payments.AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && (x.MovedToDocumentId ?? x.DocumentId) == srcId && x.Id != thisPaymentId
                && !db.PaymentAllocations.Any(a => a.PaymentId == x.Id && a.ReleasedAt == null))
            .Select(x => new { x.PaymentDate, x.CreatedAt, Amount = x.Amount + x.FeeAmount + x.SettlementAdjustmentAmount, x.PaymentNumber })
            .ToListAsync();
        events.AddRange(direct.Select(x => new SettlementReceiptTotals.LedgerEvent(
            SettlementReceiptTotals.EventKind.Payment, x.PaymentDate, x.CreatedAt, x.Amount, x.PaymentNumber)));
        var allocs = await db.PaymentAllocations.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.DocumentId == srcId && a.ReleasedAt == null && !a.Payment.IsDeleted && !excludeAllocationIds.Contains(a.Id))
            .Select(a => new { Date = a.AppliedAt ?? a.Payment.PaymentDate, a.CreatedAt, a.AllocatedAmount })
            .ToListAsync();
        events.AddRange(allocs.Select(a => new SettlementReceiptTotals.LedgerEvent(
            SettlementReceiptTotals.EventKind.Payment, a.Date, a.CreatedAt, a.AllocatedAmount)));
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.RelatedDocumentId == srcId && d.Id != exceptDocumentId && !d.IsDeleted && !NotLive.Contains(d.Status)
                && (d.DocumentType == DocumentType.CreditNote
                    || ((d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.ReceiptVoucher) && !d.IsSettlementReceipt && !d.IsDeposit)))
            .Select(d => new { d.DocumentType, d.DocumentDate, d.CreatedAt, d.TotalAmount, d.DocumentNumber })
            .ToListAsync();
        events.AddRange(docs.Select(d => new SettlementReceiptTotals.LedgerEvent(
            d.DocumentType == DocumentType.CreditNote ? SettlementReceiptTotals.EventKind.CreditNote : SettlementReceiptTotals.EventKind.Receipt,
            d.DocumentDate, d.CreatedAt, d.TotalAmount, d.DocumentNumber)));
        return events;
    }

    /// <summary>ผลของตัวตัดสิน ⇒ DTO ที่หน้าเว็บ (หลังบ้าน · หน้าลูกค้า) แสดงอย่างเดียว — ป้าย/บรรทัดจาก <see cref="Pdf.DocumentLabels"/> ชุดเดียวกับกระดาษ</summary>
    public static SettlementTotalsDto ToDto(SettlementReceiptTotals.View v, Pdf.DocumentLabels labels)
        => new(labels.TotalAmountReceived, v.AmountReceived, v.ShowSettledGrossRow ? labels.TotalSettledGross : null,
            labels.SettlementTotalsLines(v));

    /// <summary>หน้าลูกค้า: โหลดใบตาม id (บริษัทเดียวกัน) แล้วตัดสิน — null = ใบนี้ใช้แถวยอดปกติ</summary>
    public static async Task<SettlementTotalsDto?> DtoForAsync(AccountingDbContext db, Guid companyId, Guid documentId, bool english)
    {
        var doc = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (doc == null || !SettlementReceiptTotals.IsReceiptType(doc.DocumentType)) return null;
        await ResolveAsync(db, companyId, doc);
        return doc.SettlementTotals is { } v ? ToDto(v, Pdf.DocumentLabels.For(english ? "en" : "th")) : null;
    }
}
