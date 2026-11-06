using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Payments.Providers;

/// <summary>
/// Adapter สำหรับ Opn Payments (Omise) — **ไฟล์นี้คือที่เดียวในระบบที่รู้จักชื่อเจ้านี้**
/// (บังคับด้วย <c>tools/payment_provider_boundary_check.py</c>)
///
/// ═══ ข้อเท็จจริงของเจ้านี้ที่กำหนดรูปของโค้ด (PAYMENT_GATEWAY_DESIGN.md §2) ═══
/// <list type="bullet">
/// <item><b>คีย์เป็นคู่</b>: public (ฝั่งเบราว์เซอร์) + secret (ฝั่งเซิร์ฟเวอร์เท่านั้น) ·
///   test กับ live เป็นคนละคู่ ⇒ "โหมดทดสอบ" ต้องเลือก**คู่คีย์** ไม่ใช่แค่ธง</item>
/// <item><b>บัตร</b>: สคริปต์ฝั่งเบราว์เซอร์ทำ tokenization → ได้ token → เซิร์ฟเวอร์สร้าง
///   charge ด้วย token · <b>เลขบัตรไม่ผ่านเซิร์ฟเวอร์เรา</b> (PCI-DSS SAQ-A)</item>
/// <item><b>PromptPay</b>: สร้าง source → charge อยู่สถานะ pending → ลูกค้าสแกนจ่าย →
///   webhook · เป็น flow <b>อสมวาร</b> ⇒ ต้องมี poll เป็น fallback และ QR มีวันหมดอายุ</item>
/// <item><b>Webhook ไม่มีลายเซ็น HMAC</b> — วิธียืนยันคือ <b>fetch event กลับไปถามด้วย
///   secret key ของเรา</b> แล้วเชื่อเฉพาะข้อมูลที่ fetch มา ไม่ใช่ body ที่ใครก็ POST ได้ ·
///   นี่คือเหตุผลที่ webhook receiver เดิมของระบบ (HMAC ของเราเอง) ใช้กับเจ้านี้ไม่ได้</item>
/// <item><b>Idempotency เป็นหน้าที่ฝั่งเรา</b> — อ้างอิง intent ผ่าน metadata บน charge</item>
/// </list>
///
/// <para><b>ไม่ใช้ SDK</b> — เรพนี้เลี่ยง transitive deps (กฎ MiniExcel-only) · คุยผ่าน
/// named <c>HttpClient</c> ที่ตั้ง base address/timeout ไว้ที่ <c>Program.cs</c></para>
/// </summary>
public class OmisePaymentProvider : IPaymentProvider
{
    public const string Code = "omise";
    /// <summary>ชื่อ named HttpClient — ตั้งค่าใน Program.cs</summary>
    public const string HttpClientName = "payments:omise";

    private const string ApiBase = "https://api.omise.co";
    /// <summary>รอบ 250 WP-02 (Y8): ปลายทางทดสอบการเชื่อมต่อขาออกของการ์ด go-live — โดเมนของเจ้าอยู่ใน adapter เท่านั้น (payment_provider_boundary_check)</summary>
    public const string OutboundHealthCheckUrl = ApiBase;

    /// <summary>รุ่น API ที่ adapter นี้อ่านชื่อช่อง — <b>ปักทุกคำขอ</b> (หัว <c>Omise-Version</c> · คำตัดสินรอบ 200 ข้อ 18)
    ///
    /// <para>ไม่ปัก = รุ่นตั้งต้นของบัญชีผู้ขายแต่ละราย ⇒ ชื่อช่องเปลี่ยนตามบัญชี: รุ่น 2017-11-02 ใช้ <c>refunded</c> (เป็นยอด) แต่ adapter อ่าน
    /// <c>refunded_amount</c> (เปลี่ยนชื่อในรุ่น 2019-05-29) ⇒ ยอดคืนสะสม = null เงียบ ๆ ⇒ "ตรวจผลการคืนเงิน" ได้ ProviderSilent ตลอด ·
    /// ช่องที่ adapter อ่านทั้งหมด (<c>status</c> · <c>paid</c> · <c>fee</c> · <c>refunded_amount</c> · <c>refunds{data,total}</c> ·
    /// <c>source.scannable_code</c> · <c>authorize_uri</c> · <c>metadata</c> · <c>failure_*</c>) เป็นชื่อของรุ่นนี้ ·
    /// เปลี่ยนรุ่น = ต้องไล่ทุกช่องใน <see cref="ParseCharge"/> แล้วทดสอบใน sandbox ก่อน</para></summary>
    public const string ApiVersion = "2019-05-29";
    /// <summary>ชื่อหัวที่ใช้ปักรุ่น API</summary>
    public const string ApiVersionHeader = "Omise-Version";
    private const string VaultBase = "https://vault.omise.co";
    private const string CdnBase = "https://cdn.omise.co";
    /// <summary>สคริปต์ฝั่งเบราว์เซอร์ที่แปลงเลขบัตรเป็นโทเคน · หน้าเว็บขอค่านี้จาก API
    /// ไม่ hard-code เอง (ไฟล์อื่นห้ามรู้จักโดเมนนี้)</summary>
    string? IPaymentProvider.ClientScriptUrl => CdnBase + "/omise.js";

    /// <summary>รอบ 228: คีย์สาธารณะของโหมดปัจจุบัน (ตัวเดียวกับ <see cref="PublicKey"/>) — หน้าชำระใช้แปลงเลขบัตรเป็นโทเคนในเบราว์เซอร์</summary>
    string? IPaymentProvider.ClientPublicKey(PaymentProviderConfig config) => PublicKey(config);

    /// <summary>รอบ 201 ทีม GW (BACKLOG B-1 · team-G คำถามค้าง 2): รุ่น 2019-05-29 ช่อง <c>fee</c> = ค่าธรรมเนียม<b>ก่อน VAT</b> และมี <c>fee_vat</c> แยก —
    /// adapter ยังไม่อ่าน <c>fee_vat</c> และยังไม่ได้ยืนยันใน sandbox ว่าการปักรุ่นไม่เปลี่ยนช่องอื่น ⇒ เตือนล่วงหน้าบนหน้าตั้งค่าเท่านั้น
    /// (<b>ห้ามต่อสาย <c>fee_vat</c></b> จนกว่ามีผล sandbox — รายการทดสอบอยู่ PAYMENT_GATEWAY_DESIGN.md §4.4)</summary>
    string? IPaymentProvider.PendingVerificationNotice =>
        "ค่าธรรมเนียมที่ระบบอ่านจากผู้ให้บริการรายนี้เป็นยอดก่อน VAT (รุ่น API " + ApiVersion + ") และยังไม่ได้ยืนยันกับระบบทดสอบของผู้ให้บริการ — "
        + "รอบโอนแรกหลังเปิดใช้หรือหลังอัปเดตระบบอาจยอดไม่ตรงสเตทเมนต์: ดูตัวอย่างรอบโอนเทียบยอดที่เข้าธนาคารจริงก่อนบันทึก "
        + "(ระบบบล็อกเมื่อยอดไม่ตรงอยู่แล้ว ไม่ลงบัญชีผิดเงียบ) · ถ้าใบกำกับของผู้ให้บริการแยก VAT ให้ตั้ง \"VAT ของค่าธรรมเนียม\" เป็น "
        + "\"หัก VAT 7% เพิ่มจากค่าธรรมเนียม\" · ยอดไม่ตรงเพราะค่าธรรมเนียม ให้แก้ค่าธรรมเนียมรายรายการตามสเตทเมนต์ที่หน้ารายการรับชำระออนไลน์";

    /// <summary>เลขรายการที่ event อ้าง (<c>data.metadata.intentId</c>) จากเนื้อคำขอที่ยังไม่ยืนยัน — ไม่มีคำขอออก · ใช้แค่ประทับคำเตือน URL เดิม (GWO-7)</summary>
    Guid? IPaymentProvider.UnverifiedIntentHint(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                   && meta.TryGetProperty("intentId", out var el) && el.ValueKind == JsonValueKind.String
                   && Guid.TryParse(el.GetString(), out var id)
                ? id : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>โดเมนที่ CSP ต้องอนุญาต — ประกาศที่นี่ ไม่ใช่ใน SecurityMiddleware
    /// เพื่อให้การเพิ่มเจ้าใหม่ไม่ต้องแตะไฟล์นอกโฟลเดอร์นี้ (เกณฑ์ผ่านเฟส 6)</summary>
    public ProviderCspNeeds CspNeeds { get; } = new(
        ScriptSrc: new[] { CdnBase },
        // vault = ปลายทางที่สคริปต์ส่งเลขบัตรไปแลกโทเคน · api = ตรวจสถานะจากหน้า return
        ConnectSrc: new[] { VaultBase, ApiBase },
        // 3-D Secure เปิดหน้าธนาคารใน iframe
        FrameSrc: new[] { ApiBase });

    private readonly IHttpClientFactory _http;
    private readonly ISecretProtector _secrets;
    private readonly ILogger<OmisePaymentProvider> _logger;

    public OmisePaymentProvider(IHttpClientFactory http, ISecretProtector secrets,
        ILogger<OmisePaymentProvider> logger)
    { _http = http; _secrets = secrets; _logger = logger; }

    public string ProviderCode => Code;

    public PaymentCapabilities Capabilities { get; } = new(
        Methods: new HashSet<PaymentMethodKind>
        {
            PaymentMethodKind.PromptPay, PaymentMethodKind.Card,
            PaymentMethodKind.MobileBanking, PaymentMethodKind.InternetBanking,
            PaymentMethodKind.TrueMoney,
        },
        SupportsRefund: true, SupportsPartialRefund: true, SupportsWebhook: true);

    // ── คีย์ ────────────────────────────────────────────────────────────
    private string SecretKey(PaymentProviderConfig cfg)
    {
        var raw = cfg.Mode == PaymentProviderMode.Live
            ? cfg.LiveSecretKeyProtected : cfg.TestSecretKeyProtected;
        var key = _secrets.Unprotect(raw);
        if (string.IsNullOrWhiteSpace(key))
            // fail loud: คีย์ว่างแปลว่ายังตั้งค่าไม่เสร็จ — ถ้าปล่อยผ่าน ผู้ใช้จะเห็นแค่
            // "จ่ายไม่สำเร็จ" โดยไม่รู้ว่าต้องไปแก้ที่ไหน
            throw new InvalidOperationException(
                cfg.Mode == PaymentProviderMode.Live
                    ? "ยังไม่ได้ตั้งค่า secret key สำหรับโหมดใช้งานจริง"
                    : "ยังไม่ได้ตั้งค่า secret key สำหรับโหมดทดสอบ");
        return key;
    }

    /// <summary>public key ที่ปลอดภัยจะส่งให้เบราว์เซอร์ — ไม่ผ่าน protector
    /// เพราะไม่ใช่ความลับ (ออกแบบมาให้เปิดเผย)</summary>
    public static string? PublicKey(PaymentProviderConfig cfg)
        => cfg.Mode == PaymentProviderMode.Live ? cfg.LivePublicKey : cfg.TestPublicKey;

    private HttpClient Client(PaymentProviderConfig cfg, string baseUrl)
    {
        var c = _http.CreateClient(HttpClientName);
        c.BaseAddress = new Uri(baseUrl);
        // HTTP Basic: secret key เป็นชื่อผู้ใช้ รหัสผ่านว่าง
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(SecretKey(cfg) + ":"));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        // รอบ 200 ข้อ 18: ปักรุ่น API — ชื่อช่องที่อ่านต้องไม่ขึ้นกับรุ่นตั้งต้นของบัญชีผู้ขาย
        c.DefaultRequestHeaders.Remove(ApiVersionHeader);
        c.DefaultRequestHeaders.Add(ApiVersionHeader, ApiVersion);
        return c;
    }

    // ── แปลงศัพท์ของเจ้านี้ ↔ ภาษากลาง ──────────────────────────────────
    private static string SourceType(PaymentMethodKind kind) => kind switch
    {
        PaymentMethodKind.PromptPay => "promptpay",
        PaymentMethodKind.TrueMoney => "truemoney",
        PaymentMethodKind.MobileBanking => "mobile_banking_kbank",
        PaymentMethodKind.InternetBanking => "internet_banking_bay",
        _ => "promptpay",
    };

    /// <summary>แปลงสถานะดิบเป็นภาษากลาง — <b>ที่เดียว</b> ที่รู้จักคำของเจ้านี้
    ///
    /// <para>สถานะที่ไม่รู้จักถือเป็น <c>Pending</c> ไม่ใช่ <c>Failed</c> — เดาว่าล้มเหลว
    /// แล้วปิดใบทิ้งทั้งที่เงินอาจเข้าจริง คือความผิดพลาดที่แพงกว่าการรอต่ออีกนิด</para></summary>
    private static PaymentIntentStatus MapStatus(string? raw, bool? paid) => raw switch
    {
        "successful" => PaymentIntentStatus.Succeeded,
        "failed" => PaymentIntentStatus.Failed,
        "expired" => PaymentIntentStatus.Expired,
        "reversed" => PaymentIntentStatus.Refunded,
        "pending" => paid == true ? PaymentIntentStatus.Succeeded : PaymentIntentStatus.Pending,
        _ => PaymentIntentStatus.Pending,
    };

    private static decimal FromMinorUnit(long satang) => satang / 100m;
    private static long ToMinorUnit(decimal baht)
        => (long)Math.Round(baht * 100m, 0, MidpointRounding.AwayFromZero);

    private static ProviderCharge ParseCharge(JsonElement el)
    {
        var status = el.TryGetProperty("status", out var st) ? st.GetString() : null;
        bool? paid = el.TryGetProperty("paid", out var p) && p.ValueKind == JsonValueKind.True;

        string? qr = null, expires = null, authorize = null;
        if (el.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
        {
            if (src.TryGetProperty("scannable_code", out var sc)
                && sc.TryGetProperty("image", out var img)
                && img.TryGetProperty("download_uri", out var uri))
                qr = uri.GetString();
            if (src.TryGetProperty("expires_at", out var ex) && ex.ValueKind == JsonValueKind.String) expires = ex.GetString();
            // รอบ 227 (PLATFORM_OMISE_AUDIT A-7): วันหมดอายุของ QR อยู่ที่ระดับ charge (<c>expires_at</c>) — source ไม่มีช่องนี้ ⇒ เดิม QrExpiresAt = null เสมอ
            // (หน้าจ่ายไม่รู้เวลาหมด · รายการค้าง Pending จนผู้ให้บริการตอบ expired) · อ่านเฉพาะ charge ที่มี source (ไม่ใช่บัตร — บัตรไม่มี QR)
            if (expires == null && el.TryGetProperty("expires_at", out var cex) && cex.ValueKind == JsonValueKind.String) expires = cex.GetString();
        }
        if (el.TryGetProperty("authorize_uri", out var au) && au.ValueKind == JsonValueKind.String) authorize = au.GetString();
        // รอบ 232: charge พร้อมเพย์ (flow offline) มาพร้อม **ทั้ง** QR และ authorize_uri (หน้า offsite ของ flow redirect) — เก็บทั้งคู่แล้วหน้าจ่าย
        // พาออกไปหน้า offsite ซึ่งส่งกลับ return_uri ทันที ⇒ "เด้งกลับหน้าเดิม" ไม่เห็น QR · ตัวตัดสินเดียว GatewayChargeNextStep: QR ชนะ · ลิงก์เฉพาะเมื่อไม่มี QR
        var next = GatewayChargeNextStep.Decide(qr, authorize);
        qr = next.QrPayload;
        authorize = next.AuthorizeUrl;

        DateTime? expiresAt = DateTime.TryParse(expires, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var exd)
            ? exd : null;

        var refunds = ParseRefundList(el);
        // ยอดคืนสะสม: ช่อง refunded_amount ก่อน · ไม่มีช่องนั้น ⇒ ผลรวมของรายการคืน (เฉพาะเมื่อรายการครบทั้งชุด · ฝ่ายค้าน E2-3) · ไม่มีทั้งคู่ = null (ไม่เดาเป็น 0)
        decimal? refundedTotal = el.TryGetProperty("refunded_amount", out var ra) && ra.ValueKind == JsonValueKind.Number
            ? FromMinorUnit(ra.GetInt64())
            : refunds?.Sum(r => r.Amount);

        return new ProviderCharge(
            ProviderRef: el.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Status: MapStatus(status, paid),
            RawStatus: status,
            Amount: el.TryGetProperty("amount", out var amt) ? FromMinorUnit(amt.GetInt64()) : 0m,
            Fee: el.TryGetProperty("fee", out var fee) && fee.ValueKind == JsonValueKind.Number
                ? FromMinorUnit(fee.GetInt64()) : null,
            QrPayload: qr,
            QrExpiresAt: expiresAt,
            AuthorizeUrl: authorize,
            FailureCode: el.TryGetProperty("failure_code", out var fc) && fc.ValueKind == JsonValueKind.String ? fc.GetString() : null,
            // รอบ 227 (A-10): ข้อความถึงผู้จ่ายเป็นไทยจากรหัส (OmiseFailureText ตัวเดียว) — เดิมเก็บ failure_message ภาษาอังกฤษตรง ๆ
            FailureMessage: OmiseFailureText.Describe(
                el.TryGetProperty("failure_code", out var fc2) && fc2.ValueKind == JsonValueKind.String ? fc2.GetString() : null,
                el.TryGetProperty("failure_message", out var fm) && fm.ValueKind == JsonValueKind.String ? fm.GetString() : null),
            // ยอดคืนสะสมของ charge — ใช้ตรวจการคืนเงินที่ผลไม่แน่ชัด (ฝ่ายค้าน E-2)
            RefundedTotal: refundedTotal,
            Refunds: refunds,
            // รอบ 219 ส่วน B ฝ่ายค้าน B3: โหมดของ charge ตามผู้ให้บริการ — เทียบกับโหมดของ intent ก่อนเปลี่ยนสถานะ
            LiveMode: el.TryGetProperty("livemode", out var lmode) && lmode.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? lmode.GetBoolean() : null);
    }

    /// <summary>รายการคืนเงินที่ฝังมากับ charge (list object: <c>data</c> + <c>total</c>) — คืนเฉพาะเมื่อ<b>ครบทั้งชุด</b>
    /// (<c>total</c> = จำนวนใน <c>data</c>) · รายการที่ถูกยกเลิก (<c>voided</c>) ไม่นับ · ช่องยอดอ่านไม่ได้/ถูกตัดหน้า = null
    /// (ไม่ครบ ≠ ไม่มี — ห้ามใช้หาว่า "ไม่มีครั้งนี้") · เครื่องหมายของครั้งที่สั่งคืนอยู่ที่ <c>metadata.attempt</c> (ฝ่ายค้าน E2-2)</summary>
    private static List<ProviderRefundItem>? ParseRefundList(JsonElement charge)
    {
        if (!charge.TryGetProperty("refunds", out var list) || list.ValueKind != JsonValueKind.Object) return null;
        if (!list.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        if (!list.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Number
            || total.GetInt64() != data.GetArrayLength()) return null;

        var items = new List<ProviderRefundItem>();
        foreach (var r in data.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (r.TryGetProperty("voided", out var voided) && voided.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("amount", out var amt) || amt.ValueKind != JsonValueKind.Number) return null;
            string? marker = null;
            if (r.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("attempt", out var att) && att.ValueKind == JsonValueKind.String)
                marker = att.GetString();
            var id = r.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
            items.Add(new ProviderRefundItem(id, FromMinorUnit(amt.GetInt64()), marker));
        }
        return items;
    }

    // ── การทำงานหลัก ────────────────────────────────────────────────────

    public async Task<ProviderCharge> CreateChargeAsync(PaymentIntent intent, ChargeRequest req,
        PaymentProviderConfig config, CancellationToken ct = default)
    {
        using var client = Client(config, ApiBase);

        var form = new List<KeyValuePair<string, string>>
        {
            new("amount", ToMinorUnit(intent.Amount).ToString(CultureInfo.InvariantCulture)),
            new("currency", intent.Currency.ToLowerInvariant()),
            new("description", req.Description),
            // อ้างอิงกลับมาที่ intent ผ่าน metadata — webhook resolve จากตรงนี้
            // **ไม่ใช่จาก URL** (URL ปลอมได้ · metadata มาจาก charge ที่เรา fetch เอง)
            new("metadata[intentId]", intent.Id.ToString()),
            new("metadata[companyId]", intent.CompanyId.ToString()),
        };

        if (req.Kind == PaymentMethodKind.Card)
        {
            if (!string.IsNullOrWhiteSpace(req.CustomerRef))
            {
                // รอบ 230 (§5.2): ตัดด้วยบัตรที่บันทึกไว้ — customer + card ของลูกค้านั้น (ไม่มีโทเคน · เลขอ้างอิงต้องเป็นรูปของเจ้านี้ก่อนส่ง)
                if (!IsWellFormedRef(req.CustomerRef, "cust_"))
                    throw new InvalidOperationException("เลขอ้างอิงลูกค้าของบัตรที่บันทึกไว้ไม่ถูกต้อง");
                form.Add(new("customer", req.CustomerRef));
                if (!string.IsNullOrWhiteSpace(req.SavedCardRef))
                {
                    if (!IsWellFormedRef(req.SavedCardRef, "card_"))
                        throw new InvalidOperationException("เลขอ้างอิงบัตรที่บันทึกไว้ไม่ถูกต้อง");
                    form.Add(new("card", req.SavedCardRef));
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(req.CardToken))
                    throw new InvalidOperationException(
                        "ไม่พบโทเคนบัตร — เลขบัตรต้องถูกแปลงเป็นโทเคนในเบราว์เซอร์ก่อนเสมอ");
                form.Add(new("card", req.CardToken));
            }
            if (!string.IsNullOrWhiteSpace(req.ReturnUrl))
                form.Add(new("return_uri", req.ReturnUrl));   // 3-D Secure
        }
        else
        {
            form.Add(new("source[type]", SourceType(req.Kind)));
            // รอบ 227 (A-4): วอลเล็ทต้องระบุเบอร์ของผู้จ่ายใน source — ไม่ส่ง = ผู้ให้บริการปฏิเสธทุกครั้ง (หน้าชำระของ NextAcc ยังไม่เปิดวิธีนี้ · Helpers/PlatformPayMethods)
            // ฝ่ายค้าน N6: ตัวเลขล้วน ("081-234 5678" ⇒ "0812345678") · ไม่มีตัวเลขเลย = ไม่ส่ง
            if (req.Kind == PaymentMethodKind.TrueMoney && PhoneDigits(req.CustomerPhone) is { Length: > 0 } phone)
                form.Add(new("source[phone_number]", phone));
            if (!string.IsNullOrWhiteSpace(req.ReturnUrl))
                form.Add(new("return_uri", req.ReturnUrl));
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/charges") { Content = new FormUrlEncodedContent(form) };
        // รอบ 230 (§5.6 เฉพาะเส้นที่ส่งคีย์ — ตัดบัตรที่บันทึก): หัวกันซ้ำของคำขอ POST ตามเอกสารสาธารณะของเจ้านี้ · ⚠️ ต้องยืนยันชื่อหัว/พฤติกรรมใน sandbox
        if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
            msg.Headers.TryAddWithoutValidation(IdempotencyHeader, req.IdempotencyKey);
        using var resp = await client.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // ฝ่ายค้าน B1: 5xx/408 = ผู้ให้บริการอาจสร้าง charge แล้ว — ห้ามตอบว่า "ล้มเหลว" (ตัวแยกเดียวกับการคืนเงิน)
            if (GatewayRefundMath.ClassifyRefundHttpStatus((int)resp.StatusCode) == GatewayRefundHttpOutcome.Unknown)
                throw new ProviderOutcomeUnknownException($"ผู้ให้บริการตอบผิดพลาด ({(int)resp.StatusCode}) — ไม่รู้ว่าตัดเงินแล้วหรือยัง");
            // ฝ่ายค้าน (nice): ตัดบัตรที่บันทึกโดยไม่มีผู้ใช้ แล้วผู้ให้บริการขอ return_uri (3-D Secure) ⇒ ต้องให้ผู้จ่ายทำเอง · ⚠️ ข้อความ/รหัสต้องยืนยันใน sandbox
            if (!string.IsNullOrWhiteSpace(req.CustomerRef) && body.Contains("return_uri", StringComparison.OrdinalIgnoreCase))
                throw new ProviderActionRequiredException("ธนาคารขอยืนยันตัวตน (3-D Secure) — ตัดอัตโนมัติไม่ได้ กรุณาชำระด้วยตนเอง");
            throw new InvalidOperationException(FriendlyError(body, resp.StatusCode.ToString()));
        }

        using var doc = JsonDocument.Parse(body);
        return ParseCharge(doc.RootElement);
    }

    /// <summary>ฝ่ายค้าน B1: หา charge ของรายการจาก <c>metadata.intentId</c> ในรายการ charge ตั้งแต่ก่อนรายการถูกสร้าง (สูงสุด 3 หน้า) ·
    /// ⚠️ พารามิเตอร์ <c>from/order</c> ของรายการ charge ตามเอกสารสาธารณะ — ต้องยืนยันใน sandbox</summary>
    public async Task<ProviderCharge?> FindChargeForIntentAsync(PaymentIntent intent, PaymentProviderConfig config, CancellationToken ct = default)
    {
        using var client = Client(config, ApiBase);
        var from = DateTime.SpecifyKind(intent.CreatedAt, DateTimeKind.Utc).AddMinutes(-5)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        for (var page = 0; page < 3; page++)
        {
            using var resp = await client.GetAsync($"/charges?from={Uri.EscapeDataString(from)}&limit=100&offset={page * 100}&order=chronological", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(FriendlyError(body, resp.StatusCode.ToString()));
            using var doc = JsonDocument.Parse(body);
            if (FindChargeInList(doc.RootElement, intent.Id) is { } hit) return hit;
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() < 100) break;
        }
        return null;
    }

    /// <summary>charge ในรายการที่ <c>metadata.intentId</c> ตรง — ไม่พบ = null · pure</summary>
    internal static ProviderCharge? FindChargeInList(JsonElement list, Guid intentId)
    {
        if (!list.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var c in data.EnumerateArray())
            if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                && Str(meta, "intentId") is string id && Guid.TryParse(id, out var g) && g == intentId)
                return ParseCharge(c);
        return null;
    }

    /// <summary>ชื่อหัวกันซ้ำของคำขอสร้าง charge (รอบ 230 · ต้องยืนยันใน sandbox)</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    // ═══ รอบ 230 (PLATFORM_OMISE_AUDIT §5.2): บัตรที่บันทึกไว้ ═══
    // รูปข้อมูลตามเอกสารสาธารณะรุ่น 2019-05-29: token มี card{id,brand,last_digits,expiration_month,expiration_year,fingerprint} ·
    // POST /customers (card=โทเคน) สร้างลูกค้า+ผูกบัตร · PATCH /customers/{id} (card=โทเคน) ผูกบัตรเพิ่ม · DELETE /customers/{id}/cards/{card} ·
    // ⚠️ ต้องยืนยันใน sandbox: เลขบัตรหลังผูก = เลขบัตรในโทเคน (ถ้าไม่ตรง ใช้ลายนิ้วมือบัตรจับคู่ — ไม่ตรงทั้งคู่ = ล้มดัง ไม่เดา)

    bool IPaymentProvider.SupportsSavedCards => true;

    public async Task<ProviderSavedCard> SaveCardAsync(PaymentProviderConfig config, string cardToken, string? existingCustomerRef,
        string customerDescription, string? customerEmail, CancellationToken ct = default)
    {
        if (!IsWellFormedRef(cardToken, "tokn_")) throw new InvalidOperationException("โทเคนบัตรไม่ถูกต้อง — กรอกบัตรใหม่อีกครั้ง");
        if (existingCustomerRef != null && !IsWellFormedRef(existingCustomerRef, "cust_"))
            throw new InvalidOperationException("เลขอ้างอิงลูกค้าของบัตรที่บันทึกไว้ไม่ถูกต้อง");
        using var client = Client(config, ApiBase);

        // 1) อ่านบัตรในโทเคน (ไม่ใช้โทเคน) — ได้เลขบัตร/ลายนิ้วมือไว้หาบัตรใบนี้หลังผูก
        using var tokResp = await client.GetAsync($"/tokens/{cardToken}", ct);
        var tokBody = await tokResp.Content.ReadAsStringAsync(ct);
        if (!tokResp.IsSuccessStatusCode) throw new InvalidOperationException(FriendlyError(tokBody, tokResp.StatusCode.ToString()));
        string? tokCardId, tokFingerprint;
        using (var tokDoc = JsonDocument.Parse(tokBody))
        {
            if (!tokDoc.RootElement.TryGetProperty("card", out var tc) || tc.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("ผู้ให้บริการไม่ส่งข้อมูลบัตรของโทเคน — กรอกบัตรใหม่อีกครั้ง");
            tokCardId = Str(tc, "id");
            tokFingerprint = Str(tc, "fingerprint");
        }

        // 2) สร้างลูกค้า (หรือผูกบัตรเพิ่มให้ลูกค้าเดิม) ด้วยโทเคน
        var form = new List<KeyValuePair<string, string>> { new("card", cardToken) };
        HttpResponseMessage resp;
        if (existingCustomerRef == null)
        {
            form.Add(new("description", customerDescription.Length > 200 ? customerDescription[..200] : customerDescription));
            if (!string.IsNullOrWhiteSpace(customerEmail)) form.Add(new("email", customerEmail.Trim()));
            resp = await client.PostAsync("/customers", new FormUrlEncodedContent(form), ct);
        }
        else
            resp = await client.PatchAsync($"/customers/{existingCustomerRef}", new FormUrlEncodedContent(form), ct);
        using var ownedResp = resp;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(FriendlyError(body, resp.StatusCode.ToString()));
        using var doc = JsonDocument.Parse(body);
        return ParseSavedCustomerCard(doc.RootElement, tokCardId, tokFingerprint)
            ?? throw new InvalidOperationException("บันทึกบัตรแล้วแต่หาบัตรใบนี้ในข้อมูลลูกค้าของผู้ให้บริการไม่พบ — ติดต่อผู้ดูแลระบบ (ยังไม่มีการตัดเงิน)");
    }

    /// <summary>หาบัตรใบที่เพิ่งผูกจากข้อมูลลูกค้า: เลขบัตรตรงโทเคนก่อน → ลายนิ้วมือตรง (ใบล่าสุด) → ไม่พบ = null (ห้ามเดาใบอื่น) · pure</summary>
    internal static ProviderSavedCard? ParseSavedCustomerCard(JsonElement customer, string? tokenCardId, string? tokenFingerprint)
    {
        var custId = Str(customer, "id");
        if (!IsWellFormedRef(custId, "cust_")) return null;
        if (!customer.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Object
            || !cards.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        JsonElement? hit = null;
        foreach (var c in data.EnumerateArray())
            if (c.ValueKind == JsonValueKind.Object && tokenCardId != null && Str(c, "id") == tokenCardId) { hit = c; break; }
        if (hit == null && !string.IsNullOrEmpty(tokenFingerprint))
            hit = data.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.Object && Str(c, "fingerprint") == tokenFingerprint)
                .OrderByDescending(c => Str(c, "created_at") ?? Str(c, "created") ?? "", StringComparer.Ordinal)
                .Select(c => (JsonElement?)c).FirstOrDefault();
        if (hit is not JsonElement card) return null;
        var cardId = Str(card, "id");
        if (!IsWellFormedRef(cardId, "card_")) return null;
        int? Int(string name) => card.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? (int?)n : null;
        bool? live = card.TryGetProperty("livemode", out var lm) && lm.ValueKind is JsonValueKind.True or JsonValueKind.False ? (bool?)lm.GetBoolean()
            : customer.TryGetProperty("livemode", out var clm) && clm.ValueKind is JsonValueKind.True or JsonValueKind.False ? (bool?)clm.GetBoolean() : null;
        return new ProviderSavedCard(custId!, cardId!, Str(card, "brand"), Str(card, "last_digits"),
            Int("expiration_month"), Int("expiration_year"), live);
    }

    public async Task<ProviderCardRemoval> RemoveSavedCardAsync(PaymentProviderConfig config, string customerRef, string cardRef,
        CancellationToken ct = default)
    {
        if (!IsWellFormedRef(customerRef, "cust_") || !IsWellFormedRef(cardRef, "card_"))
            return new ProviderCardRemoval(false, "เลขอ้างอิงบัตรไม่ถูกต้อง");
        HttpClient client;
        try { client = Client(config, ApiBase); }
        catch (InvalidOperationException ex) { return new ProviderCardRemoval(false, ex.Message); }
        using var owned = client;
        using var resp = await client.DeleteAsync($"/customers/{customerRef}/cards/{cardRef}", ct);
        if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new ProviderCardRemoval(true, null);
        return new ProviderCardRemoval(false, FriendlyError(await resp.Content.ReadAsStringAsync(ct), resp.StatusCode.ToString()));
    }

    // ═══ รอบ 230 (PLATFORM_OMISE_AUDIT §5.3): รอบโอน (transfers) ═══
    // ⚠️ ต้องยืนยันใน sandbox: (ก) amount คือยอดเข้าธนาคารและค่าธรรมเนียมโอนหักเพิ่มจากยอดคงเหลือ หรือมี net แยก
    // (ข) ช่อง transactions ของรอบโอนชี้ charge ได้หรือไม่ — ไม่ได้ ⇒ ChargeRefs = null (ผู้เรียกจับคู่ด้วยยอด + ช่วงเวลา ไม่เดาจากช่องอื่น)

    public const int TransferPageSize = 100, TransferMaxPages = 10;

    public async Task<IReadOnlyList<ProviderSettlement>> ListSettlementsAsync(PaymentProviderConfig config, DateTime sinceUtc,
        CancellationToken ct = default)
    {
        using var client = Client(config, ApiBase);
        var list = new List<ProviderSettlement>();
        var from = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        for (var page = 0; page < TransferMaxPages; page++)
        {
            var url = $"/transfers?from={Uri.EscapeDataString(from)}&limit={TransferPageSize}&offset={page * TransferPageSize}&order=chronological";
            using var resp = await client.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(FriendlyError(body, resp.StatusCode.ToString()));
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
            var n = 0;
            foreach (var t in data.EnumerateArray())
            {
                n++;
                if (ParseTransfer(t) is { } s) list.Add(s);
            }
            if (n < TransferPageSize) break;
        }
        return list;
    }

    /// <summary>รอบโอนหนึ่งรอบ → ภาษากลาง · pure (เทสต์ได้) · ช่องที่ขาด = ไม่เดา</summary>
    internal static ProviderSettlement? ParseTransfer(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) return null;
        var id = Str(t, "id");
        if (!IsWellFormedRef(id, "trsf_")) return null;
        if (t.TryGetProperty("deleted", out var del) && del.ValueKind == JsonValueKind.True) return null;
        decimal? Money(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (decimal?)FromMinorUnit(v.GetInt64()) : null;
        var amount = Money("amount") ?? 0m;
        var fee = Money("fee") ?? 0m;
        var feeVat = Money("fee_vat") ?? 0m;
        var net = Money("net");
        decimal walletDebit, netToBank;
        string? note;
        if (net is decimal n)
        {
            // มี net แยก ⇒ ค่าธรรมเนียมหักจากยอดโอน: ยอดที่ออกจาก wallet = amount · เข้าธนาคาร = net
            walletDebit = amount; netToBank = n;
            var feeTotal = amount - n;
            note = feeTotal != fee + feeVat
                ? $"ยอดโอน − ยอดสุทธิ ({feeTotal:N2}) ไม่เท่าค่าธรรมเนียม+VAT ที่รายงาน ({fee + feeVat:N2}) — ใช้ยอดสุทธิเป็นตัวตั้ง" : null;
            // ฝ่ายค้าน (nice): คงบรรทัด VAT ค่าธรรมเนียมที่รายงานไว้ — ปรับเฉพาะส่วนค่าธรรมเนียม (ยอดสุทธิเป็นตัวตั้ง)
            if (feeTotal != fee + feeVat) { feeVat = Math.Min(feeVat, Math.Max(0m, feeTotal)); fee = feeTotal - feeVat; }
        }
        else
        {
            // ไม่มี net ⇒ ถือว่า amount เข้าธนาคารเต็ม และค่าธรรมเนียมโอนหักเพิ่มจากยอดคงเหลือ (ต้องยืนยันใน sandbox)
            walletDebit = amount + fee + feeVat; netToBank = amount;
            note = fee + feeVat > 0m ? "ถือว่าค่าธรรมเนียมโอนหักจากยอดคงเหลือเพิ่มจากยอดโอน (ต้องยืนยันใน sandbox)" : null;
        }
        List<string>? charges = null;
        if (t.TryGetProperty("transactions", out var txs) && txs.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in txs.EnumerateArray())
            {
                string? chargeId = x.ValueKind == JsonValueKind.String ? x.GetString()
                    : x.ValueKind == JsonValueKind.Object ? (Str(x, "origin") ?? Str(x, "source")) : null;
                if (IsWellFormedChargeId(chargeId)) (charges ??= new List<string>()).Add(chargeId!);
            }
        }
        DateTime? When(string name) => DateTime.TryParse(Str(t, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? (DateTime?)d : null;
        bool? live = t.TryGetProperty("livemode", out var lm) && lm.ValueKind is JsonValueKind.True or JsonValueKind.False ? (bool?)lm.GetBoolean() : null;
        var paid = t.TryGetProperty("paid", out var pd) && pd.ValueKind == JsonValueKind.True;
        var status = Str(t, "status") ?? (paid ? "paid" : t.TryGetProperty("sent", out var sn) && sn.ValueKind == JsonValueKind.True ? "sent" : "pending");
        return new ProviderSettlement(id!, walletDebit, netToBank, fee, feeVat, status, paid,
            When("created_at") ?? When("created"), When("paid_at") ?? When("sent_at"),
            charges?.Distinct().ToList(), live, note);
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>รูปเลขอ้างอิงของเจ้านี้ (prefix + ตัวอักษร/ตัวเลข/ขีดล่าง) — ด่านก่อนต่อเข้า path/ส่งเป็นค่า · pure</summary>
    internal static bool IsWellFormedRef(string? id, string prefix)
        => id is { Length: > 5 and <= 100 }
           && id.StartsWith(prefix, StringComparison.Ordinal)
           && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    public async Task<ProviderCharge> GetChargeAsync(PaymentIntent intent,
        PaymentProviderConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(intent.ProviderRef))
            return new ProviderCharge("", intent.Status, intent.ProviderStatusRaw, intent.Amount);

        using var client = Client(config, ApiBase);
        using var resp = await client.GetAsync($"/charges/{intent.ProviderRef}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(FriendlyError(body, resp.StatusCode.ToString()));

        using var doc = JsonDocument.Parse(body);
        return ParseCharge(doc.RootElement);
    }

    public async Task<ProviderRefund> RefundAsync(PaymentIntent intent, decimal amount,
        string reason, string attemptMarker, PaymentProviderConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(intent.ProviderRef))
            return new ProviderRefund("", amount, false, "รายการนี้ไม่มีเลขอ้างอิงจากผู้ให้บริการ");

        HttpClient client;
        try
        {
            client = Client(config, ApiBase);
        }
        catch (InvalidOperationException ex)
        {
            // คีย์ยังไม่ได้ตั้ง = ยังไม่มีคำขอออกไปเลย ⇒ ปฏิเสธที่ชัดเจน (เดิมโยน ⇒ ถูกตีเป็น "ผลไม่แน่ชัด" + ล็อกผิด · review198-E2 E2-12)
            return new ProviderRefund("", amount, false, ex.Message);
        }
        using var ownedClient = client;
        var form = new List<KeyValuePair<string, string>>
        {
            new("amount", ToMinorUnit(amount).ToString(CultureInfo.InvariantCulture)),
            new("metadata[reason]", reason),
            // เครื่องหมายของครั้งนี้ — การตรวจผลทีหลังหาในรายการคืนของ charge (ฝ่ายค้าน E2-2)
            new("metadata[attempt]", attemptMarker),
        };
        using var resp = await client.PostAsync($"/charges/{intent.ProviderRef}/refunds",
            new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        switch (GatewayRefundMath.ClassifyRefundHttpStatus((int)resp.StatusCode))
        {
            case GatewayRefundHttpOutcome.Refused:
                return new ProviderRefund("", amount, false, FriendlyError(body, resp.StatusCode.ToString()));
            case GatewayRefundHttpOutcome.Unknown:
                // E2-1: 5xx/408 = ผู้ให้บริการอาจบันทึกแล้ว — ห้ามตอบว่า "ปฏิเสธ" (กดใหม่ = คืนซ้ำได้)
                return new ProviderRefund("", amount, false,
                    $"ผู้ให้บริการตอบผิดพลาด ({(int)resp.StatusCode}) — ไม่รู้ว่าคืนเงินแล้วหรือยัง", OutcomeUnknown: true);
        }

        using var doc = JsonDocument.Parse(body);
        return new ProviderRefund(
            doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            amount, true);
    }

    /// <summary>ยืนยัน webhook ด้วยการ **fetch event กลับไปถามเอง**
    ///
    /// <para>เจ้านี้ไม่ส่งลายเซ็นมากับ webhook ⇒ body ที่เข้ามาเป็นเพียง "คำใบ้ว่ามีอะไร
    /// เกิดขึ้น" · เราอ่านแค่ <c>id</c> ของ event แล้วไปถามด้วย secret key ของเรา
    /// **แล้วเชื่อเฉพาะสิ่งที่ fetch กลับมา** — ใครก็ POST เข้ามาได้ แต่ปลอม event id
    /// ที่มีอยู่จริงในบัญชีของบริษัทนี้ไม่ได้</para>
    ///
    /// <para>intent resolve จาก <c>metadata.intentId</c> ของ charge ที่ fetch มา
    /// ไม่ใช่จาก URL — URL ปลอมได้ · metadata มาจากข้อมูลที่เราเป็นคนใส่ตอนสร้าง charge</para></summary>
    public async Task<VerifiedWebhookEvent?> VerifyWebhookAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers,
        PaymentProviderConfig config, CancellationToken ct = default)
    {
        string? eventId;
        try
        {
            using var hint = JsonDocument.Parse(rawBody);
            eventId = hint.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        catch (JsonException)
        {
            _logger.LogWarning("webhook: body ไม่ใช่ JSON ที่อ่านได้ — ปฏิเสธ");
            return null;
        }
        if (string.IsNullOrWhiteSpace(eventId)) return null;
        // รอบ 200 ทีม G: เลข event มาจาก body ที่ใครก็ POST ได้ แล้วถูกต่อเข้า path ของคำขอที่แนบ secret key ของบริษัท —
        // ต้องเป็นรูปเลข event เท่านั้น (กัน "../charges/…" / "?…" พาคำขอไปปลายทางอื่นด้วยคีย์ของร้าน) · ไม่ผ่าน = ไม่ยิงออกเลย
        if (!IsWellFormedEventId(eventId)) return null;

        try
        {
            using var client = Client(config, ApiBase);
            using var resp = await client.GetAsync($"/events/{eventId}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 = event นี้ไม่มีในบัญชีของบริษัทนี้ ⇒ ของปลอม หรือส่งผิดบริษัท
                _logger.LogWarning("webhook: ตรวจสอบ event {Event} ไม่ผ่าน ({Status})",
                    eventId, resp.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) return null;

            // รอบ 228 (§5.4): ข้อพิพาท/chargeback (charge.dispute.*) — data เป็น dispute ที่ชี้ charge ⇒ fetch charge กลับมาหา intent (metadata ของเราเอง)
            if (data.TryGetProperty("object", out var dObj) && dObj.GetString() == "dispute")
                return await VerifyDisputeAsync(client, eventId!, data, ct);

            // สนใจเฉพาะ event ที่เกี่ยวกับ charge — refund มีเส้นทางของตัวเอง
            if (!data.TryGetProperty("object", out var objEl) || objEl.GetString() != "charge")
                return null;

            if (!data.TryGetProperty("metadata", out var meta)
                || !meta.TryGetProperty("intentId", out var intentEl)
                || !Guid.TryParse(intentEl.GetString(), out var intentId))
            {
                _logger.LogWarning("webhook: charge ไม่มี metadata.intentId — ไม่รู้ว่าเป็นของรายการไหน");
                return null;
            }

            return new VerifiedWebhookEvent(eventId!, intentId, ParseCharge(data));
        }
        catch (Exception ex)
        {
            // ห้าม throw ขึ้นไป — ผู้เรียกต้องตอบ 200 ให้ provider เลิก retry
            // แล้วให้ job กระทบยอดจับสถานะแทน (webhook เป็นทางเร็ว ไม่ใช่ทางเดียว)
            _logger.LogError(ex, "webhook: ตรวจสอบ event {Event} ล้มเหลว", eventId);
            return null;
        }
    }

    /// <summary>รอบ 228 (§5.4): event ข้อพิพาท — อ่านเลข charge จาก dispute ที่ fetch มาแล้ว (ไม่ใช่ body) → fetch charge ด้วยคีย์ของเรา → intent จาก metadata ·
    /// เลข charge ต้องเป็นรูปเลข charge ก่อนต่อ path (เหมือนเลข event) · อ่านไม่ได้ = null (ไม่ throw)</summary>
    private async Task<VerifiedWebhookEvent?> VerifyDisputeAsync(HttpClient client, string eventId, JsonElement dispute, CancellationToken ct)
    {
        string? chargeId = null;
        if (dispute.TryGetProperty("charge", out var ch))
            chargeId = ch.ValueKind == JsonValueKind.String ? ch.GetString()
                : ch.ValueKind == JsonValueKind.Object && ch.TryGetProperty("id", out var cid) ? cid.GetString() : null;
        if (!IsWellFormedChargeId(chargeId)) return null;
        using var resp = await client.GetAsync($"/charges/{chargeId}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var charge = doc.RootElement;
        if (!charge.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("intentId", out var intentEl) || !Guid.TryParse(intentEl.GetString(), out var intentId))
            return null;
        return new VerifiedWebhookEvent(eventId, intentId, ParseCharge(charge), ParseDispute(dispute));
    }

    /// <summary>dispute ของเจ้านี้ → ภาษากลาง · สถานะที่ไม่รู้จัก = เปิด (ให้คนดู — ไม่เดาว่าจบแล้ว) · pure</summary>
    internal static ProviderDispute ParseDispute(JsonElement d)
    {
        var raw = d.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        var state = raw switch
        {
            "open" => PaymentDisputeState.Open,
            "pending" => PaymentDisputeState.UnderReview,
            "won" => PaymentDisputeState.Won,
            "lost" => PaymentDisputeState.Lost,
            "closed" => PaymentDisputeState.Closed,
            _ => PaymentDisputeState.Open,
        };
        string? reason = null;
        foreach (var f in new[] { "reason_message", "message", "reason_code" })
            if (reason == null && d.TryGetProperty(f, out var r) && r.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(r.GetString()))
                reason = r.GetString();
        if (reason is { Length: > 500 }) reason = reason[..500];
        return new ProviderDispute(
            d.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "",
            state, raw,
            d.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number ? FromMinorUnit(amt.GetInt64()) : 0m,
            reason);
    }

    /// <summary>รูปเลข charge (<c>chrg_</c> + ตัวอักษร/ตัวเลข/ขีดล่าง) — ด่านก่อนต่อเข้า path · pure</summary>
    internal static bool IsWellFormedChargeId(string? id)
        => id is { Length: > 5 and <= 100 }
           && id.StartsWith("chrg_", StringComparison.Ordinal)
           && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    /// <summary>เบอร์โทรเป็นตัวเลขล้วน (ฝ่ายค้านรอบ 227 N6) · pure</summary>
    internal static string PhoneDigits(string? phone)
        => string.IsNullOrWhiteSpace(phone) ? "" : new string(phone.Where(char.IsAsciiDigit).ToArray());

    /// <summary>รูปเลข event ของผู้ให้บริการนี้ (<c>evnt_</c> + ตัวอักษร/ตัวเลข/ขีดล่าง · โหมดทดสอบ = <c>evnt_test_…</c>) —
    /// ด่านก่อนต่อเข้า path (ไม่มี <c>/</c> <c>.</c> <c>?</c> <c>%</c>) · pure</summary>
    internal static bool IsWellFormedEventId(string? eventId)
        => eventId is { Length: > 5 and <= 100 }
           && eventId.StartsWith("evnt_", StringComparison.Ordinal)
           && eventId.All(ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    /// <summary>รอบ 219 ส่วน B ฝ่ายค้าน B3: คีย์ test ต้องเป็น pkey_test_/skey_test_ · คีย์ live ต้องไม่ใช่คีย์ทดสอบ (Services/Payments/Providers/OmiseKeyFormat)</summary>
    public string? KeyModeProblem(bool live, bool secret, string? value) => OmiseKeyFormat.Problem(live, secret, value);

    public async Task<ProviderHealth> TestConnectionAsync(PaymentProviderConfig config,
        CancellationToken ct = default)
    {
        try
        {
            using var client = Client(config, ApiBase);
            using var resp = await client.GetAsync("/account", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return new ProviderHealth(false, false,
                    "คีย์ยังใช้ไม่ได้: " + FriendlyError(body, resp.StatusCode.ToString()));
            }
            // รอบ 219 ส่วน B ฝ่ายค้าน B3: คีย์ใช้ได้ไม่พอ — โหมดของบัญชีที่คีย์นี้ชี้ต้องตรงกับช่อง (livemode) · ไม่มีช่องนี้ = ไม่ตัดสิน
            var accountJson = await resp.Content.ReadAsStringAsync(ct);
            bool? accountLive = null;
            try
            {
                using var accDoc = JsonDocument.Parse(accountJson);
                if (accDoc.RootElement.TryGetProperty("livemode", out var lm) && lm.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    accountLive = lm.GetBoolean();
            }
            catch (JsonException) { accountLive = null; }
            if (Accounting.Helpers.PaymentLiveModeGuard.Mismatch(accountLive, config.Mode == PaymentProviderMode.Test) is string modeBad)
                return new ProviderHealth(false, false, "คีย์นี้ไม่ตรงโหมด: " + modeBad);
        }
        catch (Exception ex)
        {
            return new ProviderHealth(false, false,
                "เชื่อมต่อผู้ให้บริการไม่ได้: " + ex.Message
                + " — ตรวจว่าเซิร์ฟเวอร์ออกอินเทอร์เน็ตได้และคีย์ถูกต้อง");
        }

        // คีย์ผ่านแล้ว แต่ **ยังไม่ถือว่าทดสอบผ่าน** จนกว่า webhook จะมาถึงจริง
        // (นี่คือเคสที่พบบ่อยที่สุด: คีย์ถูก แต่ลืมตั้ง URL ในแดชบอร์ดของผู้ให้บริการ
        // ⇒ ลูกค้าจ่ายเงินแล้วออเดอร์ไม่อัปเดต)
        var everReceived = config.LastWebhookAt != null;
        return new ProviderHealth(
            KeysValid: true,
            WebhookReceived: everReceived,
            Message: everReceived
                ? "คีย์ใช้งานได้ และเคยได้รับการแจ้งเตือนจากผู้ให้บริการแล้ว — พร้อมรับชำระเงิน"
                : "คีย์ใช้งานได้ แต่ยังไม่เคยได้รับการแจ้งเตือน (webhook) จากผู้ให้บริการ — "
                  + "นำ URL ที่แสดงด้านล่างไปตั้งในแดชบอร์ดของผู้ให้บริการ แล้วทดลองจ่ายเงินหนึ่งครั้ง");
    }

    /// <summary>แปลง error ของ provider เป็นข้อความที่ผู้ใช้ทำอะไรต่อได้
    ///
    /// <para>ข้อความ error ที่โยน code ดิบใส่ผู้ใช้ = ผู้ใช้ต้องไปค้นเอง · และข้อความที่
    /// **เดาสาเหตุแทนผู้ใช้** อันตรายกว่าไม่บอกอะไรเลย (บทเรียน CSP/Google SSO) จึงบอก
    /// เฉพาะสิ่งที่รู้จริง แล้วแนบข้อความต้นทางไว้ให้ผู้ดูแลระบบ</para></summary>
    private static string FriendlyError(string body, string httpStatus)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            // รอบ 227 (A-10): ตารางรหัส → ไทย ตัวเดียวกับ failure_code ของ charge (OmiseFailureText) — เดิม switch 4 รหัสในไฟล์นี้
            return OmiseFailureText.Describe(code, message) ?? $"ผู้ให้บริการตอบกลับผิดพลาด ({httpStatus})";
        }
        catch (JsonException)
        {
            return $"ผู้ให้บริการตอบกลับผิดพลาด ({httpStatus})";
        }
    }
}
