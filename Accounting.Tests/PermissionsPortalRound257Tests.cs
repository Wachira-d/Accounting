using System.Reflection;
using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Recurring;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Tests.Db;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 257 ชุด 1 (สิทธิ์ + พอร์ทัล) — เทสต์ pure + controller ด้วยตัวแทน (ไม่ต้องมีฐาน) ทั้งสองทิศ:
/// A-H1 ด่านเขียนรายการประจำ · A-M7 ธงปิดหนี้อัตโนมัติเปลี่ยนจริง · C-H1 OA กลางห้ามเป็น OA ของร้าน · D-H1 ด่านผู้ให้สิทธิ์พอร์ทัล ·
/// D-H2 ใบที่ลูกค้าเห็นได้ · D-H3 ตารางธงต่อเส้น (negative ทุกธง)
/// </summary>
public class PermissionsPortalRound257Tests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static IPermissionService Perms(params string[] keys)
        => F4Proxy<IPermissionService>.Create((name, a) => name == nameof(IPermissionService.HasPermissionAsync)
            ? (object)keys.Contains((string)a[2]!) : new List<string>(keys));

    private static readonly string[] CreateOnly = { PermissionKeys.DocumentCreate };
    private static readonly string[] Approver = { PermissionKeys.DocumentCreate, PermissionKeys.DocumentApprove };

    // ═══════════════ A-H1 · ตัวตัดสิน (pure) ═══════════════

    [Fact]
    public async Task AH1_สิทธิ์สร้างอย่างเดียว_ตั้งอนุมัติอัตโนมัติไม่ได้_ผู้อนุมัติตั้งได้()
    {
        var need = RecurringWriteGate.NeedsApprove("Document", autoApprove: true, autoSendEmail: false);
        Assert.True(need);
        Assert.NotNull(await RecurringWriteGate.DenyAsync(Perms(CreateOnly), Company, User, "Document", DocumentType.Invoice, need));
        Assert.Null(await RecurringWriteGate.DenyAsync(Perms(Approver), Company, User, "Document", DocumentType.Invoice, need));
        // คีย์แยกฝั่งขายก็นับ (ตัวเดียวกับหน้าเอกสาร)
        Assert.Null(await RecurringWriteGate.DenyAsync(Perms(PermissionKeys.DocumentRevenueCreate, PermissionKeys.DocumentRevenueApprove),
            Company, User, "Document", DocumentType.Invoice, need));
    }

    [Fact]
    public async Task AH1_ส่งอีเมลอัตโนมัติ_ต้องมีสิทธิ์อนุมัติด้วย()
    {
        Assert.True(RecurringWriteGate.NeedsApprove("Document", autoApprove: false, autoSendEmail: true));
        Assert.Contains("Document.Approve",
            await RecurringWriteGate.DenyAsync(Perms(CreateOnly), Company, User, "Document", DocumentType.Invoice, true));
    }

    [Fact]
    public async Task AH1_ทิศตรงข้าม_สิทธิ์สร้างพอสำหรับรายการเป็นร่าง_ดูอย่างเดียวสร้างไม่ได้()
    {
        Assert.False(RecurringWriteGate.NeedsApprove("Document", false, false));
        Assert.Null(await RecurringWriteGate.DenyAsync(Perms(CreateOnly), Company, User, "Document", DocumentType.Invoice, false));
        Assert.Contains("Document.Create",
            await RecurringWriteGate.DenyAsync(Perms(), Company, User, "Document", DocumentType.Invoice, false));
    }

    [Fact]
    public async Task AH1_สมุดรายวัน_ต้องมีJournalManage()
    {
        Assert.Equal(RecurringWriteGate.JournalMessage,
            await RecurringWriteGate.DenyAsync(Perms(Approver), Company, User, "Journal", null, true));
        Assert.Null(await RecurringWriteGate.DenyAsync(Perms(PermissionKeys.JournalManage), Company, User, "journal", null, true));
        // สมุดรายวันไม่มีการส่งอีเมล ⇒ ธงส่งไม่ทำให้ต้องอนุมัติ
        Assert.False(RecurringWriteGate.NeedsApprove("Journal", false, true));
    }

    [Fact]
    public void AH1_ธงหลังแก้ไข_ตรงตรรกะของservice()
    {
        Assert.Equal((true, false), RecurringWriteGate.AfterUpdate("Document", true, false, null, null));    // คงไว้
        Assert.Equal((false, false), RecurringWriteGate.AfterUpdate("Document", true, false, false, null));  // ปิด
        Assert.Equal((true, true), RecurringWriteGate.AfterUpdate("Document", false, false, false, true));   // ส่ง ⇒ อนุมัติ
        Assert.Equal((false, false), RecurringWriteGate.AfterUpdate("Journal", false, true, null, null));    // สมุดรายวันไม่ส่ง
    }

    // ═══════════════ A-H1 · จุดเรียกจริงใน RecurringController ═══════════════

    private static RecurringTransactionResponse Item(bool autoApprove, bool autoSend = false, string templateType = "Document")
        => new(Guid.NewGuid(), "ค่าบริการรายเดือน", null, RecurringFrequency.Monthly, RecurringStatus.Active, DateTime.UtcNow.Date, null,
            DateTime.UtcNow.Date, null, 0, null, templateType, templateType == "Journal" ? null : DocumentType.Invoice, null, null, "{}", 1_000m,
            false, 1, autoApprove, DateTime.UtcNow, AutoSendEmail: autoSend);

    private sealed class Calls { public readonly List<string> Names = new(); }

    private static RecurringController Controller(string[] keys, RecurringTransactionResponse item, Calls calls)
    {
        var svc = F4Proxy<IRecurringTransactionService>.Create((name, _) =>
        {
            calls.Names.Add(name);
            return name == nameof(IRecurringTransactionService.DeleteAsync) ? Task.CompletedTask : item;
        });
        var auto = F4Proxy<IRecurringAutopilotService>.Create((_, _) => null);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, User.ToString()) }, "test"));
        return new RecurringController(svc, auto, Perms(keys))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };
    }

    private static CreateRecurringTransactionRequest CreateReq(bool autoApprove, bool autoSend = false)
        => new("ค่าบริการ", null, RecurringFrequency.Monthly, DateTime.UtcNow.Date, null, null, "Document", DocumentType.Invoice, Guid.NewGuid(), "{}",
            AutoApprove: autoApprove, AutoSendEmail: autoSend);

    private static UpdateRecurringTransactionRequest UpdateReq(bool? autoApprove = null, bool? autoSend = null)
        => new(Name: "ใหม่", Description: null, Frequency: null, EndDate: null, MaxRuns: null, DocumentType: null, ContactId: null, TemplateData: null,
            NotifyBeforeRun: null, NotifyDaysBefore: null, AutoApprove: autoApprove, Status: null, AutoSendEmail: autoSend);

    private static int? Code<T>(ActionResult<T> r) => (r.Result as ObjectResult)?.StatusCode;

    [Fact]
    public async Task AH1_Create_สิทธิ์สร้างอย่างเดียว_AutoApprove_403_ไม่เรียกservice()
    {
        var calls = new Calls();
        var r = await Controller(CreateOnly, Item(false), calls).Create(Company, CreateReq(autoApprove: true));
        Assert.Equal(403, Code(r));
        Assert.DoesNotContain(nameof(IRecurringTransactionService.CreateAsync), calls.Names);
        // AutoSend ⇒ ต้องอนุมัติด้วยเช่นกัน
        Assert.Equal(403, Code(await Controller(CreateOnly, Item(false), calls).Create(Company, CreateReq(false, autoSend: true))));
    }

    [Fact]
    public async Task AH1_Create_ผู้อนุมัติ_AutoApprove_201_และสิทธิ์สร้างอย่างเดียวสร้างร่างได้()
    {
        var calls = new Calls();
        Assert.Equal(201, Code(await Controller(Approver, Item(true), calls).Create(Company, CreateReq(autoApprove: true))));
        Assert.Contains(nameof(IRecurringTransactionService.CreateAsync), calls.Names);
        Assert.Equal(201, Code(await Controller(CreateOnly, Item(false), new Calls()).Create(Company, CreateReq(autoApprove: false))));
        Assert.Equal(403, Code(await Controller(Array.Empty<string>(), Item(false), new Calls()).Create(Company, CreateReq(false))));
    }

    [Fact]
    public async Task AH1_Update_คงอนุมัติอัตโนมัติไว้_สิทธิ์สร้างอย่างเดียว403_ปิดธงแล้วแก้ได้()
    {
        var calls = new Calls();
        Assert.Equal(403, Code(await Controller(CreateOnly, Item(autoApprove: true), calls).Update(Company, Guid.NewGuid(), UpdateReq())));
        Assert.DoesNotContain(nameof(IRecurringTransactionService.UpdateAsync), calls.Names);
        // ทางไปต่อ: ปิดทั้งสองธง ⇒ รายการกลายเป็นร่าง ⇒ สิทธิ์สร้างพอ
        var ok = new Calls();
        Assert.Equal(200, Code(await Controller(CreateOnly, Item(autoApprove: true), ok).Update(Company, Guid.NewGuid(), UpdateReq(false, false))));
        Assert.Contains(nameof(IRecurringTransactionService.UpdateAsync), ok.Names);
        // เปิดส่งอีเมลระหว่างแก้ ⇒ 403
        Assert.Equal(403, Code(await Controller(CreateOnly, Item(false), new Calls()).Update(Company, Guid.NewGuid(), UpdateReq(autoSend: true))));
    }

    [Fact]
    public async Task AH1_RunNow_Resume_รายการอนุมัติอัตโนมัติ_ต้องมีสิทธิ์อนุมัติ()
    {
        var calls = new Calls();
        Assert.Equal(403, Code(await Controller(CreateOnly, Item(autoApprove: true), calls).RunNow(Company, Guid.NewGuid())));
        Assert.Equal(403, Code(await Controller(CreateOnly, Item(autoApprove: true), calls).Resume(Company, Guid.NewGuid())));
        Assert.DoesNotContain(nameof(IRecurringTransactionService.RunNowAsync), calls.Names);
        Assert.DoesNotContain(nameof(IRecurringTransactionService.ResumeAsync), calls.Names);
        Assert.Equal(200, Code(await Controller(Approver, Item(autoApprove: true), new Calls()).RunNow(Company, Guid.NewGuid())));
        // รายการเป็นร่าง: สิทธิ์สร้างพอ (ทิศตรงข้าม)
        Assert.Equal(200, Code(await Controller(CreateOnly, Item(false), new Calls()).RunNow(Company, Guid.NewGuid())));
    }

    [Fact]
    public async Task AH1_Delete_Pause_ไม่มีสิทธิ์สร้าง403_มีสิทธิ์สร้างผ่าน()
    {
        var calls = new Calls();
        Assert.Equal(403, Code(await Controller(Array.Empty<string>(), Item(true), calls).Delete(Company, Guid.NewGuid())));
        Assert.Equal(403, Code(await Controller(Array.Empty<string>(), Item(true), calls).Pause(Company, Guid.NewGuid())));
        Assert.DoesNotContain(nameof(IRecurringTransactionService.DeleteAsync), calls.Names);
        Assert.DoesNotContain(nameof(IRecurringTransactionService.PauseAsync), calls.Names);
        // หยุด/ลบ = ลดสิ่งที่ระบบทำแทนคน ⇒ สิทธิ์สร้างพอ แม้รายการอนุมัติอัตโนมัติ
        Assert.IsType<NoContentResult>((await Controller(CreateOnly, Item(true), new Calls()).Delete(Company, Guid.NewGuid())).Result);
        Assert.Equal(200, Code(await Controller(CreateOnly, Item(true), new Calls()).Pause(Company, Guid.NewGuid())));
    }

    // ═══════════════ A-M7 ═══════════════

    [Fact]
    public void AM7_ส่งค่าเดิมหรือไม่ส่ง_ไม่ใช่การเปลี่ยน_เปลี่ยนจริงเท่านั้นต้องมีBankReconcile()
    {
        Assert.False(AutopilotBankSettleToggle.IsChange(null, true));
        Assert.False(AutopilotBankSettleToggle.IsChange(true, true));
        Assert.False(AutopilotBankSettleToggle.IsChange(false, false));
        Assert.True(AutopilotBankSettleToggle.IsChange(true, false));
        Assert.True(AutopilotBankSettleToggle.IsChange(false, true));
    }

    // ═══════════════ C-H1 ═══════════════

    [Fact]
    public void CH1_tokenของบอทกลาง_ปฏิเสธทุกบริษัท()
    {
        Assert.Equal(LineOwnOaGuard.CentralOaMessage,
            LineOwnOaGuard.Refusal(false, "tok-new", null, fetchedBotUserId: "Ucentral", centralBotUserId: "Ucentral", "tok-central", "sec-central"));
        // token ตรงค่าตั้ง OA กลาง (ถามบอทไม่ได้) ก็ปฏิเสธ
        Assert.Equal(LineOwnOaGuard.CentralOaMessage, LineOwnOaGuard.Refusal(false, "tok-central", null, null, null, "tok-central", null));
        Assert.Equal(LineOwnOaGuard.CentralOaMessage, LineOwnOaGuard.Refusal(false, null, "sec-central", null, null, null, "sec-central"));
    }

    [Fact]
    public void CH1_ทิศตรงข้าม_OAของร้านเองผ่าน_ล้างค่าผ่าน_บริษัทผู้ให้บริการตั้งไม่ได้()
    {
        Assert.Null(LineOwnOaGuard.Refusal(false, "tok-shop", "sec-shop", "Ushop", "Ucentral", "tok-central", "sec-central"));
        Assert.Null(LineOwnOaGuard.Refusal(true, "", "", null, "Ucentral", "tok-central", "sec-central"));   // ล้าง = ปลอดภัย
        Assert.Null(LineOwnOaGuard.Refusal(true, null, null, null, null, null, null));                       // ไม่แตะ token
        Assert.Equal(LineOwnOaGuard.PlatformCompanyMessage, LineOwnOaGuard.Refusal(true, "tok-shop", null, "Ushop", "Ucentral", null, null));
        Assert.NotNull(LineOwnOaGuard.PlatformNotice(true));
        Assert.Null(LineOwnOaGuard.PlatformNotice(false));
        var p = Guid.NewGuid();
        Assert.True(LineOwnOaGuard.IsPlatformCompany(p, p));
        Assert.False(LineOwnOaGuard.IsPlatformCompany(p, null));
        Assert.False(LineOwnOaGuard.IsPlatformCompany(p, Guid.NewGuid()));
    }

    // ═══════════════ D-H1 ═══════════════

    [Theory]
    [InlineData(nameof(PortalAdminController.CreateAccess))]
    [InlineData(nameof(PortalAdminController.UpdateAccess))]
    [InlineData(nameof(PortalAdminController.Deactivate))]
    public void DH1_ผู้ให้สิทธิ์พอร์ทัล_ต้องมีContactEdit_และปฏิเสธคีย์API(string method)
    {
        var m = typeof(PortalAdminController).GetMethod(method)!;
        var perm = m.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault();
        Assert.NotNull(perm);
        var key = (string)typeof(RequirePermissionAttribute).GetField("_key", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(perm)!;
        Assert.Equal(PermissionKeys.ContactEdit, key);
        Assert.NotNull(m.GetCustomAttribute<RejectApiKeyAttribute>());
    }

    // ═══════════════ D-H2 ═══════════════

    [Theory]
    [InlineData(DocumentType.Invoice, null, true)]
    [InlineData(DocumentType.TaxInvoice, null, true)]
    [InlineData(DocumentType.Receipt, null, true)]
    [InlineData(DocumentType.Quotation, null, true)]
    [InlineData(DocumentType.BillingNote, null, true)]
    [InlineData(DocumentType.CreditNote, DocumentType.TaxInvoice, true)]
    [InlineData(DocumentType.DebitNote, DocumentType.Invoice, true)]
    [InlineData(DocumentType.PurchaseInvoice, null, false)]
    [InlineData(DocumentType.PurchaseOrder, null, false)]
    [InlineData(DocumentType.Expense, null, false)]
    [InlineData(DocumentType.PaymentVoucher, null, false)]
    [InlineData(DocumentType.CreditNote, DocumentType.PurchaseInvoice, false)]
    [InlineData(DocumentType.DebitNote, DocumentType.PurchaseInvoice, false)]
    public void DH2_ใบที่ลูกค้าเห็นได้(DocumentType type, DocumentType? related, bool expected)
        => Assert.Equal(expected, DocumentPayLinkPolicy.IsCustomerFacing(type, related, null, contactIsSupplierOnly: false));

    [Fact]
    public void DH2_ใบลดหนี้ไม่รู้ฝั่ง_ไม่ให้เห็น_ผู้ใช้สั่งฝั่งขาย_เห็น()
    {
        Assert.False(DocumentPayLinkPolicy.IsCustomerFacing(DocumentType.CreditNote, null, null, false));
        Assert.False(DocumentPayLinkPolicy.IsCustomerFacing(DocumentType.CreditNote, null, null, contactIsSupplierOnly: true));
        Assert.True(DocumentPayLinkPolicy.IsCustomerFacing(DocumentType.CreditNote, null, cnDnPurchaseSideOverride: false, false));
        Assert.False(DocumentPayLinkPolicy.IsCustomerFacing(DocumentType.CreditNote, DocumentType.Invoice, cnDnPurchaseSideOverride: true, false));
    }

    // ═══════════════ D-H3 ═══════════════

    private static readonly PortalAccessFlags All = new(true, true, true, true);

    [Theory]
    [InlineData("documents")]
    [InlineData("document")]
    [InlineData("pdf")]
    [InlineData("statement")]
    [InlineData("payments")]
    [InlineData("pay")]
    [InlineData("slip")]
    [InlineData("export")]
    public void DH3_ทุกธงครบ_ทุกเส้นผ่าน_ไม่มีสิทธิ์_ทุกเส้นปฏิเสธ(string route)
    {
        Assert.Null(PortalAccessPermissions.Refusal(All, PortalAccessPermissions.Route(route)));
        Assert.Equal(PortalAccessPermissions.NoAccessMessage, PortalAccessPermissions.Refusal(null, PortalAccessPermissions.Route(route)));
    }

    [Theory]
    [InlineData(PortalPermission.ViewInvoices, "documents,document,pdf,pay,slip,export", "statement,payments")]
    [InlineData(PortalPermission.ViewStatements, "statement,payments", "documents,document,pdf,pay,slip,export")]
    [InlineData(PortalPermission.DownloadPdf, "pdf", "documents,document,statement,payments,pay,slip,export")]
    [InlineData(PortalPermission.MakePayment, "pay,slip", "documents,document,pdf,statement,payments,export")]
    public void DH3_ติ๊กออกทีละธง_เส้นของธงนั้นปฏิเสธ_เส้นอื่นผ่าน(PortalPermission off, string denied, string allowed)
    {
        var flags = new PortalAccessFlags(off != PortalPermission.ViewInvoices, off != PortalPermission.ViewStatements,
            off != PortalPermission.DownloadPdf, off != PortalPermission.MakePayment);
        foreach (var r in denied.Split(','))
            Assert.Equal(PortalAccessPermissions.Message(off), PortalAccessPermissions.Refusal(flags, PortalAccessPermissions.Route(r)));
        foreach (var r in allowed.Split(','))
            Assert.Null(PortalAccessPermissions.Refusal(flags, PortalAccessPermissions.Route(r)));
    }

    [Fact]
    public void DH3_เส้นที่ไม่รู้จัก_ล้มดัง()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PortalAccessPermissions.Route("orders"));

    [Fact]
    public void DH3_migration_ครั้งเดียว_คืนเฉพาะแถวที่falseทั้งสี่()
    {
        var sql = string.Join("\n", PortalAccessPermissions.MigrationStatements());
        Assert.Contains("'r257-portal-flags'", sql);
        Assert.Contains("\"CanViewInvoices\" = false AND \"CanViewStatements\" = false AND \"CanDownloadPdf\" = false AND \"CanMakePayment\" = false", sql);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM \"OneShotDataFixes\"", sql);
    }
}
