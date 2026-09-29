namespace Accounting.Helpers;

/// <summary>ตรวจกฎการอนุมัติก่อนบันทึก — ตัวตัดสินเดียวของสร้าง/แก้ (รอบ 200 ทีม R · A07 + G2-09)
///
/// ═══ ที่มา ═══
/// (A07) หน้า approval.html ส่ง payload คนละชุดกับ DTO ทั้งก้อน ⇒ สร้าง/แก้กฎไม่ได้เลย · หน้าใหม่ส่ง <c>name/steps[]</c> ตรงสัญญาแล้ว
/// (G2-09) <c>CreateRuleAsync</c> ไม่ตรวจว่า <c>ApproverUserId</c> เป็นสมาชิกบริษัทนี้ ⇒ ใส่ Guid ของผู้ใช้บริษัทอื่น/ที่ถูกถอดออก
/// ⇒ เอกสารค้างรออนุมัติจากคนที่ไม่มีวันเข้ามาเห็น (เงียบ ๆ) · ขั้นว่าง = กฎที่อนุมัติไม่ได้</summary>
public static class ApprovalRuleValidation
{
    public const int MaxSteps = 5;

    /// <summary>null = บันทึกได้ · ข้อความ (ไทย) = ปฏิเสธพร้อมบอกช่องที่ต้องแก้</summary>
    public static string? Problem(string? name, decimal? minAmount, decimal? maxAmount,
        IReadOnlyList<(int StepOrder, Guid ApproverUserId)> steps, IReadOnlySet<Guid> companyMemberIds)
    {
        if (string.IsNullOrWhiteSpace(name)) return "กรุณาระบุชื่อกฎ";
        if (minAmount is < 0 || maxAmount is < 0) return "ช่วงยอดเงินต้องไม่ติดลบ";
        if (minAmount.HasValue && maxAmount.HasValue && minAmount > maxAmount)
            return "จำนวนขั้นต่ำต้องไม่มากกว่าจำนวนขั้นสูง";
        if (steps.Count == 0) return "กรุณาเลือกผู้อนุมัติอย่างน้อย 1 ขั้น";
        if (steps.Count > MaxSteps) return $"ผู้อนุมัติได้ไม่เกิน {MaxSteps} ขั้น";
        if (steps.Select(s => s.StepOrder).Distinct().Count() != steps.Count) return "ลำดับขั้นอนุมัติซ้ำกัน";
        if (steps.Any(s => s.ApproverUserId == Guid.Empty)) return "กรุณาเลือกผู้อนุมัติให้ครบทุกขั้น";
        if (steps.Any(s => !companyMemberIds.Contains(s.ApproverUserId)))
            return "ผู้อนุมัติบางคนไม่ใช่สมาชิกของบริษัทนี้ (อาจถูกถอดออกแล้ว) — เลือกผู้อนุมัติจากรายชื่อสมาชิกใหม่";
        return null;
    }

    // ── รอบ 200 ทีม RF (R200-X3): แก้กฎแล้วช่องที่หน้าเว็บไม่ได้ส่ง/ไม่ได้แตะต้องไม่ถูกล้างเงียบ ─────────────────────
    // เดิม UpdateRuleAsync เขียน `rule.Description = request.Description` · `rule.ProjectId = request.ProjectId` ตรง ๆ
    // ⇒ หน้า approval.html ที่ส่ง description:null และไม่มี projectId ทำให้กฎที่จำกัดโครงการกลายเป็น "ทุกโครงการ" ทันทีที่กดบันทึก
    // ⇒ เอกสารของโครงการอื่นถูกดึงเข้ากฎที่ไม่ได้ตั้งใจ · กติกาของเรพ: ไม่มีคีย์/null = คงเดิม · "" = ล้าง

    /// <summary>คำอธิบายหลังแก้: <c>null</c> (ไม่ได้ส่ง) = คงเดิม · ว่าง/ช่องว่าง = ล้าง · อื่น ๆ = ค่าใหม่ (ตัดช่องว่างหัวท้าย)</summary>
    public static string? PatchDescription(string? current, string? sent)
        => sent == null ? current : string.IsNullOrWhiteSpace(sent) ? null : sent.Trim();

    /// <summary>โครงการหลังแก้: <paramref name="clear"/> = ล้างเป็น "ทุกโครงการ" (Guid? ส่ง "" ไม่ได้ ⇒ ใช้ธงแยก) ·
    /// ส่งค่า = ค่าใหม่ · ไม่ได้ส่ง (null) = คงเดิม · ธงล้างชนะค่าที่ส่งมาพร้อมกันไม่ได้ ⇒ ส่งทั้งคู่ = ใช้ค่าที่ส่ง (ทิศที่ไม่ขยายขอบเขตกฎ)</summary>
    public static Guid? PatchProjectId(Guid? current, Guid? sent, bool clear)
        => sent ?? (clear ? null : current);
}
