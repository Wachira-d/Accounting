using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>การจองที่เลยวันเช็คเอาต์แล้วยังไม่ถูกปิด — ชนิดของความค้าง (ชื่อ enum ออก API เป็นสตริง)</summary>
public enum LodgingOverdueKind
{
    /// <summary>ไม่ค้าง</summary>
    None = 0,
    /// <summary>เช็คอินแล้ว เลยวันออกแล้ว ยังไม่มีใครกดเช็คเอาต์ (ยังไม่ได้ออกบิล)</summary>
    StayNotCheckedOut = 1,
    /// <summary>ยืนยัน/รอมัดจำ เลยวันออกแล้ว ไม่เคยเช็คอิน (น่าจะไม่มา — ยังไม่ได้คิดค่าปรับ/คืนเงิน)</summary>
    ArrivalNotRecorded = 2,
}

/// <summary>
/// **"ค้างปิด" ของการจองที่พัก — ตัวตัดสินตัวเดียวของ night audit · หน้ารายการ · หน้ารายละเอียด** (รอบ 202 ทีม LO · O-P0-2 · คำตัดสินข้อ 119)
///
/// <para><b>ที่มา (R1)</b>: night audit รุ่นเดิมประทับ <c>CheckedOut</c>/<c>NoShow</c> เอง ⇒ การจองหลุดจากเส้นเช็คเอาต์/ยกเลิกของโมดูล
/// (ด่านสถานะปฏิเสธทั้งคู่) ⇒ ออกใบเช็คเอาต์ไม่ได้ · มัดจำค้างเป็นหนี้สินตลอดไป · ค่าปรับ no-show ไม่เคยถูกคิด — สถานะปลายทางที่ระบบ
/// ประทับเองโดยไม่มีของจริงยืนยัน · รุ่นนี้ <b>ไม่เปลี่ยนสถานะ</b>: ติดธง + หมายเหตุครั้งเดียว ให้พนักงานปิดผ่านเส้นปกติ (ออกใบ/ริบ/คืน)</para>
///
/// <para>แถวที่ job รุ่นเดิมประทับไปแล้ว (ก่อนรอบ 202) ไม่ถูกย้ายสถานะกลับ (ไม่เดาว่าแขกออกจริงเมื่อไร) — แต่เปิดทางปิดให้ครบ:
/// <see cref="IsLegacyAutoCheckout"/> (ออกใบเช็คเอาต์ย้อนหลังผ่านเส้นเช็คเอาต์เดิม) · <see cref="IsLegacyAutoNoShow"/> (คิดค่าปรับ/ค้างคืนผ่านเส้นยกเลิกเดิม)</para>
/// </summary>
public static class LodgingOverdueRule
{
    /// <summary>ผ่อนกี่วันหลังวันเช็คเอาต์ก่อน night audit ติดธง+หมายเหตุ (เดิม = ปิดสถานะ) — ที่พักที่ปิดบิลวันถัดไป/สุดสัปดาห์</summary>
    public const int FlagGraceDays = 2;

    /// <summary>ข้อความที่ night audit รุ่นก่อนรอบ 202 เขียนลงหมายเหตุภายในเมื่อปิดการเข้าพักเอง — ตัวระบุแถวที่ถูกประทับ CheckedOut โดยระบบ</summary>
    public const string LegacyAutoCheckoutMarker = "ระบบปิดการเข้าพักอัตโนมัติ";

    /// <summary>เหตุผลยกเลิกที่ night audit รุ่นก่อนรอบ 202 ประทับเมื่อบันทึก no-show เอง</summary>
    public const string LegacyAutoNoShowReason = "ระบบบันทึกอัตโนมัติ: ไม่มาเข้าพักและไม่มีการเช็คอิน";

    /// <summary>ค้างแบบไหน — เทียบวันเช็คเอาต์กับวันนี้ตามปฏิทินไทย (เลยวันออกแล้วอย่างน้อยหนึ่งวัน)</summary>
    public static LodgingOverdueKind Classify(LodgingReservationStatus status, DateTime checkOutDate, DateTime todayThai)
    {
        if (checkOutDate.Date >= todayThai.Date) return LodgingOverdueKind.None;
        return status switch
        {
            LodgingReservationStatus.CheckedIn => LodgingOverdueKind.StayNotCheckedOut,
            LodgingReservationStatus.Confirmed or LodgingReservationStatus.Pending => LodgingOverdueKind.ArrivalNotRecorded,
            _ => LodgingOverdueKind.None,
        };
    }

    /// <summary>night audit ควรติดธงใบนี้รอบนี้ไหม — ค้างเกินระยะผ่อน และยังไม่เคยติด (หมายเหตุครั้งเดียว ไม่ซ้ำทุก 6 ชม.)</summary>
    public static bool ShouldFlag(LodgingOverdueKind kind, DateTime checkOutDate, DateTime todayThai, DateTime? flaggedAt)
        => kind != LodgingOverdueKind.None && flaggedAt == null && checkOutDate.Date < todayThai.Date.AddDays(-FlagGraceDays);

    /// <summary>ป้ายสั้นบนรายการ (null = ไม่ค้าง)</summary>
    public static string? Label(LodgingOverdueKind kind) => kind switch
    {
        LodgingOverdueKind.StayNotCheckedOut => "ค้างปิด · เลยวันออกยังไม่เช็คเอาต์",
        LodgingOverdueKind.ArrivalNotRecorded => "ค้างปิด · เลยวันพักไม่มีการเช็คอิน",
        _ => null,
    };

    /// <summary>หมายเหตุที่ night audit เขียนครั้งเดียวต่อใบ — บอกทางไปต่อด้วยเส้นปกติของโมดูล</summary>
    public static string FlagNote(LodgingOverdueKind kind, DateTime checkOutDate) => kind switch
    {
        LodgingOverdueKind.StayNotCheckedOut =>
            $"ค้างปิด: เลยวันเช็คเอาต์ {checkOutDate:dd/MM/yyyy} เกิน {FlagGraceDays} วันแล้วยังไม่ได้เช็คเอาต์ — ห้องยังถูกกันไว้ · "
            + "กด “เช็คเอาต์ + ออกบิล” เพื่อออกเอกสารและใช้มัดจำ (ระบบไม่ปิดสถานะเอง)",
        LodgingOverdueKind.ArrivalNotRecorded =>
            $"ค้างปิด: เลยวันพัก (ออก {checkOutDate:dd/MM/yyyy}) แล้วไม่มีการเช็คอิน — กด “No-show” เพื่อคิดค่าปรับ/ค้างคืนตามนโยบาย "
            + "หรือ “ยกเลิก” / “เลื่อนวัน” ถ้าแขกแจ้งไว้ (ระบบไม่ปิดสถานะเอง)",
        _ => "",
    };

    /// <summary>แถวที่ night audit รุ่นเดิมประทับ <c>CheckedOut</c> เองโดยยังไม่ได้ออกใบ — ออกใบเช็คเอาต์ย้อนหลังผ่านเส้นเช็คเอาต์ได้
    /// (ที่พักโหมดไม่ออกเอกสาร ไม่ต้องออก ⇒ false)</summary>
    public static bool IsLegacyAutoCheckout(LodgingReservationStatus status, Guid? finalDocumentId, string? internalNotes, bool accountingOff)
        => status == LodgingReservationStatus.CheckedOut && finalDocumentId == null && !accountingOff
           && internalNotes != null && internalNotes.Contains(LegacyAutoCheckoutMarker, StringComparison.Ordinal);

    /// <summary>แถวรุ่นเก่าที่พนักงานเปิดกลับเข้าเส้นเช็คเอาต์ (ออกใบย้อนหลัง) — เช็คอินอยู่แต่มีเวลาเช็คเอาต์แล้ว (เส้นปกติไม่มีทางเป็นแบบนี้:
    /// CheckedOutAt ถูกตั้งตอนปิดการเข้าพักเท่านั้น) ⇒ ขั้นปิดต้องไม่แตะสถานะห้อง/งานแม่บ้านปัจจุบัน และคงเวลาออกเดิม</summary>
    public static bool IsReopenedLegacy(LodgingReservationStatus status, DateTime? checkedOutAt)
        => status == LodgingReservationStatus.CheckedIn && checkedOutAt != null;

    /// <summary>แถวที่ night audit รุ่นเดิมประทับ <c>NoShow</c> เองโดยยังไม่ได้คิดค่าปรับ/ตั้งยอดคืน — เดินเส้นยกเลิกแบบ no-show ได้หนึ่งครั้ง
    /// (เส้นยกเลิกเขียนทับเหตุผล ⇒ ไม่ตรงเงื่อนไขอีก = ไม่คิดซ้ำ)</summary>
    public static bool IsLegacyAutoNoShow(LodgingReservationStatus status, string? cancellationReason, decimal cancellationFee, decimal refundAmount)
        => status == LodgingReservationStatus.NoShow && cancellationFee == 0m && refundAmount == 0m
           && cancellationReason != null && cancellationReason.StartsWith(LegacyAutoNoShowReason, StringComparison.Ordinal);

    /// <summary>night audit นับมิเตอร์ <c>lodging.stay</c> ตอนติดธงไหม — ฝ่ายค้านรอบ 202 P1-2: ต้องตรงกับกติกาของเส้นปิดปกติ
    /// (<c>CancelCoreAsync</c>: จองแล้วยกเลิกฟรีก่อนจ่ายมัดจำต้องไม่ถูกคิด) ⇒ นับเฉพาะ "พักจริง" (เช็คอิน) · ยืนยันแล้ว (กันห้องจริง = no-show ถูกคิดเสมอ) ·
    /// หรือมีมัดจำ · Pending ที่ไม่มีเงิน = ไม่นับ</summary>
    public static bool ShouldMeterOnFlag(LodgingReservationStatus status, decimal depositPaid)
        => status is LodgingReservationStatus.CheckedIn or LodgingReservationStatus.Confirmed || depositPaid > 0m;

    /// <summary>ออกใบเช็คเอาต์ย้อนหลังของแถวรุ่นเก่า: วันใช้บริการ (tax point §78/1) อยู่ในเดือนที่ยื่น ภ.พ.30 แล้ว ⇒ ต้องมี<b>คน</b>รับทราบเอง
    /// (ใบนี้ต้องยื่นแบบเพิ่มเติมของเดือนนั้น) — ฝ่ายค้านรอบ 202 P1-4: เดิมอนุมัติแบบ SystemWorkflow ⇒ คำเตือน "ยื่นแล้ว" ผ่านเงียบ · null = ไปต่อได้</summary>
    public static string? BackfillVatAckProblem(bool periodFiled, bool acknowledged, DateTime serviceDate)
        => !periodFiled || acknowledged ? null
            : $"วันใช้บริการ {serviceDate:dd/MM/yyyy} อยู่ในเดือนที่ยื่น ภ.พ.30 แล้ว — ใบเช็คเอาต์ย้อนหลังใบนี้ต้องยื่นแบบเพิ่มเติมของเดือนนั้น · "
              + "ติ๊ก “รับทราบว่าต้องยื่นเพิ่มเติม” ในหน้าเช็คเอาต์แล้วกดอีกครั้ง (หรือปรึกษาผู้ทำบัญชีก่อนออกใบ)";
}
