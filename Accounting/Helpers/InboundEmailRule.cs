using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 272 แพ็ก P6 (คำตัดสิน 272-1-8 = D-8) — อีเมลขาเข้าของเส้นสลิปภาษี · pure · ตัวส่งอีเมลขาออกที่ระบบใช้ (SMTP · Microsoft Graph · Gmail API —
/// <c>Services/Implementations/Email</c>) <b>ไม่มีทางรับอีเมลแบบ webhook</b> ⇒ ตามคำตัดสิน: ทำหลัง <see cref="IInboundEmailAdapter"/> + webhook ตัวเดียว
/// (<c>POST /api/inbound-email</c>) ที่ผู้ให้บริการรับอีเมล (หรือ relay ของเรา) เรียก · ลงลายเซ็นด้วย <see cref="WebhookSignature"/> (canonical ตัวเดียว · มีเวลา กันยิงซ้ำ) ·
/// รหัสลับ <c>INBOUND_EMAIL_SECRET</c>: เปิดใช้ (<c>InboundEmail:Enabled=true</c>) แต่ไม่มีรหัสลับใน production ⇒ boot ไม่ขึ้น (<see cref="RequireSecret"/> · กฎ #4 C fail-fast) ·
/// ไม่เปิดใช้ ⇒ webhook ตอบ 503 พร้อมเหตุ (ไม่รับเงียบ)
/// </summary>
public static class InboundEmailRule
{
    public const string SecretEnv = "INBOUND_EMAIL_SECRET";
    public const string EnabledConfig = "InboundEmail:Enabled";
    public const int MaxAttachments = 5;

    /// <summary>รหัสลับที่ใช้ตรวจ (ตั้งครั้งเดียวตอนบูตผ่าน <see cref="Configure"/>) · null = ไม่เปิดใช้</summary>
    public static string? Secret { get; private set; }

    public static void Configure(string? secret) => Secret = secret;

    /// <param name="SpfPass">ผู้ให้บริการขาเข้ายืนยัน SPF ผ่าน</param>
    /// <param name="DkimPass">ผู้ให้บริการขาเข้ายืนยัน DKIM ผ่าน</param>
    /// <param name="DmarcPass">ผู้ให้บริการขาเข้ายืนยัน DMARC ผ่าน (alignment กับโดเมนของ From แล้ว)</param>
    /// <param name="SpfDomain">โดเมนที่ SPF ผ่าน (envelope MAIL FROM) · null = ไม่รู้</param>
    /// <param name="DkimDomain">โดเมน <c>d=</c> ของลายเซ็น DKIM ที่ผ่าน · null = ไม่รู้</param>
    public sealed record Message(string From, string MessageId, bool SpfPass, bool DkimPass, IReadOnlyList<(string FileName, byte[] Bytes)> Attachments,
        bool DmarcPass = false, string? SpfDomain = null, string? DkimDomain = null);

    private static string? DomainOf(string? address)
    {
        var a = (address ?? "").Trim().TrimEnd('>').ToLowerInvariant();
        var at = a.LastIndexOf('@');
        var d = at >= 0 ? a[(at + 1)..] : a;
        d = d.Trim().TrimEnd('.');
        return d.Length == 0 || !d.Contains('.') ? null : d;
    }

    /// <summary>relaxed alignment แบบ DMARC: โดเมนที่ผ่านต้องเท่ากับโดเมนของ From หรือเป็นโดเมนแม่/ลูกกันใน organizational domain (ตัดสินแบบ suffix ที่ขอบจุด)</summary>
    private static bool Aligned(string? authDomain, string? fromDomain)
    {
        var a = DomainOf(authDomain); var f = DomainOf(fromDomain);
        if (a == null || f == null) return false;
        return a == f || f.EndsWith("." + a, StringComparison.Ordinal) || a.EndsWith("." + f, StringComparison.Ordinal);
    }

    /// <summary>
    /// ฝ่ายค้าน 272 H3: ผู้ส่ง<b>ยืนยันโดเมนของ From ได้</b>ไหม — DMARC ผ่าน · หรือ DKIM ผ่านด้วย <c>d=</c> ที่ align กับ From · หรือ SPF ผ่านด้วยโดเมน envelope ที่ align กับ From ·
    /// "SPF/DKIM ผ่าน" เฉย ๆ ไม่พอ (โดเมนของผู้โจมตีผ่านได้ทั้งคู่) · ไม่ align ⇒ รับเข้าคิวพนักงานได้ แต่<b>ไม่บันทึกเอง</b>
    /// </summary>
    public static bool SenderVerified(Message m)
    {
        var from = DomainOf(m.From);
        if (from == null) return false;
        return m.DmarcPass || (m.DkimPass && Aligned(m.DkimDomain, from)) || (m.SpfPass && Aligned(m.SpfDomain, from));
    }

    public const string UnverifiedReason = "อีเมลผู้ส่งยืนยันโดเมนไม่ได้ (DKIM/SPF ไม่ตรงโดเมนของผู้ส่ง) — ระบบไม่บันทึกเอง ให้พนักงานตรวจ";

    /// <summary>ตรวจรหัสลับตอนบูต: เปิดใช้ + production + ไม่มีค่า/เป็นค่าตัวอย่าง ⇒ throw · คืนค่าที่ใช้ได้ หรือ null (ไม่เปิดใช้)</summary>
    public static string? RequireSecret(bool enabled, bool isProduction, string? secret)
    {
        if (!enabled) return null;
        var s = (secret ?? "").Trim();
        var placeholder = s.Length < 24 || s.Contains("change", StringComparison.OrdinalIgnoreCase) || s.Contains("example", StringComparison.OrdinalIgnoreCase);
        if (placeholder && isProduction)
            throw new InvalidOperationException(SecretEnv + " ต้องตั้งเป็นค่าสุ่มยาว ≥ 24 ตัวใน production เมื่อเปิด " + EnabledConfig + " (รับสลิปภาษีทางอีเมล)");
        return placeholder ? null : s;
    }

    /// <summary>เหตุที่ไม่รับข้อความนี้ · null = รับ (ผู้ส่ง SPF หรือ DKIM ต้องผ่าน — ปลอมผู้ส่ง = ไม่อ่านไฟล์)</summary>
    public static string? Problem(Message m)
    {
        if (string.IsNullOrWhiteSpace(m.From) || !m.From.Contains('@')) return "ไม่มีผู้ส่ง";
        if (!m.SpfPass && !m.DkimPass) return "ผู้ส่งไม่ผ่าน SPF/DKIM — ไม่อ่านไฟล์แนบ";
        if (string.IsNullOrWhiteSpace(m.MessageId)) return "ไม่มี Message-ID";
        return null;
    }
}

/// <summary>รอบ 272 P6: ตัวแปลงรูปแบบของผู้ให้บริการอีเมลขาเข้า ⇒ <see cref="InboundEmailRule.Message"/> (เปลี่ยนเจ้าได้โดยไม่แตะเส้นรับสลิป)</summary>
public interface IInboundEmailAdapter
{
    string Name { get; }
    /// <summary>body ดิบ ⇒ ข้อความ · รูปผิด ⇒ null</summary>
    InboundEmailRule.Message? Parse(string body);
}

/// <summary>รอบ 272 P6: รูป JSON กลาง <c>{"from","messageId","spf":"pass","spfDomain","dkim":"pass","dkimDomain","dmarc":"pass","attachments":[{"fileName","contentBase64"}]}</c> —
/// relay ใดก็แปลงมาเป็นรูปนี้ได้ (ผู้ให้บริการที่เลือกภายหลังเขียน adapter ของตัวเองเพิ่ม)</summary>
public sealed class JsonInboundEmailAdapter : IInboundEmailAdapter
{
    public string Name => "json";

    public InboundEmailRule.Message? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var files = new List<(string, byte[])>();
            if (r.TryGetProperty("attachments", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray().Take(InboundEmailRule.MaxAttachments))
                {
                    var name = a.TryGetProperty("fileName", out var n) ? n.GetString() ?? "" : "";
                    var b64 = a.TryGetProperty("contentBase64", out var c) ? c.GetString() ?? "" : "";
                    try { files.Add((name, Convert.FromBase64String(b64))); } catch (FormatException) { }
                }
            string? N(string k) => S(k) is { Length: > 0 } v ? v : null;
            return new InboundEmailRule.Message(S("from"), S("messageId"), S("spf").Equals("pass", StringComparison.OrdinalIgnoreCase),
                S("dkim").Equals("pass", StringComparison.OrdinalIgnoreCase), files, S("dmarc").Equals("pass", StringComparison.OrdinalIgnoreCase),
                N("spfDomain"), N("dkimDomain"));
        }
        catch (JsonException) { return null; }
    }
}
