using Accounting.Helpers;
using Accounting.Models.Constants;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ไฟล์แนบของชนิดไหน แนบ/ลบ/ดู ได้ด้วยสิทธิ์อะไร — คำตัดสินเจ้าของ รอบ 193 ข้อ 29
/// ครึ่งที่ 1: ทุกชนิดที่ระบบเขียนลงตาราง FileAttachments ผูกกับคีย์ของโมดูลนั้น (ไม่ใช่ "ล็อกอินก็พอ") · ข้อมูลเงินเดือน/สลิปแขก
/// ต้องมีสิทธิ์แม้แค่ "ดู" · ชนิดที่ไม่รู้จัก = ห้ามเขียน
/// ครึ่งที่ 2 (ทิศตรงข้าม): 10 ชนิดที่หน้าเว็บแนบได้อยู่แล้ว<b>ยังแนบได้</b> · ผู้ติดต่อ/สินค้าไม่ถูกปิดการดู ·
/// ชื่อเก่า/ตัวพิมพ์ต่างกันยังจับคู่ได้ · ข้อความปฏิเสธบอกทางไปต่อ
/// </summary>
public class AttachmentPermissionScopeTests
{
    // ── ครึ่งที่ 1: ผูกกับสิทธิ์ของโมดูล ──

    [Theory]
    [InlineData("Contact", PermissionKeys.ContactEdit)]
    [InlineData("Product", PermissionKeys.ProductEdit)]
    [InlineData("Project", PermissionKeys.JournalManage)]
    [InlineData("JournalEntry", PermissionKeys.JournalManage)]
    [InlineData("FixedAsset", PermissionKeys.AssetManage)]
    [InlineData("PayrollRun", PermissionKeys.PayrollRun)]
    [InlineData("WhtCredit", PermissionKeys.TaxFile)]
    [InlineData("StatutoryRemittance", PermissionKeys.TaxFile)]
    [InlineData("SubscriptionReceipt", PermissionKeys.BillingManage)]
    [InlineData("SubscriptionInvoice", PermissionKeys.BillingManage)]
    [InlineData("LodgingReservation", PermissionKeys.LodgingManage)]
    [InlineData("SiteOrder", PermissionKeys.CmsOrderManage)]
    public void เขียนไฟล์แนบต้องมีคีย์ของโมดูลนั้น(string entityType, string key)
    {
        var rule = AttachmentPermissionScope.Resolve(entityType);
        Assert.True(rule.IsKnown);
        Assert.Contains(key, rule.WriteAnyOf);
    }

    [Theory]
    [InlineData("Document", AttachmentOwnerKind.Document)]
    [InlineData("Expense", AttachmentOwnerKind.Document)]      // ค่าใช้จ่าย = เอกสารชนิด Expense (ไม่มีตารางแยก)
    [InlineData("Payment", AttachmentOwnerKind.Payment)]
    [InlineData("ExpenseClaim", AttachmentOwnerKind.ExpenseClaim)]
    [InlineData("PayrollRun", AttachmentOwnerKind.PayrollRun)]
    [InlineData("JournalEntry", AttachmentOwnerKind.JournalEntry)]
    public void ชนิดที่ต้องค้นแถวเจ้าของ_ตัดสินด้วยข้อมูลของแถวนั้น(string entityType, AttachmentOwnerKind kind)
        => Assert.Equal(kind, AttachmentPermissionScope.Resolve(entityType).Kind);

    [Fact]
    public void ใบเบิกของคนอื่น_ทั้งดูและแนบต้องเป็นผู้ตรวจ()
    {
        var rule = AttachmentPermissionScope.Resolve("ExpenseClaim");
        Assert.Contains(PermissionKeys.HrAdmin, rule.ReadAnyOf);
        Assert.Contains(PermissionKeys.ExpenseApprove, rule.ReadAnyOf);
        Assert.Contains(PermissionKeys.ExpenseApprove, rule.WriteAnyOf);
    }

    [Theory]
    [InlineData("LodgingReservation", PermissionKeys.LodgingManage)]   // สลิปของแขก = ข้อมูลบุคคลภายนอก
    [InlineData("SiteOrder", PermissionKeys.CmsOrderManage)]
    public void ไฟล์ของบุคคลภายนอก_แค่ดูก็ต้องมีสิทธิ์(string entityType, string key)
        => Assert.Contains(key, AttachmentPermissionScope.Resolve(entityType).ReadAnyOf);

    [Theory]
    [InlineData("ชนิดที่ยังไม่มีใครรู้จัก")]
    [InlineData("BankTransaction")]
    [InlineData("OcrScanResult")]   // ชื่อนี้เป็น AuditLog ไม่ใช่ไฟล์แนบ — ต้องไม่ถูกเดาว่าเป็น OcrScan
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ชนิดที่ไม่รู้จัก_ห้ามเขียนและห้ามอัปโหลด(string? entityType)
    {
        var rule = AttachmentPermissionScope.Resolve(entityType);
        Assert.False(rule.IsKnown);
        Assert.Equal(AttachmentOwnerKind.Unknown, rule.Kind);
        Assert.False(rule.ClientUploadAllowed);
        Assert.Null(rule.CanonicalType);
        Assert.Contains("ไม่รู้จักชนิด", AttachmentPermissionScope.UnknownTypeMessage(entityType));
    }

    [Theory]
    [InlineData("OcrScan")]
    [InlineData("StatutoryRemittance")]
    [InlineData("SubscriptionReceipt")]
    [InlineData("SubscriptionInvoice")]
    [InlineData("LodgingReservation")]
    [InlineData("SiteOrder")]
    public void ชนิดที่ระบบแนบเอง_อัปโหลดจากหน้าเว็บไม่ได้แต่มีด่านลบ(string entityType)
    {
        var rule = AttachmentPermissionScope.Resolve(entityType);
        Assert.True(rule.IsKnown);
        Assert.False(rule.ClientUploadAllowed);
        Assert.NotEmpty(rule.WriteAnyOf);
    }

    // ── ครึ่งที่ 2: ของที่ใช้ได้อยู่แล้วต้องยังใช้ได้ ──

    [Theory]
    [InlineData("Document")]
    [InlineData("Contact")]
    [InlineData("Payment")]
    [InlineData("JournalEntry")]
    [InlineData("FixedAsset")]
    [InlineData("Expense")]
    [InlineData("ExpenseClaim")]
    [InlineData("Product")]
    [InlineData("Project")]
    [InlineData("PayrollRun")]
    public void สิบชนิดที่หน้าเว็บแนบได้อยู่แล้ว_ยังแนบได้และเก็บชื่อเดิม(string entityType)
    {
        var rule = AttachmentPermissionScope.Resolve(entityType);
        Assert.True(rule.ClientUploadAllowed);
        Assert.Equal(entityType, rule.CanonicalType);   // ชื่อในตารางไม่เปลี่ยน ⇒ ไฟล์เดิมยังค้นเจอ
    }

    [Fact]
    public void ภาษีถูกหัก_แนบก่อนบันทึกได้_ส่วนชนิดอื่นไม่รับเจ้าของว่าง()
    {
        Assert.True(AttachmentPermissionScope.Resolve("WhtCredit").ClientUploadAllowed);
        Assert.True(AttachmentPermissionScope.Resolve("WhtCredit").AllowsUnsavedOwner);
        Assert.False(AttachmentPermissionScope.Resolve("Contact").AllowsUnsavedOwner);
        Assert.False(AttachmentPermissionScope.Resolve("Document").AllowsUnsavedOwner);
    }

    [Theory]
    [InlineData("Contact")]
    [InlineData("Product")]
    [InlineData("FixedAsset")]
    [InlineData("Project")]
    [InlineData("OcrScan")]
    public void ข้อมูลที่สมาชิกดูได้ในโมดูลอยู่แล้ว_การดูไฟล์ไม่ถูกปิดเพิ่ม(string entityType)
        => Assert.Empty(AttachmentPermissionScope.Resolve(entityType).ReadAnyOf);

    [Theory]
    [InlineData("contacts", "Contact")]
    [InlineData("products", "Product")]
    [InlineData("document", "Document")]
    [InlineData("PAYROLLRUN", "PayrollRun")]
    [InlineData(" Contact ", "Contact")]
    public void ชื่อเก่าและตัวพิมพ์ต่างกัน_ยังจับคู่ชนิดได้(string entityType, string canonical)
        => Assert.Equal(canonical, AttachmentPermissionScope.Resolve(entityType).CanonicalType);

    [Fact]
    public void ข้อความปฏิเสธ_บอกคีย์ที่ต้องมีโดยไม่มีคำนำหน้า_perm()
    {
        var rule = AttachmentPermissionScope.Resolve("FixedAsset");
        var msg = AttachmentPermissionScope.DeniedMessage(rule, "แนบไฟล์", rule.WriteAnyOf);
        Assert.Contains("Asset.Manage", msg);
        Assert.DoesNotContain("perm:", msg);
        Assert.Contains("สินทรัพย์ถาวร", msg);
        Assert.Contains("เพิ่มสิทธิ์", msg);   // ทางไปต่อ
    }
}
