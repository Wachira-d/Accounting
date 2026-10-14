namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของผังบัญชีหนึ่งตัวที่ตัวตัดสิน "ลงรายการได้ไหม" ต้องใช้ — ผู้ดึงข้อมูลกรอง CompanyId + !IsDeleted แล้ว</summary>
public sealed record PostingAccountFacts(Guid Id, string Code, string Name, bool IsActive, bool IsGroup);

/// <summary>ปัญหาของผังบัญชีที่จะเขียนลงบรรทัดรายการบัญชี</summary>
public enum PostingAccountProblem
{
    None = 0,
    /// <summary>ไม่พบในผังของบริษัทนี้ (ถูกลบ · id ของบริษัทอื่น · id ผิดตาราง เช่น BankAccount.Id)</summary>
    NotFound = 1,
    /// <summary>ปิดใช้งาน</summary>
    Inactive = 2,
    /// <summary>บัญชีคุม — มีบัญชีย่อยอยู่ ลงรายการตรงไม่ได้ (ยอดคุมต้องมาจากผลรวมบัญชีย่อย)</summary>
    Group = 3,
}

/// <summary>
/// **ผังบัญชี "ลงรายการได้" — ของบริษัทนี้ · เปิดใช้ · ไม่ใช่บัญชีคุม** — ตัวตัดสินตัวเดียวของทุกทางที่รับ id ผังบัญชีจากผู้ใช้/API
/// แล้วเขียนลง <c>JournalEntryLine.AccountId</c> หรือเก็บเป็นค่าตั้งที่จะถูกลงรายการทีหลัง (2026-10-14 · ID#13/#14/#23/#24 · PC#6)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ FinancialManagement · FixedAsset · Integration mapping · ตั้งค่า gateway · ค่าตั้งแหล่งเงินบริษัท ·
/// บัญชีลูกหนี้/เจ้าหนี้ของผู้ติดต่อ เขียน id ผังที่รับมาดิบ ๆ ⇒ ผังบริษัทอื่น (ข้าม tenant) · ผังที่ปิดใช้ · บัญชีคุม (เช่น 11122
/// ที่มีบัญชีย่อยธนาคาร — กระทบยอดไม่เห็น) ลงสมุดได้ · ทางที่ id ผิดตาราง ได้ FK 23503 ที่ข้อความพาไปผิดทาง</para>
///
/// <para>"บัญชีคุม" ตัดสินจาก<b>การมีบัญชีย่อย</b> ไม่ใช่ระดับ (Level) — ผังของบริษัทเองอาจมีบัญชีระดับ 2 ที่ลงรายการได้ ·
/// ทุกปัญหาเป็นการบล็อก (ความถูกต้องของสมุดบัญชี + tenant — คำตัดสินข้อ 150) และข้อความบอกช่อง + ที่แก้เสมอ</para>
/// <para>G6: pure · ผู้ดึงข้อมูลคือ <c>PostingAccountGuard</c></para>
/// </summary>
public static class PostingAccountRule
{
    /// <summary>รหัสกฎที่แนบไปกับ BusinessRuleException</summary>
    public const string RuleCode = "GL-POSTING-ACCOUNT";

    public static PostingAccountProblem Check(PostingAccountFacts? account)
    {
        if (account is null) return PostingAccountProblem.NotFound;
        if (!account.IsActive) return PostingAccountProblem.Inactive;
        if (account.IsGroup) return PostingAccountProblem.Group;
        return PostingAccountProblem.None;
    }

    /// <summary>ข้อความไทยพร้อมทางไปต่อ · null = ใช้ได้ · <paramref name="fieldLabel"/> = ชื่อช่องที่ผู้ใช้เห็น (เช่น "บัญชีพักเงิน")</summary>
    public static string? Message(PostingAccountProblem problem, PostingAccountFacts? account, string fieldLabel)
    {
        var field = string.IsNullOrWhiteSpace(fieldLabel) ? "บัญชีที่เลือก" : fieldLabel;
        var name = account is null ? "" : $"{account.Code} {account.Name}".Trim();
        return problem switch
        {
            PostingAccountProblem.None => null,
            PostingAccountProblem.NotFound =>
                $"{field}: ไม่พบบัญชีที่เลือกในผังบัญชีของบริษัทนี้ (อาจถูกลบ มาจากบริษัทอื่น หรือเป็นรหัสของรายการชนิดอื่น) — เลือกบัญชีใหม่จากรายการ",
            PostingAccountProblem.Inactive =>
                $"{field}: บัญชี {name} ถูกปิดใช้งานแล้ว — เปิดใช้งานที่หน้า \"ผังบัญชี\" หรือเลือกบัญชีอื่น",
            PostingAccountProblem.Group =>
                $"{field}: บัญชี {name} เป็นบัญชีคุม (มีบัญชีย่อย) ลงรายการตรงไม่ได้ — เลือกบัญชีย่อยใต้บัญชีนี้แทน",
            _ => null,
        };
    }

    /// <summary>ย่อ: ตัดสิน + ข้อความในครั้งเดียว (null = ใช้ได้)</summary>
    public static string? Problem(PostingAccountFacts? account, string fieldLabel) => Message(Check(account), account, fieldLabel);

    /// <summary>คำอธิบายสั้นของปัญหา (ใช้ประกอบข้อความของผู้เรียก เช่น ผังที่บัญชีธนาคารผูกไว้)</summary>
    public static string ShortReason(PostingAccountProblem problem, PostingAccountFacts? account)
    {
        var name = account is null ? "" : $"{account.Code} {account.Name}".Trim();
        return problem switch
        {
            PostingAccountProblem.NotFound => "ไม่พบผังในบริษัทนี้",
            PostingAccountProblem.Inactive => $"{name} ถูกปิดใช้งาน",
            PostingAccountProblem.Group => $"{name} เป็นบัญชีคุม",
            _ => "",
        };
    }
}
