namespace Accounting.Helpers;

/// <summary>
/// รอบ 278 (คำตัดสิน 278-1..278-4) — ตัวตัดสินเดียว "ช่องลายเซ็นฝั่งบริษัทสองช่อง (ช่อง 0 ผู้จัดทำ/ผู้เสนอราคา/ผู้รับเงิน ·
/// ช่อง 1 ผู้มีอำนาจลงนาม) เป็นคนเดียวกันไหม" + แผนรวมชื่อ/ลายเซ็นเมื่อเป็นคนเดียวกัน
///
/// <para>ที่มา (เจ้าของ · QT-20261119-0001 ที่ระบบภายนอกสร้างผ่าน API key): ช่อง 0 = <c>PreparerName</c> "K.นัท" +
/// <c>PreparerSignatureBase64</c> ที่ payload ส่งมา · ช่อง 1 = ผู้ออก API key (CreatedBy/UpdatedBy) "ณัฐพล ทรัพย์บวรโรจน์" ซึ่งไม่ได้อัปโหลด
/// ลายเซ็นในโปรไฟล์ ⇒ ช่อง 1 ว่าง ทั้งที่เป็นคนเดียวกัน</para>
///
/// <para><b>ตัดสินด้วยกุญแจเท่านั้น — ห้ามเทียบความคล้ายชื่อ</b> (DECISION_DOCTRINE · "K.นัท" กับ "ณัฐพล" เทียบด้วยชื่อไม่ได้) ·
/// กุญแจที่รับ (ลำดับ): (1) <c>Document.PreparerUserId</c> ที่ผู้เรียก API ระบุ (ตรวจสมาชิกบริษัทตอนรับ) = ผู้ใช้ช่อง 1 ·
/// ระบุไว้เป็นคนอื่น ⇒ ไม่รวมเด็ดขาด (ไม่ตกไปเทียบชื่อเล่น) · (2) ช่อง 0 เป็นผู้ใช้ในระบบคนเดียวกับช่อง 1 (UserId เดียวกัน) ·
/// (3) ชื่อที่ payload ส่งมาตรง "ชื่ออื่นของฉัน" ที่ผู้ใช้ช่อง 1 <b>ประกาศเองในโปรไฟล์</b> (<c>User.SignerAliases</c> — ตรงทั้งคำหลังตัดช่องว่าง/
/// ตัวพิมพ์ ไม่ใช่ fuzzy) · ไม่เข้าข้อใด ⇒ คนละคน ไม่แตะ</para>
///
/// <para>ช่องลูกค้า (เซ็นรับของ/ยอมรับออนไลน์) ไม่ผ่านตัวนี้เลย · ช่อง 1 ที่เป็นผู้มีอำนาจลงนามที่บริษัทตั้งเอง (ไม่ใช่ผู้ใช้) ไม่มีกุญแจ ⇒ ไม่รวม</para>
/// </summary>
public static class SignatureSlotIdentity
{
    public const string RuleCode = "SIGNER-SAME-PERSON";
    public const int MaxAliases = 10;
    public const int MaxAliasLength = 100;

    /// <summary>เหตุที่ถือว่าเป็นคนเดียวกัน (None = คนละคน/ไม่รู้)</summary>
    public enum LinkKind { None, SameUser, PreparerUserId, ProfileAlias }

    /// <summary>ช่อง 0 — <paramref name="UserId"/> = ผู้ใช้เจ้าของช่อง <b>เมื่อตัวตนไม่ได้มาจาก payload</b> (มี payload ⇒ ส่ง null) ·
    /// <paramref name="BoundUserId"/> = <c>Document.PreparerUserId</c> · <paramref name="PayloadName"/> = ชื่อที่ระบบภายนอกส่งมา</summary>
    public readonly record struct Slot0(Guid? UserId, Guid? BoundUserId, string? PayloadName, bool IsCustomer = false);

    /// <summary>ช่อง 1 — ผู้ใช้ในระบบเจ้าของช่อง (null = ไม่ใช่ผู้ใช้ เช่น ผู้มีอำนาจลงนามที่บริษัทตั้ง) + ชื่ออื่นที่ประกาศในโปรไฟล์</summary>
    public readonly record struct Slot1(Guid? UserId, string? StoredAliases, bool IsCustomer = false);

    /// <summary>ตัวตัดสิน "คนเดียวกันไหม" — คืนผู้ใช้ที่เป็นเจ้าของทั้งสองช่อง</summary>
    public static (LinkKind Kind, Guid? UserId) Link(Slot0 s0, Slot1 s1)
    {
        if (s0.IsCustomer || s1.IsCustomer || s1.UserId is not { } u || u == Guid.Empty) return (LinkKind.None, null);
        if (s0.BoundUserId is { } bound && bound != Guid.Empty)
            return bound == u ? (LinkKind.PreparerUserId, u) : (LinkKind.None, null);
        if (s0.UserId is { } a && a == u) return (LinkKind.SameUser, u);
        var n = NormalizeName(s0.PayloadName);
        if (s0.UserId == null && n != null && ParseAliases(s1.StoredAliases).Any(x => NormalizeName(x) == n))
            return (LinkKind.ProfileAlias, u);
        return (LinkKind.None, null);
    }

    /// <summary>หน้าตาของช่องก่อนรวม</summary>
    public readonly record struct SlotFace(string? Name, string? Title, bool HasImage);

    /// <summary>ผลรวม — ชื่อ/ตำแหน่งของแต่ละช่อง + คัดลอกรูปทิศไหน (รูปของคนเดียวกันเท่านั้น — ผู้เรียกต้องผ่าน <see cref="Link"/> ก่อน)</summary>
    public readonly record struct MergePlan(string? Name0, string? Name1, string? Title0, string? Title1, bool CopyImage0To1, bool CopyImage1To0);

    /// <summary>ชื่อแสดงของผู้ใช้ตามโปรไฟล์ (ตัวเดียวกับที่ช่องลายเซ็นใช้มาตลอด): ชื่อที่ตั้งสำหรับลายเซ็น → ชื่อ-สกุลของบัญชี</summary>
    public static string? ProfileDisplayName(string? signatureName, string? fullName)
        => !string.IsNullOrWhiteSpace(signatureName) ? signatureName!.Trim()
         : !string.IsNullOrWhiteSpace(fullName) ? fullName!.Trim() : null;

    /// <summary>
    /// แผนรวมเมื่อสองช่องเป็นคนเดียวกัน (คำตัดสิน 278-2):
    /// <list type="bullet">
    /// <item><b>ชื่อ "ครบกว่า" = ชื่อตามโปรไฟล์ของผู้ใช้ที่ผูก</b> (<see cref="ProfileDisplayName"/>) ทั้งสองช่อง — เจ้าตัวรับรองชื่อนี้เอง ·
    /// ชื่อที่ payload ส่งมาเป็นแค่ชื่อเรียก · <b>ไม่เลือกจากความยาวสตริง</b> · โปรไฟล์ไม่มีชื่อ ⇒ แต่ละช่องคงชื่อเดิม (ช่องว่างรับชื่อของอีกช่อง)</item>
    /// <item>ตำแหน่ง: คงของช่องตัวเอง · ว่าง ⇒ ตำแหน่งในโปรไฟล์</item>
    /// <item>รูป: ช่องที่ไม่มีรูปรับรูปของอีกช่อง (คนเดียวกัน) · ทั้งสองมีรูป ⇒ คงของใครของมัน</item>
    /// </list></summary>
    public static MergePlan Merge(SlotFace s0, SlotFace s1, string? profileName, string? profileTitle)
    {
        var p = string.IsNullOrWhiteSpace(profileName) ? null : profileName!.Trim();
        string? n0 = p ?? Blank(s0.Name) ?? Blank(s1.Name);
        string? n1 = p ?? Blank(s1.Name) ?? Blank(s0.Name);
        var t = Blank(profileTitle);
        return new MergePlan(n0, n1, Blank(s0.Title) ?? t, Blank(s1.Title) ?? t,
            CopyImage0To1: s0.HasImage && !s1.HasImage,
            CopyImage1To0: s1.HasImage && !s0.HasImage);
    }

    /// <summary>ตัดช่องว่างหัวท้าย · ยุบช่องว่างกลาง · ตัวพิมพ์เล็ก — สำหรับเทียบ "ตรงทั้งคำ" เท่านั้น (ไม่ใช่วัดความคล้าย) · ว่าง ⇒ null</summary>
    public static string? NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).ToLowerInvariant();
    }

    /// <summary>อ่านรายชื่อที่เก็บ (คั่นด้วยขึ้นบรรทัด/จุลภาค/อัฒภาค)</summary>
    private static IReadOnlyList<string> ParseAliases(string? stored)
        => string.IsNullOrWhiteSpace(stored) ? Array.Empty<string>()
         : stored.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(x => x.Length > 0).ToArray();

    /// <summary>ทำความสะอาดก่อนเก็บ (ฝั่งเขียนตัวเดียว · AuthService.UpdateProfileAsync): ตัดซ้ำ (ตามรูปที่เทียบ) · เกิน <see cref="MaxAliases"/> ชื่อ
    /// หรือชื่อยาวเกิน <see cref="MaxAliasLength"/> ⇒ <see cref="BusinessRuleException"/> (บอกเหตุ — ไม่ตัดทิ้งเงียบ) · ว่าง ⇒ null</summary>
    public static string? SanitizeForStore(string? input)
    {
        var seen = new HashSet<string>();
        var kept = new List<string>();
        foreach (var raw in ParseAliases(input))
        {
            var display = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (display.Length > MaxAliasLength)
                throw new BusinessRuleException($"ชื่ออื่นสำหรับช่องลายเซ็นยาวเกิน {MaxAliasLength} ตัวอักษร: \"{display[..Math.Min(30, display.Length)]}…\"", RuleCode);
            if (seen.Add(NormalizeName(display)!)) kept.Add(display);
        }
        if (kept.Count > MaxAliases)
            throw new BusinessRuleException($"ตั้งชื่ออื่นสำหรับช่องลายเซ็นได้ไม่เกิน {MaxAliases} ชื่อ", RuleCode);
        return kept.Count == 0 ? null : string.Join('\n', kept);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
}
