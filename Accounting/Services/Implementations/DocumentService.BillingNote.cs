using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ใบวางบิล (Billing Note) — รวมใบค้างชำระหลายใบของลูกค้ารายเดียว
/// เป็นใบเรียกเก็บใบเดียวตามรอบวางบิลจริงของไทย.
///
/// หลักบัญชี: ใบวางบิล **ไม่ลง JE** (ตัวหนี้ตั้งไว้แล้วที่ใบแจ้งหนี้/ใบกำกับ —
/// ลงซ้ำ = หนี้ 2 เท่า) เป็นเอกสารเรียกเก็บล้วน ๆ · ไม่ใช่เอกสารภาษี ไม่มี VAT
/// (ยอดต่อบรรทัด = BalanceDue คงค้าง ซึ่งรวม VAT ของใบต้นทางอยู่แล้ว).
///
/// การกันซ้ำ: บรรทัดใบวางบิลเก็บ <see cref="DocumentLine.SourceDocumentId"/>
/// ชี้ใบต้นทาง — ใบเดียวห้ามอยู่ในใบวางบิล active มากกว่า 1 ใบ (ทวงลูกค้าซ้ำ
/// สองทาง = เสียเครดิต).</summary>
public partial class DocumentService
{
    // ชนิดที่รวมเข้าใบวางบิลได้ — ตัวที่ "ตั้งลูกหนี้จริง" เท่านั้น
    // (Receipt/CN ไม่ใช่ยอดเรียกเก็บ · Quotation ยังไม่เป็นหนี้)
    private static readonly DocumentType[] _billableSourceTypes =
    {
        DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote,
    };

    // สถานะที่ยังเรียกเก็บได้ (มีเลขจริง + ยังไม่จบ) — Draft ยังเป็น DRAFT-{guid}
    // ห้ามเอาไปวางบิล (§86/4 เลขจริงออกตอน Approve)
    private static readonly DocumentStatus[] _billableStatuses =
    {
        DocumentStatus.Approved, DocumentStatus.Sent,
        DocumentStatus.PartiallyPaid, DocumentStatus.Overdue,
    };

    private static string BillableTypeLabel(DocumentType t) => t switch
    {
        DocumentType.Invoice => "ใบแจ้งหนี้",
        DocumentType.TaxInvoice => "ใบกำกับภาษี",
        DocumentType.DebitNote => "ใบเพิ่มหนี้",
        _ => t.ToString(),
    };

    /// <summary>ใบวางบิล active (ไม่ถูกลบ/ไม่ Voided/Rejected) ที่มีบรรทัดอ้าง
    /// เอกสารต้นทางเหล่านี้ — คืน map sourceDocId → เลขใบวางบิล.</summary>
    private async Task<Dictionary<Guid, string>> GetActiveBillingRefsAsync(
        Guid companyId, List<Guid> sourceIds)
    {
        if (sourceIds.Count == 0) return new();
        var rows = await (
            from l in _db.DocumentLines.AsNoTracking()
            join d in _db.Documents.AsNoTracking() on l.DocumentId equals d.Id
            where d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentType == DocumentType.BillingNote
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected
                && !l.IsDeleted
                && l.SourceDocumentId != null
                && sourceIds.Contains(l.SourceDocumentId.Value)
            select new { SourceId = l.SourceDocumentId!.Value, d.DocumentNumber })
            .ToListAsync();
        // ใบเดียวไม่ควรอยู่หลาย BN อยู่แล้ว (กติกานี้กันไว้) — เผื่อข้อมูลเก่า
        // เอาเลขแรกพอ
        return rows.GroupBy(r => r.SourceId)
            .ToDictionary(g => g.Key, g => g.First().DocumentNumber);
    }

    public async Task<List<BillingNoteSourceItem>> GetOutstandingInvoicesForBillingAsync(
        Guid companyId, Guid contactId)
    {
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.ContactId == contactId
                && _billableSourceTypes.Contains(d.DocumentType)
                && _billableStatuses.Contains(d.Status)
                && d.BalanceDue > 0.009m)
            .OrderBy(d => d.DocumentDate).ThenBy(d => d.DocumentNumber)
            .ToListAsync();

        var refs = await GetActiveBillingRefsAsync(companyId, docs.Select(d => d.Id).ToList());
        return docs.Select(d => new BillingNoteSourceItem(
            d.Id, d.DocumentNumber, BillableTypeLabel(d.DocumentType),
            d.DocumentDate, d.DueDate, d.TotalAmount, d.BalanceDue,
            refs.TryGetValue(d.Id, out var bn) ? bn : null)).ToList();
    }

    public async Task<DocumentResponse> CreateBillingNoteFromInvoicesAsync(
        Guid companyId, CreateBillingNoteFromInvoicesRequest request, string createdBy)
    {
        var ids = (request.InvoiceIds ?? new List<Guid>()).Distinct().ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("เลือกใบที่จะรวมวางบิลอย่างน้อย 1 ใบ");

        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && ids.Contains(d.Id))
            .ToListAsync();
        if (docs.Count != ids.Count)
            throw new InvalidOperationException("มีใบที่ไม่พบในระบบหรือถูกลบไปแล้ว — โหลดรายการใหม่อีกครั้ง");

        foreach (var d in docs)
        {
            if (!_billableSourceTypes.Contains(d.DocumentType))
                throw new InvalidOperationException(
                    $"ใบ {d.DocumentNumber} เป็น{BillableTypeLabel(d.DocumentType)}ไม่ได้ — รวมได้เฉพาะใบแจ้งหนี้/ใบกำกับภาษี/ใบเพิ่มหนี้");
            if (!_billableStatuses.Contains(d.Status))
                throw new InvalidOperationException(
                    $"ใบ {d.DocumentNumber} สถานะ {d.Status} วางบิลไม่ได้ — ต้องอนุมัติแล้วและยังไม่จบ");
            if (d.BalanceDue <= 0.009m)
                throw new InvalidOperationException($"ใบ {d.DocumentNumber} ชำระครบแล้ว — ไม่มียอดให้เรียกเก็บ");
        }

        var contactIds = docs.Select(d => d.ContactId).Distinct().ToList();
        if (contactIds.Count != 1)
            throw new InvalidOperationException("ใบที่เลือกต้องเป็นลูกค้ารายเดียวกันทั้งชุด — ใบวางบิล 1 ใบต่อลูกค้า 1 ราย");

        var dupRefs = await GetActiveBillingRefsAsync(companyId, ids);
        if (dupRefs.Count > 0)
        {
            var msg = string.Join(" · ", dupRefs.Select(kv =>
            {
                var num = docs.First(d => d.Id == kv.Key).DocumentNumber;
                return $"{num} อยู่ในใบวางบิล {kv.Value} แล้ว";
            }));
            throw new InvalidOperationException($"มีใบที่ถูกวางบิลไปแล้ว — {msg} (ยกเลิกใบวางบิลเดิมก่อนถ้าต้องการย้าย)");
        }

        // 1 บรรทัด = 1 ใบ · ยอด = คงค้าง (รวม VAT ของใบต้นทางแล้ว) · VAT 0
        // เรียงตามวันที่เอกสารให้อ่านบนกระดาษเป็นลำดับเวลา
        var ordered = docs.OrderBy(d => d.DocumentDate).ThenBy(d => d.DocumentNumber).ToList();
        var lines = ordered.Select(d =>
        {
            var desc = $"{BillableTypeLabel(d.DocumentType)} เลขที่ {d.DocumentNumber} "
                + $"ลว. {d.DocumentDate:dd/MM/}{d.DocumentDate.Year + 543}";
            if (d.BalanceDue < d.TotalAmount - 0.009m)
                desc += $" (ยอดค้าง {d.BalanceDue:N2} จากทั้งใบ {d.TotalAmount:N2})";
            return new DocumentLineRequest(
                Description: desc, Quantity: 1, Unit: "ใบ",
                UnitPrice: d.BalanceDue, DiscountPercent: 0,
                VatRate: 0, WithholdingTaxRate: 0, AccountId: null);
        }).ToList();

        var createReq = new CreateDocumentRequest(
            DocumentType: DocumentType.BillingNote,
            // วันที่ตามปฏิทินไทย (Asia/Bangkok) — UtcNow.Date ช่วง 00:00-07:00
            // ของไทยจะได้วันก่อนหน้า (pattern เดียวกับ recurring)
            DocumentDate: Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow),
            DueDate: request.DueDate ?? ordered.Max(d => d.DueDate),
            ContactId: contactIds[0],
            Reference: null,
            Notes: request.Notes
                ?? $"รวมยอดค้างชำระ {ordered.Count} ใบ ({string.Join(", ", ordered.Select(d => d.DocumentNumber))})",
            Lines: lines,
            // ใบวางบิลอยู่ในกลุ่มที่แบรนด์ขึ้นหัวได้ — ลูกค้าที่รับใบแจ้งหนี้ชื่อร้าน
            // ทุกเดือนต้องได้ใบวางบิลชื่อร้านด้วย (ผลตรวจข้อ 7). ใบต้นทางทุกใบมา
            // จากคู่ค้ารายเดียวกันอยู่แล้ว แต่แบรนด์อาจต่างกัน → ใช้ก็ต่อเมื่อ
            // "ทุกใบใช้แบรนด์เดียวกัน" ไม่งั้นเลือกแทนผู้ใช้ไม่ได้ ปล่อยเป็นชื่อบริษัท
            BrandId: ordered.Select(d => d.BrandId).Distinct().Count() == 1
                ? ordered[0].BrandId : null);

        var resp = await CreateDocumentAsync(companyId, createReq, createdBy);

        // stamp ลิงก์ต้นทางต่อบรรทัด (ลำดับบรรทัดตรงกับ ordered เพราะ Create
        // เก็บตามลำดับ Lines) — ใช้กันวางบิลซ้ำในอนาคต
        var created = await _db.DocumentLines
            .Where(l => l.DocumentId == resp.Id && !l.IsDeleted)
            .OrderBy(l => l.LineOrder)
            .ToListAsync();
        for (var i = 0; i < created.Count && i < ordered.Count; i++)
            created[i].SourceDocumentId = ordered[i].Id;
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Billing note {Bn} รวม {Count} ใบของ contact {Contact} ยอด {Amt}",
            resp.DocumentNumber, ordered.Count, contactIds[0], ordered.Sum(d => d.BalanceDue));
        return resp;
    }
}
