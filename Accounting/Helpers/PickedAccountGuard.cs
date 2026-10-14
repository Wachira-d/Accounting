using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
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
/// <para>═══ กติกา ═══ ผังบัญชีที่ลงรายการได้ = <b>ตัวตัดสินเดียว <see cref="PostingAccountRule.Check"/></b> (ของบริษัทนี้ · ไม่ถูกลบ · เปิดใช้งาน ·
/// ไม่มีบัญชีย่อย) + ชนิดตรงกับช่อง (ถ้าช่องกำหนด — ตัวกรองเพิ่มเติมเดียวของที่นี่) · บัญชีธนาคาร → ผังที่ผูก = <see cref="MoneyAccountDirectory.TryResolveBankGlAsync"/> ·
/// ผังเงินตรง = <see cref="MoneyAccountDirectory.SourceProblem"/> · ทุกข้อความบอก "ช่องไหน · แก้ที่ไหน" เป็นไทย
/// (คำตัดสินข้อ 150 — บล็อกเพราะความถูกต้องของ GL/การแยกบริษัท ต้องมีทางไปต่อ)</para>
/// <para>ฝ่ายค้านรอบ 2 P1-1: เดิมตัดสิน "บัญชีคุม" จาก Level &lt; 4 ขณะที่ PostingAccountRule ใช้ "มีบัญชีย่อย" ⇒ ผังเงินสด 1010 ระดับ 2 ของผังที่บริษัทตั้งเอง
/// ถูกเสนอในรายการช่องทางเงินแต่เงินสดย่อย/สินเชื่อ/ใบเบิก/เงินทดรองปฏิเสธ · 11122 ที่มีบัญชีย่อยธนาคารผ่านที่นี่แต่ถูกบล็อกที่อื่น ⇒ สองเจ้าของ</para>
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
    /// <param name="IsGroup">มีบัญชีย่อย (บัญชีคุม) — ตัวตัดสิน <see cref="PostingAccountRule"/> ไม่ดู Level</param>
    public sealed record AccountFacts(string Code, string Name, bool IsActive, int Level, AccountType Type, bool IsGroup = false);

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
        return AccountProblemMessage(a, PostingAccountRule.Check(ToPostingFacts(a)), fieldLabel, allowedTypes);
    }

    /// <summary>แปลงเป็นข้อเท็จจริงของตัวตัดสินกลาง (<see cref="PostingAccountRule"/>)</summary>
    private static PostingAccountFacts? ToPostingFacts(AccountFacts? a)
        => a == null ? null : new PostingAccountFacts(Guid.Empty, a.Code, a.Name, a.IsActive, a.IsGroup);

    /// <summary>ข้อความของผลตัดสิน <paramref name="problem"/> (ตัดสินที่ <see cref="PostingAccountRule"/>/<see cref="MoneyAccountDirectory.SourceProblem"/>) + ตัวกรองชนิด</summary>
    private static string? AccountProblemMessage(AccountFacts? a, PostingAccountProblem problem, string fieldLabel, IReadOnlyCollection<AccountType>? allowedTypes)
    {
        if (problem == PostingAccountProblem.NotFound || a == null)
            return $"ช่อง “{fieldLabel}”: ไม่พบผังบัญชีที่เลือกในบริษัทนี้ (อาจถูกลบไปแล้ว หรือหน้าจอค้างข้อมูลเก่า) — รีเฟรชหน้าแล้วเลือกผังบัญชีใหม่";
        if (problem == PostingAccountProblem.Inactive)
            return $"ช่อง “{fieldLabel}”: ผังบัญชี {a.Code} {a.Name} ถูกปิดการใช้งาน — เลือกผังอื่น หรือเปิดใช้งานผังนี้ที่หน้า “ผังบัญชี”";
        if (problem == PostingAccountProblem.Group)
            return $"ช่อง “{fieldLabel}”: {a.Code} {a.Name} เป็นบัญชีคุม (มีบัญชีย่อย) ลงรายการไม่ได้ — เลือกบัญชีย่อยใต้บัญชีนี้แทน";
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
            : gl.IsGroup ? $"ผัง {gl.Code} ที่ผูกไว้เป็นบัญชีคุม"
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

    /// <summary>
    /// ฝ่ายค้านรอบ 2 P2(c) — คำขอรูปเดิมของใบเบิก/เงินทดรอง ที่ส่ง<b>ทั้ง</b> BankAccountId และ PaymentAccountId: ก่อนทีม F1 ผังตรงชนะ
    /// (ลำดับเดียวกับ <see cref="DocumentMoneyChannel"/>.Pick) — F1 เปลี่ยนเป็นปฏิเสธ ⇒ ระบบเชื่อมต่อที่ส่งสองช่องล้มทันที ·
    /// คืนลำดับเดิม (ผังตรงชนะ · ไม่ขยับยอดบัญชีธนาคาร) แทนการปฏิเสธ (เข้ากันได้ย้อนหลัง)
    /// </summary>
    public static (Guid? BankAccountId, Guid? MoneyAccountId) PreferDirectAccount(Guid? bankAccountId, Guid? moneyAccountId)
        => IsSet(moneyAccountId) ? ((Guid?)null, moneyAccountId) : (IsSet(bankAccountId) ? bankAccountId : (Guid?)null, (Guid?)null);

    /// <summary>ข้อความเลิกใช้ของรูปคำขอเดิมของสินเชื่อ (ผังบัญชีในช่อง bankAccountId)</summary>
    public const string LegacyLoanBankFieldNotice =
        "รับรหัสผังบัญชีในช่อง “bankAccountId” ไว้ให้ชั่วคราว (รูปคำขอเดิม — จะเลิกรับในรอบถัดไป) — ระบบเชื่อมต่อควรส่งรหัสผังเงินสด/ธนาคารในช่อง “moneyAccountId” "
        + "หรือรหัสบัญชีธนาคาร (หน้า “บัญชีธนาคาร”) ในช่อง “bankAccountId”";

    /// <summary>
    /// ฝ่ายค้านรอบ 2 P2(c) — สินเชื่อ: สัญญาเดิมของ <c>Loan.BankAccountId</c> รับ<b>รหัสผังบัญชี</b> · ทีม F1 เปลี่ยนเป็นรหัสบัญชีธนาคาร ⇒ ผู้เรียกเดิมล้ม ·
    /// ช่วงเลิกใช้หนึ่งรอบ: รหัสในช่อง bankAccountId ที่ไม่ใช่บัญชีธนาคารแต่เป็นผังบัญชีของบริษัทนี้ (และไม่ได้ส่ง moneyAccountId) ⇒ ถือเป็นผังเงินตรง +
    /// คืนข้อความเลิกใช้ · นอกนั้นคืนค่าเดิม (ตัวตรวจปกติตัดสิน)
    /// </summary>
    public static async Task<(Guid? BankAccountId, Guid? MoneyAccountId, string? Notice)> NormalizeLegacyLoanBankFieldAsync(
        AccountingDbContext db, Guid companyId, Guid? bankAccountId, Guid? moneyAccountId, CancellationToken ct = default)
    {
        if (!IsSet(bankAccountId) || IsSet(moneyAccountId)) return (bankAccountId, moneyAccountId, (string?)null);
        var id = bankAccountId!.Value;
        if (await db.BankAccounts.AsNoTracking().AnyAsync(b => b.Id == id && b.CompanyId == companyId && !b.IsDeleted, ct))
            return (bankAccountId, moneyAccountId, (string?)null);
        var isGl = await db.ChartOfAccounts.AsNoTracking().AnyAsync(a => a.Id == id && a.CompanyId == companyId && !a.IsDeleted, ct);
        return isGl ? ((Guid?)null, (Guid?)id, (string?)LegacyLoanBankFieldNotice) : (bankAccountId, moneyAccountId, (string?)null);
    }

    // ─────────────────────────── ตัวอ่านฐาน ───────────────────────────

    /// <summary>อ่านข้อเท็จจริงของผังบัญชี (ของบริษัทนี้ · ไม่ถูกลบ) — null = ไม่พบ</summary>
    private static Task<AccountFacts?> LoadAccountAsync(AccountingDbContext db, Guid companyId, Guid accountId, CancellationToken ct = default)
        => db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new AccountFacts(a.AccountCode, a.AccountName, a.IsActive, a.Level, a.AccountType,
                db.ChartOfAccounts.Any(c => c.CompanyId == companyId && !c.IsDeleted && c.ParentAccountId == a.Id)))
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
    /// "ยังไม่ผูก" / "ผังที่ผูกใช้ไม่ได้" เป็นคนละข้อความ
    /// <para>ฝ่ายค้านรอบ 2 P1-1: ตัดสินที่ <see cref="MoneyAccountDirectory.TryResolveBankGlAsync"/> ตัวเดียว (ผังที่ธนาคารผูกยอมเป็นบัญชีคุมได้ ·
    /// การเชื่อมเดิมของผู้ใช้) + ตัวกรองชนิด "ที่อยู่ของเงิน" ของที่นี่</para></summary>
    public static async Task<Guid> ResolveBankGlAsync(AccountingDbContext db, Guid companyId, Guid bankAccountId,
        string fieldLabel, CancellationToken ct = default)
    {
        var r = await db.TryResolveBankGlAsync(companyId, bankAccountId, ct);
        if (r.GlAccountId is not Guid glId)
            throw new BusinessRuleException($"ช่อง “{fieldLabel}”: {r.Message}", RuleBankInvalid);
        var gl = await LoadAccountAsync(db, companyId, glId, ct);
        if (gl != null && !MoneyTypes.Contains(gl.Type))
            throw new BusinessRuleException(LinkedGlProblem(new BankFacts(r.BankLabel ?? "ที่เลือก", true, glId), gl with { IsGroup = false }, fieldLabel)!,
                RuleBankInvalid);
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
            // ฝ่ายค้านรอบ 2 P1-1: ผังเงินตรงตัดสินด้วย MoneyAccountDirectory.SourceProblem (= PostingAccountRule + ผังที่ธนาคารผูกยอมเป็นบัญชีคุม)
            // ตัวเดียวกับรายการช่องทางเงินที่หน้าเว็บเสนอ — ผังที่เสนอแล้วต้องเลือกได้
            var id = moneyAccountId!.Value;
            var ctx = await db.LoadMoneyContextAsync(companyId, ct);
            var facts = await LoadAccountAsync(db, companyId, id, ct);
            var problem = ctx.SourceProblem(facts == null ? null
                : new PostingAccountFacts(id, facts.Code, facts.Name, facts.IsActive, facts.IsGroup));
            var message = AccountProblemMessage(facts, problem, fieldLabel, MoneyTypes);
            if (message != null) throw new BusinessRuleException(message, RuleAccountInvalid);
            return id;
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
