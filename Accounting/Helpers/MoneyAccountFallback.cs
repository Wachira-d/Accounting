using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ชนิดของ "ที่อยู่ของเงิน" ตามวิธีรับเงิน — ใช้เลือกผังบัญชีขาเงินเมื่อไม่มีบัญชีที่ผู้ใช้ผูกไว้</summary>
public enum MoneyAccountKind
{
    /// <summary>เงินสด (ลิ้นชัก/มือ)</summary>
    Cash = 1,
    /// <summary>เงินฝากธนาคาร — ผังขึ้นกับว่าเข้าบัญชีธนาคาร<b>ไหน</b> ⇒ ไม่มีรหัสมาตรฐานรหัสเดียว ต้องผูก BankAccount</summary>
    BankDeposit = 2,
    /// <summary>กระเป๋าเงินดิจิทัลของกิจการ</summary>
    DigitalWallet = 3,
    /// <summary>เช็คที่รับมาแต่ยังไม่นำฝาก</summary>
    ChequeOnHand = 4,
    /// <summary>บัตรเครดิต/เดบิตผ่านเครื่องรับบัตร — ผู้ให้บริการรับบัตรถือเงินไว้แล้วโอน T+n หลังหัก MDR
    /// ⇒ ลูกหนี้ผู้ให้บริการรับชำระเงิน (ไม่ใช่ลูกหนี้การค้า และไม่ใช่ธนาคาร)</summary>
    CardAcquirerClearing = 5,
}

/// <summary>ผลการเลือกบัญชีธนาคารเมื่อผู้ใช้ไม่ได้ระบุ</summary>
public enum BankAccountPickOutcome
{
    /// <summary>มีบัญชีธนาคารที่ผูกผังไว้บัญชีเดียว — ใช้ได้โดยไม่ต้องเดา</summary>
    Single = 1,
    /// <summary>ไม่มีบัญชีธนาคารที่ผูกผังเลย</summary>
    None = 2,
    /// <summary>มีหลายบัญชี — ระบบเลือกแทนไม่ได้ (เลือกผิด = กระทบยอดรายบัญชีไม่ได้ตลอดไป)</summary>
    Ambiguous = 3,
}

/// <summary>
/// **ผังบัญชีขา "เงิน" เมื่อไม่มีบัญชีที่ผู้ใช้ผูกไว้ — ตัวตั้งตัวเดียวของ POS และ Integration** (รอบ 198 P-1/I-1)
///
/// <para>═══ ที่มา (บั๊กจริง · ทีม S2 รอบ 198) ═══</para>
/// <list type="bullet">
/// <item>POS ใช้ผังสำรอง <c>1011/1012/1131</c> ซึ่ง<b>ไม่มีในผังมาตรฐาน</b> (รหัส 5 หลัก) แล้วตกไปค้นด้วย prefix:
///   QR/โอน/e-Wallet → <c>112…</c> = <b>11200 เงินลงทุนชั่วคราว</b> · บัตร → <c>113…</c> ตัวไหนก็ได้ (ลูกหนี้การค้า/ลูกหนี้อื่น/กรรมการ)</item>
/// <item>Integration รับชำระ banktransfer/promptpay/creditcard → prefix <c>"112"</c> (เงินลงทุนชั่วคราว) ด้วย
///   <c>StartsWith</c> ที่ไม่เรียง · หาไม่เจอ <c>return null</c> เงียบ (เอกสารถูกตัดชำระแต่ไม่มี JE)</item>
/// </list>
///
/// <para>═══ กติกา ═══ รหัสที่คืนเป็น<b>รหัสเต็ม 5 หลักของผังมาตรฐาน</b> (<c>ChartOfAccountTemplates</c>) ไม่ใช่ prefix ·
/// เงินฝากธนาคาร<b>ไม่มีรหัสมาตรฐาน</b> (ขึ้นกับว่าเข้าบัญชีไหน) ⇒ ต้องมาจาก <c>BankAccount.LinkedAccountId</c> — มีบัญชีเดียว
/// ใช้ได้ · ไม่มี/มีหลายบัญชี = ผู้เรียกต้อง<b>ล้มดังพร้อมทางไปต่อ</b> ห้ามเดา · บัตรผ่านเครื่องรับบัตรลง
/// <c>11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน</c> (ผังบัญชีพัก EDC แยกรายเจ้า = คำถามเจ้าของ รอบ 198 S2 §5 ข้อ 7)</para>
///
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class MoneyAccountFallback
{
    /// <summary>11111 เงินสด</summary>
    public const string CashCode = "11111";
    /// <summary>11113 กระเป๋าเงิน Digital</summary>
    public const string DigitalWalletCode = "11113";
    /// <summary>11131 เช็คในมือ</summary>
    public const string ChequeOnHandCode = "11131";
    /// <summary>11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน</summary>
    public const string CardAcquirerClearingCode = "11340";

    /// <summary>ชนิดที่อยู่ของเงินตามวิธีรับเงิน · <c>Other</c> = เงินสด (พฤติกรรมเดิม — ผู้ใช้เลือก "อื่น ๆ" เอง)</summary>
    public static MoneyAccountKind KindOf(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => MoneyAccountKind.Cash,
        PaymentMethod.BankTransfer => MoneyAccountKind.BankDeposit,
        PaymentMethod.PromptPay => MoneyAccountKind.BankDeposit,
        PaymentMethod.DirectDebit => MoneyAccountKind.BankDeposit,
        PaymentMethod.EWallet => MoneyAccountKind.DigitalWallet,
        PaymentMethod.Cheque => MoneyAccountKind.ChequeOnHand,
        PaymentMethod.CreditCard => MoneyAccountKind.CardAcquirerClearing,
        _ => MoneyAccountKind.Cash,
    };

    /// <summary>รหัสผังมาตรฐานของชนิดนั้น · <c>null</c> = ไม่มีรหัสมาตรฐาน (เงินฝากธนาคาร — ต้องผูก BankAccount)</summary>
    public static string? StandardCode(MoneyAccountKind kind) => kind switch
    {
        MoneyAccountKind.Cash => CashCode,
        MoneyAccountKind.DigitalWallet => DigitalWalletCode,
        MoneyAccountKind.ChequeOnHand => ChequeOnHandCode,
        MoneyAccountKind.CardAcquirerClearing => CardAcquirerClearingCode,
        _ => null,
    };

    /// <summary>เลือกบัญชีธนาคารจากรายการ GL ของบัญชีธนาคารที่ผูกผังไว้ (active) — ไม่เดาเมื่อมีหลายบัญชี</summary>
    public static BankAccountPickOutcome PickBank(IReadOnlyCollection<Guid> linkedBankGlIds, out Guid? picked)
    {
        var distinct = linkedBankGlIds.Distinct().ToList();
        picked = distinct.Count == 1 ? distinct[0] : null;
        return distinct.Count switch
        {
            0 => BankAccountPickOutcome.None,
            1 => BankAccountPickOutcome.Single,
            _ => BankAccountPickOutcome.Ambiguous,
        };
    }

    /// <summary>คำเตือน<b>ล่วงหน้า</b>ของเครื่อง POS (รอบ 200 ทีม G · review198-E ข้อ E-3) — <c>null</c> = บิลโอน/พร้อมเพย์ของเครื่องนี้ลงบัญชีได้
    ///
    /// <para>═══ ที่มา ═══ รอบ 198 P-1 เปลี่ยน "เดา prefix 112" เป็น "ล้มดัง" เมื่อบริษัทมีบัญชีธนาคารที่ผูกผัง 0 หรือ ≥ 2 บัญชีและเครื่องไม่ได้ปัก ·
    /// ถูกทิศ แต่ผู้ใช้เจอครั้งแรก<b>ตอนปิดบิล</b> (รวมบิล offline ที่ sync เข้ามา — เงินรับไปแล้ว) · และข้อความชี้ไป "ตั้งค่าเครื่อง → บัญชีธนาคาร"
    /// ซึ่ง<b>ไม่มีช่องนั้นบนหน้าจอ</b> (ต่อสายในรอบนี้) ⇒ ตัดสินจากกติกาเดียวกับ <see cref="PickBank"/> แล้วให้หน้าตั้งค่า/หัว POS แสดงก่อนขาย</para></summary>
    public static string? TerminalBankWarning(bool terminalBankPinned, BankAccountPickOutcome companyBanks)
    {
        if (terminalBankPinned || companyBanks == BankAccountPickOutcome.Single) return null;
        return companyBanks == BankAccountPickOutcome.Ambiguous
            ? "บริษัทมีบัญชีธนาคารที่ผูกผังบัญชีหลายบัญชี แต่เครื่องนี้ยังไม่ได้เลือก \"บัญชีธนาคารรับเงิน\" — บิลที่รับโอน/พร้อมเพย์/หักบัญชี "
              + "(รวมบิล offline ที่ sync เข้ามา) จะปิดไม่ได้ · กด \"⚙️ ตั้งค่าเครื่อง\" แล้วเลือกบัญชีธนาคารที่เงินของเครื่องนี้เข้า"
            : "ยังไม่มีบัญชีธนาคารที่ผูกผังบัญชี — บิลที่รับโอน/พร้อมเพย์/หักบัญชีจะปิดไม่ได้ · เพิ่มบัญชีธนาคารและเลือกผังบัญชีที่หน้า "
              + "\"บัญชีธนาคาร\" แล้วเลือกเป็นบัญชีรับเงินของเครื่องนี้";
    }

    /// <summary>ข้อความล้มดังเมื่อหาบัญชีธนาคารให้ไม่ได้ — บอกทางไปต่อตามช่องทาง</summary>
    public static string BankNotResolvedMessage(BankAccountPickOutcome outcome, string channelHint)
        => outcome == BankAccountPickOutcome.Ambiguous
            ? $"บริษัทมีบัญชีธนาคารหลายบัญชี ระบบเลือกแทนไม่ได้ว่าเงินเข้าบัญชีไหน (เลือกผิด = กระทบยอดรายบัญชีไม่ได้) — {channelHint}"
            : $"ยังไม่มีบัญชีธนาคารที่ผูกผังบัญชีไว้ — เพิ่มบัญชีธนาคารแล้วเลือกผังบัญชีที่หน้า \"บัญชีธนาคาร\" ก่อน หรือ {channelHint}";
}
