using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 (B-M6 · คำตัดสิน 259-4-6) — ตัวตัดสินเดียวของ "นำส่ง e-Tax ต่อกรมสรรพากร (โหมดส่งตรง)" · pure
/// <para>(1) <b>ผลตอบกลับ</b>: <c>SubmittedAt</c> ประทับเฉพาะเมื่อกรมตอบรับจริง (HTTP สำเร็จ) — เดิม RD ตอบ 4xx/5xx ก็ประทับ "Submitted" + เลขส่ง
/// <c>ERR-…</c> ที่แต่งขึ้น ⇒ เอกสารถูกล็อกว่า "ส่งกรมแล้ว" ทั้งที่กรมไม่รับ (F2 ข้อ 3) · ไม่มีเลขส่งในคำตอบ = ว่าง (ไม่แต่ง <c>RD-{เวลา}</c>)</para>
/// <para>(2) <b>ลองใหม่ได้ไหม</b>: ลงนามแล้ว หรือส่งล้มทางเทคนิค (ไม่ใช่ลงนามล้ม/กรมปฏิเสธ) — เดิมปุ่ม "ลองส่งใหม่ทั้งหมด" อ่านแถว Error แต่ตัวส่งรับเฉพาะ Signed ⇒ ล้มทุกแถว</para>
/// <para>(3) <b>งานประจำเดือน</b>: วันที่ 1–15 (เวลาไทย) นำส่งใบของเดือนก่อน (และเก่ากว่า) ที่ยังไม่ส่ง · ลองใหม่แบบเว้นช่วง (คิว) · กำหนดส่ง = วันที่ 15 ของเดือนถัดไป</para>
/// </summary>
public static class EtaxRdSubmission
{
    public const string RuleCode = "ETAX-RD-SUBMIT";
    public const string ErrorSubmit = "SUBMIT_ERROR";
    public const string ErrorNotConfigured = "RD_API_NOT_CONFIGURED";
    public const string ErrorSign = "SIGN_ERROR";
    /// <summary>วันสุดท้ายของเดือนถัดไปที่ต้องนำส่ง (CLAUDE.md §F · ขมธอ.3-2560)</summary>
    public const int DeadlineDay = 15;
    /// <summary>ต่อบริษัทต่อรอบงาน (ที่เหลือรอบถัดไป)</summary>
    public const int BatchPerCompany = 200;

    public enum Outcome { Accepted, Submitted, Rejected, Transient }

    /// <summary>จัดผลตอบกลับของกรม — HTTP สำเร็จ + status "accepted" = รับแล้ว · สำเร็จอย่างอื่น = ส่งถึงแล้วรอผล · 4xx (ยกเว้น 408/429) = กรมปฏิเสธ (แก้ข้อมูลก่อน) ·
    /// ที่เหลือ (5xx · 408 · 429 · เครือข่าย) = ชั่วคราว ลองใหม่ได้</summary>
    public static Outcome Classify(bool httpSuccess, int httpStatus, string? rdStatus)
    {
        if (httpSuccess) return string.Equals(rdStatus, "accepted", StringComparison.OrdinalIgnoreCase) ? Outcome.Accepted : Outcome.Submitted;
        if (httpStatus is >= 400 and < 500 && httpStatus is not (408 or 429)) return Outcome.Rejected;
        return Outcome.Transient;
    }

    /// <summary>สถานะที่ประทับตามผล — <c>SubmittedAt</c> มีค่าเฉพาะ Accepted/Submitted (กรมตอบรับจริง)</summary>
    public static (EtaxStatus Status, bool StampSubmittedAt) StatusFor(Outcome o) => o switch
    {
        Outcome.Accepted => (EtaxStatus.Accepted, true),
        Outcome.Submitted => (EtaxStatus.Submitted, true),
        Outcome.Rejected => (EtaxStatus.Rejected, false),
        _ => (EtaxStatus.Error, false),
    };

    /// <summary>นำส่งได้ไหม: ลงนามแล้ว · หรือส่งล้มทางเทคนิค/ยังไม่ได้ตั้งค่า (ลงนามสำเร็จแล้ว — มีลายเซ็น) · ลงนามล้ม/กรมปฏิเสธ/ยกเลิก = ไม่</summary>
    public static bool CanSubmit(EtaxStatus status, DateTime? signedAt, string? digitalSignature, string? errorCode)
    {
        if (status == EtaxStatus.Signed) return true;
        return status == EtaxStatus.Error && signedAt != null && !string.IsNullOrWhiteSpace(digitalSignature)
               && !string.Equals(errorCode, ErrorSign, StringComparison.Ordinal);
    }

    /// <summary>เวลาลองครั้งถัดไป (คิว) — 1 · 3 · 6 · 12 ชม. แล้ววันละครั้ง</summary>
    public static DateTime NextAttemptAt(int attemptsSoFar, DateTime nowUtc) => nowUtc.AddHours(attemptsSoFar switch
    {
        <= 1 => 1,
        2 => 3,
        3 => 6,
        4 => 12,
        _ => 24,
    });

    /// <summary>ช่วงที่งานประจำเดือนทำงาน: วันที่ 1 ถึง <see cref="DeadlineDay"/> ตามเวลาไทย</summary>
    public static bool InSubmissionWindow(DateTime nowUtc) => ThaiDate.CalendarDateUtc(nowUtc).Day <= DeadlineDay;

    /// <summary>เส้นแบ่ง "ใบของเดือนก่อน": วันที่ 1 ของเดือนนี้ตามเวลาไทย (วันที่เอกสารก่อนนี้ = ต้องนำส่งในรอบนี้) · คืนวันตามปฏิทิน</summary>
    public static DateTime CurrentMonthStart(DateTime nowUtc)
    {
        var d = ThaiDate.CalendarDateUtc(nowUtc);
        return new DateTime(d.Year, d.Month, 1, 0, 0, 0, d.Kind);
    }

    /// <summary>กำหนดนำส่งของใบที่ลงวันที่ <paramref name="documentDate"/> = วันที่ 15 ของเดือนถัดไป</summary>
    public static DateTime DeadlineFor(DateTime documentDate)
    {
        var d = ThaiDate.CalendarDateUtc(documentDate);
        return new DateTime(d.Year, d.Month, DeadlineDay, 0, 0, 0, d.Kind).AddMonths(1);
    }

    /// <summary>หัวข้อ/ข้อความแจ้งเมื่อนำส่งไม่สำเร็จ (ถึงทีมภายใน)</summary>
    public static string FailureTitle(string? documentNumber) => $"e-Tax {documentNumber ?? ""} นำส่งกรมสรรพากรไม่สำเร็จ".Replace("  ", " ");

    public static string FailureMessage(string reason, DateTime deadline, Outcome outcome)
        => outcome == Outcome.Rejected
            ? $"กรมสรรพากรปฏิเสธ: {reason} — แก้ข้อมูลเอกสาร/ออกใบใหม่แล้วนำส่งอีกครั้ง (ระบบไม่ลองซ้ำเอง) · กำหนดส่ง {deadline:dd/MM}/{deadline.Year + 543}"
            : $"{reason} — ระบบจะลองนำส่งให้อีกครั้งอัตโนมัติ หรือกด \"ลองส่งใหม่\" ที่หน้า e-Tax · กำหนดส่ง {deadline:dd/MM}/{deadline.Year + 543}";

    /// <summary>สรุปผลงานประจำเดือนต่อบริษัท (กระดิ่ง) — null = ไม่มีอะไรรายงาน</summary>
    public static string? JobSummary(int attempted, int succeeded, int failed, DateTime deadline)
        => attempted == 0 ? null
            : $"นำส่ง e-Tax ของเดือนก่อนต่อกรมสรรพากร: สำเร็จ {succeeded} · ไม่สำเร็จ {failed} จาก {attempted} ฉบับ"
              + (failed > 0 ? $" — ดูรายการที่ไม่สำเร็จที่หน้า Autopilot/e-Tax ก่อนกำหนดส่ง {deadline:dd/MM}/{deadline.Year + 543}" : "");
}
