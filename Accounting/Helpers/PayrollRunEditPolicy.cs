namespace Accounting.Helpers;

/// <summary>
/// กติกาเดียวของระบบว่า "รอบเงินเดือนนี้แก้ยอดได้ไหม / กลับรายการจ่ายได้ไหม"
///
/// ═══ ทำไมต้องมีตัวกลาง ═══
/// เดิมเงื่อนไข <c>Status != "Calculated" &amp;&amp; Status != "Approved"</c> ถูกเขียน
/// ซ้ำ 2 ที่ใน <c>PayrollService</c> (แก้ยอดรายคน / แก้แหล่งจ่าย) และ **เขียน
/// ใหม่อีกชุดฝั่ง JS** (<c>payEditable</c> ใน payroll.html) — สำเนามือ 3 ชุดที่
/// ไม่มีใครทำให้ตรงกัน = defect class "รายการที่คัดลอกมาด้วยมือ = drift แน่นอน
/// แค่รอเวลา" (กฎเหล็ก #4 A) ที่เรพนี้เจอซ้ำที่สุด
///
/// ที่แย่กว่า drift คือ **ฝั่ง JS ซ่อนปุ่มเฉย ๆ โดยไม่บอกเหตุผล** — ผู้ใช้เปิด
/// รอบที่จ่ายแล้วจะเห็นตารางที่ "แก้อะไรไม่ได้เลย" โดยไม่มีคำอธิบายและไม่มีทาง
/// ไปต่อ (defect class "ห้าม silent no-op" — ถ้าเซิร์ฟเวอร์ไม่รับ ต้องล็อกช่อง
/// **พร้อมบอกเหตุผลและทางแก้**) ฟังก์ชันในไฟล์นี้จึงคืน "เหตุผลเป็นภาษาไทยที่
/// เอาไปโชว์ได้เลย" ไม่ใช่แค่ bool แล้วให้แต่ละที่ไปแต่งข้อความเอง
/// </summary>
public static class PayrollRunEditPolicy
{
    public const string Draft = "Draft";
    public const string Calculated = "Calculated";
    public const string Approved = "Approved";
    public const string Paid = "Paid";
    public const string Voided = "Voided";

    /// <summary>แก้ยอด/แหล่งจ่ายรายคนได้ไหม — ได้เฉพาะรอบที่ยังไม่ลง GL.
    /// คืน (false, เหตุผล) พร้อม**ทางแก้** เสมอ ห้ามคืนเหตุผลว่าง</summary>
    public static (bool Can, string? Reason) CanEditAmounts(string? status) => status switch
    {
        Calculated or Approved => (true, null),
        Draft => (false, "รอบนี้ยังไม่ได้คำนวณ — กด \"คำนวณ\" ก่อนจึงจะมียอดรายคนให้แก้"),
        Paid => (false, "รอบนี้จ่ายและลงบัญชีไปแล้ว — กด \"กลับรายการจ่าย\" เพื่อกลับรายการ "
                        + "JE แล้วแก้ยอด จากนั้นกด \"จ่าย\" ใหม่"),
        Voided => (false, "รอบนี้ถูกยกเลิกแล้ว — แก้ไม่ได้ ต้องสร้างรอบใหม่"),
        _ => (false, $"สถานะ \"{status}\" ไม่รองรับการแก้ยอด"),
    };

    /// <summary>กลับรายการจ่าย (Paid → Approved) ได้ไหม.
    ///
    /// ⚠️ ตรวจได้แค่สิ่งที่อยู่บนตัว run — **งวดบัญชีปิดหรือยัง** ตรวจที่
    /// <c>PayrollService.ReopenPaidRunAsync</c> เพราะต้อง query FiscalPeriods
    /// (ใส่ตรงนี้จะกลายเป็น N+1 บนหน้ารายการรอบ). ฝั่ง UI จึงอาจโชว์ปุ่มแล้ว
    /// เซิร์ฟเวอร์ปฏิเสธพร้อมเหตุผล — ยอมได้ เพราะข้อความบอกทางแก้ชัด</summary>
    /// <summary>ยกเลิกทั้งรอบ (→ Voided) ได้ไหม — กติกา สปส. **เดียวกับ CanReopen**:
    /// นำส่งแล้ว = มี JE ก้อนที่สอง (Dr 21815 / Cr Bank) ที่ Void ไม่แตะ ⇒ ถ้ายอมให้ยกเลิก
    /// จะกลับแค่ JE จ่าย เหลือ 21815 **ติดลบ** ถาวร + แถวนำส่งยังบอกว่านำส่งแล้ว ⇒ รอบใหม่
    /// นำส่งซ้ำงวด. เดิม VoidPayrollAsync ตรวจแค่ "Voided ซ้ำ" — ด่านครอบทางเดียว
    /// (ERP_REVIEW_2026-09-05 H-01)</summary>
    public static (bool Can, string? Reason) CanVoid(string? status, DateTime? ssoSettledAt)
    {
        if (status == Voided)
            return (false, "รอบจ่ายเงินเดือนนี้ถูกยกเลิกแล้ว");
        if (ssoSettledAt.HasValue)
            return (false,
                $"รอบนี้นำส่งประกันสังคมไปแล้วเมื่อ {ssoSettledAt.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)} — "
                + "ต้องกลับรายการนำส่ง สปส. ก่อน (และถ้ายื่น สปส.1-10 ไปแล้วต้องยื่นแก้ไขด้วย) "
                + "จึงจะยกเลิกรอบได้");
        return (true, null);
    }

    public static (bool Can, string? Reason) CanReopen(string? status, DateTime? ssoSettledAt)
    {
        if (status != Paid)
            return (false, status switch
            {
                Draft or Calculated or Approved => "รอบนี้ยังไม่ได้จ่าย — แก้ยอดได้เลยโดยไม่ต้องกลับรายการ",
                Voided => "รอบนี้ถูกยกเลิกไปแล้ว — กลับรายการไม่ได้ ต้องสร้างรอบใหม่",
                _ => $"สถานะ \"{status}\" กลับรายการจ่ายไม่ได้",
            });

        // นำส่ง สปส. แล้ว = มี JE ก้อนที่สอง (Dr 21815 / Cr Bank) + เลขรับจาก
        // สปส. บนกระดาษ. กลับรายการจ่ายโดยไม่แตะ JE ก้อนนั้น จะเหลือหนี้สิน
        // 21815 ที่ถูกล้างไปแล้วทั้งที่ต้นทางหายไป → งบไม่ตรง และยอดที่ยื่นจริง
        // กับยอดในระบบต่างกันโดยไม่มีใครรู้
        if (ssoSettledAt.HasValue)
            // InvariantCulture: ถ้าเครื่อง/คอนเทนเนอร์ตั้ง culture เป็น th-TH
            // ปฏิทินเริ่มต้นคือพุทธศักราช ⇒ "15/09/2026" กลายเป็น "15/09/2569"
            // เงียบ ๆ. ในระบบเก็บ/แสดง ค.ศ. — พ.ศ. ใช้เฉพาะแบบยื่นภาษีเท่านั้น
            return (false,
                $"รอบนี้นำส่งประกันสังคมไปแล้วเมื่อ {ssoSettledAt.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)} — "
                + "ต้องกลับรายการนำส่ง สปส. ก่อน (และถ้ายื่น สปส.1-10 ไปแล้วต้องยื่นแก้ไขด้วย) "
                + "จึงจะกลับรายการจ่ายได้");

        return (true, null);
    }
}
