using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 234 (คำตัดสิน 234-W1…234-W3 · เจ้าของ: "เคสที่ระบบถูกหัก ณ ที่จ่าย มีหน้าให้ติดตาม และอัปโหลดเพื่อยืนยันการได้รับใบหัก ณ ที่จ่ายจากลูกค้า
/// และส่งเข้าระบบบัญชีของบริษัทตรงได้เลยรึยัง") — <b>ตัวตัดสินเดียว</b>ของสถานะหนังสือรับรอง 50 ทวิ ที่ผู้เช่าต้องส่งให้ NextAcc · pure
/// <list type="bullet">
/// <item>ผู้เช่าเห็นในศูนย์บิล · แอดมินเห็นในแท็บ "50 ทวิ จากลูกค้า" · ป้ายเดียวกัน (ห้ามสำเนาในหน้าเว็บ)</item>
/// <item>"ยืนยันแล้ว" มาจากทะเบียนภาษีถูกหักของบริษัทผู้ให้บริการ (<c>WhtCreditReceived</c> Received/Claimed) เท่านั้น — ไฟล์ที่ส่งมาแต่ยังไม่ตรวจ = "รอตรวจ"
/// (เครดิตภาษีได้เฉพาะมีหนังสือรับรองจริงที่ถูกต้อง · หลักการ #3 ห้ามประทับสถานะปลายทางเอง)</item>
/// </list>
/// </summary>
public static class PlatformWhtCertificateStatus
{
    public enum State { NotRequired = 0, AwaitingUpload = 1, AwaitingReview = 2, Confirmed = 3, Rejected = 4 }

    /// <summary>ค่าตั้งต้นของ "เกินกำหนด N วัน" ในหน้าติดตามของแอดมิน (หน้าเปลี่ยนได้ต่อการดู) — นับจากวันที่อนุมัติรายการ</summary>
    public const int DefaultOverdueDays = 30;
    public const int MinRemindIntervalHours = 24;
    public const string RuleCode = "PLATFORM-WHT-CERT";

    public static State Of(decimal withholdingTax, bool approved, bool hasFile, string? rejectedReason, WhtCreditStatus? creditStatus)
    {
        if (withholdingTax <= 0m || !approved) return State.NotRequired;
        if (creditStatus is WhtCreditStatus.Received or WhtCreditStatus.Claimed) return State.Confirmed;
        if (hasFile) return State.AwaitingReview;
        return string.IsNullOrWhiteSpace(rejectedReason) ? State.AwaitingUpload : State.Rejected;
    }

    public static string Label(State s) => s switch
    {
        State.AwaitingUpload => "รอส่งหนังสือรับรอง 50 ทวิ",
        State.AwaitingReview => "ส่งแล้ว — รอ NextAcc ตรวจ",
        State.Confirmed => "ยืนยันแล้ว",
        State.Rejected => "ถูกตีกลับ — ส่งใหม่",
        _ => "",
    };

    /// <summary>ผู้เช่าส่ง/ส่งใหม่ได้ไหม (ยืนยันแล้ว = ปิด)</summary>
    public static bool TenantCanUpload(State s) => s is State.AwaitingUpload or State.Rejected or State.AwaitingReview;

    /// <summary>ค้างเกินกำหนด (ยังไม่ยืนยัน · อนุมัติมานานกว่า <paramref name="overdueDays"/> วัน)</summary>
    public static bool IsOverdue(State s, DateTime? approvedAtUtc, DateTime nowUtc, int overdueDays)
        => (s is State.AwaitingUpload or State.Rejected) && approvedAtUtc is DateTime a && (nowUtc - a).TotalDays > Math.Max(1, overdueDays);

    /// <summary>ทวงได้ไหม — ยังรอผู้เช่า (ส่ง/ส่งใหม่) และไม่ได้ทวงภายใน 24 ชม.</summary>
    public static string? RemindRefusal(State s, DateTime? lastRemindedUtc, DateTime nowUtc)
    {
        if (s is not (State.AwaitingUpload or State.Rejected)) return "รายการนี้ไม่ได้รอผู้เช่าส่ง 50 ทวิ";
        if (lastRemindedUtc is DateTime r && (nowUtc - r).TotalHours < MinRemindIntervalHours)
            return $"ทวงไปแล้วเมื่อไม่ถึง {MinRemindIntervalHours} ชั่วโมงก่อน";
        return null;
    }

    /// <summary>แอดมินยืนยันได้ไหม — ต้องมีไฟล์ + เลขที่ใบ · ยืนยันแล้ว = ซ้ำไม่ได้</summary>
    public static string? ConfirmRefusal(State s, string? certificateNumber)
    {
        if (s == State.Confirmed) return "ยืนยันไปแล้ว";
        if (s != State.AwaitingReview) return "ผู้เช่ายังไม่ได้ส่งหนังสือรับรอง 50 ทวิ — ยืนยันได้เมื่อมีไฟล์ให้ตรวจ";
        if (string.IsNullOrWhiteSpace(certificateNumber)) return "กรอกเลขที่หนังสือรับรองตามที่พิมพ์บนใบ — กฎหมายให้เครดิตเฉพาะรายการที่มีใบจริง";
        return null;
    }

    /// <summary>ฝ่ายค้าน 234 N1: ยอดภาษีบนใบ/ในทะเบียน ต้องตรงยอดที่ผู้เช่าหักจากรายการ (คลาด ≤ 0.01) · null ยอดเทียบ = ไม่ได้กรอก (ไม่ตรวจ)</summary>
    public static string? AmountMismatch(decimal paymentWht, decimal? otherAmount)
        => otherAmount is decimal o && Math.Abs(o - paymentWht) > 0.01m
            ? $"ยอดภาษี {o:N2} บาท ไม่ตรงยอดที่ผู้เช่าหักจากรายการนี้ {paymentWht:N2} บาท"
            : null;

    public static string? RejectRefusal(State s, string? reason)
    {
        if (s != State.AwaitingReview) return "ตีกลับได้เฉพาะใบที่ส่งมาแล้วและยังไม่ยืนยัน";
        if (string.IsNullOrWhiteSpace(reason)) return "ระบุเหตุผลที่ตีกลับ (ผู้เช่าจะเห็นข้อความนี้)";
        return null;
    }
}
