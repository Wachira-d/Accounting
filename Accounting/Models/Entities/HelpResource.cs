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

    /// <summary>แสดงบน**หน้าเว็บสาธารณะ** (`/docs.html`) ให้คนที่ยังไม่ได้สมัครดูได้ด้วย
    ///
    /// <para>default = <c>true</c> ตามที่เจ้าของระบบสั่ง (2026-09-08): คู่มือสอน
    /// ใช้งานคือ**สื่อการสอน** ไม่ใช่ข้อมูลของผู้เช่า — คนที่กำลังตัดสินใจสมัคร
    /// ต้องอ่านได้ก่อน · ช่องนี้ยังอยู่เพื่อให้แอดมิน**ปิดเป็นรายชิ้น**เมื่อเนื้อหา
    /// ชิ้นนั้นอ้างอิงข้อมูลภายใน (เช่น ขั้นตอนเฉพาะของลูกค้ารายหนึ่ง)</para>
    ///
    /// <para>เดิม default = false ทำให้เส้นสาธารณะไม่มีเนื้อหาเลยสักชิ้น ⇒ หน้า
    /// `/docs.html` โชว์ "ยังไม่มีเนื้อหาที่เปิดสาธารณะ" ตลอดกาล</para></summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>ชื่อสั้นสำหรับลิงก์ตรง (`/docs.html?a=<slug>`) และเป็น**ตัวตน
    /// ที่คงที่**ของเนื้อหาที่ระบบ seed มาให้ — ตัว seeder ใช้ค้นว่าเคยลงแล้วหรือยัง
    /// (idempotent) แทนการเทียบชื่อเรื่องซึ่งแอดมินแก้ได้</summary>
    public string? Slug { get; set; }

    /// <summary>เนื้อหาเต็มของบทความ (Markdown อย่างง่าย: `## หัวข้อ` · `- ข้อ` ·
    /// `1. ข้อ` · **ตัวหนา** · `` `โค้ด` ``) — <see cref="Description"/> เป็นแค่
    /// สรุปหนึ่งบรรทัดบนการ์ด ไม่ใช่ตัวเนื้อหา
    ///
    /// <para>ก่อนมีช่องนี้ บทความถูกยัดลง <c>Description</c> ทั้งก้อน ⇒ การ์ดใน
    /// รายการโชว์ข้อความยาวทั้งดุ้น และไม่มีที่ให้เขียนคู่มือจริง</para></summary>
    public string? Body { get; set; }
    public int SortOrder { get; set; }

    /// <summary>ยอดเปิดดู — ใช้จัดลำดับ "ที่คนดูมากที่สุด" และดูว่าเนื้อหาไหนไม่มีคนใช้</summary>
    public int ViewCount { get; set; }
}
