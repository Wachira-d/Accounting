using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ค่าตั้ง LINE OA กลางของ NextAcc ที่ใช้งานจริง (ถอดรหัสแล้ว) — ได้จาก <c>INextAccLineConfig.GetAsync</c> ตัวเดียว ·
/// <see cref="Source"/> บอกว่ามาจากหน้าแอดมิน (<see cref="NextAccLineConfigRule.SourceAdmin"/>) หรือ config <c>Line:*</c></summary>
public sealed record NextAccLineSettings(string? AccessToken, string? ChannelSecret, string? BasicId, string? BotUserId, string? GroupId, string Source,
    string? Problem = null)
{
    public bool HasToken => !string.IsNullOrWhiteSpace(AccessToken);
    public bool HasSecret => !string.IsNullOrWhiteSpace(ChannelSecret);
}

/// <summary>ค่าดิบหนึ่งชุด (ถอดรหัสแล้ว) — จากแถว <c>SiteSettings</c> หรือจาก config</summary>
public sealed record NextAccLineValues(string? AccessToken, string? ChannelSecret, string? BasicId, string? BotUserId, string? GroupId);

/// <summary>ผู้รับ LINE ฝั่งพนักงาน + OA ที่ต้องใช้ส่ง</summary>
public sealed record StaffLineRecipient(string LineUserId, bool ViaNextAcc);

/// <summary>รอบ 212 แพ็ก D — กติกาค่าตั้ง LINE OA กลาง (Messaging API) <b>ตัวตั้งตัวเดียว</b> · pure
/// <list type="bullet">
/// <item><see cref="Merge"/>: ค่าในหน้าแอดมินก่อน <b>ทั้งชุด</b> เมื่อมี token · ไม่มี ⇒ config <c>Line:*</c> ทั้งชุด (deployment เดิมที่ตั้งผ่าน env ยังทำงาน) —
/// ไม่ผสมรายช่อง: token ของ channel หนึ่งกับ secret ของอีก channel = ลายเซ็น webhook ไม่มีวันผ่าน</item>
/// <item>ช่องลับ (<see cref="ApplySecret"/>): ว่าง = คงค่าเดิม · ล้างต้องสั่งชัด (<c>clear</c>) — แบบเดียวกับรหัสผ่าน SMTP/LINE Login</item>
/// <item>ช่องไม่ลับ (<see cref="ApplyText"/>): null = คงค่าเดิม · "" = ล้าง · ค่าอื่นต้องผ่านตัวตรวจรูปแบบก่อน</item>
/// <item><see cref="PayslipRecipient"/>: userId ของ LINE แยกตาม provider ⇒ ต้องส่งผ่าน OA ที่ผู้รับผูกไว้</item>
/// </list></summary>
public static class NextAccLineConfigRule
{
    public const string SourceAdmin = "admin";
    public const string SourceConfig = "config";
    public const string SourceNone = "none";

    /// <summary>ข้อความเมื่อทั้งหน้าแอดมินและ config ไม่มี token — ผู้เรียกใช้ประโยคนี้ตัวเดียว</summary>
    public const string NotConfiguredMessage = "ระบบยังไม่ได้ตั้งค่า LINE OA กลางของ NextAcc (ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์ → LINE OA กลาง)";

    /// <summary>รอบ 212 ฝ่ายค้าน (B+D) ข้อ 3: token ในหน้าแอดมินมีอยู่แต่ถอดรหัสไม่ได้ — <b>ไม่ถอยไป config</b> (อาจเป็นคนละ OA ⇒ ข้อความย้าย OA เงียบ ๆ
    /// + ลิงก์ลูกค้าทุกบริษัทกลายเป็น "สลับ OA") ⇒ ถือว่าไม่ได้ตั้ง จนกว่าแอดมินจะตั้งใหม่</summary>
    public const string UndecryptableTokenMessage = "ถอดรหัส Channel Access Token ของ LINE OA กลางไม่ได้ (คีย์เข้ารหัสของระบบถูกเปลี่ยน) — ตั้งใหม่ที่ ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์ → LINE OA กลาง · ระหว่างนี้ไม่ส่งผ่าน OA กลาง (ไม่สลับไปใช้ค่า config)";

    /// <summary>รอบ 212 ฝ่ายค้าน (B+D) ข้อ 5: token กับ secret ต้องตั้งคู่กัน (ทั้งคู่หรือไม่มีทั้งคู่) — ครึ่งชุด = webhook ถูกปฏิเสธ/ลายเซ็นไม่ผ่าน · null = ผ่าน</summary>
    public static string? PairProblem(bool tokenSet, bool secretSet)
        => tokenSet == secretSet ? null
            : tokenSet ? "ต้องตั้ง Channel Secret คู่กับ Channel Access Token (ของ channel เดียวกัน) — ไม่งั้น webhook ของ OA กลางถูกปฏิเสธทุกครั้ง"
                       : "ต้องตั้ง Channel Access Token คู่กับ Channel Secret (ของ channel เดียวกัน) — หรือล้างทั้งคู่";

    private static readonly Regex BotUserIdRe = new(@"^U[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex GroupIdRe = new(@"^[CR][0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <param name="adminTokenUndecryptable">แถวแอดมินมี token ที่เก็บไว้แต่ถอดรหัสไม่ได้ ⇒ ไม่มี token (ไม่ถอยไป config) + <see cref="UndecryptableTokenMessage"/></param>
    public static NextAccLineSettings Merge(NextAccLineValues? admin, NextAccLineValues? config, bool adminTokenUndecryptable = false)
    {
        if (adminTokenUndecryptable)
            return new NextAccLineSettings(null, null, admin == null ? null : CustomerLineLinkRule.NormalizeBasicId(admin.BasicId), T(admin?.BotUserId),
                T(admin?.GroupId), SourceNone, UndecryptableTokenMessage);
        if (admin != null && T(admin.AccessToken) != null) return Build(admin, SourceAdmin);
        if (config != null && T(config.AccessToken) != null) return Build(config, SourceConfig);
        // ไม่มี token ทั้งคู่ ⇒ ส่งไม่ได้ (HasToken=false) · ค่าที่เหลือจากหน้าแอดมินก่อน (ให้หน้าจอเห็นสิ่งที่กรอกไว้)
        var any = admin != null && (T(admin.ChannelSecret) ?? T(admin.BasicId) ?? T(admin.BotUserId) ?? T(admin.GroupId)) != null ? admin : config;
        return any == null ? new NextAccLineSettings(null, null, null, null, null, SourceNone) : Build(any, SourceNone);
    }

    private static NextAccLineSettings Build(NextAccLineValues v, string source)
        => new(T(v.AccessToken), T(v.ChannelSecret), CustomerLineLinkRule.NormalizeBasicId(v.BasicId), T(v.BotUserId), T(v.GroupId), source);

    /// <summary>ช่องลับ: ค่าใหม่ไม่ว่าง ⇒ เข้ารหัสแล้วเก็บ · ว่าง + <paramref name="clear"/> ⇒ ล้าง · ว่าง ⇒ คงค่าเดิม</summary>
    public static string? ApplySecret(string? currentStored, string? input, bool clear, Func<string, string?> protect)
    {
        if (!string.IsNullOrWhiteSpace(input)) return protect(input.Trim());
        return clear ? null : currentStored;
    }

    /// <summary>ช่องไม่ลับ (ผ่านตัวตรวจแล้ว): null ⇒ คงค่าเดิม · ว่าง ⇒ ล้าง · อื่น ⇒ ค่าใหม่</summary>
    public static string? ApplyText(string? current, string? normalizedInput, bool provided)
        => !provided ? current : normalizedInput;

    /// <summary>Basic ID → "@xxxx" · ว่าง = ล้าง (ok) · รูปแบบผิด ⇒ ข้อความเหตุ</summary>
    public static string? ValidateBasicId(string? input, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input)) return null;
        normalized = CustomerLineLinkRule.NormalizeBasicId(input);
        return normalized == null ? "Basic ID ต้องเป็นรูปแบบ @xxxx (ตัวอักษรอังกฤษ/ตัวเลข/._- 2–40 ตัว) — ดูได้ที่ LINE Official Account Manager" : null;
    }

    /// <summary>bot userId: U + 32 hex (ตัวเล็ก) · ว่าง = ล้าง</summary>
    public static string? ValidateBotUserId(string? input, out string? normalized)
    {
        normalized = T(input);
        if (normalized == null) return null;
        return BotUserIdRe.IsMatch(normalized) ? null : "Bot User ID ต้องขึ้นต้นด้วย U ตามด้วยเลขฐาน 16 อีก 32 ตัว (กด \"ทดสอบ\" เพื่อดึงค่าจริงจาก LINE)";
    }

    /// <summary>group/room id: C|R + 32 hex · ว่าง = ล้าง</summary>
    public static string? ValidateGroupId(string? input, out string? normalized)
    {
        normalized = T(input);
        if (normalized == null) return null;
        return GroupIdRe.IsMatch(normalized) ? null : "Group ID ต้องขึ้นต้นด้วย C (กลุ่ม) หรือ R (ห้อง) ตามด้วยเลขฐาน 16 อีก 32 ตัว";
    }

    /// <summary>webhook ที่ต้องวางใน LINE Developers — ฐาน URL ว่าง ⇒ ตัวอย่างให้แทนโดเมนเอง</summary>
    public static string WebhookUrl(string? appBaseUrl)
        => (string.IsNullOrWhiteSpace(appBaseUrl) ? "https://<โดเมนของคุณ>" : appBaseUrl.Trim().TrimEnd('/')) + "/api/line-webhook";

    /// <summary>ผู้รับสลิปเงินเดือนทาง LINE + OA ที่ต้องใช้:
    /// <list type="bullet">
    /// <item><c>Employee.LineId</c> ที่ได้จากการผูก "สลิป 123456" (มีแถว <c>EmployeeLineBindCode.UsedByLineUserId</c> ตรงกัน) ⇒ ผูกกับ OA กลาง (webhook <c>/api/line-webhook</c> ที่เดียวที่รับคำสั่งนี้) ⇒ ส่งผ่าน OA กลาง</item>
    /// <item><c>User.LineUserId</c> (ผูก "ผูก 123456" กับบอท OA กลางเท่านั้น) ⇒ OA กลาง</item>
    /// <item><c>Employee.LineId</c> ที่ HR กรอกเอง (ไม่มีหลักฐานการผูก) ⇒ ไม่รู้ provider ⇒ เส้นเดิม (OA บริษัทถ้าตั้ง ไม่งั้น OA กลาง) —
    /// รอบ 212 ฝ่ายค้าน (B+D) ข้อ 6: ใช้เฉพาะเมื่อไม่มีการผูกที่พิสูจน์ได้ (<c>User.LineUserId</c>) — การผูกจริงชนะค่าที่พิมพ์เอง</item>
    /// </list></summary>
    public static StaffLineRecipient? PayslipRecipient(string? employeeLineId, bool employeeLineIdBoundViaNextAcc, string? userLineId)
    {
        var e = T(employeeLineId);
        if (e != null && employeeLineIdBoundViaNextAcc) return new StaffLineRecipient(e, true);
        if (T(userLineId) is string u) return new StaffLineRecipient(u, true);
        return e == null ? null : new StaffLineRecipient(e, false);
    }
}
