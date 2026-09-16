namespace Accounting.Helpers;

/// <summary>
/// "รอบเงินเดือนไหนนับเข้าอะไร" — **ตัวตัดสินตัวเดียว** ของ ภ.ง.ด.1 / สปส.1-10
///
/// <para><b>ที่มา</b>: เรพนี้เคยมีตัวกรองสถานะ <b>3 ชุด</b> ที่ต่างกัน —
/// หน้านำส่งภาษี + ปฏิทินยื่น (<c>== "Paid"</c>) · ไฟล์ยื่น ภ.ง.ด.1/สปส.1-10
/// (<c>!= "Draft" &amp;&amp; != "Voided"</c> ⇒ รวม <b>Calculated</b> ที่ยังไม่มีใครอนุมัติ) ·
/// รายงานบนจอ (<c>!= "Voided"</c> ⇒ รวม <b>Draft</b>) ⇒ ผู้ใช้ดาวน์โหลดไฟล์ไปยื่น
/// ด้วยยอดที่ยังไม่ผ่านการอนุมัติได้ และรอบที่อนุมัติแล้วแต่ยังไม่จ่าย มียอดอยู่ในไฟล์
/// แต่หน้านำส่งบอกว่า "ยังไม่ได้รันเงินเดือนงวดนี้"</para>
///
/// <para><b>สองคำถามนี้ต่างกันจริง — ห้ามยุบเป็นชุดเดียว</b> (เหมือน
/// "ยอดต้องนำส่ง" vs "ยอดที่จะลงไฟล์" ของ ภ.ง.ด.3/53):
/// <list type="bullet">
/// <item><b>ยอดที่ประกาศในแบบ</b> — ต้องเป็นตัวเลขที่ผ่านการอนุมัติแล้ว
///   (<c>Approved</c> ขึ้นไป) เพราะ HR เตรียมไฟล์ก่อนวันจ่ายจริงเป็นเรื่องปกติ</item>
/// <item><b>หนี้ที่จ่ายได้/ลง JE ได้</b> — ต้อง <c>Paid</c> เท่านั้น เพราะ JE ที่ตั้ง
///   หนี้ 21815/21914 เกิดตอนจ่าย ถ้าหน้านำส่งโชว์ยอดของรอบที่ยังไม่จ่าย การกด
///   นำส่งจะ <c>Dr 21815</c> ที่ยังไม่มียอด</item>
/// </list>
/// สิ่งที่ต้องทำคือทำให้ความต่าง <b>มีชื่อและมองเห็นได้</b> ไม่ใช่ทำให้เท่ากัน</para>
/// </summary>
public static class PayrollRunFilingScope
{
    public const string Draft = "Draft";
    public const string Calculated = "Calculated";
    public const string Approved = "Approved";
    public const string Paid = "Paid";
    public const string Voided = "Voided";

    /// <summary>รอบที่ตัวเลข "นิ่งพอจะประกาศในแบบยื่น" — ใช้กับไฟล์ ภ.ง.ด.1 /
    /// สปส.1-10 / รายงานที่เอาไปยื่น. <b>ไม่รวม</b> Draft และ Calculated เพราะ
    /// ยังไม่มีใครอนุมัติ (ยอดเปลี่ยนได้) และไฟล์ที่อัปโหลดเข้าเว็บราชการแล้ว
    /// แก้ย้อนหลังไม่ได้</summary>
    public static readonly string[] FilingStatuses = { Approved, Paid };

    /// <summary>รอบที่ "จ่ายเงินแล้ว" — มี JE ตั้งหนี้ 21815/21914 อยู่จริง
    /// จึงนำส่ง/ล้างหนี้ได้</summary>
    public static readonly string[] RemittableStatuses = { Paid };

    public static bool CountsTowardFiling(string? status)
        => status != null && FilingStatuses.Contains(status);

    public static bool CanRemit(string? status)
        => status != null && RemittableStatuses.Contains(status);

    /// <summary>ข้อความอธิบายว่าทำไมงวดนี้ยังไม่มียอดให้นำส่ง — ต้องบอก
    /// <b>ทางไปต่อ</b> เสมอ ไม่ใช่แค่ "ไม่ทราบ" (กฎ CLAUDE.md: ด่านที่ปฏิเสธ
    /// ต้องตอบให้ได้ว่า "แล้วผู้ใช้ทำอะไรได้แทน")</summary>
    public static string PendingReason(bool hasApprovedNotPaidRun)
        => hasApprovedNotPaidRun
            ? "รอบเงินเดือนงวดนี้อนุมัติแล้วแต่ยังไม่ได้บันทึกการจ่าย — "
              + "ยอดจะมาขึ้นที่นี่เมื่อกด “จ่ายเงินเดือน” (ไฟล์ยื่นดาวน์โหลดได้แล้ว)"
            : "ยังไม่ได้รันเงินเดือนงวดนี้ — เงินสมทบ/ภาษียังคำนวณไม่ได้";
}
