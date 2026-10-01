namespace Accounting.Helpers;

/// <summary>
/// ตารางวันหยุดราชการของแพลตฟอร์มพร้อมให้กรอกหรือยัง — <b>ตัวตัดสินตัวเดียว</b> (รอบ 201 ทีม PL · ฝ่ายค้าน PL-B1 · คำตัดสินข้อ 106)
///
/// <para>═══ ที่มา ═══ B-9 ต่อสายผู้อ่านกำหนดยื่นบางเส้น (ปฏิทินภาษี · หน้านำส่ง · ปฏิทิน compliance · ตัวตรวจก่อนยื่น · tax point ของมัดจำที่ริบ)
/// แต่ยังมีผู้อ่านที่คิด "วันทำการ/กำหนดส่ง" จากเสาร์-อาทิตย์อย่างเดียว ⇒ ถ้าแอดมินกรอกวันหยุดตอนนี้ งวดเดียวกันจะได้กำหนดสองวันในสองหน้า
/// (ปฏิทินบอกเลื่อนแล้ว · เงินเพิ่ม สปส. คิดตามวันเดิม) = ตัวเลขที่ขัดกันเอง ซึ่งแย่กว่าตารางว่าง (ตารางว่าง = เตือนเร็วไปเท่ากันทุกหน้า · ทิศที่มองเห็น)</para>
///
/// <para>กติกา: ยังมีผู้อ่านค้าง ⇒ <b>ปิดการเพิ่ม</b>วันหยุด พร้อมเหตุผลที่ระบุชื่อผู้อ่าน (ลบได้ตามเดิม — ลดความขัดกันเท่านั้น) · ต่อสายครบแล้ว
/// ⇒ เอาออกจาก <see cref="PendingReaders"/> ที่เดียว</para>
/// </summary>
public static class PlatformHolidayReadiness
{
    /// <summary>ผู้อ่านวันทำการ/กำหนดส่งที่ยังไม่รับชุดวันหยุด — ตัดออกเมื่อต่อสายแล้วเท่านั้น</summary>
    public static readonly IReadOnlyList<string> PendingReaders = new[]
    {
        "กำหนดนำส่ง สปส.1-10 + เงินเพิ่ม 2% (Helpers/SsoLateFee ← PayrollService/PayrollController — ไฟล์ทีมเงินเดือน)",
        "นับ 3 วันทำการ §87 ของรอบโอนเงินแพลตฟอร์ม (Helpers/SettlementPosting.WeekdaysAfter — ไฟล์ทีม settlement)",
    };

    /// <summary>เหตุผลที่ปิดการเพิ่มวันหยุด — null = เพิ่มได้</summary>
    public static string? EntryBlockedReason(IReadOnlyCollection<string> pendingReaders)
    {
        if (pendingReaders.Count == 0) return null;
        return "ยังเพิ่มวันหยุดราชการไม่ได้ — ผู้คำนวณกำหนดส่งต่อไปนี้ยังไม่อ่านตารางนี้ ถ้ากรอกตอนนี้ งวดเดียวกันจะได้กำหนดคนละวันในแต่ละหน้า: "
               + string.Join(" · ", pendingReaders)
               + " — ระหว่างนี้ทุกหน้าเลื่อนเฉพาะเสาร์/อาทิตย์เท่ากัน (เตือนเร็วกว่ากำหนดจริงในบางงวด ไม่เตือนช้า)";
    }

    /// <summary>เหตุผลของสถานะปัจจุบันของระบบ</summary>
    public static string? EntryBlockedReason() => EntryBlockedReason((IReadOnlyCollection<string>)PendingReaders);
}
