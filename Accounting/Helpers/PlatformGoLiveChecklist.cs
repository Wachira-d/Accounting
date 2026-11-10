namespace Accounting.Helpers;

/// <summary>
/// รอบ 250 (WP-02 · คำตัดสินเจ้าของมอบให้ทีม §3.3 Y7 · Y8 · N11 · OMISE_READINESS §3) — <b>รายการตรวจ go-live ของแพลตฟอร์ม</b> บนแดชบอร์ดแอดมิน (pure) ·
/// 10 ข้อของการรับเงินจริงผ่าน Omise + รอบ 257 ชุด 4 (E-H1) ข้อ LINE OA กลาง 4 ข้อ (บริษัทผู้ให้บริการ · token/secret/Basic ID · webhook · เข้ากลุ่มได้)
///
/// <para>ทุกข้อตรวจจากข้อมูลจริงในฐาน (ผู้เรียก <c>AdminGoLiveController</c> รวบรวมข้อเท็จจริง) · ข้อที่ระบบตรวจเองไม่ได้ (ส่ง URL ให้ Omise) ติดป้าย
/// "ทำเอง" ตามจริง — ไม่ประทับ ✅ แทนเจ้าของ (F2 ข้อ 3) · URL ของระบบ: ไม่เดาโดเมน (Y7 — ค่าที่มากับซอร์ส ≠ ตั้งแล้ว) ·
/// การเชื่อมต่อขาออก (Y8): ผลการทดสอบล่าสุดที่บันทึกไว้ — ล้ม ⇒ แดงพร้อมคำอธิบาย · ยังไม่เคยทดสอบ ⇒ "ยังไม่ได้ตรวจ" (ไม่ใช่ ✅)</para>
/// </summary>
public static class PlatformGoLiveChecklist
{
    public const string Ok = "Ok";
    public const string Fail = "Fail";
    /// <summary>ระบบตรวจเองไม่ได้ — เจ้าของทำแล้วติ๊กในใจ (ไม่มีสถานะในฐาน)</summary>
    public const string Manual = "Manual";
    /// <summary>ยังไม่ได้ตรวจ (เช่น ยังไม่เคยกดทดสอบการเชื่อมต่อขาออก)</summary>
    public const string Unknown = "Unknown";

    /// <summary>ปลายทางของการทดสอบขาออก (Y8) — API ของผู้ให้บริการรับชำระที่ระบบต้องเรียกถึงจริง</summary>
    public const string OutboundTestUrl = global::Accounting.Services.Payments.Providers.OmisePaymentProvider.OutboundHealthCheckUrl;

    /// <summary>ค่าตั้งต้นของเส้นโอนเมื่อไม่รู้ธนาคาร/feed ที่ลูกค้าใช้ (N11) — แสดงใต้การ์ด</summary>
    public const string BankFeedNote =
        "ยังไม่รู้ธนาคาร/feed ที่ลูกค้าใช้จริง ⇒ เส้นเงินโอนตั้งต้น = นำเข้า statement รายวัน + ระบบเสนอจับคู่ (ไม่ปิดหนี้เอง) จนกว่าจะเชื่อม feed ธนาคาร";

    public sealed record Facts
    {
        public bool SystemBaseUrlConfigured { get; init; }
        public string? SystemBaseUrl { get; init; }
        public bool PlatformGatewayConfigured { get; init; }
        public bool TestKeyPassed { get; init; }
        public bool PublicCheckoutEnabled { get; init; }
        public int SellablePlanCount { get; init; }
        public bool PrivacyPublished { get; init; }
        public bool TermsPublished { get; init; }
        public bool RefundPublished { get; init; }
        /// <summary>ทั้งสามฉบับที่เผยแพร่มีฉบับภาษาอังกฤษ</summary>
        public bool EnglishForAllPublished { get; init; }
        public bool ContactInfoSet { get; init; }
        public bool SystemEmailConfigured { get; init; }
        public bool SelfTestPaid { get; init; }
        public bool LiveConfirmed { get; init; }
        public DateTime? OutboundCheckAt { get; init; }
        public bool? OutboundCheckOk { get; init; }
        public string? OutboundCheckMessage { get; init; }

        // ── รอบ 257 ชุด 4 (E-H1): LINE OA กลาง — การ์ดนี้คือรายการ go-live ตัวเดียวของแพลตฟอร์ม (เดิมไม่มีข้อ LINE ⇒ ขึ้นระบบแล้วกลุ่ม/ลิงก์ลูกค้าเงียบ) ──
        /// <summary><c>SiteSettings.PlatformCompanyId</c> ตั้งแล้วและชี้บริษัทที่มีอยู่ (ผู้ส่งกลุ่มผ่าน OA กลาง · 256-2)</summary>
        public bool PlatformCompanySet { get; init; }
        public string? PlatformCompanyName { get; init; }
        /// <summary>ค่าตั้ง OA กลางที่ใช้จริง (<c>INextAccLineConfig.GetAsync</c> — หน้าแอดมิน › config)</summary>
        public bool CentralOaHasToken { get; init; }
        public bool CentralOaHasSecret { get; init; }
        public bool CentralOaHasBasicId { get; init; }
        /// <summary>เหตุที่ค่าของหน้าแอดมินใช้ไม่ได้ (ถอดรหัสไม่ได้ ฯลฯ) · null = ไม่มี</summary>
        public string? CentralOaProblem { get; init; }
    }

    /// <summary>รอบ 257 ชุด 4 (E-H1): ชื่อการ์ด (server computes) — รายการ go-live ตัวเดียวของแพลตฟอร์ม ไม่ใช่เฉพาะ Omise</summary>
    public const string CardTitle = "รายการตรวจ go-live ของแพลตฟอร์ม (รับเงินจริง · LINE OA กลาง)";
    /// <summary>ปลายทาง webhook ของ LINE OA กลาง (ตั้งใน LINE Developers Console ของ OA กลาง)</summary>
    public const string CentralWebhookPath = "/api/line-webhook";

    public sealed record Item(int No, string Code, string Status, string Title, string? Why, string? FixUrl, string? FixLabel);

    public sealed record Report(IReadOnlyList<Item> Items, int DoneCount, int FailCount, string BankFeedNote, string Title = CardTitle);

    public static Report Evaluate(Facts f)
    {
        var items = new List<Item>();
        void Add(string code, string status, string title, string? why = null, string? url = null, string? label = null)
            => items.Add(new Item(items.Count + 1, code, status, title, why, url, label));

        Add("BASEURL", f.SystemBaseUrlConfigured ? Ok : Fail,
            f.SystemBaseUrlConfigured ? $"URL ของระบบ: {f.SystemBaseUrl}" : "URL ของระบบยังไม่ได้ตั้ง — ลิงก์ชำระ/QR/ลิงก์ใน LINE-อีเมลจะไม่ถูกแนบ",
            f.SystemBaseUrlConfigured ? null : "ระบบไม่เดาโดเมน (ค่าที่มากับโปรแกรมไม่นับว่าตั้งแล้ว) — ตั้ง AppBaseUrl = https://<โดเมนจริง> · URL นี้ใช้ส่งให้ Omise · returnUrl หลัง 3-D Secure · webhook",
            "/admin/site-settings.html", "ตั้ง URL ของระบบ");
        Add("GATEWAY-TEST", f.PlatformGatewayConfigured && f.TestKeyPassed ? Ok : Fail,
            !f.PlatformGatewayConfigured ? "ยังไม่ได้ตั้งช่องทาง Omise ของแพลตฟอร์ม" : f.TestKeyPassed ? "ตั้งช่องทาง Omise แล้ว · ทดสอบคีย์ test ผ่าน" : "ตั้งช่องทาง Omise แล้ว แต่ยังไม่ได้ทดสอบคีย์ test ให้ผ่าน",
            null, "/admin/platform-gateway.html", "ตั้งช่องทาง Omise");
        Add("PUBLIC-CHECKOUT", f.PublicCheckoutEnabled ? Ok : Fail,
            f.PublicCheckoutEnabled ? "เปิดหน้าซื้อแพ็กเกจสาธารณะแล้ว" : "ยังไม่ได้เปิดสวิตช์หน้าซื้อแพ็กเกจสาธารณะ",
            null, "/admin/site-settings.html", "เปิดหน้าซื้อแพ็กเกจ");
        Add("PLANS", f.SellablePlanCount > 0 ? Ok : Fail,
            f.SellablePlanCount > 0 ? $"แพ็กเกจที่ขายได้ {f.SellablePlanCount} แพ็กเกจ (เปิดใช้ + ราคา > 0)" : "ยังไม่มีแพ็กเกจที่ขายได้ (ต้องเปิดใช้ และมีราคา > 0)",
            null, "/admin/plans.html", "ตรวจแพ็กเกจ");
        var policiesOk = f.PrivacyPublished && f.TermsPublished && f.RefundPublished && f.EnglishForAllPublished;
        var missingPolicies = new List<string>();
        if (!f.PrivacyPublished) missingPolicies.Add("นโยบายความเป็นส่วนตัว");
        if (!f.TermsPublished) missingPolicies.Add("ข้อกำหนดการใช้งาน");
        if (!f.RefundPublished) missingPolicies.Add("นโยบายการยกเลิกและคืนเงิน");
        Add("POLICIES", policiesOk ? Ok : Fail,
            policiesOk ? "เผยแพร่นโยบาย 3 ฉบับของ NextAcc แล้ว พร้อมฉบับภาษาอังกฤษ"
                : missingPolicies.Count > 0 ? "ยังไม่ได้เผยแพร่ฉบับของ NextAcc: " + string.Join(" · ", missingPolicies)
                : "เผยแพร่ครบ 3 ฉบับแล้ว แต่ยังขาดฉบับภาษาอังกฤษบางฉบับ",
            "การกดเผยแพร่เป็นของเจ้าของ (ระบบประทับการยอมรับแทนไม่ได้)", "/admin/legal.html", "เปิดหน้าเอกสารกฎหมาย");
        Add("CONTACT", f.ContactInfoSet ? Ok : Fail,
            f.ContactInfoSet ? "ข้อมูลติดต่อของเว็บครบ (โทร · อีเมล · ที่อยู่)" : "ข้อมูลติดต่อของเว็บยังไม่ครบ (โทร · อีเมล · ที่อยู่) — หน้า /contact.html และนโยบายอ่านจากที่เดียวกัน",
            null, "/admin/site-settings.html", "ตั้งข้อมูลติดต่อ");
        Add("SYSTEM-EMAIL", f.SystemEmailConfigured ? Ok : Fail,
            f.SystemEmailConfigured ? "ตั้งอีเมลระบบแล้ว" : "ยังไม่ได้ตั้งอีเมลระบบ — อีเมลยืนยันคำสั่งซื้อจะไม่ถูกส่ง",
            null, "/admin/system-email.html", "ตั้งอีเมลระบบ");
        Add("SELF-TEST", f.SelfTestPaid ? Ok : Fail,
            f.SelfTestPaid ? "ทดลองจ่ายครบลูปแล้ว (โหมดทดสอบ)" : "ยังไม่ได้ทดลองจ่ายครบลูปด้วยบัตร/พร้อมเพย์ทดสอบ",
            null, "/admin/platform-gateway.html", "ทดลองจ่าย");
        Add("SEND-URLS", Manual, "ส่ง URL ให้ Omise (ระบบตรวจเองไม่ได้)",
            f.SystemBaseUrlConfigured
                ? string.Join(" · ", new[] { "/pricing.html", "/checkout.html", "/terms.html", "/refund-policy.html", "/privacy.html", "/contact.html" }
                    .Select(p => (f.SystemBaseUrl ?? "").TrimEnd('/') + p))
                : "ตั้ง URL ของระบบก่อน แล้ว URL ทั้ง 6 จะแสดงที่นี่",
            null, null);
        Add("LIVE", f.LiveConfirmed ? Ok : Fail,
            f.LiveConfirmed ? "เปิดรับเงินจริงแล้ว (ผ่านด่าน PaymentLiveGate)" : "ยังไม่ได้เปิดรับเงินจริง — หลัง Omise อนุมัติ ใส่คีย์ live แล้วผ่านด่านในหน้าช่องทาง Omise",
            null, "/admin/platform-gateway.html", "เปิดรับเงินจริง");
        // ── รอบ 257 ชุด 4 (E-H1): LINE OA กลาง (READINESS_257 ข้อ 2) ──
        Add("LINE-PLATFORM-COMPANY", f.PlatformCompanySet ? Ok : Fail,
            f.PlatformCompanySet ? $"ตั้งบริษัทผู้ให้บริการแล้ว: {f.PlatformCompanyName ?? "-"}"
                : "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ (PlatformCompanyId) — ผูก/ส่งเข้ากลุ่ม LINE ของลูกค้าผ่าน OA กลางไม่ได้ · คำสั่ง #HeyNextAcc ตอบว่ายังไม่เปิด",
            "บริษัทผู้ให้บริการคือบริษัทเดียวที่ส่งเอกสารเข้ากลุ่ม LINE ของลูกค้าผ่าน OA กลางได้ (คำตัดสิน 256-2)", "/admin/site-settings.html", "ตั้งบริษัทผู้ให้บริการ");
        var oaMissing = new List<string>();
        if (!f.CentralOaHasToken) oaMissing.Add("Channel Access Token");
        if (!f.CentralOaHasSecret) oaMissing.Add("Channel Secret");
        if (!f.CentralOaHasBasicId) oaMissing.Add("Basic ID (@…)");
        Add("LINE-CENTRAL-OA", oaMissing.Count == 0 && f.CentralOaProblem == null ? Ok : Fail,
            oaMissing.Count == 0 && f.CentralOaProblem == null ? "ตั้ง LINE OA กลางครบ (token · secret · Basic ID)"
                : oaMissing.Count > 0 ? "LINE OA กลางยังขาด: " + string.Join(" · ", oaMissing) : "ค่าตั้ง LINE OA กลางใช้ไม่ได้",
            f.CentralOaProblem ?? "ขาด token/secret ⇒ webhook กลางตรวจลายเซ็นไม่ได้และส่งข้อความไม่ได้ · ขาด Basic ID ⇒ ปุ่มเพิ่มเพื่อน/ลิงก์ผูกของลูกค้าใช้ไม่ได้",
            "/admin/site-settings.html", "ตั้ง LINE OA กลาง");
        Add("LINE-WEBHOOK", Manual, "ตั้ง Webhook URL ของ LINE OA กลางใน LINE Developers Console (ระบบตรวจเองไม่ได้)",
            (f.SystemBaseUrlConfigured ? (f.SystemBaseUrl ?? "").TrimEnd('/') + CentralWebhookPath : "ตั้ง URL ของระบบก่อน แล้ว URL จะเป็น <URL ของระบบ>" + CentralWebhookPath)
                + " · เปิด \"Use webhook\" · ห้ามวาง URL นี้ใน OA ของบริษัทใด และห้ามวาง URL ของบริษัทใน OA กลาง",
            null, null);
        Add("LINE-JOIN-GROUPS", Manual, "เปิด \"Allow bot to join group chats\" ของ LINE OA กลาง (ระบบตรวจเองไม่ได้)",
            "LINE Official Account Manager › ตั้งค่า › บัญชี — ปิดอยู่ ⇒ เชิญบอทเข้ากลุ่มไม่ได้ และอ่านจำนวนสมาชิกตอนออกรหัสไม่ได้", null, null);
        // Y8: การเชื่อมต่อขาออก (ข้อเสริม — ไม่นับใน 10 ข้อของ OMISE_READINESS แต่เป็นเงื่อนไขของ webhook/คีย์)
        var outbound = f.OutboundCheckOk switch
        {
            true => Ok,
            false => Fail,
            _ => Unknown,
        };
        Add("OUTBOUND", outbound,
            f.OutboundCheckOk switch
            {
                true => "เชื่อมต่อขาออกถึงผู้ให้บริการรับชำระได้",
                false => "เชื่อมต่อขาออกถึงผู้ให้บริการรับชำระไม่ได้ — คีย์/การชำระ/webhook จะล้ม",
                _ => "ยังไม่ได้ทดสอบการเชื่อมต่อขาออก",
            },
            f.OutboundCheckMessage is { Length: > 0 } m ? m + (f.OutboundCheckAt is DateTime at ? $" (ทดสอบเมื่อ {at:yyyy-MM-dd HH:mm} UTC)" : "")
                : "ระบบถือว่าโฮสต์มีการเชื่อมต่อขาออกตรง (ไม่บังคับ proxy) — กดปุ่มทดสอบเพื่อยืนยัน",
            null, "ทดสอบการเชื่อมต่อขาออก");
        return new Report(items, items.Count(i => i.Status == Ok), items.Count(i => i.Status == Fail), BankFeedNote);
    }

    /// <summary>ผลทดสอบขาออก (Y8) — ได้คำตอบ HTTP ใด ๆ = ต่อถึง (401/404 ของ API ที่ไม่ได้ส่งคีย์ = ปกติ) · ด่าน URL ไม่ผ่าน/ต่อไม่ได้ = ล้มพร้อมเหตุ</summary>
    public static (bool Ok, string Message) OutboundResult(int? httpStatus, string? guardRefusal, string? error, long elapsedMs)
    {
        if (guardRefusal != null) return (false, "ด่านตรวจปลายทางขาออกปฏิเสธ: " + guardRefusal);
        if (httpStatus is int code) return (true, $"ได้คำตอบ HTTP {code} ใน {elapsedMs} ms — โฮสต์เชื่อมต่อขาออกได้โดยไม่ผ่าน proxy");
        return (false, "เชื่อมต่อไม่สำเร็จ: " + (string.IsNullOrWhiteSpace(error) ? "ไม่ทราบสาเหตุ" : error)
                       + " — ถ้าโฮสต์บังคับใช้ proxy ขาออก ต้องตั้งเส้น webhook/คีย์ให้ resolve + ตรึง IP ผ่าน proxy (งานใหม่ · คำตัดสิน Y8)");
    }
}
