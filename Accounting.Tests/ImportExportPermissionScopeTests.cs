using Accounting.Helpers;
using Accounting.Models.Constants;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **เส้นนำเข้า/ส่งออกไฟล์ต้องมีด่านสิทธิ์จริง ไม่ใช่แค่ `[Authorize]`**
/// (ช่องโหว่จริง · ผลตรวจทีมทางเข้าภายนอก รอบ 184)
///
/// <para>สองครึ่ง: ครึ่งแรก = ชนิดข้อมูลที่อันตรายต้องถูกผูกกับคีย์ที่เข้มพอ ·
/// ครึ่งหลัง = <b>คนที่ทำงานนี้อยู่ทุกวันนี้ต้องไม่ถูกกัน</b> (คีย์ที่เลือกต้อง
/// อยู่ในชุดที่ <c>UserRole.Accountant</c> ได้อัตโนมัติอยู่แล้ว) — การเข้มขึ้น
/// ที่ล็อกทุกคนออกคือการปิดฟีเจอร์ ไม่ใช่การตั้งด่าน (CLAUDE.md F2 ข้อ 8)</para>
/// </summary>
public class ImportExportPermissionScopeTests
{
    /// <summary>ชุดคีย์ที่ <c>UserRole.Accountant</c> ผ่านอัตโนมัติ
    /// (สำเนาจาก <c>PermissionService.AccountantDefaultKeys</c> — เทสต์นี้คือสิ่งที่
    /// ฟ้องเมื่อสองชุดเริ่มไม่ตรงกัน)</summary>
    private static readonly string[] AccountantAutoKeys =
    {
        PermissionKeys.DocumentCreate, PermissionKeys.DocumentExport,
        PermissionKeys.JournalManage, PermissionKeys.ChartOfAccountsEdit,
        PermissionKeys.ContactEdit, PermissionKeys.ProductEdit,
        PermissionKeys.BankView, PermissionKeys.BankReconcile,
        PermissionKeys.InventoryView,
    };

    // ═══ ครึ่งที่ 1 — ของอันตรายต้องถูกผูกกับคีย์ที่ตรงกับความเสี่ยง ═══

    [Fact]
    public void ส่งออกทะเบียนพนักงานต้องใช้สิทธิ์ดูข้อมูลส่วนบุคคล()
    {
        // ไฟล์นี้มี CitizenId 13 หลัก + เลขบัญชีธนาคาร + เงินเดือน (PDPA ม.26)
        Assert.Equal(PermissionKeys.PiiView, ImportExportPermissionScope.ForExport("employees"));
        var msg = ImportExportPermissionScope.DeniedMessage("employees", PermissionKeys.PiiView, isExport: true);
        Assert.Contains("เลขบัตรประชาชน", msg);
        Assert.Contains(PermissionKeys.PiiView, msg);   // บอกว่าต้อง grant คีย์ไหน
    }

    [Fact]
    public void นำเข้าทะเบียนพนักงานต้องใช้สิทธิ์_HR()
        => Assert.Equal(PermissionKeys.HrAdmin, ImportExportPermissionScope.ForImport("employees"));

    [Theory]
    [InlineData("journalentries", "Journal.Manage")]
    [InlineData("journal-entries", "Journal.Manage")]
    [InlineData("opening-ar", "Journal.Manage")]
    [InlineData("opening-ap", "Journal.Manage")]
    [InlineData("chartofaccounts", "ChartOfAccounts.Edit")]
    [InlineData("chart-of-accounts", "ChartOfAccounts.Edit")]
    [InlineData("contacts", "Contact.Edit")]
    [InlineData("products", "Product.Edit")]
    [InlineData("payments", "Document.Create")]
    [InlineData("documents", "Document.Create")]
    [InlineData("banktransactions", "Bank.Reconcile")]
    [InlineData("stock-opening", "Inventory.Adjust")]
    [InlineData("stock-adjustment", "Inventory.Adjust")]
    [InlineData("fixed-assets", "Asset.Manage")]
    public void ชนิดข้อมูลต้องผูกกับคีย์ที่ตรงกับสิ่งที่มันเขียน(string entityType, string keySuffix)
        => Assert.Equal("perm:" + keySuffix, ImportExportPermissionScope.ForImport(entityType));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ชนิดที่ยังไม่มีในระบบ")]
    [InlineData("something-new-tomorrow")]
    public void ชนิดที่ไม่รู้จักต้องตกไปที่คีย์ที่เข้มที่สุด_ไม่ใช่ผ่าน(string? entityType)
    {
        // "เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็นผ่าน" (DOCTRINE §1 G3)
        Assert.Equal(ImportExportPermissionScope.FallbackKey,
            ImportExportPermissionScope.ForImport(entityType));
        Assert.Equal(PermissionKeys.JournalManage, ImportExportPermissionScope.FallbackKey);
    }

    // ═══ ครึ่งที่ 2 — คนที่ทำงานนี้อยู่ทุกวันนี้ต้องไม่ถูกกัน ═══

    [Theory]
    [InlineData("contacts")]
    [InlineData("products")]
    [InlineData("chartofaccounts")]
    [InlineData("journalentries")]
    [InlineData("documents")]
    [InlineData("payments")]
    [InlineData("banktransactions")]
    [InlineData("budgets")]
    [InlineData("projects")]
    [InlineData("opening-ar")]
    public void นักบัญชีต้องนำเข้าได้เหมือนเดิมทุกชนิดที่เคยทำ(string entityType)
        => Assert.Contains(ImportExportPermissionScope.ForImport(entityType), AccountantAutoKeys);

    [Theory]
    [InlineData("contacts")]
    [InlineData("products")]
    [InlineData("journalentries")]
    [InlineData("documents")]
    [InlineData("payments")]
    [InlineData("banktransactions")]
    [InlineData("stock-movements")]
    [InlineData("stock-balances")]
    public void นักบัญชีต้องส่งออกได้เหมือนเดิมทุกชนิดที่ไม่ใช่ข้อมูลพนักงาน(string entityType)
        => Assert.Contains(ImportExportPermissionScope.ForExport(entityType), AccountantAutoKeys);

    [Fact]
    public void ทุกคีย์ที่ตัวตัดสินคืนต้องเป็นคีย์สิทธิ์จริงในระบบ()
    {
        // คีย์ที่พิมพ์ผิดจะทำให้ PermissionService โยน ArgumentException ตอนรันจริง
        foreach (var e in new[]
        {
            "contacts", "products", "chartofaccounts", "journalentries", "documents",
            "banktransactions", "stock-opening", "fixed-assets", "employees",
            "payments", "projects", "budgets", "opening-ar", "opening-ap", "ไม่รู้จัก",
        })
        {
            Assert.True(PermissionKeys.IsPermissionKey(ImportExportPermissionScope.ForImport(e)));
            Assert.True(PermissionKeys.IsPermissionKey(ImportExportPermissionScope.ForExport(e)));
        }
    }

    [Fact]
    public void ข้อความปฏิเสธต้องบอกทางไปต่อเสมอ()
    {
        var msg = ImportExportPermissionScope.DeniedMessage(
            "journalentries", PermissionKeys.JournalManage, isExport: false);
        Assert.Contains("นำเข้า", msg);
        Assert.Contains(PermissionKeys.JournalManage, msg);
        Assert.Contains("เจ้าของบริษัท", msg);
    }
}
