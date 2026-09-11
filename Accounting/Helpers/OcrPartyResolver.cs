using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ชุดข้อมูลของคู่สัญญาหนึ่งฝั่งตามที่ engine/ตัวสกัดคืนมา (ชื่อ · เลขภาษี · ที่อยู่ · สาขา)</summary>
public sealed record OcrPartyBlock(string? Name, string? TaxId, string? Address, string? BranchCode)
{
    public static readonly OcrPartyBlock Empty = new(null, null, null, null);
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(TaxId)
        && string.IsNullOrWhiteSpace(Address) && string.IsNullOrWhiteSpace(BranchCode);
}

/// <summary>ตัวตนของบริษัทเรา (จากฐานข้อมูล — ไม่ใช่จากกระดาษ)</summary>
public sealed record OcrOurIdentity(string? Name, string? NameEn, string? TaxId, string? BranchCode, string? Address);

/// <summary>ตัวตัดสินทำอะไรกับสองช่องที่ engine ให้มา</summary>
public enum OcrPartyDecision
{
    /// <summary>ช่องเดิมถูกอยู่แล้ว</summary>
    Unchanged = 0,
    /// <summary>engine ใส่ชื่อเราผิดฝั่ง — ย้ายไปฝั่งที่กระดาษบอก</summary>
    Swapped = 1,
    /// <summary>ฝั่งคู่ค้าถืออยู่เป็นบริษัทเราเอง แต่ไม่มีหลักฐานว่าควรย้ายไปไหน — ล้างทิ้ง</summary>
    CounterpartyCleared = 2,
    /// <summary>หลักฐานขัดกัน (เราอยู่ทั้งสองช่อง / ไม่รู้ว่าเราคือใคร) — ต้องให้ชั้นถัดไปตัดสิน</summary>
    Conflict = 3,
}

/// <summary>ผลการตัดสิน "ชุดข้อมูลไหนเป็นของใคร"</summary>
/// <param name="Vendor">ช่องผู้ขายหลังตัดสิน</param>
/// <param name="Buyer">ช่องผู้ซื้อหลังตัดสิน</param>
/// <param name="OurSide">เราเป็นฝั่งไหนของกระดาษใบนี้ (Unknown = ตัดสินจากตัวตน/ป้ายไม่ได้)</param>
/// <param name="Confidence">ความมั่นใจของ <paramref name="OurSide"/> 0–1</param>
/// <param name="Decision">ทำอะไรกับสองช่อง</param>
/// <param name="ShouldAskAi">หลักฐานเชิงกติกาไม่พอ — เคสที่คุ้มจะถาม "ครู" (กฎเหล็ก #1: ถามเฉพาะตอนไม่มั่นใจ)</param>
/// <param name="AskAiReason">ทำไมถึงควรถาม (ใส่ในพรอมป์ต์ + ReasoningTrace)</param>
/// <param name="Reasons">เหตุผลภาษาไทยทีละข้อ — ลง ReasoningTrace ให้ผู้ใช้ไล่ย้อนได้</param>
public sealed record OcrPartyResolution(
    OcrPartyBlock Vendor,
    OcrPartyBlock Buyer,
    OcrSelfSide OurSide,
    decimal Confidence,
    OcrPartyDecision Decision,
    bool ShouldAskAi,
    string? AskAiReason,
    IReadOnlyList<string> Reasons);

/// <summary>
/// **ขั้นตัดสินกลาง "ชุดข้อมูลไหนคือใคร" — ตัวเดียวของไปป์ไลน์ OCR**
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-09-11 · บิลเงินสดเขียนมือ 3,500 บาท) ═══ ระบบ
/// สรุปว่าเราเป็นผู้ขายของบิลที่ร้านออกให้เรา แล้วเสนอสร้างใบแจ้งหนี้ขาย. ไล่ดูพบว่า
/// การตัดสิน "ใครเป็นใคร" กระจายอยู่ <b>5 จุดใน 3 ไฟล์</b> — ตัวสกัดตามป้าย · ตัวอนุมาน
/// บทบาท · บล็อก "ถ้าผู้ขายคือเราให้สลับ" (ที่ไม่ล้างอีกฝั่ง) · บล็อกเติมเลขผู้ซื้อ ·
/// บล็อก [SelfParty] ที่เพิ่งเพิ่ม — แต่ละจุดมองหลักฐานคนละชุด แก้ที่หนึ่งแล้วอีกที่
/// เขียนทับ และไม่มีจุดไหน sync ผลสุดท้ายลงแถวสแกนครบ (ชื่อผู้ขายบนจอยังเป็นเรา
/// ทั้งที่ในหน่วยความจำย้ายไปแล้ว)</para>
///
/// <para>═══ ลำดับหลักฐาน (แรง → อ่อน) ═══
/// 1. <b>เลขผู้เสียภาษีตรงกับเรา</b> (checksum ผ่าน) — ตัวตนทางกฎหมาย 1.0
/// 2. <b>ชื่อตรงกับเรา</b> (ตัดคำนำหน้านิติบุคคลแล้วครอบกัน) 0.85 · ที่อยู่ตรงเลขที่บ้านของเรา
///    เป็นหลักฐานประกอบว่าบล็อกนี้คือเรา
/// 3. <b>ป้ายบนกระดาษ</b> ใต้/เหนือชื่อเรา (นาม/ลูกค้า/Bill To vs ผู้ขาย/ผู้ออกใบ) 0.90 —
///    <b>ชนะ "ช่องที่ engine เลือกใส่"</b> เพราะช่องนั้นคือสิ่งที่ถูกสงสัย
/// 4. ช่องที่ engine เลือกใส่ — อ่อนสุด ใช้เป็นตัวตัดเสมอเท่านั้น
/// เลขภาษี 13 หลักที่<b>ต่างจากของเราแค่หลักเดียว</b> (OCR อ่านเพี้ยนจน checksum ตก)
/// นับเป็นหลักฐานว่า "เราอยู่บนกระดาษใบนี้" ไม่ใช่หลักฐานว่าอยู่ฝั่งไหน</para>
///
/// <para>═══ กติกาที่ห้ามละเมิด ═══ (ก) ฝั่งคู่ค้าต้องไม่ใช่บริษัทเราเอง — ถ้าเป็น ให้ล้าง
/// ดีกว่าปล่อย (จับคู่ Contact เป็นตัวเอง = เอกสารซื้อขายกับตัวเอง) (ข) ผู้ขาย ≠ ผู้ซื้อ
/// (ค) <b>ไม่รู้ = บอกว่าไม่รู้</b> แล้วส่งต่อให้ AI/คน — ห้ามเดา เพราะเดาผิดทิศเดียว
/// รายจ่ายกลายเป็นรายได้ (ง) ค่าที่ย้ายตามชื่อไปอีกฝั่ง ย้ายเฉพาะที่ฝั่งปลายทางยังว่าง
/// (ตัวผ่าที่อยู่อาจเติมของฝั่งนั้นไว้ถูกแล้ว)</para>
///
/// <para>═══ เมื่อไรถึงคุ้มถาม AI (<see cref="OcrPartyResolution.ShouldAskAi"/>) ═══
/// เฉพาะเคสที่กติกา<b>ตัดสินไม่ได้จริง</b>: เราโผล่ทั้งสองช่อง · ไม่พบตัวตนเราในช่องไหน
/// แต่มีชื่อคู่สัญญาครบสองฝั่ง · ชื่อเราอยู่ช่องผู้ขายโดยไม่มีคู่ค้าและไม่มีป้าย. เคสที่
/// กติกามั่นใจแล้วไม่ถาม (กฎเหล็ก #1 ข้อ 6 — อัตราเรียก AI ต้องลดลง)</para>
/// </summary>
public static class OcrPartyResolver
{
    public static OcrPartyResolution Resolve(
        string? rawText, OcrOurIdentity us, OcrPartyBlock vendor, OcrPartyBlock buyer)
        => FillOurIdentity(ResolveCore(rawText, us, vendor, buyer), rawText, us);

    /// <summary>เมื่อรู้แน่แล้วว่าเราอยู่ฝั่งไหน (≥ 0.85) และบล็อกของเรายัง<b>ไม่มีเลขภาษี</b>
    /// แต่กระดาษมีเลขเรา (ตรง หรือต่างหลักเดียวจน checksum ตก) → เติมเลขภาษี**ของจริงจาก DB**
    ///
    /// <para>ที่มา (บิลเงินสด 2026-09-11): กระดาษพิมพ์ 0203562025871 (เลขเราอ่านเพี้ยน 1 หลัก)
    /// ตัวสกัดทิ้งเพราะ checksum ตก ⇒ ช่องเลขภาษีผู้ซื้อว่างทั้งที่รู้ว่าผู้ซื้อคือเรา —
    /// นี่ไม่ใช่การ "แต่งค่า": ค่ามาจากทะเบียนของเราเอง ส่วนกระดาษเป็นแค่หลักฐานว่าเลขนั้น
    /// อยู่บนใบ (ไม่เติมเมื่อกระดาษไม่มีเลขเราเลย — ใบที่ผู้ขายไม่ได้กรอกเลขผู้ซื้อก็มี)</para></summary>
    private static OcrPartyResolution FillOurIdentity(OcrPartyResolution r, string? rawText, OcrOurIdentity us)
    {
        if (r.Confidence < 0.85m || r.OurSide == OcrSelfSide.Unknown) return r;
        var ours = ThaiTaxId.Normalize(us.TaxId);
        if (ours.Length != 13) return r;
        var (exact, nearMiss) = OurTaxIdOnPaper(rawText, ours);
        if (!exact && nearMiss == null) return r;

        var mine = r.OurSide == OcrSelfSide.Buyer ? r.Buyer : r.Vendor;
        if (!string.IsNullOrWhiteSpace(mine.TaxId)) return r;
        var filled = mine with { TaxId = ours };
        var why = exact
            ? "เติมเลขผู้เสียภาษีฝั่งเรา = เลขของบริษัทเราจากทะเบียน (พบเลขเดียวกันบนกระดาษ)"
            : $"เติมเลขผู้เสียภาษีฝั่งเรา = {ours} จากทะเบียน (กระดาษพิมพ์ {nearMiss} ซึ่งต่างหลักเดียว = OCR อ่านเพี้ยน)";
        var reasons = new List<string>(r.Reasons) { why };
        return r.OurSide == OcrSelfSide.Buyer
            ? r with { Buyer = filled, Reasons = reasons }
            : r with { Vendor = filled, Reasons = reasons };
    }

    private static OcrPartyResolution ResolveCore(
        string? rawText, OcrOurIdentity us, OcrPartyBlock vendor, OcrPartyBlock buyer)
    {
        var reasons = new List<string>();
        vendor ??= OcrPartyBlock.Empty;
        buyer ??= OcrPartyBlock.Empty;

        var vUs = SelfStrength(vendor, us, out var vWhy);
        var bUs = SelfStrength(buyer, us, out var bWhy);
        if (vUs > 0) reasons.Add($"ช่องผู้ขาย = บริษัทเรา ({vWhy})");
        if (bUs > 0) reasons.Add($"ช่องผู้ซื้อ = บริษัทเรา ({bWhy})");

        var paper = OcrSelfPartyGuard.FromPaperLabels(rawText, us.Name, us.TaxId);
        if (paper.Side == OcrSelfSide.Unknown && !string.IsNullOrWhiteSpace(us.NameEn))
            paper = OcrSelfPartyGuard.FromPaperLabels(rawText, us.NameEn, us.TaxId);
        if (paper.Side != OcrSelfSide.Unknown) reasons.Add(paper.Reason);

        var nearMiss = OurTaxIdNearMissOnPaper(rawText, us.TaxId);
        if (nearMiss != null)
            reasons.Add($"พบเลข 13 หลัก “{nearMiss}” ที่ต่างจากเลขภาษีของเราหลักเดียว — OCR อ่านเลขเราเพี้ยน ⇒ เราอยู่บนกระดาษใบนี้");

        // ── 1. เราอยู่ทั้งสองช่อง — หลักฐานขัดกัน ห้ามเดา ──
        if (vUs > 0 && bUs > 0)
        {
            reasons.Add("ทั้งสองช่องเป็นบริษัทเรา — ตัดสินทิศทางจากช่องไม่ได้");
            // ป้ายบนกระดาษยังช่วยชี้ได้ว่าเราอยู่ฝั่งไหน แล้วล้างอีกฝั่งทิ้ง
            if (paper.Side == OcrSelfSide.Buyer)
                return new(ClearIfSelf(vendor, us), buyer, OcrSelfSide.Buyer, 0.80m,
                    OcrPartyDecision.CounterpartyCleared, false, null,
                    Add(reasons, "ป้ายบอกว่าเราเป็นผู้ซื้อ → ล้างช่องผู้ขายที่เป็นเราเอง ให้ผู้ใช้ระบุผู้ขาย"));
            if (paper.Side == OcrSelfSide.Seller)
                return new(vendor, ClearIfSelf(buyer, us), OcrSelfSide.Seller, 0.80m,
                    OcrPartyDecision.CounterpartyCleared, false, null,
                    Add(reasons, "ป้ายบอกว่าเราเป็นผู้ขาย → ล้างช่องผู้ซื้อที่เป็นเราเอง ให้ผู้ใช้ระบุลูกค้า"));
            return new(vendor, buyer, OcrSelfSide.Unknown, 0.30m, OcrPartyDecision.Conflict,
                true, "ชื่อ/เลขภาษีของบริษัทเราถูกอ่านได้ทั้งฝั่งผู้ขายและผู้ซื้อ และกระดาษไม่มีป้ายชี้ทิศ",
                reasons);
        }

        // ── 2. เราอยู่ช่องผู้ขาย ──
        if (vUs > 0)
        {
            // ตัวตนทางกฎหมาย (เลขภาษีตรง) มาก่อนป้ายเสมอ — ใบที่เราออกเองมักไม่พิมพ์ป้าย
            // "ผู้ขาย" แต่พิมพ์ "ลูกค้า" ห่างจากหัวเราไม่กี่สิบตัวอักษร ถ้าให้ป้ายชนะ ใบขาย
            // ของเราเองจะถูกสลับเป็นใบซื้อ (ทีมตรวจ 2026-09-11 — วัดได้ 52–71 ตัวอักษร)
            if (vUs >= 1.0m)
            {
                // เลขเราตัวเดียวบนใบ + ไม่มีคู่ค้า + ไม่มีป้าย = ใบที่ผู้ขายพิมพ์แค่เลขลูกค้า
                // (บิลร้านไม่จด VAT) แล้วตัวสกัดเอนเลขเข้าช่องผู้ขาย — "1.0" แปลว่าเราอยู่บนใบ
                // ไม่ได้แปลว่าช่องนั้นคือผู้ออกบิล ⇒ ไม่ฟันธง ส่งต่อ
                var lonelyTax = string.IsNullOrWhiteSpace(buyer.Name) && paper.Side == OcrSelfSide.Unknown;
                if (lonelyTax)
                    return new(vendor, buyer, OcrSelfSide.Seller, 0.60m, OcrPartyDecision.Unchanged,
                        true, "เลขภาษีเราอยู่ช่องผู้ขาย แต่ไม่มีคู่ค้าอีกฝั่งและกระดาษไม่มีป้าย — อาจเป็นเลขลูกค้าที่ตัวสกัดเอนเข้าช่องผู้ขาย",
                        Add(reasons, "เลขภาษีเราอยู่ช่องผู้ขายโดยไม่มีคู่ค้า — มั่นใจต่ำ"));
                return new(vendor, ClearIfSelf(buyer, us), OcrSelfSide.Seller, 1.0m, OcrPartyDecision.Unchanged,
                    false, null, Add(reasons, "เลขภาษีผู้ขายคือเรา → ใบที่เราออกเอง (เราเป็นผู้ขาย)"));
            }
            if (paper.Side == OcrSelfSide.Buyer)
            {
                var (newVendor, newBuyer) = MoveSelfToBuyer(vendor, buyer, us);
                return new(newVendor, newBuyer, OcrSelfSide.Buyer, 0.90m, OcrPartyDecision.Swapped,
                    false, null,
                    Add(reasons, "กระดาษวางชื่อเราใต้ป้ายฝั่งผู้ซื้อ แต่ engine ใส่ไว้ช่องผู้ขาย → ย้ายไปผู้ซื้อ "
                        + (newVendor.IsEmpty || string.IsNullOrWhiteSpace(newVendor.Name)
                            ? "· ผู้ขายตัวจริงอ่านไม่ได้ ปล่อยว่างให้ผู้ใช้ระบุ"
                            : $"· ผู้ขาย = “{newVendor.Name}” (จากช่องผู้ซื้อเดิม)")));
            }
            var lonely = string.IsNullOrWhiteSpace(buyer.Name) && paper.Side == OcrSelfSide.Unknown;
            // ป้ายฝั่งผู้ขายยืนยันช่องที่ engine ใส่ → 0.90 (เท่าฝั่งผู้ซื้อที่ป้ายยืนยัน) ·
            // ไม่มีป้าย = ยังพึ่งช่องของ engine อยู่ → 0.75 · ไม่มีคู่ค้าด้วย → 0.60 + ถาม AI
            var sellerConf = paper.Side == OcrSelfSide.Seller ? 0.90m : (lonely ? 0.60m : 0.75m);
            return new(vendor, ClearIfSelf(buyer, us), OcrSelfSide.Seller, sellerConf, OcrPartyDecision.Unchanged,
                lonely, lonely
                    ? "ชื่อเราอยู่ช่องผู้ขาย แต่กระดาษไม่มีป้ายชี้ทิศและไม่พบชื่อคู่ค้าอีกฝั่ง — engine อาจใส่ผิดช่อง"
                    : null,
                Add(reasons, lonely
                    ? "ชื่อเราอยู่ช่องผู้ขาย (fuzzy) โดยไม่มีคู่ค้าอีกฝั่ง — มั่นใจต่ำ"
                    : paper.Side == OcrSelfSide.Seller
                        ? "ชื่อเราอยู่ช่องผู้ขายและป้ายบนกระดาษยืนยัน → เราเป็นผู้ขาย"
                        : "ชื่อเราอยู่ช่องผู้ขาย (fuzzy) และไม่มีป้ายค้าน → เราเป็นผู้ขาย"));
        }

        // ── 3. เราอยู่ช่องผู้ซื้อ ──
        if (bUs > 0)
        {
            if (bUs >= 1.0m)
                return new(ClearIfSelf(vendor, us), buyer, OcrSelfSide.Buyer, 1.0m, OcrPartyDecision.Unchanged,
                    false, null, Add(reasons, "เลขภาษีผู้ซื้อคือเรา → เราเป็นผู้ซื้อ"));
            if (paper.Side == OcrSelfSide.Seller)
            {
                var (newVendor, newBuyer) = MoveSelfToVendor(vendor, buyer, us);
                return new(newVendor, newBuyer, OcrSelfSide.Seller, 0.90m, OcrPartyDecision.Swapped,
                    false, null,
                    Add(reasons, "กระดาษวางชื่อเราใต้ป้ายฝั่งผู้ขาย แต่ engine ใส่ไว้ช่องผู้ซื้อ → ย้ายไปผู้ขาย"));
            }
            var conf = paper.Side == OcrSelfSide.Buyer ? 0.90m : 0.85m;
            return new(ClearIfSelf(vendor, us), buyer, OcrSelfSide.Buyer, conf, OcrPartyDecision.Unchanged,
                false, null, Add(reasons, "เราอยู่ช่องผู้ซื้อ → เราเป็นผู้ซื้อ"));
        }

        // ── 4. ไม่พบตัวตนเราในช่องไหนเลย ──
        if (paper.Side != OcrSelfSide.Unknown)
        {
            // ชื่อเราอยู่บนกระดาษใต้ป้าย แต่ engine ใส่ชื่ออื่นในช่องนั้น — เชื่อป้าย
            // เรื่อง "เราเป็นใคร" แต่ไม่แตะช่อง (ไม่มีอะไรบอกว่าชื่อในช่องผิด)
            return new(vendor, buyer, paper.Side, 0.80m, OcrPartyDecision.Unchanged, false, null,
                Add(reasons, "ตัวตนเราไม่อยู่ในช่องใด แต่ป้ายบนกระดาษชี้ฝั่งเราได้"));
        }
        var bothNamed = !string.IsNullOrWhiteSpace(vendor.Name) && !string.IsNullOrWhiteSpace(buyer.Name);
        return new(vendor, buyer, OcrSelfSide.Unknown, 0.50m, OcrPartyDecision.Unchanged,
            bothNamed,
            bothNamed ? "อ่านชื่อคู่สัญญาได้ทั้งสองฝั่งแต่ไม่พบบริษัทเราในช่องไหนและกระดาษไม่มีป้ายชี้ทิศ" : null,
            Add(reasons, "ไม่พบตัวตนบริษัทเราบนกระดาษ — ให้ตัวอนุมานบทบาทตัดสินจากหลักฐานอื่น (50 ทวิ/ตำแหน่ง)"));
    }

    /// <summary>บังคับผลลัพธ์ตามฝั่งที่ชั้นบน (AI ที่ผ่านด่าน / ผู้ใช้) ตัดสิน — ย้าย/ล้างบล็อกให้
    /// สอดคล้อง: ถ้าเราอยู่ผิดช่อง ย้าย · ถ้าคู่ค้าเป็นเราเอง ล้าง · ไม่งั้นคงเดิม
    ///
    /// <para>ที่มา (ทีมตรวจ 2026-09-11): เส้น AI เดิมเปลี่ยนแค่ป้ายบทบาท ไม่ย้ายบล็อก ⇒ ชื่อเรา
    /// ค้างในช่องผู้ขายแล้ว sync ลงแถว = อาการเดิมที่ผู้ใช้รายงานกลับมาอีกทางหนึ่ง</para></summary>
    public static (OcrPartyBlock Vendor, OcrPartyBlock Buyer) ApplySide(
        OcrPartyBlock vendor, OcrPartyBlock buyer, OcrSelfSide side, OcrOurIdentity us)
    {
        if (side == OcrSelfSide.Buyer)
        {
            if (SelfStrength(vendor, us, out _) > 0 && SelfStrength(buyer, us, out _) == 0)
                return MoveSelfToBuyer(vendor, buyer, us);
            return (ClearIfSelf(vendor, us), buyer);
        }
        if (side == OcrSelfSide.Seller)
        {
            if (SelfStrength(buyer, us, out _) > 0 && SelfStrength(vendor, us, out _) == 0)
                return MoveSelfToVendor(vendor, buyer, us);
            return (vendor, ClearIfSelf(buyer, us));
        }
        return (vendor, buyer);
    }

    /// <summary>บล็อกนี้เป็นบริษัทเราแค่ไหน — 1.0 เลขภาษีตรง · 0.85 ชื่อตรง (+ที่อยู่ประกอบ) · 0 ไม่ใช่</summary>
    public static decimal SelfStrength(OcrPartyBlock block, OcrOurIdentity us, out string why)
    {
        why = "";
        if (ThaiTaxId.IsValid(block.TaxId) && ThaiTaxId.Same(block.TaxId, us.TaxId))
        { why = "เลขผู้เสียภาษีตรง"; return 1.0m; }
        // เลขภาษีที่ checksum ผ่านและ**ไม่ใช่**ของเรา = นิติบุคคลอื่นแน่ ๆ — ชื่อคล้ายแค่ไหนก็ไม่ใช่เรา
        // (บริษัทในเครือ "แอม แฮปปี้เนส เทรดดิ้ง" มีเลขของตัวเอง)
        if (ThaiTaxId.IsValid(block.TaxId) && !ThaiTaxId.Same(block.TaxId, us.TaxId)) return 0m;
        if (IsUs(block.Name, us))
        {
            why = AddressLooksLikeOurs(block.Address, us.Address) ? "ชื่อตรง + เลขที่บ้านตรง" : "ชื่อตรง";
            return 0.85m;
        }
        return 0m;
    }

    /// <summary>ที่อยู่บนกระดาษมีเลขที่บ้านตัวเดียวกับที่อยู่จดทะเบียนของเราไหม —
    /// เทียบแค่ token แรก (เลขที่บ้าน) เพราะ OCR อ่านที่อยู่เพี้ยนได้ทุกส่วน ยกเว้นตัวเลขต้นบรรทัด</summary>
    public static bool AddressLooksLikeOurs(string? address, string? ourAddress)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(ourAddress)) return false;
        var m = Regex.Match(ourAddress, @"\d{1,5}(?:/\d{1,5})?");
        if (!m.Success) return false;
        // ต้องเป็นเลขบ้าน (token แรก หรือหลัง "เลขที่") — ไม่ใช่ "ซอย 99"/"หมู่ 99" ที่ไหนก็ได้ในที่อยู่
        return Regex.IsMatch(address, $@"^\s*(?:เลขที่\s*)?{Regex.Escape(m.Value)}(?![\d/])");
    }

    /// <summary>เลข 13 หลักบนกระดาษที่ต่างจากเลขภาษีเราแค่หลักเดียว (แต่ checksum ตก
    /// จึงถูกตัวสกัดทิ้ง) — คืนเลขที่พบ หรือ null · เทียบแบบ Hamming ≤ 1 ตำแหน่ง</summary>
    public static string? OurTaxIdNearMissOnPaper(string? rawText, string? ourTaxId)
        => OurTaxIdOnPaper(rawText, ThaiTaxId.Normalize(ourTaxId)).NearMiss;

    /// <summary>เลขภาษีเราอยู่บนกระดาษไหม — <c>Exact</c> ตรงทุกหลัก · <c>NearMiss</c> เลข 13 หลัก
    /// ที่ต่างหลักเดียว (OCR อ่านเพี้ยนจน checksum ตก) หรือ null</summary>
    public static (bool Exact, string? NearMiss) OurTaxIdOnPaper(string? rawText, string ours)
    {
        if (ours.Length != 13 || string.IsNullOrEmpty(rawText)) return (false, null);
        var exact = false; string? near = null;
        foreach (Match m in Regex.Matches(rawText, @"(?<!\d)\d(?:[- \t]?\d){12}(?!\d)"))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits == ours) { exact = true; continue; }
            var diff = 0;
            for (var i = 0; i < 13 && diff <= 1; i++) if (digits[i] != ours[i]) diff++;
            if (diff == 1) near ??= digits;
        }
        return (exact, near);
    }

    /// <summary>ย้าย "เรา" จากช่องผู้ขายไปช่องผู้ซื้อ — ค่าที่ระบุได้ว่าเป็นของเรา (ชื่อ · เลขภาษี
    /// ที่ตรง · ที่อยู่ที่เลขบ้านตรง) ตามไป · ค่าอื่นตามไปเฉพาะเมื่อฝั่งปลายทางว่าง ·
    /// ชื่อคู่ค้าที่ค้างอยู่ช่องผู้ซื้อ (ถ้ามีและไม่ใช่เรา) ย้ายมาเป็นผู้ขาย</summary>
    private static (OcrPartyBlock Vendor, OcrPartyBlock Buyer) MoveSelfToBuyer(
        OcrPartyBlock vendor, OcrPartyBlock buyer, OcrOurIdentity us)
    {
        var (mine, other) = MoveSelf(from: vendor, to: buyer, us);
        return (other, mine);
    }

    private static (OcrPartyBlock Vendor, OcrPartyBlock Buyer) MoveSelfToVendor(
        OcrPartyBlock vendor, OcrPartyBlock buyer, OcrOurIdentity us)
    {
        var (mine, other) = MoveSelf(from: buyer, to: vendor, us);
        return (mine, other);
    }

    /// <summary>ย้าย "เรา" จากช่อง <paramref name="from"/> ไปช่อง <paramref name="to"/>
    /// · ค่าที่ระบุได้ว่าเป็นของเรา (เลขภาษีตรง · เลขบ้านตรง) ตามชื่อไป**เสมอ** · ค่าอื่นตามไป
    /// เฉพาะเมื่อปลายทางว่าง**ก่อนย้าย** (เทียบสถานะก่อน ไม่ใช่เทียบค่าเท่ากัน — "00000" ทั้งสอง
    /// ฝั่งเป็นเรื่องปกติ ห้ามตีว่าย้ายแล้ว) · ค่าที่เหลือในช่องเดิม (ไม่ใช่ของเรา) กลายเป็นของคู่ค้า
    /// · ชื่อคู่ค้าที่ค้างอยู่ปลายทาง (ถ้าไม่ใช่เรา) ย้ายมาช่องเดิม</summary>
    private static (OcrPartyBlock Mine, OcrPartyBlock Other) MoveSelf(
        OcrPartyBlock from, OcrPartyBlock to, OcrOurIdentity us)
    {
        var taxIsOurs = ThaiTaxId.Same(from.TaxId, us.TaxId);
        var addrIsOurs = AddressLooksLikeOurs(from.Address, us.Address);
        var takeTax = taxIsOurs || (string.IsNullOrWhiteSpace(to.TaxId) && !string.IsNullOrWhiteSpace(from.TaxId));
        var takeAddr = addrIsOurs || (string.IsNullOrWhiteSpace(to.Address) && !string.IsNullOrWhiteSpace(from.Address));
        var takeBranch = string.IsNullOrWhiteSpace(to.BranchCode) && !string.IsNullOrWhiteSpace(from.BranchCode);

        var mine = new OcrPartyBlock(
            Name: from.Name,
            TaxId: takeTax ? from.TaxId : to.TaxId,
            Address: takeAddr ? from.Address : to.Address,
            BranchCode: takeBranch ? from.BranchCode : to.BranchCode);
        var other = new OcrPartyBlock(
            Name: IsUs(to.Name, us) ? null : to.Name,
            TaxId: takeTax ? (ThaiTaxId.Same(to.TaxId, us.TaxId) ? null : to.TaxId) : from.TaxId,
            Address: takeAddr ? (AddressLooksLikeOurs(to.Address, us.Address) ? null : to.Address) : from.Address,
            BranchCode: takeBranch ? to.BranchCode : from.BranchCode);
        return (mine, other);
    }

    /// <summary>"ชื่อนี้คือเรา" ตัวเดียวของคลาส — ครอบทั้งชื่อไทยและชื่ออังกฤษ</summary>
    private static bool IsUs(string? name, OcrOurIdentity us)
        => OcrSelfPartyGuard.IsSelf(name, us.Name) || OcrSelfPartyGuard.IsSelf(name, us.NameEn);

    /// <summary>กติกา (ก): ฝั่งคู่ค้าต้องไม่ใช่เรา — ล้างเฉพาะค่าที่ระบุได้ว่าเป็นเรา</summary>
    private static OcrPartyBlock ClearIfSelf(OcrPartyBlock counterparty, OcrOurIdentity us)
    {
        var nameIsUs = IsUs(counterparty.Name, us);
        var taxIsUs = ThaiTaxId.IsValid(counterparty.TaxId) && ThaiTaxId.Same(counterparty.TaxId, us.TaxId);
        if (!nameIsUs && !taxIsUs) return counterparty;
        return new OcrPartyBlock(
            Name: nameIsUs ? null : counterparty.Name,
            TaxId: taxIsUs ? null : counterparty.TaxId,
            Address: (nameIsUs || taxIsUs) && AddressLooksLikeOurs(counterparty.Address, us.Address) ? null : counterparty.Address,
            BranchCode: nameIsUs || taxIsUs ? null : counterparty.BranchCode);
    }

    private static IReadOnlyList<string> Add(List<string> reasons, string r) { reasons.Add(r); return reasons; }
}
