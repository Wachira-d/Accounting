namespace Accounting.Helpers;

/// <summary>
/// **บัญชีธนาคารที่รับเงินของรอบโอน — ระบบไม่เลือกให้เอง** (review198-D D-03 · CLAUDE.md F2 ข้อ 3 "ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ")
///
/// <para>═══ ทำไม ═══ บัญชีนี้คือขาเดบิตธนาคารของ JE รอบโอน (<c>SettlementAccountResolver</c> บทบาท <c>bank</c>) และบัญชีที่ใช้หารายการเดินบัญชี
/// ตอนจับคู่เงินเข้า · หน้าเว็บเดิมเลือก "บัญชีแรกตามชื่อธนาคาร" ให้เงียบ ๆ ⇒ บริษัทที่มี ≥2 บัญชี กดนำเข้าโดยไม่แตะ = เงินลงผิดบัญชี
/// และจับคู่เงินเข้าไม่เจอ โดยไม่มีที่ไหนแสดงว่าผูกบัญชีไหน</para>
/// <para>กติกา: มีเงินโอนเข้า (&gt; 0) ⇒ ต้องเลือกเองตอนนำเข้า · ค่าเริ่มต้นให้เฉพาะเมื่อบริษัทมีบัญชีที่เปิดใช้<b>บัญชีเดียว</b> (ไม่มีอะไรให้เดา) ·
/// เปลี่ยนได้ที่หน้ารอบโอนตราบที่รอบยังแก้ได้ (<c>ISettlementImportService.SetBankAccountAsync</c>)</para>
///
/// <para>G6: pure</para>
/// </summary>
public static class SettlementBankAccountRule
{
    /// <summary>ข้อความบล็อกการสร้างรอบโอนใหม่เมื่อยังไม่ได้เลือกบัญชี (null = ผ่าน) — รอบที่ไม่มีเงินโอน (0) ไม่มีขาธนาคาร ไม่ต้องเลือก</summary>
    public static string? MissingForImport(decimal netPayout, Guid? bankAccountId)
        => netPayout != 0m && bankAccountId is null
            ? "เลือกบัญชีธนาคารที่เงินรอบนี้เข้าจริง (ตามสเตทเมนต์ธนาคาร) — ระบบไม่เลือกให้เอง เพราะบัญชีนี้คือบัญชีที่ใบสำคัญรอบโอนเดบิต "
              + "และบัญชีที่ใช้หาเงินเข้าตอนจับคู่ · ยังไม่มีบัญชีในรายการ ให้เพิ่มที่ ตั้งค่า → บัญชีธนาคาร"
            : null;

    /// <summary>ค่าเริ่มต้นของช่อง "บัญชีธนาคารที่รับเงิน" — เฉพาะเมื่อมีบัญชีที่เปิดใช้บัญชีเดียว · ≥2 หรือไม่มีเลย = null (ผู้ใช้เลือกเอง)</summary>
    public static Guid? DefaultChoice(IReadOnlyCollection<Guid> activeBankAccountIds)
        => activeBankAccountIds.Count == 1 ? activeBankAccountIds.First() : null;
}
