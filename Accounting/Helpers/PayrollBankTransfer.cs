namespace Accounting.Helpers;

/// <summary>ผลตัดสินหนึ่งแถวของไฟล์รายการโอนเงินเดือน</summary>
/// <param name="TransferAmount">ยอดที่ต้องโอนเข้าบัญชีพนักงาน</param>
/// <param name="AccountDigits">เลขบัญชีเฉพาะตัวเลข (ตัดขีด/ช่องว่าง — ไฟล์ธนาคารรับเลขล้วน)</param>
/// <param name="Ready">พร้อมโอน (แหล่งจ่ายเป็นบัญชีธนาคาร + มีเลขบัญชี + ยอด &gt; 0)</param>
/// <param name="Status">ป้ายสถานะภาษาไทย</param>
/// <param name="Note">หมายเหตุ/ทางไปต่อ (null = ไม่มี)</param>
public sealed record PayrollBankTransferRow(decimal TransferAmount, string AccountDigits, bool Ready, string Status, string? Note);

/// <summary>
/// **ตัวตัดสินแถวไฟล์รายการโอนเงินเดือน** (X4 · 2026-10-14) — pure · ไม่มี I/O
///
/// <para>═══ ยอดโอน ═══ = <c>NetPay − AdvanceRecovered</c> — สูตรเดียวกับเส้น Cr เงินสด/ธนาคารตอนกดจ่าย
/// (<c>PayrollService.ProcessPaymentAsync</c>: <c>net = d.NetPay - d.AdvanceRecovered</c>) ⇒ ผลรวมไฟล์ = ยอดเงินออกจากบัญชีใน JE ·
/// <c>AdvanceRecovered</c> ถูกประทับตอนกดจ่ายเท่านั้น ⇒ รอบที่ยังไม่จ่ายใช้ <c>NetPay</c> และ<b>บอกตรง ๆ</b> เมื่อพนักงานมีเงินทดรองค้าง
/// (ยอดจริงจะลดลงตอนกดจ่าย) — ไม่คำนวณยอดหักคืนสำเนาที่สอง (หลักการ #4 ตัวตั้งตัวเดียว)</para>
///
/// <para>═══ ไม่แต่งค่า ═══ ขาดเลขบัญชี/ยอด ≤ 0 = แถวยังอยู่ในไฟล์พร้อมสถานะ "ไม่พร้อมโอน" (ไม่ตัดทิ้งเงียบ ๆ ·
/// ผลรวมแถวพร้อมโอนกับแถวไม่พร้อมแยกให้เห็น) · <b>ไม่มีรูปแบบไฟล์เฉพาะธนาคาร</b> (KBank/SCB/BBL bulk payroll) —
/// ต้องมีตัวอย่างไฟล์/สเปกจากธนาคารที่ลูกค้าใช้ก่อน (ห้ามเดารูปแบบ — ไฟล์ผิดรูปแบบ = เงินเดือนเข้าผิดคน/ตีกลับทั้งชุด)</para>
/// </summary>
public static class PayrollBankTransfer
{
    /// <summary>เลขบัญชีธนาคารไทยสั้นสุดที่พบทั่วไป (ธนาคารพาณิชย์ 10 หลัก · ออมสิน/ธ.ก.ส. 12 หลัก)</summary>
    public const int MinAccountDigits = 10;

    /// <summary>ป้ายสถานะของแถวที่จ่ายจากแหล่งอื่นที่ไม่ใช่ธนาคาร</summary>
    public const string NotFromBankStatus = "จ่ายจากแหล่งอื่น";

    /// <summary>สถานะรอบที่ส่งออกไฟล์โอนได้ — ต้องคำนวณแล้ว (Draft ยังไม่มียอด · Voided ยกเลิกแล้ว)</summary>
    public static string? RefuseReason(string? runStatus) => runStatus switch
    {
        "Calculated" or "Approved" or "Paid" => null,
        "Voided" => "รอบนี้ถูกยกเลิกแล้ว — ไม่มีรายการโอน",
        _ => "รอบนี้ยังไม่ได้คำนวณ — กด \"คำนวณ\" ก่อนแล้วจึงส่งออกรายการโอน",
    };

    /// <summary>เงินที่ออกจากบัญชีจริงของพนักงานคนนี้ — รอบจ่ายแล้ว = สุทธิ − หักคืนเงินทดรอง (ประทับตอนจ่าย) · ยังไม่จ่าย = สุทธิ</summary>
    public static decimal CashPaid(string? runStatus, decimal netPay, decimal advanceRecovered)
        => Math.Round(runStatus == "Paid" ? netPay - advanceRecovered : netPay, 2, MidpointRounding.AwayFromZero);

    /// <param name="paymentSourceIsBank">แหล่งจ่ายเงินสุทธิของคนนี้เป็นบัญชีธนาคารไหม (ฝ่ายค้านรอบส่งออก 2026-10-15 P2-4) — false = จ่ายจาก
    /// เงินสด/เงินสดย่อย/ช่องทางอื่น ⇒ <b>ไม่พร้อมโอน</b> (อัปโหลดแถวนี้ = จ่ายซ้ำ: ระบบลงบัญชีว่าจ่ายเงินสดแล้ว + ธนาคารโอนอีกรอบ) ·
    /// null = ยังไม่มีแหล่งจ่ายที่ใช้ได้ ⇒ ไม่พร้อมโอนจนกว่าจะเลือก</param>
    public static PayrollBankTransferRow Evaluate(string runStatus, decimal netPay, decimal advanceRecovered,
        bool hasOutstandingAdvance, string? bankAccountNumber, bool? paymentSourceIsBank)
    {
        var isPaid = runStatus == "Paid";
        var amount = CashPaid(runStatus, netPay, advanceRecovered);
        var digits = new string((bankAccountNumber ?? "").Where(char.IsDigit).ToArray());
        string? note = !isPaid && hasOutstandingAdvance
            ? "รอบยังไม่กดจ่าย และพนักงานมีเงินทดรองค้าง — ยอดโอนจริงจะถูกหักคืนตอนกดจ่าย ให้ส่งออกใหม่หลังกดจ่าย"
            : null;
        if (amount <= 0)
            return new(amount, digits, false, "ไม่ต้องโอน", isPaid && advanceRecovered > 0
                ? "ยอดสุทธิถูกหักคืนเงินทดรองหมดแล้ว" : note ?? "ยอดสุทธิเป็นศูนย์หรือติดลบ");
        if (paymentSourceIsBank == null)
            return new(amount, digits, false, "ยังไม่ได้เลือกแหล่งจ่าย",
                "แหล่งจ่ายเงินสุทธิของคนนี้ใช้ไม่ได้/ยังไม่ได้ตั้ง — เลือกบัญชีธนาคารที่ช่อง \"แหล่งจ่าย\" ของรอบก่อนส่งออก" + (note != null ? " · " + note : ""));
        if (paymentSourceIsBank == false)
            return new(amount, digits, false, NotFromBankStatus,
                "แหล่งจ่ายไม่ใช่บัญชีธนาคาร (เงินสด/เงินสดย่อย/ช่องทางอื่น) — ห้ามอัปโหลดแถวนี้ให้ธนาคาร (จ่ายซ้ำ) · "
                + "ถ้าจะโอนจริงให้เปลี่ยนแหล่งจ่ายเป็นบัญชีธนาคารก่อนกดจ่าย" + (note != null ? " · " + note : ""));
        if (digits.Length == 0)
            return new(amount, digits, false, "ขาดเลขบัญชี", "เพิ่มธนาคาร/เลขบัญชีที่ข้อมูลพนักงานก่อนโอน" + (note != null ? " · " + note : ""));
        if (digits.Length < MinAccountDigits)
            return new(amount, digits, false, "เลขบัญชีผิดรูปแบบ",
                $"เลขบัญชีมี {digits.Length} หลัก (ปกติ {MinAccountDigits}–12 หลัก) — ตรวจที่ข้อมูลพนักงาน" + (note != null ? " · " + note : ""));
        return new(amount, digits, true, isPaid ? "พร้อมโอน" : "พร้อมโอน (ก่อนกดจ่าย)", note);
    }
}
