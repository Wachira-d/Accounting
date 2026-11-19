namespace Accounting.Tests;

/// <summary>
/// รอบ 272 แพ็ก P1 — ชุดสลิปชำระภาษี <b>สังเคราะห์ตามรูปที่ปิดบังแล้ว</b> (ดีไซน์ §2.1: K BIZ "Bill Payment Completed" ของเจ้าของ) ·
/// <b>ไม่มีค่าจริงจากสลิปใดเลย</b>: เลขผู้เสียภาษี = เลขสังเคราะห์ที่ checksum ผ่าน (0009990000018 · 0009990000026) · Control Code/เลขที่รายการ = ลำดับตัวแทน
/// 13579 02468 (รูปเดียวกับตัวแทนค่าของ <c>tools/slip_secret_check.py</c>) · ชื่อ ⇒ <c>&lt;PAYER_NAME&gt;</c> · เลขบัญชีปิดบางเหลือ 4 หลักสังเคราะห์ · ยอด/วันที่สังเคราะห์ ·
/// <b>ไม่ commit รูป</b> · เมื่อเจ้าของส่งเอกสารจริงเพิ่ม (D-1) ให้แทนค่าแบบเดียวกับ <c>SlipCorpusRound264</c> แล้วเพิ่มแฮชค่าจริงลง <c>tools/slip_secret_hashes.txt</c> ก่อน commit
/// </summary>
public static class SlipCorpusRound272
{
    public const string ClientTaxId = "0009990000018";
    public const string OtherClientTaxId = "0009990000026";
    public const string ControlCode = "135790246813579";
    public const string TxnNo = "BILS902468013579024";

    /// <summary>K BIZ จ่ายบิลกรมสรรพากร (comp code 98037) — รูปแบบเดียวกับสลิปจริงของเจ้าของ (ป้าย Comp Code · Personal ID/Tax ID No. · Control Code)</summary>
    public static string KbizRd(string taxId = ClientTaxId, string control = ControlCode, string amount = "12,345.67", string fee = "0.00", string bankLine = "Kasikornbank",
        string date = "16 Nov 26 10:15", string compCode = "98037", string payee = "Revenue Department (Personal Income Tax)") => $"""
        Bill Payment Completed
        {date}           K BIZ
        From
             <PAYER_NAME>
        {bankLine}
        XXX-X-X1234-x
        To
        {compCode} {payee}
        Comp Code {compCode}
        Amount
        {amount} Baht
        Fee
        {fee} Baht
        Personal ID/Tax ID No. {taxId}
        Control Code {control}
        Transaction No. {TxnNo}
        """;

    /// <summary>สลิปไทยที่มีคำ "กรมสรรพากร" แต่ไม่มีป้าย Comp Code (ธนาคาร/แอปที่ยังไม่ยืนยัน)</summary>
    public const string ThaiRdNoCompCode = """
        ชำระเงินสำเร็จ
        16 พ.ย. 69 10:20
        จาก <PAYER_NAME>
        กรุงไทย
        XXX-X-XX123-4
        ไปยัง กรมสรรพากร
        จำนวนเงิน 5,000.00 บาท
        เลขที่รายการ 2468013579024
        """;
}
