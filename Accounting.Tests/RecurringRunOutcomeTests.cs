using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 247 (UX รายการประจำ · คำร้องเจ้าของ 2026-11-06): กล่อง "รอบล่าสุดสร้างเอกสารแล้ว แต่มีเรื่องต้องทำต่อ" เดิมเป็นสตริงยาวบรรทัดเดียว
/// ("อนุมัติอัตโนมัติ (…) ไม่สำเร็จ: เอกสารมีจุดที่ต้องตรวจ… · ยังไม่ได้ส่งอีเมล… · ข้าม 2 งวด…") ⇒ เจ้าของ "ไม่เข้าใจว่าต้องไปทำอะไรที่ไหน" ·
/// ตอนนี้เซิร์ฟเวอร์ประกอบหัวเรื่อง + สิ่งที่ต้องทำ + ปุ่ม (<see cref="RecurringRunOutcome.Build"/>) · ทุกข้อล็อกทั้งทิศที่แก้และทิศตรงข้าม
/// (รอบสำเร็จ ⇒ ไม่มีกล่อง · ผู้ใช้อนุมัติเองแล้ว ⇒ ขั้นอนุมัติหายไป)
/// </summary>
public class RecurringRunOutcomeTests
{
    private static readonly Guid DocId = Guid.Parse("43a2828d-0000-4000-8000-000000000001");
    private static readonly Guid ContactId = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    private const string CreditWarning = "ผู้ติดต่อนี้ยังค้างชำระ 3 ใบ รวม 9,750.00 THB — ตรวจวงเงินเครดิตก่อนอนุมัติเอกสารใหม่";

    // 26 ต.ค. 2569 (เวลาไทย) = 2026-10-25T17:00Z
    private static RecurringRunDoc Doc(DocumentStatus status, string? no = "DRAFT-43a2828d")
        => new(DocId, no, status, DocumentType.Invoice, new DateTime(2026, 10, 25, 17, 0, 0, DateTimeKind.Utc));

    /// <summary>กรณีในภาพของเจ้าของ: ติดคำเตือนค้างชำระ + ติ๊กส่งอีเมล + ข้าม 2 งวด</summary>
    private static RecurringRunFacts CreditCase => new()
    {
        ApproveBlockedReason = "เอกสารมีจุดที่ต้องตรวจก่อนยืนยันการอนุมัติ (1 รายการ): " + CreditWarning,
        ApproveWarnings = new[] { CreditWarning },
        EmailHeldForDraft = true,
        SkippedPeriods = 2,
        SkippedFrom = new DateTime(2026, 8, 25, 17, 0, 0, DateTimeKind.Utc),
    };

    private static RecurringRunOutcomeView? Build(RecurringRunFacts? facts, RecurringRunDoc? doc, string? email = "a@b.co",
        bool approvedRule = false, string? legacy = "ข้อความเดิม", bool canRunNow = true)
        => RecurringRunOutcome.Build(facts, legacy, doc, ContactId, email, approvedRule, canRunNow);

    [Fact]
    public void CreditWarning_HeadlineSaysWhyInOneSentence_AndOpenDraftIsFirstAction()
    {
        var v = Build(CreditCase, Doc(DocumentStatus.Draft))!;
        Assert.Equal("สร้างใบแจ้งหนี้งวด 26/10/2569 เป็นร่างแล้ว แต่ยังไม่อนุมัติ เพราะผู้ติดต่อนี้ยังค้างชำระ 3 ใบ รวม 9,750.00 THB", v.Headline);
        Assert.Equal("openDocument", v.Actions[0].Kind);
        Assert.Equal("เปิดใบร่างเพื่ออนุมัติ", v.Actions[0].Label);
        Assert.Equal("/pages/documents.html?openDoc=" + DocId.ToString("D"), v.Actions[0].Url);   // param ที่ documents.html อ่านจริง
        Assert.Contains("ยืนยันรับทราบ", v.Steps[0]);
        Assert.Contains(CreditWarning, v.Steps[0]);
        // ช่องอนุมัติอัตโนมัติเปิดอยู่แล้ว ไม่ใช่ต้นเหตุ — คำเตือนต้องให้คนกดรับทราบโดยออกแบบ
        Assert.Contains("เปิดอยู่แล้ว", v.AutoApproveNote);
        Assert.Contains("ระบบอัตโนมัติกดแทนไม่ได้", v.AutoApproveNote);
        Assert.Contains("§86/4", v.LegalNote);
    }

    [Fact]
    public void CreditWarning_EmailStep_SaysHonestlyThatRecurringWillNotSendAfterManualApproval()
    {
        var v = Build(CreditCase, Doc(DocumentStatus.Draft))!;
        var email = Assert.Single(v.Steps, s => s.StartsWith("อีเมล:"));
        Assert.Contains("ระบบจะไม่ส่งงวดนี้ให้เอง", email);
        Assert.Contains("กด \"ส่งอีเมล\" ที่หน้าเอกสาร", email);
    }

    [Fact]
    public void CreditWarning_WithApprovedEmailRule_SaysTheRuleWillSend()
    {
        var v = Build(CreditCase, Doc(DocumentStatus.Draft), approvedRule: true)!;
        var email = Assert.Single(v.Steps, s => s.StartsWith("อีเมล:"));
        Assert.Contains("ระบบจะส่งให้เองตามกฎ", email);
    }

    [Fact]
    public void SkippedPeriods_OffersRunNowButton()
    {
        var v = Build(CreditCase, Doc(DocumentStatus.Draft))!;
        Assert.Contains(v.Actions, a => a.Kind == "runNow" && a.Label == "สร้างงวดที่ข้าม");
        Assert.Contains(v.Steps, s => s.Contains("ข้าม 2 งวด") && s.Contains("26/08/2569"));
        // รายการถูกหยุดชั่วคราว ⇒ ไม่มีปุ่มที่กดแล้วล้ม
        var paused = Build(CreditCase, Doc(DocumentStatus.Draft), canRunNow: false)!;
        Assert.DoesNotContain(paused.Actions, a => a.Kind == "runNow");
    }

    [Fact]
    public void NonWarningReason_TellsWhatToFix_NotAboutAcknowledging()
    {
        var facts = new RecurringRunFacts { ApproveBlockedReason = "ปิดงวดบัญชีเดือน 10/2569 แล้ว" };
        var v = Build(facts, Doc(DocumentStatus.Draft))!;
        Assert.Equal("สร้างใบแจ้งหนี้งวด 26/10/2569 เป็นร่างแล้ว แต่อนุมัติอัตโนมัติไม่ผ่าน", v.Headline);
        Assert.Contains("ปิดงวดบัญชีเดือน 10/2569 แล้ว", v.Steps[0]);
        Assert.DoesNotContain("รับทราบ", v.Steps[0]);
    }

    [Fact]
    public void Opposite_SuccessfulRun_ShowsNoBox()
    {
        Assert.Null(Build(new RecurringRunFacts(), Doc(DocumentStatus.Approved, "INV-2569-0042"), legacy: null));
        Assert.Null(Build(null, Doc(DocumentStatus.Approved, "INV-2569-0042"), legacy: null));
    }

    [Fact]
    public void Opposite_UserApprovedDraftAfterwards_ApprovalStepDisappears()
    {
        var facts = CreditCase with { EmailHeldForDraft = false, SkippedPeriods = 0, SkippedFrom = null };
        // รอบ 263: ไม่ใช่ null อีกแล้ว — ได้บรรทัด "แก้ครบแล้ว" (AllResolved) แทนกล่องเตือน · ไม่มีขั้นตอน/ปุ่ม
        var v = Build(facts, Doc(DocumentStatus.Approved, "INV-2569-0042"))!;
        Assert.True(v.AllResolved);
        Assert.Empty(v.Steps);
        Assert.Empty(v.Actions);
        Assert.Contains(v.Resolved!, r => r.StartsWith("อนุมัติแล้ว: ใบแจ้งหนี้ INV-2569-0042"));
    }

    [Fact]
    public void UserApprovedDraft_ButEmailWasHeld_StillSaysSendFromDocument()
    {
        var facts = CreditCase with { SkippedPeriods = 0, SkippedFrom = null };
        var v = Build(facts, Doc(DocumentStatus.Approved, "INV-2569-0042"))!;
        Assert.Equal("ใบแจ้งหนี้ INV-2569-0042 อนุมัติแล้ว — เหลือเรื่องต้องทำต่อ", v.Headline);
        Assert.Single(v.Steps);
        Assert.Equal("เปิดเอกสารเพื่อส่งอีเมล", Assert.Single(v.Actions).Label);
        Assert.Null(v.LegalNote);
    }

    [Fact]
    public void ContactWithoutEmail_SaysNotSent_AndLinksToContactPage()
    {
        var facts = new RecurringRunFacts { EmailSkippedNoContactEmail = true };
        var v = Build(facts, Doc(DocumentStatus.Approved, "INV-2569-0042"), email: "  ")!;
        Assert.Contains(v.Steps, s => s.StartsWith(RecurringEmailReadiness.NotSentNoEmail));
        Assert.Contains(v.Actions, a => a.Kind == "openContact" && a.Url == "/pages/contacts.html?id=" + ContactId.ToString("D"));
    }

    [Fact]
    public void LegacyRowWithoutFacts_ShowsOldTextAndOpenDraftButton()
    {
        var v = Build(null, Doc(DocumentStatus.Draft), legacy: "ข้อความเก่า")!;
        Assert.Contains(v.Steps, s => s.Contains("ข้อความเก่า"));
        Assert.Equal("openDocument", v.Actions[0].Kind);
        Assert.Equal("ข้อความเก่า", v.RawNotice);
    }

    [Fact]
    public void Facts_RoundTripThroughJson()
    {
        var json = JsonSerializer.Serialize(CreditCase);
        Assert.DoesNotContain("IsEmpty", json);
        var back = JsonSerializer.Deserialize<RecurringRunFacts>(json)!;
        Assert.Equal(CreditWarning, Assert.Single(back.ApproveWarnings!));
        Assert.Equal(2, back.SkippedPeriods);
        Assert.True(back.EmailHeldForDraft);
        Assert.True(new RecurringRunFacts().IsEmpty);
        Assert.False(back.IsEmpty);
    }

    // ── ผู้ติดต่อมี/ไม่มีอีเมล (คำร้องเจ้าของ "ผู้ติดต่อยังไม่ได้ตั้งค่าอีเมลเลย ทำไมติ๊กส่งเมลได้") ──

    [Theory]
    [InlineData("billing@amhappiness.co.th", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void HasDeliverableEmail_MatchesSender(string? email, bool expected)
        => Assert.Equal(expected, RecurringEmailReadiness.HasDeliverableEmail(email));

    [Fact]
    public void SettingWarning_ContactWithoutEmail_WarnsButIsNotABlock()
    {
        var w = RecurringEmailReadiness.SettingWarning(autoSendEmail: true, isDocumentTemplate: true, ContactId, contactEmail: null);
        Assert.NotNull(w);
        Assert.Contains("ยังไม่มีอีเมล", w);
        Assert.Contains("เพิ่มอีเมลทีหลังได้", w);
    }

    [Fact]
    public void Opposite_SettingWarning_ContactWithEmail_OrEmailNotTicked_IsNull()
    {
        Assert.Null(RecurringEmailReadiness.SettingWarning(true, true, ContactId, "a@b.co"));
        Assert.Null(RecurringEmailReadiness.SettingWarning(false, true, ContactId, null));
        Assert.Null(RecurringEmailReadiness.SettingWarning(true, false, ContactId, null));   // สมุดรายวัน — ไม่มีอีเมลให้ส่ง
        Assert.Null(RecurringEmailReadiness.SettingWarning(true, true, null, null));
    }
}
