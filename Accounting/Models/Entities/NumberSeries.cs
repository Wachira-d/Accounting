using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// ตั้งค่าเลขที่เอกสาร Running Number (กำหนดรูปแบบได้)
/// เทียบเท่า FlowAccount & PEAK: Document numbering
/// </summary>
public class NumberSeries : TenantEntity
{
    public DocumentType DocumentType { get; set; }
    public string Prefix { get; set; } = null!;         // e.g. "INV", "QT", "REC"
    public string? Suffix { get; set; }
    public string Format { get; set; } = Accounting.Helpers.NumberSeriesFieldPolicy.DefaultFormat; // ⚠️ ไม่มีผลกับการออกเลข (S-20 · NumberSeriesFieldPolicy)
    public int CurrentNumber { get; set; } = 0;
    public int ResetPeriod { get; set; } = 0;            // 0=never, 1=monthly, 12=yearly
    public DateTime? LastResetDate { get; set; }
    public bool IsActive { get; set; } = true;
}
