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

    /// <summary>ค่าที่ส่งมาในช่อง token/secret หมายถึงอะไร (รอบ 257 ฝ่ายค้าน L6 — ช่องว่างล้วน = ล้าง เหมือน "")</summary>
    public enum InputKind { Keep, Clear, Set }

    public static InputKind Classify(string? value)
        => value == null ? InputKind.Keep : string.IsNullOrWhiteSpace(value) ? InputKind.Clear : InputKind.Set;

    public const string UnverifiedMessage =
        "ตรวจรหัสเชื่อมกับ LINE ไม่ได้ (LINE ไม่ตอบ หรือรหัสเชื่อมไม่ถูกต้อง) — ยังไม่ได้บันทึก กรุณาตรวจรหัสเชื่อมแล้วลองใหม่";

    public const string CentralValueMessage =
        "Basic ID / Group ID นี้เป็นของ LINE OA กลางของ NextAcc — ใช้เป็นค่าของบริษัทไม่ได้ (ปุ่มเพิ่มเพื่อนของลูกค้าและแจ้งเตือนทีมจะไปที่ OA กลางแทน) · "
        + "ใส่ค่าจาก LINE OA ของร้านเอง หรือเว้นว่าง";

    /// <summary>รอบ 257 ฝ่ายค้าน L7: Basic ID / Group ID ทีมที่จะตั้ง ห้ามเป็นค่าของ OA กลาง (ทุกบริษัท) · null = ผ่าน · ค่าว่าง/null = ไม่ได้ตั้งค่าใหม่</summary>
    /// <param name="storedBasicId">รอบ 257 ปิดท้าย MED-6: ค่าที่บันทึกไว้แล้วของบริษัท — ค่าที่ส่งมา<b>เท่ากับค่าที่เก็บอยู่</b> = ไม่ได้ตั้งค่าใหม่ ⇒ ไม่ปฏิเสธ
    /// (เดิมบริษัทผู้ให้บริการที่มีค่าของ OA กลางค้างจากก่อนมีด่าน กด "ล้างรหัสเชื่อม" ไม่ได้ — หน้าเว็บส่งค่าเดิมกลับมาแล้วถูกปฏิเสธทั้งคำขอ) ·
    /// ค่าที่<b>ต่างจากที่เก็บอยู่</b>และเป็นของ OA กลาง ⇒ ปฏิเสธเหมือนเดิม</param>
    /// <param name="storedGroupId">เช่นเดียวกันสำหรับ Group ID ทีม</param>
    public static string? CentralValueRefusal(string? normalizedBasicId, string? groupId, string? centralBasicId, IEnumerable<string>? centralGroupIds,
        string? storedBasicId = null, string? storedGroupId = null)
    {
        static string Basic(string v) => v.Trim().TrimStart('@');
        var basicUnchanged = !string.IsNullOrWhiteSpace(normalizedBasicId) && !string.IsNullOrWhiteSpace(storedBasicId)
            && string.Equals(Basic(normalizedBasicId), Basic(storedBasicId), StringComparison.OrdinalIgnoreCase);
        if (!basicUnchanged && !string.IsNullOrWhiteSpace(normalizedBasicId) && !string.IsNullOrWhiteSpace(centralBasicId)
            && string.Equals(Basic(normalizedBasicId), Basic(centralBasicId), StringComparison.OrdinalIgnoreCase))
            return CentralValueMessage;
        var groupUnchanged = !string.IsNullOrWhiteSpace(groupId) && !string.IsNullOrWhiteSpace(storedGroupId)
            && string.Equals(groupId.Trim(), storedGroupId.Trim(), StringComparison.Ordinal);
        if (!groupUnchanged && !string.IsNullOrWhiteSpace(groupId) && centralGroupIds != null
            && centralGroupIds.Any(g => string.Equals(g?.Trim(), groupId.Trim(), StringComparison.Ordinal)))
            return CentralValueMessage;
        return null;
    }

    /// <summary>รอบ 257 ฝ่ายค้าน M1: บริษัทผู้ให้บริการที่ยังมี token/secret ค้างจากก่อนรอบนี้ — หน้าซ่อนส่วน OA ของร้าน แต่ต้องยังเห็นสถานะ + ปุ่มล้าง ·
    /// null = ไม่มีอะไรค้าง/ไม่ใช่บริษัทผู้ให้บริการ</summary>
    public static string? PlatformLegacyNotice(bool isPlatformCompany, bool hasAccessToken, bool hasChannelSecret)
        => isPlatformCompany && (hasAccessToken || hasChannelSecret)
            ? "⚠️ บริษัทนี้ยังมีรหัสเชื่อม/รหัสลับ “OA ของร้านเอง” ที่บันทึกไว้ก่อนหน้า ("
              + (hasAccessToken && hasChannelSecret ? "รหัสเชื่อม + รหัสลับ" : hasAccessToken ? "รหัสเชื่อม" : "รหัสลับ")
              + ") — บริษัทผู้ให้บริการต้องไม่ใช้ OA กลางเป็น OA ของตัวเอง กรุณากด “ล้างรหัสเชื่อมและรหัสลับที่บันทึกไว้” "
              + "แล้วตรวจใน LINE Developers ว่า Webhook ของ OA กลางยังชี้ /api/line-webhook"
            : null;

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
        // รอบ 257 ฝ่ายค้าน L7: ถาม LINE ไม่ได้ว่าเป็นบอทไหน ⇒ ไม่บันทึก token ที่ยังไม่ได้ตรวจ (อาจเป็นบอทกลาง)
        if (setsToken && string.IsNullOrWhiteSpace(fetchedBotUserId)) return UnverifiedMessage;
        return null;
    }

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
}
