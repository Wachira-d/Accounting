namespace Accounting.Helpers;

/// <summary>
/// ระดับเตือนวันหมดอายุใบรับรอง SSL/TLS ของโดเมนแพลตฟอร์ม — ตัวตัดสินเดียว (job ตรวจรายวัน · แถบเตือนบนหน้าเว็บ · แจ้งเตือน/อีเมล)
///
/// <para><b>ที่มา (เจ้าของรายงาน 2026-10-19):</b> เว็บเข้าไม่ได้ทั้งระบบ ("ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ได้") สาเหตุคือ cert หมดอายุ —
/// เบราว์เซอร์ตัดการเชื่อมต่อก่อนถึงแอป ⇒ <b>แถบเตือนตอนหมดแล้วไม่มีใครเห็น</b> (หน้าเว็บโหลดไม่ขึ้น). ทางที่ใช้ได้จริงคือเตือน
/// <b>ล่วงหน้า</b> บนหน้าเว็บ + อีเมล (อีเมลยังถึงแม้เว็บล่ม)</para>
/// </summary>
public static class TlsCertificateExpiry
{
    public enum Level
    {
        /// <summary>ยังไม่เคยตรวจ / ตรวจไม่ได้ (ไม่มี AppBaseUrl https · ต่อไม่ได้) — ไม่รู้ ≠ ปกติ</summary>
        Unknown = 0,
        Ok = 1,
        /// <summary>เหลือ ≤ 30 วัน</summary>
        Notice = 2,
        /// <summary>เหลือ ≤ 14 วัน</summary>
        Warning = 3,
        /// <summary>เหลือ ≤ 7 วัน</summary>
        Urgent = 4,
        /// <summary>เหลือ ≤ 1 วัน</summary>
        Critical = 5,
        Expired = 6,
    }

    /// <summary>จำนวนวันที่เหลือ (ปัดลง · ติดลบ = หมดไปแล้วกี่วัน)</summary>
    public static int DaysLeft(DateTime notAfterUtc, DateTime nowUtc)
        => (int)Math.Floor((notAfterUtc - nowUtc).TotalDays);

    public static Level LevelFor(DateTime? notAfterUtc, DateTime nowUtc)
    {
        if (notAfterUtc is not DateTime na) return Level.Unknown;
        if (na <= nowUtc) return Level.Expired;
        var d = DaysLeft(na, nowUtc);
        return d switch
        {
            <= 1 => Level.Critical,
            <= 7 => Level.Urgent,
            <= 14 => Level.Warning,
            <= 30 => Level.Notice,
            _ => Level.Ok,
        };
    }

    /// <summary>ต้องแจ้งเตือน (in-app + อีเมล) รอบนี้ไหม — แจ้งครั้งเดียวต่อการ "ข้ามขั้น" ที่แย่ลง · ต่ออายุแล้ว (ระดับกลับเป็น Ok) รีเซ็ต</summary>
    /// <param name="lastNotified">ระดับที่แจ้งไปแล้วล่าสุด (null = ยังไม่เคย)</param>
    public static bool ShouldNotify(Level current, Level? lastNotified)
        => current >= Level.Notice && (lastNotified is null || current > lastNotified.Value);

    /// <summary>ต่อท้ายแจ้งเตือน/แถบเมื่อระบบยังไม่ได้ตั้งอีเมล — ตอน cert หมดจริงเว็บเข้าไม่ได้ ช่องเดียวที่ถึงคนคืออีเมล</summary>
    public const string EmailNotConfiguredNote =
        "ระบบยังไม่ได้ตั้งค่าอีเมล (ตั้งค่าระบบ → อีเมลระบบ) — ถ้าใบรับรองหมดจนเว็บเข้าไม่ได้ จะไม่มีอีเมลเตือนถึงผู้ดูแล";

    /// <summary>สีของแถบ (danger/warning/info/none) — ตัวเดียวที่หน้าเว็บอ่าน (ไม่คิดซ้ำใน JS)</summary>
    public static string Severity(Level level, bool hasCheckError) => level switch
    {
        Level.Expired or Level.Critical or Level.Urgent => "danger",
        Level.Warning or Level.Notice => "warning",
        _ => hasCheckError ? "info" : "none",
    };

    /// <summary>ล้าง "แจ้งขั้นนี้แล้ว" เมื่อเป็นใบใหม่ — โดเมนเปลี่ยน หรือวันหมดอายุเลื่อนออกไป (ต่ออายุแล้ว) ·
    /// ไม่งั้นใบอายุสั้นที่ไม่เคยกลับขึ้นไปเกิน 30 วัน (เช่นใบ 6 วัน) จะค้างระดับเดิมแล้วไม่เตือนอีกเลย (ฝ่ายค้าน P3)</summary>
    public static bool IsNewCertificate(string? previousHost, DateTime? previousNotAfterUtc, string host, DateTime? notAfterUtc)
        => !string.Equals(previousHost, host, StringComparison.OrdinalIgnoreCase)
           || (previousNotAfterUtc is { } p && notAfterUtc is { } n && n > p);

    /// <summary>ข้อความภาษาไทยของระดับนี้ (null = ไม่ต้องแสดงแถบ)</summary>
    /// <param name="issuer">ผู้ออกใบ (CA) — บอกในข้อความว่าต้องไปต่ออายุที่ไหน (เช่น ใบ Let's Encrypt ใกล้หมด = ตัวต่ออายุอัตโนมัติไม่ทำงาน)</param>
    public static string? Message(Level level, DateTime? notAfterUtc, DateTime nowUtc, string? host, string? checkError, string? issuer = null)
    {
        var where = string.IsNullOrWhiteSpace(host) ? "โดเมนของระบบ" : host;
        var when = (notAfterUtc is DateTime na ? ThaiDate(na) : "") + (string.IsNullOrWhiteSpace(issuer) ? "" : $" (ผู้ออก {issuer.Trim()})");
        return level switch
        {
            Level.Unknown => string.IsNullOrWhiteSpace(checkError) ? null
                : $"ตรวจวันหมดอายุใบรับรอง SSL ของ {where} ไม่ได้ — {checkError}",
            Level.Ok => null,
            Level.Expired => $"ใบรับรอง SSL ของ {where} หมดอายุแล้วเมื่อ {when} — ผู้ใช้ส่วนใหญ่เข้าเว็บไม่ได้ ต่ออายุแล้วผูกใน IIS ทันที",
            _ => $"ใบรับรอง SSL ของ {where} จะหมดอายุ {when} (อีก {Math.Max(0, DaysLeft(notAfterUtc!.Value, nowUtc))} วัน) — "
                 + "เมื่อหมดแล้วผู้ใช้จะเข้าเว็บไม่ได้เลย ต่ออายุแล้วผูกใน IIS ก่อนวันนั้น",
        };
    }

    /// <summary>ผลตรวจล่าสุดเก่ากว่านี้ = job ไม่ได้รัน (แอปหยุด/ล็อกค้าง) — ตัวเลขวันในแถบอาจไม่จริงแล้ว</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(3);

    /// <summary>ผลตรวจค้างเก่าเกินไปไหม (ยังไม่เคยตรวจ = ไม่ค้าง — job เพิ่งเริ่ม)</summary>
    public static bool IsStale(DateTime? checkedAtUtc, DateTime nowUtc)
        => checkedAtUtc is { } c && nowUtc - c > StaleAfter;

    /// <summary>ข้อความต่อท้ายเมื่อผลตรวจค้าง</summary>
    public static string StaleNote(DateTime checkedAtUtc)
        => $"ตรวจใบรับรองครั้งล่าสุดเมื่อ {ThaiDate(checkedAtUtc)} — งานตรวจรายวันไม่ได้รันตั้งแต่นั้น (ดูหน้างานเบื้องหลัง)";

    /// <summary>วันที่แสดงแบบไทย dd/MM/พ.ศ. HH:mm (เวลาไทย) — วันที่ผ่านตัวกลาง <see cref="Accounting.Helpers.ThaiDate.ToThaiDisplayString"/></summary>
    public static string ThaiDate(DateTime utc)
        => Accounting.Helpers.ThaiDate.ToThaiDisplayString(utc) + " "
           + utc.AddHours(7).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " น.";

    /// <summary>host + port ที่ต้องตรวจจาก AppBaseUrl — null เมื่อไม่ใช่ https (ไม่มีใบรับรองให้ตรวจ)</summary>
    public static (string Host, int Port)? Target(string? appBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(appBaseUrl)
            || !Uri.TryCreate(appBaseUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return (uri.Host, uri.IsDefaultPort ? 443 : uri.Port);
    }
}
