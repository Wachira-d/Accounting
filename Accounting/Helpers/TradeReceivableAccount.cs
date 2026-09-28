using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ผังลูกหนี้การค้า — ตัวตั้งตัวเดียวของ "ลงลูกหนี้ที่ไหน" และ "ผังไหนนับเป็นลูกหนี้การค้า"** (รอบ 198 I-1/S-1/G-1)
///
/// <para>═══ ที่มา (บั๊กจริง · ทีม S2 รอบ 198) ═══</para>
/// <list type="bullet">
/// <item>Integration หาลูกหนี้ด้วย <c>AccountCode.StartsWith("113")</c> <b>ไม่เรียง ไม่กรองระดับ</b> ⇒ ได้หัวกลุ่ม 113 /
///   11310 / 11320 / 11330 / 11340 แล้วแต่ลำดับแถวในฐาน — ใบแจ้งหนี้กับการรับชำระของใบเดียวกันลงคนละผังได้</item>
/// <item>รายงานกระทบบัญชีย่อยนับทุกผัง 112/113 ที่ชื่อมี "ลูกหนี้" เป็นลูกหนี้การค้า ⇒ <b>11340 ลูกหนี้ผู้ให้บริการ
///   รับชำระเงิน</b> (บัญชีพัก gateway ที่ไม่มีเอกสารลูกหนี้รองรับ) ถูกเทียบกับยอดค้างของใบแจ้งหนี้ ⇒ ส่วนต่างปลอมทุกเดือน</item>
/// </list>
///
/// <para>═══ กติกา ═══ ผังที่ผู้ใช้ปักไว้บนผู้ติดต่อ (<c>Contact.DefaultArAccountId</c>) ชนะ · ไม่มี ⇒ <b>11310 ลูกหนี้การค้า</b>
/// รหัสเต็ม · หาไม่เจอ = ผู้เรียก<b>ล้มดัง</b> (ห้ามตกไปผังอื่นในหมวด 113 ที่ความหมายต่าง) · บัญชีพักของผู้ให้บริการรับชำระเงิน
/// (11340 และผังที่ตั้งเป็นบัญชีพักใน <c>PaymentProviderConfig.ClearingAccountId</c>) <b>ไม่ใช่</b>ลูกหนี้การค้า</para>
///
/// <para><see cref="IsTradeReceivableControl"/> เป็น pure · <see cref="ResolveAsync"/> อ่านผังของบริษัท (tenant เสมอ)</para>
/// </summary>
public static class TradeReceivableAccount
{
    /// <summary>11310 ลูกหนี้การค้า</summary>
    public const string StandardCode = "11310";

    /// <summary>ผังบัญชีพักของผู้ให้บริการรับชำระเงินตามผังมาตรฐาน — ไม่ใช่ลูกหนี้การค้า</summary>
    public const string GatewayClearingCode = "11340";

    /// <summary>ผังนี้นับเป็น "บัญชีคุมลูกหนี้การค้า" ในรายงานกระทบบัญชีย่อยไหม
    /// (<paramref name="gatewayClearingAccountIds"/> = ผังที่บริษัทตั้งเป็นบัญชีพักของผู้ให้บริการ)</summary>
    public static bool IsTradeReceivableControl(Guid accountId, string accountCode, string accountName,
        IReadOnlyCollection<Guid> gatewayClearingAccountIds)
    {
        if (gatewayClearingAccountIds.Contains(accountId)) return false;
        if (accountCode == GatewayClearingCode) return false;
        return (accountCode.StartsWith("112") || accountCode.StartsWith("113"))
               && accountName.Contains("ลูกหนี้");
    }

    /// <summary>ผังลูกหนี้ที่ต้องลง/ตัด — ผังที่ปักไว้บนผู้ติดต่อ (active · บริษัทเดียวกัน) ชนะ · ไม่มี ⇒ 11310 รหัสเต็ม ·
    /// <c>null</c> = หาไม่เจอ ⇒ ผู้เรียกต้องล้มดัง (ห้ามตกไป prefix 113 ที่ความหมายต่าง)</summary>
    public static async Task<ChartOfAccount?> ResolveAsync(AccountingDbContext db, Guid companyId,
        Guid? contactPinnedArAccountId, CancellationToken ct = default)
    {
        if (contactPinnedArAccountId is Guid pinnedId)
        {
            var pinned = await db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.Id == pinnedId && a.CompanyId == companyId && a.IsActive && !a.IsDeleted, ct);
            if (pinned != null) return pinned;
        }
        return await db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode == StandardCode && a.IsActive && !a.IsDeleted, ct);
    }
}
