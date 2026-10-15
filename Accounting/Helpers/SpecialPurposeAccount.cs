namespace Accounting.Helpers;

/// <summary>
/// **ผังเฉพาะกิจ — ผังที่ระบบสร้าง/ใช้เพื่อจุดประสงค์เดียว ห้ามเป็น "ผังตั้งต้น" จากการหาตามหมวด/ตามรหัสต้น** (ฝ่ายค้านรอบส่งออก 2026-10-15 รอบ 2 P1-3)
///
/// <para><b>บั๊กที่มา:</b> ลำดับผังตั้งต้นรอบก่อน "ผังระบบก่อน" ⇒ ผังที่ migration ใส่ให้<b>ทุกบริษัท</b>เป็นผังระบบ (54960 ผลต่างจากการปัดเศษ — ไม่มีบัญชีแม่)
/// และผังที่ระบบสร้างเอง (21240 รับสินค้ายังไม่วางบิล · 1134x ผังพัก wallet · 11350 เงินที่ผู้ให้บริการกันไว้ · ผังย่อยของบัญชีธนาคาร) ชนะผังของผู้ใช้
/// ในบริษัทที่ใช้ผังของตัวเอง/ผังนำเข้า ⇒ ค่าใช้จ่ายตั้งต้นตกไป 54960 · ลูกหนี้/เจ้าหนี้ตามรหัสต้นได้ 11341/21240</para>
///
/// <para><b>กติกา:</b> ผังในรายการนี้ (รหัสจากเจ้าของเดิมของแต่ละผัง — ไม่ใช่รายการที่สอง) + ผังที่ผูกเป็นบัญชีธนาคาร/ผังพักของผู้ให้บริการรับชำระเงิน/
/// ผังพัก-ผังกันเงินของช่องทางรอบโอน ถูกตัดออกจากการหา "ตามหมวด" และ "ตามรหัสต้น" (<c>PostingAccountGuard.DefaultFallbackCandidates</c>) ·
/// การหาด้วย<b>รหัสเต็ม</b> (เช่น <c>FindAccountAsync("21240")</c>) ยังได้ผังนั้นตามเดิม</para>
/// </summary>
public static class SpecialPurposeAccount
{
    /// <summary>21240 เจ้าหนี้-รับสินค้ายังไม่วางบิล (GR/NI · 3-way match) — สร้างโดย <c>DocumentService.EnsureGrNiAccountAsync</c></summary>
    public const string GoodsReceivedNotInvoicedCode = "21240";

    /// <summary>รหัสผังเฉพาะกิจทั้งหมด (เรียงตามเลข) — ประกอบจากค่าคงที่ของเจ้าของแต่ละผัง</summary>
    public static readonly IReadOnlyList<string> Codes = BuildCodes();

    private static IReadOnlyList<string> BuildCodes()
    {
        var codes = new List<string>
        {
            DocumentRounding.AccountCode,                 // 54960 ผลต่างจากการปัดเศษ
            GoodsReceivedNotInvoicedCode,                 // 21240 GR/NI
            TradeReceivableAccount.GatewayClearingCode,   // 11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน (บัญชีพัก)
            WhtPayableAccount.Pnd2Code, WhtPayableAccount.Pnd3Code, WhtPayableAccount.Pnd53Code, WhtPayableAccount.Pnd54Code,
            WhtPayableAccount.EWithholdingCode,           // 2191x / 21923 ภาษีหัก ณ ที่จ่ายรอนำส่งตามแบบ
        };
        codes.AddRange(SettlementChartSeed.SeededCodes);  // 11350 เงินที่ผู้ให้บริการกันไว้ · 53170 ค่าธรรมเนียมรับชำระ · 57140 ขาดทุน chargeback
        for (var i = SettlementChannelAccounts.FirstClearingSuffix; i <= SettlementChannelAccounts.LastClearingSuffix; i++)
            codes.Add(SettlementChannelAccounts.ClearingCodeStem + i.ToString(System.Globalization.CultureInfo.InvariantCulture));   // 11341–11349
        return codes.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
    }
}
