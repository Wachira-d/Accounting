using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Constants;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **ความยินยอม "สำนักงานบัญชี NextAcc ทำแทนบริษัทนี้" — ตัวตัดสินเดียว** (pure · รอบ 272 แพ็ก P2 · ดีไซน์ §5 · คำตัดสินเจ้าของ 272-0-2 ·
/// ผู้ประสานงาน 272-1-6/7/9 + ทาง B) · คู่แฝดของ <see cref="PartnerConsent"/> (ใช้ซ้ำ <see cref="PartnerConsent.Need"/> · <see cref="PartnerConsent.KeyOf"/> ·
/// <see cref="PartnerConsent.ConfirmPhrase"/> · <see cref="PartnerConsent.TruncateToSecond"/>) แต่ผูก (บริษัทลูกค้า × บริษัทผู้ให้บริการ) แทน TradingPartnership —
/// ไม่ลากผลข้างเคียงของการยอมรับคู่ค้า (ดีไซน์ §5.8 ทาง ii)
/// <list type="bullet">
/// <item>ผู้ให้ (272-1-7 = D-7): <b>เจ้าของ</b>ของบริษัทลูกค้า หรือผู้มีทั้ง <c>Tax.File</c> และ <c>CompanySettings.Edit</c> · คนเดียวพอ ·
/// ห้ามเป็นสมาชิกของบริษัทผู้ให้บริการเอง (ผู้ขอให้เองไม่ได้ — ผลประโยชน์ขัดกัน)</item>
/// <item>ช่องทาง (272-1-9 = D-9): ยืนยันบน<b>เว็บ</b>หลังล็อกอินเท่านั้น (พิมพ์ "ยินยอม") — ไม่มีปุ่มยินยอมในแชต</item>
/// <item>หนังสือมอบอำนาจที่พนักงานแนบ (272-1-6 = D-6): ได้สถานะ "มีหนังสือมอบอำนาจ" — <b>ไม่</b>เปิดการบันทึกเอง (ทุกสลิป = 1 คลิกของพนักงาน)</item>
/// <item>append-only + hash canonical ตัวเดียว (<see cref="Hash"/> · prefix <c>PRC1</c>) · ถอนได้ทุกเมื่อ (ประทับครั้งเดียว · trigger ใน <c>Round272Migration</c>)</item>
/// </list>
/// </summary>
public static class ProviderConsent
{
    public const string RuleCode = "PRV-CONSENT";
    public const string LegalReference = "พ.ร.บ.การบัญชี 2543 ม.7/ม.19 (ความรับผิดชอบการลงบัญชีอยู่กับบริษัทผู้ให้ความยินยอม) · PDPA ม.24(3)/ม.24(6)/ม.39";
    public const string Scope = "TaxSlipRemit";
    public const int CurrentTextVersion = 1;
    public const int MinTextVersion = 1;
    public const int HashVersion = 1;
    public const string ConfirmPhrase = PartnerConsent.ConfirmPhrase;
    public const string SourceWeb = "web";
    public const string SourcePowerOfAttorney = "staff-poa";
    /// <summary>คำขอ/สลิปที่พักรอความยินยอม หมดอายุ (วัน)</summary>
    public const int RequestLifetimeDays = 30;
    public const string PageAnchor = "#provider-consent";

    public static string PageUrl(Guid clientCompanyId) => ProviderCentralOa.BillingPath(clientCompanyId) + PageAnchor;

    // ═══ รอบ 279 (คำตัดสิน 279-2/279-3): ทางไปต่อที่ชี้เมนูจริง + สถานะความยินยอมสำหรับการ์ดสถานะกลุ่ม/หน้าผู้ติดต่อ ═══

    /// <summary>เมนูจริงของการ์ดความยินยอมฝั่งบริษัทลูกค้า (layout.js: "แพ็กเกจและการชำระเงิน" · แท็บ "ใบแจ้งหนี้ &amp; ใบเสร็จ" · billing.html การ์ด #provider-consent) —
    /// เดิมการ์ดบอทชี้เมนูชื่อ "ค่าบริการ" ซึ่งไม่มีในระบบ · เทสต์ล็อกกับไฟล์หน้าเว็บ</summary>
    public const string ClientMenuPath = "แพ็กเกจและการชำระเงิน › ใบแจ้งหนี้ & ใบเสร็จ › สำนักงานบัญชี NextAcc ทำแทนบริษัทนี้";
    /// <summary>เมนูจริงของปุ่มส่งคำขอฝั่งพนักงานบริษัทผู้ให้บริการ (tax-remittance.html ปุ่ม "สลิปภาษีของลูกค้า" → tax-slip-desk.html ปุ่ม "ส่งคำขออนุญาตอีกครั้ง")</summary>
    public const string StaffRequestMenuPath = "นำส่งภาษี / ประกันสังคม › สลิปภาษีของลูกค้า › เลือกบริษัท › ส่งคำขออนุญาตอีกครั้ง";
    public const string StaffDeskPage = "/pages/tax-slip-desk.html";

    /// <summary>ลิงก์เต็ม (ตั้ง URL ของระบบแล้ว) หรือทางในเมนู (ยังไม่ตั้ง — ไม่เดาโดเมน) — ตัวประกอบเดียวของทุกการ์ด/ข้อความที่ชี้ไปหน้ายืนยัน</summary>
    public static string GrantLinkOrMenu(string? baseUrl, Guid clientCompanyId)
        => string.IsNullOrWhiteSpace(baseUrl) ? "อนุญาตได้ที่เมนู " + ClientMenuPath : baseUrl.TrimEnd('/') + PageUrl(clientCompanyId);

    /// <summary>รอบ 280 (ฝ่ายค้าน LOW-2 ของการ์ด 279): ลิงก์ที่<b>โพสต์เข้ากลุ่ม LINE</b> ห้ามมี id บริษัทลูกค้า (GUID ภายในของผู้เช่า — สมาชิกกลุ่มทุกคนเห็น) ⇒
    /// หน้าเดียวกันแบบไม่มีพารามิเตอร์ (หน้าเลือกบริษัทของผู้ใช้เองหลังเข้าสู่ระบบ) · ไม่ตั้ง URL ของระบบ ⇒ ทางในเมนู · ลิงก์ที่มี id ใช้ได้เฉพาะหน้าพนักงาน/การแจ้งส่วนตัว</summary>
    public const string GroupPagePath = "/pages/billing.html" + PageAnchor;

    public static string GroupGrantLinkOrMenu(string? baseUrl)
        => string.IsNullOrWhiteSpace(baseUrl) ? "อนุญาตได้ที่เมนู " + ClientMenuPath : baseUrl.TrimEnd('/') + GroupPagePath;

    /// <summary>สถานะความยินยอมของบริษัทลูกค้าหนึ่งราย (มุมกลุ่ม LINE/หน้าผู้ติดต่อ)</summary>
    public enum GroupConsentState { Granted, PowerOfAttorney, Pending, NotRequested }

    /// <summary>ตัวจำแนกเดียว: อนุญาตแล้ว (ตัวตรวจผ่าน) · มีหนังสือมอบอำนาจ (ไม่เปิดการบันทึกเอง) · รอยืนยัน (มีคำขอเปิดที่ยังไม่หมดอายุ) · ยังไม่ได้ขอ</summary>
    public static GroupConsentState Classify(Verdict verdict, bool openRequest)
        => verdict.Ok ? GroupConsentState.Granted
            : verdict.Code is Codes.PowerOfAttorneyOnly or Codes.PowerOfAttorneyPendingApproval ? GroupConsentState.PowerOfAttorney
            : openRequest ? GroupConsentState.Pending
            : GroupConsentState.NotRequested;

    public static string StateLabel(GroupConsentState s) => s switch
    {
        GroupConsentState.Granted => "ยินยอมแล้ว ✅",
        GroupConsentState.PowerOfAttorney => "มีหนังสือมอบอำนาจ (พนักงานบันทึกทีละใบ)",
        GroupConsentState.Pending => "ส่งคำขอแล้ว — รอเจ้าของบริษัทยืนยันบนเว็บ",
        _ => "ยังไม่ได้ขอ",
    };

    /// <summary>ต้องบอกทางยืนยันของลูกค้า/ทางส่งคำขอของพนักงานไหม (ยินยอมแล้ว ⇒ ไม่ต้อง)</summary>
    public static bool NeedsClientAction(GroupConsentState s) => s != GroupConsentState.Granted;

    /// <summary>แบบที่ครอบได้ · <paramref name="ReaderWired"/> = ระบบอ่านสลิปของแบบนี้ได้แล้ว (สปส. ยังไม่ — รูปแบบสลิปยังไม่ยืนยัน · ห้าม silent no-op)</summary>
    public sealed record TaxTypeDef(string Type, string Label, bool ReaderWired);

    public static IReadOnlyList<TaxTypeDef> TaxTypes { get; } = new List<TaxTypeDef>
    {
        new("VatPp30", "ภ.พ.30", true), new("VatPp36", "ภ.พ.36", true), new("WhtPnd1", "ภ.ง.ด.1", true), new("WhtPnd2", "ภ.ง.ด.2", true),
        new("WhtPnd3", "ภ.ง.ด.3", true), new("WhtPnd53", "ภ.ง.ด.53", true), new("WhtPnd54", "ภ.ง.ด.54", true),
        new("Pnd51", "ภ.ง.ด.51", true), new("Pnd50", "ภ.ง.ด.50", true),
        new("SsoSps110", "สปส.1-10 (ยังไม่รองรับ — รูปแบบสลิปยังไม่ยืนยัน)", false),
    };

    public static string LabelOf(string type) => TaxTypes.FirstOrDefault(t => t.Type == type)?.Label ?? type;

    /// <summary>ชุดแบบที่ติ๊ก ⇒ รูป canonical (เรียง · ไม่ซ้ำ · เฉพาะที่รู้จักและอ่านได้แล้ว) · มีชื่อที่ไม่รู้จัก/ยังไม่รองรับ ⇒ problem</summary>
    public static (string Csv, string? Problem) NormalizeTypes(IEnumerable<string>? types)
    {
        var list = (types ?? Array.Empty<string>()).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (list.Count == 0) return ("", "เลือกอย่างน้อยหนึ่งแบบภาษี");
        foreach (var t in list)
        {
            var def = TaxTypes.FirstOrDefault(d => d.Type == t);
            if (def == null) return ("", "ไม่รู้จักแบบภาษี " + t);
            if (!def.ReaderWired) return ("", def.Label + " — ยังให้ความยินยอมไม่ได้");
        }
        return (string.Join(",", list.OrderBy(x => x, StringComparer.Ordinal)), null);
    }

    public static IReadOnlyList<string> ParseTypes(string? csv)
        => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly Dictionary<int, string> Texts = new()
    {
        [1] = "ข้าพเจ้าในนามบริษัทอนุญาตให้ระบบของสำนักงานบัญชี NextAcc บันทึกการนำส่งภาษีของบริษัทนี้จากสลิปที่ส่งในกลุ่ม LINE/อีเมลที่ผูกกับบริษัทนี้ "
            + "เฉพาะแบบที่เลือก · ระบบจะบันทึกเองเฉพาะเมื่อ เลขผู้เสียภาษีบนสลิปตรงบริษัทนี้ · ยอดตรงทุกสตางค์ · เลขควบคุม (Control Code) ตรงใบแจ้งชำระที่ NextAcc ส่ง "
            + "(หรือเหลือรายการค้างเดียวที่ยอดและช่วงยื่นตรง) · ไม่ซ้ำ · บัญชีที่จ่ายตัดสินได้ ⇒ ลงบัญชี Dr ภาษีค้างจ่าย / Cr ธนาคาร + แนบสลิป · ไม่ตรงข้อใด ⇒ "
            + "ไม่บันทึก ส่งให้พนักงาน NextAcc ตรวจ · ทุกครั้งที่บันทึก ระบบแจ้งผลในกลุ่มและกระดิ่งของบริษัทนี้ · ยกเลิกรายการได้ที่หน้านำส่งภาษี · "
            + "สลิปไม่ใช่เลขรับการยื่นแบบ (ระบบไม่ประทับว่ายื่นแล้ว) · ถอนได้ทุกเมื่อ (มีผลทันทีกับสลิปที่ยังไม่บันทึก) · "
            + "ความรับผิดชอบการลงบัญชียังเป็นของบริษัทนี้ตาม พ.ร.บ.การบัญชี 2543",
    };

    public static string? TextFor(int version) => Texts.TryGetValue(version, out var t) ? t : null;

    /// <summary>รูป canonical ของสิ่งที่ผู้ให้ยินยอม — ฟังก์ชันเดียวที่ทั้งขั้นดู · ขั้นยินยอม · ตัวตรวจแถว ใช้ (กฎ #4 C)</summary>
    public static string Canonical(Guid clientCompanyId, Guid providerCompanyId, string taxTypesCsv, decimal? maxAmount, Guid? payBankAccountId,
        bool includesHeld, int textVersion, string text, Guid grantedByUserId, DateTime grantedAtUtc)
        => string.Join("\n",
            "PRC1",
            clientCompanyId.ToString("D"),
            providerCompanyId.ToString("D"),
            Scope,
            taxTypesCsv,
            maxAmount is decimal m ? Math.Round(m, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture) : "-",
            payBankAccountId is Guid b ? b.ToString("D") : "-",
            includesHeld ? "held" : "-",
            grantedByUserId.ToString("D"),
            PartnerConsent.TruncateToSecond(grantedAtUtc).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            textVersion.ToString(CultureInfo.InvariantCulture),
            text);

    public static string Hash(Guid clientCompanyId, Guid providerCompanyId, string taxTypesCsv, decimal? maxAmount, Guid? payBankAccountId,
        bool includesHeld, int textVersion, string text, Guid grantedByUserId, DateTime grantedAtUtc)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(clientCompanyId, providerCompanyId, taxTypesCsv, maxAmount,
            payBankAccountId, includesHeld, textVersion, text, grantedByUserId, grantedAtUtc)))).ToLowerInvariant();

    /// <summary>"hash ที่ผู้ให้เห็น" ในขั้นดู (ยังไม่รู้เวลาให้) — ตรวจในขั้นยืนยันว่าเนื้อที่เห็นตรงกับที่จะเก็บ</summary>
    public static string PreviewHash(Guid clientCompanyId, Guid providerCompanyId, string taxTypesCsv, decimal? maxAmount, Guid? payBankAccountId, bool includesHeld, Guid userId)
        => Hash(clientCompanyId, providerCompanyId, taxTypesCsv, maxAmount, payBankAccountId, includesHeld, CurrentTextVersion, TextFor(CurrentTextVersion)!, userId, DateTime.UnixEpoch);

    /// <summary>ฝ่ายค้าน 272 M4 — รุ่น 2 (<c>PRC2</c>): + แหล่งหลักฐาน + ไฟล์หลักฐาน (หนังสือมอบอำนาจ) ⇒ สลับไฟล์/แหล่งหลังบันทึก = hash ไม่ตรง · แถวหนังสือมอบอำนาจใช้รุ่นนี้เสมอ</summary>
    public const int HashVersionWithEvidence = 2;

    public static string HashV2(Guid clientCompanyId, Guid providerCompanyId, string taxTypesCsv, decimal? maxAmount, Guid? payBankAccountId,
        bool includesHeld, int textVersion, string text, Guid grantedByUserId, DateTime grantedAtUtc, string evidenceSource, Guid? evidenceAttachmentId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", "PRC2", evidenceSource, evidenceAttachmentId is Guid g ? g.ToString("D") : "-",
            Canonical(clientCompanyId, providerCompanyId, taxTypesCsv, maxAmount, payBankAccountId, includesHeld, textVersion, text, grantedByUserId, grantedAtUtc))))).ToLowerInvariant();

    public static bool HashMatches(ProviderAutomationConsent row)
    {
        var text = TextFor(row.TextVersion);
        if (text == null) return false;
        var expected = row.HashVersion switch
        {
            HashVersion when row.EvidenceSource == SourceWeb => Hash(row.CompanyId, row.ProviderCompanyId, row.TaxTypes, row.MaxAmount, row.DefaultTaxPayBankAccountId,
                row.IncludesHeldSlips, row.TextVersion, text, row.GrantedByUserId, row.GrantedAt),
            HashVersionWithEvidence => HashV2(row.CompanyId, row.ProviderCompanyId, row.TaxTypes, row.MaxAmount, row.DefaultTaxPayBankAccountId, row.IncludesHeldSlips,
                row.TextVersion, text, row.GrantedByUserId, row.GrantedAt, row.EvidenceSource, row.EvidenceAttachmentId),
            _ => null,
        };
        if (expected == null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(row.EvidenceHash ?? ""));
    }

    /// <summary>ผู้นี้ให้ความยินยอมได้ไหม (272-1-7) · null = ได้ · ข้อความปฏิเสธใช้ <see cref="PermissionKeys.DeniedMessage"/> ตัวเดียว</summary>
    /// <param name="isOwner">บทบาทเจ้าของในบริษัทลูกค้า</param>
    /// <param name="has">ด่านสิทธิ์จริงของผู้ให้ในบริษัทลูกค้า</param>
    /// <param name="isProviderMember">เป็นสมาชิกบริษัทผู้ให้บริการ (NextAcc) ด้วย ⇒ ให้ไม่ได้เสมอ</param>
    public static string? GrantProblem(bool isActiveUser, bool isOwner, Func<PartnerConsent.Need, bool> has, bool isProviderMember)
    {
        if (!isActiveUser) return "ผู้ใช้นี้ไม่ได้ใช้งานอยู่ — ให้ความยินยอมไม่ได้";
        if (isProviderMember) return "ผู้ใช้ที่เป็นพนักงานของสำนักงานบัญชีผู้ขอ ให้ความยินยอมแทนบริษัทลูกค้าไม่ได้ (ผู้ขอให้เองไม่ได้)";
        if (isOwner) return null;
        foreach (var n in new[] { PartnerConsent.Need.TaxFile, PartnerConsent.Need.CompanySettingsEdit })
            if (!has(n))
                return "ต้องเป็นเจ้าของบริษัท หรือผู้มีสิทธิ์ยื่นภาษี + ตั้งค่าบริษัท — " + PermissionKeys.DeniedMessage(PartnerConsent.KeyOf(n));
        return null;
    }

    /// <summary>ขั้นยืนยัน: เวอร์ชันข้อความ · hash ที่เห็น · คำที่พิมพ์ · null = ผ่าน</summary>
    public static string? ConfirmProblem(int shownVersion, string? shownHash, string expectedPreviewHash, string? typed)
    {
        if (shownVersion != CurrentTextVersion) return "ข้อความยินยอมมีเวอร์ชันใหม่ — เปิดอ่านข้อความล่าสุดแล้วยืนยันอีกครั้ง";
        if (string.IsNullOrWhiteSpace(shownHash)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(shownHash.Trim().ToLowerInvariant()), Encoding.ASCII.GetBytes(expectedPreviewHash)))
            return "ข้อความ/แบบ/บัญชีที่ยืนยันไม่ตรงกับที่แสดง — เปิดอ่านข้อความอีกครั้งแล้วยืนยันใหม่";
        if ((typed ?? "").Trim() != ConfirmPhrase) return "พิมพ์คำว่า “" + ConfirmPhrase + "” เพื่อยืนยัน";
        return null;
    }

    public static class Codes
    {
        public const string Ok = "Ok";
        public const string NoConsent = "NoConsent";
        public const string WrongRow = "WrongRow";
        public const string Withdrawn = "Withdrawn";
        public const string Tampered = "Tampered";
        public const string TextOutdated = "TextOutdated";
        public const string PowerOfAttorneyOnly = "PowerOfAttorneyOnly";
        public const string PowerOfAttorneyPendingApproval = "PowerOfAttorneyPendingApproval";
        public const string TaxTypeNotCovered = "TaxTypeNotCovered";
        public const string OverCap = "OverCap";
        public const string GrantorLostPermission = "GrantorLostPermission";
        public const string ProviderMismatch = "ProviderMismatch";
    }

    public sealed record Verdict(bool Ok, string Code, string Reason, Guid? ConsentId)
    {
        /// <summary>พนักงานบันทึกแทนได้ (1 คลิก) — ความยินยอมบนเว็บที่ใช้ได้ หรือมีหนังสือมอบอำนาจ (272-1-6)</summary>
        public bool AllowsStaffClick => Ok || Code == Codes.PowerOfAttorneyOnly;
    }

    /// <summary>
    /// ใช้ได้ตอนนี้ไหม (ตรวจสดทุกครั้ง — ผู้เรียกเขียนบัญชีต้องเรียกผ่าน <c>ProviderConsentService.CheckForWriteAsync</c> ใต้ล็อก) ·
    /// <paramref name="remittanceType"/> null = ถามแค่ว่ามีความยินยอมที่ใช้อยู่ (หน้า/ป้าย)
    /// </summary>
    public static Verdict Allows(ProviderAutomationConsent? row, Guid clientCompanyId, Guid providerCompanyId, string? remittanceType, decimal? amount, bool grantorStillPermitted)
    {
        if (row == null) return new(false, Codes.NoConsent, "บริษัทยังไม่อนุญาตให้ NextAcc บันทึกแทน", null);
        if (row.CompanyId != clientCompanyId || row.Scope != Scope) return new(false, Codes.WrongRow, "แถวความยินยอมไม่ใช่ของบริษัท/ขอบเขตนี้", row.Id);
        if (row.ProviderCompanyId != providerCompanyId) return new(false, Codes.ProviderMismatch, "ความยินยอมนี้ให้ผู้ให้บริการรายอื่น", row.Id);
        if (row.WithdrawnAt != null || row.IsDeleted) return new(false, Codes.Withdrawn, "บริษัทถอนการอนุญาตแล้ว", row.Id);
        if (!HashMatches(row)) return new(false, Codes.Tampered, "หลักฐานความยินยอมไม่ตรงกับเนื้อแถว (hash) — ให้ความยินยอมใหม่", row.Id);
        if (row.TextVersion < MinTextVersion) return new(false, Codes.TextOutdated, "ข้อความยินยอมเป็นเวอร์ชันเก่า — ยืนยันใหม่", row.Id);
        // ฝ่ายค้าน 272 M4: หนังสือมอบอำนาจต้องมีผู้อนุมัติคนที่สองของผู้ให้บริการ (ไม่ใช่ผู้แนบ) ก่อนใช้กดบันทึกได้
        if (row.EvidenceSource == SourcePowerOfAttorney && (row.ApprovedByUserId is not Guid ap || ap == row.GrantedByUserId || row.ApprovedAt == null))
            return new(false, Codes.PowerOfAttorneyPendingApproval, "หนังสือมอบอำนาจยังไม่ได้รับการอนุมัติจากพนักงานอีกคนของสำนักงานบัญชี (ผู้แนบอนุมัติเองไม่ได้)", row.Id);
        if (row.EvidenceSource == SourcePowerOfAttorney)
            return new(false, Codes.PowerOfAttorneyOnly, "มีหนังสือมอบอำนาจ (แนบโดยพนักงาน) — ไม่เปิดการบันทึกเอง ทุกสลิปให้พนักงานกดบันทึก (272-1-6)", row.Id);
        if (!grantorStillPermitted) return new(false, Codes.GrantorLostPermission, "ผู้ให้ความยินยอมไม่มีสิทธิ์แล้ว — ผู้มีสิทธิ์ยืนยันใหม่", row.Id);
        if (remittanceType != null && !ParseTypes(row.TaxTypes).Contains(remittanceType))
            return new(false, Codes.TaxTypeNotCovered, LabelOf(remittanceType) + " ไม่อยู่ในแบบที่บริษัทอนุญาต", row.Id);
        if (row.MaxAmount is decimal cap && amount is decimal a && a > cap)
            return new(false, Codes.OverCap, $"ยอด {a:N2} บาท เกินเพดานที่อนุญาต {cap:N2} บาท", row.Id);
        return new(true, Codes.Ok, "อนุญาตแล้ว", row.Id);
    }
}
