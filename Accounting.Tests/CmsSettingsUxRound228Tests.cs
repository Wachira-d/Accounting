using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Cms;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 228 (TEST_PLAN CMSUX-A2 · D1–D3 · E1–E2 · <c>erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md</c> แพ็ก A (รีวิว S-1–S-7) · D · E) — ตัวตัดสินล้วน
/// </summary>
public class CmsSettingsUxRound228Tests
{
    // ═════════ แพ็ก D · Q1 "ลูกค้าต้องจ่ายก่อนไหม" ═════════

    [Fact]
    public void D_คำตอบธุรกิจ_เลือกชนิดเอกสาร_ไปกลับตรงกัน_ชนิดขั้นสูงไม่มีคำตอบ()
    {
        Assert.Equal(BookingType.PrePayment, BookingPayFirst.TypeFor(BookingPayFirst.PayFirst));
        Assert.Equal(BookingType.Appointment, BookingPayFirst.TypeFor(BookingPayFirst.AtShop));
        Assert.Equal(BookingType.Lead, BookingPayFirst.TypeFor(BookingPayFirst.ShopConfirms));
        Assert.Null(BookingPayFirst.TypeFor("Guaranteed"));
        foreach (var o in BookingPayFirst.Options) Assert.Equal(o.Value, BookingPayFirst.AnswerFor(BookingPayFirst.TypeFor(o.Value)!.Value));
        Assert.Null(BookingPayFirst.AnswerFor(BookingType.Guaranteed));   // ขั้นสูง (ผู้ทำบัญชี)
        Assert.NotNull(BookingPayFirst.DepositConflict(BookingType.Appointment, null, 50m));
        Assert.Null(BookingPayFirst.DepositConflict(BookingType.PrePayment, null, 50m));
        Assert.Null(BookingPayFirst.DepositConflict(BookingType.Appointment, null, null));
    }

    [Fact]
    public void D_แม่แบบทุกตัว_ตั้งมัดจำแล้วต้องเป็นชนิดเก็บเงินล่วงหน้า_และส่งคำตอบจากเซิร์ฟเวอร์()
    {
        foreach (var p in CmsBookingServiceSeeder.PresetResponses())
            foreach (var s in p.Services)
            {
                if (s.DepositPercent is > 0m || s.DepositAmount is > 0m)
                    Assert.True(s.BookingType == BookingType.PrePayment, $"{p.Key}/{s.Name}: ตั้งมัดจำแต่ชนิดเก็บเงินล่วงหน้าไม่ได้");
                Assert.Equal(BookingPayFirst.AnswerFor(s.BookingType), s.PayFirst);
            }
    }

    [Fact]
    public void D_Q2_วันปิดของเว็บ_ใช้กับวันรับของ_วันที่อ่านไม่ได้ข้าม()
    {
        var json = BookingSiteHours.Serialize(new BookingScheduleDto
        {
            Exceptions = new()
            {
                new BookingDateExceptionDto { Date = "2026-12-31", Closed = true },
                new BookingDateExceptionDto { Date = "2027-01-02", Closed = false, Start = "10:00", End = "12:00" },
                new BookingDateExceptionDto { Date = "bad", Closed = true },
            },
        });
        var set = BookingSiteHours.ClosedDates(json);
        Assert.Single(set);
        Assert.Contains(new DateOnly(2026, 12, 31), set);
        Assert.Empty(BookingSiteHours.ClosedDates(null));
    }

    // ═════════ แพ็ก E — แผงสิ่งที่ยังไม่ได้ตั้งค่า · wizard ═════════

    private static CmsSiteSetupFacts Facts(bool bookings = true, int services = 1, bool payable = true, bool online = false, string? free = null,
        bool published = false, bool contact = true, bool privacy = true, string? payProblem = null)
        => new(published, contact, privacy, bookings, services, payProblem, Array.Empty<string>(), false, 0, false, null, null,
            NeedsTransferAccount: bookings, TransferPayable: payable, OnlineReady: online, FreeDeliveryWarning: free);

    [Fact]
    public void E_แผงนับครบไม่ครบ_และข้อความก่อนเผยแพร่_ไม่นับข้อเผยแพร่()
    {
        var full = CmsSiteSetupChecklist.Evaluate(Facts(published: true));
        var (d, t) = CmsSiteSetupChecklist.Score(full);
        Assert.Equal(t, d);
        Assert.Null(CmsSiteSetupChecklist.BeforePublishNote(full));

        var missing = CmsSiteSetupChecklist.Evaluate(Facts(services: 0, payable: false, free: "ส่งฟรีเงียบ", contact: false));
        Assert.Contains(missing, i => i.Key == "transfer" && !i.Done && i.Blocking);
        Assert.Contains(missing, i => i.Key == "services" && !i.Done);
        Assert.Contains(missing, i => i.Key == "shipping" && !i.Done);
        var note = CmsSiteSetupChecklist.BeforePublishNote(missing)!;
        Assert.Contains("ช่องทางรับเงิน", note);
        Assert.DoesNotContain("เผยแพร่เว็บไซต์", note);
        // ทิศตรงข้าม: ชำระออนไลน์พร้อม ⇒ ไม่ต้องมีบัญชีรับโอน
        Assert.True(CmsSiteSetupChecklist.Evaluate(Facts(payable: false, online: true)).Single(i => i.Key == "transfer").Done);
    }

    [Fact]
    public void E_Q4_จำนวนห้องจากwizard_วนทุกประเภท_ค่าว่างคือ11ห้องเดิม()
    {
        var three = LodgingSeedDefaults.UnitsFor(LodgingSeedDefaults.DefaultWizardRoomCount);
        Assert.Equal(new[] { "101" }, three[0]);
        Assert.Equal(new[] { "201" }, three[1]);
        Assert.Equal(new[] { "301" }, three[2]);
        Assert.Equal(LodgingSeedDefaults.TotalUnits, LodgingSeedDefaults.UnitsFor(null).Sum(u => u.Length));
        Assert.Equal(LodgingSeedDefaults.TotalUnits, LodgingSeedDefaults.UnitsFor(99).Sum(u => u.Length));   // เพดาน
        Assert.Single(LodgingSeedDefaults.UnitsFor(0).SelectMany(u => u));                                // อย่างน้อย 1
    }

    // ═════════ แพ็ก A — รีวิว S-3/S-7 ═════════

    [Fact]
    public void A_S3_auditปิดเลขบัญชีและพร้อมเพย์()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(SiteTransferAccount.MaskForAudit(
            new BookingTransferInfo("กสิกรไทย", "1234567890", "ร้าน", null, "0812345678", "https://x/qr.png")));
        Assert.DoesNotContain("1234567890", json);
        Assert.DoesNotContain("0812345678", json);
        Assert.Contains("7890", json);
        Assert.Null(SiteTransferAccount.MaskForAudit(null));
    }

    [Fact]
    public void A_S7_ล้างบัญชีที่คำสั่งซื้อหรือที่พักใช้_ปฏิเสธพร้อมเหตุ_ทิศตรงข้ามผ่าน()
    {
        Assert.Contains("คำสั่งซื้อ", SiteTransferAccount.ClearBreaksOtherWorkProblem(false, false, ordersTakeTransfer: true, lodgingGuestPays: false));
        Assert.Contains("ที่พัก", SiteTransferAccount.ClearBreaksOtherWorkProblem(false, false, false, lodgingGuestPays: true));
        Assert.Null(SiteTransferAccount.ClearBreaksOtherWorkProblem(payableAfter: true, false, true, true));
        Assert.Null(SiteTransferAccount.ClearBreaksOtherWorkProblem(false, onlineReady: true, true, true));
        Assert.Null(SiteTransferAccount.ClearBreaksOtherWorkProblem(false, false, false, false));
    }

    [Fact]
    public void A_S4_ตัวตรวจเดียว_เลขบัญชีและพร้อมเพย์ผิดถูกปฏิเสธ_ไม่มีเลขบัญชีล้างชื่อธนาคาร()
    {
        var errs = new List<string>();
        SiteTransferAccount.Normalize("กสิกรไทย", "12-3", "ร้าน", null, "0812345678", null, _ => false, errs);
        Assert.Contains(errs, e => e.Contains("10–15 หลัก"));
        Assert.Contains(errs, e => e.Contains("พร้อมเพย์"));
        var ok = new List<string>();
        var t = SiteTransferAccount.Normalize("กสิกรไทย", null, "ร้าน", "สยาม", "081-234-5678", null, _ => true, ok);
        Assert.Empty(ok);
        Assert.Null(t.BankName); Assert.Null(t.Branch);   // ไม่มีเลขบัญชี ⇒ ไม่เก็บชื่อธนาคาร/สาขาลอย ๆ
        Assert.Equal("0812345678", t.PromptPayId);
    }
}
