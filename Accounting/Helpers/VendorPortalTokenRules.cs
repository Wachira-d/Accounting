namespace Accounting.Helpers;

/// <summary>รอบ 259-3 (ทีม E-H2 · คำตัดสิน 259-3-1) — <b>ตัวตัดสินเดียวของลิงก์พอร์ทัลผู้ขาย</b> (<c>VendorPortalToken</c>)
/// <para>═══ ที่มา ═══ หน้า vendor-portal-admin ออกลิงก์ได้อย่างเดียว ไม่มีรายการ ไม่มีปุ่มเพิกถอน (ลิงก์หลุด = ทางตัน 90 วัน) · มีบทบาท "Customer"
/// ซึ่งเป็นประตูที่สองของลูกค้า (ธงสิทธิ์ของพอร์ทัลลูกค้าไม่เกี่ยว — ปิดสิทธิ์ที่พอร์ทัลลูกค้าแล้วลิงก์นี้ยังเปิด) · ช่องอีเมลผู้รับเก็บอย่างเดียวไม่ส่ง</para>
/// <para>═══ กติกา ═══ ลูกค้ามีบ้านเดียว = พอร์ทัลลูกค้า (customer-portal · ธงสิทธิ์ 4 ตัว · ชำระผ่านหน้าชำระของเอกสาร) ⇒ ออกลิงก์ใหม่ได้เฉพาะ <see cref="RoleVendor"/> ·
/// ลิงก์ Customer ที่ออกไว้ก่อนรอบนี้<b>ยังใช้ได้จนหมดอายุ</b> (ไม่ทำลายข้อมูล) และอยู่ในรายการให้เพิกถอนได้</para></summary>
public static class VendorPortalTokenRules
{
    public const string RuleCode = "VENDOR-PORTAL-259";
    public const string RoleVendor = "Vendor";
    public const string RoleCustomerLegacy = "Customer";

    public const string StatusActive = "Active";
    public const string StatusExpired = "Expired";
    public const string StatusRevoked = "Revoked";

    /// <summary>เหตุที่ออกลิงก์บทบาทนี้ไม่ได้ — null = ได้ (ว่าง = ผู้ขาย ตามค่าตั้งต้นเดิม)</summary>
    public static string? IssueRefusal(string? role)
    {
        var r = string.IsNullOrWhiteSpace(role) ? RoleVendor : role.Trim();
        if (string.Equals(r, RoleVendor, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(r, RoleCustomerLegacy, StringComparison.OrdinalIgnoreCase))
            return "ลูกค้าใช้ \"พอร์ทัลลูกค้า\" (เมนู ขาย → พอร์ทัลลูกค้า) — ที่นั่นเปิด/ปิดสิทธิ์ได้ทีละเรื่องและลูกค้าชำระเงินได้ · หน้านี้ออกลิงก์ให้ผู้ขายเท่านั้น";
        return $"ไม่รู้จักบทบาท \"{r}\" — ออกลิงก์ได้เฉพาะผู้ขาย";
    }

    /// <summary>สถานะของลิงก์ ณ เวลา <paramref name="nowUtc"/> — เพิกถอนชนะหมดอายุ</summary>
    public static string StatusOf(DateTime? revokedAt, DateTime expiresAt, DateTime nowUtc)
        => revokedAt != null ? StatusRevoked : expiresAt < nowUtc ? StatusExpired : StatusActive;

    public static string StatusLabel(string status) => status switch
    {
        StatusActive => "ใช้งานได้",
        StatusExpired => "หมดอายุ",
        StatusRevoked => "เพิกถอนแล้ว",
        _ => status,
    };

    public static string RoleLabel(string? role)
        => string.Equals(role, RoleCustomerLegacy, StringComparison.OrdinalIgnoreCase)
            ? "ลูกค้า (ลิงก์เดิม — ใช้พอร์ทัลลูกค้าแทน)" : "ผู้ขาย";

    /// <summary>เพิกถอนได้เฉพาะลิงก์ที่ยังใช้งานได้ (หมดอายุแล้วใช้ไม่ได้อยู่แล้ว · เพิกถอนแล้วซ้ำไม่มีผล)</summary>
    public static bool CanRevoke(string status) => status == StatusActive;

    /// <summary>รอบ 259-3 ฝ่ายค้าน M3: คำเชิญเข้าคิว EmailQueue (โควตา/เพดานอีเมล · ผู้ส่งในนามบริษัท) — ไม่ส่งตรงจากคำขอ ·
    /// ลิงก์ในอีเมลออกตอนส่ง (คิว/บันทึกเก็บที่หมาย <see cref="LinkPlaceholder"/> ไม่เก็บ token ดิบ — ระบบเก็บแค่ hash)</summary>
    public const string QueueEntityType = "VendorPortalInvite";
    public const string LinkPlaceholder = "{VendorPortalLink}";

    /// <summary>อีเมลผู้รับคำเชิญ: ว่าง = ไม่ส่ง (null, null) · อีเมลเดียวเท่านั้น (ห้ามคั่นหลายรายด้วย , ; หรือช่องว่าง) · รูปผิด = (null, เหตุ)</summary>
    public static (string? Email, string? Problem) InviteEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var t = raw.Trim();
        if (t.IndexOfAny(new[] { ',', ';', ' ', '\t', '\r', '\n', '<', '>' }) >= 0 || t.Count(c => c == '@') != 1)
            return (null, "ใส่อีเมลผู้รับได้ทีละหนึ่งอีเมล (รูปแบบ name@example.com)");
        var n = PortalMagicLinkPolicy.NormalizeEmail(t);
        var at = n?.IndexOf('@') ?? -1;
        return n == null || at < 1 || n.IndexOf('.', at) < 0 ? (null, "รูปแบบอีเมลผู้รับไม่ถูกต้อง") : (n, null);
    }

    /// <summary>ผลคำเชิญ — ข้อความถึงผู้ใช้ (เข้าคิว ≠ ส่งถึงแล้ว · F2 #3)</summary>
    public static string InviteNote(bool requested, bool queued)
        => !requested ? "ไม่ได้ระบุอีเมลผู้รับ — คัดลอกลิงก์ส่งให้ผู้ขายเอง"
            : queued ? "เข้าคิวส่งอีเมลเชิญแล้ว (ลิงก์ในอีเมลเป็นลิงก์แยกของผู้รับ) — ดูผลการส่งที่หน้าตารางส่งเอกสาร › คิว/ประวัติส่ง"
            : "เข้าคิวส่งอีเมลไม่สำเร็จ — คัดลอกลิงก์ส่งให้ผู้ขายเอง";

    /// <summary>ตอนส่งจากคิว: ลิงก์ต้นทางยังใช้งานได้ไหม — null = ส่ง · ข้อความ = ข้ามพร้อมเหตุ</summary>
    public static string? InviteSkipReason(string? sourceStatus)
        => sourceStatus == StatusActive ? null
            : sourceStatus == null ? "ไม่พบลิงก์พอร์ทัลผู้ขายของคำเชิญนี้ — ไม่ส่ง"
            : $"ลิงก์พอร์ทัลผู้ขาย{StatusLabel(sourceStatus)} — ไม่ส่งคำเชิญ";
}
