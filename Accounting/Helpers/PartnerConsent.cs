using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินความยินยอม "งานอัตโนมัติกับคู่ค้า" — รอบ 264 P1** (ดีไซน์ <c>erp-review/2026-11-12/team-264-a2b-design.md</c> §2 · คำตัดสิน 264-2-1…3) · pure · OWNER เดียวของ:
/// <list type="bullet">
/// <item>ชุดขอบเขต + สิ่งที่แต่ละขอบเขตต้องมี (<see cref="Scopes"/>)</item>
/// <item>ข้อความยินยอมแบบมีเวอร์ชัน (<see cref="TextFor"/>) + hash canonical ตัวเดียว (<see cref="Hash"/> — ใช้ทั้งตอนเขียนและตอนตรวจ · กฎ #4 C)</item>
/// <item>"ความยินยอมกว้างกว่าสิทธิ์ผู้ให้ไม่ได้" (<see cref="GrantProblem"/> — ตอนให้ และตรวจซ้ำทุกครั้งที่ใช้ผ่าน <see cref="Allows"/>)</item>
/// <item>ใช้ได้ตอนนี้ไหม (<see cref="Allows"/> · <see cref="AllowsCompanyWide"/>) — ผู้เรียกจริงคือ <c>PartnerConsentService.CheckAsync</c> ตัวเดียว (แพ็ก P3/P5 เรียกตัวนั้น)</item>
/// </list>
/// ความยินยอมเป็นของ<b>ฝั่งที่ถูกลงบัญชี</b>เสมอ — คู่ค้าได้แค่ "ขอ" (F9)
/// </summary>
public static class PartnerConsent
{
    public const string RuleCode = "XT-CONSENT";
    public const string LegalReference = "พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล ม.19 (ความยินยอม — ขอบเขต · ถอนได้) · ป.รัษฎากร ม.50 ทวิ (50 ทวิ ที่ส่งกลับ) · ความรับผิดชอบการลงบัญชีอยู่กับบริษัทผู้ให้";

    /// <summary>ข้อความยินยอมเวอร์ชันปัจจุบัน — เปลี่ยนถ้อยคำ = เพิ่มเวอร์ชันใหม่ใน <see cref="Texts"/> (ห้ามแก้ข้อความเวอร์ชันเดิม: แถวเก่าตรวจ hash ไม่ผ่าน)</summary>
    public const int CurrentTextVersion = 1;

    /// <summary>คำที่ต้องพิมพ์ยืนยันสำหรับขอบเขตที่เปิดการลงบัญชีอัตโนมัติ (ขั้นที่สองของการให้)</summary>
    public const string ConfirmPhrase = "ยินยอม";

    public const int MaxReasonLength = 500;

    public enum Scope
    {
        /// <summary>เอกสารขายของคู่ค้า ⇒ ใบซื้อร่างในบริษัทเรา</summary>
        PurchaseDrafts = 1,
        /// <summary>อนุมัติใบซื้อร่างจากคู่ค้าเองภายใต้เพดาน (P3 §5.2)</summary>
        AutoApprovePurchase = 2,
        /// <summary>บันทึกจ่ายใบซื้อเองเมื่อคู่ค้ารับเงินแล้วและทุกช่องมีหลักฐาน (P3 §5.1)</summary>
        AutoRecordPayment = 3,
        /// <summary>รับสำเนาสลิป/หลักฐานการโอนจากคู่ค้าเป็นหลักฐานแหล่งเงิน (P2/P4 §4.4)</summary>
        ReceiveSlipCopy = 4,
        /// <summary>ส่ง 50 ทวิ ที่เราออกกลับเข้าระบบของคู่ค้า</summary>
        ReturnWhtCerts = 5,
        /// <summary>ฝ่ายค้าน 264-P4 H2 (คำตัดสิน 264-5-14): ขอบเขตของ<b>ผู้ขาย</b> — ยอมรับสำเนาสลิปที่ผู้ซื้อคู่ค้าแนบจากระบบของเขา เข้าคิวรายการรอยืนยันรับเงินของเรา
        /// (ยังไม่ใช่เงินเข้า) · แยกจาก <see cref="ReceiveSlipCopy"/> (ของผู้ซื้อ — design §4.4(ข)) · ผู้อ่าน: <c>PartnerOutboundPaymentService.DeliverSlipCopyAsync</c></summary>
        AcceptPayerSlipCopy = 6,
    }

    /// <summary>สิ่งที่ผู้ให้ต้องมี — service แปลงเป็นด่านสิทธิ์ตัวเดิม (<c>DocumentPermissionHelper</c> · <c>IPermissionService</c>) ไม่เขียนกติกาสิทธิ์ซ้ำ</summary>
    public enum Need
    {
        CompanySettingsEdit = 1,
        PurchaseView = 2,
        PurchaseCreate = 3,
        PurchaseApprove = 4,
        TaxFile = 5,
        /// <summary>อนุมัติเอกสารฝั่งขาย (ผู้ยอมรับสลิปของผู้ซื้อเข้าคิวรับชำระ — 264-5-14)</summary>
        SalesApprove = 6,
    }

    /// <summary>คีย์สิทธิ์ที่ใช้ "เล่า" ในข้อความปฏิเสธ (ข้อความตัวเดียว <see cref="PermissionKeys.DeniedMessage"/>)</summary>
    public static string KeyOf(Need n) => n switch
    {
        Need.CompanySettingsEdit => PermissionKeys.CompanySettingsEdit,
        Need.PurchaseView => PermissionKeys.DocumentPurchaseView,
        Need.PurchaseCreate => PermissionKeys.DocumentPurchaseCreate,
        Need.PurchaseApprove => PermissionKeys.DocumentPurchaseApprove,
        Need.TaxFile => PermissionKeys.TaxFile,
        Need.SalesApprove => PermissionKeys.DocumentRevenueApprove,
        _ => PermissionKeys.CompanySettingsEdit,
    };

    /// <param name="CompanyWideToggle">ขอบเขตเดิมที่ยังตัดสินด้วยติ๊กระดับบริษัท (<c>WorkflowAutomationConfig</c>) — ต่อคู่ยังไม่เปิดรับ (คำตัดสิน 264-2-1: ห้าม silent no-op)</param>
    /// <param name="ReaderWired">มีท่อที่อ่านขอบเขตนี้แล้วหรือยัง — false ⇒ หน้าแสดง "ให้ไว้ล่วงหน้าได้ ยังไม่มีผล" (ไม่ใช่เงียบ) · แพ็กที่ต่อสายเปลี่ยนเป็น true ในคอมมิตเดียวกัน</param>
    /// <param name="MinTextVersion">ฝ่ายค้าน L3: เวอร์ชันข้อความต่ำสุดที่ยังยอมรับ — ข้อความใหม่ที่ขยายสิ่งที่ท่อทำ ⇒ ขยับค่านี้ ⇒ แถวเก่าได้ TextOutdated</param>
    /// <param name="WiredSince">ฝ่ายค้าน L4: วันที่ท่ออ่านขอบเขตนี้เริ่มทำงาน (UTC) — แพ็กที่พลิก ReaderWired ต้องตั้งค่านี้ในคอมมิตเดียวกัน ⇒
    /// แถวที่ให้ไว้ "ล่วงหน้า" ก่อนวันนั้นต้องยืนยันใหม่ (GrantedBeforeWired) — ไม่เริ่มมีผลเองโดยที่ผู้ให้ไม่รู้</param>
    /// <param name="WiredFromStart">ฝ่ายค้าน 264-P4 M7: ขอบเขตที่เกิดพร้อมผู้อ่าน (ไม่เคยมีแถว "ให้ล่วงหน้า") ⇒ ไม่ต้องมี <paramref name="WiredSince"/> ·
    /// การบังคับยืนยันใหม่ในอนาคตใช้ <paramref name="MinTextVersion"/> (ไม่ผูกนาฬิกา deploy)</param>
    public sealed record ScopeDef(Scope Scope, string Label, IReadOnlyList<Need> Needs, bool RequiresCap, bool AllowsPayAccount,
        bool NeedsTypedConfirm, bool CompanyWideToggle, bool ReaderWired, int MinTextVersion = 1, DateTime? WiredSince = null, bool WiredFromStart = false);

    /// <summary>รอบ 264 P3 — วันที่ท่อบันทึกจ่ายอัตโนมัติเริ่มอ่านขอบเขต <see cref="Scope.AutoRecordPayment"/> (UTC · go-live ของแพ็ก P3) · <b>ประกาศก่อน <see cref="Scopes"/></b> (ตัวเริ่มค่า static เรียงตามบรรทัด)</summary>
    public static readonly DateTime AutoRecordPaymentWiredSince = new(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
    /// <summary>รอบ 288 — วันที่ท่ออนุมัติใบซื้อเองเริ่มอ่านขอบเขต <see cref="Scope.AutoApprovePurchase"/> (UTC) · แถวที่ให้ไว้ล่วงหน้าก่อนวันนี้ต้องยืนยันใหม่ (L4)</summary>
    public static readonly DateTime AutoApprovePurchaseWiredSince = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
    /// <summary>รอบ 288 — วันที่ท่อรับสำเนาสลิปจากผู้ขายเริ่มอ่านขอบเขต <see cref="Scope.ReceiveSlipCopy"/> (UTC) · แถวที่ให้ไว้ล่วงหน้าก่อนวันนี้ต้องยืนยันใหม่ (L4)</summary>
    public static readonly DateTime ReceiveSlipCopyWiredSince = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<ScopeDef> Scopes { get; } = new List<ScopeDef>
    {
        new(Scope.PurchaseDrafts, "สร้างใบซื้อร่างจากเอกสารของคู่ค้า",
            new[] { Need.CompanySettingsEdit, Need.PurchaseCreate }, RequiresCap: false, AllowsPayAccount: false, NeedsTypedConfirm: false,
            CompanyWideToggle: true, ReaderWired: true),
        new(Scope.AutoApprovePurchase, "อนุมัติใบซื้อจากคู่ค้าเองภายใต้เพดาน",
            new[] { Need.CompanySettingsEdit, Need.PurchaseApprove }, RequiresCap: true, AllowsPayAccount: false, NeedsTypedConfirm: true,
            // รอบ 288: ผู้อ่าน = PartnerPayablePipeline.AutoApproveDraftsAsync (ตัวตัดสิน PartnerAutoApprovePolicy) · แถวล่วงหน้ายืนยันใหม่ (L4)
            CompanyWideToggle: false, ReaderWired: true, WiredSince: AutoApprovePurchaseWiredSince),
        // รอบ 264 P3: ผู้อ่าน = PartnerPayablePipeline (ขั้น PartnerPayable ของ AutopilotJob) — แถวที่ให้ไว้ล่วงหน้าก่อนวันต่อสายต้องยืนยันใหม่ (GrantedBeforeWired · L4)
        new(Scope.AutoRecordPayment, "บันทึกจ่ายใบซื้อเองเมื่อคู่ค้ารับเงินแล้ว",
            new[] { Need.CompanySettingsEdit, Need.PurchaseApprove }, RequiresCap: true, AllowsPayAccount: true, NeedsTypedConfirm: true,
            CompanyWideToggle: false, ReaderWired: true, WiredSince: AutoRecordPaymentWiredSince),
        // ฝ่ายค้าน 264-P4 H2: ขอบเขตของผู้ซื้อตาม design §4.4(ข) · รอบ 288: ผู้อ่าน = PartnerPayablePipeline.CopySlipsAsync (สลิปที่ผู้ขายได้รับ/ยืนยัน
        // สำหรับใบของเรา ⇒ สำเนาแนบใบซื้อของเรา · ทำในงานเบื้องหลังของฝั่งเรา ไม่ใช่ในคำขอของผู้ขาย) · แถวล่วงหน้ายืนยันใหม่ (L4)
        new(Scope.ReceiveSlipCopy, "รับสำเนาสลิปการโอนจากคู่ค้าเป็นหลักฐาน",
            new[] { Need.CompanySettingsEdit, Need.PurchaseView }, RequiresCap: false, AllowsPayAccount: false, NeedsTypedConfirm: false,
            CompanyWideToggle: false, ReaderWired: true, WiredSince: ReceiveSlipCopyWiredSince),
        new(Scope.ReturnWhtCerts, "ส่ง 50 ทวิ ที่เราออกเข้าระบบของคู่ค้า",
            new[] { Need.CompanySettingsEdit, Need.TaxFile }, RequiresCap: false, AllowsPayAccount: false, NeedsTypedConfirm: false,
            CompanyWideToggle: true, ReaderWired: true),
        // ฝ่ายค้าน 264-P4 H2 (คำตัดสิน 264-5-14): ขอบเขตของผู้ขาย · ผู้อ่าน = PartnerOutboundPaymentService.DeliverSlipCopyAsync (CheckForWriteAsync) ·
        // ผู้ให้ต้องอนุมัติเอกสารขายได้ (เป็นผู้ที่ยืนยันรับเงินจากคิวนั้น) · เกิดพร้อมผู้อ่าน ⇒ WiredFromStart (M7: ยืนยันใหม่ในอนาคตด้วย MinTextVersion)
        new(Scope.AcceptPayerSlipCopy, "รับสลิปที่คู่ค้าแนบตอนแจ้งโอนเข้าคิวรอยืนยันรับเงิน",
            new[] { Need.CompanySettingsEdit, Need.SalesApprove }, RequiresCap: false, AllowsPayAccount: false, NeedsTypedConfirm: false,
            CompanyWideToggle: false, ReaderWired: true, WiredFromStart: true),
    };

    public static ScopeDef Def(Scope s) => Scopes.First(d => d.Scope == s);

    /// <summary>แปลงชื่อขอบเขตจากหน้า/แถว — รับเฉพาะ<b>ชื่อ</b> (ไม่รับตัวเลข · F2 ข้อ 5)</summary>
    public static bool TryParse(string? name, out Scope scope)
    {
        scope = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        if (n.Length == 0 || char.IsDigit(n[0]) || n[0] == '-') return false;
        return Enum.TryParse(n, ignoreCase: false, out scope) && Enum.IsDefined(scope);
    }

    // ═══════════ ข้อความยินยอม (มีเวอร์ชัน) ═══════════

    private const string Withdraw = "ถอนได้ทุกเมื่อที่หน้า “งานอัตโนมัติกับคู่ค้า” (มีผลทันทีกับงานที่ยังไม่ได้ทำ · งานที่ทำไปแล้วไม่ถูกย้อน) · "
        + "ระงับการเชื่อมกับคู่ค้า = หยุดทุกขอบเขต และต้องให้ความยินยอมใหม่หลังเชื่อมกลับ";

    private static readonly Dictionary<(Scope, int), string> Texts = new()
    {
        [(Scope.PurchaseDrafts, 1)] =
            "ข้าพเจ้าในนามบริษัทอนุญาตให้ระบบของบริษัทเราสร้าง “ใบซื้อร่าง” จากใบแจ้งหนี้/ใบกำกับที่คู่ค้ารายนี้ส่งมา · "
            + "ระบบจะไม่อนุมัติ ไม่ลงภาษีซื้อ และไม่บันทึกจ่ายจากขอบเขตนี้ · " + Withdraw,
        [(Scope.AutoApprovePurchase, 1)] =
            "ข้าพเจ้าในนามบริษัทอนุญาตให้ระบบของบริษัทเรา “อนุมัติใบซื้อ” ที่มาจากเอกสารของคู่ค้ารายนี้เอง เฉพาะใบที่ยอดไม่เกินเพดานที่ระบุ "
            + "และผังบัญชีมีหลักฐาน · การอนุมัติไม่ใช่การเคลมภาษีซื้อ (ยังต้องมีต้นฉบับใบกำกับตาม ม.86/4) · ใบที่ไม่ผ่านจะรอคนกดเหมือนเดิม · "
            + "ข้าพเจ้ามีสิทธิ์อนุมัติเอกสารซื้อในบริษัทนี้ และความยินยอมจะหยุดเองถ้าข้าพเจ้าไม่มีสิทธิ์นั้นแล้ว · " + Withdraw,
        [(Scope.AutoRecordPayment, 1)] =
            "ข้าพเจ้าในนามบริษัทอนุญาตให้ระบบของบริษัทเรา “บันทึกจ่ายใบซื้อ” เมื่อคู่ค้ารายนี้รับเงินแล้ว เฉพาะเมื่อยอดเงิน ภาษีหัก ณ ที่จ่าย (ตามกติกาของเรา) "
            + "ใบซื้อที่อนุมัติแล้ว งวดบัญชีที่เปิด และบัญชีที่จ่ายออก มีหลักฐานครบ และยอดไม่เกินเพดานที่ระบุ · ระบบไม่เดาบัญชีที่จ่าย "
            + "(ไม่ใช้เงินสด/บัญชีแรก/บัญชีพักแทน) · 50 ทวิ ออกตามค่าตั้งของเรา ลงวันที่จ่าย · ใบที่ไม่ผ่านจะรอคนกดเหมือนเดิม · "
            + "ข้าพเจ้ามีสิทธิ์อนุมัติเอกสารซื้อและบันทึกจ่ายในบริษัทนี้ และความยินยอมจะหยุดเองถ้าข้าพเจ้าไม่มีสิทธิ์นั้นแล้ว · " + Withdraw,
        [(Scope.ReceiveSlipCopy, 1)] =
            "ข้าพเจ้าในนามบริษัทอนุญาตให้คู่ค้ารายนี้ส่ง “สำเนาสลิป/หลักฐานการโอน” ของการชำระที่เกี่ยวกับเราเข้าระบบของเรา เพื่อใช้เป็นหลักฐานแหล่งเงิน · "
            + "สลิปอาจมีชื่อและเลขบัญชีบางส่วน (ข้อมูลส่วนบุคคล) เก็บตามระยะเวลาเอกสารบัญชี · สลิปเพียงอย่างเดียวไม่ใช่หลักฐานว่าเงินออกจากบัญชีแล้ว · " + Withdraw,
        [(Scope.AcceptPayerSlipCopy, 1)] =
            "ข้าพเจ้าในนามบริษัทอนุญาตให้คู่ค้ารายนี้ (ในฐานะผู้ซื้อของเรา) ส่ง “สลิปการโอน” ที่เขาแนบตอนแจ้งโอนจากระบบของเขา เข้าคิว “รายการรอยืนยันรับเงิน” ของเอกสารขายของเรา · "
            + "รายการนั้นยังไม่ใช่การรับเงิน — ผู้มีสิทธิ์ของเรายังต้องตรวจกับบัญชีธนาคารแล้วกดยืนยันเหมือนสลิปทุกช่องทาง · "
            + "สลิปอาจมีชื่อและเลขบัญชีบางส่วนของผู้โอน (กรรมการ/พนักงานของคู่ค้า — ข้อมูลส่วนบุคคล) เก็บตามระยะเวลาเอกสารบัญชี และใช้เพื่อยืนยันการรับชำระเท่านั้น · "
            + "ข้าพเจ้ามีสิทธิ์อนุมัติเอกสารขายในบริษัทนี้ และความยินยอมจะหยุดเองถ้าข้าพเจ้าไม่มีสิทธิ์นั้นแล้ว · " + Withdraw,
        [(Scope.ReturnWhtCerts, 1)] =
            "ข้าพเจ้าในนามบริษัทอนุญาตให้ระบบของบริษัทเราส่งหนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ) ที่เราออกให้คู่ค้ารายนี้ เข้ากล่องรับในระบบของคู่ค้า · "
            + "ข้อมูลในใบเป็นข้อมูลที่ผู้รับต้องได้ตาม ม.50 ทวิ อยู่แล้ว · " + Withdraw,
    };

    /// <summary>ข้อความที่ผู้ให้เห็นของขอบเขต/เวอร์ชันนั้น · null = ไม่มีเวอร์ชันนี้ (แถวที่อ้างเวอร์ชันที่ไม่มี = ตรวจ hash ไม่ผ่าน)</summary>
    public static string? TextFor(Scope scope, int version) => Texts.TryGetValue((scope, version), out var t) ? t : null;

    // ═══════════ hash canonical (ตัวเดียว · เขียน = ตรวจ) ═══════════

    /// <summary>รูปสตริงที่ hash — ฟิลด์คั่นด้วย <c>\n</c> · เงิน 2 ตำแหน่ง InvariantCulture · ไม่มีค่า = <c>-</c> · ข้อความยินยอมอยู่บรรทัดสุดท้าย</summary>
    public static string Canonical(Guid companyId, Guid partnershipId, Scope scope, decimal? maxAmount, Guid? payBankAccountId, int textVersion, string text)
        => string.Join("\n",
            "PAC1",
            companyId.ToString("D"),
            partnershipId.ToString("D"),
            scope.ToString(),
            maxAmount is decimal m ? Math.Round(m, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture) : "-",
            payBankAccountId is Guid b ? b.ToString("D") : "-",
            textVersion.ToString(CultureInfo.InvariantCulture),
            text);

    /// <summary>SHA-256 (hex ตัวพิมพ์เล็ก 64 ตัว) ของ <see cref="Canonical"/> — ฟังก์ชันเดียวที่ทั้งขั้น "ดูข้อความ" · ขั้น "ยินยอม" · ตัวตรวจแถว ใช้</summary>
    public static string Hash(Guid companyId, Guid partnershipId, Scope scope, decimal? maxAmount, Guid? payBankAccountId, int textVersion, string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Canonical(companyId, partnershipId, scope, maxAmount, payBankAccountId, textVersion, text)))).ToLowerInvariant();

    /// <summary>ฝ่ายค้าน L2 — รูปที่เก็บรุ่น 2 (<c>PAC2</c>): เนื้อของ <see cref="Canonical"/> + บริษัทคู่ค้า + ผู้ให้ + เวลาให้ (วินาที UTC) ⇒ "หลักฐานตรง" รับรอง "ใคร/เมื่อไร" ด้วย ·
    /// เป็น <b>integrity check</b> (SHA-256 ไม่มีกุญแจ) ไม่ใช่ tamper-proof ต่อผู้ที่เขียนฐานได้ — ด่านจริงคือ trigger append-only + audit chain ·
    /// <c>PAC1</c> (<see cref="Hash"/>) ยังเป็น "hash ที่ผู้ให้เห็น" ในขั้นดู/ยินยอม และแถวรุ่น 1 ยังตรวจได้</summary>
    public static string CanonicalV2(Guid companyId, Guid partnershipId, Guid partnerCompanyId, Scope scope, decimal? maxAmount, Guid? payBankAccountId,
        int textVersion, string text, Guid grantedByUserId, DateTime grantedAtUtc)
        => string.Join("\n",
            "PAC2",
            partnerCompanyId.ToString("D"),
            grantedByUserId.ToString("D"),
            TruncateToSecond(grantedAtUtc).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            Canonical(companyId, partnershipId, scope, maxAmount, payBankAccountId, textVersion, text));

    public static string HashV2(Guid companyId, Guid partnershipId, Guid partnerCompanyId, Scope scope, decimal? maxAmount, Guid? payBankAccountId,
        int textVersion, string text, Guid grantedByUserId, DateTime grantedAtUtc)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            CanonicalV2(companyId, partnershipId, partnerCompanyId, scope, maxAmount, payBankAccountId, textVersion, text, grantedByUserId, grantedAtUtc)))).ToLowerInvariant();

    /// <summary>รุ่นของ hash ที่แถวใหม่ใช้ (1 = PAC1 · 2 = PAC2)</summary>
    public const int CurrentHashVersion = 2;

    /// <summary>เวลาให้ที่เก็บ/hash ตัดเหลือวินาที (round-trip ผ่าน timestamp ของ PostgreSQL โดยไม่เพี้ยน)</summary>
    public static DateTime TruncateToSecond(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// <summary>hash ของแถวที่เก็บไว้ตรงกับเนื้อแถว + ข้อความเวอร์ชันที่อ้างไหม (แถวถูกแก้ทับ/อ้างเวอร์ชันที่ไม่มี/รุ่น hash ไม่รู้จัก ⇒ false)</summary>
    public static bool HashMatches(PartnerAutomationConsent row)
    {
        if (!TryParse(row.Scope, out var scope)) return false;
        var text = TextFor(scope, row.TextVersion);
        if (text == null) return false;
        var expected = row.HashVersion switch
        {
            1 => Hash(row.CompanyId, row.TradingPartnershipId, scope, row.MaxAmount, row.DefaultPayBankAccountId, row.TextVersion, text),
            2 => HashV2(row.CompanyId, row.TradingPartnershipId, row.PartnerCompanyId, scope, row.MaxAmount, row.DefaultPayBankAccountId, row.TextVersion, text,
                row.GrantedByUserId, row.GrantedAt),
            _ => null,
        };
        return expected != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(row.EvidenceHash ?? ""));
    }

    // ═══════════ ตอนให้ ═══════════

    /// <summary>ความยินยอมกว้างกว่าสิทธิ์ผู้ให้ไม่ได้ (F5) — คืนข้อความของสิทธิ์แรกที่ขาด · null = ผ่าน · <paramref name="has"/> = ด่านสิทธิ์จริงของผู้ให้</summary>
    public static string? GrantProblem(Scope scope, Func<Need, bool> has)
    {
        foreach (var n in Def(scope).Needs)
            if (!has(n))
                return "ให้ความยินยอม “" + Def(scope).Label + "” ไม่ได้ เพราะความยินยอมต้องไม่กว้างกว่าสิทธิ์ของผู้ให้ — " + PermissionKeys.DeniedMessage(KeyOf(n));
        return null;
    }

    /// <summary>ค่าที่ขอให้ถูกรูปไหม (ก่อนแตะฐาน) · null = ผ่าน</summary>
    public static string? RequestProblem(Scope scope, decimal? maxAmount, Guid? payBankAccountId)
    {
        var d = Def(scope);
        if (d.CompanyWideToggle)
            return "“" + d.Label + "” ยังตัดสินด้วยค่าตั้งของทั้งบริษัท (หน้า “คู่ค้าที่ใช้ NextAcc”) — การแยกต่อคู่ค้ายังไม่เปิด (คำตัดสิน 264-2-1)";
        if (maxAmount is decimal m && m <= 0m) return "เพดานต่อใบต้องมากกว่า 0 บาท";
        if (maxAmount is decimal m2 && m2 > 999_999_999_999m) return "เพดานต่อใบสูงเกินไป";
        if (d.RequiresCap && maxAmount == null) return "“" + d.Label + "” ต้องระบุเพดานต่อใบ (ลงบัญชีอัตโนมัติไม่เปิดแบบไม่จำกัดยอด)";
        if (!d.AllowsPayAccount && payBankAccountId != null) return "ขอบเขตนี้ไม่มีบัญชีจ่ายตั้งต้น";
        if (payBankAccountId == Guid.Empty) return "บัญชีจ่ายตั้งต้นไม่ถูกต้อง";
        return null;
    }

    /// <summary>ขั้นที่สองของการให้ — ข้อความ/ค่าที่ผู้ให้เห็นในขั้นแรกต้องเป็นอันเดียวกับที่จะเก็บ (hash ตรง) และขอบเขตที่ลงบัญชีอัตโนมัติต้องพิมพ์ยืนยัน · null = ผ่าน</summary>
    public static string? ConfirmProblem(Scope scope, int shownVersion, string? shownHash, string expectedHash, string? typedPhrase)
    {
        if (shownVersion != CurrentTextVersion)
            return "ข้อความยินยอมมีเวอร์ชันใหม่ — เปิดอ่านข้อความล่าสุดแล้วยืนยันอีกครั้ง";
        if (string.IsNullOrWhiteSpace(shownHash)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(shownHash.Trim().ToLowerInvariant()), Encoding.ASCII.GetBytes(expectedHash)))
            return "ข้อความ/เพดาน/บัญชีที่ยืนยันไม่ตรงกับที่แสดง — เปิดอ่านข้อความอีกครั้งแล้วยืนยันใหม่";
        if (Def(scope).NeedsTypedConfirm && (typedPhrase ?? "").Trim() != ConfirmPhrase)
            return "ขอบเขตนี้เปิดการลงบัญชีอัตโนมัติ — พิมพ์คำว่า “" + ConfirmPhrase + "” เพื่อยืนยัน";
        return null;
    }

    // ═══════════ ตอนใช้ (ทุกครั้งที่ท่อทำงาน) ═══════════

    public static class Codes
    {
        public const string Ok = "Ok";
        public const string NoPartner = "NoPartner";
        public const string PartnerNotActive = "PartnerNotActive";
        public const string NoConsent = "NoConsent";
        public const string WrongRow = "WrongRow";
        public const string Withdrawn = "Withdrawn";
        public const string Tampered = "Tampered";
        public const string StaleAfterSuspend = "StaleAfterSuspend";
        public const string GrantorLostPermission = "GrantorLostPermission";
        public const string AmountUnknown = "AmountUnknown";
        public const string OverCap = "OverCap";
        public const string CompanyWideOff = "CompanyWideOff";
        public const string TextOutdated = "TextOutdated";
        public const string GrantedBeforeWired = "GrantedBeforeWired";
    }

    /// <summary>
    /// ฝ่ายค้าน M3 — <b>ยอดที่เทียบเพดาน</b> = ยอดรวมของใบเป็น<b>บาท</b> รวม VAT <b>ก่อนหัก ณ ที่จ่าย</b> (<c>Document.TotalAmount</c> ของใบสกุล THB) ·
    /// ห้ามส่งยอดสุทธิหลังหัก / ยอดเงินที่โอน / ยอดสกุลต่างประเทศ · สกุลอื่น ⇒ AmountUnknown (ไม่แปลงเอง) · ติดลบ ⇒ AmountUnknown
    /// </summary>
    public sealed record CapAmount(decimal GrandTotalBeforeWht, string Currency)
    {
        public static CapAmount Thb(decimal grandTotalBeforeWht) => new(grandTotalBeforeWht, "THB");
        public bool Usable => string.Equals((Currency ?? "").Trim(), "THB", StringComparison.OrdinalIgnoreCase) && GrandTotalBeforeWht >= 0m;
    }

    /// <summary>คำตอบตอนใช้ — <see cref="Code"/> เป็นชื่อคงที่ (หน้า/ท่อใช้แยกเหตุ) · <see cref="Reason"/> ข้อความไทยถึงผู้ใช้</summary>
    public sealed record Verdict(bool Ok, string Code, string Reason);

    private static Verdict No(string code, string reason) => new(false, code, reason);

    /// <summary>
    /// ขอบเขตต่อคู่ใช้ได้ตอนนี้ไหม — ตรวจสดทุกครั้ง: คู่ค้า Accepted + บริษัทคู่ค้ายังใช้งาน · แถวของบริษัท/คู่/ขอบเขตนี้ · ยังไม่ถอน · hash ตรง · เวอร์ชันข้อความยังยอมรับ ·
    /// ให้หลังการยอมรับ/การระงับครั้งล่าสุด (เชื่อมกลับไม่ฟื้นเอง) · ให้หลังท่อเริ่มทำงาน · ผู้ให้ยังมีสิทธิ์ (<paramref name="grantorStillPermitted"/> = ผล GrantProblem
    /// ของผู้ให้ ณ ตอนนี้ + ผู้ใช้ยังใช้งาน) · ยอด (<see cref="CapAmount"/>) ไม่เกินเพดาน — ตั้งเพดานไว้แล้วไม่รู้ยอด = ไม่ผ่าน
    /// </summary>
    public static Verdict Allows(Scope scope, PartnerAutomationConsent? row, TradingPartnership? partnership, Guid companyId, CapAmount? amount,
        bool grantorStillPermitted, bool partnerCompanyActive = true)
    {
        var d = Def(scope);
        if (partnership == null || (partnership.CompanyAId != companyId && partnership.CompanyBId != companyId))
            return No(Codes.NoPartner, "ไม่พบการเชื่อมกับคู่ค้ารายนี้");
        if (partnership.Status != TradingPartnershipStatus.Accepted)
            return No(Codes.PartnerNotActive, "การเชื่อมกับคู่ค้าไม่ได้ใช้งานอยู่ (" + PartnershipStatusLabel(partnership.Status) + ") — งานอัตโนมัติทุกขอบเขตหยุด");
        if (!partnerCompanyActive)
            return No(Codes.PartnerNotActive, "บริษัทคู่ค้าถูกลบหรือระงับการใช้งาน — งานอัตโนมัติทุกขอบเขตหยุด");
        if (row == null)
            return No(Codes.NoConsent, "ยังไม่ได้ให้ความยินยอม \u201c" + d.Label + "\u201d กับคู่ค้ารายนี้");
        if (row.CompanyId != companyId || row.TradingPartnershipId != partnership.Id || row.Scope != scope.ToString())
            return No(Codes.WrongRow, "แถวความยินยอมไม่ใช่ของบริษัท/คู่ค้า/ขอบเขตนี้");
        if (row.WithdrawnAt != null || row.IsDeleted)
            return No(Codes.Withdrawn, "ถอนความยินยอมแล้ว");
        if (!HashMatches(row))
            return No(Codes.Tampered, "บันทึกความยินยอมถูกแก้ไขหลังให้ไว้ (ตรวจกับต้นฉบับไม่ตรง) — ให้ความยินยอมใหม่");
        if (row.TextVersion < d.MinTextVersion)
            return No(Codes.TextOutdated, "ข้อความยินยอมที่ให้ไว้เป็นเวอร์ชันเก่า (ขอบเขตงานเปลี่ยน) — อ่านข้อความใหม่แล้วยืนยันอีกครั้ง");
        if ((partnership.SuspendedAt is DateTime s && s >= row.GrantedAt) || (partnership.AcceptedAt is DateTime acc && TruncateToSecond(acc) > row.GrantedAt))
            return No(Codes.StaleAfterSuspend, "ความยินยอมให้ไว้ก่อนการเชื่อมครั้งล่าสุด (มีการระงับ/เชื่อมใหม่) — ให้ความยินยอมใหม่");
        if (d.ReaderWired && d.WiredSince is DateTime w && row.GrantedAt < w)
            return No(Codes.GrantedBeforeWired, "ให้ไว้ล่วงหน้าก่อนงานอัตโนมัตินี้เริ่มทำงาน — ยืนยันใหม่เพื่อเริ่มใช้");
        if (!grantorStillPermitted)
            return No(Codes.GrantorLostPermission, "ผู้ให้ความยินยอมไม่มีสิทธิ์แล้ว (ลาออก/ถูกลดสิทธิ์) — ผู้มีสิทธิ์ยืนยันใหม่");
        var needAmount = d.RequiresCap || row.MaxAmount != null;
        if (needAmount && (amount == null || !amount.Usable))
            return No(Codes.AmountUnknown, "ไม่ทราบยอดรวมเงินบาทก่อนหัก ณ ที่จ่ายของงานนี้ (หรือไม่ใช่สกุลบาท) — ไม่ทำอัตโนมัติ");
        if (row.MaxAmount is decimal cap && amount is { } a && a.GrandTotalBeforeWht > cap)
            return No(Codes.OverCap, $"ยอด {a.GrandTotalBeforeWht:N2} บาท เกินเพดานที่ยินยอมไว้ {cap:N2} บาท");
        return new Verdict(true, Codes.Ok, "ใช้ได้ — ยินยอมไว้" + (row.MaxAmount is decimal c2 ? $" (เพดาน {c2:N2} บาท)" : ""));
    }

    /// <summary>ขอบเขตเดิมที่ยังตัดสินด้วยติ๊กระดับบริษัท (<see cref="ScopeDef.CompanyWideToggle"/>) — ติ๊กเปิด + คู่ค้า Accepted</summary>
    public static Verdict AllowsCompanyWide(Scope scope, bool toggleOn, TradingPartnership? partnership, Guid companyId)
    {
        if (partnership == null || (partnership.CompanyAId != companyId && partnership.CompanyBId != companyId))
            return No(Codes.NoPartner, "ไม่พบการเชื่อมกับคู่ค้ารายนี้");
        if (partnership.Status != TradingPartnershipStatus.Accepted)
            return No(Codes.PartnerNotActive, "การเชื่อมกับคู่ค้าไม่ได้ใช้งานอยู่ (" + PartnershipStatusLabel(partnership.Status) + ") — งานอัตโนมัติทุกขอบเขตหยุด");
        if (!toggleOn)
            return No(Codes.CompanyWideOff, "ค่าตั้งของทั้งบริษัท “" + Def(scope).Label + "” ปิดอยู่");
        return new Verdict(true, Codes.Ok, "ใช้ได้ — ค่าตั้งของทั้งบริษัทเปิดอยู่ (ทุกคู่ค้า)");
    }

    /// <summary>ค่าตั้งเดิมระดับบริษัทของขอบเขต <see cref="ScopeDef.CompanyWideToggle"/> (ตัวอ่านเดิมไม่ถูกแตะ — ฟังก์ชันนี้แค่ "อ่านให้ตรงตัว" ให้หน้า/ตัวตรวจ) · ขอบเขตอื่น = false</summary>
    public static bool CompanyWideToggleOn(Scope scope, WorkflowAutomationConfig? cfg) => scope switch
    {
        Scope.PurchaseDrafts => cfg?.CreatePurchaseDraftsFromPartners == true,
        Scope.ReturnWhtCerts => cfg?.SendWhtCertsToPartners == true,
        _ => false,
    };

    /// <summary>ป้ายผลจริงของขอบเขต (server computes · หน้าแสดง) — ขอบเขตที่ยังไม่มีท่ออ่านต้องบอกตรง ๆ ว่ายังไม่มีผล (ห้าม silent no-op)</summary>
    public static string? EffectNote(ScopeDef d)
        => d.CompanyWideToggle ? "เรื่องนี้เปิด/ปิดด้วยค่าตั้งของทั้งบริษัท (ทุกคู่ค้าพร้อมกัน) ที่หน้า \u201cคู่ค้าที่ใช้ NextAcc\u201d — ยังแยกเปิดทีละคู่ค้าไม่ได้"
         : d.Scope == Scope.AutoRecordPayment && !PartnerPaySourceAccount.ConsentDefaultAutoEnabled ? PartnerPaySourceAccount.ConsentDefaultNotAutoNote
         : !d.ReaderWired ? "ให้ไว้ล่วงหน้าได้ — ตอนนี้ระบบยังไม่ทำงานนี้เอง"
         : null;

    /// <summary>รอบ 288 (คำสั่งเจ้าของ "ข้อความทั้งหน้าเป็นภาษาผู้ใช้"): ยินยอมแล้วระบบทำอะไร · ทำเมื่อไร · หยุดเมื่อไร — ข้อความตัวเดียวที่หน้าแสดง
    /// (server computes · page displays · ห้ามมีเลขรอบ/คำตัดสินบนจอ)</summary>
    public sealed record ScopeExplain(string What, string When, string Stops);

    private const string StopsCommon = "ถอนความยินยอม · ระงับการเชื่อมกับคู่ค้า · ผู้ให้ความยินยอมหมดสิทธิ์หรือถูกตั้งให้ทำได้เฉพาะร่าง";

    public static ScopeExplain ExplainOf(Scope s) => s switch
    {
        Scope.PurchaseDrafts => new(
            "สร้าง “ใบซื้อร่าง” จากใบแจ้งหนี้/ใบกำกับที่คู่ค้าส่งมา (ไม่อนุมัติ ไม่ลงภาษีซื้อ ไม่บันทึกจ่าย)",
            "ทันทีที่คู่ค้าส่งเอกสารถึงเรา",
            "ปิดค่าตั้งของทั้งบริษัท หรือระงับการเชื่อมกับคู่ค้า"),
        Scope.AutoApprovePurchase => new(
            "อนุมัติใบซื้อร่างที่มาจากเอกสารของคู่ค้ารายนี้แทนผู้ให้ความยินยอม (ออกเลขตามลำดับปกติ) — การอนุมัติไม่ใช่การเคลมภาษีซื้อ ยังรอต้นฉบับใบกำกับตามเดิม",
            "ทุกไม่กี่นาทีหลังใบร่างเข้ามา เฉพาะใบที่ยอดรวมไม่เกินเพดาน · ใบกำกับของผู้ขายครบรายการ · ผังบัญชีทุกบรรทัดมั่นใจ "
                + "(เคยยืนยันกับผู้ขายรายนี้ หรือระบบในบ้านมั่นใจสูง) · ภาษีหัก ณ ที่จ่ายชัดเจน · ไม่มีข้อสังเกตก่อนอนุมัติ (รายจ่ายต้องห้าม/ภาษีซื้อต้องห้าม ฯลฯ) — "
                + "ใบที่ไม่ผ่านยังเป็นร่างพร้อมเหตุ และแจ้งเตือนให้คนกด",
            StopsCommon + " · ใบที่อนุมัติไปแล้วไม่ถูกย้อน (ยกเลิกเองได้)"),
        Scope.AutoRecordPayment => new(
            "บันทึกจ่ายใบซื้อ (ใบสำคัญจ่าย + ภาษีหัก ณ ที่จ่ายตามกติกาของเรา + 50 ทวิ ตามค่าตั้ง) เมื่อคู่ค้าแจ้งว่ารับเงินแล้ว",
            "เมื่อรู้บัญชีที่จ่ายจากรายการเดินบัญชีของเรา หรือผู้ซื้อเลือกบัญชีตอนจ่ายจากระบบ และยอดไม่เกินเพดาน · บัญชีตั้งต้นที่ตั้งไว้ที่นี่เป็นค่าเลือกไว้ให้ในปุ่ม “บันทึกจ่าย” "
                + "(กดครั้งเดียว) ไม่ใช่เหตุให้ระบบบันทึกเอง",
            StopsCommon + " · การจ่ายที่บันทึกไปแล้วไม่ถูกย้อน"),
        Scope.ReceiveSlipCopy => new(
            "รับสำเนาสลิปที่เราส่งให้คู่ค้าตอนชำระ (เฉพาะรูปสลิปของรายการนั้น) มาแนบที่ใบซื้อของเรา เป็นหลักฐานการจ่าย",
            "หลังคู่ค้ายืนยันว่าได้รับเงินจากสลิปนั้นแล้ว — เฉพาะการชำระที่เกิดหลังให้ความยินยอม · สลิปช่วยเลือกบัญชีที่จ่ายไว้ให้ แต่ไม่พอให้ระบบบันทึกจ่ายเอง",
            StopsCommon + " · สลิปที่รับมาแล้วยังอยู่กับใบซื้อตามระยะเก็บเอกสารบัญชี"),
        Scope.ReturnWhtCerts => new(
            "ส่งหนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ) ที่เราออกให้คู่ค้า เข้ากล่องรับในระบบของคู่ค้า",
            "ทันทีที่ออก 50 ทวิ หลังเปิดค่าตั้ง",
            "ปิดค่าตั้งของทั้งบริษัท หรือระงับการเชื่อมกับคู่ค้า"),
        Scope.AcceptPayerSlipCopy => new(
            "รับสลิปที่คู่ค้า (ผู้ซื้อของเรา) แนบตอนแจ้งโอนจากระบบของเขา เข้าคิว “รายการรอยืนยันรับเงิน” — ยังไม่ใช่การรับเงิน",
            "ทันทีที่คู่ค้าแจ้งโอน",
            StopsCommon),
        _ => new("", "", StopsCommon),
    };

    /// <summary>ป้ายสถานะบนหน้า (ตัวเดียว · server computes) — ไม่มีความยินยอม = “ยังไม่ได้ยินยอม” (ไม่ใช่ “ไม่ทำงาน”)</summary>
    public static string StatusLabel(ScopeDef d, Verdict v)
    {
        if (v.Ok) return d.CompanyWideToggle ? "เปิดอยู่ (ทั้งบริษัท)" : "ใช้อยู่";
        return v.Code switch
        {
            Codes.NoConsent or Codes.Withdrawn => "ยังไม่ได้ยินยอม",
            Codes.CompanyWideOff => "ปิดอยู่ (ทั้งบริษัท)",
            Codes.GrantedBeforeWired or Codes.TextOutdated or Codes.StaleAfterSuspend or Codes.Tampered => "ต้องยืนยันใหม่",
            Codes.GrantorLostPermission => "หยุดอยู่ — ผู้ให้หมดสิทธิ์",
            Codes.PartnerNotActive or Codes.NoPartner => "หยุดอยู่ — การเชื่อมไม่ได้ใช้งาน",
            _ => "หยุดอยู่",
        };
    }

    /// <summary>สถานะการเชื่อมเป็นภาษาผู้ใช้ (ไม่ส่งชื่อ enum ขึ้นจอ)</summary>
    public static string PartnershipStatusLabel(TradingPartnershipStatus s) => s switch
    {
        TradingPartnershipStatus.Pending => "รอตอบรับ",
        TradingPartnershipStatus.Accepted => "เชื่อมอยู่",
        TradingPartnershipStatus.Suspended => "ระงับอยู่",
        TradingPartnershipStatus.Rejected => "ปฏิเสธ/ยกเลิกแล้ว",
        _ => "ไม่ทราบ",
    };

    // ═══════════ audit ของค่าตั้งเดิมระดับบริษัท (C-06) ═══════════

    /// <summary>ภาพค่าตั้ง workflow ข้ามบริษัท (ชื่อช่อง → ค่าเป็นสตริงคงรูป) — ใช้ทั้งก่อนและหลังแก้ใน <c>PUT workflow-automation</c> (ตัวเดียว)</summary>
    public static IReadOnlyDictionary<string, string?> ConfigSnapshot(WorkflowAutomationConfig c, bool? partnerDiscoverable)
    {
        static string? Money2(decimal? v) => v?.ToString("0.00", CultureInfo.InvariantCulture);
        static string B(bool v) => v ? "true" : "false";
        return new Dictionary<string, string?>
        {
            ["autoApproveIncomingQuotations"] = B(c.AutoApproveIncomingQuotations),
            ["autoApproveMinAmount"] = Money2(c.AutoApproveMinAmount),
            ["autoApproveMaxAmount"] = Money2(c.AutoApproveMaxAmount),
            ["autoCreatePoOnQuotationApproval"] = B(c.AutoCreatePoOnQuotationApproval),
            ["autoCreateInvoiceFromIncomingPo"] = B(c.AutoCreateInvoiceFromIncomingPo),
            ["autoCreateReceiptFromIncomingPayment"] = B(c.AutoCreateReceiptFromIncomingPayment),
            ["autoStampSignature"] = B(c.AutoStampSignature),
            ["defaultApproverUserId"] = c.DefaultApproverUserId?.ToString("D"),
            ["defaultSignatureId"] = c.DefaultSignatureId?.ToString("D"),
            ["notifyOnIncomingDocument"] = B(c.NotifyOnIncomingDocument),
            // ฝ่ายค้าน L6: อีเมลบุคคลไม่เข้าแถว audit chain (ลบไม่ได้) — เก็บลายนิ้วมือสั้นพอให้เห็นว่า "เปลี่ยน" เท่านั้น
            ["notifyEmail"] = string.IsNullOrWhiteSpace(c.NotifyEmail) ? null
                : "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.NotifyEmail.Trim().ToLowerInvariant())))[..12].ToLowerInvariant(),
            ["createPurchaseDraftsFromPartners"] = B(c.CreatePurchaseDraftsFromPartners),
            ["sendWhtCertsToPartners"] = B(c.SendWhtCertsToPartners),
            ["sendWhtCertsToPartnersSince"] = c.SendWhtCertsToPartnersSince?.ToString("O", CultureInfo.InvariantCulture),
            ["partnerDiscoverable"] = partnerDiscoverable is bool d ? B(d) : null,
        };
    }

    /// <summary>ชื่อช่องที่เปลี่ยน (ลำดับคงที่) · ว่าง = ไม่มีอะไรเปลี่ยน ⇒ ไม่เขียน audit</summary>
    public static List<string> ConfigDiff(IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after)
        => after.Keys.Where(k => !before.TryGetValue(k, out var b) || !string.Equals(b, after[k], StringComparison.Ordinal)).ToList();

    /// <summary>ลิงก์หน้าความยินยอมของคู่นี้ (ใช้ในกระดิ่งคำขอ + แถวคู่ค้า)</summary>
    public static string PageUrl(Guid partnershipId) => "/pages/partner-automation.html?partnership=" + partnershipId.ToString("D");

    /// <summary>ผลตรวจตอนใช้ที่ส่งให้ท่อ (P3/P5) — <see cref="ConsentId"/>/<see cref="GrantorUserId"/> ใช้อ้างใน audit/actor ของงานอัตโนมัติ (264-2-3)</summary>
    /// <param name="DefaultPayBankAccountId">บัญชีจ่ายที่ผู้ให้ประกาศ — มีค่าเฉพาะเมื่อ Ok และบัญชียังใช้ได้ ณ ตอนนี้ (ของบริษัท · ใช้งาน · ไม่ถูกลบ) · ยังต้องผ่านตัวตรวจบัญชีของท่อ</param>
    /// <param name="PayAccountProblem">ฝ่ายค้าน M2: ประกาศบัญชีไว้แต่ใช้ไม่ได้แล้ว ⇒ PayAccountInactive (ท่อใช้หลักฐานเท่านั้น · ไม่เดาบัญชีอื่น) · null = ไม่มีปัญหา/ไม่ได้ประกาศ</param>
    public sealed record UseCheck(bool Ok, string Code, string Reason, Guid? ConsentId, Guid? GrantorUserId, decimal? MaxAmount, Guid? DefaultPayBankAccountId,
        string? PayAccountProblem = null)
    {
        public static UseCheck From(Verdict v, PartnerAutomationConsent? row, bool payAccountUsable = true)
            => new(v.Ok, v.Code, v.Reason, row?.Id, row?.GrantedByUserId, row?.MaxAmount,
                v.Ok && payAccountUsable ? row?.DefaultPayBankAccountId : null,
                v.Ok && !payAccountUsable && row?.DefaultPayBankAccountId != null ? PayAccountInactive : null);
    }

    public const string PayAccountInactive = "PayAccountInactive";

    /// <summary>ตัดเหตุการถอนให้สั้น (ไม่บังคับ)</summary>
    public static string? CleanReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var r = reason.Trim();
        return r.Length > MaxReasonLength ? r[..MaxReasonLength] : r;
    }
}
