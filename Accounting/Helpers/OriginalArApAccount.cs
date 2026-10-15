using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ฝั่งของหนี้ที่ใบต้นทางตั้งไว้ — ลูกหนี้ (ลง Dr ตอนออกใบ · กลุ่ม 113) / เจ้าหนี้ (ลง Cr ตอนออกใบ · กลุ่ม 212)</summary>
public enum ArApSide
{
    Receivable = 1,
    Payable = 2,
}

/// <summary>บรรทัดของ JE ตอนออกใบ (ข้อเท็จจริงที่ตัวเลือกต้องใช้)</summary>
public sealed record ArApJeLine(Guid AccountId, string AccountCode, decimal Debit, decimal Credit);

/// <summary>
/// **ผังลูกหนี้/เจ้าหนี้ที่ "ใบที่ออกแล้ว" ตั้งไว้จริง — ตัวตัดสินเดียวของทุกเส้นที่ปิดหนี้ของใบเดิม** (รอบ 206 · ฝ่ายค้านรอบ 2 P2(b) ส่วนที่ค้าง ·
/// PAYMENT_CHANNEL_AUDIT ADV2-P2-b)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ รับชำระ · จ่ายชำระ · ตัดหนี้สูญ · หักมัดจำ · ใบเสร็จ/ใบสำคัญจ่าย/ใบรับรองแทนใบเสร็จที่ปิดใบต้นทาง · ใบลด/เพิ่มหนี้
/// หาผังลูกหนี้/เจ้าหนี้ "ใหม่" จากผู้ติดต่อ (<c>Contact.DefaultAr/ApAccountId</c>) ทุกครั้ง ⇒ ผู้ติดต่อที่ปิดใช้/เปลี่ยนผังที่ปักไว้หลังออกใบ
/// ทำให้ (ก) ปิดหนี้ใบเดิมไม่ได้เลย (ผังที่ปักปิดใช้ ⇒ โยน) หรือ (ข) ตัดคนละผังกับที่ใบตั้งไว้ (integration ตกไป 212 ตัวแรกเงียบ ๆ ⇒ ลูกหนี้/เจ้าหนี้สองผังเพี้ยน
/// · ผังเดิมค้างยอดถาวร) · เดิมมีแค่เส้น WHT ตอนปิดยอดด้วยมัดจำ (<c>IssuedReceivableAccountAsync</c>) ที่อ่านจาก JE ของใบ</para>
///
/// <para>═══ กติกา ═══
/// <list type="number">
/// <item>อ่าน <b>JE ตอนออกใบ</b> = JE ที่ลงแล้วของใบนั้นที่ไม่ถูกกลับ/ไม่ใช่รายการกลับ <b>ตัวแรก</b> (JE รับชำระ/หักมัดจำของใบเดียวกันมาทีหลัง ·
/// ห้ามรวมยอดทุก JE: รับชำระผ่านบัญชีพัก 11340 ทำให้ "ผัง 113 ที่ยอดมากสุด" กลายเป็นบัญชีพัก)</item>
/// <item>ผู้สมัคร = บรรทัดฝั่งที่ใบตั้งหนี้ (ลูกหนี้ = เดบิต · เจ้าหนี้ = เครดิต) บนผังกลุ่ม 113/212 <b>หรือ</b>ผังที่ผู้ติดต่อปักอยู่ตอนนี้
/// (ผังของบริษัทเองที่รหัสไม่ขึ้นด้วย 113/212) · ไม่นับบัญชีพักของผู้ให้บริการรับชำระเงิน (11340 · 11341–11349)</item>
/// <item>เลือกผังที่ยอดฝั่งนั้นมากสุด (เท่ากัน = รหัสน้อยสุด) · ผังที่ใบตั้งไว้ใช้ได้<b>แม้ปิดใช้ทีหลัง</b> (ตัดยอดเดิมให้หมด ไม่ใช่ตั้งหนี้ใหม่)</item>
/// <item>ไม่มี JE / ไม่มีผู้สมัคร ⇒ <c>null</c> — ผู้เรียกใช้ตัวหาเดิม (ผังที่ผู้ติดต่อปัก/ค่าเริ่มต้น) ตามเดิม</item>
/// </list></para>
/// <para><see cref="Pick"/> เป็น pure · <see cref="LoadAsync"/> อ่าน JE ของใบ (กรอง CompanyId ทุก query)</para>
/// </summary>
public static class OriginalArApAccount
{
    /// <summary>กลุ่มรหัสลูกหนี้ตามผังมาตรฐาน</summary>
    public const string ReceivablePrefix = "113";
    /// <summary>กลุ่มรหัสเจ้าหนี้ตามผังมาตรฐาน</summary>
    public const string PayablePrefix = "212";

    /// <summary>ผังที่ใบตั้งหนี้ไว้ (pure) — <paramref name="issueLines"/> = บรรทัดของ JE ตอนออกใบเท่านั้น · null = ไม่พบ</summary>
    public static Guid? Pick(IEnumerable<ArApJeLine> issueLines, ArApSide side, Guid? pinnedAccountId)
    {
        var prefix = side == ArApSide.Receivable ? ReceivablePrefix : PayablePrefix;
        var best = issueLines
            .Where(l => !IsGatewayClearing(l.AccountCode)
                        && (l.AccountCode.StartsWith(prefix, StringComparison.Ordinal)
                            || (pinnedAccountId is Guid p && p != Guid.Empty && l.AccountId == p)))
            .GroupBy(l => (l.AccountId, l.AccountCode))
            .Select(g => (g.Key.AccountId, g.Key.AccountCode,
                Amount: side == ArApSide.Receivable ? g.Sum(x => x.Debit) : g.Sum(x => x.Credit)))
            .Where(x => x.Amount > 0.005m)
            .OrderByDescending(x => x.Amount)
            .ThenBy(x => x.AccountCode, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.AccountId == Guid.Empty ? null : best.AccountId;
    }

    private static bool IsGatewayClearing(string? code)
        => code == TradeReceivableAccount.GatewayClearingCode || SettlementChannelAccounts.IsClearingCode(code);

    /// <summary>
    /// อ่าน JE ตอนออกใบของ <paramref name="documentId"/> แล้วตัดสินด้วย <see cref="Pick"/> — ผังที่ผู้ติดต่อของใบปักอยู่ตอนนี้นับเป็นผู้สมัครด้วย ·
    /// <c>null</c> = ใบไม่มี JE / ไม่พบ (ผู้เรียกใช้ตัวหาเดิม)
    /// </summary>
    public static async Task<Guid?> LoadAsync(AccountingDbContext db, Guid companyId, Guid? documentId, ArApSide side,
        CancellationToken ct = default)
    {
        if (documentId is not Guid docId || docId == Guid.Empty) return null;
        var issueJeId = await db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.SourceDocumentId == docId && !j.IsDeleted
                        && j.Status == JournalEntryStatus.Posted && j.ReversedByEntryId == null && j.OriginalEntryId == null)
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.EntryNumber)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(ct);
        if (issueJeId is not Guid jeId) return null;
        var lines = await (from l in db.JournalEntryLines.AsNoTracking()
                           join a in db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                           where l.JournalEntryId == jeId && !l.IsDeleted && a.CompanyId == companyId
                           select new ArApJeLine(l.AccountId, a.AccountCode, l.DebitAmount, l.CreditAmount))
                          .ToListAsync(ct);
        if (lines.Count == 0) return null;
        var pins = await (from d in db.Documents.AsNoTracking()
                          join c in db.Contacts.AsNoTracking() on d.ContactId equals c.Id
                          where d.Id == docId && d.CompanyId == companyId && c.CompanyId == companyId
                          select new { c.DefaultArAccountId, c.DefaultApAccountId })
                         .FirstOrDefaultAsync(ct);
        return Pick(lines, side, side == ArApSide.Receivable ? pins?.DefaultArAccountId : pins?.DefaultApAccountId);
    }
}
