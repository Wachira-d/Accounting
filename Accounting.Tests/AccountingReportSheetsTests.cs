using System.Text;
using Accounting.Helpers;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบส่งออก 2026-10-14 (ทีม X3) — คอลัมน์ไฟล์ส่งออกรายงานบัญชี <see cref="AccountingReportSheets"/> ·
/// งบทดลองแบบช่วงเวลาประกอบจากสองเจ้าของ (บัญชีแยกประเภท + งบทดลอง) ต้อง "ฟ้อง" เมื่อไม่ต่อกัน และ "เงียบ" เมื่อต่อกัน
/// </summary>
public class AccountingReportSheetsTests
{
    private static readonly DateTime From = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

    private static GeneralLedgerAccount Acc(string code, AccountType t, decimal open, decimal dr, decimal cr)
    {
        var debitNormal = t is AccountType.Asset or AccountType.Expense;
        var close = open + (debitNormal ? dr - cr : cr - dr);
        return new GeneralLedgerAccount(Guid.NewGuid(), code, "บัญชี " + code, t, (int)t, open, dr, cr, close, new List<GeneralLedgerTransaction>());
    }

    [Fact]
    public void งบทดลองแบบช่วง_ต่อเนื่อง_ไม่มีผลต่าง_และรวมสมดุลทุกคู่()
    {
        var gl = new GeneralLedgerResponse(new List<GeneralLedgerAccount>
        {
            Acc("11111", AccountType.Asset, 1_000m, 500m, 200m),     // ปิด 1,300 Dr
            Acc("41000", AccountType.Revenue, 1_000m, 0m, 300m),     // ปิด 1,300 Cr
        }, From, To);
        var tb = new TrialBalanceResponse(To, new List<TrialBalanceItem>
        {
            new("11111", "เงินสด", AccountType.Asset, 0, 1_500m, 200m),
            new("41000", "รายได้", AccountType.Revenue, 3, 0m, 1_300m),
        }, 1_500m, 1_500m);
        var ws = AccountingReportSheets.BuildTrialBalanceWorksheet(gl, tb, From);
        Assert.All(ws.Rows, r => Assert.Equal(0m, r.ContinuityGap));
        Assert.Equal(ws.OpeningDebit, ws.OpeningCredit);
        Assert.Equal(ws.MovementDebit, ws.MovementCredit);
        Assert.Equal(ws.ClosingDebit, ws.ClosingCredit);
        Assert.Equal(1_300m, ws.Rows.Single(r => r.AccountCode == "11111").ClosingDebit);
        Assert.Equal(1_500m, ws.Rows.Single(r => r.AccountCode == "11111").PageTotalDebit);   // คอลัมน์ "ตามหน้า" = ค่าที่หน้าแสดงตรงตัว
    }

    [Fact]
    public void ทิศตรงข้าม_ยกไปของงบทดลองไม่ตรงบัญชีแยกประเภท_ต้องมีผลต่างในแถวนั้น()
    {
        var gl = new GeneralLedgerResponse(new List<GeneralLedgerAccount> { Acc("11111", AccountType.Asset, 0m, 500m, 0m) }, From, To);
        // งบทดลองมียอดนำเข้า 100 ที่ GL ไม่เห็น (เช่น กรองสาขาแล้วงบทดลองบวกยอดนำเข้าทั้งบริษัท)
        var tb = new TrialBalanceResponse(To, new List<TrialBalanceItem> { new("11111", "เงินสด", AccountType.Asset, 0, 600m, 0m) }, 600m, 0m);
        var ws = AccountingReportSheets.BuildTrialBalanceWorksheet(gl, tb, From);
        Assert.Equal(-100m, ws.Rows.Single().ContinuityGap);

        var csv = Encoding.UTF8.GetString(TabularExport.ToCsv(AccountingReportSheets.TrialBalance(ws, new[] { "งบทดลอง" })));
        Assert.Contains(",-100.00", csv);   // ผลต่างติดลบเป็นตัวเลข ไม่ถูกเติม '
    }

    [Fact]
    public void งบทดลองแบบกรอง_แถวกำไรขาดทุนก่อนรอบ_ยกมาเท่ายกไป_ไม่มีผลต่างเท็จ_แต่ผลต่างจริงยังฟ้อง()
    {
        // รอบ 3 P2-1: งบทดลองกรองสาขาหลังปิดปีแรกมีแถวทุน "กำไร(ขาดทุน)ก่อนรอบบัญชีนี้" (ไม่มีรหัส · บัญชีแยกประเภทไม่มีแถวคู่)
        // เดิมยกมา 0 ⇒ ผลต่างความต่อเนื่อง = กำไรรอบก่อนทั้งก้อน ⇒ ไฟล์กรองทุกไฟล์ขึ้นคำเตือนเท็จ
        var gl = new GeneralLedgerResponse(new List<GeneralLedgerAccount>
        {
            Acc("11111", AccountType.Asset, 7_000m, 3_000m, 1_000m),    // ปิด 9,000 Dr
            Acc("41000", AccountType.Revenue, 0m, 0m, 3_000m),          // ปิด 3,000 Cr
            Acc("51000", AccountType.Expense, 0m, 1_000m, 0m),          // ปิด 1,000 Dr
        }, From, To);
        var tb = new TrialBalanceResponse(To, new List<TrialBalanceItem>
        {
            new("11111", "เงินสด", AccountType.Asset, 0, 9_000m, 0m),
            new("41000", "รายได้", AccountType.Revenue, 3, 0m, 3_000m),
            new("51000", "ค่าใช้จ่าย", AccountType.Expense, 4, 1_000m, 0m),
            new("", LedgerFilterRule.PriorPnlRowName, AccountType.Equity, 2, 0m, 7_000m),
        }, 10_000m, 10_000m);
        var ws = AccountingReportSheets.BuildTrialBalanceWorksheet(gl, tb, From);
        Assert.All(ws.Rows, r => Assert.Equal(0m, r.ContinuityGap));
        var prior = ws.Rows.Single(r => r.AccountName == LedgerFilterRule.PriorPnlRowName);
        Assert.Equal(7_000m, prior.OpeningCredit);
        Assert.Equal(7_000m, prior.ClosingCredit);
        Assert.Equal(0m, prior.MovementDebit + prior.MovementCredit);
        Assert.Equal(ws.OpeningDebit, ws.OpeningCredit);
        Assert.Equal(ws.ClosingDebit, ws.ClosingCredit);

        // ทิศตรงข้าม: บัญชีอื่นที่ยกไปไม่ตรงบัญชีแยกประเภทจริง ⇒ ยังมีผลต่าง (แถวกำไรขาดทุนก่อนรอบไม่กลบ)
        var tbGap = tb with { Items = tb.Items.Select(i => i.AccountCode == "11111" ? i with { DebitBalance = 9_500m } : i).ToList() };
        var wsGap = AccountingReportSheets.BuildTrialBalanceWorksheet(gl, tbGap, From);
        Assert.Equal(-500m, wsGap.Rows.Single(r => r.AccountCode == "11111").ContinuityGap);
        Assert.Equal(0m, wsGap.Rows.Single(r => r.AccountName == LedgerFilterRule.PriorPnlRowName).ContinuityGap);
        // แถวทุนที่มีรหัสผังชื่อเดียวกัน (ไม่ใช่แถวสังเคราะห์) ไม่ถูกตีความเป็นยอดยกมา
        Assert.False(LedgerFilterRule.IsPriorPnlRow("32000", LedgerFilterRule.PriorPnlRowName));
        Assert.True(LedgerFilterRule.IsPriorPnlRow("", LedgerFilterRule.PriorPnlRowName));
    }

    [Fact]
    public void บัญชีที่เป็นศูนย์ทุกช่องไม่มีแถว_แต่บัญชีที่มีแต่ยอดยกมายังอยู่()
    {
        var gl = new GeneralLedgerResponse(new List<GeneralLedgerAccount>
        {
            Acc("11500", AccountType.Asset, 5_000m, 0m, 0m),
            Acc("11600", AccountType.Asset, 0m, 0m, 0m),
        }, From, To);
        var tb = new TrialBalanceResponse(To, new List<TrialBalanceItem> { new("11500", "มัดจำ", AccountType.Asset, 0, 5_000m, 0m) }, 5_000m, 0m);
        var ws = AccountingReportSheets.BuildTrialBalanceWorksheet(gl, tb, From);
        Assert.Equal(new[] { "11500" }, ws.Rows.Select(r => r.AccountCode).ToArray());
    }

    [Fact]
    public void สมุดรายวัน_ใบที่ถูกปิดบัง_ยังมีแถวบอกเหตุ_ไม่หายเงียบ_และไม่รั่วตัวเลข()
    {
        var hidden = new JournalEntryResponse(Guid.NewGuid(), "JV-PAY-1", To, JournalType.General, null, null, JournalEntryStatus.Posted,
            true, 0m, 0m, new List<JournalLineResponse>(), To, IsRedacted: true, RedactedReason: "ต้องมีสิทธิ์ดูข้อมูลเงินเดือน",
            Sensitivity: SensitivityKind.Payroll);
        var normal = new JournalEntryResponse(Guid.NewGuid(), "JV-1", To, JournalType.Sales, "ขาย", "REF", JournalEntryStatus.Posted,
            false, 100m, 100m, new List<JournalLineResponse>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), "11300", "ลูกหนี้", 100m, 0m, null, 1),
                new(Guid.NewGuid(), Guid.NewGuid(), "41000", "รายได้", 0m, 100m, null, 2),
            }, To);
        var sheet = AccountingReportSheets.Journals(new[] { hidden, normal }, LedgerExportNames.Empty, new[] { "สมุดรายวัน" });
        Assert.Equal(3, sheet.Rows.Count);
        Assert.Contains(sheet.Rows, r => (string?)r[0] == "JV-PAY-1" && ((string?)r[4] ?? "").Contains("ปิดบัง") && r[13] == null);
        Assert.Equal(100m, sheet.Footer.Single()[13]);
        Assert.Equal(100m, sheet.Footer.Single()[14]);
    }
}
