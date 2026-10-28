using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>สถานะ "ส่งต่อให้ต้นทาง" ของรายการชำระเงินที่เงินเข้าแล้ว</summary>
public enum PaymentRedispatchState
{
    /// <summary>เงินยังไม่เข้า (ไม่ใช่ Succeeded) — ไม่มีอะไรให้ส่งต่อ</summary>
    NotSucceeded = 0,
    /// <summary>ต้นทางถูกบันทึกแล้ว (ส่งต่อครั้งแรกสำเร็จ · ส่งต่อใหม่สำเร็จ · หรือพนักงานบันทึกที่ต้นทางเองหลังเกิดปัญหา) — ส่งต่ออีก = no-op</summary>
    Fulfilled = 1,
    /// <summary>เงินเข้าแล้วแต่ต้นทางยังไม่ถูกบันทึก (handler ล้ม/ไม่มี handler) และยังไม่มีการส่งต่อใหม่ที่สำเร็จ ⇒ กด "ส่งต่อใหม่" ได้</summary>
    Pending = 2,
    /// <summary>มีคนกด "ส่งต่อใหม่" ไปแล้วและยังไม่มีผล (✅/⚠️) ตามหลัง ภายในหน้าต่างเวลา — คำขอที่สองต้องรอ ไม่ใช่รันซ้อน</summary>
    InProgress = 3,
}

/// <summary>บันทึกรับเงินฝั่งต้นทาง (ใบเสร็จ/แถวรับเงินของการจอง·ออเดอร์·เอกสาร) ที่ใช้เป็นหลักฐานว่า "ยอดนี้ถูกบันทึกแล้ว"</summary>
/// <param name="Amount">ยอดที่บันทึก</param>
/// <param name="At">เวลาที่บันทึก (UTC)</param>
/// <param name="Reference">เลขอ้างอิงบนบันทึกนั้น — ตรงกับเลขของ gateway = บันทึกโดย handler เอง (ไม่ใช่หลักฐานการบันทึกมือ)</param>
public sealed record SourceRecordedPayment(decimal Amount, DateTime At, string? Reference);

/// <summary>
/// **ตัวตัดสินเดียว** ของปุ่ม "ส่งต่อใหม่" (รอบ 205 ทีม W1 · R-01 · BOOKING_REVENUE_AUDIT G-2) — เซิร์ฟเวอร์คำนวณจากประวัติของรายการ +
/// หลักฐานฝั่งต้นทาง · หน้าเว็บแค่แสดง (F2 #5)
///
/// <para>ข้อเท็จจริง: <c>PaymentIntentService.DispatchSucceededAsync</c> เขียนเหตุการณ์ <see cref="UnfulfilledNotePrefix"/> เมื่อ handler ล้ม/ไม่มี ·
/// การส่งต่อครั้งแรกที่สำเร็จ<b>ไม่</b>เขียนเหตุการณ์ (ของเดิม) · ส่งต่อใหม่เขียน <see cref="AttemptNotePrefix"/> ก่อน แล้ว <see cref="FulfilledNotePrefix"/>
/// หรือ ⚠️ ตามผล ⇒ "ค้าง" = มี ⚠️ แล้วไม่มี ✅ ตามหลัง · **ฝ่ายค้าน W1 ข้อ 2**: พนักงานอาจบันทึกที่ต้นทางเองหลัง ⚠️ (ใบเสร็จ/รับเงินด้วยมือ) ⇒
/// ประวัติของรายการชำระไม่รู้ — ต้องรับหลักฐานฝั่งต้นทาง (<see cref="SourceRecorded"/>: มีบันทึกยอดเท่า intent หลังเวลา ⚠️ ที่ไม่ใช่ของ handler)
/// แล้วถือว่า Fulfilled ไม่ใช่ Pending (ป้าย "ยังไม่ได้บันทึก" โกหก + กดส่งต่อแล้วเงินซ้ำ)</para>
///
/// <para>**ฝ่ายค้าน W1 ข้อ 3**: การส่งต่อซ้อน — attempt ที่ยังไม่มีผลภายใน <see cref="InProgressWindow"/> = <see cref="PaymentRedispatchState.InProgress"/>
/// (ผู้เรียกถือ advisory lock ต่อ intent ระหว่างตรวจ+เขียน attempt ⇒ คำขอที่สองเห็น attempt แล้ว) · เกินหน้าต่าง (process ตายกลางทาง) ⇒ กลับเป็น Pending</para>
/// </summary>
public static class PaymentRedispatchPolicy
{
    /// <summary>คำนำหน้าหมายเหตุ ⚠️ ที่ DispatchSucceededAsync เขียนทั้งสองกรณี (ไม่มี handler · handler ล้ม) — ค่าเดิมในฐานต้องยังตรง</summary>
    public const string UnfulfilledNotePrefix = "⚠️ เงินเข้าแล้วแต่";
    /// <summary>หมายเหตุเมื่อส่งต่อใหม่สำเร็จ</summary>
    public const string FulfilledNotePrefix = "✅ ส่งต่อใหม่ให้ต้นทางสำเร็จ";
    /// <summary>หมายเหตุตอนเริ่มส่งต่อใหม่ (ร่องรอยว่าใครกด — ไม่ใช่ผลลัพธ์)</summary>
    public const string AttemptNotePrefix = "🔁 ส่งต่อใหม่โดย";
    /// <summary>attempt ที่ไม่มีผลตามหลังนานกว่านี้ = ถือว่าหลุด (process ตาย) ⇒ ส่งต่อใหม่ได้อีก</summary>
    public static readonly TimeSpan InProgressWindow = TimeSpan.FromMinutes(10);

    public static bool IsUnfulfilledNote(string? note) => note != null && note.StartsWith(UnfulfilledNotePrefix, StringComparison.Ordinal);
    public static bool IsFulfilledNote(string? note) => note != null && note.StartsWith(FulfilledNotePrefix, StringComparison.Ordinal);
    public static bool IsAttemptNote(string? note) => note != null && note.StartsWith(AttemptNotePrefix, StringComparison.Ordinal);

    /// <summary>เวลาของ ⚠️ ล่าสุด (เรียงตามเวลา) · null = ไม่มี</summary>
    public static DateTime? LastUnfulfilledAt(IEnumerable<(DateTime At, string? Note)> events)
    {
        DateTime? last = null;
        foreach (var (at, note) in events) if (IsUnfulfilledNote(note)) last = at;
        return last;
    }

    /// <summary>มีบันทึกรับเงินฝั่งต้นทางที่ "ครอบ" ยอดของรายการนี้ไหม — ยอดเท่ากัน (±0.005) · ไม่ใช่เลขอ้างอิงของ gateway (= บันทึกมือ/ทางอื่น) ·
    /// เกิดหลัง <paramref name="sinceUtc"/> (เวลา ⚠️ ล่าสุด · null = ไม่จำกัดเวลา) — ตัวเดียวกับที่ handler ใช้กัน "รับซ้ำ"</summary>
    public static bool SourceRecorded(IEnumerable<SourceRecordedPayment> recorded, decimal intentAmount,
        IEnumerable<string> gatewayReferences, DateTime? sinceUtc)
    {
        var refs = gatewayReferences.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToHashSet(StringComparer.Ordinal);
        foreach (var p in recorded)
        {
            if (Math.Abs(p.Amount - intentAmount) > 0.005m) continue;
            if (p.Reference != null && refs.Contains(p.Reference.Trim())) continue;   // ของ handler เอง
            if (sinceUtc is DateTime s && p.At < s) continue;
            return true;
        }
        return false;
    }

    /// <summary>ตัดสินจากสถานะ + เหตุการณ์<b>เรียงตามเวลา</b> (เก่า → ใหม่) + หลักฐานฝั่งต้นทาง</summary>
    /// <param name="sourceRecordedSinceWarning">ผล <see cref="SourceRecorded"/> โดย since = <see cref="LastUnfulfilledAt"/></param>
    public static PaymentRedispatchState Evaluate(PaymentIntentStatus status, IEnumerable<(DateTime At, string? Note)> eventsChronological,
        bool sourceRecordedSinceWarning, DateTime nowUtc)
    {
        if (status != PaymentIntentStatus.Succeeded) return PaymentRedispatchState.NotSucceeded;
        var pending = false;
        DateTime? openAttempt = null;
        foreach (var (at, note) in eventsChronological)
        {
            if (IsUnfulfilledNote(note)) { pending = true; openAttempt = null; }
            else if (IsFulfilledNote(note)) { pending = false; openAttempt = null; }
            else if (IsAttemptNote(note)) openAttempt = at;
        }
        if (!pending) return PaymentRedispatchState.Fulfilled;
        if (sourceRecordedSinceWarning) return PaymentRedispatchState.Fulfilled;
        if (openAttempt is DateTime a && nowUtc - a < InProgressWindow) return PaymentRedispatchState.InProgress;
        return PaymentRedispatchState.Pending;
    }

    /// <summary>รุ่นไม่มีหลักฐานฝั่งต้นทาง/เวลา — ใช้ในเทสต์/จุดที่ไม่มีข้อมูลต้นทาง (เท่ากับ sourceRecorded=false · attempt ถือว่าเก่าหมด)</summary>
    public static PaymentRedispatchState Evaluate(PaymentIntentStatus status, IEnumerable<string?> notesChronological)
        => Evaluate(status, notesChronological.Select(n => (DateTime.MinValue, n)), sourceRecordedSinceWarning: false, nowUtc: DateTime.UtcNow);

    /// <summary>ข้อความถึงผู้ใช้เมื่อ<b>ไม่</b>ส่งต่อ (no-op) — บอกเหตุ + ทางไปต่อ</summary>
    public static string NoOpMessage(PaymentRedispatchState state) => state switch
    {
        PaymentRedispatchState.NotSucceeded =>
            "รายการนี้เงินยังไม่เข้า (สถานะไม่ใช่ “สำเร็จ”) — ไม่มีอะไรให้ส่งต่อ · ถ้าเห็นเงินเข้าบัญชีจริงใช้ “ยืนยันด้วยมือ”",
        PaymentRedispatchState.Fulfilled =>
            "ต้นทางของรายการนี้ถูกบันทึกแล้ว (โดยระบบหรือพนักงาน) — ไม่ได้ส่งต่อซ้ำ (ไม่มีการบันทึกรับเงินซ้ำ) · ถ้ายอดไม่ตรง เปิดเอกสาร/การจองต้นทางแล้วแก้ที่นั่น",
        PaymentRedispatchState.InProgress =>
            "รายการนี้กำลังถูกส่งต่ออยู่ (มีคนกดไปเมื่อครู่) — รอผลในประวัติก่อน ไม่รันซ้อน",
        _ => "",
    };

    /// <summary>ป้ายบนแถวในหน้ารายการรับชำระออนไลน์เมื่อค้าง</summary>
    public const string PendingLabel = "เงินเข้าแล้ว · ยังไม่ได้บันทึกให้ต้นทาง";
}
