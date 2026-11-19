namespace Accounting.Helpers;

/// <summary>
/// **จับสลิปชำระภาษีกับงวดที่รอชำระของบริษัทลูกค้า — ตัวตัดสินเดียว** (pure · รอบ 272 แพ็ก P4 · ดีไซน์ §3.3 · คำตัดสิน 272-1-2 (D-2) · 272-1-3 (D-3)) ·
/// ทุกด่านเป็นเลขล้วน ตรงตัว (ไม่มี fuzzy · F2 ข้อ 3) · <b>ไม่เรียก AI</b> (<see cref="UsedAi"/>)
/// <list type="number">
/// <item>สลิปต้องระบุหน่วยงานได้ (<see cref="GovPaymentSlip.Kind.Identified"/>) — มิฉะนั้น <see cref="Outcome.UnknownAgency"/></item>
/// <item>บริษัทเลือกจาก<b>ชุดบริษัทที่กลุ่ม/อีเมลนี้ผูกเท่านั้น</b> ด้วยเลขภาษี 13 หลักตรงตัว — ห้ามค้นทั้งฐาน (กฎ M · ใครก็ส่งเลขภาษีบริษัทอื่นมาได้)</item>
/// <item><b>ทางหลัก (D-2):</b> ใบขอชำระที่ Control Code (แฮช) ตรง ⇒ ตัวตนของแบบ/งวด · ตรวจยอด = ภาษี + เงินเพิ่ม + เบี้ยปรับ บนใบทุกสตางค์ · ใบยังเปิด ·
/// สมุดตรงตอนส่ง · ยังไม่หมดอายุ · ไม่ใช่ยื่นเพิ่มเติม · ยอดค้างในสมุดยังเท่าใบ</item>
/// <item><b>ทางสำรอง (D-3 · เปลี่ยนจากคำแนะนำ — เจ้าของต้องการให้งานจบเอง):</b> ไม่มีใบขอชำระ ⇒ บันทึกเองได้เฉพาะเมื่อ<b>เหลือรายการค้างตัวเดียว</b>ที่ผ่านทุกด่าน:
/// หน่วยงานตรง comp code · เลขภาษีตรง · ยอด = ยอดค้างทุกสตางค์ · วันที่บนสลิปอยู่ในช่วงยื่นของงวด · ไม่ซ้ำ · แบบไม่กำกวม
/// (ยอดเดียวกันตรงหลายแบบ เช่น ภ.ง.ด.3 กับ ภ.ง.ด.53 เดือนเดียวกัน ⇒ <see cref="Outcome.Many"/> = ถามในกลุ่ม)</item>
/// </list>
/// ค่าธรรมเนียมธนาคาร ≠ 0 ⇒ คิวพนักงาน (ไม่อยู่ในยอดนำส่ง · ไม่มีช่องใน RemitRequest — backlog)
/// </summary>
public static class TaxSlipMatch
{
    public const string LegalReference = GovPaymentSlip.LegalReference;

    public static bool UsedAi => false;

    public enum Outcome
    {
        One = 1, Many, None, AmountMismatch, TaxIdNotInGroup, NoTenant, Expired, Duplicate, BooksMismatch, Amendment,
        FeeNotZero, OutOfWindow, AlreadyRemitted, RequestClosed, BooksChanged, UnknownAgency, LateWithoutRequest, RequestCcMismatch,
    }

    public static class Codes
    {
        public const string One = "TAX-SLIP-ONE";
        public const string OneNoControlCode = "TAX-SLIP-ONE-NOCC";
        public const string Many = "TAX-SLIP-MANY";
        public const string None = "TAX-SLIP-NONE";
        public const string Amount = "TAX-SLIP-AMOUNT";
        public const string TaxId = "TAX-SLIP-TAXID";
        public const string NoTenant = "TAX-SLIP-NO-TENANT";
        public const string Expired = "TAX-SLIP-EXPIRED";
        public const string Duplicate = "TAX-SLIP-DUP";
        public const string BooksMismatch = "TAX-SLIP-BOOKS";
        public const string Amendment = "TAX-SLIP-AMENDMENT";
        public const string Fee = "TAX-SLIP-FEE";
        public const string Window = "TAX-SLIP-WINDOW";
        public const string Remitted = "TAX-SLIP-REMITTED";
        public const string RequestClosed = "TAX-SLIP-REQ-CLOSED";
        public const string BooksChanged = "TAX-SLIP-BOOKS-CHANGED";
        public const string UnknownAgency = GovPaymentSlip.UnknownAgencyRuleCode;
        public const string Late = "TAX-SLIP-LATE-NOREQ";
        public const string ControlCode = "TAX-SLIP-CC";
        public const string RequestCcMismatch = "TAX-SLIP-REQ-CC";
    }

    /// <summary>สลิปที่ระบุหน่วยงานแล้ว · <paramref name="ControlCodeHash"/> = แฮช Control Code ด้วย <c>TaxSlipRefHash</c> (null = สลิปไม่มี) ·
    /// <paramref name="SlipDate"/> = วันที่บนสลิป (ปฏิทินไทย — ส่วนวันที่เท่านั้น)</summary>
    public sealed record Slip(GovPaymentSlip.Result Agency, decimal Amount, decimal? Fee, DateTime SlipDate, string? ControlCodeHash);

    public sealed record CompanyKey(Guid Id, string? TaxId);

    /// <param name="Status">ชื่อสถานะของใบขอชำระ (<c>TaxPaymentRequestStatus</c>: Open/Paid/Cancelled/Expired)</param>
    public sealed record RequestKey(Guid Id, Guid CompanyId, string Type, int Year, int Month, decimal Amount, decimal Surcharge, decimal Penalty,
        string ControlCodeHash, string Status, bool BooksMismatch, DateTime? ExpiresAt, bool IsAmendment)
    {
        public decimal Total => Amount + Surcharge + Penalty;
    }

    /// <summary>รายการค้างจาก dashboard นำส่ง (source of truth เดียว — ห้ามคำนวณยอดค้างเอง) · <paramref name="WindowFrom"/> = วันแรกที่จ่ายได้ (null = ไม่จำกัดล่าง) ·
    /// <paramref name="DueDate"/> = วันสุดท้ายของช่วงยื่นตามกำหนด</summary>
    public sealed record PendingKey(Guid CompanyId, string Type, int Year, int Month, decimal Amount, DateTime? WindowFrom, DateTime DueDate);

    public sealed record Candidate(Guid CompanyId, string Type, int Year, int Month, decimal Amount, decimal Surcharge, decimal Penalty, Guid? RequestId);

    /// <param name="Auto">ผ่านทุกด่านของตัวตัดสินนี้ (ยังต้องผ่านความยินยอม + บัญชีธนาคาร + ด่านของ RemitAsync)</param>
    public sealed record Decision(Outcome Outcome, Guid? CompanyId, Candidate? Chosen, IReadOnlyList<Candidate> Candidates, bool Auto, string RuleCode, string Reason)
    {
        public bool UsedAi => false;
        public string LegalReference => TaxSlipMatch.LegalReference;
    }

    private static Decision D(Outcome o, string code, string reason, Guid? company = null, IReadOnlyList<Candidate>? c = null)
        => new(o, company, null, c ?? Array.Empty<Candidate>(), false, code, reason);

    private static bool Same(decimal a, decimal b) => Math.Round(a, 2, MidpointRounding.AwayFromZero) == Math.Round(b, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// ใบขอชำระที่พนักงานส่ง (P3 · D-2) ถูกรูปไหม — แบบที่ชำระผ่านแถวตารางที่ยืนยันแล้ว · ยอด &gt; 0 · เงินเพิ่ม/เบี้ยปรับ ≥ 0 · Control Code ตัวเลขล้วนตามความยาวในแถว ·
    /// <b>ยอดในสมุดของลูกค้าต้องตรงยอดภาษีบนใบแจ้งชำระ</b> (ไม่ตรง/ไม่มีรายการค้าง ⇒ บล็อก · ติ๊ก "ส่งไปก่อน" ⇒ ผ่านแต่ <c>BooksMismatch</c> = สลิปไม่บันทึกเอง)
    /// </summary>
    /// <param name="booksAmount">ยอดค้างของงวดนั้นใน dashboard ของลูกค้า · null = ไม่มีรายการค้าง</param>
    public static (string? Problem, bool BooksMismatch, string RuleCode) RequestProblem(string type, decimal amount, decimal surcharge, decimal penalty, string? controlCode,
        decimal? booksAmount, bool sendAnyway, bool isAmendment)
    {
        var row = GovPaymentSlip.Billers.FirstOrDefault(b => b.Auto && GovPaymentSlip.Covers(b, type));
        if (row == null) return ("แบบนี้ยังไม่มีช่องทางชำระที่ยืนยันกับเอกสารจริง — ส่งใบขอชำระไม่ได้", false, Codes.UnknownAgency);
        if (amount <= 0m) return ("ยอดภาษีต้องมากกว่า 0", false, Codes.Amount);
        if (surcharge < 0m || penalty < 0m) return ("เงินเพิ่ม/เบี้ยปรับติดลบไม่ได้", false, Codes.Amount);
        var cc = TaxSlipRefHash.Canon(controlCode);
        if (cc.Length == 0 || !cc.All(char.IsAsciiDigit) || (row.ControlCodeLength is int len && cc.Length != len))
            return ("Control Code ต้องเป็นตัวเลขล้วน" + (row.ControlCodeLength is int l2 ? $" {l2} หลัก" : "") + " ตามใบแจ้งชำระของกรม", false, Codes.ControlCode);
        var mismatch = booksAmount is not decimal b || !Same(b, amount);
        if (mismatch && !sendAnyway)
            return (booksAmount is decimal b2
                ? $"ยอดในสมุดของลูกค้า {b2:N2} ≠ ยอดภาษีบนใบแจ้งชำระ {amount:N2} — ตรวจรายการของงวดก่อนส่ง (หรือติ๊ก “ส่งไปก่อน · ให้สลิปเข้าคิวพนักงาน”)"
                : "ไม่พบยอดค้างของงวดนี้ในสมุดของลูกค้า — ตรวจรายการของงวดก่อนส่ง (หรือติ๊ก “ส่งไปก่อน · ให้สลิปเข้าคิวพนักงาน”)", true, Codes.BooksMismatch);
        return (null, mismatch, mismatch ? Codes.BooksMismatch : isAmendment ? Codes.Amendment : Codes.One);
    }

    /// <param name="companies">ชุดบริษัทลูกค้าที่ช่องทางนี้ผูก (กลุ่ม LINE/อีเมลของผู้ติดต่อ NextAccTenant) — <b>ห้ามส่งทั้งฐาน</b></param>
    /// <param name="remitted">(บริษัท · แบบ · ปี · เดือน) ที่มีแถวนำส่งแล้ว</param>
    /// <param name="controlCodeRemitted">Control Code นี้ถูกบันทึกจากสลิปอื่นแล้ว (กันซ้ำชั้น 4 §7)</param>
    public static Decision Decide(Slip slip, IReadOnlyList<CompanyKey> companies, IReadOnlyList<RequestKey> requests, IReadOnlyList<PendingKey> pending,
        IReadOnlySet<(Guid, string, int, int)> remitted, bool controlCodeRemitted)
    {
        if (slip.Agency.Kind != GovPaymentSlip.Kind.Identified || slip.Agency.Row == null)
            return D(Outcome.UnknownAgency, Codes.UnknownAgency, slip.Agency.Reason);
        var row = slip.Agency.Row;
        if (companies.Count == 0)
            return D(Outcome.NoTenant, Codes.NoTenant, "ผู้ติดต่อที่ผูกช่องทางนี้ไม่มีบัญชีบริษัทในระบบ — บันทึกให้ไม่ได้");
        var mine = companies.Where(c => slip.Agency.TaxId != null && ThaiTaxId.Same(c.TaxId, slip.Agency.TaxId)).Select(c => c.Id).ToHashSet();
        if (mine.Count == 0)
            return D(Outcome.TaxIdNotInGroup, Codes.TaxId, "เลขผู้เสียภาษีบนสลิปไม่ตรงบริษัทที่ช่องทางนี้ผูกไว้");
        if (controlCodeRemitted)
            return D(Outcome.Duplicate, Codes.Duplicate, "Control Code นี้ถูกบันทึกจากสลิปอื่นแล้ว — อาจจ่ายซ้ำ (ขอคืนจากกรม)", mine.Count == 1 ? mine.First() : null);

        // ── ทางหลัก (D-2): ใบขอชำระที่ Control Code ตรง ──
        var byCc = slip.ControlCodeHash == null ? new List<RequestKey>()
            : requests.Where(r => mine.Contains(r.CompanyId) && r.ControlCodeHash == slip.ControlCodeHash).ToList();
        if (byCc.Count > 1)
            return D(Outcome.Many, Codes.Many, "Control Code นี้ตรงใบขอชำระมากกว่าหนึ่งใบ — ให้พนักงานเลือก",
                null, byCc.Select(r => new Candidate(r.CompanyId, r.Type, r.Year, r.Month, r.Amount, r.Surcharge, r.Penalty, r.Id)).ToList());
        if (byCc.Count == 1)
        {
            var r = byCc[0];
            var cand = new Candidate(r.CompanyId, r.Type, r.Year, r.Month, r.Amount, r.Surcharge, r.Penalty, r.Id);
            var one = new[] { cand };
            if (r.Status == "Paid") return D(Outcome.Duplicate, Codes.Duplicate, "ใบขอชำระนี้บันทึกการชำระไปแล้ว — ไม่บันทึกซ้ำ", r.CompanyId, one);
            if (r.Status != "Open") return D(Outcome.RequestClosed, Codes.RequestClosed, "ใบขอชำระนี้ถูกยกเลิก/หมดอายุแล้ว — ให้พนักงานตรวจ", r.CompanyId, one);
            if (!GovPaymentSlip.Covers(row, r.Type)) return D(Outcome.UnknownAgency, Codes.UnknownAgency, "แบบในใบขอชำระไม่ได้ชำระผ่านหน่วยงานบนสลิปนี้", r.CompanyId, one);
            if (r.BooksMismatch) return D(Outcome.BooksMismatch, Codes.BooksMismatch, "ตอนส่งใบขอชำระ ยอดในสมุดของลูกค้าไม่ตรงยอดบนใบแจ้งชำระ — ให้พนักงานตรวจ", r.CompanyId, one);
            if (r.ExpiresAt is DateTime exp && slip.SlipDate.Date > exp.Date) return D(Outcome.Expired, Codes.Expired, "วันที่บนสลิปเลยวันหมดอายุของ Control Code — อาจอ่านผิดหรือใบผิด", r.CompanyId, one);
            if (r.IsAmendment) return D(Outcome.Amendment, Codes.Amendment, "ยื่นเพิ่มเติม — ระบบยังบันทึกนำส่งครั้งที่สองของงวดเดียวกันไม่ได้ (D-11)", r.CompanyId, one);
            if (!Same(slip.Amount, r.Total)) return D(Outcome.AmountMismatch, Codes.Amount, $"ยอดบนสลิป {slip.Amount:N2} ไม่ตรงยอดในใบแจ้งชำระ {r.Total:N2}", r.CompanyId, one);
            if (slip.Fee is decimal f && f != 0m) return D(Outcome.FeeNotZero, Codes.Fee, $"มีค่าธรรมเนียมธนาคาร {f:N2} บาท — ไม่อยู่ในยอดนำส่ง ให้พนักงานบันทึก", r.CompanyId, one);
            if (remitted.Contains((r.CompanyId, r.Type, r.Year, r.Month))) return D(Outcome.AlreadyRemitted, Codes.Remitted, "งวดนี้มีบันทึกนำส่งแล้ว — ให้พนักงานตรวจ (อาจเป็นยื่นเพิ่มเติม)", r.CompanyId, one);
            var p = pending.FirstOrDefault(x => x.CompanyId == r.CompanyId && x.Type == r.Type && x.Year == r.Year && x.Month == r.Month);
            if (p == null || !Same(p.Amount, r.Amount))
                return D(Outcome.BooksChanged, Codes.BooksChanged, "ยอดค้างในสมุดของลูกค้าเปลี่ยนหลังส่งใบขอชำระ — ให้พนักงานตรวจก่อนบันทึก", r.CompanyId, one);
            return new Decision(Outcome.One, r.CompanyId, cand, one, true, Codes.One, "Control Code ตรงใบขอชำระ · เลขภาษีตรง · ยอดตรงทุกสตางค์");
        }

        // ── ทางสำรอง (D-3): ไม่มีใบขอชำระ ⇒ เฉพาะรายการค้างตัวเดียวที่ผ่านทุกด่าน ──
        var open = pending.Where(p => mine.Contains(p.CompanyId) && GovPaymentSlip.Covers(row, p.Type) && !remitted.Contains((p.CompanyId, p.Type, p.Year, p.Month))).ToList();
        var exact = open.Where(p => Same(p.Amount, slip.Amount)).ToList();
        Candidate C(PendingKey p) => new(p.CompanyId, p.Type, p.Year, p.Month, p.Amount, 0m, 0m, null);
        if (exact.Count == 0)
        {
            if (open.Any(p => slip.Amount > p.Amount && slip.SlipDate.Date > p.DueDate.Date))
                return D(Outcome.LateWithoutRequest, Codes.Late, "ชำระหลังกำหนดและไม่มีใบขอชำระ — ระบบไม่แยกเงินเพิ่ม/เบี้ยปรับเอง ให้พนักงานตรวจ");
            return D(Outcome.None, Codes.None, $"ยังไม่พบรายการภาษีที่รอชำระที่ยอดตรง (ยอดบนสลิป {slip.Amount:N2})", mine.Count == 1 ? mine.First() : null);
        }
        // ฝ่ายค้าน 272 M2: งวดที่มีใบขอชำระเปิดอยู่ ⇒ ต้องเข้าทาง Control Code (D-2) เท่านั้น — CC ไม่ตรง (อ่านผิด/คนละใบ) ⇒ คิว ไม่ใช้ D-3
        var withOpenRequest = exact.Where(p => requests.Any(r => r.CompanyId == p.CompanyId && r.Type == p.Type && r.Year == p.Year && r.Month == p.Month && r.Status == "Open")).ToList();
        if (withOpenRequest.Count > 0)
            return D(Outcome.RequestCcMismatch, Codes.RequestCcMismatch, "งวดนี้มีใบขอชำระที่ส่งไว้ แต่ Control Code บนสลิปไม่ตรงใบนั้น — ให้พนักงานตรวจ",
                withOpenRequest[0].CompanyId, exact.Select(C).ToList());
        // ฝ่ายค้าน 272 M1: ค่าธรรมเนียม + ช่วงยื่นกรองก่อนนับ "ยอดเดียวหลายแบบ" (ทาง Many ต้องเดินด่านเดียวกับทางตัวเดียว)
        if (slip.Fee is decimal fee && fee != 0m)
            return D(Outcome.FeeNotZero, Codes.Fee, $"มีค่าธรรมเนียมธนาคาร {fee:N2} บาท — ไม่อยู่ในยอดนำส่ง ให้พนักงานบันทึก", mine.Count == 1 ? mine.First() : null, exact.Select(C).ToList());
        var inWindow = exact.Where(p => (p.WindowFrom is not DateTime from || slip.SlipDate.Date >= from.Date) && slip.SlipDate.Date <= p.DueDate.Date).ToList();
        if (inWindow.Count == 0)
        {
            var o = exact[0];
            return D(Outcome.OutOfWindow, Codes.Window, o.WindowFrom is DateTime f2 && slip.SlipDate.Date < f2.Date
                ? "วันที่บนสลิปอยู่ก่อนงวดสิ้นสุด — เป็นไปไม่ได้สำหรับงวดนี้"
                : "วันที่บนสลิปเลยกำหนดยื่นของงวด — ไม่มีใบขอชำระ ให้พนักงานตรวจเงินเพิ่ม", exact.Count == 1 ? o.CompanyId : null, exact.Select(C).ToList());
        }
        var cands = inWindow.Select(C).ToList();
        if (inWindow.Count > 1)
            return D(Outcome.Many, Codes.Many, $"ยอดนี้ตรงรายการค้าง {inWindow.Count} รายการในช่วงยื่น — แบบภาษีไม่ชัด ต้องเลือก", null, cands);
        return new Decision(Outcome.One, inWindow[0].CompanyId, cands[0], cands, true, Codes.OneNoControlCode,
            "ไม่มีใบขอชำระ · เหลือรายการค้างเดียวที่เลขภาษี/ยอด/ช่วงยื่นตรง (คำตัดสิน 272-1-3)");
    }
}
