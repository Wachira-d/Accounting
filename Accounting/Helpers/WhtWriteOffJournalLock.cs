using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// <b>JE ตัดสูญภาษีถูกหัก (อ้างอิง <see cref="WhtCreditWriteOff.ReferencePrefix"/>) ห้ามกลับรายการ/ยกเลิก/ลบ/แก้ไขจากเส้นทั่วไป — ด่านตัวเดียว</b>
/// (รอบ 243a · ฝ่ายค้าน S3)
///
/// <para><b>บั๊กที่กัน:</b> กลับรายการ JE ตัดสูญจากหน้าสมุดรายวัน ⇒ 11910 กลับมา (GL) แต่ทะเบียน 50 ทวิ ยังเป็น "ตัดสูญ" ⇒ เครดิตหาย/ไม่ตรงกับบัญชีเงียบ ๆ ·
/// ทางเดียวที่ถูกคือ "ได้รับใบแล้ว" ที่หน้า wht-credit (<c>WhtCreditService.MarkReceivedAsync</c>) หรือการยกเลิกต้นทาง (<c>UndoWriteOffAsync</c>) —
/// สองทางนั้นกลับรายการแบบ <c>systemTriggered</c> (ด่านนี้ไม่ขวาง) พร้อมเปลี่ยนสถานะแถวในธุรกรรมเดียว</para>
/// </summary>
public static class WhtWriteOffJournalLock
{
    public const string RuleCode = "GL-WHT-WRITEOFF-ENTRY";

    /// <summary>ข้อความของด่าน (pure) — บอกทางไปต่อที่ทำได้จริง</summary>
    public static string BlockMessage(string action, IEnumerable<string> entryNumbers)
        => $"{action}ใบสำคัญตัดสูญภาษีถูกหัก ณ ที่จ่ายจากสมุดรายวันไม่ได้: {string.Join(" · ", entryNumbers)} — ใบนี้ผูกกับทะเบียนหนังสือรับรอง 50 ทวิ "
           + "(แก้ที่นี่ = บัญชี 11910 เปลี่ยนแต่ทะเบียนยังเป็น \"ตัดสูญ\") · ยังไม่มีอะไรถูกบันทึก · ได้รับหนังสือรับรองภายหลัง ⇒ เปิดหน้า "
           + "\"ภาษีถูกหัก ณ ที่จ่าย\" แล้วกด \"ได้รับใบแล้ว\" ที่รายการนั้น (ระบบกลับรายการและคืนเครดิตให้)";

    /// <summary>ล้มดังเมื่อมีใบใดใน <paramref name="entryIds"/> เป็น JE ตัดสูญ (หรือตัวกลับของมัน) ของบริษัท — เรียกก่อนเขียนอะไร</summary>
    public static async Task EnsureNotWriteOffEntriesAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> entryIds,
        string action, CancellationToken ct = default)
    {
        if (entryIds.Count == 0) return;
        var ids = entryIds.Distinct().ToList();
        var prefix = WhtCreditWriteOff.ReferencePrefix;
        var hits = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.CompanyId == companyId && ids.Contains(j.Id) && j.Reference != null && j.Reference.StartsWith(prefix))
            .Select(j => j.EntryNumber)
            .ToListAsync(ct);
        if (hits.Count == 0) return;
        throw new BusinessRuleException(BlockMessage(action, hits.OrderBy(h => h, StringComparer.Ordinal)), RuleCode, 409);
    }
}
