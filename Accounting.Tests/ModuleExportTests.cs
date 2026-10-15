using System.Text;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Payroll;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// X4 · 2026-10-14 — ไฟล์ส่งออกของหน้ารายการโมดูล (เงินเดือน/HR/ที่พัก/รอบโอน): ตัวเดินทุกหน้า <see cref="ModuleExport"/> (ตัวเขียน = TabularExport ของ X3) ·
/// ตัวตัดสินไฟล์โอนเงินเดือน <see cref="PayrollBankTransfer"/> · คอลัมน์ <see cref="PayrollExportSheets"/>
/// <para>ทุกกลุ่มมีทั้ง "เคสที่ต้องจับ" และ "เคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ" (เช่น ยอดติดลบต้องไม่โดนเติม <c>'</c> · ข้อความสูตรต้องโดน)</para>
/// </summary>
public class ModuleExportTests
{
    /// <summary>ไฟล์จริงผ่านตัวเขียนกลาง (<see cref="TabularExport"/>) — ล็อกว่า "ชนิดช่อง" ของคอลัมน์ X4 ถูก:
    /// ยอดติดลบในคอลัมน์เงินต้องออกเป็นตัวเลข ไม่ใช่ข้อความ <c>'-…</c> (คอลัมน์เงินที่ลืมใส่ Kind = Text จะถูกตัวกันสูตรเติม <c>'</c>)</summary>
    [Fact]
    public void ไฟล์บรรทัดรอบโอน_ยอดติดลบเป็นตัวเลข_ข้อความสูตรถูกกัน()
    {
        var row = new OpsExportSheets.SettlementLineRow("Shopee", "PO-1", 1, "ค่าธรรมเนียม", "=cmd|' /c calc'!A0", "ค่าคอม",
            new DateTime(2026, 10, 1), "ORD-1", "TX-1", -1500.00m, -105.00m, null, "ระบบ", "ไม่ใช่", "ไม่ต้องจับคู่", null, null);
        var f = ModuleExport.File(TabularFormat.Csv, "t", "s", OpsExportSheets.SettlementLines, new[] { row });
        var text = Encoding.UTF8.GetString(f.Content, 3, f.Content.Length - 3);
        var line = text.Split("\r\n")[1];
        Assert.Contains(",-1500.00,-105.00,", line);          // เงิน = ตัวเลขดิบ
        Assert.Contains(",'=cmd|", line);                      // ข้อความในไฟล์ต้นทางที่เป็นสูตร = ถูกกัน
        Assert.Contains(",2026-10-01,", line);                 // วันที่ ISO ค.ศ.
    }

    /// <summary>ทุกคอลัมน์ที่ค่าเป็นตัวเลขต้องมีชนิดตัวเลข (ไม่ใช่ Text) — กันคลาส "ลืมใส่ Kind" ทั้งชุดคอลัมน์ของ X4</summary>
    [Fact]
    public void คอลัมน์ที่ค่าเป็นตัวเลข_ต้องไม่ใช่ชนิดข้อความ()
    {
        static void Check<T>(string sheet, IReadOnlyList<TabularColumn<T>> cols, T sample)
        {
            foreach (var c in cols)
            {
                var v = c.Value(sample);
                if (v is decimal or int or long or double)
                    Assert.True(c.Kind != TabularCellKind.Text, $"{sheet}: คอลัมน์ \"{c.Header}\" เป็นตัวเลขแต่ชนิด Text");
                if (v is DateTime)
                    Assert.True(c.Kind is TabularCellKind.Date or TabularCellKind.Timestamp or TabularCellKind.DateThai, $"{sheet}: \"{c.Header}\" เป็นวันที่แต่ชนิด {c.Kind}");
            }
        }
        var run = PayrollExportSheetsTests.Run("Paid");
        var line = PayrollExportSheetsTests.Line("E01", 18750m, 100m);
        Check("runs", PayrollExportSheets.Runs, run with { PaidAt = DateTime.UtcNow, SsoSettledAt = DateTime.UtcNow });
        Check("register", PayrollExportSheets.Register(run, "x"), line);
        Check("leaves", PayrollExportSheets.Leaves, new LeaveResponse(Guid.NewGuid(), Guid.NewGuid(), "ก", "Annual",
            DateTime.UtcNow, DateTime.UtcNow, 1.5m, "Approved", null, CreatedAt: DateTime.UtcNow));
        var sheet = new PayrollBankTransferSheet(Guid.NewGuid(), "PR-1", "ก", 2026, 9, DateTime.UtcNow, "Paid", 1m,
            new[] { new PayrollBankTransferLine(Guid.NewGuid(), "E1", "ก", "ธ", "1234567890", "ก", 1m, 0m, false, null) });
        Check("bank", PayrollExportSheets.BankTransfer, PayrollExportSheets.BankTransferRows(sheet)[0]);
        Check("lodging", OpsExportSheets.LodgingReservations, new Accounting.Models.DTOs.Lodging.LodgingReservationListItem
            { ReservationNumber = "R1", GuestName = "ก", CheckInDate = DateTime.UtcNow, CheckOutDate = DateTime.UtcNow, Nights = 1, RoomCount = 1,
              TotalAmount = 1m, HoldExpiresAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
        Check("settlement-lines", OpsExportSheets.SettlementLines, new OpsExportSheets.SettlementLineRow("c", "p", 1, null, null, null,
            DateTime.UtcNow, null, null, 1m, 1m, 1m, null, null, null, null, null));
    }

    [Theory]
    [InlineData("csv", TabularFormat.Csv)]
    [InlineData("CSV", TabularFormat.Csv)]
    [InlineData("xlsx", TabularFormat.Xlsx)]
    [InlineData("excel", TabularFormat.Xlsx)]
    public void ParseFormat_รับค่าที่รู้จัก(string raw, TabularFormat expected)
        => Assert.Equal(expected, ModuleExport.ParseFormat(raw));

    [Theory]
    [InlineData("pdf")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseFormat_ค่าที่ไม่รู้จักหรือว่าง_คืนnull_ไม่เดา(string? raw) => Assert.Null(ModuleExport.ParseFormat(raw));

    // ═══ ดึงทุกหน้า (บั๊กคลาสสิก: export แค่หน้าปัจจุบัน) ═══

    private static Func<int, Task<PagedResponse<int>>> Pager(int total, int pageSize, List<int> calls) => page =>
    {
        calls.Add(page);
        var items = Enumerable.Range((page - 1) * pageSize, Math.Max(0, Math.Min(pageSize, total - (page - 1) * pageSize))).ToList();
        return Task.FromResult(new PagedResponse<int>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize)));
    };

    [Fact]
    public async Task CollectAllPages_ได้ครบทุกแถว_ไม่ใช่แค่หน้าแรก()
    {
        var calls = new List<int>();
        var all = await ModuleExport.CollectAllPagesAsync(Pager(450, 200, calls));
        Assert.Equal(450, all.Count);
        Assert.Equal(Enumerable.Range(0, 450), all);
        Assert.Equal(new[] { 1, 2, 3 }, calls);
    }

    [Fact]
    public async Task CollectAllPages_ข้อมูลว่าง_เรียกหน้าเดียวคืนว่าง()
    {
        var calls = new List<int>();
        Assert.Empty(await ModuleExport.CollectAllPagesAsync(Pager(0, 200, calls)));
        Assert.Single(calls);
    }

    [Fact]
    public async Task CollectAllPages_เกินเพดาน_ล้มดังพร้อมจำนวนจริง_ไม่ตัดเงียบ()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            ModuleExport.CollectAllPagesAsync(Pager(1_001, 200, new List<int>()), maxRows: 1_000));
        Assert.Contains("1,001", ex.Message);
        Assert.Equal("EXPORT-ROW-CAP", ex.RuleCode);
    }

    [Fact]
    public async Task CollectAllSkipTake_เดินจนได้น้อยกว่าที่ขอ()
    {
        var data = Enumerable.Range(0, ModuleExport.PageSize * 2 + 7).ToList();
        var skips = new List<int>();
        var all = await ModuleExport.CollectAllSkipTakeAsync<int>((skip, take) =>
        {
            skips.Add(skip);
            return Task.FromResult<IReadOnlyList<int>>(data.Skip(skip).Take(take).ToList());
        });
        Assert.Equal(data, all);
        Assert.Equal(new[] { 0, ModuleExport.PageSize, ModuleExport.PageSize * 2 }, skips);
    }
}

/// <summary>ไฟล์รายการโอนเงินเดือน — ยอดโอน = สูตรเดียวกับเส้น Cr ธนาคารตอนกดจ่าย · แถวไม่พร้อมยังอยู่ในไฟล์พร้อมเหตุผล</summary>
public class PayrollBankTransferTests
{
    [Fact]
    public void รอบจ่ายแล้ว_ยอดโอน_เท่ากับสุทธิลบหักคืนเงินทดรอง()
    {
        var r = PayrollBankTransfer.Evaluate("Paid", 15000m, 2000m, hasOutstandingAdvance: false, "123-4-56789-0");
        Assert.Equal(13000m, r.TransferAmount);
        Assert.Equal("1234567890", r.AccountDigits);
        Assert.True(r.Ready);
        Assert.Null(r.Note);
    }

    [Fact]
    public void รอบยังไม่จ่าย_ใช้สุทธิ_และบอกเมื่อมีเงินทดรองค้าง_ไม่เดายอดหักคืน()
    {
        var withAdvance = PayrollBankTransfer.Evaluate("Approved", 15000m, 0m, hasOutstandingAdvance: true, "1234567890");
        Assert.Equal(15000m, withAdvance.TransferAmount);
        Assert.True(withAdvance.Ready);
        Assert.Contains("เงินทดรองค้าง", withAdvance.Note);
        // ทิศตรงข้าม: ไม่มีเงินทดรองค้าง = ไม่มีหมายเหตุรบกวน
        var clean = PayrollBankTransfer.Evaluate("Approved", 15000m, 0m, hasOutstandingAdvance: false, "1234567890");
        Assert.Null(clean.Note);
    }

    [Fact]
    public void ขาดเลขบัญชี_หรือเลขสั้น_ไม่พร้อมโอน_แต่ยังอยู่ในไฟล์พร้อมทางไปต่อ()
    {
        var none = PayrollBankTransfer.Evaluate("Paid", 9000m, 0m, false, null);
        Assert.False(none.Ready);
        Assert.Equal("ขาดเลขบัญชี", none.Status);
        Assert.Equal(9000m, none.TransferAmount);
        var shortNo = PayrollBankTransfer.Evaluate("Paid", 9000m, 0m, false, "12345");
        Assert.False(shortNo.Ready);
        Assert.Contains("5 หลัก", shortNo.Note);
        // ทิศตรงข้าม: 12 หลัก (ออมสิน/ธ.ก.ส.) ผ่าน
        Assert.True(PayrollBankTransfer.Evaluate("Paid", 9000m, 0m, false, "020123456789").Ready);
    }

    [Fact]
    public void ยอดหลังหักคืนเป็นศูนย์_ไม่ต้องโอน()
    {
        var r = PayrollBankTransfer.Evaluate("Paid", 5000m, 5000m, false, "1234567890");
        Assert.False(r.Ready);
        Assert.Equal("ไม่ต้องโอน", r.Status);
        Assert.Equal(0m, r.TransferAmount);
    }

    [Theory]
    [InlineData("Draft", true)]
    [InlineData("Voided", true)]
    [InlineData("Calculated", false)]
    [InlineData("Approved", false)]
    [InlineData("Paid", false)]
    public void สถานะรอบที่ส่งออกรายการโอนได้(string status, bool refused)
        => Assert.Equal(refused, PayrollBankTransfer.RefuseReason(status) != null);

    [Fact]
    public void CashPaid_ปัดแบบAwayFromZero()
        => Assert.Equal(100.03m, PayrollBankTransfer.CashPaid("Paid", 100.025m, 0m));   // banker's rounding (ค่าเริ่มต้น) จะได้ 100.02
}

/// <summary>คอลัมน์ไฟล์ของโมดูลเงินเดือน — ตัวเลขเท่ากับ DTO บนจอ · PII/เงินเดือนตามสิทธิ์</summary>
public class PayrollExportSheetsTests
{
    internal static PayrollRunLineDto Line(string code, decimal net, decimal adv = 0m) => new(
        Guid.NewGuid(), "พนักงาน " + code, code, 20000m, 0m, 0m, 0m, 0m, 0m, 20000m, 15000m, 750m, 750m, 500m, 0m, 0m, 0m,
        20000m - net, net, AdvanceRecovered: adv, ProvidentFundEmployer: 600m);

    internal static PayrollRunResponse Run(string status) => new(
        Guid.NewGuid(), "PR-202609-001", "กันยายน", 2026, 9, new DateTime(2026, 9, 30), status,
        40000m, 2500m, 37500m, 1000m, 1500m, 1500m, 2, DateTime.UtcNow);

    [Fact]
    public void ทะเบียน_แถวรวมยาวเท่าหัวคอลัมน์_และรวมจากแถวที่ออกไฟล์()
    {
        var run = Run("Paid");
        var lines = new[] { Line("E01", 18750m, 1000m), Line("E02", 18750m) };
        var cols = PayrollExportSheets.Register(run, "11121 ธนาคาร");
        var totals = PayrollExportSheets.RegisterTotals(lines);
        object? V(PayrollRunLineDto l, string h) => cols.First(c => c.Header == h).Value(l);
        Assert.Equal("รวม", V(totals, "รหัสพนักงาน"));
        Assert.Equal(37500m, V(totals, "เงินได้สุทธิ"));
        Assert.Equal(36500m, V(totals, "เงินโอนจริง"));        // 37,500 − หักคืนเงินทดรอง 1,000
        Assert.Equal(1200m, V(totals, "กองทุนสำรองฯ นายจ้าง"));
        Assert.Null(V(totals, "แหล่งจ่าย"));                   // แถวรวมไม่มีแหล่งจ่าย
        Assert.Equal(17750m, V(lines[0], "เงินโอนจริง"));       // รายคน = สุทธิ − หักคืน
        Assert.Equal("11121 ธนาคาร", V(lines[0], "แหล่งจ่าย"));
    }

    [Fact]
    public void พนักงาน_ไม่มีสิทธิ์ดูเงินเดือน_ช่องเงินเดือนว่าง_ไม่ใช่ศูนย์()
    {
        var e = new EmployeeResponse(Guid.NewGuid(), "E01", "นาย", "สมชาย", "ใจดี", null, null, "1-XXXX-XXXXX-XX-3",
            null, null, null, new DateTime(2024, 1, 1), null, 0m, "Monthly", true, DateTime.UtcNow);
        var hidden = PayrollExportSheets.Employees(canViewSalary: false);
        var salaryIdx = hidden.ToList().FindIndex(c => c.Header == "ฐานเงินเดือน");
        Assert.Null(hidden[salaryIdx].Value(e));
        // ทิศตรงข้าม: มีสิทธิ์ = ออกตัวเลขตาม DTO
        var shown = PayrollExportSheets.Employees(canViewSalary: true);
        Assert.Equal(0m, shown[salaryIdx].Value(e));
        // เลขบัตรออกตามที่ service ปิดบังมาแล้ว (ไม่เปิดเพิ่มเอง)
        Assert.Equal("1-XXXX-XXXXX-XX-3", shown.First(c => c.Header == "เลขบัตรประชาชน").Value(e));
    }

    [Fact]
    public void ไฟล์โอน_เลขบัญชีตัวเลขล้วน_อ้างอิงไม่ซ้ำ_แถวไม่พร้อมยังอยู่()
    {
        var sheet = new PayrollBankTransferSheet(Guid.NewGuid(), "PR-1", "ก.ย.", 2026, 9, new DateTime(2026, 9, 30), "Paid", 30000m,
            new[]
            {
                new PayrollBankTransferLine(Guid.NewGuid(), "E01", "นายเอ", "กสิกรไทย", "012-3-45678-9", "เอ", 20000m, 0m, false, "11121 ธนาคาร"),
                new PayrollBankTransferLine(Guid.NewGuid(), "E02", "นายบี", null, null, null, 10000m, 0m, false, null),
            });
        var rows = PayrollExportSheets.BankTransferRows(sheet);
        var cols = PayrollExportSheets.BankTransfer;
        string V(int row, string header) => Convert.ToString(cols.First(c => c.Header == header).Value(rows[row]))!;
        Assert.Equal("0123456789", V(0, "เลขที่บัญชี"));
        Assert.Equal("PR-1-E01", V(0, "อ้างอิง"));
        Assert.Equal("พร้อมโอน", V(0, "สถานะ"));
        Assert.Equal("ขาดเลขบัญชี", V(1, "สถานะ"));
        Assert.Equal("นายบี", V(1, "ชื่อบัญชี"));   // ไม่มีชื่อบัญชี = ชื่อพนักงาน (ไม่ปล่อยว่าง)
        Assert.Equal(2, rows.Count);
    }
}
