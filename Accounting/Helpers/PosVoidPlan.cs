namespace Accounting.Helpers;

/// <summary>สิ่งที่การยกเลิกบิล POS 1 ใบต้องทำ — ผลของ <see cref="PosVoidPlan.Decide"/></summary>
/// <param name="RestoreRemainingStock">คืนสต็อก/วัตถุดิบเฉพาะ<b>จำนวนที่ยังไม่ถูกคืนเงิน</b>
/// (<see cref="PosVoidPlan.RemainingQuantity"/>)</param>
/// <param name="ReverseSaleJournal">กลับ JE ขายทั้งใบ</param>
/// <param name="ReverseRefundJournals">กลับ JE คืนเงินทุกใบของบิลนี้ด้วย</param>
/// <param name="BlockedMessage">ไม่ null = ห้ามยกเลิก (ข้อความไทยถึงผู้ใช้ พร้อมทางไปต่อ)</param>
public readonly record struct PosVoidDecision(
    bool RestoreRemainingStock,
    bool ReverseSaleJournal,
    bool ReverseRefundJournals,
    string? BlockedMessage)
{
    public bool Blocked => BlockedMessage != null;
}

/// <summary>
/// **ยกเลิกบิล POS ที่ปิดแล้ว — กลับเฉพาะสิ่งที่ยังค้างอยู่** (ตัวตัดสินตัวเดียวของ
/// <c>PosService.VoidOrderAsync</c>)
///
/// ═══ ที่มา (รอบ 193 · E193-2) ═══
/// เดิม void บิลที่<b>คืนเงินไปบางส่วนแล้ว</b> (สถานะยังเป็น Completed) คืนสต็อก
/// <c>item.Quantity</c> เต็ม และกลับ JE ขายทั้งใบ ทั้งที่การคืนเงินคืนสต็อกส่วนนั้นแล้วและ JE
/// คืนเงินกลับรายได้/ภาษีขาย/ต้นทุนส่วนนั้นไปแล้ว ⇒ <b>สต็อกกลับซ้ำ + GL กลับซ้ำ</b>
///
/// ═══ วิธีที่เลือก ═══
/// <list type="bullet">
/// <item><b>สต็อก</b>: คืนเฉพาะ <c>Quantity − RefundedQuantity</c> ต่อบรรทัด · ต้นทุนเข้าคลัง
///   = <see cref="PosCogsBooking.RestockUnitCost"/> (ต้นทุนที่ขายออกไป) ตัวเดียวกับเส้นคืนเงิน</item>
/// <item><b>GL</b>: กลับ JE ขาย<b>และ</b> JE คืนเงินทุกใบ (<c>ReverseJournalEntryAsync</c> ตรงตัว) ⇒
///   ผลสุทธิของ void = ขาย − คืน = <b>ส่วนที่ยังไม่ถูกคืนพอดีทุกบัญชี</b> (รายได้ · ภาษีขาย ·
///   ค่าบริการ · ทิป · ปัดเศษ · เงินตามวิธีชำระ · COGS) โดยไม่ต้องคิดสัดส่วนใหม่ ·
///   ไม่เลือกการคิด "JE ส่วนที่เหลือ" ด้วย <c>PosRefundMath</c> เพราะสูตรคืนเงินไม่ครอบทิปและเศษปัด
///   (ตั้งใจ — ดู doc ของ PosRefundMath) ⇒ void จะทิ้งทิป/เศษค้างใน GL</item>
/// <item>พบว่ามีการคืนเงินแต่<b>หา JE คืนเงินไม่เจอเลย</b> (บิลก่อนรอบ 184 ที่ JE คืนเงินเคยถูกกลืน
///   ทิ้ง) ⇒ กลับ JE ขายทั้งใบจะกลับส่วนที่คืนไปแล้วซ้ำ ⇒ <b>บล็อก</b> — ทางไปต่อ: คืนเงินรายการที่เหลือ</item>
/// </list>
/// บิลที่ยังไม่ปิด / คืนครบแล้ว (สถานะ Refunded) — ไม่มีอะไรค้างให้กลับ (พฤติกรรมเดิม)
/// </summary>
public static class PosVoidPlan
{
    /// <summary>จำนวนที่ยังไม่ถูกคืนเงินของบรรทัด (ไม่ติดลบ)</summary>
    public static decimal RemainingQuantity(decimal lineQuantity, decimal refundedQuantity)
        => Math.Max(0m, lineQuantity - Math.Max(0m, refundedQuantity));

    /// <param name="wasCompleted">สถานะก่อนยกเลิกเป็น Completed (ปิดบิลแล้ว — มีสต็อก/JE ขาย)</param>
    /// <param name="hasSaleJournal">บิลมี <c>JournalEntryId</c></param>
    /// <param name="anyRefunded">มีบรรทัดใดที่ <c>RefundedQuantity &gt; 0</c></param>
    /// <param name="postedRefundJournalCount">จำนวน JE คืนเงินของบิลนี้ที่ยัง Posted</param>
    public static PosVoidDecision Decide(bool wasCompleted, bool hasSaleJournal, bool anyRefunded,
        int postedRefundJournalCount)
    {
        if (!wasCompleted)
            return new PosVoidDecision(false, false, false, null);

        if (anyRefunded && hasSaleJournal && postedRefundJournalCount == 0)
            return new PosVoidDecision(false, false, false,
                "บิลนี้คืนเงินไปบางส่วนแล้ว แต่ไม่พบรายการบัญชีของการคืนเงิน — ยกเลิกทั้งบิลไม่ได้ "
                + "เพราะจะกลับรายได้/ต้นทุนของส่วนที่คืนไปแล้วซ้ำ · ใช้ \"คืนเงิน\" กับรายการที่เหลือแทน");

        return new PosVoidDecision(
            RestoreRemainingStock: true,
            ReverseSaleJournal: hasSaleJournal,
            ReverseRefundJournals: postedRefundJournalCount > 0,
            BlockedMessage: null);
    }
}
