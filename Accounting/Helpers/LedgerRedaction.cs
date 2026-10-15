using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ปิดบังรายการบัญชีที่ผู้ใช้ไม่มีสิทธิ์เห็น — กติกาตัวเดียว** ของสมุดรายวัน (<c>GetJournalEntriesForUserAsync</c>) และบัญชีแยกประเภท
/// (หน้า + ไฟล์ส่งออก) · ฝ่ายค้านรอบส่งออก 2026-10-15 P2-7
///
/// <para><b>บั๊กที่มา:</b> สมุดรายวันปิดบังใบเงินเดือน (<c>SensitivityKind.Payroll</c> ฯลฯ) สำหรับผู้ไม่มีสิทธิ์ แต่บัญชีแยกประเภท (หน้า + ไฟล์)
/// แสดงทุกบรรทัดพร้อมคำอธิบาย/ยอดรายบรรทัด ⇒ เห็นเงินเดือนรายคนจากอีกหน้า</para>
///
/// <para><b>ที่เลือก (รั่วน้อยที่สุดที่ยังกระทบยอดได้):</b> บัญชีแยกประเภท — บรรทัดของใบที่ซ่อนในบัญชีเดียวกัน<b>รวมเป็นแถวเดียวต่อใบ</b>
/// (ยอดรวมของใบนั้นในบัญชีนั้น) · คำอธิบาย = "[ซ่อน] เหตุผล" · ตัดอ้างอิง/เอกสารต้นทาง/บรรทัด/สาขา/โครงการ/มิติ · เลขที่/วันที่/ชนิดใบคงไว้
/// (สมุดรายวันก็แสดง) · ยอดยกมา/ยกไป/ยอดคงเหลือไม่เปลี่ยน (ต้องเท่างบทดลอง) ⇒ เห็นได้แค่ "ยอดรวมของใบ" ไม่เห็นรายคน ·
/// งบทดลอง = ยอดรวมต่อผังอยู่แล้ว (ไม่มีรายบรรทัด) ⇒ ไม่ปิดบัง — ผังเงินเดือนรวมทั้งงวดคือข้อมูลงบการเงินที่ผู้ดูงบเห็นได้</para>
/// </summary>
public static class LedgerRedaction
{
    /// <summary>เหตุผลที่ผู้ใช้เห็นแทนเนื้อหา — ตัวตั้งเดียว (สมุดรายวัน + บัญชีแยกประเภท)</summary>
    public static string Reason(SensitivityKind kind) => kind switch
    {
        SensitivityKind.Payroll      => "ต้องมีสิทธิ์ดูข้อมูลเงินเดือน",
        SensitivityKind.ExecutivePay => "ต้องมีสิทธิ์ดูข้อมูลค่าตอบแทนผู้บริหาร",
        SensitivityKind.HrPersonal   => "ต้องมีสิทธิ์ดูข้อมูลบุคลากร",
        SensitivityKind.Confidential => "ต้องมีสิทธิ์ดูเอกสารลับ",
        _                            => "ต้องมีสิทธิ์เพิ่มเติม"
    };

    public const string HiddenPrefix = "[ซ่อน] ";

    /// <summary>ปิดบังบัญชีแยกประเภท · <paramref name="hiddenEntries"/> = ใบสำคัญที่ผู้ใช้<b>ไม่มีสิทธิ์</b>เห็น → ชนิดความลับ</summary>
    public static GeneralLedgerResponse Redact(GeneralLedgerResponse gl, IReadOnlyDictionary<Guid, SensitivityKind> hiddenEntries)
    {
        if (hiddenEntries.Count == 0) return gl;
        var accounts = new List<GeneralLedgerAccount>(gl.Accounts.Count);
        foreach (var a in gl.Accounts)
        {
            if (!a.Transactions.Any(t => hiddenEntries.ContainsKey(t.JournalEntryId)))
            {
                accounts.Add(a);
                continue;
            }
            var debitNormal = a.AccountType is AccountType.Asset or AccountType.Expense;
            var merged = new List<GeneralLedgerTransaction>();
            var seen = new HashSet<Guid>();
            foreach (var t in a.Transactions)
            {
                if (!hiddenEntries.TryGetValue(t.JournalEntryId, out var kind)) { merged.Add(t); continue; }
                if (!seen.Add(t.JournalEntryId)) continue;   // รวมไว้ที่ตำแหน่งแรกของใบแล้ว
                var same = a.Transactions.Where(x => x.JournalEntryId == t.JournalEntryId).ToList();
                merged.Add(new GeneralLedgerTransaction(t.JournalEntryId, t.EntryNumber, t.EntryDate, t.JournalType,
                    HiddenPrefix + Reason(kind), same.Sum(x => x.DebitAmount), same.Sum(x => x.CreditAmount), 0m));
            }
            // ยอดคงเหลือสะสมคำนวณใหม่ตามลำดับเดิม — แถวสุดท้าย = ยอดยกไปเดิม (ผลรวมไม่เปลี่ยน)
            var running = a.OpeningBalance;
            var rebuilt = new List<GeneralLedgerTransaction>(merged.Count);
            foreach (var t in merged)
            {
                running += debitNormal ? t.DebitAmount - t.CreditAmount : t.CreditAmount - t.DebitAmount;
                rebuilt.Add(t with { RunningBalance = running });
            }
            accounts.Add(a with { Transactions = rebuilt });
        }
        return gl with { Accounts = accounts };
    }
}
