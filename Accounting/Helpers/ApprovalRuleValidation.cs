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
}
