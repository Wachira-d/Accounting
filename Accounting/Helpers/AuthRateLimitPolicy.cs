namespace Accounting.Helpers;

/// <summary>ผลการนับหนึ่งครั้งของตัวนับร่วม (<c>IChatRateLimiter.ConsumeAsync</c>) — รอบ 235 ฝ่ายค้าน N4: แยก "เกินเพดาน" กับ "ตารางนับใช้ไม่ได้"</summary>
public enum RateConsumeOutcome
{
    Allowed = 0,
    /// <summary>เกินเพดานนาที/วัน</summary>
    Limited = 1,
    /// <summary>ตารางนับใช้ไม่ได้ (DB ล้ม/ไม่มีตัวนับร่วม)</summary>
    Unavailable = 2,
}

/// <summary>
/// ตัวตั้งเดียวของ "เพดานยิงเส้นยืนยันตัวตน" (สมัคร · ล็อกอิน · ลืม/ตั้งรหัสผ่าน · SSO · ประตูลูกค้า) — รอบ 235 (OMISE_READINESS S4 · กฎ #4 D)
///
/// <para>ที่มา: เดิม <c>RateLimitMiddleware</c> นับเส้น auth ทั้งหมดด้วยถังเดียว <c>auth:{ip}</c> (DB-upsert ผ่าน <c>IChatRateLimiter</c>
/// <b>fail-open</b> + ตัวนับในหน่วยความจำต่อเครื่อง) ⇒ ตารางนับพังเมื่อไร การสมัครบัญชี/ขอรีเซ็ตรหัสผ่าน (ส่งอีเมลออก = email bombing)
/// เหลือแค่ตัวนับต่อเครื่อง ซึ่งหลาย instance = เพดานคูณจำนวนเครื่อง · หน้า checkout สาธารณะสมัครผ่าน <c>/api/auth/register</c></para>
///
/// <para>กติกา: ทุกเส้น auth ยังนับถังเดิม <c>auth:{ip}</c> (fail-open · พฤติกรรมเดิม) · <b>สมัครบัญชีแพลตฟอร์ม</b> และ <b>ลืม/ตั้งรหัสผ่าน</b>
/// เพิ่มถังต่อ IP <b>fail-closed</b> + ถังทั้งระบบที่เป็น<b>สัญญาณเตือนแอดมินเท่านั้น</b> (ฝ่ายค้าน S1: ถังทั้งระบบเป็นตัวปฏิเสธ = ผู้โจมตีหลาย IP
/// ปิดการสมัครของทุกคนได้ ⇒ เกินแล้วแจ้งแอดมินวันละครั้ง ไม่ปฏิเสธ) · <b>สมัครสมาชิกประตูลูกค้าของร้าน</b> ถังต่อ IP fail-closed ไม่มีถังทั้งระบบ ·
/// คีย์ต่อ IP: IPv4 เต็มที่อยู่ · IPv6 ใช้ /64 (ผู้ใช้หนึ่งรายได้ทั้ง /64 — นับต่อที่อยู่ = เลี่ยงได้ไม่จำกัด) ผ่าน <see cref="ClientKey"/> ตัวเดียว</para>
///
/// <para>ตารางนับใช้ไม่ได้ในเส้น fail-closed ⇒ 503 + ข้อความกลาง "ระบบไม่พร้อม…" (ฝ่ายค้าน N4 — ไม่ใช่ "คุณยิงบ่อยเกินไป") ·
/// เกินเพดาน ⇒ 429 + ข้อความของกลุ่ม</para>
/// </summary>
public static class AuthRateLimitPolicy
{
    public enum RouteGroup
    {
        /// <summary>ไม่ใช่เส้น auth — ใช้ tier ทั่วไปของ middleware</summary>
        None = 0,
        /// <summary>ล็อกอิน · refresh · SSO · external-logins · ประตูลูกค้า login</summary>
        SignIn = 1,
        /// <summary>สมัครบัญชีแพลตฟอร์ม (รวมหน้า checkout สาธารณะที่เรียก /api/auth/register)</summary>
        Register = 2,
        /// <summary>ลืมรหัสผ่าน / ตั้งรหัสผ่านใหม่ (ส่งอีเมลออก · เดา token)</summary>
        PasswordReset = 3,
        /// <summary>สมัครสมาชิกประตูลูกค้าของร้าน (ข้อมูลของ tenant)</summary>
        PortalRegister = 4,
    }

    /// <summary>ถังนับหนึ่งใบ — <see cref="Enforce"/> = false ⇒ สัญญาณเตือนแอดมิน (ไม่ปฏิเสธ)</summary>
    public sealed record Bucket(string Key, int PerMinute, int PerDay, bool FailClosed, bool Enforce = true);

    /// <summary>คำตัดสินต่อถังหลังนับ</summary>
    public enum Verdict
    {
        Pass = 0,
        /// <summary>เกินเพดาน ⇒ 429</summary>
        Deny = 1,
        /// <summary>ตารางนับใช้ไม่ได้ในเส้น fail-closed ⇒ 503 ข้อความกลาง</summary>
        DenyUnavailable = 2,
        /// <summary>ถังสัญญาณเกิน ⇒ ผ่าน + เตือนแอดมิน (วันละครั้ง)</summary>
        Alert = 3,
    }

    /// <summary>ค่าตั้งจาก <c>appsettings</c> หมวด <c>RateLimit</c> (ค่าเริ่มต้นอยู่ที่นี่ที่เดียว)</summary>
    public sealed record Settings(
        int AuthPerMinutePerIp = 10,
        int RegisterPerMinutePerIp = 5, int RegisterPerDayPerIp = 20,
        int RegisterPerMinuteGlobal = 60, int RegisterPerDayGlobal = 2000,
        int ResetPerMinutePerIp = 5, int ResetPerDayPerIp = 20,
        int ResetPerMinuteGlobal = 60, int ResetPerDayGlobal = 2000,
        int PortalRegisterPerMinutePerIp = 5, int PortalRegisterPerDayPerIp = 30)
    {
        public static Settings From(Microsoft.Extensions.Configuration.IConfiguration config)
        {
            var d = new Settings();
            int Read(string key, int fallback) => int.TryParse(config[key], out var v) && v > 0 ? v : fallback;
            return new Settings(
                Read("RateLimit:AuthEndpointMaxPerMinute", d.AuthPerMinutePerIp),
                Read("RateLimit:Register:PerMinutePerIp", d.RegisterPerMinutePerIp),
                Read("RateLimit:Register:PerDayPerIp", d.RegisterPerDayPerIp),
                Read("RateLimit:Register:PerMinuteGlobal", d.RegisterPerMinuteGlobal),
                Read("RateLimit:Register:PerDayGlobal", d.RegisterPerDayGlobal),
                Read("RateLimit:PasswordReset:PerMinutePerIp", d.ResetPerMinutePerIp),
                Read("RateLimit:PasswordReset:PerDayPerIp", d.ResetPerDayPerIp),
                Read("RateLimit:PasswordReset:PerMinuteGlobal", d.ResetPerMinuteGlobal),
                Read("RateLimit:PasswordReset:PerDayGlobal", d.ResetPerDayGlobal),
                Read("RateLimit:PortalRegister:PerMinutePerIp", d.PortalRegisterPerMinutePerIp),
                Read("RateLimit:PortalRegister:PerDayPerIp", d.PortalRegisterPerDayPerIp));
        }
    }

    /// <summary>จำแนกเส้น (ไม่สนตัวพิมพ์) — รายชื่อเส้นเดิมของ <c>RateLimitMiddleware.IsAuthEndpoint</c> ย้ายมาที่นี่ที่เดียว</summary>
    public static RouteGroup Classify(string? path)
    {
        var p = path ?? "";
        bool S(string prefix) => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        bool E(string suffix) => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

        if (S("/api/auth/register")) return RouteGroup.Register;
        if (S("/api/auth/forgot-password") || S("/api/auth/reset-password")) return RouteGroup.PasswordReset;
        if (S("/api/portal/register") || E("/portal/register")) return RouteGroup.PortalRegister;
        if (S("/api/auth/login") || S("/api/auth/refresh")
            // ⚠️ เส้น SSO ต้องอยู่ tier เดียวกับ login — ไม่งั้นตกไป tier anonymous (600/นาที/IP) ⇒ email bombing ด้วยลิงก์ยืนยัน ·
            // เดา token · เอนูมอีเมล (เหตุผลเดิมจาก middleware)
            || S("/api/auth/sso") || S("/api/auth/external-logins")
            // ⚠️ ประตูล็อกอินของลูกค้า/คู่ค้า ก็เป็นประตูล็อกอิน (ผลตรวจทีม A · A-07)
            || S("/api/portal/login") || E("/portal/login"))
            return RouteGroup.SignIn;
        return RouteGroup.None;
    }

    /// <summary>คีย์ผู้เรียกต่อ IP — IPv4 (รวม IPv4-mapped IPv6) = ที่อยู่เต็ม · IPv6 = prefix /64 · ไม่รู้ = "unknown"</summary>
    public static string ClientKey(System.Net.IPAddress? ip)
    {
        if (ip == null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
        var b = ip.GetAddressBytes();
        for (var i = 8; i < 16; i++) b[i] = 0;
        return new System.Net.IPAddress(b).ToString() + "/64";
    }

    /// <summary>ถังที่ต้องผ่าน<b>ทุกใบตามลำดับ</b>ของเส้นนี้ — ว่าง = ไม่ใช่เส้น auth · ใบแรกเสมอคือถังเดิม <c>auth:{ip}</c> (fail-open) ·
    /// <paramref name="clientKey"/> มาจาก <see cref="ClientKey"/></summary>
    public static IReadOnlyList<Bucket> BucketsFor(RouteGroup group, string? clientKey, Settings s)
    {
        if (group == RouteGroup.None) return Array.Empty<Bucket>();
        var who = string.IsNullOrWhiteSpace(clientKey) ? "unknown" : clientKey;
        var list = new List<Bucket> { new($"auth:{who}", s.AuthPerMinutePerIp, s.AuthPerMinutePerIp * 60, FailClosed: false) };
        switch (group)
        {
            case RouteGroup.Register:
                list.Add(new($"auth-register:ip:{who}", s.RegisterPerMinutePerIp, s.RegisterPerDayPerIp, FailClosed: true));
                list.Add(new("auth-register:global", s.RegisterPerMinuteGlobal, s.RegisterPerDayGlobal, FailClosed: false, Enforce: false));
                break;
            case RouteGroup.PasswordReset:
                list.Add(new($"auth-pwreset:ip:{who}", s.ResetPerMinutePerIp, s.ResetPerDayPerIp, FailClosed: true));
                list.Add(new("auth-pwreset:global", s.ResetPerMinuteGlobal, s.ResetPerDayGlobal, FailClosed: false, Enforce: false));
                break;
            case RouteGroup.PortalRegister:
                list.Add(new($"portal-register:ip:{who}", s.PortalRegisterPerMinutePerIp, s.PortalRegisterPerDayPerIp, FailClosed: true));
                break;
        }
        return list;
    }

    /// <summary>คำตัดสินของถังหนึ่งใบจากผลการนับ — <paramref name="outcome"/> = null ⇒ ไม่มีตัวนับร่วม (เทียบเท่า Unavailable)</summary>
    public static Verdict Decide(Bucket b, RateConsumeOutcome? outcome)
    {
        var o = outcome ?? RateConsumeOutcome.Unavailable;
        if (!b.Enforce) return o == RateConsumeOutcome.Limited ? Verdict.Alert : Verdict.Pass;
        return o switch
        {
            RateConsumeOutcome.Allowed => Verdict.Pass,
            RateConsumeOutcome.Limited => Verdict.Deny,
            _ => b.FailClosed ? Verdict.DenyUnavailable : Verdict.Pass,
        };
    }

    /// <summary>คีย์กันเตือนซ้ำ (หนึ่งครั้งต่อถังต่อวันตาม UTC) — นับผ่านตัวนับร่วมด้วยเพดาน 1/1</summary>
    public static string AlertOnceKey(Bucket b, DateTime utcNow) => $"auth-alert:{b.Key}:{utcNow:yyyyMMdd}";

    /// <summary>ข้อความเตือนแอดมินแพลตฟอร์ม (PlatformOps)</summary>
    public static string AlertMessage(Bucket b)
        => $"⚠️ เส้น {b.Key} เกินเพดานทั้งระบบ ({b.PerMinute}/นาที หรือ {b.PerDay}/วัน) — อาจมีการสมัคร/ขอรีเซ็ตรหัสผ่านจำนวนมากจากหลาย IP · "
           + "ระบบยังไม่ปฏิเสธ (ถังต่อ IP ยังกันอยู่) · ตรวจรายการบัญชีใหม่/ล็อก แล้วปรับ RateLimit:* ถ้าจำเป็น (แจ้งวันละครั้ง)";

    public const string UnavailableMessage = "ระบบไม่พร้อมรับการสมัครชั่วคราว — ลองใหม่อีกครั้ง";

    /// <summary>ข้อความ 429 ต่อกลุ่ม (ไทย · บอกทางไปต่อ)</summary>
    public static string DeniedMessage(RouteGroup group) => group switch
    {
        RouteGroup.Register or RouteGroup.PortalRegister => "มีการสมัครบัญชีจากเครือข่ายนี้บ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่ (หรือติดต่อผู้ให้บริการ)",
        RouteGroup.PasswordReset => "ขอตั้งรหัสผ่านใหม่บ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่ และตรวจกล่องอีเมล (รวมถึงอีเมลขยะ) จากคำขอก่อนหน้า",
        _ => "พยายามเข้าสู่ระบบบ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่",
    };
}
