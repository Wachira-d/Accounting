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

    /// <summary>รอบ 201 ทีม GW (BACKLOG B-1 · team-G คำถามค้าง 2): รุ่น 2019-05-29 ช่อง <c>fee</c> = ค่าธรรมเนียม<b>ก่อน VAT</b> และมี <c>fee_vat</c> แยก —
    /// adapter ยังไม่อ่าน <c>fee_vat</c> และยังไม่ได้ยืนยันใน sandbox ว่าการปักรุ่นไม่เปลี่ยนช่องอื่น ⇒ เตือนล่วงหน้าบนหน้าตั้งค่าเท่านั้น
    /// (<b>ห้ามต่อสาย <c>fee_vat</c></b> จนกว่ามีผล sandbox — รายการทดสอบอยู่ PAYMENT_GATEWAY_DESIGN.md §4.4)</summary>
    string? IPaymentProvider.PendingVerificationNotice =>
        "ค่าธรรมเนียมที่ระบบอ่านจากผู้ให้บริการรายนี้เป็นยอดก่อน VAT (รุ่น API " + ApiVersion + ") และยังไม่ได้ยืนยันกับระบบทดสอบของผู้ให้บริการ — "
        + "รอบโอนแรกหลังเปิดใช้หรือหลังอัปเดตระบบอาจยอดไม่ตรงสเตทเมนต์: ดูตัวอย่างรอบโอนเทียบยอดที่เข้าธนาคารจริงก่อนบันทึก "
        + "(ระบบบล็อกเมื่อยอดไม่ตรงอยู่แล้ว ไม่ลงบัญชีผิดเงียบ) · ถ้าใบกำกับของผู้ให้บริการแยก VAT ให้ตั้ง \"VAT ของค่าธรรมเนียม\" เป็น "
        + "\"หัก VAT 7% เพิ่มจากค่าธรรมเนียม\" · ยอดไม่ตรงเพราะค่าธรรมเนียม ให้แก้ค่าธรรมเนียมรายรายการตามสเตทเมนต์ที่หน้ารายการรับชำระออนไลน์";

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
            if (src.TryGetProperty("expires_at", out var ex)) expires = ex.GetString();
        }
        if (el.TryGetProperty("authorize_uri", out var au)) authorize = au.GetString();

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
            FailureCode: el.TryGetProperty("failure_code", out var fc) ? fc.GetString() : null,
            FailureMessage: el.TryGetProperty("failure_message", out var fm) ? fm.GetString() : null,
            // ยอดคืนสะสมของ charge — ใช้ตรวจการคืนเงินที่ผลไม่แน่ชัด (ฝ่ายค้าน E-2)
            RefundedTotal: refundedTotal,
            Refunds: refunds);
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

    /// <summary>รูปเลข event ของผู้ให้บริการนี้ (<c>evnt_</c> + ตัวอักษร/ตัวเลข/ขีดล่าง · โหมดทดสอบ = <c>evnt_test_…</c>) —
    /// ด่านก่อนต่อเข้า path (ไม่มี <c>/</c> <c>.</c> <c>?</c> <c>%</c>) · pure</summary>
    internal static bool IsWellFormedEventId(string? eventId)
        => eventId is { Length: > 5 and <= 100 }
           && eventId.StartsWith("evnt_", StringComparison.Ordinal)
           && eventId.All(ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

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
