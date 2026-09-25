using System.Linq.Expressions;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// "JV ตัดชำระด้วยมัดจำ" (<c>ApplyDepositToInvoiceAsync</c> — Dr หนี้สินมัดจำ / Cr ลูกหนี้ 113 · <c>SourceDocumentId</c> = ใบมัดจำ ·
/// <c>Reference</c> = เลขใบปลายทาง) — ตัวกรอง/สูตรตัวเดียวของทุกเส้นที่ต้อง "หา" หรือ "คืน" JV พวกนี้ (รอบ 194 R2-4/R2-5)
/// <para>ทำไมต้องมีตัวเดียว: หลัง C2 (มัดจำใบเดียวตัดชำระได้หลายใบเมื่อมีใบกำกับของการริบ) ตัวชี้ <c>DepositAppliedToDocumentId</c>
/// ชี้ได้แค่ใบล่าสุด — เส้นยกเลิกใบมัดจำเคยลบยอด Cr 113 ของ JV <b>ทุกใบ</b> ออกจากใบที่ตัวชี้ชี้ใบเดียว (R2-4) และเส้น purge หา JV
/// โดยไม่กรองคู่ที่ถูกกลับแล้ว ⇒ ลบ JV ต้นฉบับทิ้งเหลือตัวกลับกำพร้า + หักยอดรับรู้ซ้ำ (R2-5)</para>
/// </summary>
public static class DepositApplyJournals
{
    /// <summary>JV ตัดชำระที่ "ยังมีผล" (ไม่ใช่ตัวกลับ · ยังไม่ถูกกลับ · ไม่ลบ) และอ้างเลขใบปลายทางนี้ — ใช้หาใบมัดจำที่ต้องคืนยอด
    /// ตอนยกเลิก/ลบใบปลายทาง (<c>DocumentService.DepositsAppliedToAsync</c>) · tenant-safe · รูป expression ให้ EF แปลเป็น SQL</summary>
    public static Expression<Func<JournalEntry, bool>> AppliedTo(Guid companyId, string targetNumber)
        => j => j.CompanyId == companyId && j.Reference == targetNumber && j.SourceDocumentId != null
                && j.OriginalEntryId == null && j.ReversedByEntryId == null && !j.IsDeleted;

    /// <summary>JV ตัดชำระที่ยังมีผลของใบมัดจำ <paramref name="depositId"/> เข้าใบปลายทาง <paramref name="targetNumber"/> — เส้น void (2b) และ purge (0c)
    /// ใช้ตัวเดียวกัน (R2-5: purge เดิมไม่กรองคู่ที่ถูกกลับ ⇒ ลบต้นฉบับทิ้ง ตัวกลับลอย + หักยอดรับรู้ซ้ำ)</summary>
    public static Expression<Func<JournalEntry, bool>> AppliedFromDepositTo(Guid companyId, Guid depositId, string targetNumber)
        => j => j.CompanyId == companyId && j.SourceDocumentId == depositId && j.Reference == targetNumber
                && j.OriginalEntryId == null && j.ReversedByEntryId == null && !j.IsDeleted
                && j.Status == JournalEntryStatus.Posted;

    /// <summary>JE ที่ยังมีผลทั้งหมดของใบมัดจำ (ต้นฉบับ · ยังไม่ถูกกลับ · Posted) — ใช้ "จับภาพ" JV ตัดชำระก่อนยกเลิกใบมัดจำ (R2-4)
    /// และเลือก JE ที่ต้องกลับ (ขั้น 2: JE ที่ถูกกลับไปแล้ว ⇒ กลับซ้ำ = ล้มทั้งการยกเลิก)</summary>
    public static Expression<Func<JournalEntry, bool>> LiveOfSource(Guid companyId, Guid sourceDocumentId)
        => j => j.CompanyId == companyId && j.SourceDocumentId == sourceDocumentId
                && j.OriginalEntryId == null && j.ReversedByEntryId == null && !j.IsDeleted
                && j.Status == JournalEntryStatus.Posted;

    /// <summary>
    /// ยอด gross ที่มัดจำตัดชำระ<b>ต่อใบปลายทาง</b> (รวม Cr ลูกหนี้ 113 ของ JV ที่ยังมีผล จัดกลุ่มตามเลขใบปลายทางใน <c>Reference</c>) —
    /// ตอนยกเลิกใบมัดจำ แต่ละใบปลายทางได้ยอดจ่ายคืน<b>เท่าที่ JV ของใบนั้นตัด</b> (R2-4: มัดจำ 10,000 → F 3,000 + X 7,000 ⇒ F −3,000 · X −7,000 ·
    /// เดิม X −10,000 (ปัดเป็น 0) และ F ค้าง Paid 3,000 ขณะ GL เปิดลูกหนี้ F 3,000)
    /// <para>ขาที่ไม่มีเลขอ้างอิง/ยอด ≤ 0 ไม่นับ · เลขเทียบแบบตรงตัว (เลขเอกสาร unique ต่อบริษัท)</para>
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> GrossByTarget(IEnumerable<(string? Reference, decimal CreditToReceivable)> legs)
    {
        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (reference, credit) in legs)
        {
            if (string.IsNullOrWhiteSpace(reference) || credit <= 0m) continue;
            map[reference] = map.TryGetValue(reference, out var sum) ? sum + credit : credit;
        }
        return map;
    }

    /// <summary>ยอดจ่าย/คงค้าง/สถานะของใบปลายทางหลังคืนยอดที่มัดจำเคยตัดชำระ (<paramref name="restoredGross"/>) — ไม่ติดลบ ·
    /// จ่ายหมดคืน ⇒ <see cref="DocumentStatus.Approved"/> · ครบ ⇒ Paid · ที่เหลือ ⇒ PartiallyPaid (สูตรเดิมของขั้น 7b ย้ายมาไว้ที่เดียว)</summary>
    public static (decimal Paid, decimal BalanceDue, DocumentStatus Status) AfterRestore(decimal totalAmount, decimal paidAmount, decimal restoredGross)
    {
        var paid = Math.Max(0m, paidAmount - Math.Max(0m, restoredGross));
        var balance = totalAmount - paid;
        var status = paid <= 0.01m ? DocumentStatus.Approved
            : balance <= 0.01m ? DocumentStatus.Paid : DocumentStatus.PartiallyPaid;
        return (paid, balance, status);
    }
}
