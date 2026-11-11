using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 258-3 (F-H1 · A-L8 · คำตัดสิน 258-3-2) — <b>ลิงก์ดู/ดาวน์โหลด PDF ของเอกสารจากการ์ด LINE</b> (pure · ตัวเดียวของฝั่งออกลิงก์ <c>ContactLineQueueService</c>
/// และฝั่งรับ <c>LineDocumentViewController</c>)
/// <list type="bullet">
/// <item>token = HMAC-SHA256(กุญแจอนุพันธ์จากความลับของเซิร์ฟเวอร์, บริษัท + แถวคิว LINE) — แบบเดียวกับลิงก์อัปโหลด 50 ทวิ ของอีเมลทวง (<see cref="WhtCertUploadLinkPolicy.TokenForQueue"/>):
/// คำนวณซ้ำได้ตอนส่ง/ลองใหม่ (URL เดิม) · ฐานไม่ต้องเก็บ token · ความลับว่าง ⇒ โยน (กฎ #4 C ห้ามถอยไปกุญแจ dev)</item>
/// <item>สิทธิ์ผูก "แถวคิว LINE ที่ส่งเอกสารนั้นจริง" — ฝั่งรับตรวจว่าแถวเป็นของบริษัทใน URL · มีปลายทาง · ไม่ถูกยกเลิก/ลบ · อายุไม่เกิน <see cref="TtlDays"/> ·
/// เอกสารยังออกแล้ว (ยกเลิก/กลับเป็นร่าง ⇒ ลิงก์ตาย) — ลบ/ยกเลิกแถวคิว = เพิกถอนลิงก์</item>
/// <item>ข้อความล้ม = ข้อความเดียวทุกเหตุ (ไม่บอกว่ามีเอกสาร/บริษัทนี้ไหม)</item>
/// </list>
/// </summary>
public static class LineDocumentViewLink
{
    public const string RuleCode = "LINE-DOC-VIEW-LINK";

    /// <summary>อายุลิงก์นับจากวันเข้าคิว (วัน) — ใบเสร็จ/ใบกำกับที่ลูกค้าเก็บไว้เปิดซ้ำได้ตลอดรอบบัญชี</summary>
    public const int TtlDays = 400;

    public const int ViewsPerMinute = 20;
    public const int ViewsPerDay = 300;

    public const string NotFoundMessage = "ลิงก์ไม่ถูกต้องหรือหมดอายุ — กรุณาติดต่อผู้ส่งเอกสาร";

    /// <summary>สถานะแถวคิวที่ลิงก์ยังใช้ได้ (ยกเลิก/ข้าม ⇒ ไม่ได้ส่ง ⇒ ไม่มีลิงก์)</summary>
    public static bool RowStatusAllows(string? status)
        => status is ContactLineChannel.StatusPending or ContactLineChannel.StatusSending or ContactLineChannel.StatusSent;

    public static byte[] DeriveKey(string? serverSecret)
    {
        if (string.IsNullOrWhiteSpace(serverSecret))
            throw new InvalidOperationException("ไม่ได้ตั้งความลับของเซิร์ฟเวอร์ (Jwt:Secret) — ออกลิงก์ดูเอกสารจากการ์ด LINE ไม่ได้");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(serverSecret), Encoding.UTF8.GetBytes("line-doc-view-token-v1"));
    }

    /// <summary>token ของแถวคิวหนึ่ง (hex 64 ตัว · ตัวเล็ก)</summary>
    public static string Token(byte[] key, Guid companyId, Guid lineQueueId)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"linedoc:{companyId:N}:{lineQueueId:N}"))).ToLowerInvariant();

    /// <summary>รูป token ที่รับก่อนแตะฐาน — hex ตัวเล็ก 64 ตัว</summary>
    public static bool LooksValid(string? token)
        => token is { Length: 64 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>ยืนยัน token แบบเวลาคงที่ (คำนวณใหม่จากกุญแจ — ไม่มีค่าเก็บในฐาน)</summary>
    public static bool Matches(byte[] key, Guid companyId, Guid lineQueueId, string? token)
    {
        if (!LooksValid(token)) return false;
        var expected = Encoding.ASCII.GetBytes(Token(key, companyId, lineQueueId));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(token!));
    }

    /// <summary>ลิงก์ยังไม่หมดอายุ (นับจากวันเข้าคิว)</summary>
    public static bool Fresh(DateTime queuedAtUtc, DateTime nowUtc) => nowUtc <= queuedAtUtc.AddDays(TtlDays);

    /// <summary>URL สาธารณะ (origin จาก <c>SystemBaseUrl</c> · ไม่ประกอบโดเมนเอง) · origin ว่าง ⇒ null (การ์ดไม่มีปุ่ม — ไม่ใช่ลิงก์เสีย)</summary>
    public static string? Url(string? origin, Guid companyId, Guid lineQueueId, string token)
        => string.IsNullOrWhiteSpace(origin) ? null
            : $"{origin.Trim().TrimEnd('/')}/api/companies/{companyId:D}/line-doc/{lineQueueId:D}/{token}";
}
