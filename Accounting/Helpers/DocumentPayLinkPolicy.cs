using System.Security.Cryptography;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ลิงก์ชำระเงินของเอกสารขาย (ใบแจ้งหนี้ · ใบกำกับภาษี · ใบเพิ่มหนี้ฝั่งขาย) — ตัวตั้งเดียว** (รอบ 241 · คำตัดสิน 241-1..241-4 ใน
/// erp-review/2026-09-29/DECISIONS.md · DOCUMENT_FLOW §2.12g)
///
/// <para>ตอบ 5 คำถามที่ทุกทางเข้า (ตารางส่งอีเมล · หน้าเอกสาร · หน้าชำระสาธารณะ · handler ตอนเงินเข้า) ต้องตอบเหมือนกัน:
/// (1) บริษัทนี้รับช่องทางไหนได้จริง (<see cref="CompanyAvailable"/> / <see cref="UnavailableReason"/>) ·
/// (2) เอกสารนี้อนุญาตช่องทางไหน (<see cref="Allowed"/> = ที่บริษัทเปิดจริง ∩ ที่ตารางเลือก ∩ ค่าเริ่มต้นบริษัท) ·
/// (3) ใบนี้ออกลิงก์ได้ไหม (<see cref="IssueRefusal"/>) · (4) ยอดที่ลูกค้าต้องโอน (<see cref="AmountsFor"/> — ยอดค้าง − ภาษีหัก ณ ที่จ่ายที่ลูกค้าจะหัก) ·
/// (5) เงินเข้าแล้วต้องบันทึก WHT เท่าไร (<see cref="WhtForPayment"/>)</para>
///
/// <para>ช่องทางเก็บเป็น<b>ชื่อ enum</b> <see cref="PaymentMethodKind"/> (PromptPay · Card · …) + <see cref="BankTransferCode"/> (= ManualSlip:
/// แสดงเลขบัญชีให้โอน · ไม่มีการอัปโหลดสลิปแบบไม่ล็อกอิน — คำตัดสิน 241-2) · หน้าเว็บไม่มีตารางป้ายของตัวเอง (ป้ายจาก <see cref="Label"/>)</para>
/// </summary>
public static class DocumentPayLinkPolicy
{
    /// <summary>อายุลิงก์ (วัน) — ส่งอีเมลรอบใหม่หลังหมดอายุ = ออกลิงก์ใหม่ (ลิงก์เดิมใช้ไม่ได้)</summary>
    public const int TtlDays = 60;

    /// <summary>ช่องทาง "โอนเข้าบัญชี" (แสดงเลขบัญชี/พร้อมเพย์ของบริษัท · ไม่ใช่ช่องทางออนไลน์)</summary>
    public static readonly string BankTransferCode = nameof(PaymentMethodKind.ManualSlip);

    /// <summary>หน้าสาธารณะของลิงก์ (ผู้อ่าน: <see cref="Url"/>)</summary>
    public const string PagePath = "/pages/pay-document.html";

    public const string RuleCode = "PAYLINK-241";

    /// <summary>ความจริงของบริษัท ณ ตอนถาม — <paramref name="OnlineConfigured"/> = มีช่องทางรับชำระออนไลน์ที่เปิดอยู่ (PaymentProviderConfig.IsActive) ·
    /// <paramref name="Online"/> = วิธีที่หน้าชำระทำจบได้จริง (ผลของ <c>PlatformPayMethods.Effective</c> — ชื่อ enum) ·
    /// <paramref name="BankTransfer"/> = มีเลขบัญชี/พร้อมเพย์สำหรับโอน (ตัวเดียวกับหน้าพอร์ทัล <c>SiteTransferAccount.Rows</c>)</summary>
    public sealed record CompanyFacts(bool OnlineConfigured, IReadOnlyList<string> Online, bool BankTransfer);

    /// <summary>ช่องทางที่บริษัทเปิดได้จริง (ลำดับ: ออนไลน์ตามผู้ให้บริการ แล้วโอนเข้าบัญชี)</summary>
    public static IReadOnlyList<string> CompanyAvailable(CompanyFacts f)
    {
        var list = new List<string>();
        if (f.OnlineConfigured)
            foreach (var m in f.Online)
                if (!string.Equals(m, BankTransferCode, StringComparison.OrdinalIgnoreCase)
                    && !list.Contains(m, StringComparer.OrdinalIgnoreCase)) list.Add(m);
        if (f.BankTransfer) list.Add(BankTransferCode);
        return list;
    }

    /// <summary>เหตุที่ "แนบลิงก์ชำระเงิน" เลือกไม่ได้ — null = เลือกได้ · ต้องมีช่องทางออนไลน์อย่างน้อย 1 (ลิงก์ที่มีแต่เลขบัญชีไม่ใช่ "ชำระออนไลน์")</summary>
    public static string? UnavailableReason(CompanyFacts f)
    {
        if (!f.OnlineConfigured)
            return "บริษัทยังไม่ได้ตั้งค่ารับชำระเงินออนไลน์ — ตั้งที่ ตั้งค่า → รับชำระเงินออนไลน์ แล้วจึงเปิดแนบลิงก์ชำระเงินได้";
        if (CompanyAvailable(f).All(c => string.Equals(c, BankTransferCode, StringComparison.OrdinalIgnoreCase)))
            return "ช่องทางรับชำระออนไลน์ที่ตั้งไว้ยังไม่มีวิธีที่หน้าชำระทำจบได้ (เช่น พร้อมเพย์/บัตร) — ตรวจวิธีที่เปิดในหน้าตั้งค่ารับชำระเงินออนไลน์";
        return null;
    }

    /// <summary>ช่องทางที่ <b>เอกสารนี้</b> เปิดให้ลูกค้า = ที่บริษัทเปิดจริง ∩ ตัวเลือก (ตารางส่งเอกสาร ถ้ามี · ไม่งั้นค่าเริ่มต้นบริษัท) ·
    /// ตัวเลือก null/ว่าง = ทุกช่องทางที่บริษัทเปิด · ชื่อที่บริษัทไม่ได้เปิดถูกตัดทิ้ง (ห้ามเปิดช่องทางที่ร้านไม่ได้ตั้ง) · ลำดับตามบริษัท</summary>
    public static IReadOnlyList<string> Allowed(IReadOnlyList<string> companyAvailable,
        IReadOnlyCollection<string>? scheduleChoice, IReadOnlyCollection<string>? companyDefaultChoice)
    {
        var choice = scheduleChoice is { Count: > 0 } ? scheduleChoice
            : companyDefaultChoice is { Count: > 0 } ? companyDefaultChoice : null;
        if (choice == null) return companyAvailable.ToList();
        return companyAvailable.Where(c => choice.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>ช่องทางออนไลน์ในชุด (ตัดโอนเข้าบัญชี) — สิ่งที่ปุ่ม "ชำระออนไลน์" และด่านสร้างรายการชำระใช้</summary>
    public static IReadOnlyList<string> OnlineOnly(IEnumerable<string> allowed)
        => allowed.Where(c => !string.Equals(c, BankTransferCode, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>ชื่อที่ผู้ใช้ส่งมาแต่ไม่ใช่ช่องทางที่บริษัทเปิด (ด่านตอนบันทึก — ปฏิเสธพร้อมชื่อ ไม่เก็บเงียบ ๆ)</summary>
    public static IReadOnlyList<string> Unknown(IReadOnlyList<string> companyAvailable, IEnumerable<string>? requested)
        => (requested ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)
            && !companyAvailable.Contains(r.Trim(), StringComparer.OrdinalIgnoreCase)).Select(r => r.Trim()).Distinct().ToList();

    public static string Label(string code)
        => Enum.TryParse<PaymentMethodKind>(code, true, out var k)
            ? (k == PaymentMethodKind.ManualSlip ? "โอนเข้าบัญชีธนาคาร / พร้อมเพย์ของบริษัท" : PlatformPayMethods.Label(k))
            : code;

    /// <summary>JSON ↔ รายการ (ตัวเดียวทั้งฝั่งเขียน/อ่าน) · อ่านไม่ได้ = null (ไม่ได้เลือก)</summary>
    public static List<string>? ParseChannels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            return list?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static string? SerializeChannels(IEnumerable<string>? channels)
    {
        var list = channels?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(list) : null;
    }

    /// <summary>เหตุที่ออกลิงก์ชำระเงินให้ใบนี้ไม่ได้ — null = ได้ · เฉพาะเอกสารตั้งหนี้ฝั่งขายที่ออกเลขจริงแล้ว (§86/4 — ร่างยังเป็นเลข DRAFT)
    /// และยังมียอดค้าง · ตรงกับชนิดที่ <c>DocumentPaymentHandler</c> → <c>CreatePaymentAsync</c> บันทึกรับชำระได้</summary>
    public static string? IssueRefusal(DocumentType type, DocumentStatus status, decimal balanceDue, bool saleSide, string? currency)
    {
        if (!(type is DocumentType.Invoice or DocumentType.TaxInvoice || (type == DocumentType.DebitNote && saleSide)))
            return "ลิงก์ชำระเงินใช้ได้กับใบแจ้งหนี้ · ใบกำกับภาษี · ใบเพิ่มหนี้ฝั่งขายเท่านั้น";
        if (!string.IsNullOrWhiteSpace(currency) && !string.Equals(currency.Trim(), "THB", StringComparison.OrdinalIgnoreCase))
            return "ลิงก์ชำระเงินออนไลน์รองรับเฉพาะเอกสารสกุลเงินบาท (THB)";
        if (status is DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected)
            return "ต้องอนุมัติเอกสารก่อน (ฉบับร่าง/รออนุมัติยังไม่มีเลขเอกสารจริง)";
        if (status == DocumentStatus.Voided)
            return "เอกสารถูกยกเลิกแล้ว — ลิงก์ชำระเงินใช้ไม่ได้";
        if (balanceDue <= 0.005m)
            return "เอกสารนี้ชำระครบแล้ว";
        return null;
    }

    /// <summary>เอกสารนี้เป็น "ลูกค้าจ่ายเรา" ไหม — ใบเพิ่มหนี้ดูฝั่งจากใบต้นทาง (ต้นทางฝั่งซื้อ = เราจ่ายผู้ขาย) · ตรงกับ <c>DocumentService.IsCashInflowDocAsync</c></summary>
    public static bool IsSaleSide(DocumentType type, DocumentType? relatedType)
        => type is DocumentType.Invoice or DocumentType.TaxInvoice
           || (type == DocumentType.DebitNote
               && relatedType is not (DocumentType.PurchaseInvoice or DocumentType.Expense or DocumentType.CertificateInLieu));

    /// <summary>ผู้ซื้อมีหน้าที่หักภาษี ณ ที่จ่ายไหม (ฝ่ายค้าน S3) — นิติบุคคล/หน่วยงานรัฐ = มี (ท.ป.4/2528) · บุคคลธรรมดา = ไม่มี ·
    /// ชนิดไม่รู้ ⇒ ใช้รูปเลขผู้เสียภาษี (<see cref="ContactTypeResolver.FromTaxId"/> ตัวเดิม) · ยังไม่รู้ ⇒ <c>null</c> (ผู้เรียกเชื่อยอดที่ผู้ใช้ตั้งบนใบ)</summary>
    public static bool? BuyerWithholds(ContactType? declared, string? taxId)
    {
        var t = declared is ContactType.JuristicPerson or ContactType.GovernmentAgency or ContactType.Individual
            ? declared : ContactTypeResolver.FromTaxId(taxId);
        return t switch
        {
            ContactType.JuristicPerson or ContactType.GovernmentAgency => true,
            ContactType.Individual => false,
            _ => null,
        };
    }

    /// <summary>ยอด WHT ที่คาดว่าลูกค้าจะหักจริง — ผู้ซื้อไม่มีหน้าที่หัก ⇒ 0 (จ่ายยอดเต็ม) · อื่น ๆ = ยอดบนใบ</summary>
    public static decimal ExpectedWht(decimal documentWht, bool? buyerWithholds) => buyerWithholds == false ? 0m : documentWht;

    /// <summary>คำเตือนบนหน้าเอกสาร: ใบตั้งยอด WHT ไว้แต่ผู้ซื้อไม่มีหน้าที่หัก (null = ไม่มีอะไรต้องเตือน)</summary>
    public static string? WhtMismatchNote(decimal documentWht, bool? buyerWithholds)
        => documentWht > 0.005m && buyerWithholds == false
            ? $"ใบนี้ตั้งภาษีหัก ณ ที่จ่าย {documentWht:N2} บาท แต่ผู้ซื้อเป็นบุคคลธรรมดา (ไม่มีหน้าที่หัก) — ลิงก์ชำระเรียกยอดค้างตามใบซึ่งหัก WHT ไว้แล้ว (ไม่มีหน้าที่หัก · ยอดอาจขาดเท่า WHT) · ตรวจยอด WHT บนใบหรือชนิดผู้ติดต่อ"
            : null;

    /// <summary>ยอดบนหน้าชำระ — <paramref name="Gross"/> = ยอดก่อนหัก ณ ที่จ่าย (ยอดค้าง + WHT ที่ยังไม่ถูกหัก) · <paramref name="Wht"/> = ภาษีหัก ณ ที่จ่ายที่ลูกค้าจะหักไว้
    /// (ส่วนที่ยังไม่ถูกหัก) · <paramref name="Net"/> = ยอดที่ลูกค้าโอนจริง = <b>ยอดค้างของเอกสาร</b> (= รายการชำระออนไลน์)</summary>
    public sealed record Amounts(decimal Gross, decimal Wht, decimal Net);

    /// <summary>
    /// ยอดของลิงก์ — <b>ยอดค้างของเอกสาร (<c>BalanceDue</c>) เป็นยอดสุทธิหลังหัก ณ ที่จ่ายอยู่แล้ว</b>: ตัวคำนวณเอกสารตั้ง
    /// <c>TotalAmount = SubTotal + VAT − WHT</c> (<c>WhtGrossUp.PayeeTotal</c>) และการรับชำระนับเฉพาะเงินเข้า (<c>PaidAmount += เงิน</c> · WHT ล้างลูกหนี้ใน GL ที่ขา 11910)
    /// ⇒ ยอดโอน = ยอดค้าง · ยอดก่อนหัก = ยอดค้าง + WHT ที่เหลือ (= ยอด WHT ของเอกสาร − ที่หักไปแล้วกับงวดก่อน · ไม่ติดลบ) · ตัวเลขของเอกสาร
    /// (<c>Document.WithholdingTaxAmount</c>) คือผลของตัวคำนวณ WHT เดิมตอนออกใบ — ไม่คิดอัตราใหม่
    /// <para>รอบ 244 (แก้ CI ของรอบ 241): เดิมถือยอดค้างเป็นยอด<b>ก่อน</b>หัก แล้วลบ WHT ซ้ำอีกรอบ ⇒ ลิงก์เรียกเงินขาดเท่า WHT และใบค้างเท่า WHT ตลอดกาล
    /// (ใบจริง 1,000 + VAT 70 − WHT 30 = ยอดค้าง 1,040 ⇒ ลิงก์เดิมเรียก 1,010)</para>
    /// </summary>
    public static Amounts AmountsFor(decimal balanceDue, decimal documentWhtTotal, decimal whtAlreadyWithheld)
    {
        var net = Math.Round(Math.Max(0m, balanceDue), 2, MidpointRounding.AwayFromZero);
        var wht = net <= 0.005m ? 0m
            : Math.Round(Math.Max(0m, documentWhtTotal - whtAlreadyWithheld), 2, MidpointRounding.AwayFromZero);
        return new Amounts(net + wht, wht, net);
    }

    /// <summary>เงินเข้าผ่านลิงก์แล้ว: ภาษีหัก ณ ที่จ่ายที่ต้องบันทึกคู่กับการชำระ — ลูกค้าโอนยอดค้างพอดี (± 0.01 · งวดนี้ปิดหนี้) ⇒ WHT ที่เหลือทั้งก้อน ·
    /// ไม่ตรง/ไม่มี WHT ⇒ null = ให้ตัวบันทึกรับชำระเดิมแนะนำเอง (พฤติกรรมเดิม) · ยอดค้างเป็นยอดสุทธิหลังหักแล้ว (ดู <see cref="AmountsFor"/>)</summary>
    public static decimal? WhtForPayment(decimal paidAmount, decimal balanceDue, decimal whtRemaining)
    {
        if (whtRemaining <= 0.005m) return null;
        return Math.Abs(paidAmount - balanceDue) <= 0.01m ? whtRemaining : null;
    }

    /// <summary>token ใหม่ (256 บิต · hex 64 ตัว) — สุ่มจาก CSPRNG</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>ฐานเก็บ<b>เฉพาะ hash</b> ของ token (คำตัดสิน 241-10 · แบบเดียวกับ <c>PlatformPaymentLinks.TokenHash</c>) — ฟังก์ชัน hash ตัวเดียวกับลิงก์ชำระค่าบริการ
    /// (<see cref="PlatformPaymentLinkRules.Hash"/> · ฝั่งเขียน/ฝั่งค้นเรียกที่นี่ที่เดียว)</summary>
    public static string TokenHash(string token) => PlatformPaymentLinkRules.Hash(token);

    /// <summary>รูป token ที่รับก่อนแตะฐาน (ไม่ผ่าน = "ไม่พบ" เหมือน token ผิด) — ตัวเดียวกับลิงก์ชำระค่าบริการ</summary>
    public static bool LooksValid(string? token) => PlatformPaymentLinkRules.LooksValid(token);

    /// <summary>ยืนยัน token กับ hash ที่เก็บ — เทียบแบบเวลาคงที่ (constant-time) · ว่าง = ไม่ตรง</summary>
    public static bool TokenMatches(string? token, string? storedHash)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(storedHash)) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(TokenHash(token)), System.Text.Encoding.ASCII.GetBytes(storedHash));
    }

    /// <summary>ลิงก์ใช้ได้ไหม — มี token (hash) · ไม่ถูกเพิกถอน · ยังไม่หมดอายุ</summary>
    public static bool LinkUsable(string? tokenHash, DateTime? expiresAt, DateTime? revokedAt, DateTime nowUtc)
        => !string.IsNullOrWhiteSpace(tokenHash) && revokedAt == null && (expiresAt == null || expiresAt > nowUtc);

    /// <summary>URL สาธารณะ — origin จาก <c>SystemBaseUrl</c> (ไม่ประกอบโดเมนเอง) · companyId อยู่ใน URL เพราะเส้น API สาธารณะผูกบริษัท</summary>
    public static string Url(string origin, Guid companyId, string token)
        => $"{origin.TrimEnd('/')}{PagePath}?c={companyId:D}&t={token}";

    /// <summary>ที่หมายของ URL ในคิวอีเมล (ฝ่ายค้าน S1) — คิว/บันทึกเก็บคำนี้ ไม่เก็บ token · URL จริงประกอบตอนส่งด้วย <see cref="ResolvePlaceholder"/></summary>
    public const string QueuePlaceholder = "{PayLink}";
    private const string BlockStart = "<!--paylink:start-->";
    private const string BlockEnd = "<!--paylink:end-->";

    public static bool HasPlaceholder(string? text) => text?.Contains(QueuePlaceholder, StringComparison.Ordinal) ?? false;

    /// <summary>แยกบล็อกลิงก์ชำระ (HTML ของเซิร์ฟเวอร์) ออกจากเนื้ออีเมล — ผู้เรียกหนีส่วนที่เหลือเป็นข้อความล้วนได้โดยไม่ทำปุ่มพัง</summary>
    public static (string Remainder, string? Block) SplitBlock(string body)
    {
        var s = body.IndexOf(BlockStart, StringComparison.Ordinal);
        var e = s < 0 ? -1 : body.IndexOf(BlockEnd, s, StringComparison.Ordinal);
        if (s < 0 || e < 0) return (body, null);
        e += BlockEnd.Length;
        return (body[..s] + body[e..], body[s..e]);
    }

    /// <summary>แทน <see cref="QueuePlaceholder"/> ด้วย URL จริงตอนส่ง — <paramref name="url"/> null (ลิงก์ใช้ไม่ได้แล้ว: ชำระครบ/ยกเลิก/เพิกถอน) ⇒ ถอดบล็อกลิงก์ทิ้ง
    /// และคำที่เหลือเป็นค่าว่าง (ไม่ส่งปุ่มที่กดแล้วไปไหนไม่ได้) · <paramref name="html"/> = หนี URL ก่อนวางใน HTML</summary>
    public static string ResolvePlaceholder(string text, string? url, bool html)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (url == null)
        {
            while (true)
            {
                var (rest, block) = SplitBlock(text);
                if (block == null) break;
                text = rest;
            }
            return text.Replace(QueuePlaceholder, "", StringComparison.Ordinal);
        }
        return text.Replace(QueuePlaceholder, html ? System.Net.WebUtility.HtmlEncode(url) : url, StringComparison.Ordinal);
    }

    /// <summary>บล็อก HTML ในอีเมล (ค่าทุกตัวหนีแล้ว) — ยอดตามเอกสาร/ภาษีหัก ณ ที่จ่าย/ยอดชำระ + ปุ่ม · ไม่มี tracking · ครอบด้วยเครื่องหมายให้ <see cref="SplitBlock"/> หาเจอ</summary>
    public static string EmailBlock(string url, Amounts a, bool isEnglish)
        => BlockStart + EmailBlockInner(url, a, isEnglish) + BlockEnd;

    private static string EmailBlockInner(string url, Amounts a, bool isEnglish)
    {
        var u = System.Net.WebUtility.HtmlEncode(url);
        var whtRow = a.Wht > 0.005m
            ? (isEnglish
                ? $"<br>Less withholding tax you will withhold: THB {a.Wht:N2} (please send us the withholding tax certificate)<br>Amount to pay: <strong>THB {a.Net:N2}</strong>"
                : $"<br>หักภาษี ณ ที่จ่ายที่ท่านหักไว้ {a.Wht:N2} บาท (กรุณาส่งหนังสือรับรองการหักภาษี ณ ที่จ่าย ให้เรา)<br>ยอดที่ต้องชำระ <strong>{a.Net:N2} บาท</strong>")
            : "";
        // รอบ 244: Gross = ยอดก่อนหัก ณ ที่จ่าย (ยอดค้างของเอกสารเป็นยอดสุทธิแล้ว — ดู AmountsFor)
        var head = a.Wht > 0.005m
            ? (isEnglish ? $"Amount before withholding tax: THB {a.Gross:N2}" : $"ยอดก่อนหักภาษี ณ ที่จ่าย {a.Gross:N2} บาท")
            : (isEnglish ? $"Amount due: THB {a.Gross:N2}" : $"ยอดค้างชำระตามเอกสาร {a.Gross:N2} บาท");
        var btn = isEnglish ? "Pay online" : "ชำระเงินออนไลน์";
        return $"<div style='margin-top:14px;padding:12px;border:1px solid #c7d2fe;border-radius:8px;background:#eef2ff'>{head}{whtRow}"
             + $"<br><a href='{u}' style='display:inline-block;margin-top:8px;background:#4f46e5;color:#fff;padding:9px 16px;border-radius:6px;text-decoration:none'>{btn}</a>"
             + $"<br><span style='font-size:12px;color:#64748b'>{u}</span></div>";
    }
}
