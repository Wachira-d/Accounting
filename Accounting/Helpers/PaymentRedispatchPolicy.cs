using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>สถานะ "ส่งต่อให้ต้นทาง" ของรายการชำระเงินที่เงินเข้าแล้ว</summary>
public enum PaymentRedispatchState
{
    /// <summary>เงินยังไม่เข้า (ไม่ใช่ Succeeded) — ไม่มีอะไรให้ส่งต่อ</summary>
    NotSucceeded = 0,
    /// <summary>ต้นทางถูกบันทึกแล้ว (ส่งต่อครั้งแรกสำเร็จ หรือส่งต่อใหม่สำเร็จแล้ว) — ส่งต่ออีก = no-op</summary>
    Fulfilled = 1,
    /// <summary>เงินเข้าแล้วแต่ต้นทางยังไม่ถูกบันทึก (handler ล้ม/ไม่มี handler) และยังไม่มีการส่งต่อใหม่ที่สำเร็จ ⇒ กด "ส่งต่อใหม่" ได้</summary>
    Pending = 2,
}

/// <summary>
/// **ตัวตัดสินเดียว** ของปุ่ม "ส่งต่อใหม่" (รอบ 205 ทีม W1 · R-01 · BOOKING_REVENUE_AUDIT G-2) — เซิร์ฟเวอร์คำนวณจากประวัติของรายการ
/// หน้าเว็บแค่แสดง (F2 #5)
///
/// <para>ข้อเท็จจริงที่มีอยู่แล้ว: <c>PaymentIntentService.DispatchSucceededAsync</c> เขียนเหตุการณ์ <see cref="UnfulfilledNotePrefix"/> เมื่อ
/// handler ล้ม/ไม่มี · การส่งต่อครั้งแรกที่สำเร็จ<b>ไม่</b>เขียนเหตุการณ์ (ของเดิม — ห้ามเปลี่ยนความหมายย้อนหลัง) · การส่งต่อใหม่ที่สำเร็จเขียน
/// <see cref="FulfilledNotePrefix"/> ⇒ "ค้าง" = มี ⚠️ และไม่มี ✅ หลังจากนั้น (เรียงตามเวลา)</para>
///
/// <para>handler ทุกตัว idempotent (L-1/S-1) ⇒ การส่งต่อใหม่ซ้ำไม่บันทึกเงินซ้ำ — แต่สถานะ <see cref="PaymentRedispatchState.Fulfilled"/> ยังตัด
/// ปุ่ม/คำขอออกก่อน เพื่อให้ "no-op พร้อมข้อความ" ไม่ใช่รัน handler เล่น ๆ</para>
/// </summary>
public static class PaymentRedispatchPolicy
{
    /// <summary>คำนำหน้าหมายเหตุ ⚠️ ที่ DispatchSucceededAsync เขียนทั้งสองกรณี (ไม่มี handler · handler ล้ม) — ค่าเดิมในฐานต้องยังตรง</summary>
    public const string UnfulfilledNotePrefix = "⚠️ เงินเข้าแล้วแต่";
    /// <summary>หมายเหตุเมื่อส่งต่อใหม่สำเร็จ</summary>
    public const string FulfilledNotePrefix = "✅ ส่งต่อใหม่ให้ต้นทางสำเร็จ";
    /// <summary>หมายเหตุตอนเริ่มส่งต่อใหม่ (ร่องรอยว่าใครกด — ไม่ใช่ผลลัพธ์)</summary>
    public const string AttemptNotePrefix = "🔁 ส่งต่อใหม่โดย";

    public static bool IsUnfulfilledNote(string? note) => note != null && note.StartsWith(UnfulfilledNotePrefix, StringComparison.Ordinal);
    public static bool IsFulfilledNote(string? note) => note != null && note.StartsWith(FulfilledNotePrefix, StringComparison.Ordinal);

    /// <summary>ตัดสินจากสถานะ + หมายเหตุเหตุการณ์<b>เรียงตามเวลา</b> (เก่า → ใหม่)</summary>
    public static PaymentRedispatchState Evaluate(PaymentIntentStatus status, IEnumerable<string?> notesChronological)
    {
        if (status != PaymentIntentStatus.Succeeded) return PaymentRedispatchState.NotSucceeded;
        var pending = false;
        foreach (var n in notesChronological)
        {
            if (IsUnfulfilledNote(n)) pending = true;
            else if (IsFulfilledNote(n)) pending = false;
        }
        return pending ? PaymentRedispatchState.Pending : PaymentRedispatchState.Fulfilled;
    }

    /// <summary>ข้อความถึงผู้ใช้เมื่อ<b>ไม่</b>ส่งต่อ (no-op) — บอกเหตุ + ทางไปต่อ</summary>
    public static string NoOpMessage(PaymentRedispatchState state) => state switch
    {
        PaymentRedispatchState.NotSucceeded =>
            "รายการนี้เงินยังไม่เข้า (สถานะไม่ใช่ “สำเร็จ”) — ไม่มีอะไรให้ส่งต่อ · ถ้าเห็นเงินเข้าบัญชีจริงใช้ “ยืนยันด้วยมือ”",
        PaymentRedispatchState.Fulfilled =>
            "ต้นทางของรายการนี้ถูกบันทึกแล้ว — ไม่ได้ส่งต่อซ้ำ (ไม่มีการบันทึกรับเงินซ้ำ) · ถ้ายอดไม่ตรง เปิดเอกสาร/การจองต้นทางแล้วแก้ที่นั่น",
        _ => "",
    };

    /// <summary>ป้ายบนแถวในหน้ารายการรับชำระออนไลน์เมื่อค้าง</summary>
    public const string PendingLabel = "เงินเข้าแล้ว · ยังไม่ได้บันทึกให้ต้นทาง";
}
