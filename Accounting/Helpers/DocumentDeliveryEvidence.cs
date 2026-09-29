using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **"ส่งเอกสารให้ลูกค้าแล้ว" — ตัดสินจากหลักฐานการส่งจริงตัวเดียว** (ฝ่ายค้านรอบ 200 V2-C1 → DECISIONS ข้อ 25)
///
/// <para>ที่มา: ตัวแยกของกำพร้า (DECISIONS ข้อ 10) นับ "ส่งลูกค้าแล้ว" จาก <c>Document.Status == DocumentStatus.Sent</c> ซึ่ง<b>ไม่มีโค้ดไหนประทับ</b>
/// (นับทั้งเรพ: Sent = 0 จุด · <c>DocumentEmailService</c> เขียนแค่ <c>DocumentEmailLog.Status</c>) ⇒ เงื่อนไขตาย (F2 ข้อ 2) — ใบลดหนี้ที่อีเมลให้ลูกค้าแล้ว
/// ถูกพาไปยกเลิกแทนการรับรู้ · ตอนนี้: หลักฐาน = แถว <c>DocumentEmailLog</c> ของใบนั้นที่ส่ง<b>สำเร็จ</b> (<see cref="EmailLogStatus.Sent"/> — ครอบทั้งอีเมลปกติและ
/// e-Tax by email ซึ่งเขียนแถวชนิดเดียวกันพร้อม <c>DocumentId</c>) · สถานะเอกสารไม่ใช่หลักฐาน (ไม่มีผู้ประทับ) · ส่งผ่าน LINE ยังไม่มีบันทึกการส่ง
/// (<c>DocumentLineDeliveryService</c> ไม่เขียนแถว) ⇒ ยังนับไม่ได้ — ข้อจำกัดในรายงานทีม SF</para>
/// </summary>
public static class DocumentDeliveryEvidence
{
    /// <summary>บันทึกการส่งหนึ่งแถวเป็นหลักฐานว่า "ลูกค้าได้รับแล้ว" ไหม — ส่งสำเร็จเท่านั้น (รอส่ง/ล้ม/เด้งกลับ ไม่ใช่) · pure</summary>
    internal static bool IsDeliveryEvidence(EmailLogStatus status) => status == EmailLogStatus.Sent;

    /// <summary>ใบใน <paramref name="documentIds"/> ที่มีหลักฐานการส่งให้ลูกค้าแล้ว (tenant · ไม่นับแถวที่ลบ)</summary>
    public static async Task<HashSet<Guid>> DeliveredAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> documentIds,
        CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return new HashSet<Guid>();
        var ids = documentIds.Distinct().ToList();
        var rows = await db.DocumentEmailLogs.AsNoTracking()
            .Where(l => l.CompanyId == companyId && !l.IsDeleted && l.DocumentId != null && ids.Contains(l.DocumentId.Value))
            .Select(l => new { DocumentId = l.DocumentId!.Value, l.Status })
            .ToListAsync(ct);
        return rows.Where(r => IsDeliveryEvidence(r.Status)).Select(r => r.DocumentId).ToHashSet();
    }
}
