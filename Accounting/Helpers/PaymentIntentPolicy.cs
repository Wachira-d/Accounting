using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กติกาการเปลี่ยนสถานะของ <c>PaymentIntent</c> — **ฟังก์ชันบริสุทธิ์ที่เดียวของระบบ**
///
/// ═══ ทำไมต้องแยกออกมา ═══
/// สถานะการจ่ายเงินถูกเปลี่ยนจาก <b>4 ทาง</b> ที่ไม่เห็นกัน (webhook · job กระทบยอด ·
/// คนกดยืนยัน · หน้าเว็บที่ poll) · ถ้าแต่ละทางเขียนเงื่อนไขเอง จะได้กติกา 4 ชุดที่
/// ขัดกันเองในเคสที่หายาก — เช่น webhook มาช้ากว่า poll แล้วเขียนทับสถานะที่ถูกต้องแล้ว
/// หรือคนกด "ยืนยันด้วยมือ" ทับรายการที่ provider บอกว่า Failed
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item><b>สถานะปลายทางเป็นสถานะสุดท้าย</b> — <c>Succeeded</c> · <c>Refunded</c> ห้ามถอย
///   (เงินเข้าแล้วเข้าเลย) · <c>Failed</c>/<c>Expired</c> ถอยกลับไม่ได้ แต่ **เดินหน้าไป
///   Succeeded ได้** เพราะ provider บางเจ้าตัดสิน timeout ก่อนแล้วเงินเข้าทีหลังจริง ๆ
///   (ถ้าห้ามไว้ ลูกค้าจ่ายแล้วระบบไม่รับ = เคสร้องเรียนที่แก้ยากที่สุด)</item>
/// <item><b>ซ้ำ = no-op ไม่ใช่ error</b> — webhook ส่งซ้ำเป็นเรื่องปกติของทุกเจ้า
///   การ throw จะทำให้ provider retry ไม่รู้จบ</item>
/// <item><b>ถอยหลังต้องเงียบและถูกบันทึก</b> — ไม่ throw (ผู้ส่งไม่ผิด) แต่ต้องรู้ว่าเกิดขึ้น</item>
/// </list>
/// </summary>
public static class PaymentIntentPolicy
{
    /// <summary>ผลการตัดสินว่าจะรับการเปลี่ยนสถานะนี้ไหม</summary>
    public readonly record struct Decision(bool Apply, bool IsDuplicate, string? Reason)
    {
        public static Decision Accept() => new(true, false, null);
        public static Decision Duplicate() => new(false, true, null);
        public static Decision Reject(string reason) => new(false, false, reason);
    }

    /// <summary>สถานะที่ "จบแล้วในทางบวก" — เงินอยู่กับเราแล้ว</summary>
    public static bool IsSettledPositive(PaymentIntentStatus s)
        => s is PaymentIntentStatus.Succeeded
            or PaymentIntentStatus.Refunded
            or PaymentIntentStatus.PartiallyRefunded;

    /// <summary>ยังรอผลอยู่ — job กระทบยอดจะไล่ถามสถานะสดเฉพาะกลุ่มนี้</summary>
    public static bool IsOpen(PaymentIntentStatus s)
        => s is PaymentIntentStatus.Created or PaymentIntentStatus.Pending;

    public static Decision Evaluate(PaymentIntentStatus from, PaymentIntentStatus to)
    {
        if (from == to) return Decision.Duplicate();

        switch (from)
        {
            // เงินเข้าแล้ว: ไปได้แค่ทางคืนเงิน
            case PaymentIntentStatus.Succeeded:
                return to is PaymentIntentStatus.Refunded or PaymentIntentStatus.PartiallyRefunded
                    ? Decision.Accept()
                    : Decision.Reject("รายการที่ชำระสำเร็จแล้วเปลี่ยนกลับไม่ได้ — ถ้าต้องยกเลิกให้บันทึกการคืนเงิน");

            case PaymentIntentStatus.PartiallyRefunded:
                return to == PaymentIntentStatus.Refunded
                    ? Decision.Accept()
                    : Decision.Reject("คืนเงินบางส่วนแล้ว เปลี่ยนได้เฉพาะเป็นคืนเงินเต็มจำนวน");

            case PaymentIntentStatus.Refunded:
                return Decision.Reject("รายการที่คืนเงินครบแล้วเปลี่ยนสถานะไม่ได้");

            // ล้มเหลว/หมดอายุ: เดินหน้าไป Succeeded ได้ (เงินเข้าช้ากว่าที่ provider ตัดสิน)
            // แต่สลับไปมาระหว่างกันเองไม่ได้ — ไม่มีความหมายและปิดบังของจริง
            case PaymentIntentStatus.Failed:
            case PaymentIntentStatus.Expired:
                return to == PaymentIntentStatus.Succeeded
                    ? Decision.Accept()
                    : Decision.Reject("รายการที่ปิดไปแล้วเปลี่ยนได้เฉพาะเมื่อเงินเข้าจริงภายหลัง");

            // ยังเปิดอยู่: ไปได้ทุกสถานะปลายทาง แต่ห้ามถอยจาก Pending กลับ Created
            case PaymentIntentStatus.Pending:
                return to == PaymentIntentStatus.Created
                    ? Decision.Reject("ถอยกลับไปสถานะเริ่มต้นไม่ได้")
                    : Decision.Accept();

            case PaymentIntentStatus.Created:
            default:
                return Decision.Accept();
        }
    }

    /// <summary>คีย์กันสร้าง intent ซ้ำสำหรับการจ่ายครั้งเดียวกัน
    ///
    /// <para><paramref name="sequence"/> เพิ่มเมื่อครั้งก่อน **ปิดไปแล้วโดยไม่สำเร็จ**
    /// (QR หมดอายุ/บัตรถูกปฏิเสธ) — ลูกค้าต้องลองใหม่ได้ · ถ้าใช้คีย์เดิมจะติดที่ unique
    /// index แล้วกดจ่ายซ้ำไม่ได้เลย ซึ่งแย่กว่าปัญหาที่ตั้งใจกัน</para>
    ///
    /// <para>จำนวนเงินอยู่ในคีย์ด้วย เพราะยอดที่เปลี่ยน (เพิ่มรายการในตะกร้า) คือการจ่าย
    /// คนละครั้ง ไม่ใช่ครั้งเดิม</para></summary>
    public static string IdempotencyKey(PaymentSourceKind kind, Guid sourceId, decimal amount, int sequence)
        => $"{kind}:{sourceId:N}:{amount:0.00}:{sequence}";
}
