using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// คำขอส่งข้อความผ่าน <c>IBookingNotifier.SendAsync</c> (รอบ 212 แพ็ก A — ทางเดียวของทุกข้อความถึงลูกค้า/ร้าน) ·
/// <c>EventKey</c> ต้องอยู่ใน <see cref="CustomerNotifyEvents"/> · <c>MessageKey</c> = คีย์กันส่งซ้ำ (<see cref="CustomerNotifyKeys"/> · ต่อบริษัท ·
/// null = ไม่กันซ้ำ ใช้กับข้อความถึงร้านเท่านั้น) · <c>Html</c> = เนื้ออีเมลแบบ HTML ที่ผู้เรียก encode แล้ว (null = แปลงจาก <c>Text</c>)
/// </summary>
public sealed record CustomerNotifyRequest(Guid CompanyId, Guid? SiteId, string EventKey, string? MessageKey, BookingContact Contact, string Subject, string Text)
{
    public string? Html { get; init; }
    /// <summary>ชนิด/รหัสรายการปลายทางสำหรับบันทึก (ไม่เก็บอีเมล/userId — PDPA) · null = หาจาก <c>Contact.LineTarget</c></summary>
    public string? TargetKind { get; init; }
    public Guid? TargetId { get; init; }
}

/// <summary>รูปแบบคีย์กันส่งซ้ำต่อข้อความ (design §3.3) — ตัวตั้งเดียว · ยาวไม่เกิน 200 ตัวอักษร</summary>
public static class CustomerNotifyKeys
{
    private static string N(Guid id) => id.ToString("N");
    private static string Short(string? s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""))).Substring(0, 12).ToLowerInvariant();

    public static string Booking(Guid bookingId, string eventKey) => $"booking:{N(bookingId)}:{eventKey}";
    public static string BookingRescheduled(Guid bookingId, DateTime oldDate, TimeOnly oldStart)
        => $"booking:{N(bookingId)}:{CustomerNotifyEvents.BookingRescheduled}:{oldDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{oldStart.ToString("HHmm", CultureInfo.InvariantCulture)}";
    public static string Reminder(Guid bookingId, int stageHours) => $"booking:{N(bookingId)}:reminder:{stageHours}";
    public static string Waitlist(Guid bookingId) => $"booking:{N(bookingId)}:waitlist";
    /// <summary>คลาส: เปลี่ยนผู้สอนหลายครั้งได้ ⇒ คีย์รวม hash ของผู้สอนใหม่</summary>
    public static string Class(Guid sessionId, Guid bookingId, string eventKey, string? teacher)
        => $"class:{N(sessionId)}:{N(bookingId)}:{eventKey}" + (eventKey == CustomerNotifyEvents.ClassSubstitute ? ":" + Short(teacher) : "");
    public static string Order(Guid orderId, string eventKey) => $"order:{N(orderId)}:{eventKey}";
    public static string Credit(Guid passId, DateOnly expiresOn) => $"credit:{N(passId)}:expiring:{expiresOn.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
    /// <summary>ที่พัก: <paramref name="occurrence"/> แยกเหตุการณ์ที่เกิดซ้ำได้ (สลิปไม่ผ่านหลายรอบ)</summary>
    public static string Lodging(Guid reservationId, string eventKey, string? occurrence = null)
        => $"lodging:{N(reservationId)}:{eventKey}" + (string.IsNullOrEmpty(occurrence) ? "" : ":" + Short(occurrence));
    public static string Cart(Guid cartId) => $"cart:{N(cartId)}:abandoned";
    public static string Form(Guid submissionId) => $"form:{N(submissionId)}:autoreply";
}

/// <summary>แถวค่าตั้ง 1 แถว (บริษัท: <c>SiteId</c> null · เว็บ: มีค่า) · <c>null</c> ต่อช่อง = สืบทอดจากชั้นถัดไป</summary>
public sealed record CustomerNotifyRuleRow(Guid? SiteId, string EventKey, bool? LineEnabled, bool? EmailEnabled);

/// <summary>ผล resolve ต่อช่อง (อิสระต่อกัน) + ชั้นที่ตัดสิน (<c>site</c> · <c>site-all</c> · <c>company</c> · <c>company-all</c> · <c>default</c>)</summary>
public sealed record CustomerNotifyChannelPolicy(bool LineOn, bool EmailOn, string LineSource, string EmailSource)
{
    public bool AnyOn => LineOn || EmailOn;
}

/// <summary>ขั้นส่ง 1 ขั้นตามลำดับ — <c>Source</c> = ช่องจริง (<see cref="CustomerNotifyPolicy.SrcNextAccOa"/> ฯลฯ) · <c>Metered</c> = นับโควตาช่องกลาง</summary>
public sealed record CustomerNotifyStep(string Channel, string Source, bool Metered);

/// <summary>
/// **ตัวตัดสินค่าตั้งการแจ้งเตือนลูกค้า — ตัวเดียว** (รอบ 212 แพ็ก A · คำตัดสินข้อ 171 · design §2.1/§3.1)
/// <list type="bullet">
/// <item><see cref="Resolve"/>: เว็บ+คีย์ → เว็บ+<c>*</c> → บริษัท+คีย์ → บริษัท+<c>*</c> → <b>ค่าเริ่มต้น = เปิดทั้งสองช่อง</b> (= พฤติกรรมก่อนรอบ 212) · ช่องละอิสระ</item>
/// <item><see cref="Plan"/>: ลำดับขั้นส่งจาก <see cref="CustomerNotifyChannel.Order"/> เดิม (ไม่เขียนลำดับซ้ำ) × ผู้ส่งอีเมลจาก <see cref="CustomerNotifyChannel.EmailPlan"/> ·
///   ทุกขั้นบอกว่านับโควตาไหม (<see cref="NotifyQuotaPolicy.IsMetered"/>)</item>
/// <item>ข้อความถึงร้าน (<c>shop.*</c>) ไม่อยู่ในค่าตั้ง ⇒ เปิดเสมอ</item>
/// </list>
/// เรียกที่ <c>BookingNotifier.SendAsync</c> จุดเดียว · สวิตช์เดิมที่คุม "สร้างข้อความไหม/เมื่อไร" (<see cref="CustomerNotifyEvent.OwnerSwitch"/>) ไม่ถูกแตะ
/// </summary>
public static class CustomerNotifyPolicy
{
    public const string LayerDefault = "default";
    public const string LayerSite = "site";
    public const string LayerSiteAll = "site-all";
    public const string LayerCompany = "company";
    public const string LayerCompanyAll = "company-all";

    /// <summary>LINE OA ของ NextAcc (นับโควตา)</summary>
    public const string SrcNextAccOa = "nextacc-oa";
    /// <summary>LINE OA ของร้านเอง (ไม่นับ)</summary>
    public const string SrcCompanyOa = "company-oa";
    /// <summary>SMTP ของระบบ NextAcc (นับโควตา)</summary>
    public const string SrcSystemSmtp = "system-smtp";
    /// <summary>ผู้ส่งอีเมลของร้านเอง (ไม่นับ)</summary>
    public const string SrcTenantSmtp = "tenant-smtp";

    public const string SuppressedByPolicyReason = "ปิดการแจ้งเตือนประเภทนี้ไว้ตามค่าตั้ง (ตั้งค่า → การแจ้งเตือน → การแจ้งเตือนลูกค้า)";
    public const string SuppressedTenantOnlyReason = "ร้านตั้งให้ส่งอีเมลผ่านผู้ส่งของร้านเท่านั้น แต่ยังไม่ได้ตั้ง/ทดสอบผู้ส่งอีเมล (ตั้งค่า → อีเมล) — ไม่ได้ส่ง";

    public static CustomerNotifyChannelPolicy Resolve(IEnumerable<CustomerNotifyRuleRow> rules, Guid? siteId, string eventKey)
    {
        if (CustomerNotifyEvents.IsShopEvent(eventKey)) return new(true, true, LayerDefault, LayerDefault);
        var list = rules as IReadOnlyList<CustomerNotifyRuleRow> ?? rules.ToList();
        CustomerNotifyRuleRow? Find(Guid? site, string key)
            => list.FirstOrDefault(r => r.SiteId == site && string.Equals(r.EventKey, key, StringComparison.Ordinal));

        var chain = new List<(CustomerNotifyRuleRow? Row, string Layer)>(4);
        if (siteId is Guid sid)
        {
            chain.Add((Find(sid, eventKey), LayerSite));
            chain.Add((Find(sid, CustomerNotifyEvents.All), LayerSiteAll));
        }
        chain.Add((Find(null, eventKey), LayerCompany));
        chain.Add((Find(null, CustomerNotifyEvents.All), LayerCompanyAll));

        (bool On, string Layer) Pick(Func<CustomerNotifyRuleRow, bool?> field)
        {
            foreach (var (row, layer) in chain)
                if (row != null && field(row) is bool v) return (v, layer);
            return (true, LayerDefault);
        }
        var line = Pick(r => r.LineEnabled);
        var email = Pick(r => r.EmailEnabled);
        return new CustomerNotifyChannelPolicy(line.On, email.On, line.Layer, email.Layer);
    }

    /// <summary>OA ที่ส่งจริง (<see cref="CustomerNotifyChannel.ScopeCompany"/>/<see cref="CustomerNotifyChannel.ScopeNextAcc"/>) → แหล่ง</summary>
    public static string LineSource(string? oaScope)
        => oaScope == CustomerNotifyChannel.ScopeCompany ? SrcCompanyOa : SrcNextAccOa;

    /// <summary>ขั้นของ <see cref="CustomerNotifyChannel.EmailPlan"/> → แหล่ง</summary>
    public static string EmailSource(string emailPlanEntry)
        => emailPlanEntry == CustomerNotifyChannel.EmailTenant ? SrcTenantSmtp : SrcSystemSmtp;

    /// <summary>ขั้นส่งตามลำดับ (หยุดที่ขั้นแรกที่สำเร็จ — สัญญาเดิมของ <c>IBookingNotifier</c>) · ช่องที่ค่าตั้งปิด = ไม่มีขั้นของช่องนั้น ·
    /// <paramref name="emailPlan"/> ว่าง (ร้านตั้ง "ผู้ส่งของร้านเท่านั้น" แต่ยังไม่ตั้ง) ⇒ ไม่มีขั้นอีเมล</summary>
    public static IReadOnlyList<CustomerNotifyStep> Plan(CustomerNotifyChannelPolicy policy, bool hasEmail, bool hasLine, bool lineOptedIn,
        string? lineScope, IReadOnlyList<string> emailPlan)
    {
        var steps = new List<CustomerNotifyStep>(3);
        foreach (var channel in CustomerNotifyChannel.Order(hasEmail && policy.EmailOn, hasLine && policy.LineOn, lineOptedIn))
        {
            if (channel == CustomerNotifyChannel.ChannelLine)
            {
                var src = LineSource(lineScope);
                steps.Add(new CustomerNotifyStep(channel, src, NotifyQuotaPolicy.IsMetered(src)));
            }
            else
            {
                foreach (var e in emailPlan)
                {
                    var src = EmailSource(e);
                    steps.Add(new CustomerNotifyStep(channel, src, NotifyQuotaPolicy.IsMetered(src)));
                }
            }
        }
        return steps;
    }

    /// <summary>เหตุที่ "ไม่มีขั้นให้ส่ง" ทั้งที่ลูกค้ามีช่องติดต่อ (ระงับโดยตั้งใจ · ไม่ใช่ความล้มเหลว) · null = มีขั้นส่ง หรือไม่มีช่องติดต่อเลย (= Fail เดิม)</summary>
    public static string? SuppressReason(CustomerNotifyChannelPolicy policy, bool hasEmail, bool hasLine, IReadOnlyList<string> emailPlan)
    {
        if (!hasEmail && !hasLine) return null;
        var lineUsable = hasLine && policy.LineOn;
        var emailUsable = hasEmail && policy.EmailOn && emailPlan.Count > 0;
        if (lineUsable || emailUsable) return null;
        if (hasEmail && policy.EmailOn && emailPlan.Count == 0) return SuppressedTenantOnlyReason;
        return SuppressedByPolicyReason;
    }

    /// <summary>ตรวจคำขอบันทึกค่าตั้ง (ภาษาไทย) — คีย์ต้องอยู่ในแคตตาล็อก/แถว "ทั้งหมด" · ซ้ำในคำขอเดียวไม่ได้</summary>
    public static IReadOnlyList<string> Validate(IEnumerable<CustomerNotifyRuleRow> rows)
    {
        var errs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!CustomerNotifyEvents.IsConfigurable(r.EventKey)) errs.Add($"ไม่รู้จักประเภทข้อความ \"{r.EventKey}\"");
            else if (!seen.Add(r.EventKey)) errs.Add($"ประเภทข้อความ \"{r.EventKey}\" ซ้ำในคำขอ");
        }
        return errs;
    }
}
