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

    /// <summary>ค่าแนะนำ · <c>BankAccountId</c> = ค่าที่ส่งเป็น <c>OverrideBankAccountId</c> (null = ไม่ส่ง ⇒ สืบทอดแหล่งเงินของเอกสาร/เงินสดตั้งต้น) ·
    /// <c>DisplayBankAccountId</c> = บัญชีที่หน้าแสดงเป็นค่าตั้งต้นของช่อง (แหล่งเงินของเอกสารที่เป็นธนาคาร — ไม่ส่งเป็น override เพื่อให้ผลเท่าเดิมทุกประการ)</summary>
    public sealed record Suggestion(Guid? BankAccountId, Guid? DisplayBankAccountId, Origin Origin, string Reason);

    /// <summary>
    /// **ค่าแนะนำ (pure)** — (หลักฐานจากสลิปเอง เช่นบัญชีผู้รับใน QR ท้ายสลิป ยังไม่มีตัวอ่าน ⇒ ไม่มีชั้นนี้จนกว่าจะอ่านได้จริง · ห้ามแต่ง — F2 ข้อ 3) ·
    /// <paramref name="documentHasChannel"/> = เอกสารตั้งแหล่งเงินไว้ (ผังตรงหรือธนาคาร) · <paramref name="documentChannelBank"/> = บัญชีธนาคารของแหล่งเงินนั้น (ถ้ามี) ·
    /// <paramref name="shownToCustomer"/> = บัญชีที่ระบบแสดงให้ลูกค้าโอน · <paramref name="usable"/> = บัญชีที่ผ่าน <see cref="UsableProblem"/> ทั้งหมดของบริษัท
    /// </summary>
    public static Suggestion Suggest(bool applies, bool documentHasChannel, Guid? documentChannelBank,
        Guid? shownToCustomer, IReadOnlyCollection<Guid> usable)
    {
        if (!applies)
            return new Suggestion(null, null, Origin.NotApplicable, "เงินผ่านผู้ให้บริการรับชำระ — ลงบัญชีพักของผู้ให้บริการ (รอรอบโอน)");
        if (documentHasChannel)
            return new Suggestion(null, documentChannelBank is Guid d && usable.Contains(d) ? d : null, Origin.DocumentChannel,
                "ตามแหล่งเงินที่ตั้งไว้บนเอกสาร (เหมือนเดิม)");
        if (shownToCustomer is Guid shown && usable.Contains(shown))
            return new Suggestion(shown, shown, Origin.ShownToCustomer, "บัญชีที่ระบบแสดงให้ลูกค้าโอน (QR/กล่องโอนของเอกสาร หรือบัญชีรับโอนของเว็บ)");
        if (usable.Count == 1)
        {
            var only = usable.First();
            return new Suggestion(only, only, Origin.SoleTransferAccount, "บริษัทมีบัญชีรับโอนที่ใช้ได้บัญชีเดียว");
        }
        return new Suggestion(null, null, Origin.None, usable.Count == 0
            ? "ยังไม่มีบัญชีธนาคารที่รับโอนได้ (ผูกผังบัญชี + มีเลขบัญชี/พร้อมเพย์) — ระบบลงตามแหล่งเงินของเอกสาร/เงินสดตั้งต้นตามเดิม · ตั้งบัญชีที่หน้า “บัญชีธนาคาร”"
            : "บริษัทมีบัญชีรับโอนหลายบัญชีและไม่มีหลักฐานว่าเข้าบัญชีไหน — ระบบไม่เดา · เลือกบัญชีที่เห็นเงินเข้าจริง (ไม่เลือก = ลงตามแหล่งเงินของเอกสาร/เงินสดตั้งต้นตามเดิม)");
    }

    /// <summary>ผลตัดสินของการยืนยันหนึ่งครั้ง · <c>Problem</c> มีค่า = ปฏิเสธ (ไม่ยืนยัน) · <c>BankAccountId</c> = ค่าที่ handler ส่งเป็น override</summary>
    public sealed record Decision(Guid? BankAccountId, Origin Origin, string Reason, string? Problem, Suggestion Suggested)
    {
        public bool Ok => Problem is null;
        /// <summary>พนักงานเลือกต่างจากค่าแนะนำ (ลง audit)</summary>
        public bool Overridden => Origin == Origin.Chosen && BankAccountId != (Suggested.DisplayBankAccountId ?? Suggested.BankAccountId);
    }

    /// <summary>**ตัดสิน (pure)** — <paramref name="chosen"/> null/ว่าง = ใช้ค่าแนะนำ · มีค่า = ตรวจด้วย <see cref="ChoiceProblem"/></summary>
    public static Decision Decide(bool applies, Guid? chosen, AccountFacts? chosenFacts, string intentCurrency, Suggestion suggested)
    {
        if (chosen is not Guid c || c == Guid.Empty)
            return new Decision(suggested.BankAccountId, suggested.Origin, suggested.Reason, null, suggested);
        var problem = ChoiceProblem(applies, chosenFacts, intentCurrency);
        if (problem != null) return new Decision(null, Origin.Chosen, problem, problem, suggested);
        // เลือกตรงกับแหล่งเงินธนาคารของเอกสาร ⇒ ไม่ต้องส่ง override (ผลเท่าเดิมทุกประการ — ไม่ขยับ OverrideBankAccountId ของแถวการชำระ)
        if (suggested.Origin == Origin.DocumentChannel && suggested.DisplayBankAccountId == c)
            return new Decision(null, Origin.DocumentChannel, suggested.Reason, null, suggested);
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

    /// <summary>เลขบัญชีเทียบเฉพาะตัวเลข (ขีด/ช่องว่างไม่นับ) — บัญชีรับโอนของเว็บเก็บเลขเป็นข้อความ ไม่ผูก id</summary>
    internal static Guid? MatchByAccountNumber(string? number, IEnumerable<(Guid Id, string? AccountNumber)> accounts)
    {
        var want = new string((number ?? "").Where(char.IsDigit).ToArray());
        if (want.Length < 6) return null;
        var hits = accounts.Where(a => new string((a.AccountNumber ?? "").Where(char.IsDigit).ToArray()) == want).Select(a => a.Id).Distinct().ToList();
        return hits.Count == 1 ? hits[0] : null;   // ซ้ำ = ไม่เดา
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

        var docHasChannel = false;
        Guid? docBank = null;
        Guid? shown = null;
        if (applies && intent.SourceKind == PaymentSourceKind.Document)
        {
            var doc = await db.Documents.AsNoTracking().Include(d => d.Brand)
                .FirstOrDefaultAsync(d => d.Id == intent.SourceId && d.CompanyId == companyId, ct);
            if (doc != null)
            {
                var channel = DocumentMoneyChannel.ForPayment(null, null, doc.BankAccountId, doc.PaymentAccountId);
                docHasChannel = channel.BankAccountId != null || channel.PaymentAccountId != null;
                docBank = channel.BankAccountId;
                if (docBank == null && channel.PaymentAccountId is Guid gl)
                {
                    var linked = await db.BankAccounts.AsNoTracking()
                        .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive && b.LinkedAccountId == gl)
                        .Select(b => b.Id).ToListAsync(ct);
                    if (linked.Count == 1) docBank = linked[0];
                }
                if (!docHasChannel)
                    shown = await Accounting.Services.Implementations.PaymentQrService.ShownDocumentAccountIdAsync(db, attachmentsRoot, companyId, doc, ct);
            }
            shown ??= await SiteTransferMatchAsync(db, companyId, null, ct);
        }
        else if (applies && intent.SourceKind == PaymentSourceKind.SiteBooking)
        {
            var siteId = intent.SiteId ?? await db.SiteBookings.AsNoTracking()
                .Where(b => b.Id == intent.SourceId && b.CompanyId == companyId).Select(b => (Guid?)b.SiteId).FirstOrDefaultAsync(ct);
            shown = await SiteTransferMatchAsync(db, companyId, siteId, ct);
        }

        var suggestion = Suggest(applies, docHasChannel, docBank, shown, usableIds);
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

    /// <summary>บัญชีรับโอนของเว็บแถวแรก (ตัวอ่านเดียว <see cref="SiteTransferAccount.Rows"/> — แถวเดียวกับที่ลูกค้าเห็น) ⇒ บัญชีธนาคารที่เลขตรง (ซ้ำ = ไม่เดา)</summary>
    private static async Task<Guid?> SiteTransferMatchAsync(AccountingDbContext db, Guid companyId, Guid? siteId, CancellationToken ct)
    {
        var gw = await SiteTransferAccount.Rows(db.Set<SitePaymentGateway>().AsNoTracking(), companyId, siteId)
            .Select(g => g.BankAccountNumber).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(gw)) return null;
        var banks = await db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive)
            .Select(b => new { b.Id, b.AccountNumber }).ToListAsync(ct);
        return MatchByAccountNumber(gw, banks.Select(b => (b.Id, (string?)b.AccountNumber)));
    }
}
