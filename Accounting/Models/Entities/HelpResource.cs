using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// **ศูนย์ช่วยเหลือ — เอกสารและวิดีโอสอนใช้งาน**
///
/// <para>เป็นข้อมูล **ระดับแพลตฟอร์ม** ไม่ใช่ของผู้เช่า (ไม่ได้สืบทอด
/// <c>TenantEntity</c>) — ผู้ให้บริการเป็นคนสอน ลูกค้าทุกรายเห็นชุดเดียวกัน
/// จึงไม่มี <c>CompanyId</c> และไม่ต้องมี tenant filter</para>
///
/// <para>แยกสองแกนตามที่เจ้าของระบบสั่ง:
/// <list type="bullet">
/// <item><see cref="Category"/> — "สอนเรื่องอะไร" (บัญชี · ภาษี · เงินเดือน …)</item>
/// <item><see cref="ModuleCode"/> — "ของธุรกิจไหน" (Lodging · Pos · Cms …)
/// null = ใช้ได้กับทุกธุรกิจ</item>
/// </list>
/// สองแกนนี้ไม่ใช่ฟังก์ชันของกันและกัน (เช่น "ออกใบกำกับจากการเข้าพัก" =
/// หมวดภาษี + โมดูลที่พัก) จึงต้องเก็บแยก ไม่ใช่ยุบเป็นหมวดเดียว</para>
/// </summary>
public class HelpResource : BaseEntity
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }

    public HelpCategory Category { get; set; } = HelpCategory.GettingStarted;

    /// <summary>โมดูลธุรกิจที่เกี่ยวข้อง — ตรงกับ <c>ApiFeature.ModuleCode</c>
    /// ("Lodging" · "Pos" · "Cms" …) · null/ว่าง = ทุกธุรกิจ</summary>
    public string? ModuleCode { get; set; }

    public HelpResourceKind Kind { get; set; } = HelpResourceKind.Video;

    /// <summary>ผู้ให้บริการวิดีโอ — เดาให้อัตโนมัติจาก URL ตอนบันทึก
    /// (<see cref="HelpMediaEmbed.DetectProvider"/>) ผู้ดูแลไม่ต้องเลือกเอง</summary>
    public HelpMediaProvider Provider { get; set; } = HelpMediaProvider.SelfHosted;

    /// <summary>ลิงก์ต้นฉบับที่ผู้ดูแลวางมา (เก็บ**ตามที่วาง** เพื่อให้แก้/ตรวจสอบ
    /// ย้อนหลังได้) — URL สำหรับฝังคำนวณตอนอ่าน ไม่เก็บซ้ำ ป้องกัน drift
    /// เมื่อสูตร embed ของ provider เปลี่ยน</summary>
    public string? SourceUrl { get; set; }

    /// <summary>ไฟล์ที่อัปโหลดเข้าระบบ (วิดีโอ/PDF) — path ใต้ /uploads/help-media</summary>
    public string? StoragePath { get; set; }
    public string? FileName { get; set; }
    public long FileSizeBytes { get; set; }

    /// <summary>ความยาววิดีโอเป็นวินาที (0 = ไม่ระบุ) — โชว์ให้ผู้ใช้ตัดสินใจก่อนกด</summary>
    public int DurationSeconds { get; set; }

    public string? ThumbnailUrl { get; set; }

    /// <summary>ยังไม่เผยแพร่ = เห็นเฉพาะแอดมิน (ใช้เตรียมเนื้อหาก่อนปล่อย)</summary>
    public bool IsPublished { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>ยอดเปิดดู — ใช้จัดลำดับ "ที่คนดูมากที่สุด" และดูว่าเนื้อหาไหนไม่มีคนใช้</summary>
    public int ViewCount { get; set; }
}
