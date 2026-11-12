using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินของ "คำขอเชื่อมคู่ค้า NextAcc" รอบ 264** (คำตัดสิน 264-1-1…264-1-8 · <c>erp-review/2026-09-29/DECISIONS.md</c>) · pure
///
/// <para>═══ ที่มา ═══ บริษัท A ส่งคำขอเชื่อมด้วยเลขภาษี + สาขา แต่บริษัท B ไม่เห็นอะไรเลย — <c>InvitePartnerAsync</c> เดิมค้นเฉพาะบริษัทที่เปิด
/// "ให้คู่ค้าค้นเจอ" (<c>Company.PartnerDiscoverable</c> · ค่าตั้งต้นปิด) ⇒ B ที่ไม่ได้เปิด (เกือบทุกบริษัท) = ไม่พบ ⇒ ไม่เก็บ ไม่แจ้ง (คำตัดสิน 257-3-3) ·
/// ถึงเปิดแล้วก็แจ้งแค่แถวกระดิ่งของเจ้าของ (ไม่ผ่านตารางแจ้งเตือน · ไม่มีอีเมล/LINE · กระดิ่งกดไปหน้าใดไม่ได้)</para>
///
/// <para>═══ กติกา ═══
/// <list type="number">
/// <item>คำขอถึงบริษัทที่ตรงเลขภาษี + สาขา<b>เสมอ</b> (ไม่ดู PartnerDiscoverable) — B ตัดสินเองด้วยการกดยอมรับ/ปฏิเสธ · ยอมรับ = เปิดการเชื่อม<b>กับผู้ขอรายนี้รายเดียว</b>
/// (ไม่เปิดค้นเจอทั้งระบบ) · ข้อมูลของ B ไม่ถึง A จนกว่า B ยอมรับ (PDPA ม.19/ม.24 — ความยินยอม/การทำตามคำขอของเจ้าของข้อมูล)</item>
/// <item>คำตอบถึง A เหมือนกันทุกกรณี (<see cref="PartnerCompanyMatch.InviteAckMessage"/>) · A เห็นคำขอของตัวเองเป็น "รอคู่ค้าตอบ" → "เชื่อมแล้ว" / "หมดอายุ"
/// เท่านั้น — การปฏิเสธและการ "ไม่มีบริษัทนี้" แสดงเหมือนกัน (<see cref="OutgoingStateOf"/>)</item>
/// <item>หลายบริษัทตรงเลข + สาขาเดียวกัน ⇒ ส่งถึง<b>ทุกบริษัทที่ตรง</b> (<see cref="PartnerCompanyMatch.InviteTargets"/>) ไม่ใช่ทิ้ง — ทิ้ง = บริษัทที่ตั้งเลขซ้ำวัดได้ว่ามีอีกบริษัทถือเลขนั้น ·
/// ใครยอมรับ A ได้แจ้งชื่อ + เลขภาษี + สาขาของผู้ยอมรับ (<see cref="AcceptedNoticeMessage"/>) ⇒ ตรวจได้ว่าเป็นคู่ค้าจริง</item>
/// <item>แจ้ง B ผ่านเครื่องแจ้งเตือนกลาง (กระดิ่ง + อีเมล + LINE ถึงเจ้าของ/ผู้มีสิทธิ์ตั้งค่าบริษัท · ตามตารางแจ้งเตือนของ B) จาก<b>งานเบื้องหลัง</b> ไม่ใช่ในคำขอของ A
/// (อีเมล/LINE ใช้เวลาไม่คงที่ ⇒ วัดเวลาตอบแยก "มี/ไม่มี" ได้)</item>
/// <item>คำขอหมดอายุใน <see cref="ExpiryDays"/> วัน · B แจ้งได้ไม่เกิน <see cref="NoticesPerTargetPerDay"/> ฉบับ/วัน (เกิน = ยังอยู่ในรายการหน้าคู่ค้า แค่ไม่แจ้งซ้ำ) ·
/// ผู้ขอส่งได้ ≤ 20 ครั้ง/วัน (<c>InvitesPerCompanyPerDay</c> เดิม) · คู่เดิมที่รอตอบและยังไม่หมดอายุ = ไม่แจ้งซ้ำ</item>
/// </list></para>
/// </summary>
public static class PartnerLinkRequestRules
{
    public const string RuleCode = "PARTNER-LINK-REQUEST";
    public const string LegalReference = "พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล 2562 ม.19 · ม.24(3) (เปิดเผยข้อมูลคู่ค้าเมื่อเจ้าของตอบรับเท่านั้น)";

    /// <summary>คำขอที่ยังไม่ตอบหมดอายุหลังกี่วัน (คำตัดสิน 264-1-5)</summary>
    public const int ExpiryDays = 30;

    /// <summary>เพดานการแจ้ง "มีคำขอเชื่อม" ต่อบริษัทปลายทางต่อ 24 ชม. (กันบริษัทหลายรายรุมส่ง — รายการบนหน้าคู่ค้ายังครบ)</summary>
    public const int NoticesPerTargetPerDay = 10;

    /// <summary>ส่งแจ้งไม่สำเร็จแล้วลองใหม่หลังกี่นาที · เลิกลองเมื่อคำขอเก่ากว่า <see cref="NoticeGiveUpHours"/> ชม. (รายการบนหน้ายังอยู่)</summary>
    public const int NoticeRetryMinutes = 30;
    public const int NoticeGiveUpHours = 48;

    /// <summary>เวลาที่คำขอถูกส่ง (เปิดใหม่) — แถวเก่าก่อนมีคอลัมน์ใช้ <paramref name="createdAt"/></summary>
    public static DateTime RequestedAt(DateTime? invitedAt, DateTime createdAt) => invitedAt ?? createdAt;

    public static DateTime ExpiryOf(DateTime? invitedAt, DateTime createdAt) => RequestedAt(invitedAt, createdAt).AddDays(ExpiryDays);

    /// <summary>คำขอที่รอตอบหมดอายุแล้วไหม — สถานะอื่นไม่ใช่เรื่องของตัวนี้ (false)</summary>
    public static bool IsExpired(TradingPartnershipStatus status, DateTime? invitedAt, DateTime createdAt, DateTime nowUtc)
        => status == TradingPartnershipStatus.Pending && nowUtc > ExpiryOf(invitedAt, createdAt);

    public const string ExpiredMessage = "คำขอเชื่อมนี้หมดอายุแล้ว (เกิน 30 วัน) — ให้คู่ค้าส่งคำขอใหม่ หรือเราส่งคำขอถึงเขาจากหน้าคู่ค้า";

    /// <summary>ยอมรับได้ไหม (ต่อจาก <see cref="PartnerExchangeRules.AcceptProblem"/>) · null = ได้</summary>
    public static string? AcceptProblem(TradingPartnershipStatus status, DateTime? invitedAt, DateTime createdAt, DateTime nowUtc)
        => PartnerExchangeRules.AcceptProblem(status) ?? (IsExpired(status, invitedAt, createdAt, nowUtc) ? ExpiredMessage : null);

    /// <summary>ป้ายสถานะของแถวคู่ค้าบนหน้าของ<b>เรา</b> (ผู้รับ/สมาชิกของคู่) — ฝั่งผู้ขอไม่เห็นแถว Pending/Rejected ของตัวเองอยู่แล้ว (P-1)</summary>
    public static string PartnershipLabel(TradingPartnershipStatus status, bool invitedByUs, bool expired) => status switch
    {
        TradingPartnershipStatus.Accepted => "เชื่อมแล้ว",
        TradingPartnershipStatus.Suspended => "ระงับชั่วคราว",
        TradingPartnershipStatus.Pending when expired => "คำขอหมดอายุ",
        TradingPartnershipStatus.Pending => invitedByUs ? "รอคู่ค้ายอมรับ" : "รอเรายอมรับ",
        _ => "ปฏิเสธแล้ว",
    };

    /// <summary>ผู้รับกดตอบได้ไหม (ปุ่มบนหน้า — เซิร์ฟเวอร์ตัดสิน)</summary>
    public static bool CanRespond(TradingPartnershipStatus status, bool invitedByUs, bool expired)
        => status == TradingPartnershipStatus.Pending && !invitedByUs && !expired;

    // ═══════════ ฝั่งผู้ขอ (A) — ไม่รั่วสมาชิกภาพ ═══════════

    public enum OutgoingState { Awaiting, Accepted, Expired }

    /// <summary>สถานะคำขอที่เราส่ง — ขึ้นกับ "มีผู้ยอมรับ" กับเวลาเท่านั้น · ไม่มีบริษัทนี้ / ถูกปฏิเสธ / ยังไม่ตอบ = <see cref="OutgoingState.Awaiting"/> จนหมดอายุ</summary>
    public static OutgoingState OutgoingStateOf(DateTime? acceptedAt, DateTime expiresAt, DateTime nowUtc)
        => acceptedAt != null ? OutgoingState.Accepted : nowUtc > expiresAt ? OutgoingState.Expired : OutgoingState.Awaiting;

    public static string OutgoingLabel(OutgoingState s) => s switch
    {
        OutgoingState.Accepted => "เชื่อมแล้ว — คู่ค้าตอบรับ (ดูในตารางคู่ค้า)",
        OutgoingState.Expired => "หมดอายุ — คู่ค้าไม่ได้ตอบรับภายใน 30 วัน",
        _ => "ส่งแล้ว — รอคู่ค้าตอบรับ",
    };

    // ═══════════ ข้อความแจ้ง B (ไม่มีข้อความอิสระของผู้ขอ — ช่องฟิชชิง · เปิดอ่านที่หน้าคู่ค้า) ═══════════

    public const string NoticeTitle = "มีคำขอเชื่อมระบบจากคู่ค้าที่ใช้ NextAcc";

    /// <summary>ข้อความถึง B — ชื่อ + เลขภาษีของผู้ขอ (ผู้ขอเปิดเผยเองเมื่อส่งคำขอ) · ยังไม่มีข้อมูลของ B ไปถึงผู้ขอจนกว่าจะยอมรับ</summary>
    public static string NoticeMessage(string? requesterName, string? requesterTaxId, DateTime expiresAtUtc)
    {
        var name = Clip(requesterName, 120) ?? "บริษัทที่ใช้ NextAcc";
        var tax = string.IsNullOrWhiteSpace(requesterTaxId) ? "" : $" (เลขผู้เสียภาษี {requesterTaxId!.Trim()})";
        var until = QuotaAddOnPricing.ThaiDateText(expiresAtUtc.AddHours(7).Date);   // พ.ศ. ไม่ขึ้นกับ culture ของเครื่อง
        return $"{name}{tax} ขอเชื่อมระบบกับบริษัทของคุณ — เปิดรับคำขอได้ที่หน้า \"คู่ค้าที่ใช้ NextAcc\" แล้วกดยอมรับหรือปฏิเสธ · "
             + $"ยังไม่มีข้อมูลหรือเอกสารใดของคุณถูกส่งไปจนกว่าคุณจะยอมรับ · คำขอหมดอายุ {until}";
    }

    /// <summary>ลิงก์เปิดการ์ดยอมรับ (path ภายใน · <c>?company=</c> สลับบริษัทหลังเข้าสู่ระบบ — <c>Layout.init</c>)</summary>
    public static string AcceptUrl(Guid targetCompanyId, Guid partnershipId)
        => $"/pages/partners.html?company={targetCompanyId}&request={partnershipId}";

    /// <summary>ข้อความถึงผู้ขอเมื่อมีผู้ยอมรับ — ชื่อ + เลขภาษี + สาขาของผู้ยอมรับ (ยินยอมแล้ว) ให้ตรวจว่าเป็นคู่ค้าที่ตั้งใจจริง</summary>
    public static string AcceptedNoticeMessage(string? name, string? taxId, string? branchCode)
        => $"{Clip(name, 120) ?? "คู่ค้า"} (เลขผู้เสียภาษี {(taxId ?? "-").Trim()} · {TaxBranchCode.Label(branchCode)}) ตอบรับคำขอเชื่อมระบบแล้ว — "
           + "ตรวจชื่อ/เลขภาษี/สาขาให้ตรงกับคู่ค้าที่ตั้งใจ ถ้าไม่ใช่ ให้กด \"ระงับการเชื่อม\" ที่หน้าคู่ค้าทันที";

    public const string AcceptedNoticeTitle = "คู่ค้าตอบรับคำขอเชื่อมระบบแล้ว";

    // ═══════════ การแจ้งจากงานเบื้องหลัง ═══════════

    public enum NoticeAction { Send, SkipCapped, SkipExpired, GiveUp }

    /// <summary>งานเบื้องหลังทำอะไรกับแถวที่รอแจ้ง — หมดอายุ ⇒ ไม่แจ้ง · <b>เคยส่งล้ม</b>และคำขอเก่าเกิน <see cref="NoticeGiveUpHours"/> ⇒ เลิก
    /// (งานหยุดไปหลายวันโดยไม่เคยลอง ⇒ ยังส่ง) · ปลายทางได้รับครบเพดานวันนี้ ⇒ ไม่แจ้ง (แถวยังอยู่บนหน้า)</summary>
    public static NoticeAction Decide(bool expired, DateTime requestedAt, DateTime nowUtc, int targetNoticesLast24h, bool previouslyFailed)
    {
        if (expired) return NoticeAction.SkipExpired;
        if (previouslyFailed && nowUtc - requestedAt > TimeSpan.FromHours(NoticeGiveUpHours)) return NoticeAction.GiveUp;
        if (targetNoticesLast24h >= NoticesPerTargetPerDay) return NoticeAction.SkipCapped;
        return NoticeAction.Send;
    }

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return t.Length > max ? t[..max] : t;
    }
}
