namespace Accounting.Helpers;

/// <summary>ชนิดของการชน foreign key (PostgreSQL 23503)</summary>
public enum ForeignKeyViolationKind
{
    /// <summary>ไม่รู้ทิศ (ข้อความเซิร์ฟเวอร์เป็นภาษาอื่น และไม่ใช่คำขอลบ)</summary>
    Unknown = 0,
    /// <summary>INSERT/UPDATE อ้างถึงแถวที่ไม่มี — id ค้าง (ถูกลบ) หรือ id ผิดตาราง (เช่น BankAccount.Id ในช่องผังบัญชี)</summary>
    MissingReference = 1,
    /// <summary>DELETE/UPDATE แถวที่ยังมีข้อมูลอื่นอ้างอิงอยู่</summary>
    StillReferenced = 2,
}

/// <summary>
/// **ข้อความถึงผู้ใช้เมื่อชน foreign key — แยก "อ้างถึงสิ่งที่ไม่มี" ออกจาก "ยังถูกอ้างอิง"** (2026-10-14 · ID#17)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ ExceptionMiddleware ตอบทุก 23503 ว่า "รายการนี้ยังถูกอ้างอิงโดยข้อมูลอื่น — ลบหรือเปลี่ยนไม่ได้ ·
/// ปิดการใช้งานแทน" ⇒ ฟอร์มรายการประจำที่ส่ง id บัญชีธนาคารในช่องผังบัญชี (และเงินสดย่อย) ได้ข้อความที่พาไป "ปิดการใช้งาน"
/// ทั้งที่ต้องเลือกค่าในช่องใหม่ · ทิศมาจากข้อความของ PostgreSQL ("insert or update on table …" / "update or delete on table …")
/// ไม่รู้ = ดูว่าคำขอเป็น DELETE ไหม · ไม่รู้ทั้งสองทาง = ข้อความกลาง (ไม่วินิจฉัยเกินที่รู้ — บทเรียน 23505)</para>
/// <para>G6: pure</para>
/// </summary>
public static class ForeignKeyViolation
{
    public static ForeignKeyViolationKind Classify(string? serverMessageText, string? httpMethod)
    {
        var m = (serverMessageText ?? "").TrimStart();
        if (m.StartsWith("insert or update on table", StringComparison.OrdinalIgnoreCase)) return ForeignKeyViolationKind.MissingReference;
        if (m.StartsWith("update or delete on table", StringComparison.OrdinalIgnoreCase)) return ForeignKeyViolationKind.StillReferenced;
        return string.Equals(httpMethod, "DELETE", StringComparison.OrdinalIgnoreCase)
            ? ForeignKeyViolationKind.StillReferenced
            : ForeignKeyViolationKind.Unknown;
    }

    private static readonly IReadOnlyDictionary<string, string> TableLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ChartOfAccounts"] = "ผังบัญชี",
        ["BankAccounts"] = "บัญชีธนาคาร",
        ["Contacts"] = "ผู้ติดต่อ",
        ["Products"] = "สินค้า",
        ["ProductVariants"] = "ตัวเลือกสินค้า",
        ["Warehouses"] = "คลังสินค้า",
        ["Branches"] = "สาขา",
        ["Projects"] = "โครงการ",
        ["Employees"] = "พนักงาน",
        ["Documents"] = "เอกสาร",
        ["AccountingDimensions"] = "ศูนย์ต้นทุน/มิติบัญชี",
        ["Departments"] = "แผนก",
        ["JournalEntries"] = "รายการบัญชี",
        ["Users"] = "ผู้ใช้",
        ["FixedAssets"] = "สินทรัพย์ถาวร",
        ["PettyCashFunds"] = "กองทุนเงินสดย่อย",
    };

    /// <summary>ชื่อไทยของตารางปลายทางจากชื่อ constraint ของ EF (<c>FK_{ตารางลูก}_{ตารางแม่}_{คอลัมน์}</c>) · ไม่รู้จัก = null</summary>
    public static string? TargetLabel(string? constraintName)
    {
        if (string.IsNullOrWhiteSpace(constraintName)) return null;
        var parts = constraintName.Split('_');
        if (parts.Length < 4 || !parts[0].Equals("FK", StringComparison.OrdinalIgnoreCase)) return null;
        return TableLabels.TryGetValue(parts[2], out var label) ? label : null;
    }

    /// <summary>ข้อความถึงผู้ใช้ (ไทย · มีทางไปต่อ · ไม่มี GUID) พร้อมรหัสอ้างอิงของ Error Logs</summary>
    public static string Message(ForeignKeyViolationKind kind, string? constraintName, string refCode)
    {
        var target = TargetLabel(constraintName);
        return kind switch
        {
            ForeignKeyViolationKind.MissingReference =>
                $"บันทึกไม่ได้ — {(target != null ? $"ช่อง{target}" : "บางช่อง")}อ้างถึงรายการที่ไม่มีอยู่ในระบบแล้ว หรือเลือกผิดชนิด "
                + "(เช่น ใส่บัญชีธนาคารในช่องผังบัญชี) · เปิดฟอร์มใหม่ แล้วเลือกค่าในช่องนั้นจากรายการอีกครั้ง "
                + $"(รหัสอ้างอิง {refCode})",
            ForeignKeyViolationKind.StillReferenced =>
                $"รายการนี้ยังถูกอ้างอิงโดยข้อมูลอื่น{(target != null ? $" ({target})" : "")} (เช่น เอกสาร/รายการบัญชี) — ลบหรือเปลี่ยนไม่ได้ · "
                + $"ปิดการใช้งานแทน หรือจัดการรายการที่อ้างอิงก่อน (รหัสอ้างอิง {refCode})",
            _ =>
                "บันทึกไม่ได้ — ข้อมูลที่อ้างถึงกันไม่สอดคล้อง (รายการที่เลือกอาจถูกลบไปแล้ว หรือรายการนี้ยังถูกใช้อยู่ในข้อมูลอื่น) · "
                + $"โหลดหน้าใหม่แล้วเลือกค่าอีกครั้ง ถ้ายังไม่ได้ แจ้งรหัสอ้างอิง {refCode} ให้ผู้ดูแลระบบ",
        };
    }
}
