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
    private const string VaultBase = "https://vault.omise.co";
    private const string CdnBase = "https://cdn.omise.co";
    /// <summary>สคริปต์ฝั่งเบราว์เซอร์ที่แปลงเลขบัตรเป็นโทเคน · หน้าเว็บขอค่านี้จาก API
    /// ไม่ hard-code เอง (ไฟล์อื่นห้ามรู้จักโดเมนนี้)</summary>
    string? IPaymentProvider.ClientScriptUrl => CdnBase + "/omise.js";

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
            if (src.TryGetProperty("expires_at", out var ex)) expires = ex.GetString();
        }
        if (el.TryGetProperty("authorize_uri", out var au)) authorize = au.GetString();

        DateTime? expiresAt = DateTime.TryParse(expires, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var exd)
            ? exd : null;

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
            FailureCode: el.TryGetProperty("failure_code", out var fc) ? fc.GetString() : null,
            FailureMessage: el.TryGetProperty("failure_message", out var fm) ? fm.GetString() : null);
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
            if (string.IsNullOrWhiteSpace(req.CardToken))
                throw new InvalidOperationException(
                    "ไม่พบโทเคนบัตร — เลขบัตรต้องถูกแปลงเป็นโทเคนในเบราว์เซอร์ก่อนเสมอ");
            form.Add(new("card", req.CardToken));
            if (!string.IsNullOrWhiteSpace(req.ReturnUrl))
                form.Add(new("return_uri", req.ReturnUrl));   // 3-D Secure
        }
        else
        {
            form.Add(new("source[type]", SourceType(req.Kind)));
            if (!string.IsNullOrWhiteSpace(req.ReturnUrl))
                form.Add(new("return_uri", req.ReturnUrl));
        }

        using var resp = await client.PostAsync("/charges", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(FriendlyError(body, resp.StatusCode.ToString()));

        using var doc = JsonDocument.Parse(body);
        return ParseCharge(doc.RootElement);
    }

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
        string reason, PaymentProviderConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(intent.ProviderRef))
            return new ProviderRefund("", amount, false, "รายการนี้ไม่มีเลขอ้างอิงจากผู้ให้บริการ");

        using var client = Client(config, ApiBase);
        var form = new List<KeyValuePair<string, string>>
        {
            new("amount", ToMinorUnit(amount).ToString(CultureInfo.InvariantCulture)),
            new("metadata[reason]", reason),
        };
        using var resp = await client.PostAsync($"/charges/{intent.ProviderRef}/refunds",
            new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new ProviderRefund("", amount, false, FriendlyError(body, resp.StatusCode.ToString()));

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

            // สนใจเฉพาะ event ที่เกี่ยวกับ charge — refund/dispute มีเส้นทางของตัวเอง
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
            return code switch
            {
                "authentication_failure" => "คีย์ไม่ถูกต้องหรือหมดอายุ — ตรวจคีย์ในหน้าตั้งค่าการรับชำระเงิน",
                "insufficient_fund" => "ยอดเงินในบัญชี/วงเงินบัตรไม่พอ",
                "invalid_card" => "ข้อมูลบัตรไม่ถูกต้อง",
                "payment_rejected" => "ธนาคารผู้ออกบัตรปฏิเสธรายการนี้",
                _ => message ?? $"ผู้ให้บริการตอบกลับผิดพลาด ({httpStatus})",
            };
        }
        catch (JsonException)
        {
            return $"ผู้ให้บริการตอบกลับผิดพลาด ({httpStatus})";
        }
    }
}
