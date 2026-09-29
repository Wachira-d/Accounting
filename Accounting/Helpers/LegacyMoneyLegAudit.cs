namespace Accounting.Helpers;

/// <summary>ผลตรวจขา JE 1 บรรทัดที่เข้าข่าย "ลงผังเงิน/ลูกหนี้ผิดหมวดจากโค้ดก่อนรอบ 198" — ข้อความ + ทางไปต่อสำหรับนักบัญชี</summary>
public sealed record LegacyMoneyLegFinding(string RuleCode, string Message, string Fix);

/// <summary>
/// **รายงานอ่านอย่างเดียวของ JE เก่าที่ลงขาเงิน/ลูกหนี้ผิดหมวด** (รอบ 198 I-1/P-1 · คำตัดสินรอบ 200 ข้อ 20: <b>รายงานให้นักบัญชีตรวจ ไม่แก้อัตโนมัติ</b> —
/// JE ที่อยู่ในงวดปิดแล้วห้ามแก้เงียบ · สอดคล้องคำตัดสินรอบ 193 ข้อ 14)
///
/// <para>ที่มา: ก่อนรอบ 198 <c>IntegrationService.CreatePaymentJournalAsync</c> ลงขาเงินของโอน/พร้อมเพย์/บัตรด้วย prefix "112"
/// (= 112xx <b>เงินลงทุนชั่วคราว</b> ในผังมาตรฐาน) และหาลูกหนี้ด้วย <c>StartsWith("113")</c> ไม่เรียง (ได้หัวกลุ่ม/ลูกหนี้อื่น/11340) ·
/// POS ที่ไม่ปักบัญชีบนเครื่องลง QR/โอน/e-Wallet ที่ 112xx และบัตรที่ 113xx ตัวไหนก็ได้ · โค้ดแก้แล้ว (<c>MoneyAccountFallback</c> ·
/// <c>TradeReceivableAccount</c>) แต่ JE ที่ลงไปแล้วยังอยู่ — SQL เดิม (<c>erp-review/2026-09-25/settlement/I1-legacy-query.sql</c>) นักบัญชีเปิดเองไม่ได้
/// ⇒ ต่อเข้าเครื่องมือตรวจโครงสร้าง JE ของหน้านักบัญชี (<c>JournalAnomalyService</c>) ที่มีอยู่แล้ว</para>
///
/// <para>จับด้วย<b>รูปข้อความที่โค้ดเก่าเขียนเอง</b> (คำอธิบาย JE/บรรทัด) + หมวดผัง — ไม่พึ่งค่าตัวเลขของ <c>JournalType</c>
/// (SQL เดิมใส่ <c>JournalType IN (2,3)</c> ซึ่งผิด: 2 = สมุดซื้อ · รับ/จ่าย = 3/4)</para>
/// </summary>
public static class LegacyMoneyLegAudit
{
    public const string IntegrationMoney112 = "JE-LEGACY-MONEY-112";
    public const string IntegrationReceivable = "JE-LEGACY-AR-113";
    public const string PosMoney = "JE-LEGACY-POS-MONEY";

    private const string Fix =
        "ให้นักบัญชีตรวจ — ระบบไม่แก้อัตโนมัติ (JE ในงวดที่ปิด/ยื่นภาษีแล้วห้ามแก้เงียบ) · ถ้ายืนยันว่าลงผิดผัง: บันทึก JE ปรับปรุงในงวดปัจจุบัน "
        + "ย้ายยอดจากผังนี้ไปผังที่ถูก (บัญชีธนาคารที่รับเงินจริง · 11340 ลูกหนี้บัตรเครดิต/ผู้ให้บริการรับชำระ · 11310 ลูกหนี้การค้า) พร้อมอ้างเลข JE นี้ · "
        + "ถ้าผังนี้ถูกตั้งใจ (เช่นปักผังลูกหนี้ไว้บนลูกค้า) ไม่ต้องทำอะไร";

    /// <param name="journalDescription"><c>JournalEntry.Description</c></param>
    /// <param name="lineDescription"><c>JournalEntryLine.Description</c></param>
    /// <param name="isDebit">ขาเดบิต (ยอดเดบิต &gt; 0)</param>
    /// <returns>null = ไม่เข้าข่าย</returns>
    public static LegacyMoneyLegFinding? Classify(string? journalDescription, string? lineDescription, string? accountCode, bool isDebit)
    {
        var code = (accountCode ?? "").Trim();
        var je = journalDescription ?? "";
        var line = lineDescription ?? "";
        if (code.Length == 0) return null;

        // I-1 ขาเงินของ JE รับ/จ่ายชำระจาก integration ("รับชำระ (banktransfer)" · "จ่ายชำระ (promptpay)")
        var integrationPayment = je.StartsWith("รับชำระ ", StringComparison.Ordinal) || je.StartsWith("จ่ายชำระ ", StringComparison.Ordinal);
        if (integrationPayment
            && (line.StartsWith("รับชำระ (", StringComparison.Ordinal) || line.StartsWith("จ่ายชำระ (", StringComparison.Ordinal))
            && code.StartsWith("112", StringComparison.Ordinal))
            return new LegacyMoneyLegFinding(IntegrationMoney112,
                $"ขาเงินของการรับ/จ่ายชำระจากระบบเชื่อมต่อลงผัง {code} (หมวด 112 = เงินลงทุนชั่วคราว) — โค้ดก่อนรอบ 198 ใช้ prefix \"112\" แทนบัญชีเงินจริง",
                Fix);

        // I-1 ขาลูกหนี้ของ JE รับชำระจาก integration ("ตัดลูกหนี้ INV-…") ที่ไม่ใช่ 11310 — เดิม StartsWith("113") ไม่เรียง
        if (je.StartsWith("รับชำระ ", StringComparison.Ordinal) && !isDebit
            && line.StartsWith("ตัดลูกหนี้ ", StringComparison.Ordinal)
            && code.StartsWith("113", StringComparison.Ordinal) && code != "11310")
            return new LegacyMoneyLegFinding(IntegrationReceivable,
                $"ขาลูกหนี้ของการรับชำระจากระบบเชื่อมต่อลงผัง {code} ไม่ใช่ 11310 ลูกหนี้การค้า — โค้ดก่อนรอบ 198 หยิบผัง 113xx ตัวแรกที่เจอ (ไม่เรียง)",
                Fix);

        // P-1 ขาเงินของ JE ขาย POS ("รับเงิน QR POS #…") ลง 112xx หรือบัตรลง 113xx ที่ไม่ใช่ 11340
        if (isDebit && line.StartsWith("รับเงิน ", StringComparison.Ordinal) && line.Contains(" POS #", StringComparison.Ordinal)
            && (code.StartsWith("112", StringComparison.Ordinal) || (code.StartsWith("113", StringComparison.Ordinal) && code != "11340")))
            return new LegacyMoneyLegFinding(PosMoney,
                $"ขาเงินของการขาย POS ลงผัง {code} — โค้ดก่อนรอบ 198 ตก prefix \"112\" (เงินลงทุนชั่วคราว) เมื่อเครื่องไม่ได้ปักบัญชี QR/โอน/e-Wallet "
                + "หรือหยิบ 113xx ตัวไหนก็ได้ให้บัตร (ที่ถูก = บัญชีธนาคาร/11340)",
                Fix);

        return null;
    }
}
