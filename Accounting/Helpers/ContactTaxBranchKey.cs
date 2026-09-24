using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ผู้ติดต่อที่ถือเลขผู้เสียภาษีเดียวกัน (ข้อมูลเท่าที่ตัวจับคู่ต้องใช้)</summary>
public sealed record ContactKeyCandidate(Guid Id, string? TaxId, string? BranchCode);

/// <summary>จับคู่ได้ด้วยเหตุอะไร — ให้ผู้เรียก log/ตัดสินต่อ (เช่น ห้ามเขียนทับสาขาของแถวที่ไม่ได้ตรงสาขา)</summary>
public enum ContactKeyBasis
{
    /// <summary>เลขภาษี + สาขาตรงกัน (แถวที่ไม่เคยระบุสาขา ≡ 00000 ตาม <c>FindDuplicateContactAsync</c>)</summary>
    ExactBranch,
    /// <summary>payload ระบุสาขา N · ไม่มีแถว N แต่มีแถวเลขเดียวกันที่<b>ไม่เคยระบุสาขา</b> (ข้อมูลเก่า) — ผูกแถวนั้นแทนการสร้างซ้ำ</summary>
    UnspecifiedBranchRow,
    /// <summary>payload ไม่ระบุสาขา → แถวสำนักงานใหญ่ (หรือแถวที่ไม่เคยระบุสาขา)</summary>
    HeadOfficeForMissingBranch,
    /// <summary>payload ไม่ระบุสาขา · เลขนี้มีแถวเดียว (สาขาอะไรก็ได้) — พฤติกรรมเดิม ไม่สร้างแถวใหม่</summary>
    OnlyRowForMissingBranch,
    /// <summary>payload ไม่ระบุสาขา · หลายแถว ไม่มี สนญ. → แถวรหัสสาขาต่ำสุด (แน่นอน ไม่ขึ้นกับลำดับจากฐาน)</summary>
    LowestBranchForMissingBranch,
    /// <summary>เลขไม่ใช่ 13 หลัก (ต่างประเทศ/เลขเก่า) — เทียบข้อความตรงตัวตามเดิม ไม่ดูสาขา</summary>
    RawTaxIdEquality,
}

/// <summary>ผลจับคู่ · <see cref="TaxIdExists"/> = มีผู้ติดต่อเลขนี้อยู่แล้ว (แม้สาขาไม่ตรง) —
/// ผู้เรียกต้อง<b>ไม่</b>ถอยไปจับคู่ด้วยชื่อ/อีเมลเมื่อเป็นจริง (จะได้แถวสาขาอื่นของเลขเดียวกันกลับมา)</summary>
public readonly record struct ContactKeyMatch(Guid? ContactId, ContactKeyBasis? Basis, bool TaxIdExists)
{
    public bool Found => ContactId != null;
}

/// <summary>
/// **คีย์ผู้ติดต่อ = เลขผู้เสียภาษี + รหัสสาขา** — ตัวจับคู่ตัวเดียวของทุกทางเข้าที่หาผู้ติดต่อด้วยเลขภาษี
/// (รอบ 193 · คำตัดสินเจ้าของข้อ 20 · ทีม C รอบ 190 คำถาม 4)
///
/// <para><b>ทำไม</b>: ตามประกาศอธิบดีฯ 199 หนึ่งเลขผู้เสียภาษีมีได้หลายสถานประกอบการ และระบบเก็บ 1 แถว
/// ผู้ติดต่อ = 1 (เลข, สาขา) อยู่แล้ว (<c>DocumentService.FindDuplicateContactAsync</c>). แต่ทางเข้าอื่น 8 จุด
/// (integration ลูกค้า/ใบขาย/ผู้ขาย · นำเข้าผู้ติดต่อ/ลูกหนี้ยกมา/เอกสาร · POS ออกใบกำกับเต็มรูป · ที่พัก ·
/// API v1 resolve) หาด้วย <c>c.TaxId == x</c> อย่างเดียว ⇒ หยิบแถวไหนก็ได้ของเลขนั้น แล้ว integration ยัง
/// <b>เขียนรหัสสาขาของ payload ทับแถวที่หยิบมา</b> ⇒ แถวสำนักงานใหญ่กลายเป็นสาขา 8 เงียบ ๆ</para>
///
/// <para><b>กติกา</b> (ทุกข้อเรียงแถวแน่นอน — ผลไม่ขึ้นกับลำดับที่ฐานคืน):</para>
/// <list type="number">
/// <item>payload ระบุสาขา → แถวที่สาขาตรง (แถวไม่ระบุสาขา ≡ 00000) · ไม่มี → แถวที่<b>ไม่เคยระบุสาขา</b> (ข้อมูลเก่า —
///   กันสร้างแถวซ้ำ) · ไม่มีอีก → ไม่พบ (<see cref="ContactKeyMatch.TaxIdExists"/> = true) ⇒ ผู้เรียกสร้างแถวของสาขานั้น</item>
/// <item>payload <b>ไม่</b>ระบุสาขา ("ไม่รู้" ไม่ใช่ 00000 — ผู้เรียกที่ความหมายเดิมคือ สนญ. ต้องส่ง "00000" เอง) →
///   แถว สนญ./ไม่ระบุสาขา → แถวเดียวที่มี → แถวรหัสต่ำสุด (ไม่สร้างแถวใหม่ให้ข้อมูลเดิม)</item>
/// <item>รหัสสาขาผิดรูป ("8A") = ไม่รู้ → กติกาข้อ 2</item>
/// <item>เลขไม่ใช่ 13 หลัก → เทียบข้อความตรงตัวแบบเดิม ไม่ดูสาขา</item>
/// </list>
/// </summary>
public static class ContactTaxBranchKey
{
    public static ContactKeyMatch Pick(IEnumerable<ContactKeyCandidate> candidates, string? taxId, string? branchCode)
    {
        var raw = taxId?.Trim();
        if (string.IsNullOrEmpty(raw) || candidates == null) return default;
        var tax = Digits(raw);

        if (tax.Length != 13)
        {
            // เลขเก่า 10 หลัก/ต่างประเทศ: ตรงตัว หรือ (≥ 10 หลัก) ตัวเลขล้วนตรงกัน — ตามตัวจับคู่เดิมของ integration
            var legacy = candidates.Where(c => string.Equals(c.TaxId?.Trim(), raw, StringComparison.Ordinal)
                    || (tax.Length >= 10 && Digits(c.TaxId) == tax))
                .OrderBy(c => c.Id).FirstOrDefault();
            return legacy == null ? default : new ContactKeyMatch(legacy.Id, ContactKeyBasis.RawTaxIdEquality, true);
        }

        var same = candidates.Where(c => Digits(c.TaxId) == tax)
            .OrderBy(c => IsSpecified(c.BranchCode) ? TaxBranchCode.Normalize(c.BranchCode) : TaxBranchCode.HeadOffice, StringComparer.Ordinal)
            .ThenBy(c => IsSpecified(c.BranchCode) ? 1 : 0)      // ระบุ 00000 ชัดเจน ชนะแถวที่ไม่เคยระบุ
            .ThenBy(c => c.Id)
            .ToList();
        if (same.Count == 0) return default;

        var wanted = WantedBranch(branchCode);
        if (wanted != null)
        {
            var exact = same.FirstOrDefault(c => IsSpecified(c.BranchCode) && TaxBranchCode.Normalize(c.BranchCode) == wanted);
            if (exact != null) return new ContactKeyMatch(exact.Id, ContactKeyBasis.ExactBranch, true);
            var unspecified = same.FirstOrDefault(c => !IsSpecified(c.BranchCode));
            if (unspecified != null)
                return new ContactKeyMatch(unspecified.Id,
                    wanted == TaxBranchCode.HeadOffice ? ContactKeyBasis.ExactBranch : ContactKeyBasis.UnspecifiedBranchRow, true);
            return new ContactKeyMatch(null, null, true);
        }

        var hq = same.FirstOrDefault(c => !IsSpecified(c.BranchCode) || TaxBranchCode.IsHeadOffice(c.BranchCode));
        if (hq != null) return new ContactKeyMatch(hq.Id, ContactKeyBasis.HeadOfficeForMissingBranch, true);
        if (same.Count == 1) return new ContactKeyMatch(same[0].Id, ContactKeyBasis.OnlyRowForMissingBranch, true);
        return new ContactKeyMatch(same[0].Id, ContactKeyBasis.LowestBranchForMissingBranch, true);
    }

    /// <summary>
    /// ค้นในฐานแล้วตัดสินด้วย <see cref="Pick"/> — <paramref name="scope"/> คือชุดผู้ติดต่อที่ผู้เรียกยอมรับ
    /// (global filter <c>!IsDeleted</c> ทำงานอยู่แล้ว · ผู้เรียกเติมเงื่อนไขอื่นได้ เช่น <c>IsActive</c>) ·
    /// กรอง <c>CompanyId</c> ที่นี่เสมอ (กฎ M tenant isolation)
    /// </summary>
    public static async Task<ContactKeyMatch> FindAsync(IQueryable<Contact> scope, Guid companyId,
        string? taxId, string? branchCode, CancellationToken ct = default)
    {
        var raw = taxId?.Trim();
        if (string.IsNullOrEmpty(raw)) return default;
        var digits = Digits(raw);
        // เลขที่เก็บถูก normalize เป็นตัวเลขล้วนแล้ว (migration) — ยังดึงแถวที่มีขีด/ช่องว่างมาเทียบในหน่วยความจำด้วย
        var rows = await scope
            .Where(c => c.CompanyId == companyId && c.TaxId != null && c.TaxId != ""
                && (c.TaxId == raw || c.TaxId == digits || c.TaxId.Contains("-") || c.TaxId.Contains(" ")))
            .Select(c => new ContactKeyCandidate(c.Id, c.TaxId, c.BranchCode))
            .ToListAsync(ct);
        return Pick(rows, raw, branchCode);
    }

    private static string? WantedBranch(string? branchCode)
        => TaxBranchCode.TryNormalize(branchCode, out var code, out _) ? code : null;

    private static bool IsSpecified(string? branchCode) => Digits(branchCode).Length > 0;

    private static string Digits(string? s)
        => new((s ?? "").Where(ch => ch >= '0' && ch <= '9').ToArray());
}
