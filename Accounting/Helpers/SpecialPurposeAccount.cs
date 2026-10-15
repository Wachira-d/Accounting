using System.Linq.Expressions;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **ผังเฉพาะกิจ — ผังที่ระบบสร้าง/ใช้เพื่อจุดประสงค์เดียว ห้ามเป็น "ผังตั้งต้น" จากการหาตามหมวด/ตามรหัสต้น** (ฝ่ายค้านรอบส่งออก 2026-10-15 รอบ 2 P1-3)
///
/// <para><b>บั๊กที่มา:</b> ลำดับผังตั้งต้นรอบก่อน "ผังระบบก่อน" ⇒ ผังที่ migration ใส่ให้<b>ทุกบริษัท</b>เป็นผังระบบ (54960 ผลต่างจากการปัดเศษ — ไม่มีบัญชีแม่)
/// และผังที่ระบบสร้างเอง (21240 รับสินค้ายังไม่วางบิล · 1134x ผังพัก wallet · 11350 เงินที่ผู้ให้บริการกันไว้ · ผังย่อยของบัญชีธนาคาร) ชนะผังของผู้ใช้
/// ในบริษัทที่ใช้ผังของตัวเอง/ผังนำเข้า ⇒ ค่าใช้จ่ายตั้งต้นตกไป 54960 · ลูกหนี้/เจ้าหนี้ตามรหัสต้นได้ 11341/21240</para>
///
/// <para><b>กติกา (รอบ 3 P1 — แทนการตัดด้วยรหัส/ตัวชี้ใน config ของรอบ 2):</b> ผังเฉพาะกิจ = ผังที่<b>ระบบสร้างเอง</b> (<c>IsSystemAccount</c>) และ
/// (ก) รหัสอยู่ในรายการนี้ (รหัสจากเจ้าของเดิมของแต่ละผัง — ไม่ใช่รายการที่สอง) หรือ (ข) รหัสรูปผังย่อยบัญชีธนาคารที่ <c>BankService</c> สร้าง
/// (<see cref="BankSubAccountCode"/> — "กลุ่ม-ลำดับ") · ผังของลูกค้าที่บังเอิญใช้รหัสเดียวกันแต่ไม่ใช่ผังระบบ = ผังทั่วไป ·
/// ผังที่ config ชี้ (ผังพัก gateway · ผังกันเงิน · ผังที่ผูกธนาคาร) แต่ผู้ใช้/แม่แบบสร้าง = ผังทั่วไป (ผู้ใช้เลือกผังใดก็ได้ — ตัวชี้ไม่ใช่หลักฐานว่าเป็นผังเฉพาะกิจ) ·
/// ผังเฉพาะกิจถูก<b>เลื่อนไปท้ายลำดับ</b>ของการหา "ตามหมวด" และ "ตามรหัสต้น" (<c>PostingAccountGuard.OrderByGeneralPurposeFirst</c>) ไม่ใช่ตัดทิ้ง ⇒
/// หาเจอเสมอถ้ามีผู้สมัคร · การหาด้วย<b>รหัสเต็ม</b> (เช่น <c>FindAccountAsync("21240")</c>) ได้ผังนั้นตามเดิม</para>
/// </summary>
public static class SpecialPurposeAccount
{
    /// <summary>21240 เจ้าหนี้-รับสินค้ายังไม่วางบิล (GR/NI · 3-way match) — สร้างโดย <c>DocumentService.EnsureGrNiAccountAsync</c></summary>
    public const string GoodsReceivedNotInvoicedCode = "21240";

    /// <summary>ตัวคั่นในรหัสผังย่อยของบัญชีธนาคารที่ระบบสร้าง ("11122-001") — รหัสแม่แบบ/รหัสที่ผู้ใช้นำเข้าไม่มีตัวนี้</summary>
    public const string BankSubAccountSeparator = "-";

    /// <summary>รหัสผังย่อยของบัญชีธนาคารที่ระบบสร้าง (<c>BankService</c> — สร้างใหม่และย้ายกลุ่ม) — ตัวตั้งเดียวของรูปรหัส</summary>
    public static string BankSubAccountCode(string parentCode, int sequence)
        => parentCode + BankSubAccountSeparator + sequence.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>
    /// (ประกาศหลัง <see cref="Codes"/> — ตัวเริ่มค่า static รันตามลำดับในไฟล์)
    /// ผังนี้คือผังเฉพาะกิจที่<b>ระบบสร้าง</b> (ใช้ใน query — EF แปลเป็น SQL ได้): <c>IsSystemAccount</c> และ (รหัสอยู่ใน <see cref="Codes"/> หรือรหัสรูปผังย่อยธนาคาร)
    /// </summary>
    public static readonly Expression<Func<ChartOfAccount, bool>> IsSystemCreated = BuildIsSystemCreated();

    private static Expression<Func<ChartOfAccount, bool>> BuildIsSystemCreated()
    {
        var codes = Codes.ToList();
        return a => a.IsSystemAccount && (codes.Contains(a.AccountCode) || a.AccountCode.Contains(BankSubAccountSeparator));
    }
}
