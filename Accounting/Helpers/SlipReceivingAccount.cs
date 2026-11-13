using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **บัญชีธนาคารที่เงินโอนตามสลิปเข้า — ตัวตัดสินตัวเดียว** (รอบ 260-2 · <c>erp-review/2026-11-12/team-260-2-slip-bank-account.md</c> · คำตัดสิน 260-2-1..5)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ ยืนยันสลิปที่คิว (<c>PaymentGatewayController.ConfirmManually</c>) ⇒ <c>DocumentPaymentHandler</c> ส่ง
/// <c>OverrideBankAccountId: null</c> + ผังจาก <c>IGatewayAccountResolver</c> (= null สำหรับสลิป เพราะเงินเข้าธนาคารตรง) ⇒ การชำระสืบทอดแหล่งเงินของใบแจ้งหนี้ ·
/// ใบส่วนใหญ่ไม่ได้ตั้ง ⇒ <b>JE ลงเงินสด 111</b> ทั้งที่เงินเข้าธนาคาร · ยอดธนาคารในระบบไม่ขยับ · กระทบยอดธนาคารหา JE ไม่เจอตลอดไป ·
/// สลิปการจองนัดหมายเป็นคลาสเดียวกัน (ใบรับมัดจำไม่มีแหล่งเงิน)</para>
///
/// <para>═══ กติกา ═══
/// <list type="number">
/// <item><see cref="Applies"/> — ใช้กับ<b>สลิปโอน</b>เท่านั้น (manual-slip) · เงินผ่านผู้ให้บริการ (Omise ฯลฯ) ยังอยู่กับผู้ให้บริการ ⇒ บัญชีพักของ
///   <c>IGatewayAccountResolver</c> ตัวเดิม — <b>เลือกธนาคารไม่ได้</b> (ลงธนาคารด้วยเงินที่ยังไม่เข้า)</item>
/// <item><see cref="ChoiceProblem"/> — บัญชีที่พนักงานเลือก: ของบริษัทนี้ · เปิดใช้ · สกุลเดียวกับรายการ · ไม่ใช่บัญชีฝากประจำ · มีเลขบัญชี/พร้อมเพย์ ·
///   ผูกผังที่ลงรายการได้ (ตัวตรวจเดียวกับแหล่งเงินของเอกสาร <see cref="DocumentMoneyChannel.EvaluateAsync"/>) — ใช้ไม่ได้ = ปฏิเสธพร้อมเหตุ ไม่ตกไปเงินสด</item>
/// <item><see cref="Suggest"/> — ค่าแนะนำเมื่อไม่ได้เลือก (ลำดับตายตัว · บัญชีผู้รับที่อ่านจากสลิปเองยังไม่มีตัวอ่าน — ไม่แต่งชั้นนี้): <b>แหล่งเงินของเอกสาร</b>
///   (สืบทอดตามเดิม = พฤติกรรมก่อนรอบนี้) → <b>บัญชีที่ระบบแสดงให้ลูกค้าโอน</b> (ตัวเลือกบัญชีของกระดาษ/หน้าชำระ → บัญชีรับโอนของเว็บ) →
///   <b>บัญชีรับโอนบัญชีเดียว</b>ของบริษัท → ไม่แนะนำ (หลายบัญชี = ไม่เดา · คงพฤติกรรมเดิม)</item>
/// <item>ผลที่ตัดสินแล้วถูก<b>ประทับบนรายการชำระ</b> (<c>ReceivingBankAccountId</c> + <c>ReceivingAccountOrigin</c>) ⇒ handler · ส่งต่อใหม่ · หน้ารายการ อ่านค่าเดียวกัน</item>
/// </list></para>
/// </summary>
public static class SlipReceivingAccount
{
    public const string RuleCode = "SLIP-RECEIVING-ACCOUNT";
    /// <summary>ฐานของการบันทึกขาเงินเข้าตามบัญชีที่เงินเข้าจริง (audit · กฎ M)</summary>
    public const string LegalReference = "พ.ร.บ.การบัญชี 2543 ม.8 (บันทึกบัญชีตามเอกสารประกอบที่ถูกต้อง) · TFRS for NPAEs (เงินสดและรายการเทียบเท่าเงินสด)";

    /// <summary>ที่มาของบัญชีที่ใช้ — เก็บเป็นชื่อใน <c>PaymentIntent.ReceivingAccountOrigin</c> (varchar) · หน้าแสดงป้ายจาก <see cref="OriginLabel"/></summary>
    public enum Origin
    {
        /// <summary>ไม่แนะนำ — ไม่มีหลักฐานพอ (หลายบัญชี/ไม่มีบัญชีรับโอน) ⇒ สืบทอดแหล่งเงินของเอกสารตามเดิม (ไม่มี ⇒ เงินสดตั้งต้น)</summary>
        None = 0,
        /// <summary>พนักงานเลือกเอง</summary>
        Chosen = 1,
        /// <summary>เอกสารตั้งแหล่งเงินไว้ ⇒ สืบทอดตามเดิม (ไม่ส่ง override)</summary>
        DocumentChannel = 2,
        /// <summary>บัญชีที่ระบบแสดงให้ลูกค้าโอน (QR/กล่องโอนของใบ หรือบัญชีรับโอนของเว็บ)</summary>
        ShownToCustomer = 3,
        /// <summary>บริษัทมีบัญชีรับโอนที่ใช้ได้บัญชีเดียว</summary>
        SoleTransferAccount = 4,
        /// <summary>เงินผ่านผู้ให้บริการ — ไม่ใช่เรื่องของตัวนี้ (บัญชีพักของ IGatewayAccountResolver)</summary>
        NotApplicable = 5,
    }

    /// <summary>ใช้กับรายการนี้ไหม — สลิปโอน (ผู้ให้บริการ manual-slip + วิธีแนบสลิป) เท่านั้น</summary>
    public static bool Applies(string? providerCode, PaymentMethodKind method)
        => providerCode == Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code && method == PaymentMethodKind.ManualSlip;

    /// <summary>ข้อเท็จจริงของบัญชีธนาคาร (โหลดด้วย CompanyId · ไม่นับแถวที่ลบ) · <c>LinkedGlProblem</c> = ข้อความจาก
    /// <see cref="DocumentMoneyChannel.EvaluateAsync"/> (null = ผูกผังที่ลงรายการได้)</summary>
    public sealed record AccountFacts(Guid Id, string Label, bool IsActive, string Currency, string AccountType,
        bool HasTransferId, string? LinkedGlProblem);

    /// <summary>ใช้รับโอนจากลูกค้าได้ไหม (ไม่สนใจว่าเลือกหรือแนะนำ) — null = ได้</summary>
    public static string? UsableProblem(AccountFacts a, string intentCurrency)
    {
        if (!a.IsActive)
            return $"บัญชีธนาคาร “{a.Label}” ถูกปิดใช้งาน — เปิดใช้ที่หน้า “บัญชีธนาคาร” หรือเลือกบัญชีอื่น";
        if (!string.Equals((a.Currency ?? "THB").Trim(), (intentCurrency ?? "THB").Trim(), StringComparison.OrdinalIgnoreCase))
            return $"บัญชีธนาคาร “{a.Label}” เป็นสกุล {a.Currency} แต่รายการนี้เป็นสกุล {intentCurrency} — เลือกบัญชีสกุลเดียวกัน";
        if (string.Equals((a.AccountType ?? "").Trim(), "Fixed", StringComparison.OrdinalIgnoreCase))
            return $"บัญชีธนาคาร “{a.Label}” เป็นบัญชีฝากประจำ รับเงินโอนจากลูกค้าไม่ได้ — เลือกบัญชีออมทรัพย์/กระแสรายวัน";
        if (!a.HasTransferId)
            return $"บัญชีธนาคาร “{a.Label}” ไม่มีเลขบัญชีหรือพร้อมเพย์ — ลูกค้าโอนเข้าบัญชีนี้ไม่ได้ · ตั้งเลขบัญชีที่หน้า “บัญชีธนาคาร”";
        return a.LinkedGlProblem;
    }

    /// <summary>
    /// ตรวจบัญชีที่พนักงานเลือก (pure) — <paramref name="facts"/> null = ไม่มีบัญชีนี้ในบริษัทนี้ (หรือถูกลบ) · null = ใช้ได้
    /// </summary>
    internal static string? ChoiceProblem(bool applies, AccountFacts? facts, string intentCurrency)
    {
        if (!applies)
            return "รายการนี้รับเงินผ่านผู้ให้บริการรับชำระ — เงินยังอยู่กับผู้ให้บริการ ระบบลงบัญชีพักของผู้ให้บริการให้เอง เลือกบัญชีธนาคารไม่ได้ "
                   + "(เงินจะเข้าธนาคารตอนรอบโอนของผู้ให้บริการ)";
        if (facts is null)
            return "ไม่พบบัญชีธนาคารที่เลือกในบริษัทนี้ (อาจถูกลบไปแล้ว) — เปิดหน้าต่างยืนยันใหม่แล้วเลือกบัญชีจากรายการ";
        return UsableProblem(facts, intentCurrency);
    }

    /// <summary>แหล่งเงินของเอกสาร (โหลดแล้ว) — <c>HasChannel</c> = ตั้งผังตรงหรือธนาคารไว้ · <c>IsBank</c> = แหล่งเงินเป็น<b>บัญชีธนาคารเอง</b> (<c>doc.BankAccountId</c>) ·
    /// <c>Bank</c> = บัญชีธนาคารของแหล่งเงิน (ธนาคารเอง หรือธนาคารบัญชีเดียวที่ผูกผังตรงนั้น) · <c>Problem</c> = แหล่งเงินใช้ลงบัญชีไม่ได้ (ปิดใช้/ไม่ผูกผัง ฯลฯ)</summary>
    public sealed record DocChannel(bool HasChannel, bool IsBank, Guid? Bank, string? Problem)
    {
        public static readonly DocChannel None = new(false, false, null, null);
    }

    /// <summary>บัญชีรับโอนของเว็บ (แถวที่ลูกค้าเห็น) เทียบกับทะเบียนบัญชีธนาคาร — ฝ่ายค้าน 260-2 M3: "มีแถวแต่ไม่ตรง" ≠ "ไม่มีแถว"</summary>
    public enum SiteRowMatch { NoRow = 0, Matched = 1, Unmatched = 2 }

    /// <summary>ค่าแนะนำ · <c>BankAccountId</c> = ค่าที่ส่งเป็น <c>OverrideBankAccountId</c> (null = ไม่ส่ง ⇒ สืบทอดแหล่งเงินของเอกสาร/เงินสดตั้งต้น) ·
    /// <c>DisplayBankAccountId</c> = บัญชีที่หน้าแสดงเป็นค่าตั้งต้นของช่อง · <c>InheritsBank</c> = แหล่งเงินของเอกสารเป็นธนาคารเอง (สืบทอดแล้วยอดธนาคารขยับเอง — ไม่ต้อง override)</summary>
    public sealed record Suggestion(Guid? BankAccountId, Guid? DisplayBankAccountId, Origin Origin, string Reason, bool InheritsBank = false,
        bool RequiresChoice = false);

    /// <summary>
    /// **ค่าแนะนำ (pure)** — (หลักฐานจากสลิปเอง เช่นบัญชีผู้รับใน QR ท้ายสลิป ยังไม่มีตัวอ่าน ⇒ ไม่มีชั้นนี้จนกว่าจะอ่านได้จริง · ห้ามแต่ง — F2 ข้อ 3) ·
    /// <paramref name="doc"/> = แหล่งเงินของเอกสาร · <paramref name="shownToCustomer"/> = บัญชีที่ระบบ<b>แสดงจริง</b>ให้ลูกค้าโอน (QR/กล่องโอนของใบ หรือบัญชีรับโอนของเว็บที่เลขตรง) ·
    /// <paramref name="siteRow"/> = สถานะบัญชีรับโอนของเว็บ (มีแถวแต่ไม่ตรง ⇒ ไม่เดา) · <paramref name="usable"/> = บัญชีที่ผ่าน <see cref="UsableProblem"/>
    /// </summary>
    public static Suggestion Suggest(bool applies, DocChannel doc, Guid? shownToCustomer, SiteRowMatch siteRow, IReadOnlyCollection<Guid> usable)
    {
        if (!applies)
            return new Suggestion(null, null, Origin.NotApplicable, "เงินผ่านผู้ให้บริการรับชำระ — ลงบัญชีพักของผู้ให้บริการ (รอรอบโอน)");
        if (doc.HasChannel)
        {
            // ฝ่ายค้าน 260-2 M4: แหล่งเงินของเอกสารใช้ไม่ได้ ⇒ ไม่แนะนำ (ไม่ปล่อยให้ยืนยันแล้ว "สำเร็จแต่ไม่บันทึกรับชำระ")
            if (doc.Problem != null)
                return new Suggestion(null, null, Origin.None, "แหล่งเงินที่ตั้งบนเอกสารใช้ลงบัญชีไม่ได้ (" + doc.Problem + ") — เลือกบัญชีที่เงินเข้าจริง",
                    RequiresChoice: true);
            if (doc.IsBank)
                return new Suggestion(null, doc.Bank is Guid b && usable.Contains(b) ? b : null, Origin.DocumentChannel,
                    "ตามแหล่งเงินที่ตั้งไว้บนเอกสาร (เหมือนเดิม)", InheritsBank: true);
            // ฝ่ายค้าน 260-2 M1: แหล่งเงินเป็นผังตรงที่ผูกธนาคารบัญชีเดียว ⇒ ส่งธนาคารนั้นเป็น override (ผังเดียวกัน + ยอดธนาคาร/ผู้สมัครกระทบยอดขยับ)
            if (doc.Bank is Guid linked && usable.Contains(linked))
                return new Suggestion(linked, linked, Origin.DocumentChannel, "ตามแหล่งเงินที่ตั้งไว้บนเอกสาร (ผังที่ผูกบัญชีธนาคารนี้)");
            return new Suggestion(null, null, Origin.DocumentChannel, "ตามแหล่งเงินที่ตั้งไว้บนเอกสาร (ผังที่ไม่ใช่บัญชีธนาคาร — เหมือนเดิม)");
        }
        if (shownToCustomer is Guid shown && usable.Contains(shown))
            return new Suggestion(shown, shown, Origin.ShownToCustomer, "บัญชีที่ระบบแสดงให้ลูกค้าโอน (QR/กล่องโอนของเอกสาร หรือบัญชีรับโอนของเว็บ)");
        // ฝ่ายค้าน 260-2 M3: ลูกค้าเห็นบัญชีรับโอนของเว็บที่ไม่ตรงบัญชีใดในระบบ ⇒ หลักฐานขัดกับ "บัญชีเดียว" ⇒ ไม่เดา
        if (siteRow == SiteRowMatch.Unmatched)
            return new Suggestion(null, null, Origin.None,
                "บัญชีรับโอนที่แสดงให้ลูกค้า (หน้าตั้งค่ารับโอนของเว็บ) ไม่ตรงบัญชีธนาคารใดในระบบ — ระบบไม่เดา · เลือกบัญชีที่เห็นเงินเข้าจริง หรือเพิ่มบัญชีนั้นที่หน้า “บัญชีธนาคาร”");
        if (usable.Count == 1)
        {
            var only = usable.First();
            return new Suggestion(only, only, Origin.SoleTransferAccount, "บริษัทมีบัญชีรับโอนที่ใช้ได้บัญชีเดียว");
        }
        return new Suggestion(null, null, Origin.None, usable.Count == 0
            ? "ยังไม่มีบัญชีธนาคารที่รับโอนได้ (ผูกผังบัญชี + มีเลขบัญชี/พร้อมเพย์) — ระบบลงตามแหล่งเงินของเอกสาร/เงินสดตั้งต้นตามเดิม · ตั้งบัญชีที่หน้า “บัญชีธนาคาร”"
            : "บริษัทมีบัญชีรับโอนหลายบัญชีและไม่มีหลักฐานว่าเข้าบัญชีไหน — ระบบไม่เดา · เลือกบัญชีที่เห็นเงินเข้าจริง (ไม่เลือก = ลงตามแหล่งเงินของเอกสาร/เงินสดตั้งต้นตามเดิม)");
    }

    /// <summary>ผลตัดสินของการยืนยันหนึ่งครั้ง · <c>Problem</c> มีค่า = ปฏิเสธ (ไม่ยืนยัน) · <c>BankAccountId</c> = ค่าที่ handler ส่งเป็น override ·
    /// <c>AcceptedSuggestion</c> = ส่งบัญชีเดียวกับค่าแนะนำมา (ที่มาคงเป็นของค่าแนะนำ — ฝ่ายค้าน L2)</summary>
    public sealed record Decision(Guid? BankAccountId, Origin Origin, string Reason, string? Problem, Suggestion Suggested, bool AcceptedSuggestion = false)
    {
        public bool Ok => Problem is null;
        /// <summary>พนักงานเลือกต่างจากค่าแนะนำ (ลง audit)</summary>
        public bool Overridden => Origin == Origin.Chosen && BankAccountId != (Suggested.DisplayBankAccountId ?? Suggested.BankAccountId);
    }

    /// <summary>**ตัดสิน (pure)** — <paramref name="chosen"/> null/ว่าง = ใช้ค่าแนะนำ · มีค่า = ตรวจด้วย <see cref="ChoiceProblem"/></summary>
    public static Decision Decide(bool applies, Guid? chosen, AccountFacts? chosenFacts, string intentCurrency, Suggestion suggested)
    {
        if (chosen is not Guid c || c == Guid.Empty)
            // ไม่เลือก + แหล่งเงินของเอกสารใช้ไม่ได้ ⇒ ปฏิเสธพร้อมเหตุ (ยืนยันไปก็ "สำเร็จแต่ไม่บันทึกรับชำระ" — ฝ่ายค้าน M4)
            return new Decision(suggested.BankAccountId, suggested.Origin, suggested.Reason, suggested.RequiresChoice ? suggested.Reason : null, suggested);
        var problem = ChoiceProblem(applies, chosenFacts, intentCurrency);
        if (problem != null) return new Decision(null, Origin.Chosen, problem, problem, suggested);
        // เลือกบัญชีเดียวกับค่าแนะนำ ⇒ คงที่มาของค่าแนะนำ (ฝ่ายค้าน L2) · แหล่งเงินของเอกสารเป็นธนาคารเอง ⇒ ไม่ส่ง override (ผลเท่าเดิมทุกประการ) ·
        // ผังตรงที่ผูกธนาคาร ⇒ ส่งธนาคารนั้น (ฝ่ายค้าน M1 — ยอดธนาคาร/Payment.BankAccountId ต้องขยับ)
        if (suggested.DisplayBankAccountId == c || suggested.BankAccountId == c)
            return new Decision(suggested.InheritsBank ? null : c, suggested.Origin, suggested.Reason, null, suggested, AcceptedSuggestion: true);
        return new Decision(c, Origin.Chosen, "พนักงานเลือกบัญชีที่เงินเข้า", null, suggested);
    }

    /// <summary>ป้ายไทยของที่มา (หน้ารายการ/ประวัติ) — ค่าที่ไม่รู้จัก/ว่าง = null</summary>
    public static string? OriginLabel(string? origin) => Enum.TryParse<Origin>(origin, out var o) ? o switch
    {
        Origin.Chosen => "พนักงานเลือก",
        Origin.DocumentChannel => "ตามแหล่งเงินของเอกสาร",
        Origin.ShownToCustomer => "บัญชีที่แสดงให้ลูกค้าโอน",
        Origin.SoleTransferAccount => "บัญชีรับโอนบัญชีเดียว",
        Origin.None => "ตามแหล่งเงินของเอกสาร/เงินสดตั้งต้น (ไม่มีคำแนะนำ)",
        Origin.NotApplicable => "บัญชีพักของผู้ให้บริการ",
        _ => null,
    } : null;

    /// <summary>ค่าที่ประทับไว้บนรายการ ⇒ ผลตัดสินที่ handler ใช้ (null = ยังไม่ประทับ ⇒ ผู้เรียกคำนวณค่าแนะนำ)</summary>
    internal static (Guid? BankAccountId, Origin Origin)? StampedDecision(Guid? receivingBankAccountId, string? receivingAccountOrigin)
        => Enum.TryParse<Origin>(receivingAccountOrigin, out var o) ? (receivingBankAccountId, o) : null;

    /// <summary>เลขบัญชีเทียบเฉพาะตัวเลข (ขีด/ช่องว่างไม่นับ) — บัญชีรับโอนของเว็บเก็บเลขเป็นข้อความ ไม่ผูก id · ฝ่ายค้าน L4: ทั้งสองฝั่งมีชื่อธนาคาร ⇒ ต้องเข้ากันด้วย
    /// (<see cref="SameBank"/>) · ซ้ำ = ไม่เดา</summary>
    internal static Guid? MatchByAccountNumber(string? number, string? bankName, IEnumerable<(Guid Id, string? AccountNumber, string? BankName)> accounts)
    {
        var want = new string((number ?? "").Where(char.IsDigit).ToArray());
        if (want.Length < 6) return null;
        var hits = accounts.Where(a => new string((a.AccountNumber ?? "").Where(char.IsDigit).ToArray()) == want && SameBank(bankName, a.BankName))
            .Select(a => a.Id).Distinct().ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>ชื่อธนาคารสองแบบเข้ากันไหม — ว่างฝั่งใดฝั่งหนึ่ง = ไม่ตัดสิน (true) · ตัดคำ "ธนาคาร"/ช่องว่าง แล้วชื่อหนึ่งอยู่ในอีกชื่อ หรือรหัสในวงเล็บ (เช่น KBANK) ตรงกัน</summary>
    internal static bool SameBank(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return true;
        static string Code(string s) { var m = System.Text.RegularExpressions.Regex.Match(s, @"\(([A-Za-z]{2,10})\)"); return m.Success ? m.Groups[1].Value.ToUpperInvariant() : ""; }
        static string Core(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\([^)]*\)", "").Replace("ธนาคาร", "").Replace("จำกัด", "").Replace("(มหาชน)", "")
            .Replace(" ", "").Trim().ToLowerInvariant();
        var (ca, cb) = (Code(a), Code(b));
        if (ca.Length > 0 && cb.Length > 0) return ca == cb;
        if ((ca.Length > 0 && b.ToUpperInvariant().Contains(ca)) || (cb.Length > 0 && a.ToUpperInvariant().Contains(cb))) return true;
        var (xa, xb) = (Core(a), Core(b));
        if (xa.Length == 0 || xb.Length == 0) return true;
        return xa.Contains(xb) || xb.Contains(xa);
    }

    // ═══════════════ ตัวโหลด (ทุก query กรอง CompanyId) ═══════════════

    /// <summary>ตัวเลือก + ค่าแนะนำ + ผลตัดสินของรายการหนึ่ง · <paramref name="attachmentsRoot"/> = ที่เก็บไฟล์ (ตรวจรูป QR ของบัญชี — null ⇒ นับเฉพาะพร้อมเพย์)</summary>
    public sealed record Resolution(bool Applies, IReadOnlyList<AccountFacts> Accounts, IReadOnlyList<AccountFacts> Usable, Decision Decision);

    public static async Task<Resolution> ResolveAsync(AccountingDbContext db, string? attachmentsRoot, PaymentIntent intent, Guid? chosen,
        CancellationToken ct = default)
    {
        var companyId = intent.CompanyId;
        var applies = Applies(intent.ProviderCode, intent.MethodKind);
        var accounts = await LoadAccountsAsync(db, companyId, ct);
        var usable = accounts.Where(a => UsableProblem(a, intent.Currency) == null).ToList();
        var usableIds = usable.Select(a => a.Id).ToList();

        var docChannel = DocChannel.None;
        Guid? shown = null;
        var siteRow = SiteRowMatch.NoRow;
        if (applies && intent.SourceKind == PaymentSourceKind.Document)
        {
            var doc = await db.Documents.AsNoTracking().Include(d => d.Brand)
                .FirstOrDefaultAsync(d => d.Id == intent.SourceId && d.CompanyId == companyId, ct);
            if (doc != null)
            {
                var channel = DocumentMoneyChannel.ForPayment(null, null, doc.BankAccountId, doc.PaymentAccountId);
                if (channel.BankAccountId != null || channel.PaymentAccountId != null)
                {
                    // ตัวตรวจเดียวกับตอนลงบัญชี (Post) — ใช้ไม่ได้ ⇒ ไม่แนะนำ (ฝ่ายค้าน M4)
                    var check = await DocumentMoneyChannel.EvaluateAsync(db, companyId, channel.BankAccountId, channel.PaymentAccountId,
                        MoneyChannelCheck.Post, DocumentMoneyChannel.DocumentFixHint, ct);
                    var bank = channel.BankAccountId;
                    string? problem = check.Problem;
                    if (bank is Guid ownBank && problem == null && accounts.FirstOrDefault(a => a.Id == ownBank) is { } facts)
                        problem = UsableProblem(facts, intent.Currency);
                    if (bank == null && channel.PaymentAccountId is Guid gl)
                    {
                        var linked = await db.BankAccounts.AsNoTracking()
                            .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive && b.LinkedAccountId == gl)
                            .Select(b => b.Id).ToListAsync(ct);
                        if (linked.Count == 1) bank = linked[0];
                    }
                    docChannel = new DocChannel(true, channel.BankAccountId != null, bank, problem);
                }
                else
                    shown = await Accounting.Services.Implementations.PaymentQrService.ShownDocumentAccountIdAsync(db, attachmentsRoot, companyId, doc, ct);
            }
            // กล่องโอนของหน้าชำระ/พอร์ทัลใช้บัญชีรับโอนของเว็บเมื่อใบไม่มีบัญชีที่แสดง (DocumentPayLinkService.TransferBlockAsync — ลำดับเดียวกัน)
            if (!docChannel.HasChannel && shown == null)
                (siteRow, shown) = await SiteTransferMatchAsync(db, companyId, null, ct);
        }
        else if (applies && intent.SourceKind == PaymentSourceKind.SiteOrder)
        {
            // รอบ 268 (268-1-2): สลิปของออเดอร์หน้าเว็บผ่านคิวรายการชำระ ⇒ บัญชีรับโอนของเว็บนั้น (แถวเดียวกับที่ลูกค้าเห็น) → บัญชีเดียว · เดิมไม่มีชั้นนี้
            var siteId = intent.SiteId ?? await db.SiteOrders.AsNoTracking()
                .Where(o => o.Id == intent.SourceId && o.CompanyId == companyId).Select(o => (Guid?)o.SiteId).FirstOrDefaultAsync(ct);
            (siteRow, shown) = await SiteTransferMatchAsync(db, companyId, siteId, ct);
        }
        else if (applies && intent.SourceKind == PaymentSourceKind.SiteBooking)
        {
            var siteId = intent.SiteId ?? await db.SiteBookings.AsNoTracking()
                .Where(b => b.Id == intent.SourceId && b.CompanyId == companyId).Select(b => (Guid?)b.SiteId).FirstOrDefaultAsync(ct);
            (siteRow, shown) = await SiteTransferMatchAsync(db, companyId, siteId, ct);
        }

        var suggestion = Suggest(applies, docChannel, shown, siteRow, usableIds);
        var chosenFacts = chosen is Guid c ? accounts.FirstOrDefault(a => a.Id == c) : null;
        return new Resolution(applies, accounts, usable, Decide(applies, chosen, chosenFacts, intent.Currency, suggestion));
    }

    /// <summary>ผลที่ handler ใช้ — ค่าที่ประทับบนรายการชนะ (ตัดสินแล้วตอนกดยืนยัน) · ยังไม่ประทับ (ส่งต่อใหม่ของรายการก่อนรอบนี้ · ทางเข้าที่เลือกไม่ได้) ⇒ ค่าแนะนำตัวเดียวกัน ·
    /// ไม่ใช้กับรายการ ⇒ null (ผู้เรียกใช้ผังของ IGatewayAccountResolver ตามเดิม)</summary>
    public static async Task<Guid?> OverrideForHandlerAsync(AccountingDbContext db, string? attachmentsRoot, PaymentIntent intent, CancellationToken ct = default)
    {
        if (!Applies(intent.ProviderCode, intent.MethodKind)) return null;
        if (StampedDecision(intent.ReceivingBankAccountId, intent.ReceivingAccountOrigin) is { } s) return s.BankAccountId;
        return (await ResolveAsync(db, attachmentsRoot, intent, null, ct)).Decision.BankAccountId;
    }

    // ═══════════════ รอบ 268 (268-1-2): ยืนยันสลิปของออเดอร์หน้าเว็บที่หน้า "คำสั่งซื้อ" (ไม่มีรายการชำระ PaymentIntent) ═══════════════

    /// <summary>แถวรับเงินของออเดอร์หน้าเว็บเป็น "สลิปโอน" ไหม (pure) — ไม่มีรหัสธุรกรรมของผู้ให้บริการ และ (แนบสลิป หรือวิธีโอน/พร้อมเพย์) ·
    /// เงินผ่านผู้ให้บริการ (มีรหัสธุรกรรม) = บัญชีพักของ <c>IGatewayAccountResolver</c> ตามเดิม</summary>
    public static bool AppliesToSiteOrderPayment(bool hasSlip, bool hasGatewayTransaction, PaymentMethod method)
        => !hasGatewayTransaction && (hasSlip || method is PaymentMethod.BankTransfer or PaymentMethod.PromptPay);

    /// <summary>ตัวเลือก + ค่าแนะนำ + ผลตัดสินของแถวรับเงินของออเดอร์หน้าเว็บ (ลำดับเดียวกับ <see cref="Suggest"/>: ใบขายของออเดอร์สร้างโดยไม่ตั้งแหล่งเงิน ⇒
    /// บัญชีรับโอนของเว็บนั้น → บัญชีรับโอนบัญชีเดียว → ไม่เดา) · ทุก query ผูก <paramref name="companyId"/></summary>
    public static async Task<Resolution> ResolveForSiteOrderAsync(AccountingDbContext db, Guid companyId, Guid siteId, bool applies, string currency,
        Guid? chosen, CancellationToken ct = default)
    {
        var accounts = await LoadAccountsAsync(db, companyId, ct);
        var usable = accounts.Where(a => UsableProblem(a, currency) == null).ToList();
        var (siteRow, shown) = applies ? await SiteTransferMatchAsync(db, companyId, siteId, ct) : (SiteRowMatch.NoRow, (Guid?)null);
        var suggestion = Suggest(applies, DocChannel.None, shown, siteRow, usable.Select(a => a.Id).ToList());
        var chosenFacts = chosen is Guid c ? accounts.FirstOrDefault(a => a.Id == c) : null;
        return new Resolution(applies, accounts, usable, Decide(applies, chosen, chosenFacts, currency, suggestion));
    }

    private static async Task<List<AccountFacts>> LoadAccountsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct)
    {
        var rows = await db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted)
            .OrderBy(b => b.BankName).ThenBy(b => b.AccountNumber).ThenBy(b => b.Id)
            .Select(b => new { b.Id, b.AccountName, b.BankName, b.AccountNumber, b.IsActive, b.Currency, b.AccountType, b.PromptPayId })
            .ToListAsync(ct);
        var list = new List<AccountFacts>(rows.Count);
        foreach (var b in rows)
        {
            var gl = await DocumentMoneyChannel.EvaluateAsync(db, companyId, b.Id, null, MoneyChannelCheck.Post,
                "แก้ที่หน้า “บัญชีธนาคาร” หรือเลือกบัญชีอื่น", ct);
            list.Add(new AccountFacts(b.Id, $"{b.BankName} {b.AccountNumber} ({b.AccountName})".Trim(), b.IsActive, b.Currency ?? "THB",
                b.AccountType ?? "", !string.IsNullOrWhiteSpace(b.AccountNumber) || !string.IsNullOrWhiteSpace(b.PromptPayId), gl.Problem));
        }
        return list;
    }

    /// <summary>บัญชีรับโอนของเว็บแถวแรก (ตัวอ่านเดียว <see cref="SiteTransferAccount.Rows"/> — แถวเดียวกับที่ลูกค้าเห็น) ⇒ บัญชีธนาคารที่เลขตรง (ซ้ำ = ไม่เดา) ·
    /// คืนสามสถานะ: ไม่มีแถว (ไม่มีเลขบัญชี) · ตรง · มีแถวแต่ไม่ตรง (ฝ่ายค้าน M3 — ห้ามตกไป "บัญชีเดียว")</summary>
    private static async Task<(SiteRowMatch Match, Guid? Id)> SiteTransferMatchAsync(AccountingDbContext db, Guid companyId, Guid? siteId, CancellationToken ct)
    {
        var gw = await SiteTransferAccount.Rows(db.Set<SitePaymentGateway>().AsNoTracking(), companyId, siteId)
            .Select(g => new { g.BankAccountNumber, g.BankName, g.PromptPayId }).FirstOrDefaultAsync(ct);
        if (gw == null || (string.IsNullOrWhiteSpace(gw.BankAccountNumber) && string.IsNullOrWhiteSpace(gw.PromptPayId))) return (SiteRowMatch.NoRow, null);
        var banks = await db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive)
            .Select(b => new { b.Id, b.AccountNumber, b.BankName, b.PromptPayId }).ToListAsync(ct);
        var hit = MatchByAccountNumber(gw.BankAccountNumber, gw.BankName, banks.Select(b => (b.Id, (string?)b.AccountNumber, (string?)b.BankName)))
            ?? MatchByAccountNumber(gw.PromptPayId, null, banks.Select(b => (b.Id, b.PromptPayId, (string?)null)));
        return hit is Guid id ? (SiteRowMatch.Matched, (Guid?)id) : (SiteRowMatch.Unmatched, (Guid?)null);
    }
}
