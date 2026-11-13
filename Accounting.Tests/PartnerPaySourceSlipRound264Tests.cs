using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;
using Origin = Accounting.Helpers.PartnerPaySourceAccount.Origin;
using SlipKind = Accounting.Helpers.PartnerPaySourceAccount.SlipKind;

namespace Accounting.Tests;

/// <summary>
/// รอบ 264 แพ็ก P2 (คำตัดสิน 264-4-7) — ป้อนชั้น <c>SlipUnique</c> ของ <see cref="PartnerPaySourceAccount"/> (ฝั่งผู้ซื้อ) จากผู้โอนบนสลิปจริงที่ผู้ขายได้รับ ·
/// ส่งต่อเฉพาะบัญชีธนาคาร (ธนาคาร + เลขปิดบาง) · บัตร/บิล/ไม่รู้บทบาท ⇒ ไม่ส่ง · ผลจับคู่ ⇒ ชนิดของชั้น (ตัวแปลงเดียว <c>SlipKindOf</c>)
/// </summary>
public class PartnerPaySourceSlipRound264Tests
{
    private static readonly Guid BKb = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid BScb = Guid.Parse("11111111-0000-0000-0000-000000000002");

    private static readonly SlipAccountMatch.AccountKey[] Accounts =
    {
        new(BKb, "ธนาคารกสิกรไทย", "123-4-50621-8"),    // ตรงผู้โอน KBIZ xxx-x-x0621-x
        new(BScb, "ไทยพาณิชย์", "123-456189-5"),         // ตรงผู้โอน SCB xxx-xxx189-5 (ธนาคารจาก QR)
    };

    private static OcrSlipText.SlipParty? PayerOf(SlipCorpusRound264.Slip s)
        => PartnerPaymentNoticeService.SlipPayerForNotice(OcrSlipText.FromStoredJson(OcrSlipText.ToStoredJson(OcrSlipText.Parse(s.Text, 2026, s.Qr))));

    [Fact]
    public void ผู้โอนบนสลิปจริง_ส่งต่อเฉพาะบัญชีธนาคาร()
    {
        var kbiz = PayerOf(SlipCorpusRound264.KbizTransferMemo)!;
        Assert.Equal(("004", "xxx-x-x0621-x"), (kbiz.BankCode, kbiz.AccountMask));
        Assert.Null(kbiz.Name);                                         // ชื่อไม่ข้ามบริษัท
        Assert.Equal("014", PayerOf(SlipCorpusRound264.ScbBillStore)!.BankCode);   // ธนาคารจาก QR
        Assert.Null(PayerOf(SlipCorpusRound264.ScbBillRefs));            // mask ที่ OCR อ่านเพี้ยน ⇒ ไม่ส่ง
        Assert.Null(PayerOf(SlipCorpusRound264.KplusSameBank));          // ไม่รู้บทบาท
        Assert.Null(PayerOf(SlipCorpusRound264.TtbCardEnglish));
        Assert.Null(PartnerPaymentNoticeService.SlipPayerForNotice(null));
    }

    [Fact]
    public void ผลจับคู่_เป็นชนิดของชั้นสลิป()
    {
        Assert.Equal(new PartnerPaySourceAccount.Slip(SlipKind.Unique, BKb), PartnerPayablePipeline.SlipKindOf("004", "xxx-x-x0621-x", Accounts));
        Assert.Equal(new PartnerPaySourceAccount.Slip(SlipKind.Unique, BScb), PartnerPayablePipeline.SlipKindOf("014", "xxx-xxx189-5", Accounts));
        // ธนาคารไม่ตรง (เลขท้ายเหมือน SCB แต่บอกเป็นกรุงไทย) ⇒ ไม่ใช่ของเรา (กรรมการ/พนักงานจ่ายแทน — G5)
        Assert.Equal(SlipKind.NotOurs, PartnerPayablePipeline.SlipKindOf("006", "xxx-x-xx189-5", Accounts)!.Kind);
        // mask ที่ไม่ใช่รูปบัญชี ⇒ อ่านไม่ได้ (ไม่จับคู่)
        Assert.Equal(SlipKind.None, PartnerPayablePipeline.SlipKindOf("014", "xxx-xxx-0035", Accounts)!.Kind);
        Assert.Equal(SlipKind.Unreadable, PartnerPayablePipeline.SlipKindOf("004", "xxx-x-x06", Accounts)!.Kind);
        Assert.Null(PartnerPayablePipeline.SlipKindOf(null, "xxx-x-x0621-x", Accounts));
        Assert.Null(PartnerPayablePipeline.SlipKindOf("004", null, Accounts));
    }

    [Fact]
    public void ชั้นสลิปป้อนแล้ว_preselect_ไม่บันทึกเอง_และขัดบัญชีที่ตั้งไว้_ไม่อัตโนมัติ()
    {
        var usable = new Dictionary<Guid, string> { [BKb] = "กสิกรไทย ••0621", [BScb] = "SCB ••1895" };
        var slip = PartnerPayablePipeline.SlipKindOf("004", "xxx-x-x0621-x", Accounts);
        var d = PartnerPaySourceAccount.Decide(new(usable, SlipMatch: slip));
        // ฝ่ายค้าน 264-P2 M1: preselect เท่านั้น (ไม่บันทึกเอง) + เหตุตรง ๆ
        Assert.Equal((BKb, Origin.SlipUnique, false), (d.BankAccountId!.Value, d.Origin, d.AllowsAuto));
        Assert.StartsWith(PartnerPaySourceAccount.SlipUniqueNotAutoReason, d.Reason);
        Assert.False(d.UsedAi);
        var conflict = PartnerPaySourceAccount.Decide(new(usable, SlipMatch: slip, ConsentDefault: BScb));
        Assert.Null(conflict.BankAccountId);
        Assert.False(conflict.AllowsAuto);
        // ไม่มีผลอ่าน ⇒ เท่าก่อนรอบนี้ (ตัดสินจากชั้นที่มีจริง)
        Assert.Equal(Origin.ConsentDefault, PartnerPaySourceAccount.Decide(new(usable, SlipMatch: null, ConsentDefault: BScb)).Origin);
        // ผู้โอนไม่ใช่บัญชีของเรา ⇒ เปิดตัวเลือกกรรมการจ่ายแทนให้คนเลือก
        var notOurs = PartnerPaySourceAccount.Decide(new(usable, SlipMatch: PartnerPayablePipeline.SlipKindOf("006", "xxx-x-xx999-9", Accounts)));
        Assert.True(notOurs.OfferDirectorAdvance);
        Assert.Null(notOurs.BankAccountId);
    }
}
