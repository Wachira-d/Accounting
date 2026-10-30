using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179) — ตัวตัดสิน pure ของ: เอกสารกฎหมายชนิด "refund" (ไม่แตะเวอร์ชันความยินยอม PDPA) · ช่องติ๊กยอมรับนโยบาย ·
/// ด่านเปิดรับเงินจริง (ร้าน/แพลตฟอร์ม) · ทดลองจ่าย · หน้าทดลองจ่าย · ยกเลิกแพ็กเกจสิ้นรอบ — ทุกด่านมีทิศตรงข้าม (F2 ข้อ 6/8)
/// </summary>
public class PlatformGatewayRefundPolicyTests
{
    // ═══════════════ ① ชนิดเอกสารกฎหมาย ═══════════════

    [Fact]
    public void Every_kind_has_distinct_title_path_slug_and_template()
    {
        Assert.Equal(new[] { "privacy", "terms", "refund" }, LegalDocumentDefaults.Kinds);
        Assert.Equal(3, LegalDocumentDefaults.Kinds.Select(LegalDocumentDefaults.Title).Distinct().Count());
        Assert.Equal(3, LegalDocumentDefaults.Kinds.Select(LegalDocumentDefaults.PlatformPath).Distinct().Count());
        Assert.Equal(3, LegalDocumentDefaults.Kinds.Select(CmsLegalPages.Slug).Distinct().Count());
        Assert.Equal(3, LegalDocumentDefaults.Kinds.Select(LegalDocumentDefaults.ShopTemplate).Distinct().Count());
        Assert.Equal(3, LegalDocumentDefaults.Kinds.Select(LegalDocumentDefaults.PlatformBuiltIn).Distinct().Count());
        Assert.Equal("/refund-policy.html", LegalDocumentDefaults.PlatformPath("refund"));
        Assert.Equal("legal/refund", CmsLegalPages.Slug("refund"));
        Assert.True(LegalDocumentDefaults.IsKind("refund"));
    }

    [Fact]
    public void Unknown_kind_throws_instead_of_silently_becoming_privacy()
    {
        // บั๊กที่กัน: ternary สองทางเดิม "kind == terms ? … : privacy" ⇒ ชนิดใหม่ตกเป็น privacy เงียบ ๆ
        Assert.False(LegalDocumentDefaults.IsKind("cookies"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegalDocumentDefaults.Title("cookies"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegalDocumentDefaults.PlatformBuiltIn("cookies"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CmsLegalPages.Slug("cookies"));
    }

    [Fact]
    public void Refund_is_not_part_of_pdpa_consent_version()
    {
        Assert.Equal(new[] { "privacy", "terms" }, LegalDocumentDefaults.ConsentKinds);
        Assert.DoesNotContain(LegalDocumentDefaults.KindRefund, LegalDocumentDefaults.ConsentKinds);
        // เวอร์ชันความยินยอมเดิมไม่เปลี่ยนรูป (แถวเก่า "1.0" ยังตรง)
        Assert.Equal("1.0", LegalDocumentPolicy.ConsentVersion("1.0", "1.0"));
        Assert.Equal("terms 1.0 · refund 1.1", LegalDocumentPolicy.PurchasePolicyVersion("1.0", "1.1"));
    }

    [Fact]
    public void Platform_refund_v1_states_no_refund_and_end_of_cycle_cancellation_with_known_tokens()
    {
        var md = LegalDocumentDefaults.PlatformBuiltIn("refund");
        Assert.Contains("ไม่มีการคืนเงิน", md);
        Assert.Contains("มีผลเมื่อสิ้นรอบบิลที่ชำระแล้ว", md);
        Assert.Contains("ใบลดหนี้", md);
        Assert.Contains("พระราชบัญญัติคุ้มครองผู้บริโภค", md);
        var html = LegalMarkdown.Render(md, LegalDocumentDefaults.BuildPlatformTokens("บริษัท เน็กซ์แอค จำกัด", "0105500000001", "กรุงเทพฯ", "02-000-0000", "billing@nextacc.example", "NextAcc"));
        Assert.Contains("บริษัท เน็กซ์แอค จำกัด", html);
        Assert.DoesNotContain("{{", html);
        Assert.Empty(LegalMarkdown.Warnings(md, LegalDocumentDefaults.PlatformTokens.Select(t => t.Token)));
    }

    [Fact]
    public void Shop_refund_template_reads_numbers_from_settings()
    {
        var tokens = CmsLegalPages.BuildShopTokens(CmsContentTokens.BuildValues("ร้านขนมบ้านสวน", null, "02-111-2222", "shop@example.test",
            "1 ถ.สุขุมวิท", "0105551234567", null, siteName: "บ้านสวน"), isJuristic: true);
        foreach (var kv in CmsLegalPages.BuildRefundTokens(30m, 10, 5)) tokens[kv.Key] = kv.Value;
        var md = LegalDocumentDefaults.ShopTemplate("refund");
        var html = LegalMarkdown.Render(md, tokens);
        Assert.Contains("หักมัดจำ 30%", html);
        Assert.Contains("10 วัน", html);
        Assert.Contains("5 วัน", html);
        Assert.DoesNotContain("{{", html);
        Assert.Empty(LegalMarkdown.Warnings(md, CmsLegalPages.KnownTokens("refund")));
        // ไม่ตั้งค่า ⇒ ค่าเริ่มต้น · ไม่ริบมัดจำ ⇒ คืนเต็ม
        var d = CmsLegalPages.BuildRefundTokens(null, null, null);
        Assert.Equal("7", d["shop.returnWindowDays"]);
        Assert.Equal("14", d["shop.refundProcessingDays"]);
        Assert.Contains("คืนมัดจำเต็มจำนวน", d["shop.depositForfeit"]);
        Assert.Equal("7", CmsLegalPages.BuildRefundTokens(null, 999, 0)["shop.returnWindowDays"]);   // นอกช่วง ⇒ ค่าเริ่มต้น
    }

    [Fact]
    public void Shop_terms_template_points_to_refund_policy_instead_of_duplicating_it()
    {
        var md = LegalDocumentDefaults.ShopTemplate("terms");
        Assert.Contains("(refund)", md);
        Assert.DoesNotContain("ภายใน 7 วันนับแต่ได้รับ", md);   // ตัวเลขอยู่ในนโยบายคืนเงินที่เดียว (อ่านจากค่าตั้ง)
    }

    [Fact]
    public void Refund_contradictions_warn_only_on_real_conflicts()
    {
        Assert.NotEmpty(CmsLegalPages.RefundContradictions("ยกเลิกได้ คืนมัดจำเต็มจำนวน", 50m));
        Assert.NotEmpty(CmsLegalPages.RefundContradictions("ยกเลิกแล้วร้านริบมัดจำ", null));
        Assert.NotEmpty(CmsLegalPages.RefundContradictions("ไม่คืนเงินทุกกรณี", null));
        // ทิศตรงข้าม: ข้อความที่ตรงกับค่าตั้ง ⇒ ไม่เตือน
        Assert.Empty(CmsLegalPages.RefundContradictions("ยกเลิกแล้วร้านริบมัดจำ 50%", 50m));
        Assert.Empty(CmsLegalPages.RefundContradictions("ไม่ริบมัดจำ คืนเต็ม", null));
        Assert.Empty(CmsLegalPages.RefundContradictions(null, 50m));
    }

    [Fact]
    public void Shop_policy_version_changes_with_text_or_settings_and_is_stable_otherwise()
    {
        var a = CmsLegalPages.ShopPolicyVersion(null, null, null, null, null, null, null);
        Assert.StartsWith("shop-", a);
        Assert.Equal(a, CmsLegalPages.ShopPolicyVersion(null, " ", null, "", null, null, null));   // ว่าง = แม่แบบ
        Assert.NotEqual(a, CmsLegalPages.ShopPolicyVersion(null, null, null, null, 20m, null, null));
        Assert.NotEqual(a, CmsLegalPages.ShopPolicyVersion(null, null, null, "## ของร้าน", null, null, null));
        Assert.NotEqual(a, CmsLegalPages.ShopPolicyVersion(null, null, "https://shop.example/refund", null, null, null, null));
        Assert.NotEqual(a, CmsLegalPages.ShopPolicyVersion(null, null, null, null, null, 3, null));
    }

    // ═══════════════ ② ช่องติ๊กยอมรับนโยบาย ═══════════════

    [Fact]
    public void Problem_requires_tick_rejects_stale_version_and_exempts_staff()
    {
        Assert.Equal(PurchasePolicyAcceptance.MissingMessage, PurchasePolicyAcceptance.Problem(false, false, null, "v1"));
        Assert.Equal(PurchasePolicyAcceptance.StaleMessage, PurchasePolicyAcceptance.Problem(false, true, "v0", "v1"));
        Assert.Null(PurchasePolicyAcceptance.Problem(false, true, "v1", "v1"));
        Assert.Null(PurchasePolicyAcceptance.Problem(false, true, null, "v1"));   // ผู้เรียกที่ไม่ส่งฉบับ = ยอมรับฉบับปัจจุบัน
        Assert.Null(PurchasePolicyAcceptance.Problem(true, false, null, "v1"));   // พนักงานบันทึกแทน
    }

    [Fact]
    public void Evidence_hash_verifies_and_detects_tampering()
    {
        var row = PurchasePolicyAcceptance.Create(Guid.NewGuid(), PurchasePolicyAcceptance.ScopeShop, PurchasePolicyAcceptance.ChannelSiteOrder,
            Guid.NewGuid(), Guid.NewGuid(), "shop-abc", "shop-abc", "guest@example.test", null,
            new DateTime(2026, 10, 30, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567), "203.0.113.1", new string('x', 900));
        Assert.Equal(500, row.UserAgent!.Length);   // ตัดก่อน hash — ค่าที่เก็บ = ค่าที่ hash
        Assert.StartsWith(PdpaConsentEvidence.V2Prefix, row.EvidenceHash);
        Assert.True(PurchasePolicyAcceptance.Verify(row));
        row.Subject = "other@example.test";
        Assert.False(PurchasePolicyAcceptance.Verify(row));
    }

    [Fact]
    public void Platform_policy_info_points_to_public_pages()
    {
        var p = PurchasePolicyAcceptance.PlatformInfo("terms 1.0 · refund 1.0");
        Assert.Equal("/refund-policy.html", p.RefundUrl);
        Assert.Contains("ไม่คืนเงิน", p.Text);
    }

    // ═══════════════ ③ ด่านเปิดรับเงินจริง ═══════════════

    private static PaymentLiveGate.Facts F(bool tested = true, bool liveVerified = true, bool livePub = true, bool liveSecret = true)
        => new(tested ? DateTime.UtcNow : null, liveVerified ? DateTime.UtcNow : null, livePub, liveSecret);

    [Fact]
    public void Live_gate_requires_test_pass_live_keys_and_live_key_verification()
    {
        Assert.Null(PaymentLiveGate.Refusal(F()));
        Assert.True(PaymentLiveGate.CanEnableLive(F()));
        Assert.NotNull(PaymentLiveGate.Refusal(F(tested: false)));
        Assert.NotNull(PaymentLiveGate.Refusal(F(livePub: false)));
        Assert.NotNull(PaymentLiveGate.Refusal(F(liveSecret: false)));
        // บั๊กที่กัน: เดิมคีย์ live ไม่เคยถูกตรวจก่อนสลับ
        Assert.Contains("คีย์โหมดใช้งานจริงยังไม่ผ่าน", PaymentLiveGate.Refusal(F(liveVerified: false)));
    }

    [Fact]
    public void Platform_live_gate_adds_refund_policy_and_typed_confirmation()
    {
        Assert.Null(PaymentLiveGate.PlatformRefusal(F(), refundPolicyPublished: true, PaymentLiveGate.ConfirmPhrase));
        Assert.Contains("นโยบายการยกเลิกและคืนเงิน", PaymentLiveGate.PlatformRefusal(F(), refundPolicyPublished: false, PaymentLiveGate.ConfirmPhrase));
        Assert.NotNull(PaymentLiveGate.PlatformRefusal(F(), true, "ok"));
        Assert.NotNull(PaymentLiveGate.PlatformRefusal(F(), true, null));
        Assert.NotNull(PaymentLiveGate.PlatformRefusal(F(tested: false), true, PaymentLiveGate.ConfirmPhrase));
    }

    [Fact]
    public void Self_test_only_in_test_mode_and_other_kinds_untouched()
    {
        Assert.Null(PaymentLiveGate.SelfTestRefusal(PaymentSourceKind.GatewaySelfTest, PaymentProviderMode.Test));
        Assert.NotNull(PaymentLiveGate.SelfTestRefusal(PaymentSourceKind.GatewaySelfTest, PaymentProviderMode.Live));
        Assert.NotNull(PaymentLiveGate.SelfTestRefusal(PaymentSourceKind.GatewaySelfTest, null));
        Assert.Null(PaymentLiveGate.SelfTestRefusal(PaymentSourceKind.SiteOrder, PaymentProviderMode.Live));   // ทิศตรงข้าม
        Assert.Null(PaymentLiveGate.SelfTestRefusal(PaymentSourceKind.SiteOrder, null));
        Assert.False(PlatformRevenueSource.IsPlatformRevenue(PaymentSourceKind.GatewaySelfTest));
    }

    [Fact]
    public void Platform_pay_offline_unless_active_live_with_methods()
    {
        Assert.Null(PlatformPayAvailability.OfflineReason(true, PaymentProviderMode.Live, 1));
        Assert.NotNull(PlatformPayAvailability.OfflineReason(false, PaymentProviderMode.Live, 1));
        Assert.NotNull(PlatformPayAvailability.OfflineReason(true, PaymentProviderMode.Test, 1));   // โหมดทดสอบ ⇒ ผู้เช่าไม่เห็นปุ่มออนไลน์
        Assert.NotNull(PlatformPayAvailability.OfflineReason(true, PaymentProviderMode.Live, 0));
    }

    [Fact]
    public void Test_page_modes()
    {
        Assert.Equal(PlatformPayTestPage.AdminsOnly, PlatformPayTestPage.Normalize(null));
        Assert.Equal(PlatformPayTestPage.AdminsOnly, PlatformPayTestPage.Normalize("weird"));
        Assert.Equal(PlatformPayTestPage.Public, PlatformPayTestPage.Normalize("public"));
        Assert.NotNull(PlatformPayTestPage.AccessRefusal(PlatformPayTestPage.Off, true, true));
        Assert.NotNull(PlatformPayTestPage.AccessRefusal(PlatformPayTestPage.AdminsOnly, false, true));
        Assert.Null(PlatformPayTestPage.AccessRefusal(PlatformPayTestPage.AdminsOnly, true, true));
        Assert.Null(PlatformPayTestPage.AccessRefusal(PlatformPayTestPage.Public, false, true));
        Assert.NotNull(PlatformPayTestPage.AccessRefusal(PlatformPayTestPage.Public, false, false));   // ช่องทางอยู่โหมดจริง ⇒ ปิดเอง
    }

    // ═══════════════ ④ ยกเลิกแพ็กเกจ ═══════════════

    [Fact]
    public void Cancel_paid_active_plan_is_at_period_end_others_immediate()
    {
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(SubscriptionCancelPolicy.Outcome.AtPeriodEnd, SubscriptionCancelPolicy.Decide(SubscriptionStatus.Active, now.AddDays(10), now, false));
        Assert.Equal(SubscriptionCancelPolicy.Outcome.Immediate, SubscriptionCancelPolicy.Decide(SubscriptionStatus.Trial, now.AddDays(10), now, false));
        Assert.Equal(SubscriptionCancelPolicy.Outcome.Immediate, SubscriptionCancelPolicy.Decide(SubscriptionStatus.Active, now.AddDays(-1), now, false));
        Assert.Equal(SubscriptionCancelPolicy.Outcome.Immediate, SubscriptionCancelPolicy.Decide(SubscriptionStatus.Active, now.AddDays(10), now, true));
        Assert.Equal(SubscriptionCancelPolicy.Outcome.Immediate, SubscriptionCancelPolicy.Decide(SubscriptionStatus.PastDue, now.AddDays(10), now, false));
        Assert.True(SubscriptionCancelPolicy.DueForCancellation(true, SubscriptionStatus.Active, now, now));
        Assert.False(SubscriptionCancelPolicy.DueForCancellation(false, SubscriptionStatus.Active, now, now));
        Assert.False(SubscriptionCancelPolicy.DueForCancellation(true, SubscriptionStatus.Active, now.AddDays(1), now));
        Assert.Null(SubscriptionCancelPolicy.ResumeRefusal(true, SubscriptionStatus.Active, now.AddDays(1), now));
        Assert.NotNull(SubscriptionCancelPolicy.ResumeRefusal(false, SubscriptionStatus.Active, now.AddDays(1), now));
        Assert.NotNull(SubscriptionCancelPolicy.ResumeRefusal(true, SubscriptionStatus.Cancelled, now.AddDays(1), now));
        Assert.Contains("2569", SubscriptionCancelPolicy.Message(SubscriptionCancelPolicy.Outcome.AtPeriodEnd, now.AddDays(10)));   // พ.ศ.
    }
}
