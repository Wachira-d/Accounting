using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เอกสาร ERP ที่ผูกกับการจอง ณ ตอนลูกค้าขอเปลี่ยนวิธีรับ (ตัดสินจากใบที่ผูกจริง)</summary>
public enum BookingLinkedDocKind
{
    /// <summary>ไม่มีใบ / ใบถูกยกเลิก-ปฏิเสธแล้ว</summary>
    None = 0,
    /// <summary>ใบรับมัดจำร่าง (ยังไม่มีเลข · ไม่ลงบัญชี) — รอเงิน</summary>
    DepositDraft = 1,
    /// <summary>ใบรับมัดจำออกแล้ว</summary>
    DepositIssued = 2,
    /// <summary>ใบเสนอราคา/ใบยืนยันการจอง/ใบกำกับที่รวมค่าจัดส่งแล้ว</summary>
    Other = 3,
}

/// <summary>ข้อเท็จจริงของคำขอเปลี่ยนวิธีรับ/ที่อยู่หนึ่งครั้ง (ยอดใหม่คิดโดยตัวคิดค่าส่งของร้านแล้ว)</summary>
public sealed record BookingFulfillmentChangeFacts(
    BookingStatus Status, DateTime StartUtc, DateTime NowUtc, int? CutoffHours,
    decimal ServiceTotal, decimal OldFee, decimal NewFee, decimal OldDeposit, decimal NewDeposit, decimal PaidAmount,
    bool SlipPending, bool GatewayInProgress, BookingLinkedDocKind LinkedDoc, bool PaidWithCredit, bool PosBillOpen = false);

/// <summary>ผลตัดสิน — <see cref="DepositAmount"/> = มัดจำที่บันทึกหลังเปลี่ยน · <see cref="DiscardDraftDeposit"/> = ลบใบรับมัดจำร่างที่ยอดไม่ตรงแล้ว
/// (ออกใหม่ตอนเงินเข้าผ่านเส้นเดิม) · <see cref="Effect"/> = ข้อความถึงลูกค้า/ร้าน</summary>
public sealed record BookingFulfillmentChangeDecision(bool Allowed, string? Problem, decimal DepositAmount, bool DiscardDraftDeposit, string Effect);

/// <summary>หน้าต่างเปลี่ยนเอง — <see cref="Enabled"/> false = ร้านไม่เปิด/ไม่เกี่ยว (ไม่มีปุ่ม) · <see cref="Open"/> false + <see cref="Note"/> = เลยกำหนดแล้ว</summary>
public sealed record BookingFulfillmentChangeWindow(bool Enabled, bool Open, DateTime? DeadlineUtc, string? Note);

/// <summary>
/// รอบ 228 (BOOKING_PAYMENT_AUDIT §7 "ยังเปิด" · §9) — <b>ตัวตัดสินเดียว</b>ของ "ลูกค้าเปลี่ยนวิธีรับ (จัดส่ง/รับเอง) หรือที่อยู่จัดส่งเองจากหน้าจัดการการจอง"
///
/// <para><b>ค่าตั้ง</b>: <c>SiteBookingSettings.FulfillmentChangeCutoffHours</c> — null = ไม่เปิด (เดิม: ติดต่อร้าน) · N = ได้ถึง N ชม. ก่อนเวลานัด ·
/// ค่าส่งคิดใหม่ที่เซิร์ฟเวอร์ด้วยตัวคิดเดียวกับตอนจอง (<c>CmsBookingService.QuoteFulfillmentAsync</c> → <see cref="ShopDeliveryFee"/>) · client ส่งแค่วิธี/ที่อยู่/อัตรา</para>
///
/// <para><b>คำตัดสิน 228-3 (ส่วนต่างค่าส่ง)</b>:
/// (ก) ค่าส่งเท่าเดิม (แก้ที่อยู่ในโซนเดียวกัน · รับเอง↔ส่งฟรี) ⇒ เปลี่ยนได้เสมอในหน้าต่าง — เงิน/เอกสารไม่ขยับ ·
/// (ข) ค่าส่งเปลี่ยน + ยังไม่รับเงิน ⇒ มัดจำคิดใหม่ (ชำระเต็มจำนวนรวมค่าส่ง) · ใบรับมัดจำ<b>ร่าง</b>ที่ยอดไม่ตรงแล้ว ⇒ ลบใบร่าง (ยังไม่มีเลข/ไม่ลงบัญชี) แล้วออกใหม่ตอนเงินเข้า ·
/// (ค) ค่าส่งเปลี่ยน + รับเงินแล้ว ⇒ <b>ไม่แก้มัดจำ/ใบที่ออกแล้ว</b>: ค่าส่งเพิ่ม = ยอดค้างเพิ่ม ชำระกับร้านตอนรับของ (รวมในใบส่วนที่เหลือ — <c>SettleDepositOnCompleteAsync</c> คิดจากค่าส่งใหม่) ·
///   ค่าส่งลดลงแต่ยอดที่จ่ายแล้วยังไม่เกินยอดรวมใหม่ = หักจากยอดที่เหลือ · <b>ยอดที่จ่ายแล้วเกินยอดรวมใหม่ (ต้องคืนเงิน) ⇒ ปฏิเสธ ให้ติดต่อร้าน</b>
///   (คืนเงิน = ใบลดหนี้ §86/10 ที่คนต้องตัดสิน — ระบบไม่ประทับ "ค้างคืน" เอง) ·
/// (ง) ค่าส่งเปลี่ยน + มีใบเสนอราคา/ใบกำกับที่รวมค่าส่งแล้ว (ชนิดนัดหมาย/จองยืนยัน) ⇒ ปฏิเสธ ให้ติดต่อร้าน (ต้องปรับเอกสาร/ออกใบเพิ่มหนี้-ลดหนี้) ·
/// (จ) มีสลิปรอตรวจ / กำลังชำระออนไลน์ ⇒ ปฏิเสธจนกว่าจะเสร็จ (ยอดที่กำลังจ่ายผูกยอดเดิม) · จ่ายด้วยบัตรคอร์ส + ค่าส่ง &gt; 0 ⇒ ปฏิเสธ (บัตรไม่ครอบค่าส่ง — กติกาเดียวกับตอนจอง)</para>
/// </summary>
public static class BookingFulfillmentChange
{
    public const string RuleCode = "CMS-BOOKING-FULFILLMENT-CHANGE";
    /// <summary>ฝ่ายค้านรอบ 228 S6: ยอดตอนบันทึกไม่ตรงกับที่ลูกค้าเห็นในตัวอย่าง</summary>
    public const string AmountChangedMessage = "ยอดเปลี่ยน — ตรวจค่าจัดส่ง/ยอดชำระอีกครั้งแล้วกดยืนยันใหม่";
    /// <summary>เพดานค่าตั้ง (ชั่วโมงก่อนเวลานัด) — 30 วัน</summary>
    public const int MaxCutoffHours = 720;

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string Baht(decimal v) => "฿" + v.ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string Local(DateTime utc) => utc.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " น.";

    /// <summary>ตรวจค่าตั้งก่อนบันทึก (null = ผ่าน)</summary>
    public static string? ValidateCutoff(int? hours)
        => hours is < 0 or > MaxCutoffHours ? $"ชั่วโมงก่อนเวลานัดที่ลูกค้าเปลี่ยนวิธีรับเองได้ต้องอยู่ระหว่าง 0–{MaxCutoffHours}" : null;

    /// <summary>คำอธิบายค่าตั้ง (หน้าตั้งค่า)</summary>
    public static string DescribeCutoff(int? hours) => hours switch
    {
        null => "ลูกค้าเปลี่ยนวิธีรับ/ที่อยู่เองไม่ได้ — ติดต่อร้าน (ค่าเดิม)",
        0 => "ลูกค้าเปลี่ยนวิธีรับ/ที่อยู่เองได้จนถึงเวลานัด",
        int h => $"ลูกค้าเปลี่ยนวิธีรับ/ที่อยู่เองได้ถึง {h} ชม. ก่อนเวลานัด",
    };

    public static BookingFulfillmentChangeWindow Window(int? cutoffHours, BookingStatus status, DateTime startUtc, DateTime nowUtc)
    {
        if (cutoffHours is not int h || status is not (BookingStatus.Pending or BookingStatus.Confirmed))
            return new BookingFulfillmentChangeWindow(false, false, null, null);
        var deadline = startUtc.AddHours(-Math.Clamp(h, 0, MaxCutoffHours));
        return nowUtc < deadline
            ? new BookingFulfillmentChangeWindow(true, true, deadline, $"เปลี่ยนวิธีรับ/ที่อยู่ได้ถึง {Local(deadline)}")
            : new BookingFulfillmentChangeWindow(true, false, deadline, $"เลยกำหนดเปลี่ยนวิธีรับ/ที่อยู่แล้ว ({Local(deadline)}) — กรุณาติดต่อร้าน");
    }

    public static BookingFulfillmentChangeDecision Decide(BookingFulfillmentChangeFacts f)
    {
        BookingFulfillmentChangeDecision No(string why) => new(false, why, f.OldDeposit, false, why);
        var w = Window(f.CutoffHours, f.Status, f.StartUtc, f.NowUtc);
        if (!w.Enabled) return No(f.CutoffHours == null
            ? "ร้านไม่ได้เปิดให้เปลี่ยนวิธีรับ/ที่อยู่เอง — กรุณาติดต่อร้าน"
            : "การจองนี้เปลี่ยนวิธีรับไม่ได้ (สถานะไม่ใช่รอยืนยัน/ยืนยันแล้ว)");
        if (!w.Open) return No(w.Note!);
        // บิลหน้าร้าน (POS) ที่เปิดจากการจองตรึงค่าส่งไว้ตอนเปิดบิล ⇒ เปลี่ยนที่นี่บิลไม่ตาม
        if (f.PosBillOpen) return No("ร้านเปิดบิลหน้าร้านของการจองนี้แล้ว — เปลี่ยนวิธีรับ/ที่อยู่ กรุณาติดต่อร้าน");
        var oldFee = R2(f.OldFee); var newFee = R2(f.NewFee);
        if (f.PaidWithCredit && newFee > 0m) return No("การจองนี้ชำระด้วยบัตรคอร์ส ซึ่งไม่ครอบคลุมค่าจัดส่ง — เลือก “รับเอง” หรือติดต่อร้าน");
        var oldGrand = BookingFulfillment.GrandTotal(f.ServiceTotal, oldFee);
        var newGrand = BookingFulfillment.GrandTotal(f.ServiceTotal, newFee);
        if (newFee == oldFee)
            return new(true, null, f.OldDeposit, false, $"ค่าจัดส่งเท่าเดิม ({(newFee > 0m ? Baht(newFee) : "ส่งฟรี/ไม่มี")}) · ยอดรวม {Baht(newGrand)}");

        var feeLine = $"ค่าจัดส่ง {Baht(oldFee)} → {Baht(newFee)} · ยอดรวม {Baht(oldGrand)} → {Baht(newGrand)}";
        if (f.SlipPending) return No("มีสลิปรอร้านตรวจอยู่ — ค่าจัดส่งจะเปลี่ยนยอดที่ต้องชำระ รอร้านตรวจสลิปก่อนแล้วค่อยเปลี่ยน หรือติดต่อร้าน");
        if (f.GatewayInProgress) return No("มีรายการชำระออนไลน์ที่ยังเปิดอยู่ — รอให้ชำระเสร็จ/หมดอายุก่อนแล้วค่อยเปลี่ยน (ยอดที่กำลังจ่ายผูกกับค่าจัดส่งเดิม)");
        if (f.LinkedDoc is BookingLinkedDocKind.Other || f.LinkedDoc == BookingLinkedDocKind.DepositIssued && f.PaidAmount <= 0.005m)
            return No($"{feeLine} — ร้านต้องปรับเอกสาร (ออกเอกสาร/ส่งใบอนุมัติที่รวมยอดเดิมแล้ว) กรุณาติดต่อร้าน");

        if (f.PaidAmount > 0.005m)
        {
            var paid = R2(f.PaidAmount);
            if (paid > newGrand + 0.005m)
                return No($"{feeLine} — ยอดที่ชำระแล้ว {Baht(paid)} เกินยอดรวมใหม่ ต้องคืนเงินส่วนต่าง {Baht(R2(paid - newGrand))} กรุณาติดต่อร้าน");
            var remain = R2(newGrand - paid);
            // ฝ่ายค้านรอบ 228 S1: มัดจำไม่เกินยอดรวมใหม่เสมอ
            return new(true, null, Math.Min(R2(f.OldDeposit), newGrand), false,
                $"{feeLine} · ชำระแล้ว {Baht(paid)} · ยอดคงเหลือ {Baht(remain)} ชำระกับร้านตอนรับสินค้า/ใช้บริการ");
        }

        var newDeposit = Math.Min(R2(f.NewDeposit), newGrand);   // ฝ่ายค้านรอบ 228 S1: เพดาน = ยอดรวมใหม่
        var oldSplit = BookingFulfillment.SplitPrepayment(f.OldDeposit, f.ServiceTotal, oldFee);
        var newSplit = BookingFulfillment.SplitPrepayment(newDeposit, f.ServiceTotal, newFee);
        var draftStale = f.LinkedDoc == BookingLinkedDocKind.DepositDraft && (newDeposit != R2(f.OldDeposit) || oldSplit != newSplit);
        var depositLine = newDeposit != R2(f.OldDeposit) ? $" · ยอดชำระล่วงหน้า {Baht(R2(f.OldDeposit))} → {Baht(newDeposit)}" : "";
        return new(true, null, newDeposit, draftStale, feeLine + depositLine);
    }

    /// <summary>ฝ่ายค้านรอบ 228 S1: มัดจำหลังเปลี่ยนค่าส่ง จาก<b>ข้อเท็จจริงที่ตรึงบนการจอง</b> (ไม่ใช่โหมดปัจจุบันของเว็บ — เจ้าของเปลี่ยนโหมดภายหลังไม่ทำให้ใบเดิมขยับผิด) ·
    /// มัดจำเดิมครอบยอดรวมเดิมทั้งหมด (ชำระเต็มจำนวน) ⇒ ยอดรวมใหม่ · นอกนั้น (มัดจำ บาท/% ฐานค่าบริการ · ไม่เลือก = 0) ⇒ เท่าเดิม (ไม่เกินยอดรวมใหม่)</summary>
    public static decimal NewDeposit(decimal oldDeposit, decimal serviceTotal, decimal oldFee, decimal newFee)
    {
        var oldGrand = BookingFulfillment.GrandTotal(serviceTotal, oldFee);
        var newGrand = BookingFulfillment.GrandTotal(serviceTotal, newFee);
        var coversAll = oldDeposit > 0.005m && oldDeposit + 0.005m >= oldGrand;
        return coversAll ? newGrand : Math.Min(R2(Math.Max(0m, oldDeposit)), newGrand);
    }

    /// <summary>ฝ่ายค้านรอบ 228 S8: ใบรับมัดจำ "ร่าง" ที่ลบทิ้งได้ = ร่างจริง (สถานะร่าง <b>และ</b>เลขยังเป็น DRAFT-…) · รออนุมัติ/มีเลขแล้ว = ถือว่าออกแล้ว</summary>
    public static BookingLinkedDocKind DepositDocKind(DocumentStatus status, string? documentNumber)
        => status == DocumentStatus.Draft && (documentNumber ?? "").StartsWith("DRAFT-", StringComparison.Ordinal)
            ? BookingLinkedDocKind.DepositDraft : BookingLinkedDocKind.DepositIssued;

    /// <summary>หมายเหตุภายในบนการจอง (ร้านเห็นว่าใครเปลี่ยนอะไร)</summary>
    public static string ChangedNote(string? oldMethod, string? newMethod, string effect, DateTime atUtc, bool discardedDraft)
        => $"[{RuleCode}] ลูกค้าเปลี่ยนวิธีรับจากหน้าจัดการการจอง {Local(atUtc)}: {BookingFulfillment.Label(oldMethod)} → {BookingFulfillment.Label(newMethod)} · {effect}"
           + (discardedDraft ? " · ลบใบรับมัดจำร่างที่ยอดไม่ตรงแล้ว (ออกใหม่ตอนรับเงิน)" : "");
}
