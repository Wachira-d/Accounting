using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// ใบในใบวางบิล — ตัวโหลดเดียว (รอบ 267 P5 · ฝ่ายค้าน 267-P5 LOW-7) · บรรทัดของใบวางบิลอ้างใบแจ้งหนี้ด้วย <c>DocumentLine.SourceDocumentId</c> ·
/// ผู้เรียก: ตัวตัดสินจ่ายทั้งใบ (<c>DocumentPayLinkService.BillingNotePlanAsync</c>) · ตัวติดตามใบลดหนี้ของการคืนเงิน · หลักฐานการชำระที่บันทึกไว้แล้วของเป้าหมาย
/// </summary>
public static class BillingNoteChildren
{
    /// <summary>ใบวางบิลในชุด ⇒ ใบในบิล (เรียงตามบรรทัด · ไม่ซ้ำ) · ใบที่ไม่ใช่ใบวางบิลไม่อยู่ในผล</summary>
    public static async Task<Dictionary<Guid, List<Guid>>> MapAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0) return new();
        var bns = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && documentIds.Contains(d.Id) && d.DocumentType == DocumentType.BillingNote)
            .Select(d => d.Id).ToListAsync(ct);
        if (bns.Count == 0) return new();
        var lines = await db.DocumentLines.AsNoTracking()
            .Where(l => bns.Contains(l.DocumentId) && l.Document.CompanyId == companyId && l.SourceDocumentId != null)
            .OrderBy(l => l.LineOrder)
            .Select(l => new { l.DocumentId, Child = l.SourceDocumentId!.Value }).ToListAsync(ct);
        return bns.ToDictionary(id => id, id => lines.Where(l => l.DocumentId == id).Select(l => l.Child).Distinct().ToList());
    }
}
