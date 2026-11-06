using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 246 QR — <b>ตัวตัดสินเดียวของ "QR ชำระเงินบนเอกสาร"</b> (คำตัดสิน 246-QR-1..246-QR-8 · DOCUMENT_FLOW §2.12i)
///
/// <para>═══ ที่มา ═══ เจ้าของ (2026-11-06): "บัญชีแต่ละบัญชีรองรับอัปโหลด QR การจ่ายเงิน · เอกสารไหนแสดงเลขบัญชี ก็แนบรูป QR ไปด้วย ·
/// บริษัทที่เปิด payment gateway ก็สร้าง QR ของหน้าชำระแนบไปด้วยถ้าเลือกช่องทางนี้" · เดิม: บัญชีธนาคาร (<c>BankAccount</c>) ไม่มีช่อง QR เลย ·
/// เทมเพลตมีธง <c>ShowQrCode</c>/<c>PromptPayId</c> แต่<b>ไม่มีตัวเรนเดอร์ตัวไหนอ่าน</b> (ค่าตั้งไม่มีผล)</para>
///
/// <para>═══ ลำดับความสำคัญของ QR บัญชี (246-QR-2) ═══ ต่อบัญชี: <see cref="SourceAuto"/> = QR พร้อมเพย์ที่ระบบสร้าง (ฝังยอดของใบได้) ถ้าตั้ง
/// เลขพร้อมเพย์ที่ถูกต้อง ไม่งั้นรูปที่อัปโหลด · <see cref="SourcePromptPay"/> = พร้อมเพย์ก่อน (ไม่มี ⇒ รูป) · <see cref="SourceUpload"/> = รูปก่อน (ไม่มี ⇒ พร้อมเพย์) —
/// ทางเลือกของผู้ใช้เป็น "ก่อน" ไม่ใช่ "เท่านั้น": ตั้งไว้แล้วแต่อีกอย่างหายไป ไม่ทำให้กระดาษว่างเงียบ ๆ</para>
///
/// <para>═══ บัญชีที่ใช้บนใบ (246-QR-3 · แก้ตามฝ่ายค้าน S3) ═══ แหล่งเงินของใบ (<c>Document.PaymentAccountId</c> — entity กำหนดว่าชนะ <c>BankAccountId</c>)
/// ที่ผูกบัญชีธนาคาร → บัญชีธนาคารของใบ (<c>Document.BankAccountId</c>) → บัญชีที่เทมเพลตเลือก (<c>DocumentTemplate.PaymentQrBankAccountId</c>) → บัญชีเดียวของบริษัทที่ตั้ง QR ไว้ (มีหลายบัญชี = ไม่เดา) · ตัวที่ระบุชัดแล้วไม่มี QR
/// = ไม่พิมพ์ (ไม่สลับไปบัญชีอื่นเงียบ ๆ — <see cref="PickAccount"/>)</para>
/// </summary>
public static class PaymentQrPolicy
{
    public const string RuleCode = "PAYQR-246";

    public const string SourceAuto = "auto";
    public const string SourcePromptPay = "promptpay";
    public const string SourceUpload = "upload";
    public static readonly IReadOnlyList<string> Sources = new[] { SourceAuto, SourcePromptPay, SourceUpload };

    /// <summary>QR ที่ใช้จริงของบัญชี</summary>
    public enum Kind { None, PromptPay, Upload }

    /// <summary>ค่าที่เก็บ — ว่าง/ไม่รู้จัก = <see cref="SourceAuto"/> (ค่าเดิมของแถวเก่า)</summary>
    public static string NormalizeSource(string? s)
    {
        var v = (s ?? "").Trim().ToLowerInvariant();
        return Sources.Contains(v) ? v : SourceAuto;
    }

    /// <summary>ค่าจากคำขอ — ว่าง = ค่าตั้งต้น · ไม่รู้จัก = ข้อความผิด (ไม่ตกเป็นค่าตั้งต้นเงียบ ๆ)</summary>
    public static string? SourceProblem(string? s)
        => string.IsNullOrWhiteSpace(s) || Sources.Contains(s.Trim().ToLowerInvariant())
            ? null
            : $"แหล่ง QR \"{s}\" ไม่รู้จัก — เลือกได้: auto (อัตโนมัติ) · promptpay (พร้อมเพย์ก่อน) · upload (รูปที่อัปโหลดก่อน)";

    /// <summary>
    /// เลขพร้อมเพย์ของบัญชี — รับเบอร์มือถือ 10 หลัก (ขึ้นต้น 0) หรือเลขประจำตัวผู้เสียภาษี/บัตรประชาชน 13 หลัก (checksum ผ่าน · <see cref="ThaiTaxId.IsValid"/>) ·
    /// ขีด/ช่องว่างถูกตัดทิ้ง · ว่าง = ล้างค่า (คืน <c>(null, null)</c>) · ผิดรูป = <c>(null, ข้อความ)</c>
    /// <para>ไม่รับ e-Wallet 15 หลักบนเอกสาร (เจ้าของระบุ "เลขภาษี 13 หลัก หรือเบอร์ 10 หลัก") — ตัวสร้าง payload ยังรับได้สำหรับเส้นอื่น</para>
    /// </summary>
    public static (string? Id, string? Problem) NormalizePromptPayId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var trimmed = raw.Trim();
        if (trimmed.Any(c => !(char.IsDigit(c) || c is '-' or ' ')))
            return (null, "เลขพร้อมเพย์ต้องเป็นตัวเลขเท่านั้น (เบอร์มือถือ 10 หลัก หรือเลขประจำตัวผู้เสียภาษี 13 หลัก)");
        var d = new string(trimmed.Where(char.IsDigit).ToArray());
        if (d.Length == 10)
        {
            if (d[0] == '0') return (d, null);
            return (null, "เบอร์มือถือพร้อมเพย์ต้องขึ้นต้นด้วย 0 (10 หลัก)");
        }
        if (d.Length == 13)
        {
            if (ThaiTaxId.IsValid(d)) return (d, null);
            return (null, "เลขประจำตัวผู้เสียภาษี 13 หลักไม่ถูกต้อง (ตรวจหลักสุดท้ายไม่ผ่าน)");
        }
        return (null, $"เลขพร้อมเพย์ต้องเป็นเบอร์มือถือ 10 หลัก หรือเลขประจำตัวผู้เสียภาษี 13 หลัก (ที่กรอกมี {d.Length} หลัก)");
    }

    /// <summary>ใช้เลขพร้อมเพย์ที่เก็บไว้สร้าง QR ได้ไหม (แถวเก่า/ค่าที่ถูกแก้ตรงในฐานก็ต้องผ่านตัวตรวจเดียวกัน)</summary>
    public static bool PromptPayUsable(string? stored) => stored != null && NormalizePromptPayId(stored).Id != null;

    /// <summary>เลือก QR ของบัญชีตามทางเลือกของผู้ใช้ (ดูคำอธิบายคลาส)</summary>
    public static Kind Pick(string? source, bool promptPayUsable, bool hasUpload)
    {
        var s = NormalizeSource(source);
        if (s == SourceUpload) return hasUpload ? Kind.Upload : promptPayUsable ? Kind.PromptPay : Kind.None;
        return promptPayUsable ? Kind.PromptPay : hasUpload ? Kind.Upload : Kind.None;
    }

    /// <summary>ที่มาของบัญชีที่เลือก — ใช้บอกเหตุบนหน้าเอกสาร/พรีวิว (ฝ่ายค้าน S4)</summary>
    public enum AccountOrigin { None, DocumentPaymentAccount, DocumentBankAccount, Template, SoleConfigured }

    /// <summary>ผลการเลือกบัญชี — <paramref name="AccountId"/> = บัญชีที่ใช้ (null = ไม่พิมพ์) · <paramref name="ChosenId"/> = บัญชีที่ถูกระบุชัด (มีแม้ไม่มี QR) ·
    /// <paramref name="Problem"/> = เหตุที่ไม่พิมพ์ (null = พิมพ์ได้ หรือไม่มีอะไรต้องบอก)</summary>
    public sealed record AccountPick(Guid? AccountId, AccountOrigin Origin, Guid? ChosenId, string? Problem);

    public const string ProblemChosenNoQr = "บัญชีธนาคารที่เลือกไว้ยังไม่ได้ตั้ง QR ชำระเงิน (หรือถูกปิดใช้งาน) — ตั้งเลขพร้อมเพย์/อัปโหลดรูป QR ที่หน้า “บัญชีธนาคาร” (ระบบไม่สลับไปบัญชีอื่นให้เอง)";
    public const string ProblemGlAmbiguous = "ผังบัญชีแหล่งเงินของใบนี้ผูกกับบัญชีธนาคารมากกว่าหนึ่งบัญชี — ระบบไม่เดาว่าใช้ QR บัญชีไหน · เลือกบัญชีธนาคารของใบ หรือ “บัญชีที่ใช้ QR” ในเทมเพลต";
    public const string ProblemManyConfigured = "บริษัทตั้ง QR ไว้หลายบัญชี — เลือก “บัญชีที่ใช้ QR ชำระเงิน” ในเทมเพลต หรือเลือกบัญชีธนาคารของใบ (ระบบไม่เดา)";

    /// <summary>
    /// บัญชีที่ใช้บนใบ (246-QR-3 · ฝ่ายค้าน S3) — ลำดับตาม entity: <b>แหล่งเงินของใบ</b> (<c>PaymentAccountId</c> "takes precedence over BankAccountId")
    /// ที่ผูกบัญชีธนาคาร (<paramref name="glBankAccounts"/> = บัญชีธนาคารที่เปิดใช้ซึ่งผูกผังนั้น: 1 = ใช้ · มากกว่า 1 = ไม่เดา · 0 = ผังไม่ใช่บัญชีธนาคาร เช่นเงินสด ⇒ ไปขั้นถัดไป) →
    /// บัญชีธนาคารของใบ (<paramref name="documentBank"/>) → บัญชีของเทมเพลต (<paramref name="templateBank"/>) → บัญชีเดียวของบริษัทที่ตั้ง QR ·
    /// <b>ตัวที่ระบุชัดชนะและไม่ถูกแทน</b> (ไม่มี QR = ไม่พิมพ์ + เหตุ) · <paramref name="configured"/> = บัญชีของบริษัทที่เปิดใช้และ <see cref="Pick"/> ≠ None
    /// </summary>
    public static AccountPick PickAccount(IReadOnlyCollection<Guid> glBankAccounts, Guid? documentBank, Guid? templateBank, IReadOnlyCollection<Guid> configured)
    {
        if (glBankAccounts.Count > 1) return new AccountPick(null, AccountOrigin.DocumentPaymentAccount, null, ProblemGlAmbiguous);
        AccountPick Chosen(Guid g, AccountOrigin o)
            => configured.Contains(g) ? new AccountPick(g, o, g, null) : new AccountPick(null, o, g, ProblemChosenNoQr);
        if (glBankAccounts.Count == 1) return Chosen(glBankAccounts.First(), AccountOrigin.DocumentPaymentAccount);
        if (documentBank is Guid db && db != Guid.Empty) return Chosen(db, AccountOrigin.DocumentBankAccount);
        if (templateBank is Guid tb && tb != Guid.Empty) return Chosen(tb, AccountOrigin.Template);
        return configured.Count switch
        {
            1 => new AccountPick(configured.First(), AccountOrigin.SoleConfigured, null, null),
            0 => new AccountPick(null, AccountOrigin.None, null, null),
            _ => new AccountPick(null, AccountOrigin.None, null, ProblemManyConfigured),
        };
    }

    /// <summary>ฝ่ายค้าน S1 — แก้ธนาคาร/ชื่อ/เลขบัญชี (ข้อความใต้ QR) ไหม · เลขบัญชีเทียบเฉพาะตัวเลข (ขีด/ช่องว่างไม่นับ)</summary>
    public static bool CaptionChanged(string? oldBank, string? oldName, string? oldNumber, string? newBank, string? newName, string? newNumber)
        => !string.Equals((oldBank ?? "").Trim(), (newBank ?? "").Trim(), StringComparison.Ordinal)
           || !string.Equals((oldName ?? "").Trim(), (newName ?? "").Trim(), StringComparison.Ordinal)
           || Digits(oldNumber) != Digits(newNumber);

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>
    /// ฝ่ายค้าน S2(ก) — ข้อความบัญชีของเทมเพลต (<c>BankDetailsText</c>) ไม่มีเลขบัญชีของบัญชีที่ QR ใช้ ⇒ ข้อความเตือน (null = ตรงกัน/ไม่มีข้อความ/ไม่มี QR) ·
    /// เทียบเลขแบบตัดขีด/ช่องว่างระหว่างตัวเลข · ข้อความว่างไม่เตือน (ใต้ QR พิมพ์ธนาคาร/ชื่อ/เลขของบัญชีจริงอยู่แล้ว — S2(ข))
    /// </summary>
    public static string? BankTextMismatch(string? bankDetailsText, string? qrAccountNumber, string? qrAccountLabel)
    {
        var acct = Digits(qrAccountNumber);
        if (string.IsNullOrWhiteSpace(bankDetailsText) || acct.Length == 0) return null;
        var joined = System.Text.RegularExpressions.Regex.Replace(bankDetailsText, @"(?<=\d)[\s\-]+(?=\d)", "");
        return joined.Contains(acct, StringComparison.Ordinal)
            ? null
            : $"ข้อความบัญชีธนาคารของเทมเพลตไม่มีเลขบัญชีของบัญชีที่ QR จะใช้ ({qrAccountLabel}) — ลูกค้าอาจเห็นสองบัญชีไม่ตรงกัน · "
              + "แก้ข้อความ หรือเลือก “บัญชีที่ใช้ QR ชำระเงิน” ให้ตรงกับข้อความ";
    }

    /// <summary>ชนิดเอกสารที่ "ลูกค้าจ่ายเรา" และพิมพ์ QR บัญชีรับเงินได้ — ใบเสนอราคา · ใบแจ้งหนี้ · ใบกำกับภาษี · ใบวางบิล · ใบเพิ่มหนี้ฝั่งขาย ·
    /// ใบเสร็จ/ใบลดหนี้/เอกสารฝั่งซื้อ = ไม่พิมพ์ (QR บัญชีเราบนใบสั่งซื้อที่ส่งผู้ขาย = เชิญคนผิดฝั่งโอนเงิน)</summary>
    public static bool IsBankQrDocument(DocumentType type, DocumentType? relatedType)
        => type is DocumentType.Quotation or DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.BillingNote
           // ไม่มีข้อเท็จจริงผู้ใช้สั่งย้ายฝั่ง/ผู้ขายอย่างเดียวที่นี่ ⇒ ตัดสินจากใบต้นทาง · ใบเพิ่มหนี้ไม่รู้ฝั่ง ⇒ ไม่พิมพ์ QR (รับชำระไม่ได้จนกว่าจะเลือกฝั่ง)
           || (type == DocumentType.DebitNote && DocumentPayLinkPolicy.IsSaleSide(type, relatedType, null, false));

    /// <summary>พิมพ์ QR บัญชีบนใบนี้ไหม — ปิดในเทมเพลต · ชนิดไม่เข้า · ยกเลิก · ไม่ใช่เงินบาท (พร้อมเพย์รับแต่บาท) · ใบตั้งหนี้ที่ชำระครบแล้ว ⇒ ไม่พิมพ์ ·
    /// ใบเสนอราคา/ใบวางบิล/ร่าง ยังไม่มียอดค้างที่เชื่อได้ ⇒ พิมพ์ได้ (QR ไม่ฝังยอด — <see cref="EmbedAmount"/>)</summary>
    public static bool ShowBankQr(bool templateToggle, DocumentType type, DocumentType? relatedType, DocumentStatus status, decimal balanceDue, string? currency)
    {
        if (!templateToggle || !IsBankQrDocument(type, relatedType)) return false;
        if (status == DocumentStatus.Voided) return false;
        if (!IsThb(currency)) return false;
        var receivable = type is DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.DebitNote;
        var issued = status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected);
        return !(receivable && issued && balanceDue <= 0.005m);
    }

    /// <summary>ยอดที่ฝังใน QR พร้อมเพย์ (246-QR-4) — เฉพาะใบที่ออกลิงก์ชำระได้ (<see cref="DocumentPayLinkPolicy.IssueRefusal"/> = null: ใบตั้งหนี้ฝั่งขายที่อนุมัติแล้ว
    /// ยังค้าง · เงินบาท) ใช้<b>ยอดสุทธิหลังหัก ณ ที่จ่ายจากตัวคิดเดียวกับลิงก์ชำระ</b> · อื่น ๆ = null (QR ไม่ระบุยอด ลูกค้ากรอกเอง — บอกบนกระดาษ)
    /// <para>ฝ่ายค้าน N6: ใบแจ้งหนี้ที่มีใบกำกับภาษี (ไม่ยกเลิก/ออกแล้ว) อ้างอยู่ (<paramref name="supersededByTaxInvoice"/>) = ลูกหนี้ย้ายไปใบกำกับ ⇒ ไม่ฝังยอด
    /// (พิมพ์ซ้ำใบแจ้งหนี้ต้องไม่ได้ QR ยอดเต็มซ้ำกับใบกำกับ) · ใบเสร็จรับบางส่วนลดยอดค้างของใบอยู่แล้ว (ยอดสุทธิ = ยอดที่เหลือ)</para></summary>
    public static decimal? EmbedAmount(string? payLinkRefusal, decimal? netAmount, bool supersededByTaxInvoice = false)
        => payLinkRefusal == null && !supersededByTaxInvoice && netAmount is > 0.005m
            ? Math.Round(netAmount.Value, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>พิมพ์ QR หน้าชำระออนไลน์ไหม (246-QR-5) — เปิดในเทมเพลต · ไม่ใช่ไฟล์ PDF/A-3 ของ e-Tax (ไฟล์ที่ส่ง ETDA/เก็บถาวรต้องไม่มี token) ·
    /// มีลิงก์ที่ยังใช้ได้อยู่แล้ว (ไม่ออกลิงก์ใหม่ตอนพิมพ์) · ช่องทางของใบยังมีช่องทางออนไลน์ที่บริษัทเปิด</summary>
    public static bool ShowPayLinkQr(bool templateToggle, bool pdfA, string? usableUrl, int onlineChannelCount)
        => templateToggle && !pdfA && !string.IsNullOrWhiteSpace(usableUrl) && onlineChannelCount > 0;

    private static bool IsThb(string? currency)
        => string.IsNullOrWhiteSpace(currency) || string.Equals(currency.Trim(), "THB", StringComparison.OrdinalIgnoreCase);

    /// <summary>เลขพร้อมเพย์แบบอ่านง่ายบนกระดาษ: 081-234-5678 · 0-1055-56000-00-9</summary>
    public static string DisplayPromptPayId(string id)
    {
        if (id.Length == 10) return $"{id[..3]}-{id[3..6]}-{id[6..]}";
        if (id.Length == 13) return $"{id[0]}-{id[1..5]}-{id[5..10]}-{id[10..12]}-{id[12]}";
        return id;
    }
}

/// <summary>QR ที่ตัวเรนเดอร์ทั้งสอง (HTML · QuestPDF) วาด — ผลของ <c>PaymentQrService.ResolveForDocumentAsync</c> ตัวเดียว · null = ไม่วาดกล่องนั้น</summary>
public sealed record PaymentQrBlock(BankPaymentQr? Bank, PayLinkQr? PayLink)
{
    public bool IsEmpty => Bank == null && PayLink == null;
    /// <summary>ฉบับ PDF/A-3 (e-Tax) — ตัด QR หน้าชำระ (มี token) ทิ้ง · เหลือ QR บัญชี (ไม่มีความลับ)</summary>
    public PaymentQrBlock? WithoutPayLink() => Bank == null ? null : this with { PayLink = null };
}

/// <summary>QR บัญชีรับเงิน — <paramref name="Png"/> = ไบต์ PNG ที่เซิร์ฟเวอร์สร้าง/เข้ารหัสใหม่แล้วเท่านั้น (ไม่ใช่ไฟล์ดิบของผู้ใช้) ·
/// <paramref name="Amount"/> = ยอดที่ฝังใน QR (null = ไม่ระบุยอด · รูปที่อัปโหลดเป็น null เสมอ) ·
/// <paramref name="AmountAsOf"/> = เวลาที่คิดยอด (UTC — กระดาษพิมพ์ "ยอด ณ วันที่พิมพ์" · ฝ่ายค้าน N1) · ธนาคาร/ชื่อ/เลข = ของบัญชีที่ QR ใช้จริงเสมอ (S2(ข))</summary>
public sealed record BankPaymentQr(byte[] Png, PaymentQrPolicy.Kind Kind, string? BankName, string? AccountName, string? AccountNumber,
    string? PromptPayId, decimal? Amount, DateTime? AmountAsOf = null);

/// <summary>ผลของ resolver สำหรับหน้าเว็บ (ฝ่ายค้าน S2(ก)/S4/N5) — กล่องที่จะพิมพ์ + เหตุที่ไม่พิมพ์ + คำเตือน (เซิร์ฟเวอร์ตัดสิน หน้าแสดงอย่างเดียว)</summary>
public sealed record PaymentQrDiagnosis(PaymentQrBlock? Block, Guid? AccountId, string? AccountLabel, string? Problem, IReadOnlyList<string> Warnings);

/// <summary>QR ของหน้าชำระออนไลน์ (token เดียวกับลิงก์ · หมดอายุ/เพิกถอนตามลิงก์) — ไม่พิมพ์ URL เป็นข้อความ</summary>
public sealed record PayLinkQr(byte[] Png, DateTime? ExpiresAt);
