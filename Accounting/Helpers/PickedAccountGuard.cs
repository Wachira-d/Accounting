using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ด่านตรวจ "รหัสที่ผู้ใช้เลือกมา" ก่อนเขียนลงบรรทัด JE — ผังบัญชี และบัญชีธนาคาร (→ ผังที่ผูก)** (ทีม F1 · 2026-10-14 ·
/// PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 1 · ส่วนที่ 2 ข้อ 1/2/13/19)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ หน้าเงินสดย่อยส่ง <c>BankAccount.Id</c> ไปในช่องที่เซิร์ฟเวอร์ใช้เป็น <c>ChartOfAccount.Id</c>
/// แล้วเขียนตรงลง <c>JournalEntryLine.AccountId</c> ⇒ FK ล้ม ผู้ใช้เห็น "ถูกอ้างอิงโดยข้อมูลอื่น… ลบหรือเปลี่ยนไม่ได้" (วินิจฉัยผิดทาง) ·
/// สินเชื่อ/เงินสดย่อยรับรหัสผังจาก request โดยไม่ตรวจบริษัท/สถานะ/ระดับ ⇒ ลงบรรทัด JE ข้ามบริษัทหรือผังคุมได้ ·
/// "ไม่พบบัญชีธนาคาร" กับ "ยังไม่ผูกผัง" ถูกรายงานเป็นข้อความเดียวกัน (<c>.Select(b =&gt; b.LinkedAccountId)</c> คืน null ทั้งสองกรณี)</para>
///
/// <para>═══ กติกา ═══ ผังบัญชีที่ลงรายการได้ = ของบริษัทนี้ · ไม่ถูกลบ · เปิดใช้งาน · ระดับรายการ (Level ≥ 4) · ชนิดตรงกับช่อง (ถ้าช่องกำหนด) ·
/// บัญชีธนาคาร = ของบริษัทนี้ · ไม่ถูกลบ · เปิดใช้งาน · ผูกผังแล้ว · ผังที่ผูกผ่านกติกาเดียวกัน · ทุกข้อความบอก "ช่องไหน · แก้ที่ไหน" เป็นไทย
/// (คำตัดสินข้อ 150 — บล็อกเพราะความถูกต้องของ GL/การแยกบริษัท ต้องมีทางไปต่อ)</para>
///
/// <para>ตัวตัดสินข้อความเป็น pure (<see cref="AccountProblem"/> · <see cref="BankProblem"/> · <see cref="LinkedGlProblem"/>) มีเทสต์ ·
/// ตัวอ่านฐาน (<see cref="RequirePostingAccountAsync"/> · <see cref="ResolveBankGlAsync"/> · <see cref="ResolveMoneyChannelAsync"/>)
/// ผู้ใช้: เงินสดย่อย · สินเชื่อ · ใบเบิก · เงินทดรอง · เช็ค (ตรวจเฉพาะบัญชีธนาคาร)</para>
/// </summary>
public static class PickedAccountGuard
{
    public const string RuleAccountInvalid = "PICKED-ACCOUNT-INVALID";
    public const string RuleBankInvalid = "PICKED-BANK-INVALID";
    public const string RuleChannelAmbiguous = "PICKED-CHANNEL-AMBIGUOUS";

    /// <summary>ข้อเท็จจริงของผังบัญชีที่ค้นเจอ (null = ไม่พบในบริษัทนี้/ถูกลบ)</summary>
    public sealed record AccountFacts(string Code, string Name, bool IsActive, int Level, AccountType Type);

    /// <summary>ข้อเท็จจริงของบัญชีธนาคารที่ค้นเจอ (null = ไม่พบในบริษัทนี้/ถูกลบ)</summary>
    public sealed record BankFacts(string Label, bool IsActive, Guid? LinkedAccountId);

    /// <summary>ชนิดบัญชีที่เป็น "ที่อยู่ของเงิน" ได้ — เงินสด/เงินฝาก/กระเป๋าเงิน (สินทรัพย์) และเงินกู้ยืมกรรมการที่จ่ายแทน (หนี้สิน ·
    /// ตรงกับชุด <c>accounts/payment-channels</c>)</summary>
    public static readonly AccountType[] MoneyTypes = { AccountType.Asset, AccountType.Liability };

    public static string TypeLabel(AccountType t) => t switch
    {
        AccountType.Asset => "สินทรัพย์",
        AccountType.Liability => "หนี้สิน",
        AccountType.Equity => "ส่วนของเจ้าของ",
        AccountType.Revenue => "รายได้",
        AccountType.Expense => "ค่าใช้จ่าย",
        _ => "ไม่ทราบชนิด",
    };

    /// <summary>ผังบัญชีนี้ลงบรรทัด JE ในช่อง <paramref name="fieldLabel"/> ได้ไหม — null = ได้ · ข้อความ = เหตุที่ไม่ได้ + ทางไปต่อ</summary>
    internal static string? AccountProblem(AccountFacts? a, string fieldLabel, IReadOnlyCollection<AccountType>? allowedTypes = null)
    {
        if (a == null)
            return $"ช่อง “{fieldLabel}”: ไม่พบผังบัญชีที่เลือกในบริษัทนี้ (อาจถูกลบไปแล้ว หรือหน้าจอค้างข้อมูลเก่า) — รีเฟรชหน้าแล้วเลือกผังบัญชีใหม่";
        if (!a.IsActive)
            return $"ช่อง “{fieldLabel}”: ผังบัญชี {a.Code} {a.Name} ถูกปิดการใช้งาน — เลือกผังอื่น หรือเปิดใช้งานผังนี้ที่หน้า “ผังบัญชี”";
        if (a.Level < 4)
            return $"ช่อง “{fieldLabel}”: {a.Code} {a.Name} เป็นบัญชีคุม (หมวด) ลงรายการไม่ได้ — เลือกบัญชีย่อยระดับรายการในหมวดนี้แทน";
        if (allowedTypes is { Count: > 0 } && !allowedTypes.Contains(a.Type))
            return $"ช่อง “{fieldLabel}”: ผังบัญชี {a.Code} {a.Name} เป็นบัญชีประเภท{TypeLabel(a.Type)} — ช่องนี้ต้องเป็นบัญชีประเภท"
                   + string.Join("หรือ", allowedTypes.Select(TypeLabel)) + " เลือกผังบัญชีใหม่";
        return null;
    }

    /// <summary>บัญชีธนาคารนี้ใช้เป็นช่องทางเงินได้ไหม (ก่อนดูผังที่ผูก) · <paramref name="requireLinked"/>=false สำหรับงานที่ขยับแค่ยอดธนาคาร
    /// ไม่ลง JE (สมุดเช็ค)</summary>
    internal static string? BankProblem(BankFacts? b, string fieldLabel, bool requireLinked = true)
    {
        if (b == null)
            return $"ช่อง “{fieldLabel}”: ไม่พบบัญชีธนาคารที่เลือกในบริษัทนี้ (อาจถูกลบไปแล้ว หรือหน้าจอค้างข้อมูลเก่า) — รีเฟรชหน้าแล้วเลือกบัญชีธนาคารใหม่";
        if (!b.IsActive)
            return $"ช่อง “{fieldLabel}”: บัญชีธนาคาร {b.Label} ถูกปิดการใช้งาน — เลือกบัญชีอื่น หรือเปิดใช้งานที่หน้า “บัญชีธนาคาร”";
        if (requireLinked && !IsSet(b.LinkedAccountId))
            return $"ช่อง “{fieldLabel}”: บัญชีธนาคาร {b.Label} ยังไม่ได้ผูกผังบัญชี — ไปที่หน้า “บัญชีธนาคาร” กดแก้ไขบัญชีนี้ "
                   + "แล้วเลือก “ผังบัญชีที่เชื่อมโยง” ก่อน หรือเลือกบัญชีอื่น";
        return null;
    }

    /// <summary>ผังที่บัญชีธนาคารผูกไว้ยังลงรายการได้ไหม</summary>
    internal static string? LinkedGlProblem(BankFacts b, AccountFacts? gl, string fieldLabel)
    {
        var inner = AccountProblem(gl, fieldLabel, MoneyTypes);
        if (inner == null) return null;
        var why = gl == null ? "ผังที่ผูกไว้ถูกลบแล้ว"
            : !gl.IsActive ? $"ผัง {gl.Code} ที่ผูกไว้ถูกปิดการใช้งาน"
            : gl.Level < 4 ? $"ผัง {gl.Code} ที่ผูกไว้เป็นบัญชีคุม"
            : $"ผัง {gl.Code} ที่ผูกไว้เป็นบัญชีประเภท{TypeLabel(gl.Type)}";
        return $"ช่อง “{fieldLabel}”: บัญชีธนาคาร {b.Label} ผูกกับผังบัญชีที่ลงรายการไม่ได้ ({why}) — แก้ “ผังบัญชีที่เชื่อมโยง” ที่หน้า “บัญชีธนาคาร” "
               + "หรือเลือกบัญชีอื่น";
    }

    /// <summary>ช่องทางเงินส่งมาทั้งสองแบบพร้อมกันไหม (บัญชีธนาคาร + ผังเงินตรง) — เลือกได้ทางเดียว ห้ามเดาว่าตัวไหนชนะ</summary>
    internal static string? ChannelProblem(Guid? bankAccountId, Guid? moneyAccountId, string fieldLabel)
        => IsSet(bankAccountId) && IsSet(moneyAccountId)
            ? $"ช่อง “{fieldLabel}”: ส่งมาทั้งบัญชีธนาคารและผังบัญชีเงินสดพร้อมกัน — เลือกอย่างใดอย่างหนึ่ง"
            : null;

    public static bool IsSet(Guid? id) => id is Guid g && g != Guid.Empty;

    // ─────────────────────────── ตัวอ่านฐาน ───────────────────────────

    /// <summary>อ่านข้อเท็จจริงของผังบัญชี (ของบริษัทนี้ · ไม่ถูกลบ) — null = ไม่พบ</summary>
    private static Task<AccountFacts?> LoadAccountAsync(AccountingDbContext db, Guid companyId, Guid accountId, CancellationToken ct = default)
        => db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new AccountFacts(a.AccountCode, a.AccountName, a.IsActive, a.Level, a.AccountType))
            .FirstOrDefaultAsync(ct);

    /// <summary>ผังบัญชีที่ผู้ใช้เลือกต้องลงรายการได้ — ไม่ได้ ⇒ <see cref="BusinessRuleException"/> ข้อความไทยพร้อมทางไปต่อ</summary>
    public static async Task<AccountFacts> RequirePostingAccountAsync(AccountingDbContext db, Guid companyId, Guid accountId,
        string fieldLabel, IReadOnlyCollection<AccountType>? allowedTypes = null, CancellationToken ct = default)
    {
        var facts = await LoadAccountAsync(db, companyId, accountId, ct);
        var problem = AccountProblem(facts, fieldLabel, allowedTypes);
        if (problem != null) throw new BusinessRuleException(problem, RuleAccountInvalid);
        return facts!;
    }

    /// <summary>บัญชีธนาคาร (<c>BankAccounts.Id</c>) → ผังที่ผูก (<c>LinkedAccountId</c>) ที่ลงรายการได้ · แยก "ไม่พบ" / "ปิดใช้งาน" /
    /// "ยังไม่ผูก" / "ผังที่ผูกใช้ไม่ได้" เป็นคนละข้อความ</summary>
    public static async Task<Guid> ResolveBankGlAsync(AccountingDbContext db, Guid companyId, Guid bankAccountId,
        string fieldLabel, CancellationToken ct = default)
    {
        var bank = await db.BankAccounts.AsNoTracking()
            .Where(b => b.Id == bankAccountId && b.CompanyId == companyId && !b.IsDeleted)
            .Select(b => new BankFacts(b.BankName + " " + b.AccountNumber + " (" + b.AccountName + ")", b.IsActive, b.LinkedAccountId))
            .FirstOrDefaultAsync(ct);
        var problem = BankProblem(bank, fieldLabel);
        if (problem != null) throw new BusinessRuleException(problem, RuleBankInvalid);
        var glId = bank!.LinkedAccountId!.Value;
        var gl = await LoadAccountAsync(db, companyId, glId, ct);
        var glProblem = LinkedGlProblem(bank, gl, fieldLabel);
        if (glProblem != null) throw new BusinessRuleException(glProblem, RuleBankInvalid);
        return glId;
    }

    /// <summary>ช่องทางเงินจากหน้าจอ: บัญชีธนาคาร (→ ผังที่ผูก) <b>หรือ</b> ผังเงินตรง (เงินสด/กระเป๋าเงิน/เช็คในมือ/เงินกู้กรรมการ) ·
    /// ไม่ส่งมาเลย = null (ผู้เรียกตัดสินเองว่าต้องมีไหม) · ส่งมาทั้งคู่ = ปฏิเสธ</summary>
    public static async Task<Guid?> ResolveMoneyChannelAsync(AccountingDbContext db, Guid companyId,
        Guid? bankAccountId, Guid? moneyAccountId, string fieldLabel, CancellationToken ct = default)
    {
        var ambiguous = ChannelProblem(bankAccountId, moneyAccountId, fieldLabel);
        if (ambiguous != null) throw new BusinessRuleException(ambiguous, RuleChannelAmbiguous);
        if (IsSet(bankAccountId))
            return await ResolveBankGlAsync(db, companyId, bankAccountId!.Value, fieldLabel, ct);
        if (IsSet(moneyAccountId))
        {
            await RequirePostingAccountAsync(db, companyId, moneyAccountId!.Value, fieldLabel, MoneyTypes, ct);
            return moneyAccountId!.Value;
        }
        return null;
    }

    /// <summary>บัญชีธนาคารต้องเป็นของบริษัทนี้และเปิดใช้งาน (ไม่สนผังที่ผูก — เช่นสมุดเช็คที่ขยับแค่ยอดธนาคาร)</summary>
    public static async Task RequireActiveBankAsync(AccountingDbContext db, Guid companyId, Guid bankAccountId,
        string fieldLabel, CancellationToken ct = default)
    {
        var bank = await db.BankAccounts.AsNoTracking()
            .Where(b => b.Id == bankAccountId && b.CompanyId == companyId && !b.IsDeleted)
            .Select(b => new BankFacts(b.BankName + " " + b.AccountNumber + " (" + b.AccountName + ")", b.IsActive, b.LinkedAccountId))
            .FirstOrDefaultAsync(ct);
        var problem = BankProblem(bank, fieldLabel, requireLinked: false);
        if (problem != null) throw new BusinessRuleException(problem, RuleBankInvalid);
    }
}
