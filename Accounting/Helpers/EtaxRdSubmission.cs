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
    /// <summary>ฝ่ายค้าน 259-D H2: กรมไม่ยอมรับการเชื่อมต่อ (401/403) หรือ URL ผิด (404/405) — ปัญหาการตั้งค่าของเรา ไม่ใช่เนื้อเอกสาร</summary>
    public const string ErrorAuth = "RD_AUTH";
    /// <summary>วันสุดท้ายของเดือนถัดไปที่ต้องนำส่ง (CLAUDE.md §F · ขมธอ.3-2560)</summary>
    public const int DeadlineDay = 15;
    /// <summary>ต่อบริษัทต่อรอบงาน (ที่เหลือรอบถัดไป)</summary>
    public const int BatchPerCompany = 200;

    /// <summary><c>ConfigError</c> (ฝ่ายค้าน H2) = คีย์/URL ของกรมผิด — ลองใหม่ได้หลังแก้ตั้งค่า · หยุดชุดที่เหลือ · แจ้งครั้งเดียว</summary>
    public enum Outcome { Accepted, Submitted, Rejected, Transient, ConfigError }

    /// <summary>จัดผลตอบกลับของกรม — HTTP สำเร็จ + status "accepted" = รับแล้ว · สำเร็จอย่างอื่น = ส่งถึงแล้วรอผล ·
    /// 400/409/422 = กรมปฏิเสธเนื้อเอกสาร (แก้ข้อมูลก่อน) · 401/403/404/405 = ตั้งค่าการเชื่อมต่อผิด (ฝ่ายค้าน 259-D H2 — เดิมนับเป็นปฏิเสธ ⇒ ทั้งชุดตายถาวร) ·
    /// ที่เหลือ (5xx · 408 · 429 · 4xx อื่น · เครือข่าย) = ชั่วคราว ลองใหม่ได้</summary>
    public static Outcome Classify(bool httpSuccess, int httpStatus, string? rdStatus)
    {
        if (httpSuccess) return string.Equals(rdStatus, "accepted", StringComparison.OrdinalIgnoreCase) ? Outcome.Accepted : Outcome.Submitted;
        if (httpStatus is 400 or 409 or 422) return Outcome.Rejected;
        if (httpStatus is 401 or 403 or 404 or 405) return Outcome.ConfigError;
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

    /// <summary>ฝ่ายค้าน 259-D M4: เวลาลองครั้งถัดไปของใบที่<b>เลยกำหนดส่งแล้ว</b> = วันละครั้ง (งานยังลองหลังวันที่ 15 · ไม่ค้างไปเดือนหน้า)</summary>
    public static DateTime NextAttemptAt(int attemptsSoFar, DateTime nowUtc, DateTime documentDate)
        => IsOverdue(documentDate, nowUtc) ? nowUtc.AddHours(24) : NextAttemptAt(attemptsSoFar, nowUtc);

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

    /// <summary>ฝ่ายค้าน 259-D M3: เดือนภาษีที่ต้องนำส่งของใบ = เดือนของ<b>วันที่เอกสาร</b> (ไม่ใช่วันที่สร้าง/ลงนาม e-Tax) — ตัวเดียวของงาน · ข้อความ · กระดิ่ง · Autopilot</summary>
    public static DateTime FilingMonthOf(DateTime documentDate)
    {
        var d = ThaiDate.CalendarDateUtc(documentDate);
        return new DateTime(d.Year, d.Month, 1, 0, 0, 0, d.Kind);
    }

    /// <summary>ฝ่ายค้าน 259-D M4: เลยกำหนดนำส่งแล้ว (วันนี้ตามเวลาไทยเลยวันที่ 15 ของเดือนถัดจากเดือนเอกสาร)</summary>
    public static bool IsOverdue(DateTime documentDate, DateTime nowUtc)
        => ThaiDate.CalendarDateUtc(nowUtc).Date > DeadlineFor(documentDate).Date;

    /// <summary>ป้ายกำหนดส่งบนข้อความ/การ์ด — "กำหนดส่ง 15/12/2569" หรือ "เลยกำหนดส่ง 15/12/2569 แล้ว"</summary>
    public static string DeadlineLabel(DateTime documentDate, DateTime nowUtc)
    {
        var dl = DeadlineFor(documentDate);
        return IsOverdue(documentDate, nowUtc) ? $"เลยกำหนดส่ง {dl:dd/MM}/{dl.Year + 543} แล้ว" : $"กำหนดส่ง {dl:dd/MM}/{dl.Year + 543}";
    }

    /// <summary>กำหนดนำส่งของใบที่ลงวันที่ <paramref name="documentDate"/> = วันที่ 15 ของเดือนถัดไป</summary>
    public static DateTime DeadlineFor(DateTime documentDate)
    {
        return FilingMonthOf(documentDate).AddDays(DeadlineDay - 1).AddMonths(1);
    }

    /// <summary>หัวข้อ/ข้อความแจ้งเมื่อนำส่งไม่สำเร็จ (ถึงทีมภายใน)</summary>
    public static string FailureTitle(string? documentNumber) => $"e-Tax {documentNumber ?? ""} นำส่งกรมสรรพากรไม่สำเร็จ".Replace("  ", " ");

    public static string FailureMessage(string reason, DateTime documentDate, DateTime nowUtc, Outcome outcome) => outcome switch
    {
        Outcome.Rejected => $"กรมสรรพากรปฏิเสธ: {reason} — แก้ข้อมูลเอกสาร/ออกใบใหม่แล้วนำส่งอีกครั้ง (ระบบไม่ลองซ้ำเอง) · {DeadlineLabel(documentDate, nowUtc)}",
        Outcome.ConfigError => $"กรมสรรพากรไม่ยอมรับการเชื่อมต่อ ({reason}) — ตรวจ RD API Key/Secret/URL ที่หน้าตั้งค่า e-Tax แล้วกด \"ลองส่งใหม่\" (เอกสารไม่ได้ผิด · ระบบหยุดส่งฉบับอื่นในรอบนี้) · {DeadlineLabel(documentDate, nowUtc)}",
        _ => $"{reason} — ระบบจะลองนำส่งให้อีกครั้งอัตโนมัติ (ทุกวันหลังเลยกำหนด) หรือกด \"ลองส่งใหม่\" ที่หน้า e-Tax · {DeadlineLabel(documentDate, nowUtc)}",
    };

    /// <summary>สรุปผลงานประจำเดือนต่อบริษัท (กระดิ่ง) — null = ไม่มีอะไรรายงาน</summary>
    public static string? JobSummary(int attempted, int succeeded, int failed, DateTime deadline, string? stoppedReason = null)
        => attempted == 0 ? null
            : $"นำส่ง e-Tax ของเดือนก่อนต่อกรมสรรพากร: สำเร็จ {succeeded} · ไม่สำเร็จ {failed} จาก {attempted} ฉบับ"
              + (failed > 0 ? $" — ดูรายการที่ไม่สำเร็จที่หน้า Autopilot/e-Tax ก่อนกำหนดส่ง {deadline:dd/MM}/{deadline.Year + 543}" : "")
              + (stoppedReason != null ? $" · หยุดส่งฉบับที่เหลือในรอบนี้: {stoppedReason}" : "");

    /// <summary>ฝ่ายค้าน 259-D H1 (F2 ข้อ 9): แถวที่ก่อนรอบ 259 ถูกประทับ "Submitted" + เลขส่ง <c>ERR-…</c> ตอนกรมตอบไม่สำเร็จ ⇒ คืนเป็น Error (ไม่มี SubmittedAt/เลขส่ง)
    /// ให้งาน/ปุ่มลองส่งใหม่ได้ · แถวที่กรมตอบ 2xx แต่ระบบแต่งเลข <c>RD-yyyyMMddHHmmss</c> ⇒ ล้างเฉพาะเลขส่ง (คงสถานะ) · แถวเลขจริงไม่ถูกแตะ · idempotent</summary>
    public static IReadOnlyList<string> LegacyStampRepairSql() => new[]
    {
        $"""UPDATE "EtaxInvoices" SET "Status" = {(int)EtaxStatus.Error}, "SubmittedAt" = NULL, "SubmissionId" = NULL, "ErrorCode" = '{ErrorSubmit}', "ErrorMessage" = 'ระบบเคยประทับว่านำส่งแล้วทั้งที่กรมสรรพากรตอบไม่สำเร็จ (ก่อนรอบ 259) — ล้างสถานะแล้ว รอนำส่งใหม่', "RdNextAttemptAt" = NULL WHERE "Status" = {(int)EtaxStatus.Submitted} AND "SubmissionId" LIKE 'ERR-%';""",
        """UPDATE "EtaxInvoices" SET "SubmissionId" = NULL WHERE "SubmissionId" ~ '^RD-[0-9]{14}$';""",
    };
}
