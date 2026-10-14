namespace Accounting.Helpers;

/// <summary>กลุ่มของแถว "ช่องทางเงิน" ที่ endpoint <c>accounts/payment-channels</c> คืน — หน้าเว็บแยก optgroup ตามนี้ ห้ามกรองรหัสเอง</summary>
public enum MoneyChannelGroup
{
    /// <summary>ผังเงินฝากธนาคาร — ผูกกับบัญชีธนาคารในทะเบียน (หรือรหัสเงินฝาก 1112x ของผังมาตรฐาน) ⇒ ควรเลือกผ่าน "บัญชีธนาคาร"</summary>
    Bank = 1,
    /// <summary>เงินสด / เงินทดรองกรรมการ / กระเป๋าเงิน / ช่องจ่ายอื่น ที่ไม่ใช่บัญชีธนาคาร</summary>
    Other = 2,
}

/// <summary>ปัญหาของบัญชีธนาคาร (ตาราง BankAccounts) ที่ถูกเลือกเป็นแหล่งเงิน — แยก "ไม่พบ" ออกจาก "ยังไม่ผูกผัง" (ID#19)</summary>
public enum BankLinkProblem
{
    None = 0,
    /// <summary>ไม่พบในบริษัทนี้ (ถูกลบ · id ของบริษัทอื่น · id ผิดตาราง)</summary>
    NotFound = 1,
    /// <summary>บัญชีธนาคารถูกปิดใช้งาน</summary>
    Inactive = 2,
    /// <summary>ยังไม่ได้ผูกผังบัญชี (LinkedAccountId ว่าง)</summary>
    NotLinked = 3,
    /// <summary>ผูกผังไว้แต่ผังนั้นลงรายการไม่ได้ (ปิดใช้ · บัญชีคุม · ไม่อยู่ในบริษัทนี้)</summary>
    LinkedAccountUnusable = 4,
}

/// <summary>
/// **กติกา "ผังบัญชีนี้คือที่อยู่ของเงิน (เงินสด/ธนาคาร/ช่องจ่าย) หรือไม่" — ตัวตั้งตัวเดียวของทั้งระบบ** (2026-10-14 · PC#11)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ รหัสขึ้นต้น <c>111 / 1133 / 2123</c> และ "ตัด 1112" ถูกเขียนซ้ำอย่างน้อย 9 ที่ (AccountingService ·
/// PayrollService · BankService · documents/recurring/payroll/tax-remittance.html) ⇒ บริษัทที่ใช้ผังของตัวเอง (เช่น เงินสด 1010)
/// เห็นแค่ "เงินสด (ค่าเริ่มต้น)" ที่ resolve ไม่เจอ · ตั้งแหล่งจ่ายเงินเดือนไม่ได้ · และธนาคารที่ผูกกับผัง 111x ที่ไม่ใช่ 1112
/// โผล่สองครั้งในรายการ (ทั้ง bank: และ account:)</para>
///
/// <para>═══ กติกา ═══</para>
/// <list type="bullet">
/// <item>ผังที่<b>ผูกกับบัญชีธนาคารที่เปิดใช้</b> = ที่อยู่ของเงินเสมอ (ทุกผัง ไม่ขึ้นกับรหัส) · กลุ่ม <see cref="MoneyChannelGroup.Bank"/></item>
/// <item>ผังมาตรฐาน: รหัสขึ้นต้น <see cref="StandardPrefixes"/> (111 เงินสด/เงินฝาก · 1133 ลูกหนี้ผู้ให้บริการรับชำระ/ช่องจ่าย · 2123 เงินทดรองกรรมการ)
///   — รหัส 1112x ที่ยังไม่ผูกธนาคารถือเป็นกลุ่มธนาคาร (ควรผูกในทะเบียนธนาคารแล้วเลือกผ่านบัญชีธนาคาร)</item>
/// <item>ผังของบริษัทเอง: ตั้ง <c>CompanySettings.MoneyAccountCodePrefixes</c> (คั่นด้วยจุลภาค เช่น <c>101, 102</c>) — ตั้งแล้ว<b>แทน</b>รหัสมาตรฐาน
///   (ผังของบริษัทเองอาจใช้ 2123 เป็นอย่างอื่น) และไม่มีกลุ่ม 1112 อัตโนมัติ</item>
/// </list>
/// <para>ไม่ใช่ที่อยู่ของเงิน ≠ บล็อก (คำตัดสินข้อ 150): ผู้เรียกเตือนด้วย <see cref="NotMoneyAccountWarning"/> · บล็อกเฉพาะ "ลงรายการไม่ได้"
/// (ไม่พบ/ปิดใช้/บัญชีคุม — <see cref="PostingAccountRule"/>)</para>
/// <para>G6: pure · ไม่มี I/O · ผู้ดึงข้อมูลคือ <c>MoneyAccountDirectory</c></para>
/// </summary>
public static class MoneyAccountRule
{
    /// <summary>รหัสขึ้นต้นของผังมาตรฐาน (ใช้เมื่อบริษัทไม่ได้ตั้งรหัสเอง)</summary>
    public static readonly IReadOnlyList<string> StandardPrefixes = new[] { "111", "1133", "2123" };

    /// <summary>รหัสขึ้นต้นของ "เงินฝากธนาคาร" ในผังมาตรฐาน</summary>
    public const string StandardBankDepositPrefix = "1112";

    /// <summary>ความยาวสูงสุดของค่าตั้ง (กันค่าขยะ)</summary>
    public const int MaxSettingLength = 200;

    /// <summary>แปลงค่าตั้ง <c>MoneyAccountCodePrefixes</c> เป็นรายการ prefix · ว่าง/null = ผังมาตรฐาน</summary>
    public static IReadOnlyList<string> ParsePrefixes(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return StandardPrefixes;
        var list = configured.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count == 0 ? StandardPrefixes : list;
    }

    /// <summary>รายการ prefix นี้คือชุดของผังมาตรฐานไหม</summary>
    public static bool IsStandard(IReadOnlyList<string> prefixes) => prefixes.SequenceEqual(StandardPrefixes, StringComparer.OrdinalIgnoreCase);

    /// <summary>ปรับค่าตั้งก่อนเก็บ: null = ไม่แตะ (ผู้เรียกจัดการ) · ว่าง = ล้าง (กลับผังมาตรฐาน) · อื่น ๆ = รูปมาตรฐาน "a, b"
    /// · คืน error (ไทย) เมื่อมีตัวที่ไม่ใช่ตัวเลข/ตัวอักษร/ขีด</summary>
    public static (string? Normalized, string? Error) NormalizeSetting(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return (null, null);
        if (trimmed.Length > MaxSettingLength)
            return (null, $"รหัสผังบัญชีเงินสด/ธนาคารยาวเกิน {MaxSettingLength} ตัวอักษร — ใส่เฉพาะรหัสขึ้นต้น คั่นด้วยจุลภาค เช่น 101, 102");
        var parts = ParsePrefixes(trimmed);
        if (parts.Any(p => !p.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '.')))
            return (null, "รหัสผังบัญชีเงินสด/ธนาคาร ใส่ได้เฉพาะตัวเลข/ตัวอักษร คั่นด้วยจุลภาค เช่น 101, 102");
        return (string.Join(", ", parts), null);
    }

    /// <summary>รหัสนี้เข้ากติกา "ที่อยู่ของเงิน" ตามรหัสขึ้นต้นไหม</summary>
    private static bool MatchesPrefixes(string accountCode, IReadOnlyList<string> prefixes)
        => !string.IsNullOrEmpty(accountCode)
           && prefixes.Any(p => accountCode.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>เป็นที่อยู่ของเงินไหม — ผูกธนาคาร (ทุกรหัส) หรือรหัสเข้ากติกา</summary>
    public static bool IsMoneyAccount(string accountCode, bool linkedToActiveBank, IReadOnlyList<string> prefixes)
        => linkedToActiveBank || MatchesPrefixes(accountCode, prefixes);

    /// <summary>กลุ่มของแถว (null = ไม่ใช่ที่อยู่ของเงิน) · ผูกธนาคาร หรือ 1112x ของผังมาตรฐาน = ธนาคาร</summary>
    public static MoneyChannelGroup? GroupOf(string accountCode, bool linkedToActiveBank, IReadOnlyList<string> prefixes)
    {
        if (linkedToActiveBank) return MoneyChannelGroup.Bank;
        if (!MatchesPrefixes(accountCode, prefixes)) return null;
        var standard = IsStandard(prefixes);
        return standard && accountCode.StartsWith(StandardBankDepositPrefix, StringComparison.Ordinal)
            ? MoneyChannelGroup.Bank
            : MoneyChannelGroup.Other;
    }

    /// <summary>ชื่อกลุ่มที่ส่งให้หน้าเว็บ (enum ออกเป็นชื่อ — CLAUDE.md F2 ข้อ 5)</summary>
    public static string GroupName(MoneyChannelGroup g) => g == MoneyChannelGroup.Bank ? "bank" : "other";

    /// <summary>คำเตือน (ไม่บล็อก) เมื่อผู้ใช้เลือกผังที่ไม่ใช่ที่อยู่ของเงินเป็นแหล่งรับ/จ่าย</summary>
    public static string NotMoneyAccountWarning(string accountCode, string accountName, string fieldLabel)
        => $"{fieldLabel}: ผัง {accountCode} {accountName} ไม่ใช่บัญชีเงินสด/ธนาคาร/ช่องจ่ายตามผังของบริษัท — บันทึกให้แล้ว "
           + "แต่ตรวจว่าเงินออก/เข้าทางนี้จริง · ถ้าบริษัทใช้ผังบัญชีของตัวเอง ตั้ง \"รหัสผังบัญชีเงินสด/ธนาคาร\" ที่หน้า ตั้งค่า → การเงิน";

    /// <summary>ข้อความล้มดังของบัญชีธนาคารที่ใช้เป็นแหล่งเงินไม่ได้ — บอกช่อง/ที่แก้เสมอ (PC#17 · ID#19)</summary>
    public static string BankProblemMessage(BankLinkProblem problem, string? bankLabel, string? linkedAccountDetail = null)
    {
        var name = string.IsNullOrWhiteSpace(bankLabel) ? "ที่เลือก" : $"\"{bankLabel}\"";
        return problem switch
        {
            BankLinkProblem.NotFound =>
                "ไม่พบบัญชีธนาคารที่เลือกในบริษัทนี้ (อาจถูกลบหรือมาจากรายการเก่า) — เลือกบัญชีธนาคารใหม่จากรายการ",
            BankLinkProblem.Inactive =>
                $"บัญชีธนาคาร {name} ถูกปิดใช้งานแล้ว — เปิดใช้งานที่หน้า \"บัญชีธนาคาร\" หรือเลือกบัญชีอื่น",
            BankLinkProblem.NotLinked =>
                $"บัญชีธนาคาร {name} ยังไม่ได้ผูกผังบัญชี — ไปที่หน้า \"บัญชีธนาคาร\" กดแก้ไขบัญชีนี้แล้วเลือกผังบัญชีเงินฝาก หรือเลือกบัญชีอื่น",
            BankLinkProblem.LinkedAccountUnusable =>
                $"ผังบัญชีที่บัญชีธนาคาร {name} ผูกไว้ใช้ลงรายการไม่ได้"
                + (string.IsNullOrWhiteSpace(linkedAccountDetail) ? "" : $" ({linkedAccountDetail})")
                + " — ไปที่หน้า \"บัญชีธนาคาร\" แล้วผูกผังบัญชีเงินฝากที่เปิดใช้อยู่",
            _ => "",
        };
    }

    /// <summary>ตัดสินปัญหาบัญชีธนาคาร (pure) — <paramref name="found"/>=false ⇒ ไม่พบ · ลำดับ: ไม่พบ → ปิดใช้ → ไม่ผูก → ผังที่ผูกใช้ไม่ได้</summary>
    public static BankLinkProblem CheckBank(bool found, bool bankActive, Guid? linkedAccountId, PostingAccountProblem linkedAccountProblem)
    {
        if (!found) return BankLinkProblem.NotFound;
        if (!bankActive) return BankLinkProblem.Inactive;
        if (linkedAccountId is null) return BankLinkProblem.NotLinked;
        return linkedAccountProblem == PostingAccountProblem.None ? BankLinkProblem.None : BankLinkProblem.LinkedAccountUnusable;
    }
}
