namespace Accounting.Helpers;

/// <summary>
/// รอบ 257 C-H1 — <b>ด่าน "OA ของร้านเอง" ห้ามเป็น OA กลางของ NextAcc (ตัวเดียว · pure)</b> · ผู้เรียก: <c>LineConfigController.Update</c> / <c>BuildAsync</c>
/// <para>═══ ที่มา ═══ บริษัทผู้ให้บริการ (<c>SiteSettings.PlatformCompanyId</c>) เห็นส่วน "ใช้ LINE OA ของร้านเอง" + Webhook URL ของบริษัท ⇒ ใส่ token/secret ของ OA กลาง
/// แล้ววาง URL บริษัทใน LINE Console = ทับ <c>/api/line-webhook</c> ⇒ บอท ERP · ลิงก์ลูกค้า · คำสั่งกลุ่มรอบ 256 พังทุกผู้เช่า</para>
/// <para>กติกา: (1) token ที่ LINE บอกว่าเป็นบอทกลาง (bot userId ตรง) หรือ token/secret ตรงค่าตั้งของ OA กลาง ⇒ ปฏิเสธ <b>ทุกบริษัท</b> ·
/// (2) บริษัทผู้ให้บริการใส่ token/secret ใหม่ไม่ได้เลย (ใช้ OA กลางอยู่แล้ว) — ล้างค่าเดิม ("") ได้ · หน้าเว็บซ่อนส่วนนี้ตาม <see cref="PlatformNotice"/></para>
/// </summary>
public static class LineOwnOaGuard
{
    public const string CentralOaMessage =
        "รหัสเชื่อมนี้เป็นของ LINE OA กลางของ NextAcc — ใช้เป็น \"OA ของร้านเอง\" ไม่ได้ (ถ้าวาง Webhook ของบริษัทใน OA กลาง ระบบแจ้งเตือนของทุกบริษัทจะหยุดทำงาน) · "
        + "ใส่รหัสเชื่อมจาก LINE OA ของร้านเอง หรือเว้นว่างเพื่อส่งผ่าน LINE ของ NextAcc ตามเดิม";

    public const string PlatformCompanyMessage =
        "บริษัทนี้เป็นบริษัทผู้ให้บริการ — ใช้ LINE OA กลางของ NextAcc อยู่แล้ว ตั้ง \"OA ของร้านเอง\" ไม่ได้ · "
        + "ค่าตั้ง OA กลาง (token/secret/Basic ID) แก้ที่หน้าแอดมินระบบ และห้ามเปลี่ยน Webhook ของ OA กลาง (ต้องชี้ /api/line-webhook)";

    /// <summary>คำอธิบายบนหน้าตั้งค่าของบริษัทผู้ให้บริการ (เซิร์ฟเวอร์ส่ง · หน้าเว็บแสดงอย่างเดียว) · บริษัทอื่น = null</summary>
    public static string? PlatformNotice(bool isPlatformCompany) => isPlatformCompany
        ? "บริษัทนี้เป็นบริษัทผู้ให้บริการ — แจ้งเตือนทาง LINE ส่งผ่าน LINE OA กลางของ NextAcc อยู่แล้ว จึงไม่มีส่วน \"ใช้ LINE OA ของร้านเอง\" · "
          + "ห้ามเปลี่ยน Webhook ของ OA กลางใน LINE Developers (ต้องชี้ /api/line-webhook ไม่ใช่ URL ของบริษัท) มิฉะนั้นบอท ERP · การผูก LINE ของลูกค้า · คำสั่งในกลุ่มของทุกบริษัทจะหยุดทำงาน · "
          + "ค่าตั้ง OA กลางแก้ที่หน้าแอดมินระบบ"
        : null;

    public static bool IsPlatformCompany(Guid companyId, Guid? platformCompanyId) => platformCompanyId is Guid p && p == companyId;

    /// <summary>ตัดสินคำขอแก้ token/secret · null = ผ่าน · <paramref name="newToken"/>/<paramref name="newSecret"/> = ค่าที่จะตั้ง (null/ว่าง = ไม่ได้ตั้งค่าใหม่)</summary>
    public static string? Refusal(bool isPlatformCompany, string? newToken, string? newSecret,
        string? fetchedBotUserId, string? centralBotUserId, string? centralToken, string? centralSecret)
    {
        var setsToken = !string.IsNullOrWhiteSpace(newToken);
        var setsSecret = !string.IsNullOrWhiteSpace(newSecret);
        if (!setsToken && !setsSecret) return null;   // ไม่ตั้งค่าใหม่ (คงเดิม/ล้าง) ⇒ ไม่มีอะไรต้องกัน
        if (setsToken && Same(fetchedBotUserId, centralBotUserId)) return CentralOaMessage;
        if (setsToken && Same(newToken, centralToken)) return CentralOaMessage;
        if (setsSecret && Same(newSecret, centralSecret)) return CentralOaMessage;
        if (isPlatformCompany) return PlatformCompanyMessage;
        return null;
    }

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
}
