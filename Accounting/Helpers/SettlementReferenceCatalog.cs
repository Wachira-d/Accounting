using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ตัวเลือก 1 ค่าของ enum สำหรับหน้าเว็บ — <c>Value</c> = ชื่อ enum (API ส่ง/รับเป็นชื่อ) · <c>Label</c> = ป้ายไทยที่ผู้ใช้เห็น</summary>
public sealed record SettlementEnumOption(string Value, string Label, string? Description = null);

/// <summary>ประเภทบรรทัด 1 ค่า — ป้าย/คุณสมบัติมาจาก <see cref="SettlementLineTypeRules"/> ตัวเดียว (หน้าเว็บไม่มีตารางป้ายเอง)</summary>
/// <param name="Postable">เลือกเป็นคำตอบได้ ("รอจัดประเภท" ไม่ใช่คำตอบ)</param>
/// <param name="RequiresReason">ต้องระบุเหตุผล + ผังบัญชีเมื่อเลือก (Adjustment)</param>
/// <param name="Posting">ทางลงบัญชี (ชื่อ <see cref="SettlementPostingKind"/>) — หน้าเว็บใช้ซ่อนตัวเลือก "เข้าใบขายสรุป" ของบรรทัดคืนเงิน</param>
public sealed record SettlementLineTypeOption(string Value, string Label, bool Postable, bool RequiresReason, bool IsFee,
    bool RequiresSaleMatch, string Posting);

/// <summary>บทบาทผังที่ตั้งเองได้ใน <c>FeeAccountMapJson</c> + ผังมาตรฐานที่ใช้เมื่อไม่ตั้ง</summary>
public sealed record SettlementFeeRoleOption(string Role, string Label, string? DefaultAccountCode);

/// <summary>ช่องของการจับคู่คอลัมน์แบบยาว (<c>SettlementColumnMap</c> — ชื่อคีย์ camelCase ตรงกับ JSON ที่ service อ่าน)</summary>
public sealed record SettlementColumnFieldOption(string Key, string Label, bool Required, string? Hint);

/// <summary>ชุดข้อมูลอ้างอิงของหน้า settlement ทั้งหมด (<c>GET settlement/reference</c>)</summary>
public sealed record SettlementReferenceData(
    IReadOnlyList<SettlementLineTypeOption> LineTypes,
    IReadOnlyList<SettlementEnumOption> ChannelKinds,
    IReadOnlyList<SettlementEnumOption> BatchStatuses,
    IReadOnlyList<SettlementEnumOption> MatchStatuses,
    IReadOnlyList<SettlementEnumOption> ClassifiedBy,
    IReadOnlyList<SettlementEnumOption> SourceKinds,
    IReadOnlyList<SettlementEnumOption> FeeVatModes,
    IReadOnlyList<SettlementEnumOption> FeeWhtModes,
    IReadOnlyList<SettlementEnumOption> RevenueModels,
    IReadOnlyList<SettlementFeeRoleOption> FeeRoles,
    IReadOnlyList<SettlementColumnFieldOption> ColumnFields,
    IReadOnlyList<SettlementEnumOption> DateOrders,
    IReadOnlyList<SettlementEnumOption> BatchFilterStatuses,
    IReadOnlyList<SettlementEnumOption> TimeZones);

/// <summary>
/// **ป้ายไทยของ enum settlement ทั้งชุด — ตัวตั้งตัวเดียวของหน้าเว็บ** (รอบ 198 เฟส 1 ทีม D · CLAUDE.md F2 ข้อ 4/5)
///
/// <para>═══ ทำไม ═══ หน้าเว็บเคยพิมพ์ตาราง <c>{1:'…',2:'…'}</c> เองแล้ว API ส่งค่ามาเป็นชื่อ ⇒ ป้ายเป็น "-" / select เลือกตัวแรกเงียบ
/// (ทีมตรวจรอบ 189 F-01/F-02) · ตัวนี้คืนป้ายทุกค่าจากเซิร์ฟเวอร์ ⇒ เพิ่มค่า enum ใหม่แล้วลืมป้าย = เทสต์ล้ม (ไม่ใช่หน้าจอโชว์ชื่ออังกฤษ)</para>
///
/// <para>ป้ายประเภทบรรทัดมาจาก <see cref="SettlementLineTypeRules.All"/> (ห้ามเขียนซ้ำที่นี่) · ผังมาตรฐานของบทบาทจาก
/// <see cref="SettlementAccountRoles.DefaultCode"/> · G6: pure</para>
/// </summary>
public static class SettlementReferenceCatalog
{
    public static SettlementReferenceData Build() => new(
        SettlementLineTypeRules.All
            .Select(r => new SettlementLineTypeOption(r.Type.ToString(), r.LabelTh, r.Postable, r.RequiresReason, r.IsFee,
                r.RequiresSaleMatch, r.Posting.ToString()))
            .ToList(),
        Options<SettlementChannelKind>(ChannelKindLabel),
        Options<SettlementBatchStatus>(BatchStatusLabel),
        Options<SettlementMatchStatus>(MatchStatusLabel),
        Options<SettlementClassifiedBy>(ClassifiedByLabel),
        Options<SettlementSourceKind>(SourceKindLabel),
        Options<SettlementFeeVatMode>(FeeVatModeLabel),
        Options<SettlementFeeWhtMode>(FeeWhtModeLabel),
        Options<SettlementRevenueModel>(RevenueModelLabel),
        SettlementAccountRoles.Mappable
            .Select(r => new SettlementFeeRoleOption(r, FeeRoleLabel(r), SettlementAccountRoles.DefaultCode(r)))
            .ToList(),
        ColumnFields,
        new[]
        {
            new SettlementEnumOption("Auto", "ให้ระบบดูจากทั้งไฟล์"),
            new SettlementEnumOption("DayMonthYear", "วัน/เดือน/ปี"),
            new SettlementEnumOption("MonthDayYear", "เดือน/วัน/ปี"),
        },
        Options<SettlementBatchStatus>(BatchStatusLabel).Where(o => IsListable(Enum.Parse<SettlementBatchStatus>(o.Value))).ToList(),
        // review198-B R-B8 (ทีม I รอบ 200): เขตเวลาของ "วันที่+เวลา" ที่ไม่มี offset ในไฟล์ — ค่าตรงกับ SettlementFileTimeZone (ชื่อ enum)
        new[]
        {
            new SettlementEnumOption("Auto", "ให้ระบบดูจากหัวคอลัมน์ (ถามเมื่อไม่แน่ใจ)"),
            new SettlementEnumOption("Bangkok", "เวลาไทย (UTC+7)"),
            new SettlementEnumOption("Utc", "UTC (ระบบบวก 7 ชั่วโมงก่อนตัดวัน)"),
        });

    /// <summary>
    /// **สถานะที่รายการรอบโอนกรองได้** (review198-D D-05) — รอบที่ยกเลิกแล้วถูก soft-delete (<c>IsDeleted</c> + ตัวกรองส่วนกลางของ EF) ⇒
    /// ไม่มีทางอยู่ในรายการ · ตัวกรอง "ยกเลิกแล้ว" จึงคืนว่างเสมอ (ผู้ใช้เข้าใจว่าไม่เคยมีรอบที่ยกเลิก) — ตัดออกจากตัวเลือก และ endpoint รายการตอบ 400
    /// พร้อมเหตุผลถ้าถูกส่งมา · ประวัติการยกเลิกอยู่ในประวัติการแก้ไข (audit) ของรอบโอนนั้น
    /// </summary>
    public static bool IsListable(SettlementBatchStatus s) => s != SettlementBatchStatus.Voided;

    /// <summary>ข้อความเมื่อกรองด้วยสถานะที่ไม่มีทางอยู่ในรายการ (ไม่ใช่ 200 กับรายการว่าง — F2 ข้อ 7)</summary>
    public const string VoidedNotListedMessage =
        "รอบโอนที่ยกเลิกแล้วไม่อยู่ในรายการ (ถูกลบแบบเก็บหลักฐาน — นำเข้าไฟล์เดิมใหม่ได้) · ดูเหตุผล/ผู้ยกเลิกได้ที่ประวัติการแก้ไขของระบบ";

    private static IReadOnlyList<SettlementEnumOption> Options<T>(Func<T, (string Label, string? Description)> label)
        where T : struct, Enum
        => Enum.GetValues<T>().Select(v =>
        {
            var (l, d) = label(v);
            return new SettlementEnumOption(v.ToString(), l, d);
        }).ToList();

    private static (string, string?) ChannelKindLabel(SettlementChannelKind k) => k switch
    {
        SettlementChannelKind.Gateway => ("Payment gateway", "ผู้ให้บริการรับชำระเงินออนไลน์ (บัตร/QR)"),
        SettlementChannelKind.Marketplace => ("Marketplace", "ร้านบนแพลตฟอร์มขายของออนไลน์"),
        SettlementChannelKind.Ota => ("OTA (เว็บจองที่พัก)", "ยังลงบัญชีไม่ได้ในเฟส 1"),
        SettlementChannelKind.CardAcquirer => ("เครื่องรูดบัตร (EDC)", "ธนาคารหักค่าธรรมเนียม MDR ก่อนโอน"),
        SettlementChannelKind.Delivery => ("แอปส่งอาหาร", null),
        SettlementChannelKind.Other => ("อื่น ๆ", null),
        _ => (k.ToString(), null),
    };

    private static (string, string?) BatchStatusLabel(SettlementBatchStatus s) => s switch
    {
        SettlementBatchStatus.Imported => ("นำเข้าแล้ว (ยังมีบรรทัดรอจัดประเภท)", null),
        SettlementBatchStatus.Classified => ("จัดประเภทครบ (รอตัดสินการจับคู่)", null),
        SettlementBatchStatus.Matched => ("พร้อมลงบัญชี", null),
        SettlementBatchStatus.Posted => ("ลงบัญชีแล้ว", "รอจับคู่เงินเข้าธนาคาร"),
        SettlementBatchStatus.BankMatched => ("จับคู่เงินเข้าธนาคารแล้ว", null),
        SettlementBatchStatus.Voided => ("ยกเลิกแล้ว", null),
        _ => (s.ToString(), null),
    };

    private static (string, string?) MatchStatusLabel(SettlementMatchStatus s) => s switch
    {
        SettlementMatchStatus.Unmatched => ("ยังไม่จับคู่ — รอตัดสิน", null),
        SettlementMatchStatus.Matched => ("จับคู่เอกสารแล้ว", null),
        SettlementMatchStatus.AutoSummary => ("เข้าใบขายสรุปรายวัน", "ไม่พบเอกสารขายในระบบ — ระบบจะออกใบขายสรุปรายวันให้และติดป้ายให้ตรวจ"),
        SettlementMatchStatus.NotRequired => ("ไม่ต้องจับคู่", null),
        SettlementMatchStatus.AmountMismatch => ("ยอดไม่ตรงเอกสาร — ต้องตรวจ", null),
        _ => (s.ToString(), null),
    };

    private static (string, string?) ClassifiedByLabel(SettlementClassifiedBy c) => c switch
    {
        SettlementClassifiedBy.None => ("ยังไม่จัดประเภท", null),
        SettlementClassifiedBy.AdapterRule => ("⚙️ ระบบแนะนำ (ป้ายที่รู้จัก)", null),
        SettlementClassifiedBy.Learned => ("⚙️ ระบบแนะนำ (จากที่เคยเลือก)", null),
        SettlementClassifiedBy.Ai => ("🤖 AI แนะนำ (ผ่านด่านชุดประเภทแล้ว)", "ครู (AI ภายนอก) ถูกเรียกจริงตอนจัดประเภทบรรทัดนี้"),
        SettlementClassifiedBy.User => ("👤 ผู้ใช้เลือก", null),
        _ => (c.ToString(), null),
    };

    private static (string, string?) SourceKindLabel(SettlementSourceKind s) => s switch
    {
        SettlementSourceKind.CsvImport => ("นำเข้าไฟล์", null),
        SettlementSourceKind.PaymentIntents => ("จากรายการรับชำระออนไลน์ในระบบ", null),
        SettlementSourceKind.Manual => ("บันทึกมือ", null),
        SettlementSourceKind.Api => ("API", null),
        _ => (s.ToString(), null),
    };

    private static (string, string?) FeeVatModeLabel(SettlementFeeVatMode m) => m switch
    {
        SettlementFeeVatMode.ThaiVat7 => ("VAT 7% (ผู้ให้บริการไทย)", "ภาษีซื้อรอใบกำกับรายเดือน"),
        SettlementFeeVatMode.ForeignPp36 => ("ผู้ให้บริการต่างประเทศ — ภ.พ.36",
            "ผู้จ่ายประเมิน VAT เอง §83/6 (ไม่จด VAT ก็ต้องยื่น — VAT เป็นต้นทุน) · ถ้าหัก ณ ที่จ่าย = ม.70 ภ.ง.ด.54"),
        SettlementFeeVatMode.None => ("ไม่มี VAT", null),
        _ => (m.ToString(), null),
    };

    private static (string, string?) FeeWhtModeLabel(SettlementFeeWhtMode m) => m switch
    {
        SettlementFeeWhtMode.None => ("ไม่หัก ณ ที่จ่าย", null),
        SettlementFeeWhtMode.AgentWithholds => ("แพลตฟอร์มเป็นตัวแทนหัก/ยื่นแทน", "เก็บ 50 ทวิ แต่ไม่นับเข้ายอดที่เรายื่นเอง"),
        SettlementFeeWhtMode.SelfWithholdReimbursed => ("เราหักเองแล้วแพลตฟอร์มคืนให้", "ตั้งลูกหนี้แพลตฟอร์มรอคืน"),
        SettlementFeeWhtMode.SelfWithholdPayerBorne => ("เราออกภาษีแทน",
            "แพลตฟอร์มหักค่าธรรมเนียมเต็มไปแล้ว — ภาษี = ฐาน × r/(100−r) (ในประเทศ 3% = 3/97 · ต่างประเทศ ม.70 "
            + $"{ForeignWhtRateResolver.Section70GeneralRate:0.##}% = {ForeignWhtRateResolver.Section70GeneralRate:0.##}/{100m - ForeignWhtRateResolver.Section70GeneralRate:0.##})"),
        _ => (m.ToString(), null),
    };

    private static (string, string?) RevenueModelLabel(SettlementRevenueModel m) => m switch
    {
        SettlementRevenueModel.GrossWithFees => ("รายได้เต็มจำนวน + ค่าธรรมเนียมเป็นค่าใช้จ่าย", "ค่าเริ่มต้น (ตัวการ · TFRS NPAEs)"),
        SettlementRevenueModel.NetRate => ("รายได้ราคาสุทธิ (ซื้อมาขายต่อ)", "ยังลงบัญชีไม่ได้ในเฟส 1"),
        _ => (m.ToString(), null),
    };

    private static string FeeRoleLabel(string role) => role switch
    {
        SettlementAccountRoles.Commission => "ค่าคอมมิชชัน",
        SettlementAccountRoles.PaymentFee => "ค่าธรรมเนียมรับชำระเงิน",
        SettlementAccountRoles.Shipping => "ค่าขนส่ง",
        SettlementAccountRoles.Ads => "ค่าโฆษณา",
        SettlementAccountRoles.ServiceFee => "ค่าบริการแพลตฟอร์ม",
        SettlementAccountRoles.WithdrawalFee => "ค่าธรรมเนียมถอนเงิน",
        SettlementAccountRoles.WhtCredit => "ภาษีที่แพลตฟอร์มหัก ณ ที่จ่ายจากเรา",
        SettlementAccountRoles.FxGain => "กำไรจากอัตราแลกเปลี่ยน",
        SettlementAccountRoles.FxLoss => "ขาดทุนจากอัตราแลกเปลี่ยน",
        SettlementAccountRoles.ChargebackLoss => "ขาดทุนจาก chargeback",
        _ => role,
    };

    /// <summary>ช่องของไฟล์แบบยาว — คีย์ตรงกับ property ของ <c>SettlementColumnMap</c> (camelCase) · ข้อกำหนด "ต้องมี" เป็นแค่ป้ายช่วย
    /// (ตัวตัดสินจริง = <c>SettlementColumnMap.Validate</c> ที่ service รันตอนนำเข้า)</summary>
    private static readonly IReadOnlyList<SettlementColumnFieldOption> ColumnFields = new[]
    {
        new SettlementColumnFieldOption("type", "ประเภทรายการ", true, "ยอดขาย · ค่าคอม · ค่าธรรมเนียม · คืนเงิน ฯลฯ"),
        new SettlementColumnFieldOption("amount", "ยอดเงิน (มีเครื่องหมาย)", false, "ใช้คอลัมน์นี้ หรือคู่ยอดเข้า/ยอดออก อย่างใดอย่างหนึ่ง"),
        new SettlementColumnFieldOption("amountIn", "ยอดเข้า wallet", false, null),
        new SettlementColumnFieldOption("amountOut", "ยอดออกจาก wallet", false, "ค่าบวกในไฟล์ ระบบกลับเป็นยอดหัก"),
        new SettlementColumnFieldOption("orderId", "เลขออเดอร์", false, "ใช้จับคู่เอกสารขาย"),
        new SettlementColumnFieldOption("txnId", "เลขรายการ", false, "ใช้กันนำเข้าซ้ำ"),
        new SettlementColumnFieldOption("date", "วันที่รายการ", false, null),
        new SettlementColumnFieldOption("description", "คำอธิบาย", false, "ระบบตัดข้อมูลส่วนบุคคลออกก่อนเก็บ"),
        new SettlementColumnFieldOption("vat", "VAT", false, null),
        new SettlementColumnFieldOption("wht", "ภาษีหัก ณ ที่จ่าย", false, null),
        new SettlementColumnFieldOption("payoutRef", "เลขรอบโอน", false, null),
    };
}
