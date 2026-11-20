namespace Accounting.Helpers;

/// <summary>
/// **สลิปนี้จ่ายหน่วยงานรัฐอะไร — ตัวตัดสินเดียว** (pure · รอบ 272 แพ็ก P1 · ดีไซน์ <c>erp-review/2026-11-16/team-272-tax-slip-autoremit.md</c> §2 ·
/// คำตัดสิน 272-1-1 = D-1) · <b>ไม่เรียก AI</b> (<see cref="UsedAi"/> = false · 272-1-10) · ตัวเลขล้วน ตรงตัว ไม่มี fuzzy (F2 ข้อ 3)
/// <para><b>ตาราง OWNER เดียว</b> <see cref="Billers"/> — เพิ่มแถว = แก้ตารางนี้ที่เดียว (เจ้าของส่งเอกสารจริงเพิ่ม) · แถวที่ยังไม่ยืนยันกับเอกสารจริงต้อง
/// <see cref="Biller.Auto"/> = false ⇒ ผล <see cref="Kind.UnknownGovernment"/> (คิวพนักงานพร้อมเหตุ) ไม่ใช่ "ไม่ใช่สลิปรัฐ"</para>
/// <list type="bullet">
/// <item>ต้องตรงทั้ง (ธนาคารผู้โอน · comp code · คำชื่อผู้รับ) — comp code เป็นรหัสภายในของแต่ละธนาคาร ไม่ใช่รหัสกลาง</item>
/// <item>ป้ายแบบบนชื่อผู้รับ ("Personal Income Tax"/"VAT") <b>ไม่ถูกอ่านเป็นแบบภาษี</b> — สลิปจริงของนิติบุคคลก็ขึ้นป้ายนี้ (§2.1)</item>
/// <item>Ref1 ของกรมสรรพากรต้องเป็นเลข 13 หลักที่ checksum ผ่าน · Control Code ต้องเป็นตัวเลขล้วนตามความยาวในแถวตาราง (ไม่ hard-code)</item>
/// </list>
/// </summary>
public static class GovPaymentSlip
{
    public const string RuleCode = "TAX-SLIP-AGENCY";
    public const string UnknownAgencyRuleCode = "TAX-SLIP-UNKNOWN-AGENCY";
    public const string LegalReference = "ป.รัษฎากร ม.52 / ม.69 ทวิ / ม.83 (ชำระภาษีที่ยื่นแบบ) · พ.ร.บ.ประกันสังคม ม.47";

    /// <summary>ตัวตัดสินนี้ไม่เรียก AI (กฎเหล็ก #1 ผ่านโดยโครงสร้าง · kill-switch ไม่มีผล)</summary>
    public static bool UsedAi => false;

    public enum Agency { RevenueDepartment = 1, SocialSecurityOffice = 2 }

    /// <summary>ความหมายของ Ref บนสลิปตามแถวตาราง</summary>
    public enum RefKind { None = 0, TaxId13 = 1, RdControlCode = 2, SsoEmployerAccount10 = 3 }

    public enum Kind { NotGovernment = 0, Identified = 1, UnknownGovernment = 2 }

    /// <param name="BankCode">ธนาคารผู้โอน (รหัส 3 หลัก <see cref="ThaiBankCodes"/>) · null = แถวที่ไม่รู้ธนาคาร (ไม่มีวันผ่าน)</param>
    /// <param name="CompCode">comp code บนสลิปของธนาคารนั้น · null = ยังไม่รู้ (ไม่มีวันผ่าน)</param>
    /// <param name="NameWords">คำที่ต้องพบในข้อความสลิป (อย่างน้อยหนึ่งคำ)</param>
    /// <param name="ControlCodeLength">ความยาว Control Code ที่ยืนยันจากสลิปจริง · null = ไม่ตรวจความยาว</param>
    /// <param name="TaxTypes">ชื่อแบบนำส่ง (<c>StatutoryRemittance.RemittanceType</c>) ที่จ่ายผ่านช่องทางนี้ได้</param>
    /// <param name="Auto">ยืนยันกับเอกสารจริงแล้ว ⇒ บันทึกเองได้ · false = ไม่รู้ ⇒ คิวพนักงาน</param>
    /// <param name="ConfirmedOn">วันที่ยืนยันกับเอกสารจริง (หลักฐาน · null = ยังไม่ยืนยัน)</param>
    public sealed record Biller(Agency Agency, string? BankCode, string? CompCode, IReadOnlyList<string> NameWords, RefKind Ref1, RefKind Ref2,
        int? ControlCodeLength, IReadOnlyList<string> TaxTypes, bool Auto, DateTime? ConfirmedOn, string Note);

    /// <summary>แบบที่ชำระกับกรมสรรพากร (ชุดเดียวกับ <c>StatutoryRemittanceService.Meta</c>)</summary>
    public static readonly IReadOnlyList<string> RdTaxTypes = new[] { "WhtPnd1", "WhtPnd2", "WhtPnd3", "WhtPnd53", "WhtPnd54", "VatPp30", "VatPp36", "Pnd51", "Pnd50" };

    /// <summary>ตารางผู้รับชำระของหน่วยงานรัฐ — <b>ที่เดียวในระบบ</b> (F2 ข้อ 4) · แถวที่ 1 = แถวเดียวที่ยืนยันแล้ว (สลิปจริง K BIZ ของเจ้าของ 2026-11-16 · D-1) ·
    /// แถวอื่นมีไว้ให้ "รู้ว่าเป็นสลิปรัฐแต่ยังไม่ยืนยัน" (Auto=false) — ห้ามพลิกเป็น true จนกว่าจะมีเอกสารจริง</summary>
    public static IReadOnlyList<Biller> Billers { get; } = new List<Biller>
    {
        new(Agency.RevenueDepartment, "004", "98037", new[] { "Revenue Department", "กรมสรรพากร" }, RefKind.TaxId13, RefKind.RdControlCode,
            ControlCodeLength: 15, RdTaxTypes, Auto: true, ConfirmedOn: new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc),
            "K BIZ Bill Payment · สลิปจริง 1 ใบ (ป้าย Comp Code · Personal ID/Tax ID No. · Control Code)"),
        new(Agency.SocialSecurityOffice, null, null, new[] { "สำนักงานประกันสังคม", "Social Security", "ประกันสังคม", "สมทบ" }, RefKind.SsoEmployerAccount10, RefKind.None,
            ControlCodeLength: null, new[] { "SsoSps110" }, Auto: false, ConfirmedOn: null,
            "ยังไม่ยืนยัน — รูปแบบ Ref1/Ref2/comp code ของ e-Payment สปส. ยังไม่มีเอกสารจริง (D-1)"),
    };

    /// <summary>คำที่บอกว่าเป็นการชำระหน่วยงานรัฐ (ใช้แยก "ไม่รู้จัก" ออกจาก "ไม่ใช่สลิปรัฐ")</summary>
    private const string WeakGovWord = "สมทบ";
    private static readonly string[] BillLabels = { "Biller", "Comp Code", "Comp. Code", "Bill Payment", "จ่ายบิล", "ชำระบิล", "ชําระบิล" };
    private static readonly string[] GovWords = { "Revenue Department", "กรมสรรพากร", "สรรพากร", "Revenue Dept", "สำนักงานประกันสังคม", "Social Security", "ประกันสังคม", "สมทบ", "e-Payment ภาษี" };

    /// <param name="TaxId">Ref1 = เลขผู้เสียภาษี 13 หลัก (checksum ผ่านแล้ว) — เฉพาะ Identified</param>
    /// <param name="ControlCode">Ref2 = Control Code (ตัวเลขล้วนตามความยาวในแถว) — เฉพาะ Identified · null = สลิปไม่มี/อ่านไม่ได้</param>
    public sealed record Result(Kind Kind, Biller? Row, string? TaxId, string? ControlCode, string RuleCode, string Reason)
    {
        public bool UsedAi => false;
        public bool IsGovernment => Kind != Kind.NotGovernment;
    }

    private static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>ธนาคารผู้โอนบนสลิป — บล็อก "จาก" ก่อน แล้ว QR แล้วชื่อธนาคารเดียวในข้อความ (ตัวอ่านเดียวกับ <see cref="SlipAccountMatch"/>)</summary>
    public static string? PayerBank(OcrSlipText.Reading reading, string? text)
        => reading.Payer?.BankCode ?? reading.Qr?.SendingBankCode ?? ThaiBankCodes.FromText(text);

    /// <summary>ตัดสิน (pure) — <paramref name="text"/> = ข้อความ OCR ดิบของสลิปเดียวกับ <paramref name="reading"/></summary>
    public static Result Identify(OcrSlipText.Reading reading, string? text = null)
    {
        var t = text ?? reading.Text ?? "";
        var bill = reading.Bill;
        var bank = PayerBank(reading, t);
        // รอบ 275 แพ็ก D (L8): "สมทบ" คำเดียวพบในบันทึกช่วยจำของการโอนทั่วไปได้ ("เงินสมทบงานบุญ") ⇒ นับเป็นคำของรัฐเฉพาะเมื่ออยู่คู่ป้ายจ่ายบิล (Comp Code/Biller)
        var anyGovWord = GovWords.Where(w => w != WeakGovWord).Any(w => Has(t, w))
            || (Has(t, WeakGovWord) && (bill?.CompCode != null || BillLabels.Any(l => Has(t, l))));
        if (bill?.CompCode is string comp)
        {
            var byComp = Billers.Where(b => b.CompCode == comp).ToList();
            var exact = byComp.FirstOrDefault(b => b.BankCode == bank && b.NameWords.Any(w => Has(t, w)));
            if (exact == null)
                return byComp.Count > 0 || anyGovWord
                    ? Unknown(byComp.Count > 0
                        ? $"comp code {comp} อยู่ในตาราง แต่ธนาคารผู้โอน/ชื่อผู้รับบนสลิปไม่ตรงแถวที่ยืนยันแล้ว (comp code เป็นรหัสภายในของแต่ละธนาคาร)"
                        : $"comp code {comp} ยังไม่อยู่ในตารางที่ยืนยันแล้ว")
                    : new Result(Kind.NotGovernment, null, null, null, RuleCode, "ไม่ใช่สลิปชำระหน่วยงานรัฐที่รู้จัก");
            if (!exact.Auto) return Unknown("รูปแบบสลิปของ " + AgencyLabel(exact.Agency) + " ยังไม่ยืนยันกับเอกสารจริง");
            string? taxId = null, control = null;
            if (exact.Ref1 == RefKind.TaxId13)
            {
                if (bill.Ref1 == null || !ThaiTaxId.IsWellFormed(bill.Ref1) || !ThaiTaxId.HasValidChecksum(bill.Ref1))
                    return Unknown("อ่านเลขผู้เสียภาษีบนสลิปไม่ชัด (ต้องเป็นเลข 13 หลักที่ถูกต้อง)");
                taxId = ThaiTaxId.Normalize(bill.Ref1);
            }
            if (exact.Ref2 == RefKind.RdControlCode && bill.Ref2 is string r2)
            {
                if (!r2.All(char.IsAsciiDigit) || (exact.ControlCodeLength is int len && r2.Length != len))
                    return Unknown("อ่าน Control Code บนสลิปไม่ชัด (ต้องเป็นตัวเลขล้วน" + (exact.ControlCodeLength is int l2 ? $" {l2} หลัก" : "") + ")");
                control = r2;
            }
            return new Result(Kind.Identified, exact, taxId, control, RuleCode, "สลิปชำระ" + AgencyLabel(exact.Agency) + " (comp code " + comp + ")");
        }
        return anyGovWord
            ? Unknown("สลิปนี้ดูเป็นการชำระหน่วยงานรัฐ แต่ไม่พบรหัสผู้รับชำระ (Comp Code) ที่อยู่ในตารางที่ยืนยันแล้ว")
            : new Result(Kind.NotGovernment, null, null, null, RuleCode, "ไม่ใช่สลิปชำระหน่วยงานรัฐที่รู้จัก");
    }

    /// <summary>รอบ 275 แพ็ก D (ฝ่ายค้าน MED-1): หน่วยงานที่ข้อความบนสลิปบ่งบอก — ใช้กับสลิปที่ระบุแถวผู้รับชำระไม่ได้ (เช่น สปส. ยังไม่มีแถว Auto)
    /// เพื่อกันพนักงานลงสลิปหน่วยงานหนึ่งเป็นแบบของอีกหน่วยงาน · คำชื่อของแถวในตาราง (คำอ่อน "สมทบ" ต้องคู่ป้ายจ่ายบิล · L8) · ตรงหลายหน่วยงาน/ไม่ตรงเลย ⇒ null (ไม่รู้)</summary>
    public static Agency? AgencyHint(OcrSlipText.Reading reading, string? text = null)
    {
        var t = text ?? reading.Text ?? "";
        var billLabel = reading.Bill?.CompCode != null || BillLabels.Any(l => Has(t, l));
        var hits = Billers.Where(b => b.NameWords.Any(w => w == WeakGovWord ? billLabel && Has(t, w) : Has(t, w))).Select(b => b.Agency).Distinct().ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    private static Result Unknown(string reason) => new(Kind.UnknownGovernment, null, null, null, UnknownAgencyRuleCode, reason);

    private static string AgencyLabel(Agency a) => a switch
    {
        Agency.RevenueDepartment => "กรมสรรพากร",
        Agency.SocialSecurityOffice => "สำนักงานประกันสังคม",
        _ => "หน่วยงานรัฐ",
    };

    /// <summary>แบบนำส่งนี้จ่ายผ่านแถวตารางนี้ได้ไหม (ด่าน "หน่วยงานตรง comp code" ของ <c>TaxSlipMatch</c>)</summary>
    public static bool Covers(Biller row, string remittanceType) => row.TaxTypes.Contains(remittanceType);
}
