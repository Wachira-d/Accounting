using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **ปฏิทินภาษีควรมีแถวของแบบไหนบ้าง** — ตัวตัดสินเดียวของ <c>TaxCalendarService.InitializeYearAsync</c> (กระดานตัดสิน 2026-10-13 · A12)
///
/// <para>ที่มา: <c>InitializeYearAsync</c> สร้างแถว Pending ภ.ง.ด.2 ทุกเดือนให้<b>ทุกบริษัท</b> (รอบ 204 เพิ่มแบบนี้เข้าลิสต์) ⇒ บริษัทที่ไม่เคยจ่าย
/// ดอกเบี้ย/เงินปันผลให้บุคคลธรรมดาเห็น "เลยกำหนด" + เตือนทุกเดือนสำหรับแบบที่ตัวเองไม่ต้องยื่น ⇒ ผู้ใช้ชินกับการปัดทิ้ง = ปิดด่านปฏิทินโดยไม่ตั้งใจ
/// (กฎเหล็ก #4 F2 ข้อ 8 "คำเตือนที่ฟ้องทุกบริษัท") · ภ.ง.ด.54 / ภ.พ.36 อาการเดียวกันสำหรับบริษัทที่ไม่เคยจ่ายต่างประเทศ</para>
///
/// <para><b>กติกา</b> — แบบที่ "ผูกกับกิจกรรม" สร้างเฉพาะเมื่อบริษัทมีกิจกรรมจริงใน<b>ปีก่อนหรือปีนั้น</b> (<see cref="ActivityYears"/>):
/// ภ.ง.ด.2 / ภ.ง.ด.2ก ⇐ หนังสือรับรอง 50 ทวิ แบบ ภ.ง.ด.2 ที่ไม่ถูกยกเลิก · ภ.ง.ด.54 ⇐ 50 ทวิ แบบ ภ.ง.ด.54 หรือเอกสารจ่ายต่างประเทศ
/// (<c>IsForeignService</c> · ผู้รับต่างประเทศที่หักภาษี — ทางเดียวกับ <c>WhtPayeeKind.IsForeignPayee</c>) · ภ.พ.36 ⇐ เอกสาร <c>IsForeignService</c> ·
/// แบบอื่น (ภ.พ.30 · ภ.ง.ด.1/3/53 · สปส. · ภ.ง.ด.50/51 ฯลฯ) สร้างเสมอเหมือนเดิม</para>
///
/// <para><b>สร้างใหม่ (re-initialise)</b> — แถวของแบบที่ไม่มีกิจกรรมถูกลบแบบ soft-delete <b>เฉพาะแถวที่ระบบสร้างแล้วไม่มีใครแตะ</b>
/// (<see cref="CanRetract"/>: Pending หรือ Overdue ที่ระบบประทับเอง · ไม่มีวันยื่น/เลขอ้างอิง/ยอดภาษี/หมายเหตุ) · แถวที่ผู้ใช้ทำเครื่องหมาย
/// (Filed · NotApplicable · InProgress · กรอกข้อมูลไว้) <b>ห้ามแตะ</b> · พอบริษัทเริ่มมีกิจกรรม กด "เริ่มต้นปฏิทิน" อีกครั้ง แถวกลับมา
/// (แถวที่ soft-delete ไม่นับเป็นแถวเดิม)</para>
/// </summary>
public static class TaxCalendarSeedScope
{
    // รหัสแบบบนปฏิทิน (TaxCalendarEvent.TaxFormCode) ของแบบที่ผูกกับกิจกรรม — ตัวตั้งเดียว (ลิสต์ใน TaxCalendarService ใช้ค่าเหล่านี้)
    public const string Pnd2 = "ภ.ง.ด.2";
    public const string Pnd2a = "ภ.ง.ด.2ก";
    public const string Pnd54 = "ภ.ง.ด.54";
    public const string Pp36 = "ภ.พ.36";

    /// <summary>กิจกรรมของบริษัทในช่วง <see cref="ActivityYears"/> — ผู้เรียกโหลดจากฐาน (tenant-scoped) แล้วส่งมา</summary>
    public sealed record Activity(bool Pnd2, bool Pnd54, bool Pp36)
    {
        public static readonly Activity None = new(false, false, false);
    }

    /// <summary>ช่วงปี (ค.ศ.) ที่นับกิจกรรม: ปีก่อน + ปีของปฏิทิน — บริษัทที่จ่ายปีที่แล้วมักจ่ายต่อ (ต้นปียังไม่มีใบของปีนี้)</summary>
    public static (int FromYear, int ToYear) ActivityYears(int year) => (year - 1, year);

    private static bool? Gate(string? taxFormCode, Activity activity) => taxFormCode switch
    {
        Pnd2 or Pnd2a => activity.Pnd2,
        Pnd54 => activity.Pnd54,
        Pp36 => activity.Pp36,
        _ => null,
    };

    /// <summary>สร้างแถวของแบบนี้ให้บริษัทไหม — แบบที่ไม่ผูกกับกิจกรรม = สร้างเสมอ</summary>
    public static bool ShouldSeed(string? taxFormCode, Activity activity) => Gate(taxFormCode, activity) ?? true;

    /// <summary>
    /// แถวนี้ "ระบบสร้างแล้วไม่มีใครแตะ" ไหม — ถอนได้เฉพาะแบบนี้ · Overdue นับด้วยเพราะ <c>GetOverdueAsync</c> ประทับเองจาก Pending
    /// (แถวเลยกำหนดปลอมคืออาการที่ต้องแก้) · ข้อมูลใด ๆ ที่ผู้ใช้กรอก = เป็นของผู้ใช้ ห้ามแตะ
    /// </summary>
    public static bool CanRetract(string? status, DateTime? filedDate, string? filingReference, decimal? taxAmount, string? notes)
        => (string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Overdue", StringComparison.OrdinalIgnoreCase))
           && filedDate == null
           && string.IsNullOrWhiteSpace(filingReference)
           && taxAmount == null
           && string.IsNullOrWhiteSpace(notes);

    /// <summary>ตอนสร้างปฏิทินใหม่: แถวเดิมนี้ต้อง soft-delete ไหม (แบบผูกกิจกรรม · ไม่มีกิจกรรม · ไม่มีใครแตะ)</summary>
    public static bool ShouldRetract(TaxCalendarEvent row, Activity activity)
        => !row.IsDeleted
           && !ShouldSeed(row.TaxFormCode, activity)
           && CanRetract(row.Status, row.FiledDate, row.FilingReference, row.TaxAmount, row.Notes);
}
