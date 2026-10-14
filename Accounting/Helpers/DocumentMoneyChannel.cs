using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ช่องที่ตัดสินแหล่งเงินของเอกสาร/การชำระ</summary>
public enum MoneyChannelKind
{
    /// <summary>ไม่ได้เลือกแหล่งเงิน ⇒ ผู้เรียกใช้เงินสดตั้งต้น</summary>
    None = 0,
    /// <summary>ผังบัญชีตรง (<c>PaymentAccountId</c> / <c>OverridePaymentAccountId</c>) — เงินทดรองกรรมการ เงินสดย่อย บัญชีพัก</summary>
    GlAccount = 1,
    /// <summary>บัญชีธนาคาร (<c>BankAccountId</c>) ⇒ ลงผังที่บัญชีธนาคารผูกไว้ (<c>LinkedAccountId</c>)</summary>
    BankAccount = 2,
}

/// <summary>ตรวจตอนไหน — ตอนบันทึกเอกสารเข้มกว่าตอนลงบัญชี (บัญชีธนาคารที่ปิดใช้ทีหลัง ไม่ทำให้ใบที่บันทึกไว้แล้วอนุมัติไม่ได้)</summary>
public enum MoneyChannelCheck
{
    /// <summary>ผู้ใช้เพิ่งเลือก/เปลี่ยนแหล่งเงิน — บัญชีธนาคารต้องเปิดใช้อยู่ด้วย</summary>
    Save = 1,
    /// <summary>กำลังลงบัญชี — ต้องได้ผังที่ลงรายการได้จริงเท่านั้น</summary>
    Post = 2,
}

/// <summary>ข้อเท็จจริงของผังบัญชีที่ถูกอ้าง (โหลดจากฐานด้วย CompanyId) · <c>null</c> ทั้งก้อน = ไม่มีในบริษัทนี้</summary>
public sealed record MoneyGlFacts(Guid Id, string Code, string Name, bool IsActive, int Level);

/// <summary>ข้อเท็จจริงของบัญชีธนาคารที่ถูกอ้าง · <c>Linked</c> = ผังที่ผูกไว้ (null = ไม่ได้ผูก หรือผังนั้นไม่มีในบริษัทนี้แล้ว)</summary>
public sealed record MoneyBankFacts(Guid Id, string DisplayName, bool IsActive, Guid? LinkedAccountId, MoneyGlFacts? Linked);

/// <summary>ผลตัดสิน · <c>Problem</c> = ข้อความไทยถึงผู้ใช้ (บอกช่อง + ที่แก้) · <c>GlAccountId</c> มีค่าเมื่อผ่านและมีการเลือกแหล่งเงิน</summary>
public sealed record MoneyChannelDecision(MoneyChannelKind Kind, Guid? GlAccountId, string? Problem)
{
    public bool Ok => Problem is null;
}

/// <summary>ช่องทางเงินของการชำระหนึ่งรายการ — <c>BankAccountId</c> = บัญชีธนาคารที่ยอดคงเหลือขยับ · <c>PaymentAccountId</c> = ผังตรง</summary>
public sealed record PaymentMoneyChannel(Guid? BankAccountId, Guid? PaymentAccountId);

/// <summary>
/// **แหล่งเงินของเอกสาร/การชำระ → ผังบัญชี — ตัวตัดสินตัวเดียว** (ผลตรวจ 2026-10-14 PAYMENT_CHANNEL_AUDIT PC#4/PC#5/PC#15/PC#21 · ID#7)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ ลำดับ "PaymentAccountId → ธนาคาร.LinkedAccount → 111" เคยถูกเขียนไว้ 4 ที่ (ลงบัญชีตอนอนุมัติ ·
/// JE การชำระ · ตัวหาผังของเงินส่วนเกิน · พรีวิวการลงบัญชี — ตัวหลังสุดสลับลำดับเป็นธนาคารก่อน) และ<b>ทุกที่ตกไปเงินสดเงียบ ๆ</b>
/// เมื่อบัญชีธนาคารที่เลือกยังไม่ผูกผัง/อ้างรหัสผิดตาราง/เป็นของบริษัทอื่น — เงินโอนเข้าธนาคารจึงไปโผล่ในเงินสด ขณะที่ยอดคงเหลือของ
/// ธนาคารยังขยับ (สมุดธนาคารกับ GL แยกทางกันโดยไม่มีอะไรฟ้อง) · ผังเงินสดไม่มี (ผังกำหนดเอง) = บันทึกรับ/จ่ายโดย<b>ไม่มี JE เลย</b></para>
///
/// <para>═══ กติกา ═══
/// <list type="bullet">
/// <item><b>ลำดับ</b> (<see cref="Pick"/>): ผังตรงชนะบัญชีธนาคาร — ทุกจุด (ลงบัญชี · พรีวิว · ฟอร์มแก้ไข ผ่าน <see cref="ChannelValue"/>) อ่านจากที่นี่</item>
/// <item>เลือกแล้วแต่ใช้ไม่ได้ (ไม่มีในบริษัทนี้ · รหัสผิดตาราง · ปิดใช้ · บัญชีกลุ่ม · ธนาคารยังไม่ผูกผัง/ผังที่ผูกใช้ไม่ได้) ⇒ <b>ปฏิเสธ
/// พร้อมทางไปต่อ</b> ไม่ตกไปเงินสด (คำตัดสินข้อ 150: GL integrity)</item>
/// <item>ไม่ได้เลือกเลย ⇒ <see cref="MoneyChannelKind.None"/> — ผู้เรียกใช้เงินสดตั้งต้นของตัวเอง และต้องล้มดังเมื่อหาไม่เจอ</item>
/// <item>บัญชีธนาคารที่ปิดใช้: ห้ามเลือกใหม่ (<see cref="MoneyChannelCheck.Save"/>) · ใบที่เลือกไว้ก่อนปิดยังลงบัญชีได้ถ้าผังที่ผูกยังใช้ได้</item>
/// <item>การชำระสืบทอดแหล่งเงินของเอกสาร (<see cref="ForPayment"/>) — เดิมสืบทอดแต่ธนาคาร ⇒ ใบที่จ่ายจากเงินทดรองกรรมการไปลงเงินสด</item>
/// </list></para>
/// </summary>
public static class DocumentMoneyChannel
{
    /// <summary>รหัสกฎสำหรับ audit/ข้อความ</summary>
    public const string RuleCode = "MONEY-CHANNEL";

    /// <summary>ที่แก้แหล่งเงินบนเอกสาร — ใช้ในข้อความถึงผู้ใช้</summary>
    public const string DocumentFixHint = "เปิดเอกสาร → ช่อง “แหล่งเงิน” แล้วเลือกใหม่";

    /// <summary>ที่แก้แหล่งเงินของการรับ/จ่ายชำระ — ใช้ในข้อความถึงผู้ใช้</summary>
    public const string PaymentFixHint = "เลือก “บัญชีรับ-จ่ายเงิน” ในหน้าบันทึกรับ/จ่ายชำระใหม่ (หรือแก้ช่อง “แหล่งเงิน” ที่เอกสาร)";

    private static Guid? Norm(Guid? id) => id is { } g && g != Guid.Empty ? g : null;

    /// <summary>ลำดับเดียวของระบบ: ผังตรง &gt; บัญชีธนาคาร &gt; ไม่ได้เลือก</summary>
    internal static MoneyChannelKind Pick(Guid? bankAccountId, Guid? paymentAccountId)
        => Norm(paymentAccountId).HasValue ? MoneyChannelKind.GlAccount
            : Norm(bankAccountId).HasValue ? MoneyChannelKind.BankAccount
            : MoneyChannelKind.None;

    /// <summary>ค่าของช่อง "แหล่งเงิน" บนฟอร์ม (<c>account:&lt;id&gt;</c> / <c>bank:&lt;id&gt;</c> / null) — เซิร์ฟเวอร์ตัดสินลำดับ หน้าเว็บแสดงตาม
    /// (เดิมฟอร์มแก้ไขเลือกธนาคารก่อน ขณะที่การลงบัญชีเลือกผังตรงก่อน ⇒ ใบที่มีทั้งสองค่า บันทึกซ้ำแล้วเปลี่ยนผังเงินเงียบ ๆ)</summary>
    public static string? ChannelValue(Guid? bankAccountId, Guid? paymentAccountId) => Pick(bankAccountId, paymentAccountId) switch
    {
        MoneyChannelKind.GlAccount => "account:" + Norm(paymentAccountId)!.Value,
        MoneyChannelKind.BankAccount => "bank:" + Norm(bankAccountId)!.Value,
        _ => null,
    };

    /// <summary>
    /// ช่องทางเงินของการชำระ — ผู้ใช้เลือกในหน้าบันทึกชำระชนะ · ไม่เลือก = สืบทอดแหล่งเงินของเอกสาร (เฉพาะตัวที่ชนะตามลำดับ)
    /// <list type="bullet">
    /// <item>เลือก "จ่าย/รับจากบัญชีอื่น" (ผังตรง) ⇒ <b>ไม่ขยับยอดบัญชีธนาคารของเอกสาร</b> (PC#21 — เงินไม่ได้ผ่านธนาคารนั้น)</item>
    /// <item>เลือกทั้งสองช่องพร้อมกัน ⇒ ไม่รู้ว่าเงินผ่านทางไหน — ผู้เรียกต้องปฏิเสธ (<see cref="BothOverridesMessage"/>)</item>
    /// </list>
    /// </summary>
    public static PaymentMoneyChannel ForPayment(Guid? overrideBankAccountId, Guid? overridePaymentAccountId,
        Guid? docBankAccountId, Guid? docPaymentAccountId)
    {
        var ob = Norm(overrideBankAccountId);
        var op = Norm(overridePaymentAccountId);
        if (op.HasValue || ob.HasValue) return new PaymentMoneyChannel(ob, op);
        return Pick(docBankAccountId, docPaymentAccountId) switch
        {
            MoneyChannelKind.GlAccount => new PaymentMoneyChannel(null, Norm(docPaymentAccountId)),
            MoneyChannelKind.BankAccount => new PaymentMoneyChannel(Norm(docBankAccountId), null),
            _ => new PaymentMoneyChannel(null, null),
        };
    }

    /// <summary>ผู้ใช้เลือกทั้งบัญชีธนาคารและผังตรงของการชำระเดียวกัน</summary>
    public static bool HasBothOverrides(Guid? overrideBankAccountId, Guid? overridePaymentAccountId)
        => Norm(overrideBankAccountId).HasValue && Norm(overridePaymentAccountId).HasValue;

    /// <summary>ข้อความเมื่อเลือกสองทางพร้อมกัน</summary>
    public const string BothOverridesMessage =
        "เลือกได้ทางเดียว: “บัญชีรับ-จ่ายเงิน” (บัญชีธนาคาร) หรือ “จ่าย/รับจากบัญชีอื่น” (ผังบัญชี) — "
        + "ถ้าเงินผ่านธนาคาร ให้เลือกช่องแรกและล้างช่องที่สอง แล้วบันทึกใหม่";

    /// <summary>
    /// **ตัวตัดสิน (pure)** — <paramref name="gl"/>/<paramref name="bank"/> = ข้อเท็จจริงที่โหลดด้วย CompanyId (null = ไม่มีในบริษัทนี้) ·
    /// <paramref name="glIdIsBank"/>/<paramref name="bankIdIsGl"/> = รหัสที่ส่งมาเป็นของอีกตาราง (สลับช่อง) ·
    /// <paramref name="fixHint"/> = ที่แก้แหล่งเงินของผู้เรียก (เอกสาร/การชำระ)
    /// </summary>
    internal static MoneyChannelDecision Decide(MoneyChannelKind kind, MoneyGlFacts? gl, bool glIdIsBank,
        MoneyBankFacts? bank, bool bankIdIsGl, MoneyChannelCheck check, string fixHint)
    {
        switch (kind)
        {
            case MoneyChannelKind.None:
                return new MoneyChannelDecision(kind, null, null);
            case MoneyChannelKind.GlAccount:
                if (gl is null)
                    return Fail(kind, glIdIsBank
                        ? "ช่อง “แหล่งเงิน (ผังบัญชี)” ได้รหัสบัญชีธนาคารมาแทนรหัสผังบัญชี — บัญชีธนาคารต้องเลือกจากกลุ่ม “บัญชีธนาคาร” · " + fixHint
                        : "ช่อง “แหล่งเงิน (ผังบัญชี)” อ้างผังบัญชีที่ไม่มีในบริษัทนี้ (อาจถูกลบไปแล้ว) — " + fixHint);
                if (!gl.IsActive)
                    return Fail(kind, $"ผังบัญชี {gl.Code} {gl.Name} ที่เลือกเป็นแหล่งเงินถูกปิดใช้งาน — "
                        + "เปิดใช้ที่หน้า “ผังบัญชี” หรือ " + fixHint);
                if (gl.Level < 4)
                    return Fail(kind, $"ผังบัญชี {gl.Code} {gl.Name} เป็นบัญชีกลุ่ม ลงรายการไม่ได้ — เลือกบัญชีย่อยแทน · " + fixHint);
                return new MoneyChannelDecision(kind, gl.Id, null);
            case MoneyChannelKind.BankAccount:
                if (bank is null)
                    return Fail(kind, bankIdIsGl
                        ? "ช่อง “แหล่งเงิน (บัญชีธนาคาร)” ได้รหัสผังบัญชีมาแทนรหัสบัญชีธนาคาร — ผังบัญชีต้องเลือกจากกลุ่ม “เงินสด / อื่นๆ” · " + fixHint
                        : "ช่อง “แหล่งเงิน (บัญชีธนาคาร)” อ้างบัญชีธนาคารที่ไม่มีในบริษัทนี้ — " + fixHint);
                if (check == MoneyChannelCheck.Save && !bank.IsActive)
                    return Fail(kind, $"บัญชีธนาคาร “{bank.DisplayName}” ถูกปิดใช้งาน — เปิดใช้ที่หน้า “บัญชีธนาคาร” หรือ " + fixHint);
                if (bank.LinkedAccountId is null)
                    return Fail(kind, $"บัญชีธนาคาร “{bank.DisplayName}” ยังไม่ได้ผูกผังบัญชี ระบบจึงลงบัญชีเงินเข้า/ออกไม่ได้ "
                        + "(ไม่ลงเงินสดแทนให้ — เงินโอนจะไปโผล่ผิดบัญชี) — ไปที่หน้า “บัญชีธนาคาร” → แก้ไขบัญชีนี้ → เลือก “ผังบัญชีที่ผูก” "
                        + "แล้วลองใหม่ หรือ " + fixHint);
                if (bank.Linked is null)
                    return Fail(kind, $"ผังบัญชีที่บัญชีธนาคาร “{bank.DisplayName}” ผูกไว้ไม่มีในบริษัทนี้แล้ว — "
                        + "ไปที่หน้า “บัญชีธนาคาร” → แก้ไขบัญชีนี้ → ผูกผังเงินฝากใหม่ หรือ " + fixHint);
                if (!bank.Linked.IsActive)
                    return Fail(kind, $"ผังบัญชี {bank.Linked.Code} {bank.Linked.Name} ที่บัญชีธนาคาร “{bank.DisplayName}” ผูกไว้ถูกปิดใช้งาน — "
                        + "เปิดใช้ที่หน้า “ผังบัญชี” หรือผูกผังใหม่ที่หน้า “บัญชีธนาคาร”");
                if (bank.Linked.Level < 4)
                    return Fail(kind, $"บัญชีธนาคาร “{bank.DisplayName}” ผูกกับผังกลุ่ม {bank.Linked.Code} ซึ่งลงรายการไม่ได้ — "
                        + "ไปที่หน้า “บัญชีธนาคาร” → แก้ไขบัญชีนี้ → ผูกผังเงินฝากระดับย่อย");
                return new MoneyChannelDecision(kind, bank.Linked.Id, null);
            default:
                return Fail(kind, "แหล่งเงินไม่ถูกต้อง — " + fixHint);
        }
    }

    private static MoneyChannelDecision Fail(MoneyChannelKind kind, string message) => new(kind, null, message);

    /// <summary>โหลดข้อเท็จจริง (ทุก query กรอง CompanyId) แล้วตัดสินด้วย <see cref="Decide"/></summary>
    public static async Task<MoneyChannelDecision> EvaluateAsync(AccountingDbContext db, Guid companyId,
        Guid? bankAccountId, Guid? paymentAccountId, MoneyChannelCheck check, string fixHint, CancellationToken ct = default)
    {
        var kind = Pick(bankAccountId, paymentAccountId);
        MoneyGlFacts? gl = null;
        MoneyBankFacts? bank = null;
        var glIdIsBank = false;
        var bankIdIsGl = false;
        if (kind == MoneyChannelKind.GlAccount)
        {
            var id = Norm(paymentAccountId)!.Value;
            gl = await LoadGlAsync(db, companyId, id, ct);
            if (gl is null)
                glIdIsBank = await db.BankAccounts.AsNoTracking().AnyAsync(b => b.Id == id && b.CompanyId == companyId, ct);
        }
        else if (kind == MoneyChannelKind.BankAccount)
        {
            var id = Norm(bankAccountId)!.Value;
            var b = await db.BankAccounts.AsNoTracking()
                .Where(x => x.Id == id && x.CompanyId == companyId && !x.IsDeleted)
                .Select(x => new { x.Id, x.AccountName, x.BankName, x.AccountNumber, x.IsActive, x.LinkedAccountId })
                .FirstOrDefaultAsync(ct);
            if (b is null)
                bankIdIsGl = await db.ChartOfAccounts.AsNoTracking().AnyAsync(a => a.Id == id && a.CompanyId == companyId, ct);
            else
                bank = new MoneyBankFacts(b.Id, $"{b.AccountName} ({b.BankName} {b.AccountNumber})".Trim(), b.IsActive, b.LinkedAccountId,
                    b.LinkedAccountId is Guid linked ? await LoadGlAsync(db, companyId, linked, ct) : null);
        }
        return Decide(kind, gl, glIdIsBank, bank, bankIdIsGl, check, fixHint);
    }

    /// <summary>ผังบัญชีของแหล่งเงินที่เลือก สำหรับลงบัญชี · null = ไม่ได้เลือก (ผู้เรียกใช้เงินสดตั้งต้น) · ใช้ไม่ได้ = โยนพร้อมทางไปต่อ</summary>
    /// <exception cref="BusinessRuleException">แหล่งเงินที่เลือกใช้ลงบัญชีไม่ได้</exception>
    public static async Task<Guid?> ResolvePostingAccountAsync(AccountingDbContext db, Guid companyId,
        Guid? bankAccountId, Guid? paymentAccountId, string fixHint, CancellationToken ct = default)
    {
        var d = await EvaluateAsync(db, companyId, bankAccountId, paymentAccountId, MoneyChannelCheck.Post, fixHint, ct);
        if (!d.Ok) throw new BusinessRuleException(d.Problem!, RuleCode);
        return d.GlAccountId;
    }

    /// <summary>ข้อความเมื่อไม่ได้เลือกแหล่งเงินและหาผังเงินสดตั้งต้นไม่เจอ (ผังกำหนดเอง) — เดิมบันทึกรับ/จ่ายโดยไม่มี JE เลย</summary>
    public static string NoDefaultCashMessage(string fixHint) =>
        "ไม่พบผังเงินสด (กลุ่ม 111) ที่ลงรายการได้ในผังบัญชีของบริษัท ระบบจึงลงบัญชีเงินเข้า/ออกไม่ได้ — "
        + "เพิ่มผัง 11111 เงินสด ที่หน้า “ผังบัญชี” หรือ " + fixHint;

    /// <summary>ตรวจหมวดค่าใช้จ่ายระดับเอกสาร (<c>ExpenseCategoryId</c>) — ผังของบริษัทนี้ · เปิดใช้ · ลงรายการได้ · ไม่ใช่รหัสบัญชีธนาคาร ·
    /// null = ผ่าน (ไม่ได้เลือก)</summary>
    public static async Task<string?> ExpenseCategoryProblemAsync(AccountingDbContext db, Guid companyId, Guid? expenseCategoryId,
        CancellationToken ct = default)
    {
        if (Norm(expenseCategoryId) is not Guid id) return null;
        var gl = await LoadGlAsync(db, companyId, id, ct);
        return ExpenseCategoryProblem(gl,
            gl is null && await db.BankAccounts.AsNoTracking().AnyAsync(b => b.Id == id && b.CompanyId == companyId, ct));
    }

    /// <summary>ตัวตัดสิน (pure) ของหมวดค่าใช้จ่าย</summary>
    internal static string? ExpenseCategoryProblem(MoneyGlFacts? gl, bool idIsBank)
    {
        const string fix = "เปิดเอกสาร → ช่อง “หมวดค่าใช้จ่าย” แล้วเลือกใหม่";
        if (gl is null)
            return idIsBank
                ? "ช่อง “หมวดค่าใช้จ่าย” ได้รหัสบัญชีธนาคารมาแทนรหัสผังบัญชี — " + fix
                : "ช่อง “หมวดค่าใช้จ่าย” อ้างผังบัญชีที่ไม่มีในบริษัทนี้ — " + fix;
        if (!gl.IsActive)
            return $"หมวดค่าใช้จ่าย {gl.Code} {gl.Name} ถูกปิดใช้งาน — เปิดใช้ที่หน้า “ผังบัญชี” หรือ " + fix;
        if (gl.Level < 4)
            return $"หมวดค่าใช้จ่าย {gl.Code} {gl.Name} เป็นบัญชีกลุ่ม ลงรายการไม่ได้ — " + fix;
        return null;
    }

    /// <summary>
    /// ค่าช่อง "แหล่งเงิน" ของฟอร์มจาก<b>ผังบัญชี</b>ที่ตั้งเป็นค่าตั้งต้น (เทมเพลตเอกสารเก็บรหัสผัง — PC#14 · ID#4) ·
    /// ผังที่บัญชีธนาคารที่เปิดใช้<b>บัญชีเดียว</b>ผูกไว้ ⇒ <c>bank:&lt;id&gt;</c> (การรับ/จ่ายจะขยับยอดธนาคารนั้นด้วย) · นอกนั้น ⇒ <c>account:&lt;id&gt;</c> ·
    /// ผังที่ใช้ไม่ได้แล้ว (บริษัทอื่น/ปิดใช้/บัญชีกลุ่ม) ⇒ null (ไม่เติมค่าที่ลงบัญชีไม่ได้) — ผู้เรียกบอกผู้ใช้เอง
    /// </summary>
    public static async Task<string?> ChannelValueForGlAsync(AccountingDbContext db, Guid companyId, Guid? glAccountId,
        CancellationToken ct = default)
    {
        if (Norm(glAccountId) is not Guid id) return null;
        var gl = await LoadGlAsync(db, companyId, id, ct);
        if (gl is null || !gl.IsActive || gl.Level < 4) return null;
        var banks = await db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive && b.LinkedAccountId == id)
            .Select(b => b.Id).Take(2).ToListAsync(ct);
        return banks.Count == 1 ? "bank:" + banks[0] : "account:" + id;
    }

    private static async Task<MoneyGlFacts?> LoadGlAsync(AccountingDbContext db, Guid companyId, Guid id, CancellationToken ct)
        => await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.Id == id && a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new MoneyGlFacts(a.Id, a.AccountCode, a.AccountName, a.IsActive, a.Level))
            .FirstOrDefaultAsync(ct);
}
