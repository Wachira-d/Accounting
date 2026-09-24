namespace Accounting.Helpers;

/// <summary>ผลการตัดสินว่า "ใบที่สแกน (สาขาที่อ่านได้) ควรผูกกับผู้ติดต่อแถวไหน"</summary>
public enum OcrVendorBranchOutcome
{
    /// <summary>ไม่มีผู้ติดต่อที่เลขผู้เสียภาษีตรงเลย — ไปเส้นสร้างใหม่ตามเดิม</summary>
    NoTaxIdMatch,
    /// <summary>อ่านสาขาจากกระดาษไม่ได้ — ใช้กติกาเดิม (แถวเดียว = แถวนั้น · หลายแถว = สำนักงานใหญ่ก่อน)</summary>
    BranchNotRead,
    /// <summary>มีแถวที่สาขาตรงกับกระดาษ</summary>
    ExactBranch,
    /// <summary>ไม่มีแถวสาขาตรง แต่มีแถวที่ <b>ไม่เคยระบุสาขา</b> (ข้อมูลเก่า) — ใช้แถวนั้นตามเดิม
    /// (ตัวเติม <c>[Enrich]</c> จะเติมสาขาจากกระดาษลงช่องที่ว่างให้เอง)</summary>
    AdoptBlankBranchRow,
    /// <summary>ทุกแถวระบุสาขา<b>อื่น</b>ไว้ชัด — ผูกกับแถวสำนักงานใหญ่/แถวแรกตามพฤติกรรมเดิม (สาขาของใบ
    /// ยังถูกบันทึกที่เอกสาร <c>SupplierBranchCode</c> ตามเดิม) แต่ <b>ห้ามเอาข้อมูลจากกระดาษไปเติม/ทับแถวนั้น</b>
    /// (คนละสาขา = คนละที่อยู่). <b>ไม่สร้างแถวสาขาใหม่อัตโนมัติ</b> — รอเจ้าของตัดสิน (รายงานทีม C คำถามที่ 1:
    /// ร้านค้าปลีกหลายร้อยสาขาจะกลายเป็นผู้ติดต่อหลายร้อยแถว)</summary>
    OtherBranchRow,
}

/// <summary>คำตัดสิน — <see cref="ContactId"/> คือแถวที่จะผูก (null เมื่อต้องสร้างใหม่/ไม่มีคู่)</summary>
public sealed record OcrVendorBranchPick(
    OcrVendorBranchOutcome Outcome,
    Guid? ContactId,
    string? ScannedBranch,
    string Trace)
{
    /// <summary>ข้อมูลจากกระดาษ (ที่อยู่ · สาขา · เบอร์) เติมลงแถวที่ผูกได้ไหม — ได้เฉพาะเมื่อแถวนั้น
    /// เป็นสาขาเดียวกับกระดาษ (หรือไม่รู้สาขาทั้งสองฝั่ง). แถว "คนละสาขา" ห้ามแตะ มิฉะนั้นที่อยู่สาขาที่ 8
    /// ไปทับที่อยู่สำนักงานใหญ่ถาวร · <see cref="OcrVendorBranchOutcome.NoTaxIdMatch"/> = ตัวนี้ไม่ได้ผูก
    /// (แถวถูกจับด้วยชื่อทีหลัง) ⇒ คงพฤติกรรมเดิม</summary>
    public bool MayEnrichMatchedRow => Outcome is OcrVendorBranchOutcome.ExactBranch
        or OcrVendorBranchOutcome.AdoptBlankBranchRow
        or OcrVendorBranchOutcome.BranchNotRead
        or OcrVendorBranchOutcome.NoTaxIdMatch;
}

/// <summary>
/// "เลขผู้เสียภาษีเดียว หลายสาขา" — เลือกแถวผู้ติดต่อให้ตรงสาขาที่อ่านได้จากใบกำกับ (รอบ 190 ทีม C ข้อ 5)
///
/// <para><b>โครงสร้างที่ระบบถืออยู่แล้ว</b>: ผู้ติดต่อ 1 แถว = 1 (เลขผู้เสียภาษี, สาขา) —
/// <c>DocumentService.FindDuplicateContactAsync</c> ถือว่าเลขเดียวกันคนละสาขา = คนละแถว (ถูกต้องตาม
/// ประกาศอธิบดีฯ ฉบับที่ 199: ใบกำกับต้องระบุสาขาที่ออกใบ). ตัวนี้ไม่ได้เปลี่ยนโครงสร้าง แค่ <b>เพิ่มขั้นคิด</b>
/// ให้เส้น OCR เดินตามโครงสร้างนั้น</para>
///
/// <para><b>ที่พลาดก่อนหน้า</b> (ใบ B Radisson — "สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8"): เดิมถ้าเลขภาษีตรง
/// <b>แถวเดียว</b> ระบบหยิบแถวนั้นทันทีโดยไม่ดูสาขา ⇒ ใบของสาขาที่ 8 ผูกกับแถวสำนักงานใหญ่ และตัวเติม
/// <c>[Enrich]</c> อาจเอาที่อยู่จากใบสาขาไปทับที่อยู่แถวสำนักงานใหญ่ (ถ้าแถวนั้นสร้างโดย OCR) —
/// และไม่มีทางได้แถวของสาขาที่ 8 เลย</para>
///
/// <para><b>ขั้นคิดที่เพิ่ม</b> (ไม่รื้อ): แถวที่ผูกยังเป็นแถวเดิมทุกกรณี (ไม่มีแถวสาขาตรง → สำนักงานใหญ่/แถวแรก)
/// แต่ตอนนี้ (ก) เลือกแถวที่สาขาตรงก่อนเสมอ แม้มีแถวเดียว (ข) รู้ว่าแถวที่ผูก "คนละสาขา" กับกระดาษ ⇒ ห้ามเติม
/// ข้อมูลข้ามสาขา (ดู <see cref="OcrVendorBranchPick.MayEnrichMatchedRow"/>) (ค) บอกผู้ใช้ตรง ๆ ใน trace.
/// การ<b>สร้างแถวสาขาใหม่อัตโนมัติ</b>เป็นคำถามเจ้าของ — ไม่ทำเอง</para>
/// </summary>
public static class OcrVendorBranchContact
{
    /// <summary>ผู้ติดต่อที่เลขผู้เสียภาษีตรงกับกระดาษ (กรองบริษัทตัวเองออกแล้ว)</summary>
    public sealed record Candidate(Guid Id, string? Name, string? BranchCode);

    /// <param name="candidates">แถวที่เลขผู้เสียภาษีตรง (normalize แล้ว) — ลำดับไม่มีผล</param>
    /// <param name="scannedBranch">สาขาผู้ขายที่อ่านได้ (ว่าง/ผิดรูป = อ่านไม่ได้)</param>
    public static OcrVendorBranchPick Decide(IReadOnlyList<Candidate> candidates, string? scannedBranch)
    {
        if (candidates == null || candidates.Count == 0)
            return new(OcrVendorBranchOutcome.NoTaxIdMatch, null, null, "");

        // ลำดับกำหนดได้เสมอ: สำนักงานใหญ่ก่อน → รหัสสาขาน้อย → Id (เดิมใช้กติกานี้กับเคสหลายแถวอยู่แล้ว)
        var ordered = candidates
            .OrderBy(c => TaxBranchCode.Normalize(c.BranchCode), StringComparer.Ordinal)
            .ThenBy(c => c.Id)
            .ToList();
        var headOrFirst = ordered[0];

        var scanned = ScannedCode(scannedBranch);
        if (scanned == null)
        {
            var pick = candidates.Count == 1 ? candidates[0] : headOrFirst;
            return new(OcrVendorBranchOutcome.BranchNotRead, pick.Id, null,
                candidates.Count > 1
                    ? $"[Branch] TaxID ตรง {candidates.Count} รายชื่อ แต่อ่านสาขาบนกระดาษไม่ได้ — ใช้ '{pick.Name}' "
                      + $"({TaxBranchCode.Label(pick.BranchCode)}) โปรดตรวจสอบ"
                    : "");
        }

        // (1) แถวที่ระบุสาขาตรงกับกระดาษไว้ชัด
        var exact = ordered.FirstOrDefault(c => HasExplicitBranch(c.BranchCode)
            && TaxBranchCode.Normalize(c.BranchCode) == scanned);
        // (1b) กระดาษบอกสำนักงานใหญ่ + แถวที่ไม่เคยระบุสาขา = สำนักงานใหญ่ตามค่าเริ่มต้นสากลของระบบ
        exact ??= scanned == TaxBranchCode.HeadOffice
            ? ordered.FirstOrDefault(c => !HasExplicitBranch(c.BranchCode))
            : null;
        if (exact != null)
            return new(OcrVendorBranchOutcome.ExactBranch, exact.Id, scanned,
                candidates.Count > 1
                    ? $"[Branch] TaxID ตรง {candidates.Count} รายชื่อ — เลือกตามสาขา {TaxBranchCode.Label(scanned)} ({scanned}): '{exact.Name}'"
                    : "");

        // (2) ไม่มีแถวสาขาตรง แต่มีแถวที่ไม่เคยระบุสาขา → ใช้แถวนั้น (พฤติกรรมเดิม — ตัวเติมจะเติมสาขาให้)
        var blank = ordered.FirstOrDefault(c => !HasExplicitBranch(c.BranchCode));
        if (blank != null)
            return new(OcrVendorBranchOutcome.AdoptBlankBranchRow, blank.Id, scanned,
                $"[Branch] ผู้ติดต่อ '{blank.Name}' ยังไม่เคยระบุสาขา — ผูกใบ{TaxBranchCode.Label(scanned)} ({scanned}) กับรายนี้");

        // (3) ทุกแถวเป็นสาขาอื่นที่ระบุไว้ชัด — ผูกแถวเดิม (พฤติกรรมเดิม) แต่ห้ามเติมข้ามสาขา + บอกตรง ๆ
        return new(OcrVendorBranchOutcome.OtherBranchRow, headOrFirst.Id, scanned,
            $"[Branch] ⚠ กระดาษออกโดย {TaxBranchCode.Label(scanned)} ({scanned}) แต่ในระบบมีผู้ติดต่อเลขนี้เฉพาะ "
            + string.Join(" · ", ordered.Select(c => TaxBranchCode.Label(c.BranchCode)).Distinct())
            + $" — ผูกกับ '{headOrFirst.Name}' ({TaxBranchCode.Label(headOrFirst.BranchCode)}) · สาขาของใบบันทึกที่เอกสาร "
            + "และไม่นำที่อยู่/สาขาจากกระดาษไปแก้ผู้ติดต่อรายนั้น · ถ้าต้องการแยกผู้ติดต่อของสาขานี้ ให้สร้างที่หน้าผู้ติดต่อ "
            + "(กรอกเลขผู้เสียภาษี + รหัสสาขา แล้วกด \"ดึงข้อมูล DBD\" จะได้ที่อยู่ของสาขานั้น)");
    }

    /// <summary>สาขาที่อ่านได้ → รหัส 5 หลัก · อ่านไม่ได้/ผิดรูป → null ("ไม่รู้" ห้ามกลายเป็น 00000)</summary>
    private static string? ScannedCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return TaxBranchCode.TryNormalize(raw, out var code, out _) ? code : null;
    }

    /// <summary>แถวนี้ระบุสาขาไว้ชัด (มีตัวเลข) — ว่าง = "ไม่เคยระบุ" (ข้อมูลเก่า)</summary>
    private static bool HasExplicitBranch(string? code)
        => !string.IsNullOrWhiteSpace(code) && code.Any(ch => ch >= '0' && ch <= '9');
}
