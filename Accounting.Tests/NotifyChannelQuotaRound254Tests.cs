using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 254 WP-11 L4 (คำตอบเจ้าของ Q3/Q4 · AUDIT_249 §3.3 N7/N9) — ตัวตัดสินโควตาแยกช่อง LINE / อีเมล (pure) ·
/// ทั้งสองทิศ: ช่องที่ตั้งเพดานแยก ⇒ แถวของช่อง · ไม่ตั้ง ⇒ ก้อนรวมเดิม (ลูกค้าเดิมไม่สะดุด) · อีเมลเอกสารที่เพิ่งเริ่มนับไม่กินก้อนรวม
/// </summary>
public class NotifyChannelQuotaRound254Tests
{
    [Fact]
    public void เพดานแยกช่อง_ไม่ตั้งทั้งบริษัทและแพ็กเกจ_คือไม่แยก()
    {
        Assert.Null(NotifyQuotaPolicy.ChannelPlanLimit(null, null, null, isTrial: false));
        Assert.Null(NotifyQuotaPolicy.ChannelPlanLimit(null, null, null, isTrial: true));
        // ทดลองใช้: ค่าทดลองว่าง ⇒ ใช้ค่าแพ็กเกจ (ไม่ใช่ "ไม่แยก")
        Assert.Equal(300, NotifyQuotaPolicy.ChannelPlanLimit(null, 300, null, isTrial: true));
        Assert.Equal(100, NotifyQuotaPolicy.ChannelPlanLimit(null, 300, 100, isTrial: true));
        Assert.Equal(300, NotifyQuotaPolicy.ChannelPlanLimit(null, 300, 100, isTrial: false));
        // ค่าเฉพาะบริษัทชนะ · -1 = แยกแต่ไม่จำกัด (N9: ทับรายบริษัท)
        Assert.Equal(5000, NotifyQuotaPolicy.ChannelPlanLimit(5000, 300, null, isTrial: false));
        Assert.Equal(NotifyQuotaPolicy.Unlimited, NotifyQuotaPolicy.ChannelPlanLimit(-1, null, null, isTrial: false));
        // แม่แบบตั้งแค่ค่าทดลอง ⇒ บริษัทที่ไม่ใช่ทดลองไม่แยก
        Assert.Null(NotifyQuotaPolicy.ChannelPlanLimit(null, null, 100, isTrial: false));
    }

    [Theory]
    [InlineData("all", null, true, "all")]
    [InlineData("line", 300, true, "line")]            // ตั้งเพดาน LINE ⇒ แถวของ LINE
    [InlineData("line", null, true, "all")]            // ไม่ตั้ง ⇒ ก้อนรวมเดิม (พฤติกรรมเดิมของข้อความแจ้งลูกค้า)
    [InlineData("email", null, false, "email")]        // อีเมลเอกสาร (เส้นใหม่) ⇒ ห้ามกินก้อนรวม — แถวอีเมลแบบไม่จำกัด
    [InlineData("email", 1500, false, "email")]
    [InlineData("email", null, true, "all")]
    public void แถวตัวนับ(string channel, int? limit, bool combinedFallback, string expected)
        => Assert.Equal(expected, NotifyQuotaPolicy.CounterChannel(channel, limit, combinedFallback));

    [Fact]
    public void ชื่อช่อง_ค่าแปลก_เป็นก้อนรวม_และขั้นส่งแปลงถูก()
    {
        Assert.Equal(NotifyQuotaPolicy.ChannelAll, NotifyQuotaPolicy.NormalizeChannel(null));
        Assert.Equal(NotifyQuotaPolicy.ChannelAll, NotifyQuotaPolicy.NormalizeChannel("sms"));
        Assert.Equal(NotifyQuotaPolicy.ChannelLine, NotifyQuotaPolicy.ChannelOfStep(CustomerNotifyChannel.ChannelLine));
        Assert.Equal(NotifyQuotaPolicy.ChannelEmail, NotifyQuotaPolicy.ChannelOfStep(CustomerNotifyChannel.ChannelEmail));
        Assert.Equal(NotifyQuotaPolicy.ChannelAll, NotifyQuotaPolicy.ChannelOfStep(null));
    }

    [Fact]
    public void ลำดับโบนัส_แพ็กของช่องก่อน_แล้วแพ็กรวมเดิม()
    {
        Assert.Equal(new[] { "line", "all" }, NotifyQuotaPolicy.BonusOrder("line"));
        Assert.Equal(new[] { "email", "all" }, NotifyQuotaPolicy.BonusOrder("email"));
        Assert.Equal(new[] { "all" }, NotifyQuotaPolicy.BonusOrder("all"));   // ก้อนรวมไม่ใช้แพ็กของช่อง
    }

    [Fact]
    public void แพ็กของช่อง_ค่าตั้งต้น_N7_และค่าที่แอดมินตั้งชนะ()
    {
        Assert.Equal(new NotifyTopUpPack(1000, 60), NotifyQuotaPolicy.TopUpPackFor("line", null, null));
        Assert.Equal(new NotifyTopUpPack(5000, 60), NotifyQuotaPolicy.TopUpPackFor("email", null, null));
        Assert.Equal(new NotifyTopUpPack(AddOnCodes.MessagesPerNotifyTopUpPack, 60), NotifyQuotaPolicy.TopUpPackFor("all", null, null));
        Assert.Equal(new NotifyTopUpPack(2000, 30), NotifyQuotaPolicy.TopUpPackFor("email", 2000, 30));   // ผู้ให้บริการแพง ⇒ 2,000/฿190 (N7) โดยไม่ deploy
        Assert.Equal(new NotifyTopUpPack(1000, 60), NotifyQuotaPolicy.TopUpPackFor("line", 0, 9999));      // นอกช่วง ⇒ ค่าตั้งต้นของช่อง
    }

    [Fact]
    public void รหัสแพ็ก_ไปกลับได้_และไม่ใช่แพ็กข้อความ_คือ_null()
    {
        foreach (var ch in new[] { "all", "line", "email" })
            Assert.Equal(ch, NotifyQuotaPolicy.ChannelOfTopUpCode(NotifyQuotaPolicy.TopUpCodeFor(ch)));
        Assert.Equal(AddOnCodes.NotifyTopUpLine, NotifyQuotaPolicy.TopUpCodeFor("line"));
        Assert.Equal(AddOnCodes.NotifyTopUpEmail, NotifyQuotaPolicy.TopUpCodeFor("email"));
        Assert.Null(NotifyQuotaPolicy.ChannelOfTopUpCode(AddOnCodes.DocumentsTopUp));
        Assert.Contains(AddOnCodes.NotifyTopUpLine, AddOnCodes.SystemMeters);    // มิเตอร์ระบบ — ไม่โผล่เป็นสวิตช์ลูกค้า · แอดมินแก้ราคาได้ (IsKnown)
        Assert.Contains(AddOnCodes.NotifyTopUpEmail, AddOnCodes.SystemMeters);
        Assert.True(AddOnCodes.IsKnown(AddOnCodes.NotifyTopUpEmail));
    }

    [Fact]
    public void หน่วยบนใบแจ้งหนี้_บอกช่อง_และก้อนรวมคงรูปเดิม()
    {
        Assert.Equal("แพ็ก 1,000 ข้อความ LINE", NotifyQuotaPolicy.PackUnitLabelFor("line", 1000));
        Assert.Equal("แพ็ก 5,000 อีเมล", NotifyQuotaPolicy.PackUnitLabelFor("email", 5000));
        Assert.Equal(NotifyQuotaPolicy.PackUnitLabel(500), NotifyQuotaPolicy.PackUnitLabelFor("all", 500));
    }

    [Fact]
    public void ค่าแนะนำต่อช่อง_NOTIFY_ข้อ5()
    {
        Assert.Equal(300, NotifyQuotaPolicy.SuggestedChannelLimit(SubscriptionPlan.Basic, "line"));
        Assert.Equal(5000, NotifyQuotaPolicy.SuggestedChannelLimit(SubscriptionPlan.Enterprise, "line"));
        Assert.Equal(1500, NotifyQuotaPolicy.SuggestedChannelLimit(SubscriptionPlan.Basic, "email"));
        Assert.Equal(6000, NotifyQuotaPolicy.SuggestedChannelLimit(SubscriptionPlan.Pro, "email"));
        Assert.Equal(NotifyQuotaPolicy.Unlimited, NotifyQuotaPolicy.SuggestedChannelLimit(SubscriptionPlan.Enterprise, "email"));
        Assert.Equal(100, NotifyQuotaPolicy.SuggestedTrialChannelLimit("line"));
        Assert.Equal(300, NotifyQuotaPolicy.SuggestedTrialChannelLimit("email"));
    }

    [Fact]
    public void อีเมลเต็มรายเดือน_บอกทางไปต่อ()
    {
        var reason = NotifyQuotaPolicy.EmailMonthlyFullReason(1500);
        Assert.Contains("1,500", reason);
        Assert.Contains("ซื้อแพ็กอีเมล", reason);
        Assert.Contains("ผู้ส่งอีเมลของบริษัทเอง", reason);
    }

    // ───────── OA กลาง: เฝ้าต้นทุน push/reply (N8) ─────────

    [Fact]
    public void โควตา_OA_กลาง_อ่าน_JSON_ของ_LINE_ได้_และอ่านไม่ได้คือ_null()
    {
        Assert.Equal(5000, NextAccOaQuotaRule.ParseQuota("{\"type\":\"limited\",\"value\":5000}"));
        Assert.Null(NextAccOaQuotaRule.ParseQuota("{\"type\":\"none\"}"));          // ไม่จำกัด
        Assert.Null(NextAccOaQuotaRule.ParseQuota("not json"));
        Assert.Null(NextAccOaQuotaRule.ParseQuota(null));
        Assert.Equal(4200, NextAccOaQuotaRule.ParseConsumption("{\"totalUsage\":4200}"));
        Assert.Null(NextAccOaQuotaRule.ParseConsumption("{\"total\":1}"));
        Assert.Null(NextAccOaQuotaRule.ParseConsumption("[1]"));
    }

    [Theory]
    [InlineData(4499L, 5000L, false)]
    [InlineData(4500L, 5000L, true)]       // 90%
    [InlineData(99999L, null, false)]      // ไม่จำกัด/ไม่รู้ ⇒ ไม่เตือน
    [InlineData(10L, 0L, false)]
    public void เกณฑ์เตือน_90เปอร์เซ็นต์(long used, long? limit, bool expected)
        => Assert.Equal(expected, NextAccOaQuotaRule.Crossed(used, limit));

    [Fact]
    public void ข้อความเตือนผู้ดูแล_บอกว่าไม่ตัดโควตาผู้เช่า()
    {
        Assert.Contains("4,500/5,000", NextAccOaQuotaRule.PushAlert("2027-02", 4500, 5000));
        var reply = NextAccOaQuotaRule.ReplyAlert("2027-02", 22500, NextAccOaQuotaRule.AssumedFreeRepliesPerMonth);
        Assert.Contains("22,500", reply);
        Assert.Contains("ไม่ตัดโควตาผู้เช่า", reply);
        Assert.Contains("ยังไม่ยืนยันกับ LINE", reply);
    }

    // ───────── ฝ่ายค้าน fix2 ─────────

    [Fact]
    public void M1_อีเมลแพลตฟอร์ม_ไม่ใช้แพ็กรวม_ข้อความแจ้งลูกค้ายังใช้ได้()
    {
        Assert.Equal(new[] { "email" }, NotifyQuotaPolicy.BonusOrder("email", combinedFallback: false));
        Assert.Equal(new[] { "email", "all" }, NotifyQuotaPolicy.BonusOrder("email", combinedFallback: true));   // ทิศตรงข้าม
        Assert.Equal(new[] { "all" }, NotifyQuotaPolicy.BonusOrder("all", combinedFallback: false));
    }

    [Fact]
    public void M2_ส่วนเกิน_ไม่ใช้กับอีเมลแพลตฟอร์ม()
    {
        Assert.False(NotifyQuotaPolicy.OverageAllowed(combinedFallback: false));
        Assert.True(NotifyQuotaPolicy.OverageAllowed(combinedFallback: true));
    }

    [Fact]
    public void H1_แพ็กของช่อง_ขายได้เฉพาะช่องที่แยกเพดาน()
    {
        Assert.False(NotifyQuotaPolicy.ChannelPackSellable(null));
        Assert.True(NotifyQuotaPolicy.ChannelPackSellable(300));
        // รอบ 257 ชุด 4 (C-M2): ไม่จำกัด ⇒ แพ็กไม่มีผล ⇒ ขายไม่ได้ (เดิม true) · 0 = เพดานจริง ขายได้
        Assert.False(NotifyQuotaPolicy.ChannelPackSellable(NotifyQuotaPolicy.Unlimited));
        Assert.True(NotifyQuotaPolicy.ChannelPackSellable(0));
        var msg = NotifyQuotaPolicy.ChannelPackNotSellableMessage("line");
        Assert.Contains("ก้อนรวม", msg);
        Assert.Contains("ยังไม่ได้คิดเงิน", msg);
    }

    [Theory]
    [InlineData(null, true, "all")]
    [InlineData("", true, "all")]
    [InlineData("LINE", true, "line")]
    [InlineData(" email ", true, "email")]
    [InlineData("all", true, "all")]
    [InlineData("sms", false, "")]
    [InlineData("lne", false, "")]
    public void L5_ช่องที่ไม่รู้จัก_ไม่เดาเป็นก้อนรวม(string? input, bool ok, string expected)
    {
        Assert.Equal(ok, NotifyQuotaPolicy.TryParseChannel(input, out var ch));
        Assert.Equal(expected, ch);
    }

    [Fact]
    public void M3_ยกเลิกปลายทาง_ล้างป้ายจาก_LINE_และชื่อกลุ่ม_แต่ไม่แตะป้ายที่ผู้ดูแลพิมพ์()
    {
        var now = new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);
        var invite = new Accounting.Models.Entities.ContactNotifyEndpoint
        {
            Kind = NotifyEndpointKind.LineUser, Source = ContactLineBindRule.SourceLineInvite, Label = "LINE: สมชาย", LineUserId = "enc", LineIdHash = "h",
        };
        NotifyEndpointKind.MarkUnlinked(invite, ContactLineBindRule.UnlinkUser, now);
        Assert.Null(invite.Label);
        Assert.Null(invite.LineUserId);
        Assert.Equal("h", invite.LineIdHash);                 // คงแฮชเป็นร่องรอย
        Assert.Equal(now, invite.UnlinkedAt);

        var group = new Accounting.Models.Entities.ContactNotifyEndpoint
        {
            Kind = NotifyEndpointKind.LineGroup, Source = LineGroupBindRule.SourceGroupCode, GroupNameSnapshot = "ABC × ร้านเรา", LineGroupId = "enc",
        };
        NotifyEndpointKind.MarkUnlinked(group, LineGroupBindRule.UnlinkBotRemoved, now);
        Assert.Null(group.GroupNameSnapshot);
        Assert.Null(group.LineGroupId);

        var manual = new Accounting.Models.Entities.ContactNotifyEndpoint
        {
            Kind = NotifyEndpointKind.Email, Source = NotifyEndpointKind.SourceManual, Label = "ฝ่ายบัญชี", Email = "ap@x.co",
            UnlinkedAt = now.AddDays(-1), UnlinkReason = NotifyEndpointKind.UnlinkCompany,
        };
        NotifyEndpointKind.MarkUnlinked(manual, NotifyEndpointKind.UnlinkMerge, now);
        Assert.Equal("ฝ่ายบัญชี", manual.Label);              // ทิศตรงข้าม: ป้ายที่ผู้ดูแลพิมพ์เองไม่ถูกแตะ
        Assert.Equal(now.AddDays(-1), manual.UnlinkedAt);     // ประทับครั้งแรกคงไว้
        Assert.Equal(NotifyEndpointKind.UnlinkCompany, manual.UnlinkReason);
    }
}
