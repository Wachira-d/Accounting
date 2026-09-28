namespace Accounting.Models.Enums;

// ═══════════════════════════════════════════════════════════════════════
// Settlement (wallet → ธนาคาร) — รอบ 198 เฟส 1 ทีม A
// ที่มา: erp-review/2026-09-25/settlement/{DECISIONS,report-S1,report-S2}.md
//
// ⚠️ ทุกค่าในไฟล์นี้ **ถูกเก็บลงฐานเป็นตัวเลข** — ห้ามเปลี่ยนเลขของค่าที่มีอยู่ ·
// เพิ่มค่าใหม่ต่อท้ายเท่านั้น · API ส่งออกเป็น "ชื่อ" (JsonStringEnumConverter ใน Program.cs)
// หน้าเว็บห้ามตัดสินด้วยตัวเลข (tools/enum_number_compare_check.py)
// · SettlementLineType ทุกค่าต้องมีกติกาใน Helpers/SettlementLineTypeRules
//   (tools/settlement_line_type_rules_check.py)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>ชนิดของช่องทางที่เงินพักอยู่ก่อนโอนเข้าธนาคาร</summary>
public enum SettlementChannelKind
{
    /// <summary>Payment gateway (Omise · 2C2P · GB Prime Pay · Stripe · KBank/SCB)</summary>
    Gateway = 1,
    /// <summary>Marketplace (Shopee · Lazada · TikTok Shop · LINE MyShop)</summary>
    Marketplace = 2,
    /// <summary>OTA (Agoda · Booking.com · Expedia · Trip.com · Traveloka) — เฟส 4</summary>
    Ota = 3,
    /// <summary>เครื่องรูดบัตร/EDC ของธนาคาร (MDR)</summary>
    CardAcquirer = 4,
    /// <summary>แอปส่งอาหาร (Grab · LINE MAN · Robinhood)</summary>
    Delivery = 5,
    Other = 9,
}

/// <summary>สถานะของรอบโอน 1 รอบ (1 batch = 1 payout ของผู้ให้บริการ)</summary>
public enum SettlementBatchStatus
{
    /// <summary>นำเข้าแล้ว ยังมีบรรทัดที่ยังไม่จัดประเภท/ยังไม่ตรวจ</summary>
    Imported = 0,
    /// <summary>ทุกบรรทัดจัดประเภทแล้ว (ไม่มี Unclassified)</summary>
    Classified = 1,
    /// <summary>บรรทัดขายจับคู่ใบขายแล้ว หรือถูกกำหนดให้เข้าใบขายสรุปรายวัน</summary>
    Matched = 2,
    /// <summary>ลงบัญชีแล้ว (JE รอบโอน + ใบค่าธรรมเนียม + รับชำระ)</summary>
    Posted = 3,
    /// <summary>เงินเข้าธนาคารจับคู่กับรายการเดินบัญชีแล้ว</summary>
    BankMatched = 4,
    Voided = 9,
}

/// <summary>บรรทัดของ batch มาจากไหน</summary>
public enum SettlementSourceKind
{
    /// <summary>ไฟล์ CSV/Excel ที่ผู้ใช้อัปโหลด (adapter จับคู่คอลัมน์)</summary>
    CsvImport = 1,
    /// <summary>ประกอบจาก <c>PaymentIntent</c> ของ gateway ในระบบเราเอง</summary>
    PaymentIntents = 2,
    Manual = 3,
    Api = 4,
}

/// <summary>ประเภทบรรทัดใน settlement report — 19 ค่าตาม report-S2 §3.2
/// <para>ความหมาย/เครื่องหมาย/ผังของแต่ละค่า อยู่ใน <c>Helpers/SettlementLineTypeRules</c> ตัวเดียว</para>
/// <para><b>เครื่องหมายของ <c>SettlementLine.Amount</c></b> = มุมมองยอดใน wallet: บวก = แพลตฟอร์มค้างเราเพิ่ม ·
/// ลบ = ถูกหักออกจาก wallet</para></summary>
public enum SettlementLineType
{
    /// <summary>ยังไม่รู้ว่าเป็นอะไร — <b>ห้ามลงบัญชี</b> (ไม่ตกค่าใช้จ่ายอื่นเงียบ · report-S1 §7 ข้อ 2)</summary>
    Unclassified = 0,
    /// <summary>ยอดขาย (ราคาที่ผู้ซื้อจ่าย) — บวก</summary>
    Sale = 1,
    /// <summary>คืนเงินผู้ซื้อ — ลบ · ต้องมีใบขายเดิม (ใบลดหนี้ §86/10)</summary>
    Refund = 2,
    /// <summary>ผู้ถือบัตรปฏิเสธรายการ (dispute เปิด) — ลบ · พักไว้ลูกหนี้อื่นจนรู้ผล</summary>
    Chargeback = 3,
    /// <summary>ชนะ dispute ได้เงินคืน — บวก</summary>
    ChargebackReversal = 4,
    /// <summary>ค่าคอมมิชชันแพลตฟอร์ม</summary>
    Commission = 5,
    /// <summary>ค่าธรรมเนียมรับชำระเงิน (MDR / transaction fee)</summary>
    PaymentFee = 6,
    /// <summary>ค่าขนส่งที่แพลตฟอร์มเรียกเก็บจากร้าน</summary>
    ShippingFeeCharged = 7,
    /// <summary>แพลตฟอร์มช่วยค่าขนส่ง — บวก · ลดค่าขนส่ง</summary>
    ShippingSubsidy = 8,
    /// <summary>ส่วนลด/โค้ดที่ร้านออกเงินเอง — ลบ · <b>ลดยอดขาย</b> (ไม่ใช่ค่าใช้จ่าย · ไม่อยู่ในฐาน VAT §79)</summary>
    SellerVoucher = 9,
    /// <summary>โค้ด/coins ที่แพลตฟอร์มออกเงินแทนผู้ซื้อ — บวก · <b>เป็นส่วนของยอดขาย</b> (อยู่ในฐาน VAT)</summary>
    PlatformVoucherSubsidy = 10,
    /// <summary>ค่าโฆษณาบนแพลตฟอร์มที่หักจาก wallet</summary>
    AdsFee = 11,
    /// <summary>ค่าบริการอื่นของแพลตฟอร์ม (โปรแกรมส่งฟรี · ค่าบริการร้าน ฯลฯ)</summary>
    ServiceFee = 12,
    /// <summary>ค่าธรรมเนียมถอนเงิน/โอนออก</summary>
    WithdrawalFee = 13,
    /// <summary>แพลตฟอร์มกันเงินไว้ (reserve/holdback) — ลบ</summary>
    ReserveHold = 14,
    /// <summary>ปล่อยเงินที่กันไว้ — บวก</summary>
    ReserveRelease = 15,
    /// <summary>แพลตฟอร์มหักภาษี ณ ที่จ่าย<b>จากเงินได้ของเรา</b> (e-Withholding) — ลบ · เป็นเครดิตภาษีของเรา</summary>
    TaxWithheldByPlatform = 16,
    /// <summary>ผลต่างอัตราแลกเปลี่ยน — ±</summary>
    FxDifference = 17,
    /// <summary>ปรับปรุงอื่น — ± · <b>บังคับเหตุผล + ผังบัญชี</b></summary>
    Adjustment = 18,
}

/// <summary>ผลการจับคู่บรรทัดกับเอกสาร/รายการในระบบ</summary>
public enum SettlementMatchStatus
{
    /// <summary>ยังไม่จับคู่ (หรือยังไม่ได้ตรวจ)</summary>
    Unmatched = 0,
    /// <summary>จับคู่กับใบขาย/intent/การรับชำระแล้ว</summary>
    Matched = 1,
    /// <summary>จับคู่ไม่ได้ ⇒ เข้าใบขายสรุปรายวันอัตโนมัติ + ติดป้ายให้ตรวจ (DECISIONS ข้อ 3)</summary>
    AutoSummary = 2,
    /// <summary>ประเภทนี้ไม่ต้องจับคู่ (ค่าธรรมเนียม · reserve ฯลฯ)</summary>
    NotRequired = 3,
    /// <summary>จับคู่ได้แต่ยอดไม่ตรงเอกสาร — ต้องตรวจ</summary>
    AmountMismatch = 4,
}

/// <summary>ใครเป็นผู้จัดประเภทบรรทัด — ใช้แยกตัวชี้วัด "local โตจริง" (DECISION_DOCTRINE §3)</summary>
public enum SettlementClassifiedBy
{
    None = 0,
    /// <summary>กติกาของ adapter (ชื่อคอลัมน์/ป้ายรายการที่รู้จัก)</summary>
    AdapterRule = 1,
    /// <summary>คลังที่เรียนจากที่ผู้ใช้ยืนยัน (student ของ <c>AiFeatureKey.SettlementLineClassify</c>)</summary>
    Learned = 2,
    /// <summary>ครู (AI ภายนอก) ตอบ และผ่านด่านแล้ว</summary>
    Ai = 3,
    /// <summary>ผู้ใช้เลือกเอง</summary>
    User = 4,
}

/// <summary>VAT บนค่าธรรมเนียมของช่องทาง (report-S1 §3/§5)</summary>
public enum SettlementFeeVatMode
{
    /// <summary>ผู้ให้บริการไทยเก็บ VAT 7% — ภาษีซื้อ (พักที่ 11630 จนได้ใบกำกับ)</summary>
    ThaiVat7 = 1,
    /// <summary>ผู้ให้บริการต่างประเทศ — ประเมิน VAT เอง ภ.พ.36 §83/6 (11640 / 21912)</summary>
    ForeignPp36 = 2,
    /// <summary>ไม่มี VAT บนค่าธรรมเนียม</summary>
    None = 3,
}

/// <summary>หัก ณ ที่จ่ายบนค่าธรรมเนียมที่เราจ่ายแพลตฟอร์ม (report-S1 D5 · §4 W1–W3) — ค่าเริ่มต้น None + เตือนนิติบุคคล</summary>
public enum SettlementFeeWhtMode
{
    None = 0,
    /// <summary>W1 แพลตฟอร์มเป็นตัวแทนหัก/ยื่นแทนเรา — เก็บ 50 ทวิ แต่<b>ห้ามนับเข้ายอดที่เรายื่นเอง</b></summary>
    AgentWithholds = 1,
    /// <summary>W2 เราหักเองแล้วแพลตฟอร์มคืนให้ (Stripe) — ตั้งลูกหนี้แพลตฟอร์มรอคืน</summary>
    SelfWithholdReimbursed = 2,
    /// <summary>W3 เราออกภาษีแทน (แพลตฟอร์มหักค่าธรรมเนียมเต็มไปแล้ว) — ภาษี = ฐาน × 3/97</summary>
    SelfWithholdPayerBorne = 3,
}

/// <summary>วิธีรับรู้รายได้ของช่องทาง (DECISIONS ข้อ 1)</summary>
public enum SettlementRevenueModel
{
    /// <summary>รายได้เต็มจำนวน + ค่าธรรมเนียมเป็นค่าใช้จ่าย (ตัวการ · TFRS NPAEs) — ค่าเริ่มต้น</summary>
    GrossWithFees = 1,
    /// <summary>รายได้ = ราคาสุทธิที่ขายให้แพลตฟอร์ม (OTA ซื้อมาขายต่อ) — <b>ยังไม่รองรับในเฟส 1</b> (เฟส 4)</summary>
    NetRate = 2,
}
