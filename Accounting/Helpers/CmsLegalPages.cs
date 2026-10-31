namespace Accounting.Helpers;

/// <summary>
/// รอบ 214 (คำตัดสินข้อ 173 ข้อ B) — นโยบายความเป็นส่วนตัว / ข้อกำหนดการใช้บริการของ<b>ร้าน</b> (เว็บ CMS ของลูกค้า) — ตัวตัดสินเดียว:
/// ลำดับ <b>ลิงก์ภายนอกที่ร้านตั้ง &gt; ข้อความที่ร้านเขียนเอง &gt; แม่แบบของระบบ</b> (<see cref="LegalDocumentDefaults.ShopTemplate"/>)
/// <para>ทุกจุดที่วาดลิงก์ (footer · ช่องยินยอมบนฟอร์มสั่งซื้อ/จอง/ติดต่อ/สมัครสมาชิก · แบนเนอร์คุกกี้) และหน้าแสดงเนื้อความ ใช้ผลจากที่นี่
/// ห้าม inline <c>url ?? ...</c> เอง (กฎเหล็ก #4 A "Resolver กลาง") · ไม่มีทาง "ไม่มีลิงก์" อีกต่อไป — ร้านที่ไม่ได้ตั้งอะไรได้หน้าแม่แบบ
/// ที่มีชื่อ/ที่อยู่/เลขผู้เสียภาษี/ช่องทางติดต่อของร้านจริง (PDPA ม.23 — แจ้งผู้ควบคุมข้อมูลก่อน/ขณะเก็บ)</para>
/// </summary>
public static class CmsLegalPages
{
    public const string ModeExternal = "external";
    public const string ModeCustom = "custom";
    public const string ModeDefault = "default";

    /// <summary>slug ของหน้าภายในบน storefront (<c>/site/{key}/legal/privacy</c> · <c>{โดเมนร้าน}/?slug=legal/privacy</c>) —
    /// อยู่ใต้ <c>legal/</c> ⇒ ไม่ชนหน้า CMS ชื่อ "privacy" ที่ร้านอาจสร้างเองไว้แล้ว (ร้านชี้ไปหน้านั้นได้ด้วยลิงก์ <c>/privacy</c>)</summary>
    public static string Slug(string kind) => kind switch
    {
        LegalDocumentDefaults.KindPrivacy => "legal/privacy",
        LegalDocumentDefaults.KindTerms => "legal/terms",
        LegalDocumentDefaults.KindRefund => "legal/refund",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "ชนิดเอกสารกฎหมายไม่รู้จัก"),
    };

    /// <summary>ผลการตัดสินต่อเอกสาร — <c>ExternalUrl</c> มีค่าเฉพาะโหมด external · โหมดอื่นใช้ <c>Slug</c> (หน้าเว็บประกอบเป็น URL ตามแบบเส้นทางของเว็บ)</summary>
    public sealed record Link(string Kind, string Mode, string? ExternalUrl, string Slug);

    public static Link Resolve(string kind, string? externalUrl, string? customMarkdown)
    {
        // ลิงก์ที่ร้านตั้ง: URL เต็ม http(s) หรือ path ในเว็บร้านเอง ("/privacy" = หน้า CMS ที่ร้านทำไว้) — ค่าไม่ปลอดภัย = เหมือนไม่ได้ตั้ง
        if (CmsGalleryBlock.IsSafeUrl(externalUrl?.Trim()))
            return new Link(kind, ModeExternal, externalUrl!.Trim(), Slug(kind));
        if (!string.IsNullOrWhiteSpace(customMarkdown))
            return new Link(kind, ModeCustom, null, Slug(kind));
        return new Link(kind, ModeDefault, null, Slug(kind));
    }

    /// <summary>เนื้อความที่จะแสดงบนหน้าภายใน — ข้อความของร้าน หรือแม่แบบ (โหมด external ก็คืนแม่แบบ — หน้าภายในยังเปิดได้
    /// เผื่อลิงก์ภายนอกล่ม แต่ลิงก์ทุกจุดชี้ไปภายนอก)</summary>
    public static string EffectiveMarkdown(string kind, string? customMarkdown)
        => string.IsNullOrWhiteSpace(customMarkdown) ? LegalDocumentDefaults.ShopTemplate(kind) : customMarkdown;

    public const string NotSpecified = "ยังไม่ได้ระบุ";
    public const string TaxIdWithheld = "ไม่แสดงบนเว็บ (ผู้ประกอบการบุคคลธรรมดา — เลขประจำตัวผู้เสียภาษีคือเลขบัตรประชาชน)";
    public const string AskTheShop = "สอบถามได้ที่ร้านตามเบอร์โทรด้านล่าง";

    /// <summary>ฝ่ายค้านรอบ 214 S3/S4 — ค่าโทเคนของหน้านโยบาย/ข้อกำหนดของร้าน (ตัวเดียว · ทุกทางแสดงใช้):
    /// <list type="bullet">
    /// <item><b>เลขผู้เสียภาษี</b> แสดงเฉพาะนิติบุคคล (<c>Company.JuristicId</c> มีค่า) — บุคคลธรรมดาใช้เลขบัตรประชาชน (PDPA · กฎ J: ปิดบังเลข 13 หลัก)
    /// ⇒ แทนด้วยข้อความ ไม่เผยแพร่อัตโนมัติ (ทั้งในแม่แบบและข้อความที่ร้านเขียนเองที่อ้าง <c>{{company.taxId}}</c>)</item>
    /// <item>ค่าที่ยังไม่ได้ตั้ง ⇒ "ยังไม่ได้ระบุ" (ไม่ใช่ช่องว่าง/ประโยคขาด) · mailto ที่ไม่มีอีเมลกลายเป็นข้อความ (<c>LegalMarkdown</c>)</item>
    /// <item><paramref name="bookingPageScope"/> = หน้าจองโต๊ะที่ไม่ผูกเว็บ (ใครรู้ companyId ก็เปิดได้) ⇒ เปิดเผยเท่าที่หน้าจองแสดงอยู่แล้ว
    /// (ชื่อ · เบอร์โทร) — ที่อยู่/อีเมล/เลขผู้เสียภาษี ไม่แสดง</item>
    /// </list></summary>
    public static Dictionary<string, string?> BuildShopTokens(IReadOnlyDictionary<string, string?> contentTokens, bool isJuristic, bool bookingPageScope = false)
    {
        var t = new Dictionary<string, string?>(contentTokens, StringComparer.OrdinalIgnoreCase);
        string? Get(string k) => t.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v!.Trim() : null;
        var name = Get("company.name");
        t["company.name"] = name ?? NotSpecified;
        t["site.name"] = Get("site.name") ?? name ?? NotSpecified;
        t["company.phone"] = Get("company.phone") ?? NotSpecified;
        if (bookingPageScope)
        {
            t["company.address"] = AskTheShop;
            t["company.email"] = AskTheShop;
            t["company.taxId"] = AskTheShop;
        }
        else
        {
            t["company.address"] = Get("company.address") ?? NotSpecified;
            t["company.email"] = Get("company.email") ?? NotSpecified;
            t["company.taxId"] = isJuristic ? (Get("company.taxId") ?? NotSpecified) : TaxIdWithheld;
        }
        return t;
    }

    /// <summary>ข้อมูลตัวตนร้านที่ยังขาด (หน้า cms-edit เตือนเจ้าของก่อนเผยแพร่) — คืนชื่อช่องภาษาไทย</summary>
    public static List<string> IdentityGaps(string? name, string? address, string? phone, string? email)
    {
        var gaps = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) gaps.Add("ชื่อกิจการ");
        if (string.IsNullOrWhiteSpace(address)) gaps.Add("ที่อยู่");
        if (string.IsNullOrWhiteSpace(phone)) gaps.Add("เบอร์โทร");
        if (string.IsNullOrWhiteSpace(email)) gaps.Add("อีเมลติดต่อ");
        return gaps;
    }

    /// <summary>ลิงก์ที่เก็บไว้ก่อนรอบ 214 แต่ใช้ไม่ได้ (เช่น javascript: · ไม่ใช่ URL) — ตัวตัดสินถือว่าไม่ได้ตั้งแล้วใช้หน้าระบบ ⇒ ต้องบอกเจ้าของ
    /// (ไม่ลบเงียบ) · null = ไม่มีปัญหา</summary>
    public static string? StoredLinkWarning(string kindTitle, string? storedUrl)
        => !string.IsNullOrWhiteSpace(storedUrl) && !CmsGalleryBlock.IsSafeUrl(storedUrl.Trim())
            ? $"ลิงก์{kindTitle}เดิมไม่ถูกต้อง: \"{storedUrl.Trim()}\" — ระบบใช้หน้าของระบบแทน (บันทึกครั้งถัดไปจะล้างลิงก์นี้)"
            : null;

    /// <summary>ตรวจลิงก์ที่ร้านกรอก — "" = ล้าง (ใช้หน้าของระบบ) · รับ URL เต็ม http(s) หรือ path ในเว็บร้าน (<c>/privacy</c>) ·
    /// คืนข้อความผิด (ไทย) หรือ null = ใช้ได้ (ก่อนรอบ 214 เก็บค่าดิบอะไรก็ได้ — storefront กรองตอนแสดงเท่านั้น)</summary>
    public static string? ValidateExternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!CmsGalleryBlock.IsSafeUrl(url.Trim()))
            return "ลิงก์นโยบาย/ข้อกำหนดต้องเป็น URL เต็มที่ขึ้นต้นด้วย https:// หรือ path ในเว็บนี้ เช่น /privacy (หรือเว้นว่างเพื่อใช้หน้าของระบบ)";
        return null;
    }

    // ════════════════ รอบ 219 ส่วน B (คำตัดสินข้อ 179): นโยบายการยกเลิกและคืนเงินของร้าน ════════════════

    /// <summary>ค่าเริ่มต้นของจำนวนวันแจ้งคืนสินค้า (ร้านไม่ได้ตั้ง)</summary>
    public const int DefaultReturnWindowDays = 7;
    /// <summary>ค่าเริ่มต้นของจำนวนวันดำเนินการคืนเงิน (ร้านไม่ได้ตั้ง)</summary>
    public const int DefaultRefundProcessingDays = 14;

    /// <summary>โทเคนเฉพาะนโยบายคืนเงินของร้าน (ตัวเลขจากค่าตั้ง — ไม่ให้ร้านพิมพ์ซ้ำ) + คำอธิบาย (หน้า cms-edit แสดงจากรายการนี้)</summary>
    public static readonly IReadOnlyList<(string Token, string Label)> RefundTokens = new[]
    {
        ("shop.depositForfeit",       "การหักมัดจำเมื่อยกเลิกสินค้าสั่งทำ (จากค่าตั้ง \"ริบมัดจำเมื่อยกเลิก %\")"),
        ("shop.returnWindowDays",     "จำนวนวันที่ลูกค้าแจ้งคืนสินค้าได้ (ค่าตั้งเว็บ · ไม่ตั้ง = 7)"),
        ("shop.refundProcessingDays", "จำนวนวันที่ร้านคืนเงินหลังอนุมัติ (ค่าตั้งเว็บ · ไม่ตั้ง = 14)"),
    };

    /// <summary>ข้อความการหักมัดจำตามค่าตั้ง <c>Site.DepositForfeitOnCancelPercent</c> — null/0 = คืนมัดจำเต็ม</summary>
    private static string DepositForfeitText(decimal? forfeitPercent)
        => forfeitPercent is decimal p && p > 0m
            ? $"ร้านหักมัดจำ {Math.Min(p, 100m):0.##}% ของมัดจำที่ชำระแล้ว ส่วนที่เหลือคืนให้ท่าน"
            : "ร้านคืนมัดจำเต็มจำนวน (ไม่มีการริบมัดจำ)";

    /// <summary>จำนวนวันที่ใช้จริง — ค่าตั้งนอกช่วง 1–365 ⇒ ค่าเริ่มต้น</summary>
    public static int Days(int? configured, int fallback) => configured is int d && d >= 1 && d <= 365 ? d : fallback;

    /// <summary>ค่าโทเคนนโยบายคืนเงินจากค่าตั้งของเว็บ — ตัวเดียวของหน้าแสดง ตัวอย่าง และเลขฉบับ (<see cref="ShopPolicyVersion"/>)</summary>
    public static Dictionary<string, string?> BuildRefundTokens(decimal? forfeitPercent, int? returnWindowDays, int? refundProcessingDays)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["shop.depositForfeit"] = DepositForfeitText(forfeitPercent),
            ["shop.returnWindowDays"] = Days(returnWindowDays, DefaultReturnWindowDays).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["shop.refundProcessingDays"] = Days(refundProcessingDays, DefaultRefundProcessingDays).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>หน้าจองโต๊ะ (ไม่ผูกเว็บ) — ไม่มีค่าตั้งของเว็บ ⇒ บอกให้ดูหน้าจองแทนการเดาตัวเลข</summary>
    public static Dictionary<string, string?> BookingPageRefundTokens()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["shop.depositForfeit"] = "เป็นไปตามเงื่อนไขมัดจำที่แสดงบนหน้าจอง",
            ["shop.returnWindowDays"] = DefaultReturnWindowDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["shop.refundProcessingDays"] = DefaultRefundProcessingDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>โทเคนที่รู้จักของเอกสารชนิดนั้น (ตัวเตือน "โทเคนไม่รู้จัก" ของหน้าตัวอย่าง)</summary>
    public static IEnumerable<string> KnownTokens(string kind)
    {
        var baseTokens = CmsContentTokens.Supported.Select(t => t.Token);
        return kind == LegalDocumentDefaults.KindRefund ? baseTokens.Concat(RefundTokens.Select(t => t.Token)) : baseTokens;
    }

    /// <summary>คำเตือนเมื่อข้อความที่ร้านเขียนเองขัดกับค่าตั้งเชิงตัวเลข (ร้านเขียนตัวเลขเองแล้วลืมแก้เมื่อเปลี่ยนค่าตั้ง = สองความจริง) —
    /// ตรวจแบบอนุรักษ์ (คำที่ชัด) · ไม่บล็อกการบันทึก แสดงบน cms-edit · ว่าง = ไม่พบ</summary>
    public static List<string> RefundContradictions(string? customMarkdown, decimal? forfeitPercent)
    {
        var w = new List<string>();
        if (string.IsNullOrWhiteSpace(customMarkdown)) return w;
        var md = customMarkdown;
        var forfeits = forfeitPercent is decimal p && p > 0m;
        if (forfeits && (md.Contains("คืนมัดจำเต็ม", StringComparison.Ordinal) || md.Contains("ไม่ริบมัดจำ", StringComparison.Ordinal)))
            w.Add($"ข้อความนโยบายคืนเงินบอกว่าคืนมัดจำเต็ม แต่ค่าตั้งของร้านริบมัดจำ {forfeitPercent:0.##}% เมื่อยกเลิก — แก้ข้อความหรือค่าตั้งให้ตรงกัน (หรือใช้โทเคน {{{{shop.depositForfeit}}}})");
        if (!forfeits && (md.Contains("ริบมัดจำ", StringComparison.Ordinal) || md.Contains("หักมัดจำ", StringComparison.Ordinal)) && !md.Contains("ไม่ริบมัดจำ", StringComparison.Ordinal))
            w.Add("ข้อความนโยบายคืนเงินพูดถึงการริบ/หักมัดจำ แต่ค่าตั้งของร้านไม่ได้ตั้งการริบมัดจำ — ระบบจะไม่หักมัดจำให้อัตโนมัติ (ตั้งที่ \"การชำระเงิน › ริบมัดจำเมื่อยกเลิก\" หรือแก้ข้อความ)");
        if (md.Contains("ไม่คืนเงินทุกกรณี", StringComparison.Ordinal))
            w.Add("\"ไม่คืนเงินทุกกรณี\" อาจขัดกฎหมายคุ้มครองผู้บริโภค (สินค้าชำรุด/ไม่ตรงคำสั่งซื้อ) — ควรระบุข้อยกเว้น");
        return w;
    }

    /// <summary>เลขฉบับของ "ข้อกำหนด + นโยบายคืนเงิน" ของร้าน ณ ตอนนี้ — ร้านไม่มีเลขฉบับเอง ⇒ hash ของสิ่งที่ลูกค้าเห็นจริง
    /// (ลิงก์ภายนอก หรือข้อความที่ใช้จริง + ค่าตั้งตัวเลขที่แทนในแม่แบบ) · เปลี่ยนข้อความ/ค่าตั้ง ⇒ เลขเปลี่ยน ⇒ หน้าที่เปิดค้างได้คำขอให้โหลดใหม่
    /// <para>ตัวเดียวของหน้าเช็กเอาต์ (แสดง) และด่านเซิร์ฟเวอร์ (<c>PurchasePolicyAcceptance</c>) — ห้ามคำนวณที่อื่น</para></summary>
    public static string ShopPolicyVersion(string? termsUrl, string? termsMarkdown, string? refundUrl, string? refundMarkdown,
        decimal? forfeitPercent, int? returnWindowDays, int? refundProcessingDays)
    {
        var terms = Resolve(LegalDocumentDefaults.KindTerms, termsUrl, termsMarkdown);
        var refund = Resolve(LegalDocumentDefaults.KindRefund, refundUrl, refundMarkdown);
        var tokens = BuildRefundTokens(forfeitPercent, returnWindowDays, refundProcessingDays);
        var canonical = string.Join("\n",
            "terms", terms.Mode, terms.ExternalUrl ?? "", terms.Mode == ModeExternal ? "" : EffectiveMarkdown(LegalDocumentDefaults.KindTerms, termsMarkdown),
            "refund", refund.Mode, refund.ExternalUrl ?? "", refund.Mode == ModeExternal ? "" : EffectiveMarkdown(LegalDocumentDefaults.KindRefund, refundMarkdown),
            string.Join("|", tokens.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)));
        return "shop-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
            .Substring(0, 12).ToLowerInvariant();
    }
}
