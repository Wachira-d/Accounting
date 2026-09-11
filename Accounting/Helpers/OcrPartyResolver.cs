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
    {
        var reasons = new List<string>();
        vendor ??= OcrPartyBlock.Empty;
        buyer ??= OcrPartyBlock.Empty;

        var vUs = SelfStrength(vendor, us, out var vWhy);
        var bUs = SelfStrength(buyer, us, out var bWhy);
        if (vUs > 0) reasons.Add($"ช่องผู้ขาย = บริษัทเรา ({vWhy})");
        if (bUs > 0) reasons.Add($"ช่องผู้ซื้อ = บริษัทเรา ({bWhy})");

        var paper = OcrSelfPartyGuard.FromPaperLabels(rawText, us.Name);
        if (paper.Side == OcrSelfSide.Unknown && !string.IsNullOrWhiteSpace(us.NameEn))
            paper = OcrSelfPartyGuard.FromPaperLabels(rawText, us.NameEn);
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
            if (vUs >= 1.0m)
                return new(vendor, ClearIfSelf(buyer, us), OcrSelfSide.Seller, 1.0m, OcrPartyDecision.Unchanged,
                    false, null, Add(reasons, "เลขภาษีผู้ขายคือเรา → ใบที่เราออกเอง (เราเป็นผู้ขาย)"));
            var lonely = string.IsNullOrWhiteSpace(buyer.Name) && paper.Side == OcrSelfSide.Unknown;
            return new(vendor, buyer, OcrSelfSide.Seller, lonely ? 0.60m : 0.75m, OcrPartyDecision.Unchanged,
                lonely, lonely
                    ? "ชื่อเราอยู่ช่องผู้ขาย แต่กระดาษไม่มีป้ายชี้ทิศและไม่พบชื่อคู่ค้าอีกฝั่ง — engine อาจใส่ผิดช่อง"
                    : null,
                Add(reasons, lonely
                    ? "ชื่อเราอยู่ช่องผู้ขาย (fuzzy) โดยไม่มีคู่ค้าอีกฝั่ง — มั่นใจต่ำ"
                    : "ชื่อเราอยู่ช่องผู้ขาย (fuzzy) และไม่มีป้ายค้าน → เราเป็นผู้ขาย"));
        }

        // ── 3. เราอยู่ช่องผู้ซื้อ ──
        if (bUs > 0)
        {
            if (paper.Side == OcrSelfSide.Seller)
            {
                var (newVendor, newBuyer) = MoveSelfToVendor(vendor, buyer, us);
                return new(newVendor, newBuyer, OcrSelfSide.Seller, 0.90m, OcrPartyDecision.Swapped,
                    false, null,
                    Add(reasons, "กระดาษวางชื่อเราใต้ป้ายฝั่งผู้ขาย แต่ engine ใส่ไว้ช่องผู้ซื้อ → ย้ายไปผู้ขาย"));
            }
            var conf = bUs >= 1.0m ? 1.0m : (paper.Side == OcrSelfSide.Buyer ? 0.90m : 0.85m);
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

    /// <summary>บล็อกนี้เป็นบริษัทเราแค่ไหน — 1.0 เลขภาษีตรง · 0.85 ชื่อตรง (+ที่อยู่ประกอบ) · 0 ไม่ใช่</summary>
    public static decimal SelfStrength(OcrPartyBlock block, OcrOurIdentity us, out string why)
    {
        why = "";
        if (ThaiTaxId.IsValid(block.TaxId) && ThaiTaxId.Same(block.TaxId, us.TaxId))
        { why = "เลขผู้เสียภาษีตรง"; return 1.0m; }
        var nameHit = OcrSelfPartyGuard.IsSelf(block.Name, us.Name) || OcrSelfPartyGuard.IsSelf(block.Name, us.NameEn);
        if (nameHit)
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
        return Regex.IsMatch(address, $@"(?<![\d/]){Regex.Escape(m.Value)}(?![\d/])");
    }

    /// <summary>เลข 13 หลักบนกระดาษที่ต่างจากเลขภาษีเราแค่หลักเดียว (แต่ checksum ตก
    /// จึงถูกตัวสกัดทิ้ง) — คืนเลขที่พบ หรือ null · เทียบแบบ Hamming ≤ 1 ตำแหน่ง</summary>
    public static string? OurTaxIdNearMissOnPaper(string? rawText, string? ourTaxId)
    {
        var ours = ThaiTaxId.Normalize(ourTaxId);
        if (ours.Length != 13 || string.IsNullOrEmpty(rawText)) return null;
        foreach (Match m in Regex.Matches(rawText, @"(?<!\d)\d(?:[- \t]?\d){12}(?!\d)"))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits == ours) continue;                 // ตรงเป๊ะ = ตัวสกัดเห็นแล้ว ไม่ต้องนับ
            var diff = 0;
            for (var i = 0; i < 13 && diff <= 1; i++) if (digits[i] != ours[i]) diff++;
            if (diff == 1) return digits;
        }
        return null;
    }

    /// <summary>ย้าย "เรา" จากช่องผู้ขายไปช่องผู้ซื้อ — ค่าที่ระบุได้ว่าเป็นของเรา (ชื่อ · เลขภาษี
    /// ที่ตรง · ที่อยู่ที่เลขบ้านตรง) ตามไป · ค่าอื่นตามไปเฉพาะเมื่อฝั่งปลายทางว่าง ·
    /// ชื่อคู่ค้าที่ค้างอยู่ช่องผู้ซื้อ (ถ้ามีและไม่ใช่เรา) ย้ายมาเป็นผู้ขาย</summary>
    private static (OcrPartyBlock Vendor, OcrPartyBlock Buyer) MoveSelfToBuyer(
        OcrPartyBlock vendor, OcrPartyBlock buyer, OcrOurIdentity us)
    {
        var newBuyer = new OcrPartyBlock(
            Name: vendor.Name,
            TaxId: Pick(buyer.TaxId, vendor.TaxId),
            Address: Pick(buyer.Address, vendor.Address),
            BranchCode: Pick(buyer.BranchCode, vendor.BranchCode));
        var newVendor = new OcrPartyBlock(
            Name: OcrSelfPartyGuard.IsSelf(buyer.Name, us.Name) ? null : buyer.Name,
            TaxId: MovedAway(vendor.TaxId, newBuyer.TaxId) || ThaiTaxId.Same(vendor.TaxId, us.TaxId) ? null : vendor.TaxId,
            Address: MovedAway(vendor.Address, newBuyer.Address) || AddressLooksLikeOurs(vendor.Address, us.Address) ? null : vendor.Address,
            BranchCode: MovedAway(vendor.BranchCode, newBuyer.BranchCode) ? null : vendor.BranchCode);
        return (newVendor, newBuyer);
    }

    private static (OcrPartyBlock Vendor, OcrPartyBlock Buyer) MoveSelfToVendor(
        OcrPartyBlock vendor, OcrPartyBlock buyer, OcrOurIdentity us)
    {
        var newVendor = new OcrPartyBlock(
            Name: buyer.Name,
            TaxId: Pick(vendor.TaxId, buyer.TaxId),
            Address: Pick(vendor.Address, buyer.Address),
            BranchCode: Pick(vendor.BranchCode, buyer.BranchCode));
        var newBuyer = new OcrPartyBlock(
            Name: OcrSelfPartyGuard.IsSelf(vendor.Name, us.Name) ? null : vendor.Name,
            TaxId: MovedAway(buyer.TaxId, newVendor.TaxId) || ThaiTaxId.Same(buyer.TaxId, us.TaxId) ? null : buyer.TaxId,
            Address: MovedAway(buyer.Address, newVendor.Address) || AddressLooksLikeOurs(buyer.Address, us.Address) ? null : buyer.Address,
            BranchCode: MovedAway(buyer.BranchCode, newVendor.BranchCode) ? null : buyer.BranchCode);
        return (newVendor, newBuyer);
    }

    /// <summary>กติกา (ก): ฝั่งคู่ค้าต้องไม่ใช่เรา — ล้างเฉพาะค่าที่ระบุได้ว่าเป็นเรา</summary>
    private static OcrPartyBlock ClearIfSelf(OcrPartyBlock counterparty, OcrOurIdentity us)
    {
        var nameIsUs = OcrSelfPartyGuard.IsSelf(counterparty.Name, us.Name)
            || OcrSelfPartyGuard.IsSelf(counterparty.Name, us.NameEn);
        var taxIsUs = ThaiTaxId.IsValid(counterparty.TaxId) && ThaiTaxId.Same(counterparty.TaxId, us.TaxId);
        if (!nameIsUs && !taxIsUs) return counterparty;
        return new OcrPartyBlock(
            Name: nameIsUs ? null : counterparty.Name,
            TaxId: taxIsUs ? null : counterparty.TaxId,
            Address: (nameIsUs || taxIsUs) && AddressLooksLikeOurs(counterparty.Address, us.Address) ? null : counterparty.Address,
            BranchCode: nameIsUs || taxIsUs ? null : counterparty.BranchCode);
    }

    private static string? Pick(string? preferred, string? fallback)
        => string.IsNullOrWhiteSpace(preferred) ? (string.IsNullOrWhiteSpace(fallback) ? null : fallback) : preferred;

    /// <summary>ค่านี้ถูกยกไปอีกฝั่งแล้ว (ฝั่งปลายทางว่าง จึงรับไป) — ต้องไม่ค้างซ้ำสองที่</summary>
    private static bool MovedAway(string? source, string? destinationAfter)
        => !string.IsNullOrWhiteSpace(source) && string.Equals(source, destinationAfter, StringComparison.Ordinal);

    private static IReadOnlyList<string> Add(List<string> reasons, string r) { reasons.Add(r); return reasons; }
}
