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
        Assert.Contains("labels.SettlementReceiptLine(src.DocumentType, src.DocumentNumber, installment: false)", ctl);
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
}
