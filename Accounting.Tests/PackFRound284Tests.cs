using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Pdf;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 284 (แพ็ก F — งานคงค้างจากผลตรวจรายสัปดาห์ 2026-11-20) · pure + อ่านซอร์สตรงจุด
/// <list type="bullet">
/// <item>T4 L-1 ตัวจับซ้ำนำเข้าพนักงานเงียบเมื่อเลขบัตรเดิมถอดรหัสไม่ได้ ⇒ <see cref="EmployeeIdDuplicateScan"/></item>
/// <item>T4 L-9 env <c>ENCRYPTION_KEY=""</c> ชนะ config ที่คอลัมน์ PII (สองคีย์ในระบบเดียว) ⇒ <c>EncryptionKeyResolver.Resolve</c> ตัวเดียว</item>
/// <item>T4 L-6 ไฟล์โอนไม่มีสรุป "พร้อมโอน X/Y" ⇒ แผ่น "สรุป" ของ Excel</item>
/// <item>T3 MED-5 (ส่วนที่ค้าง) สำเนาข้อความไทยบรรทัดใบเสร็จในฟอร์ม JS ⇒ ค่าตั้งต้นจากเซิร์ฟเวอร์ (<c>DocumentLabels</c>)</item>
/// <item>ข้อความชี้ที่ตั้ง "URL ของระบบ" ผิดหน้า ⇒ <see cref="SystemBaseUrl.AdminPagePath"/></item>
/// </list>
/// </summary>
public class PackFRound284Tests
{
    private const string IdA = "1101700207366";
    private const string IdB = "3100600445768";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Accounting.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ไม่พบรากเรพ");
    }

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    // ───────────── T4 L-1 ─────────────

    [Fact]
    public void R284_L1_เลขเดิมถอดไม่ได้_ไม่จับคู่ปลอม_และนับให้ขึ้นคำเตือน()
    {
        var lost = EncryptionHelper.Encrypt(IdA, "r284-key-nobody-has");   // ciphertext ค้าง (คีย์หาย)
        Assert.True(EncryptedColumnConverter.IsUnreadable(lost));
        var existing = new List<(Guid, string?, string?)>
        {
            (Guid.NewGuid(), lost, null),
            (Guid.NewGuid(), IdB, IdB),
        };
        var r = EmployeeIdDuplicateScan.Scan(existing, IdA);
        Assert.Null(r.MatchId);                                   // ไม่เดาว่าเป็นคนเดิม
        Assert.Equal(1, r.UnreadableCount);
        var w = EmployeeIdDuplicateScan.Warning(r.UnreadableCount);
        Assert.NotNull(w);
        Assert.Contains("ตรวจซ้ำด้วยเลขบัตรไม่ได้ 1 คน", w);
    }

    [Fact]
    public void R284_L1_ทิศตรงข้าม_เลขอ่านได้ทุกคน_จับคู่ตามเดิม_ไม่มีคำเตือน()
    {
        var hit = Guid.NewGuid();
        var existing = new List<(Guid, string?, string?)>
        {
            (Guid.NewGuid(), IdB, null),
            (hit, "1-1017-00207-36-6", null),   // รูปมีขีด = เลขเดียวกันหลัง normalize
        };
        var r = EmployeeIdDuplicateScan.Scan(existing, IdA);
        Assert.Equal(hit, r.MatchId);
        Assert.Equal(0, r.UnreadableCount);
        Assert.Null(EmployeeIdDuplicateScan.Warning(0));
        // ช่อง TaxId อ่านได้แม้ CitizenId ถอดไม่ได้ ⇒ ยังจับคู่ด้วย TaxId (นับว่าตรวจไม่ครบ 1 คน)
        var lost = EncryptionHelper.Encrypt(IdB, "r284-other");
        var r2 = EmployeeIdDuplicateScan.Scan(new List<(Guid, string?, string?)> { (hit, lost, IdA) }, IdA);
        Assert.Equal(hit, r2.MatchId);
        Assert.Equal(1, r2.UnreadableCount);
    }

    [Fact]
    public void R284_L1_จุดเรียก_ตัวจับซ้ำและศูนย์นำเข้าใช้ตัวตัดสินเดียว()
    {
        var det = Src("Accounting", "Services", "Implementations", "Import", "DuplicateDetector.cs");
        Assert.Contains("EmployeeIdDuplicateScan.Scan(ids, idCard)", det);
        Assert.DoesNotContain("EncryptedIdMatch.Same(e.CitizenId, idCard)", det);   // สำเนาเดิมที่ไม่ข้ามค่าถอดไม่ได้
        var res = Src("Accounting", "Services", "Import", "ImportEntityResolver.cs");
        Assert.Contains("UnreadableEmployeeIdCountAsync(companyId, ct)", res);
        Assert.Contains("EmployeeIdDuplicateScan.IssueCode", res);
        var ctl = Src("Accounting", "Controllers", "ImportConflictController.cs");
        Assert.Contains("UnreadableEmployeeIdCountAsync(companyId, ct)", ctl);
    }

    // ───────────── T4 L-9 ─────────────

    [Fact]
    public void R284_L9_envว่างไม่ชนะconfig_ค่ามีช่องว่างคงเดิมทุกตัวอักษร()
    {
        Assert.Equal("cfg-key", EncryptionKeyResolver.Resolve("", "cfg-key"));
        Assert.Equal("cfg-key", EncryptionKeyResolver.Resolve("   ", "cfg-key"));
        Assert.Equal("abc ", EncryptionKeyResolver.Resolve("abc ", "cfg-key"));      // ไม่ Trim (PBKDF2 ไวต่อช่องว่าง — ข้อมูลเดิมผูกค่านี้)
        Assert.Null(EncryptionKeyResolver.Resolve("", null));
        // แถวที่เข้ารหัสด้วย dev key ตอน env ว่าง ยังถอดได้หลังเปลี่ยน (dev key อยู่ในห่วงโซ่ถอด)
        Assert.Contains(EncryptionKeyResolver.DevKey, EncryptionKeyResolver.DecryptKeyChain("cfg-key", _ => null, "cfg-key", null));
        var program = Src("Accounting", "Program.cs");
        Assert.Contains("var encryptionKey = Accounting.Helpers.EncryptionKeyResolver.Resolve(", program);
        Assert.DoesNotContain("var encryptionKey = Environment.GetEnvironmentVariable(\"ENCRYPTION_KEY\")", program);
    }

    // ───────────── T4 L-6 ─────────────

    private static PayrollExportSheets.BankTransferRow Row(int seq, decimal amt, bool ready)
        => new(seq, null!, null!, new PayrollBankTransferRow(amt, ready ? "0123456789" : "", ready, ready ? "พร้อมโอน" : "ขาดเลขบัญชี", null));

    [Fact]
    public void R284_L6_สรุปไฟล์โอน_พร้อมโอนXจากY_ยอดพร้อมบวกไม่พร้อมเท่ายอดรวม()
    {
        var rows = new[] { Row(1, 29625m, true), Row(2, 15000.50m, false), Row(3, 1000m, true) };
        var s = PayrollExportSheets.BankTransferSummaryOf(rows);
        Assert.Equal(2, s.ReadyCount);
        Assert.Equal(3, s.TotalCount);
        Assert.Equal(30625m, s.ReadyAmount);
        Assert.Equal(15000.50m, s.NotReadyAmount);
        Assert.Equal(45625.50m, s.TotalAmount);
        var lines = PayrollExportSheets.BankTransferSummaryRows(s, "PR-1");
        Assert.Contains(lines, l => l.Label == "พร้อมโอน (ราย)" && (string?)l.Value == "2 จาก 3 ราย");
        Assert.Contains(lines, l => l.Label == "ยอดพร้อมโอน (บาท)" && (string?)l.Value == "30,625.00");
    }

    [Fact]
    public void R284_L6_ทิศตรงข้าม_พร้อมครบ_ไม่มีหมายเหตุไม่พร้อม_และแผ่นรายการไม่มีแถวรวม()
    {
        var s = PayrollExportSheets.BankTransferSummaryOf(new[] { Row(1, 100m, true) });
        Assert.Equal(0m, s.NotReadyAmount);
        var lines = PayrollExportSheets.BankTransferSummaryRows(s, "PR-2");
        Assert.Null(lines.Single(l => l.Label == "ไม่พร้อมโอน (ราย)").Note);
        // แผ่นรายการ (ใช้อัปโหลดเข้าธนาคาร) ห้ามมีแถวรวม — สรุปอยู่แผ่นแยก
        var ctl = Src("Accounting", "Controllers", "PayrollController.cs");
        Assert.Contains("TabularExport.Sheet(\"รายการโอน\", PayrollExportSheets.BankTransfer, rows)", ctl);
        Assert.Contains("PayrollExportSheets.BankTransferSummaryOf(rows)", ctl);
    }

    // ───────────── T3 MED-5 (สำเนา JS) ─────────────

    [Fact]
    public void R284_MED5_ฟอร์มใบเสร็จไม่มีสำเนาข้อความไทย_ใช้ค่าจากเซิร์ฟเวอร์()
    {
        var html = Src("Accounting", "wwwroot", "pages", "documents.html");
        Assert.DoesNotContain("`รับชำระเงินตาม${Layout.docTypeLabel(d.documentType)}เลขที่", html);
        Assert.DoesNotContain("unitEl.value = 'รายการ'", html);
        Assert.Contains("/receipt-line-default`", html);
        var ctl = Src("Accounting", "Controllers", "DocumentController.cs");
        Assert.Contains("labels.SettlementReceiptLine(srcLabel, src.DocumentNumber, installment: false)", ctl);
        Assert.Contains("d.CompanyId == companyId && d.Id == documentId", ctl);
        // เซิร์ฟเวอร์: ใบอังกฤษได้ข้อความอังกฤษ · ใบไทยได้ไทย (ตัวเดียวกับใบเสร็จที่ระบบออกเอง)
        Assert.DoesNotMatch("[฀-๿]", DocumentLabels.For("en").SettlementReceiptLine(DocumentType.Invoice, "INV-1", false) + DocumentLabels.For("en").SettlementUnitItem);
        Assert.Contains("รับชำระเงินตาม", DocumentLabels.For("th").SettlementReceiptLine(DocumentType.Invoice, "INV-1", false));
        // ผู้ใช้ "เห็นเฉพาะของตัวเอง" (276) เรียกได้ภายใต้ตัวกรองแถว
        Assert.Contains(DocumentRestrictionPolicy.Routes, r => r.Template.EndsWith("/receipt-line-default") && r.Method == "GET" && r.OwnScope);
    }

    // ───────────── URL ของระบบ ─────────────

    [Fact]
    public void R284_URLของระบบ_ทุกข้อความชี้หน้าที่มีช่องจริง()
    {
        var page = Src("Accounting", "wwwroot", "admin", "system-email.html");
        Assert.Equal("/admin/system-email.html", SystemBaseUrl.AdminPagePath);
        Assert.Contains("id=\"appBaseUrl\"", page);                         // ช่องอยู่หน้านี้จริง
        Assert.Contains("ไม่ใช่โดเมนของเรา", page);                            // ป้ายบอกว่าค่าเริ่มต้นไม่ใช่โดเมนเรา
        Assert.Contains(SystemBaseUrl.ShippedDefault, page);
        Assert.DoesNotContain("id=\"appBaseUrl\"", Src("Accounting", "wwwroot", "admin", "site-settings.html"));
        Assert.Equal(SystemBaseUrl.AdminPagePath, AutopilotReadiness.SystemSettingsUrl);
        // ข้อความชี้ผิดเดิมเหลือ 0 จุด
        foreach (var f in new[] { "site-settings.html", "legal.html", "sso-config.html" })
        {
            var s = Src("Accounting", "wwwroot", "admin", f);
            Assert.DoesNotContain("(แท็บทั่วไป)", s);
            Assert.DoesNotContain("ตั้งค่าระบบ → URL ของระบบ", s);
            Assert.DoesNotContain("\"URL ของระบบ\" ที่หน้าตั้งค่าเว็บไซต์", s);
        }
        Assert.DoesNotContain("ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์", PaymentLiveGate.SystemBaseUrlMissing);
        Assert.Contains("App Base URL", PaymentLiveGate.SystemBaseUrlMissing);
        Assert.Contains("system-email.html", Src("Accounting", "wwwroot", "admin", "site-settings.html"));
    }

    // ───────────── Q1 (284-7) ด่านรายงานการเงิน ─────────────

    [Fact]
    public void R284_Q1_ตัวตัดสิน_ไม่ผูกบทบาทผ่านตามเดิม_บทบาทกำหนดเองต้องมีคีย์_ไม่ใช่สมาชิกไม่ผ่าน()
    {
        Assert.True(FinancialReportAccess.Allows(keyGranted: false, member: true, hasCustomRole: false));   // (ก) เมนู "*" = เดิม
        Assert.False(FinancialReportAccess.Allows(keyGranted: false, member: true, hasCustomRole: true));   // บทบาทไม่ติ๊กคีย์ ⇒ ปิด
        Assert.True(FinancialReportAccess.Allows(keyGranted: true, member: true, hasCustomRole: true));
        Assert.False(FinancialReportAccess.Allows(keyGranted: false, member: false, hasCustomRole: false)); // fail closed
        // (ข) นักบัญชีในตัวได้คีย์ทุกตัวของด่าน
        foreach (var k in new[] { Accounting.Models.Constants.PermissionKeys.ReportsFinancial, Accounting.Models.Constants.PermissionKeys.AccountingView,
                     Accounting.Models.Constants.PermissionKeys.ReportsOperational, Accounting.Models.Constants.PermissionKeys.TaxExport })
            Assert.True(BuiltInRolePermissions.Grants(UserRole.Accountant, k));
    }

    [Fact]
    public async Task R284_Q1_เมนูรายงาน_ไม่ผูกบทบาทไม่ถูกตัด_บทบาทไม่มีคีย์ถูกตัด()
    {
        var none = await MenuPermissionMap.DeniedMenuIdsAsync(_ => Task.FromResult(false), hasCustomRole: false) ?? new List<string>();
        foreach (var m in MenuPermissionMap.LegacyKeys.Keys) Assert.DoesNotContain(m, none);
        Assert.Contains("dashboard", none);                                  // ด่านเข้มเดิมไม่ผ่อน
        var role = await MenuPermissionMap.DeniedMenuIdsAsync(_ => Task.FromResult(false), hasCustomRole: true) ?? new List<string>();
        foreach (var m in MenuPermissionMap.LegacyKeys.Keys) Assert.Contains(m, role);
        var granted = await MenuPermissionMap.DeniedMenuIdsAsync(k => Task.FromResult(k == Accounting.Models.Constants.PermissionKeys.ReportsFinancial), hasCustomRole: true) ?? new List<string>();
        Assert.DoesNotContain("reports", granted);
        Assert.DoesNotContain("general-ledger", granted);                   // any-of (AccountingView หรือ ReportsFinancial)
        Assert.Contains("aging", granted);
    }

    [Fact]
    public void R284_Q1_ทุกรายงานการเงินติดด่าน_คีย์ตรงทะเบียนเมนู()
    {
        IReadOnlyList<string>? KeysOf(Type t, string method)
        {
            var m = t.GetMethods().Single(x => x.Name == method);
            var a = m.GetCustomAttributes(typeof(Accounting.Filters.RequireReportAccessAttribute), true).Cast<Accounting.Filters.RequireReportAccessAttribute>().SingleOrDefault()
                ?? t.GetCustomAttributes(typeof(Accounting.Filters.RequireReportAccessAttribute), true).Cast<Accounting.Filters.RequireReportAccessAttribute>().SingleOrDefault();
            return a == null ? null : (string[])typeof(Accounting.Filters.RequireReportAccessAttribute)
                .GetField("_keys", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(a)!;
        }
        var acc = typeof(Accounting.Controllers.AccountingController);
        foreach (var m in new[] { "GetTrialBalance", "GetBalanceSheet", "GetProfitAndLoss", "GetProfitAndLossComparison", "GetProfitAndLossDrilldown", "GetCashFlowStatement" })
            Assert.Equal(MenuPermissionMap.LegacyKeys["reports"], KeysOf(acc, m));
        Assert.Equal(MenuPermissionMap.LegacyKeys["general-ledger"], KeysOf(acc, "GetGeneralLedger"));
        Assert.Equal(MenuPermissionMap.LegacyKeys["aging"], KeysOf(typeof(Accounting.Controllers.AgingReportController), "GetReceivableAging"));
        Assert.Equal(MenuPermissionMap.LegacyKeys["cash-forecast"], KeysOf(typeof(Accounting.Controllers.CashForecastController), "GetDetailed"));
        // month-snapshot: ไม่ 403 ทั้งหน้า — ซ่อนยอดพร้อมป้าย (หน้าหลักโหมดง่ายของพนักงานใช้ได้)
        var src = Src("Accounting", "Controllers", "AccountingController.cs");
        Assert.Contains("FinancialReportAccess.AllowsAsync(db, permissions, companyId, JwtHelper.GetUserIdFromClaims(User), FinancialReportAccess.MonthSnapshot)", src);
        Assert.Contains("hidden = true, notice = FinancialReportAccess.SnapshotHiddenNotice", src);
        Assert.Contains("pl.hidden", Src("Accounting", "wwwroot", "simple.html"));
        // ไฟล์เงินเดือนของ tax-filing-export คงด่าน Payroll.View (ไม่ใส่ด่านภาษี — ฝ่าย HR ไม่เสีย สปส.)
        Assert.Null(KeysOf(typeof(Accounting.Controllers.TaxFilingExportController), "ExportSps103"));
    }

    [Fact]
    public void R284_Q1_migrationครั้งเดียว_บทบาทที่ติ๊กเมนูได้คีย์ของหน้า()
    {
        var sql = string.Join("\n", Round284Migration.Statements());
        Assert.Contains(Round284Migration.MarkerKey, sql);
        Assert.Contains("ON CONFLICT (\"CompanyRoleId\", \"MenuItemId\") DO NOTHING", sql);   // ไม่ทับค่าที่เจ้าของตั้ง (CanAccess=false คงเดิม)
        foreach (var (menu, key) in Round284Migration.MenuGrants)
        {
            Assert.Contains($"('{menu}', '{key}')", sql);
            if (MenuPermissionMap.LegacyKeys.TryGetValue(menu, out var keys)) Assert.Contains(key, keys);   // คีย์ที่ให้เปิดหน้านั้นได้จริง
        }
    }

    // ───────────── Q2 (284-8) ค่าธรรมเนียมฝั่งผู้รับ ─────────────

    [Fact]
    public void R284_Q2_ใบเสร็จแสดงยอดที่ลูกค้าชำระเต็ม_ไม่หักค่าธรรมเนียม()
    {
        Assert.Equal(749_000m, SettlementReceiptTotals.ReceiptPaperAmount(748_970m, 30m));
        Assert.Equal(1_000m, SettlementReceiptTotals.ReceiptPaperAmount(1_000m, 0m));   // ทิศตรงข้าม: ไม่มีค่าธรรมเนียม = เดิม
        Assert.Equal(1_000m, SettlementReceiptTotals.ReceiptPaperAmount(1_000m, -5m));  // ค่าติดลบไม่ลดยอด
        Assert.Contains("ReceiptPaperAmount(payment.Amount, payment.FeeAmount)", Src("Accounting", "Services", "Implementations", "DocumentService.cs"));
    }

    // ───────────── Q3 (284-9) หักมัดจำ ─────────────

    private static readonly DateTime D0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void R284_Q3_หักมัดจำ_ยอดคงค้างก่อนรับคงที่_ก่อนและหลังมีงวดถัดไป()
    {
        var me = new SettlementReceiptTotals.ThisSettlement(D0.AddDays(5), D0.AddDays(5), 200m, 0m, 0m, 0m);
        // ตอนออกใบเสร็จ: มัดจำ 300 หักแล้ว (ไม่มีแถว) · ยอดค้างหลังรับ 500
        var atIssue = SettlementReceiptTotals.Walk(1000m, Array.Empty<SettlementReceiptTotals.LedgerEvent>(), me, 500m);
        // ภายหลัง: รับงวดถัดไป 500 ⇒ ยอดค้าง 0 (เดิม "คงค้างก่อนรับ" กระโดดเป็น 1,000)
        var later = SettlementReceiptTotals.Walk(1000m, new[] { new SettlementReceiptTotals.LedgerEvent(SettlementReceiptTotals.EventKind.Payment, D0.AddDays(9), D0.AddDays(9), 500m) }, me, 0m);
        Assert.Equal((700m, 500m, 300m), (atIssue.BalanceBefore, atIssue.BalanceAfterRaw, atIssue.PriorOther));
        Assert.Equal((700m, 500m, 300m), (later.BalanceBefore, later.BalanceAfterRaw, later.PriorOther));
        Assert.Equal(0m, later.PriorSettled);                                // ไม่ใช่ "รับชำระก่อนหน้า" และไม่ใช่ส่วนลด
    }

    [Fact]
    public void R284_Q3_ทิศตรงข้าม_ไม่มีมัดจำ_ไล่ตามเดิม_บรรทัดมัดจำเฉพาะใบที่หักจริง()
    {
        var me = new SettlementReceiptTotals.ThisSettlement(D0.AddDays(5), D0.AddDays(5), 200m, 0m, 0m, 0m);
        var w = SettlementReceiptTotals.Walk(1000m, new[] { new SettlementReceiptTotals.LedgerEvent(SettlementReceiptTotals.EventKind.Payment, D0.AddDays(9), D0.AddDays(9), 500m) }, me, 300m);
        Assert.Equal((1000m, 800m, 0m), (w.BalanceBefore, w.BalanceAfterRaw, w.PriorOther));
        var src = new SettlementReceiptTotals.Source(DocumentType.Invoice, "INV-1", D0, 934.58m, 65.42m, 0m, 1000m);
        SettlementReceiptTotals.Facts F(bool dep) => new(DocumentType.Receipt, false, false, 0m, 0m, 200m, new[] { src }, me,
            Array.Empty<SettlementReceiptTotals.LedgerEvent>(), 500m, null, dep);
        Assert.Equal(300m, SettlementReceiptTotals.Decide(F(true))!.PriorOther);
        Assert.Equal(0m, SettlementReceiptTotals.Decide(F(false))!.PriorOther);    // ไม่ติดป้ายมัดจำให้ใบที่ไม่ได้หัก
        Assert.Contains(DocumentLabels.For("th").SettlementTotalsLines(SettlementReceiptTotals.Decide(F(true))!), l => l.Contains("ตัดยอดด้วยเงินมัดจำที่รับแล้ว 300.00"));
        Assert.DoesNotMatch("[฀-๿]", string.Join(" ", DocumentLabels.For("en").SettlementTotalsLines(SettlementReceiptTotals.Decide(F(true))!)));
    }

    // ───────────── T3 LOW-2 ชื่อใบต้นทาง ─────────────

    [Fact]
    public void R284_LOW2_ชื่อใบต้นทาง_ใช้หัวที่พิมพ์จริงเมื่อภาษาตรง_ทิศตรงข้ามชื่อชนิดเดิม()
    {
        var th = DocumentLabels.For("th");
        var s = new SettlementReceiptTotals.Source(DocumentType.TaxInvoice, "TIV-1", D0, 100m, 7m, 0m, 107m, "ใบแจ้งหนี้/ใบกำกับภาษี", "th");
        Assert.Equal("ใบแจ้งหนี้/ใบกำกับภาษี", th.SourceHeadingLabel(s));
        Assert.Equal(th.SettlementSourceLabel(DocumentType.TaxInvoice), th.SourceHeadingLabel(s with { Heading = null }));
        Assert.Equal(DocumentLabels.For("en").SettlementSourceLabel(DocumentType.TaxInvoice), DocumentLabels.For("en").SourceHeadingLabel(s));   // คนละภาษา ⇒ เดิม
        Assert.Equal("รับชำระเงินตามใบแจ้งหนี้/ใบกำกับภาษีเลขที่ TIV-1", th.SettlementReceiptLine(th.SourceHeadingLabel(s), "TIV-1", false));
        Assert.Contains("ResolveDocumentHeadingAsync(db, companyId, srcId)", Src("Accounting", "Services", "Implementations", "SettlementReceiptTotalsLoader.cs"));
        Assert.Contains("labels.SourceHeadingLabel(srcHeadingSource)", Src("Accounting", "Services", "Implementations", "DocumentService.cs"));
    }

    // ───────────── T2 L2 แถว [สรุป] ตามการติ๊ก ─────────────

    private static Accounting.Models.Entities.TaxReportLine WLine(int order, string id, string? branch, decimal tax, bool excluded = false)
        => new() { LineOrder = order, TaxPayerId = id, TaxPayerName = "ผู้รับ " + id, TaxPayerBranchCode = branch, IncomeAmount = tax * 33.33m, TaxAmount = tax, IncomeTypeCode = "40(8)", IsExcluded = excluded };

    [Fact]
    public void R284_L2_แถวสรุปเพิ่มเมื่อครบสองบรรทัด_ลบเมื่อติ๊กเหลือหนึ่ง_แยกสาขา()
    {
        var r = new Accounting.Models.Entities.TaxReport { TaxType = TaxType.WithholdingTax53 };
        r.Lines.Add(WLine(1, "0105556000002", "00000", 30m));
        r.Lines.Add(WLine(2, "0105556000002", "00000", 60m, excluded: true));
        r.Lines.Add(WLine(3, "0105556000002", "00001", 15m));
        var (add0, rem0) = WhtReportTotals.SyncSummaryRows(r);
        Assert.Empty(add0); Assert.Empty(rem0);                                  // ทิศตรงข้าม: ไม่มีรายที่ครบสองบรรทัด ⇒ ไม่แตะ
        r.Lines.Single(l => l.LineOrder == 2).IsExcluded = false;                 // ติ๊กกลับ ⇒ 1 → 2
        var (add1, _) = WhtReportTotals.SyncSummaryRows(r);
        var sum = Assert.Single(add1);
        Assert.Equal(("0105556000002", "00000"), (sum.TaxPayerId, sum.TaxPayerBranchCode));   // สาขา 00001 ไม่ถูกรวม
        r.Lines.Add(sum); WhtReportTotals.Apply(r);
        Assert.Equal(90m, sum.TaxAmount);
        Assert.Equal(105m, r.TotalTaxWithheld);                                    // ยอดหัวไม่นับแถวสรุป
        r.Lines.Single(l => l.LineOrder == 1).IsExcluded = true;                  // ติ๊กออก ⇒ 2 → 1
        var (add2, rem2) = WhtReportTotals.SyncSummaryRows(r);
        Assert.Empty(add2);
        Assert.Same(sum, Assert.Single(rem2));
    }

    // ───────────── ฝ่ายค้าน 284 (HIGH-1…3 · MED-1…3 · LOW) ─────────────

    private static string[]? GateKeys(Type t, string? method = null)
    {
        var attrs = method == null
            ? t.GetCustomAttributes(typeof(Accounting.Filters.RequireReportAccessAttribute), true)
            : t.GetMethods().Single(x => x.Name == method).GetCustomAttributes(typeof(Accounting.Filters.RequireReportAccessAttribute), true);
        var a = attrs.Cast<Accounting.Filters.RequireReportAccessAttribute>().SingleOrDefault();
        return a == null ? null : (string[])typeof(Accounting.Filters.RequireReportAccessAttribute)
            .GetField("_keys", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(a)!;
    }

    [Fact]
    public void R284A_HIGH1_HIGH2_มือถือและLINEผ่านด่านเดียวกับยอดเดือน()
    {
        var mobile = Src("Accounting", "Controllers", "MobileController.cs");
        Assert.Contains("FinancialReportAccess.AllowsAsync(db, permissions, companyId, JwtHelper.GetUserIdFromClaims(User), FinancialReportAccess.MonthSnapshot)", mobile);
        Assert.Contains("GetMobileDashboardAsync(companyId, financials)", mobile);
        var line = Src("Accounting", "Services", "Implementations", "LineBotService.cs");
        var gate = line.IndexOf("FinancialReportAccess.AllowsAsync(_db, _perms, companyId, user.Id, FinancialReportAccess.MonthSnapshot)", StringComparison.Ordinal);
        Assert.True(gate > 0 && gate < line.IndexOf("ReportableJournal.WherePeriodActivity(_db.JournalEntryLines)", gate, StringComparison.Ordinal));   // ตรวจก่อนคำนวณ
        Assert.DoesNotMatch("ปุ่ม", FinancialReportAccess.NoFinancialAccessNotice);                     // ข้อความไม่อ้างปุ่มบนเว็บ
    }

    [Fact]
    public void R284A_HIGH3_รายงานตั้งเวลา_คีย์ตามชนิด_ผู้รับภายนอกต้องอนุมัติ_ทิศตรงข้ามสมาชิกส่งได้()
    {
        Assert.Equal(new[] { Accounting.Models.Constants.PermissionKeys.ReportsFinancial }, ScheduledReportAccess.KeysFor("pnl"));
        Assert.Equal(new[] { Accounting.Models.Constants.PermissionKeys.ReportsOperational }, ScheduledReportAccess.KeysFor("AR_AGING"));
        Assert.Null(ScheduledReportAccess.KeysFor("SALARY_DUMP"));                                  // ชนิดที่ไม่รู้จัก ⇒ ปฏิเสธ
        var members = new HashSet<string> { "acc@co.th" };
        Assert.Equal(new[] { "x@gmail.com" }, ScheduledReportAccess.UnapprovedRecipients(new[] { "ACC@co.th ", "x@gmail.com" }, members, null));
        Assert.Empty(ScheduledReportAccess.UnapprovedRecipients(new[] { "acc@co.th" }, members, null));    // ทิศตรงข้าม: สมาชิกส่งได้เสมอ
        Assert.Empty(ScheduledReportAccess.UnapprovedRecipients(new[] { "x@gmail.com" }, members, new[] { "X@gmail.com" }));
        // เจ้าของบันทึก ⇒ อนุมัติรายชื่อภายนอก · คนอื่นบันทึก ⇒ คงเฉพาะที่อนุมัติไว้ (เพิ่มเองไม่ได้)
        Assert.Equal(new[] { "x@gmail.com" }, ScheduledReportAccess.ApprovedAfterSave(new[] { "acc@co.th", "x@gmail.com" }, members, null, saverIsOwner: true));
        Assert.Empty(ScheduledReportAccess.ApprovedAfterSave(new[] { "x@gmail.com" }, members, null, saverIsOwner: false));
        Assert.Equal(new[] { "x@gmail.com" }, ScheduledReportAccess.ApprovedAfterSave(new[] { "x@gmail.com" }, members, new[] { "x@gmail.com", "old@y.com" }, false));
        var disp = Src("Accounting", "Services", "Implementations", "ScheduledReportDispatcher.cs");
        var g = disp.IndexOf("ScheduledReportAccess.SendProblemAsync(", StringComparison.Ordinal);
        Assert.True(g > 0 && g < disp.IndexOf("BuildReportBodyAsync(sp, db, s, nowBkk, ct)", StringComparison.Ordinal));   // ตรวจสิทธิ์ก่อนสร้างตัวเลข
        Assert.Contains("NotifyRefusedAsync(db, s, refusal, ct)", disp);
    }

    [Fact]
    public void R284A_MED1_MED3_LOW_ด่านภาษี_งบประมาณคีย์แคบ_ความเสี่ยงและลูกหนี้()
    {
        var tax = typeof(Accounting.Controllers.TaxController);
        foreach (var m in new[] { "GetTaxReports", "GetTaxReport", "ExportTaxReportXlsx", "ExportTaxReportPdf" })
            Assert.Equal(MenuPermissionMap.LegacyKeys["tax"], GateKeys(tax, m));
        Assert.Equal(MenuPermissionMap.LegacyKeys["risk"], GateKeys(typeof(Accounting.Controllers.RiskController)));
        Assert.NotNull(GateKeys(typeof(Accounting.Controllers.AdvancedArApController)));
        Assert.Equal(Accounting.Models.Constants.PermissionKeys.ReportsBudget, GateKeys(typeof(Accounting.Controllers.BudgetController), "GetBudgetVsActual")![0]);
        // migration: เมนูงบประมาณได้คีย์งบประมาณ ไม่ใช่ "งบการเงิน" ทั้งชุด
        Assert.Contains(Round284Migration.MenuGrants, g => g.MenuId == "budget" && g.Key == Accounting.Models.Constants.PermissionKeys.ReportsBudget);
        Assert.DoesNotContain(Round284Migration.MenuGrants, g => g.MenuId == "budget" && g.Key == Accounting.Models.Constants.PermissionKeys.ReportsFinancial);
        Assert.True(BuiltInRolePermissions.Grants(UserRole.Accountant, Accounting.Models.Constants.PermissionKeys.ReportsBudget));
        Assert.Contains(Accounting.Models.Constants.PermissionKeys.Catalog, c => c.Key == Accounting.Models.Constants.PermissionKeys.ReportsBudget);
        // MED-2: JE ทั้งบริษัทต้องมีคีย์ · JE ของใบเดียว (หน้าเอกสาร) คงเดิม
        var acc = Src("Accounting", "Controllers", "AccountingController.cs");
        Assert.Contains("if (sourceDocumentId == null && string.IsNullOrWhiteSpace(sourceDocumentNumber)", acc);
        Assert.Contains("FinancialReportAccess.AllowsAsync(db, permissions, companyId, userId, FinancialReportAccess.Ledger)", acc);
        Assert.Equal(MenuPermissionMap.LegacyKeys["general-ledger"], FinancialReportAccess.Ledger);
    }
}
