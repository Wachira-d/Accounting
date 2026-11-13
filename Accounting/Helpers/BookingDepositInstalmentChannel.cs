namespace Accounting.Helpers;

/// <summary>
/// รอบ 268 (คำตัดสิน 268-1-3 · แทน "บันทึกไว้ ไม่แก้" ของ 260-2-7 L5) — <b>มัดจำการจองนัดหมายที่รับหลายงวด: เงินแต่ละงวดต้องลงผังของช่องทางที่งวดนั้นเข้าจริง</b> ·
/// pure (ผู้เรียกโหลดแถวรับเงินของการจองมาให้)
/// <para>═══ ที่มา (บั๊กจริง) ═══ ใบเสร็จมัดจำของการจองเป็นใบเดียวทั้งยอดมัดจำ (ร่างรอเงิน) อนุมัติเมื่อรับครบ ⇒ แหล่งเงินของใบ = ช่องทางของ<b>งวดสุดท้าย</b>
/// ⇒ งวดแรกโอนเข้าธนาคาร A งวดสองเงินสด = JE ลงเงินสดทั้งก้อน (ธนาคาร A ในสมุดไม่ขยับ · กระทบยอดหา JE ไม่เจอ)</para>
/// <para>═══ กติกา ═══
/// <list type="number">
/// <item>ช่องทางของแต่ละงวด<b>ประทับบนแถวรับเงิน</b>ตอนรับ (<c>SiteBookingPayment.ChannelStamped</c> + <c>BankAccountId</c>/<c>MoneyInAccountId</c>) — ไม่เดาย้อนหลัง</item>
/// <item>ใบเสร็จมัดจำลงตามช่องทางของงวดสุดท้าย (เส้นเดิม · ใบเดียว · เลขเดียว) แล้ว<b>ย้ายเงินของงวดก่อนหน้าที่ช่องทางต่างกัน</b>ไปผังของงวดนั้น
/// ด้วย JE ผูกเอกสาร (Dr ผังของงวดนั้น / Cr ผังของใบ) — หนึ่ง JE ต่องวด · กุญแจกันซ้ำ <see cref="ReferenceFor"/></item>
/// <item>งวดที่ไม่ได้ประทับ (บันทึกก่อนรอบ 268) = <b>ไม่รู้</b> ⇒ ไม่ย้าย + หมายเหตุให้คนตรวจ (F2 ข้อ 3)</item>
/// <item>งวดที่ผังเดียวกับใบ (เช่นสองธนาคารผูกผังเดียวกัน) ⇒ ไม่มี JE (ผู้เรียกเทียบผังหลังแปลงแล้ว)</item>
/// </list></para>
/// ที่พักไม่ใช่คลาสนี้ — ออกใบเสร็จมัดจำ<b>ต่องวด</b>อยู่แล้ว (<c>LodgingService.ConfirmCoreAsync</c> → <c>CreateDepositReceiptAsync</c> ทุกครั้งที่เงินเข้า)
/// </summary>
public static class BookingDepositInstalmentChannel
{
    public const string RuleCode = "BOOKING-DEPOSIT-INSTALMENT-CHANNEL";
    public const string LegalReference = "พ.ร.บ.การบัญชี 2543 ม.8 (บันทึกตามเอกสารประกอบ) · TFRS for NPAEs (เงินสดและรายการเทียบเท่าเงินสด)";

    /// <summary>แถวรับเงินหนึ่งงวด (ไม่รวมคืนเงิน · เฉพาะที่รับแล้ว)</summary>
    public sealed record Instalment(Guid PaymentId, decimal Amount, bool Stamped, Guid? BankAccountId, Guid? MoneyInAccountId, DateTime? PaidAt = null);

    /// <summary>งวดที่ต้องย้ายเงินออกจากผังของใบไปผังของช่องทางงวดนั้น</summary>
    public sealed record Move(Guid PaymentId, decimal Amount, Guid? BankAccountId, Guid? MoneyInAccountId, DateTime? PaidAt = null);

    public sealed record Plan(IReadOnlyList<Move> Moves, int UnknownCount);

    /// <param name="docBank">แหล่งเงินบนใบเสร็จมัดจำ (หลังตั้งตามงวดสุดท้าย)</param>
    /// <param name="docMoneyIn">ผังตรงบนใบ (ผังตรงชนะธนาคาร — <c>DocumentMoneyChannel.Pick</c>)</param>
    public static Plan For(IReadOnlyList<Instalment> rows, Guid finalPaymentId, Guid? docBank, Guid? docMoneyIn)
    {
        var moves = new List<Move>();
        var unknown = 0;
        var docChannel = Norm(docBank, docMoneyIn);
        foreach (var r in rows)
        {
            if (r.PaymentId == finalPaymentId || r.Amount <= 0m) continue;
            if (!r.Stamped) { unknown++; continue; }
            if (Norm(r.BankAccountId, r.MoneyInAccountId) == docChannel) continue;
            moves.Add(new Move(r.PaymentId, r.Amount, r.MoneyInAccountId == null ? r.BankAccountId : null, r.MoneyInAccountId, r.PaidAt));
        }
        return new Plan(moves, unknown);
    }

    /// <summary>ฝ่ายค้าน 268 M3 (268-1-10): รู้ช่องทางของงวดนี้ไหม — มีผังตรง/ธนาคาร · หรือพนักงานบันทึกเงินสดเอง (ไม่ใช่สลิป · ไม่ใช่เงินออนไลน์) ·
    /// สลิป/เงินออนไลน์ที่ไม่มีบัญชี = ไม่รู้ (ห้ามแต่งเป็นเงินสด)</summary>
    public static bool ChannelKnown(Guid? moneyInAccountId, Guid? bankAccountId, bool fromOnlinePayment, bool isSlipMoney)
        => moneyInAccountId != null || bankAccountId != null || (!fromOnlinePayment && !isSlipMoney);

    /// <summary>ฝ่ายค้าน 268 M5 (268-1-11): วันที่ของ JE ย้ายเงิน — วันที่เงินงวดนั้นเข้าเมื่องวดบัญชีเปิด · ไม่อย่างนั้นวันที่ใบ</summary>
    public static DateTime EntryDate(DateTime? paidAt, DateTime documentDate, bool paidPeriodOpen)
        => paidAt is DateTime p && paidPeriodOpen ? p : documentDate;

    /// <summary>ช่องทางเทียบได้ (ผังตรงชนะธนาคาร — ลำดับเดียวกับตัวลงบัญชี)</summary>
    private static (Guid? Bank, Guid? MoneyIn) Norm(Guid? bank, Guid? moneyIn) => moneyIn != null ? (null, moneyIn) : (bank, null);

    /// <summary>กุญแจของ JE ย้ายเงินต่องวด (Reference ของ JE · ผูก SourceDocument = ใบเสร็จมัดจำ) — ตรวจก่อนเขียนเสมอ</summary>
    public static string ReferenceFor(Guid paymentId) => "BKDEP-" + paymentId.ToString("N");

    public static string UnknownNote(int count)
        => $"มีเงินมัดจำ {count} งวดที่บันทึกก่อนรอบ 268 (ระบบไม่ได้จดว่าเข้าบัญชีไหน) — ใบเสร็จมัดจำลงตามช่องทางของงวดสุดท้ายทั้งก้อน · "
           + "ถ้างวดนั้นเข้าบัญชีอื่น ให้บันทึกสมุดรายวันย้ายเงินเองที่หน้า “สมุดรายวัน”";

    public static string MovedNote(string documentNumber, decimal amount, string toAccount, DateTime? paidAt = null, DateTime? entryDate = null)
        => $"[{RuleCode}] ย้ายเงินมัดจำงวดก่อน {amount:N2} บาท ของ {documentNumber} ไปผัง {toAccount} (ช่องทางที่งวดนั้นเข้าจริง)"
           + (paidAt is DateTime p && entryDate is DateTime e && ThaiDate.CalendarDateUtc(p) != ThaiDate.CalendarDateUtc(e)
               ? $" · เงินเข้าจริง {ThaiDate.CalendarDateUtc(p):dd/MM/yyyy} (งวดบัญชีปิด — ลงวันที่ใบ {ThaiDate.CalendarDateUtc(e):dd/MM/yyyy})" : "");
}
