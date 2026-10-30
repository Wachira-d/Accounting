namespace Accounting.Helpers;

/// <summary>ที่อยู่ของสมาชิก (ค่าที่ผู้เรียกส่งเข้า/ค่าที่ทำความสะอาดแล้ว — โครงเดียวกับ <c>SiteCustomerAddress</c>)</summary>
public sealed record MemberAddress(
    string? RecipientName, string? Phone, string? AddressLine,
    string? SubDistrict, string? District, string? Province, string? PostalCode);

/// <summary>โปรไฟล์สมาชิกหน้าร้าน — ชื่อ · เบอร์ · ข้อมูลใบกำกับ (ชื่อ/เลขผู้เสียภาษี/สาขา) · ที่อยู่จัดส่งหลัก · ที่อยู่ใบกำกับ ·
/// อีเมลคือชื่อเข้าระบบ (อ่านอย่างเดียว — ไม่อยู่ในโครงนี้)</summary>
public sealed record MemberProfile(
    string? FullName, string? Phone, string? CompanyName, string? TaxId, string? BranchCode,
    MemberAddress? Shipping, MemberAddress? Billing);

/// <summary>ค่าที่ลูกค้าพิมพ์บนฟอร์มจอง/สั่งซื้อ/จองที่พัก แล้วติ๊ก "บันทึกเป็นข้อมูลของฉัน" — ช่องที่ฟอร์มนั้นไม่มี = null (ไม่แตะโปรไฟล์)</summary>
public sealed record CheckoutProfileInput(
    string? FullName, string? Phone,
    string? ShippingAddressText = null, string? ShippingProvince = null, string? ShippingPostalCode = null,
    string? BillingName = null, string? BillingTaxId = null, string? BillingBranchCode = null, string? BillingAddressText = null);

/// <summary>ผลรวมค่าที่ฟอร์มพิมพ์เข้ากับโปรไฟล์เดิม — <see cref="Saved"/>/<see cref="Skipped"/> เป็นชื่อช่องภาษาไทย (บอกผู้ใช้ตรง ๆ ว่าอะไรไม่ถูกบันทึก)</summary>
public sealed record CheckoutProfileMerge(MemberProfile Next, IReadOnlyList<string> Saved, IReadOnlyList<string> Skipped);

/// <summary>
/// รอบ 216 (คำตัดสินข้อ 174) — <b>ตัวตัดสินเดียวของ "ข้อมูลส่วนตัวของสมาชิกหน้าร้าน"</b> (pure · ไม่มี I/O):
/// <list type="bullet">
/// <item><see cref="Validate"/> — หน้า "ข้อมูลส่วนตัว" (PUT portal/me/profile): ชื่อบังคับ · เบอร์ผ่านตัว normalize กลาง
/// (<see cref="CmsContactResolver.NormalizePhone"/>) · เลขผู้เสียภาษี 13 หลัก + mod-11 (<see cref="ThaiTaxId.IsValid"/>) · สาขา 5 หลัก
/// (ประกาศอธิบดีฯ 199) · รหัสไปรษณีย์ 5 หลัก · ผิดหลายข้อรวมข้อความเดียว</item>
/// <item><see cref="FillContact"/> — ตาข่ายฝั่งเซิร์ฟเวอร์: สมาชิกที่ล็อกอินจอง/สั่งแล้วเว้นชื่อ/เบอร์/อีเมล ⇒ เติมจากโปรไฟล์
/// (ค่าที่พิมพ์มาชนะเสมอ · ไม่เดาค่าที่โปรไฟล์ไม่มี)</item>
/// <item><see cref="MergeCheckout"/> — "บันทึกเป็นข้อมูลของฉัน" หลังจอง/สั่งสำเร็จ: เอาเฉพาะค่าที่ไม่ว่างและผ่านตัวตรวจเดียวกับ
/// <see cref="Validate"/> · ค่าที่ผิดรูปแบบ <b>ไม่ถูกบันทึกและถูกบอกชื่อช่อง</b> (ไม่ใช่ no-op เงียบ)</item>
/// </list>
/// <para>โปรไฟล์เป็นของสมาชิก — ไม่เขียนทับผู้ติดต่อ ERP ที่ผูกไว้ (ข้อมูลบัญชี/ใบกำกับของร้าน) · ดู DECISIONS ข้อ 174</para>
/// </summary>
public static class StorefrontMemberProfile
{
    public const string RuleCode = "CMS-MEMBER-PROFILE";
    public const int MaxName = 200;
    public const int MaxAddressLine = 500;
    public const int MaxPart = 100;
    /// <summary>เบอร์ (ตัวเลขล้วนหลัง normalize) ยาวสุด — รองรับเบอร์ต่างประเทศ · คอลัมน์ Phone ยาว 20</summary>
    public const int MaxPhoneDigits = 15;
    public const string HeadOffice = "00000";

    private static string? T(string? s, int max)
    {
        var v = s?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return v.Length > max ? v[..max] : v;
    }

    /// <summary>เบอร์ที่ใช้เก็บ: ว่าง ⇒ (null, ok) · normalize กลางได้และยาวไม่เกิน <see cref="MaxPhoneDigits"/> ⇒ (ตัวเลข, ok) · อื่น ⇒ (null, ผิด)</summary>
    public static (string? Value, bool Ok) Phone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, true);
        var n = CmsContactResolver.NormalizePhone(raw);
        return n != null && n.Length <= MaxPhoneDigits ? (n, true) : (null, false);
    }

    private static bool IsDigits(string? s, int len) => s is { } v && v.Length == len && v.All(ch => ch is >= '0' and <= '9');

    private static MemberAddress? CleanAddress(MemberAddress? a, string label, List<string> errs)
    {
        if (a == null) return null;
        var (phone, phoneOk) = Phone(a.Phone);
        if (!phoneOk) errs.Add($"{label}: เบอร์โทรไม่ถูกต้อง");
        var postal = T(a.PostalCode, 10);
        if (postal != null && !IsDigits(postal, 5)) errs.Add($"{label}: รหัสไปรษณีย์ต้องเป็นตัวเลข 5 หลัก");
        var c = new MemberAddress(T(a.RecipientName, MaxName), phone, T(a.AddressLine, MaxAddressLine),
            T(a.SubDistrict, MaxPart), T(a.District, MaxPart), T(a.Province, MaxPart), postal);
        // ทุกช่องว่าง = ไม่มีที่อยู่ (ลบที่อยู่หลักเดิม) · มีบางช่องแต่ไม่มีบรรทัดที่อยู่ = ใช้ส่งของไม่ได้ ⇒ บอก
        if (c.RecipientName == null && c.Phone == null && c.AddressLine == null && c.SubDistrict == null
            && c.District == null && c.Province == null && c.PostalCode == null) return null;
        if (c.AddressLine == null) errs.Add($"{label}: กรุณากรอกบ้านเลขที่/ถนน");
        return c;
    }

    /// <summary>ตรวจ + ทำความสะอาดโปรไฟล์ทั้งชุด (หน้า "ข้อมูลส่วนตัว" ส่งครบทุกช่อง · ว่าง = ล้างค่า) · Errors ว่าง = บันทึกได้</summary>
    public static (IReadOnlyList<string> Errors, MemberProfile Clean) Validate(MemberProfile input)
    {
        var errs = new List<string>();
        var name = T(input.FullName, MaxName);
        if (name == null) errs.Add("กรุณากรอกชื่อ-นามสกุล");
        var (phone, phoneOk) = Phone(input.Phone);
        if (!phoneOk) errs.Add("เบอร์โทรไม่ถูกต้อง (ตัวเลขอย่างน้อย 9 หลัก)");
        var company = T(input.CompanyName, MaxName);
        var taxRaw = T(input.TaxId, 30);
        string? tax = null, branch = null;
        if (taxRaw != null)
        {
            tax = ThaiTaxId.Normalize(taxRaw);
            if (tax.Length != 13 || !ThaiTaxId.IsValid(tax))
            {
                errs.Add("เลขประจำตัวผู้เสียภาษีไม่ถูกต้อง (ต้องเป็นเลข 13 หลักที่ตรวจสอบหลักสุดท้ายผ่าน)");
                tax = null;
            }
            branch = T(input.BranchCode, 10) ?? HeadOffice;
            if (!IsDigits(branch, 5)) { errs.Add("รหัสสาขาต้องเป็นตัวเลข 5 หลัก (สำนักงานใหญ่ = 00000)"); branch = null; }
        }
        else if (T(input.BranchCode, 10) != null) errs.Add("กรอกรหัสสาขาแล้ว — กรุณากรอกเลขประจำตัวผู้เสียภาษีด้วย");
        var ship = CleanAddress(input.Shipping, "ที่อยู่จัดส่ง", errs);
        var bill = CleanAddress(input.Billing, "ที่อยู่ใบกำกับภาษี", errs);
        return (errs, new MemberProfile(name, phone, company, tax, branch, ship, bill));
    }

    /// <summary>ที่อยู่เป็นข้อความบรรทัดเดียว (เซิร์ฟเวอร์ประกอบ — หน้าเว็บแสดง/เติมช่อง textarea อย่างเดียว) ·
    /// <paramref name="withProvince"/>=false สำหรับฟอร์มที่มีช่องจังหวัด/รหัสไปรษณีย์แยก (เช็กเอาต์)</summary>
    public static string? ComposeAddress(MemberAddress? a, bool withProvince)
    {
        if (a == null) return null;
        var parts = new List<string?> { a.AddressLine, a.SubDistrict, a.District };
        if (withProvince) { parts.Add(a.Province); parts.Add(a.PostalCode); }
        var s = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return s.Length == 0 ? null : s;
    }

    /// <summary>ตาข่ายเซิร์ฟเวอร์: ช่องที่เว้นว่างเติมจากโปรไฟล์ (ชื่อ/เบอร์/อีเมลเข้าระบบ) · ค่าที่พิมพ์มาชนะเสมอ · ไม่มีโปรไฟล์ = คืนค่าเดิม</summary>
    public static (string? Name, string? Phone, string? Email) FillContact(string? name, string? phone, string? email,
        string? profileName, string? profilePhone, string? profileEmail)
    {
        static string? Pick(string? typed, string? fallback)
            => !string.IsNullOrWhiteSpace(typed) ? typed : (string.IsNullOrWhiteSpace(fallback) ? typed : fallback.Trim());
        return (Pick(name, profileName), Pick(phone, profilePhone), Pick(email, profileEmail));
    }

    private static string Squash(string? s) => string.Join(" ", (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// "บันทึกเป็นข้อมูลของฉัน" — รวมค่าที่พิมพ์บนฟอร์มเข้ากับโปรไฟล์เดิม: ช่องว่าง/ไม่มีบนฟอร์ม = คงค่าเดิม · ค่าที่ผ่านตัวตรวจ = ทับ ·
    /// ค่าที่ผิดรูปแบบ = คงค่าเดิม + ชื่อช่องใน <see cref="CheckoutProfileMerge.Skipped"/> ·
    /// ที่อยู่จัดส่งจากเช็กเอาต์เป็นข้อความอิสระ ⇒ เก็บเป็นบรรทัดที่อยู่ (ตำบล/อำเภอเดิมถูกล้าง เพราะรวมอยู่ในข้อความแล้ว) เว้นแต่ข้อความตรงกับที่อยู่เดิม ·
    /// ข้อมูลใบกำกับเปลี่ยนเฉพาะเมื่อเลขผู้เสียภาษีผ่าน mod-11 (ชื่อ/สาขา/ที่อยู่ใบกำกับไปด้วยกันเป็นชุด)
    /// </summary>
    public static CheckoutProfileMerge MergeCheckout(MemberProfile current, CheckoutProfileInput typed)
    {
        var saved = new List<string>();
        var skipped = new List<string>();
        var name = current.FullName;
        var phone = current.Phone;
        var t = T(typed.FullName, MaxName);
        if (t != null && t != name) { name = t; saved.Add("ชื่อ-นามสกุล"); }
        if (!string.IsNullOrWhiteSpace(typed.Phone))
        {
            var (p, ok) = Phone(typed.Phone);
            if (!ok) skipped.Add("เบอร์โทร (รูปแบบไม่ถูกต้อง)");
            else if (p != phone) { phone = p; saved.Add("เบอร์โทร"); }
        }

        var ship = current.Shipping;
        var shipText = T(typed.ShippingAddressText, MaxAddressLine);
        if (shipText != null)
        {
            var province = T(typed.ShippingProvince, MaxPart);
            var postal = T(typed.ShippingPostalCode, 10);
            if (postal != null && !IsDigits(postal, 5)) { skipped.Add("รหัสไปรษณีย์ (ต้องเป็นตัวเลข 5 หลัก)"); postal = null; }
            var same = Squash(shipText) == Squash(ComposeAddress(ship, false))
                && (province ?? ship?.Province) == ship?.Province && (postal ?? ship?.PostalCode) == ship?.PostalCode;
            if (!same)
            {
                ship = new MemberAddress(ship?.RecipientName ?? name, ship?.Phone ?? phone, shipText, null, null,
                    province ?? ship?.Province, postal ?? ship?.PostalCode);
                saved.Add("ที่อยู่จัดส่ง");
            }
        }

        var company = current.CompanyName; var tax = current.TaxId; var branch = current.BranchCode; var bill = current.Billing;
        if (!string.IsNullOrWhiteSpace(typed.BillingTaxId))
        {
            var digits = ThaiTaxId.Normalize(typed.BillingTaxId);
            var br = T(typed.BillingBranchCode, 10) ?? HeadOffice;
            if (digits.Length != 13 || !ThaiTaxId.IsValid(digits)) skipped.Add("ข้อมูลใบกำกับภาษี (เลขประจำตัวผู้เสียภาษีไม่ถูกต้อง)");
            else if (!IsDigits(br, 5)) skipped.Add("ข้อมูลใบกำกับภาษี (รหัสสาขาต้องเป็นตัวเลข 5 หลัก)");
            else
            {
                var bName = T(typed.BillingName, MaxName) ?? company;
                var bText = T(typed.BillingAddressText, MaxAddressLine);
                var changed = digits != tax || br != branch || bName != company
                    || (bText != null && Squash(bText) != Squash(ComposeAddress(bill, true)));
                if (changed)
                {
                    tax = digits; branch = br; company = bName;
                    if (bText != null && Squash(bText) != Squash(ComposeAddress(bill, true)))
                        bill = new MemberAddress(bName, null, bText, null, null, null, null);
                    saved.Add("ข้อมูลใบกำกับภาษี");
                }
            }
        }
        return new CheckoutProfileMerge(new MemberProfile(name, phone, company, tax, branch, ship, bill), saved, skipped);
    }

    /// <summary>ข้อความถึงผู้ใช้เมื่อมีช่องที่<b>ไม่ได้</b>บันทึกลงบัญชี (แนบใน <c>ApiResponse.Errors</c> ของการจอง/สั่งซื้อ — ไม่ใช่ no-op เงียบ) ·
    /// บันทึกได้ครบ/ไม่มีอะไรเปลี่ยน ⇒ null (หน้าเว็บบอก "บันทึกแล้ว" เอง)</summary>
    public static string? SaveNote(IReadOnlyList<string> skipped)
        => skipped.Count > 0
            ? "ไม่ได้บันทึกลงบัญชีของคุณ: " + string.Join(" · ", skipped) + " — แก้ได้ที่หน้า \"บัญชีของฉัน › ข้อมูลส่วนตัว\""
            : null;
}
