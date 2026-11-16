namespace Accounting.Helpers;

/// <summary>
/// **บัญชีธนาคารฝั่ง Cr ของการนำส่งจากสลิปภาษี — ตัวตัดสินเดียว** (pure · รอบ 272 แพ็ก P4 · ดีไซน์ §4.2 · คำตัดสิน 272-1-5 (D-5) · 272-1-13) ·
/// ลำดับชั้นแบบ <c>PartnerPaySourceAccount.Precedence</c> แต่<b>เฉพาะเส้นนี้</b>: ชั้น 1 (บล็อก "จาก" บนสลิปตรงบัญชีเดียวของบริษัทลูกค้า) บันทึกเองได้ —
/// ข้อยกเว้นที่ตั้งใจต่อ 264-4-9 ตามคำตัดสินเจ้าของ 272-0-1 (หลักฐาน "จ่ายแล้ว" มาจาก Control Code/เลขภาษี/ยอดทุกสตางค์) ·
/// <c>PartnerPaySourceAccount.Precedence</c> แถว SlipUnique <b>ไม่ถูกแตะ</b> (ยัง AllowsAuto=false สำหรับเส้นคู่ค้า)
/// <list type="bullet">
/// <item>ชั้น 2: บัญชีชำระภาษีที่ลูกค้าระบุในความยินยอม + ธนาคารบนสลิปตรงธนาคารของบัญชีนั้น + เลขที่เห็นไม่ขัด</item>
/// <item>ชั้น 3: ตัดสินไม่ได้ (บัตร · อ่านไม่ได้ · หลายบัญชี) ⇒ <see cref="Kind.NeedsAccount"/> — รอพนักงาน 1 คลิก <b>ไม่ลงบัญชีพักเอง</b> (D-5)</item>
/// <item>ชั้น 4: เลขชัดแต่ไม่ใช่บัญชีของบริษัท ⇒ <see cref="Kind.NotOurs"/> — คิวพนักงาน (ทางไปต่อ: เงินทดรองกรรมการ)</item>
/// </list>
/// ห้ามคืนบัญชีเมื่อไม่มีหลักฐาน — <c>RemitAsync</c> ที่ได้ <c>BankAccountId=null</c> จะถอยไปบัญชีตั้งต้นของบริษัท (ค่าที่ระบบแต่งขึ้น · F2 ข้อ 3)
/// </summary>
public static class TaxSlipBankAccount
{
    public const string RuleCode = "TAX-SLIP-BANK";

    public static bool UsedAi => false;

    public enum Kind { FromSlip = 1, FromConsent = 2, NeedsAccount = 3, NotOurs = 4 }

    /// <param name="AccountId">มีค่าเฉพาะ <see cref="Kind.FromSlip"/>/<see cref="Kind.FromConsent"/></param>
    /// <param name="Preselect">บัญชีที่พนักงานน่าจะเลือก (หน้าคิว preselect) · ไม่ใช่คำตัดสิน</param>
    public sealed record Result(Kind Kind, Guid? AccountId, IReadOnlyList<Guid> Preselect, string Reason, string? Label)
    {
        public bool CanAuto => Kind is Kind.FromSlip or Kind.FromConsent && AccountId != null;
        public bool UsedAi => false;
    }

    /// <param name="match">ผล <see cref="SlipAccountMatch.Match"/> ของบล็อกผู้โอนกับบัญชีธนาคารของ<b>บริษัทลูกค้า</b></param>
    /// <param name="payer">บล็อกผู้โอนบนสลิป (ชนิด · ธนาคาร)</param>
    /// <param name="consentAccountId">บัญชีชำระภาษีหลักที่ลูกค้าระบุในความยินยอม · null = ไม่ระบุ</param>
    /// <param name="consentAccountBankCode">ธนาคารของบัญชีนั้น (<see cref="ThaiBankCodes.FromText"/> ของช่องธนาคาร) · null = ไม่รู้ ⇒ ใช้ชั้น 2 ไม่ได้</param>
    public static Result Decide(SlipAccountMatch.Result match, OcrSlipText.SlipParty? payer, Guid? consentAccountId, string? consentAccountBankCode)
    {
        if (match.Kind == SlipAccountMatch.Kind.Unique && match.AccountId is Guid id)
            return new Result(Kind.FromSlip, id, new[] { id }, "บัญชีที่จ่ายจากบล็อก “จาก” บนสลิปตรงบัญชีเดียวของบริษัท", match.PartyLabel);
        if (match.Kind == SlipAccountMatch.Kind.NotOurs)
            return new Result(Kind.NotOurs, null, consentAccountId is Guid c0 ? new[] { c0 } : Array.Empty<Guid>(),
                "เลขบัญชีบนสลิปไม่ใช่บัญชีของบริษัท (อาจจ่ายจากบัญชีส่วนตัวกรรมการ) — ให้พนักงานเลือกผังที่จ่าย", match.PartyLabel);
        var isCard = payer?.Kind == OcrSlipText.PartyKind.Card;
        if (!isCard && consentAccountId is Guid ca && consentAccountBankCode != null && payer?.BankCode == consentAccountBankCode
            && (match.Kind != SlipAccountMatch.Kind.Many || match.Candidates.Contains(ca)))
            return new Result(Kind.FromConsent, ca, new[] { ca }, "บัญชีชำระภาษีที่บริษัทระบุไว้ในความยินยอม · ธนาคารบนสลิปตรง", match.PartyLabel);
        var pre = match.Candidates.Count > 0 ? match.Candidates : consentAccountId is Guid c1 ? new[] { c1 } : Array.Empty<Guid>();
        return new Result(Kind.NeedsAccount, null, pre,
            isCard ? "จ่ายด้วยบัตร — ไม่มีบัญชีธนาคารต้นทางบนสลิป ให้พนักงานเลือกผังที่จ่าย"
                : "ระบบระบุบัญชีที่จ่ายจากสลิปไม่ได้ (" + match.Reason + ") — ให้พนักงานเลือกบัญชี", match.PartyLabel);
    }
}
