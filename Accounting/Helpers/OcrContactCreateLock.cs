namespace Accounting.Helpers;

/// <summary>
/// **สร้างผู้ติดต่อจากสแกน OCR ทีละคำขอต่อนิติบุคคล** (pure · รอบ 200 · คำตัดสินเจ้าของข้อ 19 · ฝ่ายค้าน K-5 รอบ 197)
///
/// <para>═══ ที่มา ═══ หน้าสแกนอัปโหลดพร้อมกัน 3 ไฟล์ (<c>_maxParallelUploads: 3</c>) และตาราง <c>Contacts</c> ไม่มี unique index บน
/// (CompanyId, TaxId, BranchCode) ⇒ ใบ Makro สาขา 00005 สองใบที่มาพร้อมกัน ต่างคนต่างเห็นว่า "ยังไม่มีแถวของสาขานี้" แล้วสร้างทั้งคู่
/// (ต้นเหตุเดียวกับผู้ติดต่อซ้ำ Branch 1/2 รุ่นเดิม). คำตัดสิน: ล็อก advisory ต่อ (CompanyId, เลขผู้เสียภาษี) ตอนสร้าง · คีย์คงที่ข้ามเครื่อง
/// (<see cref="AdvisoryLockKey.For(Guid, string, string)"/>) · <b>ไม่</b>เพิ่ม unique index จนกว่าข้อมูลซ้ำเดิมจะถูกจัดการ · แถวซ้ำที่มีอยู่
/// แล้ว <b>รายงาน</b>ในหน้า contact-hygiene ไม่รวมอัตโนมัติ (การรวมย้อนไม่ได้)</para>
///
/// <para>ลำดับที่ผู้เรียกต้องทำ (ในธุรกรรมเดียว): ล็อกคีย์ <see cref="LockPart"/> → ถามคีย์ผู้ติดต่อกลางซ้ำ
/// (<see cref="ContactTaxBranchKey.FindAsync"/> — ตัวเดียวกับทุกทางเข้า) → <see cref="ReuseAfterLock"/> มีค่า = ใช้แถวนั้น (อีกคำขอเพิ่ง
/// สร้าง) · null = สร้างได้ → บันทึก → commit (ปล่อยล็อก)</para>
/// </summary>
public static class OcrContactCreateLock
{
    /// <summary>ส่วนของคีย์ล็อก = เลขผู้เสียภาษีตัวเลขล้วน ("0 10 7 567 00041 4" ≡ "0107567000414" — ต้องเป็นคีย์เดียวกันไม่ว่ากระดาษ
    /// จะแบ่งกลุ่มแบบไหน) · <c>null</c> = ไม่ล็อก: ไม่มีเลข (สร้างจากชื่อ — ไม่มีกุญแจให้ชน) · ศูนย์ล้วน (placeholder "ลูกค้าทั่วไป" —
    /// ไม่ใช่ตัวตน) · สั้นกว่า 10 หลัก (อ่านเพี้ยน)</summary>
    public static string? LockPart(string? taxId)
    {
        var d = ThaiTaxId.Normalize(taxId);
        if (d.Length < 10 || d.TrimStart('0').Length == 0) return null;
        return d;
    }

    /// <summary>หลังได้ล็อกแล้วถามคีย์ผู้ติดต่อซ้ำ — พบแถวของคีย์นี้ (เลขภาษี + สาขา ตามกติกากลาง) = อีกคำขอสร้างไปแล้วระหว่างรอ ⇒ ใช้แถวนั้น
    /// (ห้ามสร้างซ้ำ) · ไม่พบ = สร้างได้ · ผู้เรียกตัดสิน "ต้องสร้าง" มาแล้วก่อนล็อก ดังนั้นแถวที่เจอตอนนี้คือแถวที่<b>เพิ่งเกิด</b></summary>
    public static Guid? ReuseAfterLock(ContactKeyMatch afterLock) => afterLock.ContactId;

    /// <summary>
    /// **เติมเลขผู้เสียภาษีลงผู้ติดต่อแถวเดิมได้ไหม หลังได้ล็อกแล้ว** (รอบ 200 ทีม K2 · ฝ่ายค้าน K R6) — เส้น "ผูกเลขภาษีเข้าแถวเดิม" (backfill ตอนสแกน ·
    /// <c>AdoptTaxId</c> ฝั่งขาย · backfill ฝั่งซื้อตอนสร้างเอกสาร) ก็สร้างคีย์ (เลข+สาขา) ขึ้นมาเหมือนการสร้างแถวใหม่ ⇒ ต้องอยู่ใต้ล็อกเดียวกับ K-5 ·
    /// ถามคีย์ซ้ำแล้วพบแถว<b>อื่น</b>ที่ถือคีย์นี้อยู่แล้ว (อีกคำขอเพิ่งสร้าง หรือมีอยู่ก่อน) = ห้ามเติม (ได้แถวคีย์ซ้ำ) · ไม่พบ/พบแถวตัวเอง = เติมได้
    /// </summary>
    /// <param name="foundAfterLock">ผลของการถามคีย์ซ้ำหลังได้ล็อก (<see cref="ReuseAfterLock"/>)</param>
    /// <param name="adoptingRowId">แถวที่กำลังจะรับเลข</param>
    public static bool MayAdoptAfterLock(Guid? foundAfterLock, Guid adoptingRowId)
        => foundAfterLock == null || foundAfterLock == adoptingRowId;

    /// <summary>ข้อความลง ProcessingNotes เมื่อไม่เติมเลขลงแถวเดิมเพราะมีแถวอื่นถือคีย์นี้แล้ว — ผู้ใช้เห็นว่าทำไมผู้ติดต่อยังไม่มีเลข และไปรวมที่หน้าผู้ติดต่อได้</summary>
    public static string AdoptSkippedNote(string? taxId, string? branchCode, string? rowName)
        => $"[Enrich] ไม่เติมเลขผู้เสียภาษี {ThaiTaxId.Normalize(taxId)} ให้ '{rowName}' — มีผู้ติดต่ออีกแถวถือเลขนี้"
           + $" {TaxBranchCode.Label(branchCode)} อยู่แล้ว (อาจสร้างพร้อมกันจากอีกคำขอ) · ตรวจ/รวมผู้ติดต่อซ้ำที่หน้าผู้ติดต่อ";

    /// <summary>ข้อความลง ProcessingNotes เมื่อใช้แถวที่อีกคำขอเพิ่งสร้าง — ผู้ใช้เห็นว่าทำไมไม่มี "[Auto-Create]" ในใบนี้</summary>
    public static string ReusedNote(string? taxId, string? branchCode)
        => $"[Auto-Create] ไม่สร้างผู้ติดต่อซ้ำ — มีคำขออื่น (อัปโหลดพร้อมกัน) สร้างผู้ติดต่อของเลข {ThaiTaxId.Normalize(taxId)} "
           + $"{TaxBranchCode.Label(branchCode)} ไปแล้วระหว่างรอ จึงผูกกับแถวนั้น";
}
