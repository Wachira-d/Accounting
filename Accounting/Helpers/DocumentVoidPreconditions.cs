using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>เอกสารที่อ้างใบหนึ่งอยู่จนยกเลิกใบนั้นไม่ได้ — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
/// <param name="ByTextReference">true = ใบลดหนี้/ใบเพิ่มหนี้รุ่นเก่าที่อ้างด้วย "เลขที่" ในช่องอ้างอิง (ไม่มี <c>RelatedDocumentId</c>) ·
/// false = เอกสารลูกที่ผูก <c>RelatedDocumentId</c></param>
public sealed record DocumentVoidChildFact(Guid ParentId, DocumentType ChildType, string? ChildNumber, bool ByTextReference);

/// <summary>
/// **เหตุที่ยกเลิกเอกสารไม่ได้เพราะมีเอกสารอื่นอ้างอยู่ — ตัวตัดสินตัวเดียว** ของ <c>DocumentService.VoidDocumentAsync</c> และด่านก่อน
/// "ยกเลิกการลงบัญชีรอบโอน" (<see cref="SettlementUnpostGate"/> · review198-S3 S3-3 · ทีม S4)
///
/// <para>ที่มา: ด่าน C-2 ตรวจ e-Tax/รายงานล็อก/ภาษีที่ยื่นแล้ว แต่ไม่รู้เหตุที่ <c>VoidDocumentAsync</c> ปฏิเสธเอง (เอกสารลูก active อ้าง ·
/// ใบลดหนี้/ใบเพิ่มหนี้อ้างเลขที่) ⇒ รอบโอนที่มีใบสรุปหลายวันและมีใบลดหนี้อ้างใบหนึ่ง: ใบแรกถูกยกเลิก ใบที่สองล้มกลางทาง = ครึ่งกลับครึ่งค้าง ·
/// เดิมเงื่อนไขทั้งสองเขียนอยู่ในเมธอดเดียว — ย้ายมาที่นี่ให้สองเส้นเรียกตัวเดียว (F2 ข้อ 4 · ห้ามสำเนาที่สอง)</para>
/// <para>กติกา (เหมือนเดิมทุกตัวอักษร): (1) เอกสารลูกที่ <c>RelatedDocumentId</c> = ใบนี้ ไม่ถูกลบ ไม่ Voided/Rejected · (2) ใบลดหนี้/ใบเพิ่มหนี้ที่
/// ไม่มี <c>RelatedDocumentId</c> แต่ <c>Reference</c> = เลขที่ใบนี้ ไม่ใช่ร่าง ไม่ Voided/Rejected — มีข้อ (1) ใช้ข้อ (1) ก่อน</para>
/// </summary>
public static class DocumentVoidPreconditions
{
    /// <summary>ข้อความของเหตุ 1 ข้อ (ข้อความเดิมของ <c>VoidDocumentAsync</c>)</summary>
    internal static string Reason(DocumentVoidChildFact c)
        => c.ByTextReference
            ? $"ยกเลิกไม่ได้ — มี{(c.ChildType == DocumentType.CreditNote ? "ใบลดหนี้" : "ใบเพิ่มหนี้")} "
              + $"{c.ChildNumber} อ้างเลขที่ใบนี้อยู่ (§86/9-10) — ยกเลิกใบนั้นก่อน"
            : $"ยกเลิกไม่ได้ — เอกสารนี้มีเอกสารลูก {c.ChildType} ({c.ChildNumber}) "
              + "อ้างอิงอยู่ (เช่นใบกำกับภาษี/ใบเสร็จที่แปลงไป). กรุณายกเลิกเอกสารลูกก่อน";

    /// <summary>เลือกเหตุของแต่ละใบ — เอกสารลูก (<c>RelatedDocumentId</c>) ก่อนใบที่อ้างด้วยเลขที่ · ใบที่ไม่มีเหตุไม่อยู่ในผล · pure</summary>
    internal static IReadOnlyDictionary<Guid, string> Decide(IEnumerable<DocumentVoidChildFact> facts)
        => facts.GroupBy(f => f.ParentId)
            .ToDictionary(g => g.Key, g => Reason(g.OrderBy(f => f.ByTextReference ? 1 : 0).First()));

    /// <summary>ตัวโหลดข้อเท็จจริง + ตัดสิน สำหรับเอกสารหลายใบ (ของบริษัทนี้) — คืนเหตุต่อใบ · ใบที่ยกเลิกได้ไม่อยู่ในผล</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> ChildBlocksAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return new Dictionary<Guid, string>();
        var ids = documentIds.Distinct().ToList();
        var facts = (await db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.RelatedDocumentId != null && ids.Contains(d.RelatedDocumentId.Value)
                    && !d.IsDeleted
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { Parent = d.RelatedDocumentId!.Value, d.DocumentType, d.DocumentNumber })
                .ToListAsync(ct))
            .Select(d => new DocumentVoidChildFact(d.Parent, d.DocumentType, d.DocumentNumber, false))
            .ToList();

        var numbers = (await db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && ids.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentNumber }).ToListAsync(ct))
            .Where(d => !string.IsNullOrWhiteSpace(d.DocumentNumber)).ToList();
        if (numbers.Count > 0)
        {
            var nums = numbers.Select(n => n.DocumentNumber).Distinct().ToList();
            var textRefs = await db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && (d.DocumentType == DocumentType.CreditNote || d.DocumentType == DocumentType.DebitNote)
                    && d.RelatedDocumentId == null && d.Reference != null && nums.Contains(d.Reference)
                    && d.Status != DocumentStatus.Draft
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.Reference, d.DocumentType, d.DocumentNumber })
                .ToListAsync(ct);
            foreach (var t in textRefs)
                foreach (var parent in numbers.Where(n => n.DocumentNumber == t.Reference))
                    facts.Add(new DocumentVoidChildFact(parent.Id, t.DocumentType, t.DocumentNumber, true));
        }
        return Decide(facts);
    }
}
