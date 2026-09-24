#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""attachment_gate_check.py — ทุก action ที่อ่าน/เขียนไฟล์แนบหรือสแกนต้อง **เรียกด่านกลางจริงในตัวเมธอด**

═══ ที่มา (ฝ่ายค้านรอบ 193 · P6 · S2-C4) ═══
เทสต์ของด่านไฟล์แนบ (U2) ทดสอบแค่ตาราง `AttachmentPermissionScope` (pure) — ถอดการเรียกด่านออกจาก
controller แล้วเทสต์ยังเขียวทุกตัว · repo นี้ไม่มี WebApplicationFactory / DB ในเทสต์ จึงยิง endpoint จริงไม่ได้
และ `write_permission_gate_check` นับ "มีคำนี้อยู่ในช่วงข้อความของ action" ⇒ มองไม่เห็น `UploadReceipt` ที่ไม่มีด่าน
เพราะช่วงข้อความลากไปถึงเมธอด private ข้างล่างที่มีคำว่า `UserRole.Owner` (false negative)
ฝ่ายค้านรอบถัดมา (S2-C4) ลอง mutation แล้วเขียวผิด: ใช้ผลด่านแต่ไม่ return · เรียกด่านด้วยชนิด/ทิศผิด · ห่อด่านไว้ใน if ·
ค้นทางเข้าไม่เจอเมื่อ field เป็น `IFileAttachmentService?` / อ่านผ่าน `Set<FileAttachment>()` / สแกนไฟล์ผ่าน `IOcrService.ScanAsync`

═══ กติกา (ต่อ action ใน TARGETS) ═══
1. หาเมธอดด้วยการ parse (ตัดคอมเมนต์/สตริงออกก่อน) ⇒ ชื่อด่านในคอมเมนต์หรือในสตริง **ไม่นับ**
2. ต้อง `var x = await <ด่าน>(...)` ที่**ระดับบนสุดของตัวเมธอด** (ไม่อยู่ใน if/loop) และเก็บผลไว้ในตัวแปร
3. ตัวแปรนั้นต้องถูกใช้ตัดสินจริง: `if (x …) return …` (ด่านเดี่ยว) หรือ `x.Contains(…)` (ตัวกรองรายการ)
4. อาร์กิวเมนต์ของการเรียกต้องตรงที่กำหนด (ชนิดเจ้าของ · ทิศอ่าน/เขียน) — เรียกด่านด้วยชนิด/ทิศผิดคือไม่มีด่าน
5. การเรียกด่านต้องมา **ก่อน** จุดที่แตะไฟล์/ข้อมูล (sink) ตัวแรกในเมธอด
6. ไม่พบเมธอด = ฟ้อง (เปลี่ยนชื่อ action แล้ว checker ห้ามเขียวเงียบ)

═══ กติกาเชิงโครงสร้างของ OcrController ═══
ทุก action (`[Http…]`) ที่รับ `Guid scanId` ต้องผ่าน `ScanGateAsync` · รับ `Guid fileAttachmentId` ต้องผ่าน
`DenyScanSourceAsync` · รับ `Guid documentId` ต้องผ่าน `DocGateAsync` (หรือ `ScanGateAsync` + `DocGateAsync`) — action ใหม่
ไม่ต้องรอให้ใครจำมาเพิ่มใน TARGETS

═══ ค้นหาทางเข้าใหม่ (allow-list ครบไหม ≠ ผ่านไหม) ═══
ทุกเมธอดใน `Accounting/Controllers/**` ที่เรียก service ไฟล์แนบ (field ชนิด `IFileAttachmentService` / `IFileAttachmentService?`) ·
แตะ `.FileAttachments` / `Set<FileAttachment>` · หรือเรียก `IOcrService` เส้นที่รับ id ของไฟล์/สแกน (`ScanAsync` ·
`DeleteScanAsync` · `LinkScanToExistingDocumentAsync`) ต้องอยู่ใน TARGETS / กติกา OcrController หรือ EXEMPT (พร้อมเหตุผล) ·
ข้าม `.claude` `bin` `obj`

═══ ฝ่ายค้านรอบสอง (R2-C11 — กลายพันธุ์หลุด 7/8 + ฟ้องผิด 1) ═══
A1 ทิศ: action ที่ไม่ใช่ GET ต้องเรียก `ScanGateAsync(..., write: true)` / `DocGateAsync(..., AttachmentAccess.Write, ...)` ·
A2 เงื่อนไขอ่อนลง: การใช้ผลต้องเป็น **รูปตรงตัว** `if (x != null) return` / `if (x is { } d) return` / `if (x is not null) return`
(เติม `&& …` = ไม่นับ) · A3 พารามิเตอร์ชื่ออื่น: action ของ OcrController ที่รับ `Guid` ชื่ออื่นนอก companyId/scanId/
fileAttachmentId/documentId ต้องอยู่ใน `OTHER_GUID_PARAMS` พร้อมเหตุผล · A5/A6/A7/A8 แกนใน service: ด่านชั้นในต้องเรียกตัวตัดสิน pure
(`UnlinkedScanKeys` · `ScanSourceGate` · `ScanOwner` · `PickLinkedOwner` · `ScanFileRelinkable`) **และส่งอาร์กิวเมนต์ที่กำหนด**
(`DELEGATE_ARGS` — เช่น `ScanOwner(file?.EntityType, file?.EntityId, …, fileOwnerExists)`) · A-FP: ด่านที่อยู่บนสุดของ
`try { }` ระดับบนสุดของเมธอด = ยังเป็นด่านระดับบนสุด (ไม่ฟ้อง)

═══ สิ่งที่ checker นี้ **ทำไม่ได้** (เขียนไว้ตรง ๆ — ต้องพึ่งเทสต์ของ helper pure / คนตรวจ) ═══
  • ความหมายของผลตัวตัดสิน — เรียก `UnlinkedScanKeys(...)` ครบแต่เอาผลไปทิ้งแล้วใช้ `ReadAnyOf` ตรง ๆ ทีหลัง · ค่าที่ส่งเข้า
    อาร์กิวเมนต์ถูกชื่อแต่คำนวณผิด (เช่น `fileOwnerExists = true` เสมอ) — ส่วนนี้ล็อกด้วยเทสต์ของ helper pure
    (`AttachmentGateOtherEntriesTests` · `OcrScanFileDisposalTests`) ไม่ใช่ checker (F4 ข้อ 1: ไม่เขียน checker ที่ต้องรู้ชนิด/taint)
  • action ที่รับ id ผ่าน body (record ใน `[FromBody]`) — มองเห็นแค่พารามิเตอร์ของ signature
  • control flow ภายใน `try` (เช่น return ก่อนด่านใน try) — ตรวจแค่ว่าด่านอยู่ก่อน sink ตัวแรก

รันเปล่า ๆ = ตรวจเรพ **และ** negative test (ถอดการเรียกด่านจากไฟล์จริงทีละ target แล้วต้องฟ้อง) ·
`--self-test` = negative test อย่างเดียว
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONTROLLERS = os.path.join(ROOT, "Accounting", "Controllers")
SKIP_DIRS = {".claude", "bin", "obj", ".git", "node_modules"}

# ใช้ผลด่าน = if (x …) return  (บรรทัดเดียวกัน — ตัวอย่าง `if (deny != null) return deny;` · `if (deny is { } d) return …`)
DENY_USE = r"\bif\s*\(\s*{v}\s*(?:!=\s*null|is\s+not\s+null|is\s*\{{\s*\}}\s*\w*)\s*\)\s*return\b"
HIDDEN_USE = r"\b{v}\s*\.\s*Contains\s*\("        # hidden.Contains(x.Id)

OCR = "Accounting/Controllers/OcrController.cs"
FAC = "Accounting/Controllers/FileAttachmentController.cs"

# (ไฟล์, เมธอด, regex ของการเรียกด่าน, regex ของการใช้ผล, sinks, regex ที่อาร์กิวเมนต์ของการเรียกต้องมี [ตรวจบนข้อความจริง])
TARGETS = [
    # ── FileAttachmentController: ทุก action ผ่าน wrapper ที่ส่งต่อไป IAttachmentAccessGate ──
    (FAC, "Upload", r"DenyAttachmentAsync\s*\(", DENY_USE,
     [r"\bReadHeadAsync\s*\(", r"\bProcessAndSaveAsync\s*\(", r"\bFileStream\b", r"\.UploadAsync\s*\("],
     [r"AttachmentAccess\.Write"]),
    (FAC, "GetByEntity", r"DenyAttachmentAsync\s*\(", DENY_USE, [r"\.GetByEntityAsync\s*\("],
     [r"AttachmentAccess\.Read"]),
    (FAC, "Delete", r"DenyAttachmentAsync\s*\(", DENY_USE, [r"\.DeleteAsync\s*\("],
     [r"AttachmentAccess\.Write", r"\batt\.Id\b"]),
    (FAC, "Download", r"DenyAttachmentAsync\s*\(", DENY_USE, [r"\bPhysicalFile\s*\(", r"File\.Exists\s*\("],
     [r"AttachmentAccess\.Read", r"\battachment\.Id\b"]),
    # ── ใบเสร็จนำส่ง (C2) ──
    ("Accounting/Controllers/StatutoryRemittanceController.cs", "UploadReceipt",
     r"\.DenyAttachmentAsync\s*\(", DENY_USE,
     [r"\.CopyToAsync\s*\(", r"\.UploadBytesAsync\s*\(", r"\.AttachReceiptAsync\s*\("],
     [r'"StatutoryRemittance"', r"\bremittanceId\b", r"AttachmentAccess\.Write"]),
    # ── รายการสแกน/คิว/รายงาน (ตัวกรองแถว) ──
    (OCR, "GetResults", r"\.HiddenScanIdsAsync\s*\(", HIDDEN_USE, [r"\bOk\s*\("], []),
    (OCR, "ReviewQueue", r"\.HiddenScanIdsAsync\s*\(", HIDDEN_USE, [r"ScanQualityGrader\s*\.", r"\bOk\s*\("], []),
    # ฝ่ายค้านงาน O1 (C7): รายงานตรวจยอดคืนชื่อผู้ขาย/ชื่อไฟล์/ยอดของสแกนได้ถึง 1,000 ใบ
    (OCR, "GetStoredAmountAudit", r"\.HiddenScanIdsAsync\s*\(", HIDDEN_USE, [r"\bOk\s*\("], []),
    (OCR, "GetStoredAmountAudit", r"\.HiddenDocumentIdsAsync\s*\(", HIDDEN_USE, [r"\bOk\s*\("], []),
    (OCR, "GetOpenPos", r"\.HiddenDocumentIdsAsync\s*\(", HIDDEN_USE, [r"\bOk\s*\("], []),
    (OCR, "PredecessorCandidates", r"\.HiddenDocumentIdsAsync\s*\(", HIDDEN_USE, [r"\bOk\s*\("], []),
    # ── เอกสาร → สแกนที่ผูก (S2-C2) ──
    ("Accounting/Controllers/DocumentController.cs", "GetLinkedScan", r"\.DenyAttachmentAsync\s*\(", DENY_USE,
     [r"\.OcrScanResults\b"], [r'"Document"', r"\bdocumentId\b", r"AttachmentAccess\.Read"]),
]

# กติกาเชิงโครงสร้าง: controller → [(regex ของพารามิเตอร์, regex ของด่าน, regex อาร์กิวเมนต์ที่ต้องมี)]
PARAM_RULES = {
    OCR: [
        (r"\bGuid\s+scanId\b", r"\bScanGateAsync\s*\(", [r"\bscanId\b"]),
        (r"\bGuid\s+fileAttachmentId\b", r"\bDenyScanSourceAsync\s*\(", [r"\bfileAttachmentId\b"]),
        (r"\bGuid\s+documentId\b", r"\bDocGateAsync\s*\(", [r"\bdocumentId\b"]),
    ],
}

# A1 (ฝ่ายค้านรอบสอง): action ที่ไม่ใช่ GET ต้องเรียกด่านทิศเขียน — regex ด่าน → อาร์กิวเมนต์ที่ต้องมีเพิ่มเมื่อ verb ≠ GET
WRITE_ARGS = {
    r"\bScanGateAsync\s*\(": [r"\bwrite\s*:\s*true\b"],
    r"\bDocGateAsync\s*\(": [r"AttachmentAccess\.Write\b"],
}

# A3 (ฝ่ายค้านรอบสอง): `Guid` ชื่ออื่นในพารามิเตอร์ของ action ใน controller ของ PARAM_RULES — ต้องตัดสินทีละตัวพร้อมเหตุผล
# (ไม่งั้น `RawText(Guid companyId, Guid id)` ที่คืนผลสแกนเขียวทันทีเพราะกติกาผูกกับ**ชื่อ** scanId)
KNOWN_GUID_PARAMS = {"companyId", "scanId", "fileAttachmentId", "documentId"}
OTHER_GUID_PARAMS = {
    (OCR, "MatchContact", "contactId"):
        "id ของผู้ติดต่อที่จะผูกกับสแกน (ไม่ใช่สแกน/ไฟล์) — ด่านของสแกนคือ scanId · service กรอง CompanyId ของผู้ติดต่อ",
}

# ด่านชั้นใน — wrapper/ตัวตัดสินต้อง "ส่งต่อจริง" (ไม่ใช่ return null) · (ไฟล์, เมธอด, regex ที่ต้องเรียก, ตรวจบนข้อความจริงไหม)
GATE = "Accounting/Services/Implementations/AttachmentAccessGate.cs"
OCRSVC = "Accounting/Services/Implementations/OcrService.cs"
FAS = "Accounting/Services/Implementations/FileAttachmentService.cs"
SELFCORR = "Accounting/Services/Implementations/Ocr/OcrSelfCorrectionService.cs"
DELEGATES = [
    (FAC, "DenyAttachmentAsync", [r"_gate\s*\.\s*DenyAttachmentAsync\s*\("], False),
    (OCR, "ScanGateAsync", [r"_gate\s*\.\s*DenyScanAsync\s*\([^;]*AttachmentAccess\.Read",
                            r"_gate\s*\.\s*DenyScanAsync\s*\([^;]*AttachmentAccess\.Write"], False),
    (OCR, "DocGateAsync", [r"_gate\s*\.\s*DenyAttachmentAsync\s*\(", r'"Document"'], True),
    (OCR, "DenyScanSourceAsync", [r"_gate\s*\.\s*DenyScanSourceAsync\s*\("], False),
    # ใบเบิก (P1/S2-P1/P2) · สแกน (C3/S2-C1) · ถังก่อนบันทึก (P7/S2-P6)
    (GATE, "DenyAttachmentAsync",
     [r"ExpenseClaimEvidencePolicy\s*\.\s*OwnerMayChange\s*\(", r"ExpenseClaimEvidencePolicy\s*\.\s*ReviewerKeyApplies\s*\(",
      r"\bDenyScanCoreAsync\s*\(", r"AttachmentPermissionScope\s*\.\s*IsUnsavedBucket\s*\(",
      r"AttachmentPermissionScope\s*\.\s*UnsavedFileVisible\s*\(", r"AttachmentPermissionScope\s*\.\s*UnsavedFileRemovable\s*\("],
     False),
    (GATE, "DenyScanCoreAsync", [r"AttachmentPermissionScope\s*\.\s*ScanOwner\s*\(", r"\bDenyAttachmentAsync\s*\(",
                                 r"AttachmentPermissionScope\s*\.\s*UnlinkedScanKeys\s*\(",
                                 r"AttachmentPermissionScope\s*\.\s*PickLinkedOwner\s*\(",
                                 r"AttachmentPermissionScope\s*\.\s*ScanFileOwnerNeedsExistenceCheck\s*\("], False),
    (GATE, "HiddenScanIdsAsync", [r"AttachmentPermissionScope\s*\.\s*ScanOwner\s*\(",
                                  r"AttachmentPermissionScope\s*\.\s*DocumentReadable\s*\(",
                                  r"AttachmentPermissionScope\s*\.\s*PickLinkedOwner\s*\(",
                                  r"AttachmentPermissionScope\s*\.\s*ScanFileOwnerNeedsExistenceCheck\s*\("], False),
    (GATE, "DenyScanSourceAsync", [r"\bDenyAttachmentAsync\s*\(", r"\bDenyScanAsync\s*\(",
                                   r"AttachmentPermissionScope\s*\.\s*ScanSourceGate\s*\("], False),
    # ไฟล์ของรายการอื่นห้ามถูกลบ/ย้ายเพราะสแกน (S2-C1/C2)
    (OCRSVC, "DeleteScanAsync", [r"OcrScanFileDisposal\s*\.\s*Decide\s*\(", r"\bScanFileOwnerExistsAsync\s*\("], False),
    (OCRSVC, "RelinkScanFileToDocumentAsync", [r"AttachmentPermissionScope\s*\.\s*ScanFileRelinkable\s*\(",
                                               r"\bScanFileOwnerExistsAsync\s*\("], False),
    (OCRSVC, "LinkScanToExistingDocumentAsync", [r"AttachmentPermissionScope\s*\.\s*ScanFileRelinkable\s*\(",
                                                 r"\bScanFileOwnerExistsAsync\s*\("], False),
    (OCRSVC, "ScanFileOwnerExistsAsync", [r"AttachmentPermissionScope\s*\.\s*ScanFileOwnerNeedsExistenceCheck\s*\(",
                                          r"\.Documents\b", r"\bCompanyId\s*==\s*companyId\b"], False),
    # A8: relink-on-read ตัดสินด้วยตัวเดียวกับเส้นสร้าง/ผูก (เดิมตรวจแค่ "มีข้อความ EntityType == \"OcrScan\"")
    (FAS, "GetByEntityAsync", [r"AttachmentPermissionScope\s*\.\s*ScanFileRelinkable\s*\(",
                               r"AttachmentPermissionScope\s*\.\s*ScanFileOwnerNeedsExistenceCheck\s*\("], False),
    # R2-C4: งานเก็บกวาดไฟล์สแกนตัดสินด้วย AttachmentRetention ตัวเดียว (สแกนที่ลง JE ห้ามลบ · ไฟล์ที่ถอดแล้วต้องถูกกวาด)
    (SELFCORR, "RunMaintenanceAsync", [r"AttachmentRetention\s*\.\s*ScanFilePurgeable\s*\(",
                                       r"AttachmentRetention\s*\.\s*DeletedScanFileSweepable\s*\("], False),
]

# ด่านชั้นใน: การเรียกตัวตัดสิน **ทุกครั้ง** ในเมธอดต้องส่งอาร์กิวเมนต์เหล่านี้ (ตรวจบนข้อความจริง) — กันถอยแกนแบบ
# `ScanOwner(null, null, …)` (A7) · `Decide(...)` ที่ไม่ส่งว่าเจ้าของไฟล์ยังอยู่ไหม (R2-C1) · (ไฟล์, เมธอด, callee, [arg regex])
DELEGATE_ARGS = [
    (GATE, "DenyScanCoreAsync", r"AttachmentPermissionScope\s*\.\s*ScanOwner\s*\(",
     [r"\bfile\?\.EntityType\b", r"\bfile\?\.EntityId\b", r"\bfileOwnerExists\b"]),
    (GATE, "HiddenScanIdsAsync", r"AttachmentPermissionScope\s*\.\s*ScanOwner\s*\(",
     [r"\bfile\?\.EntityType\b", r"\bfile\?\.EntityId\b", r"\bfileOwnerExists\b"]),
    (GATE, "DenyScanCoreAsync", r"AttachmentPermissionScope\s*\.\s*UnlinkedScanKeys\s*\(",
     [r"\bocrRule\b", r"\baccess\b", r"\bunlinkedEditIsMemberLevel\b"]),
    (GATE, "DenyScanSourceAsync", r"AttachmentPermissionScope\s*\.\s*ScanSourceGate\s*\(", [r"\bfile\.EntityType\b"]),
    (OCRSVC, "DeleteScanAsync", r"OcrScanFileDisposal\s*\.\s*Decide\s*\(", [r"\bfileOwnerExists\s*:"]),
    (OCRSVC, "RelinkScanFileToDocumentAsync", r"AttachmentPermissionScope\s*\.\s*ScanFileRelinkable\s*\(", [r"\bownerExists\b"]),
    (FAS, "GetByEntityAsync", r"AttachmentPermissionScope\s*\.\s*ScanFileRelinkable\s*\(", [r"\bfileOwnerExists\s*:"]),
]

# ทางเข้าที่แตะไฟล์แนบแต่ตั้งใจไม่ผ่านด่านนี้ — ต้องมีเหตุผลทุกแถว (เพิ่มแถว = การตัดสินใจที่ตั้งใจ)
EXEMPT = {
    ("Accounting/Controllers/IntegrationController.cs", "AttachFilesAsync"):
        "แนบไฟล์ให้เอกสารที่คีย์ integration เพิ่งสร้างในคำขอเดียวกัน — ด่านคือคีย์ + scope (write_permission_gate_check)",
    (OCR, "UploadAndScan"):
        "สร้างไฟล์ต้นฉบับของสแกนใหม่จากไฟล์ที่ผู้ใช้เพิ่งอัปโหลด (ไม่ใช่ไฟล์ของรายการอื่น) — สแกนใหม่ได้ด่าน OCR",
    ("Accounting/Controllers/V1/OcrV1Controller.cs", "SaveUploadAsync"):
        "API v1 — ผู้เรียก (Scan) ผ่าน ResolveCallerAsync(\"ocr:write\") ก่อน · ไฟล์เป็นของสแกนใหม่",
    ("Accounting/Controllers/V1/OcrV1Controller.cs", "Scan"):
        "สแกนเฉพาะไฟล์ที่เส้นนี้เพิ่งบันทึกเอง (SaveUploadAsync) — ไม่รับ id ของไฟล์เดิมจากผู้เรียก",
}

# ─────────────────────────── C# lexer (ตัดคอมเมนต์/สตริง คงตำแหน่ง) ───────────────────────────

_STRIP_CACHE = {}


def strip_code(src):
    """แทนเนื้อคอมเมนต์และสตริงด้วยช่องว่าง (คง newline) — ตำแหน่งตัวอักษรเท่าเดิม · จำผลต่อข้อความ (negative test
    เรียกซ้ำกับไฟล์เดิมหลายสิบครั้ง)"""
    hit = _STRIP_CACHE.get(src)
    if hit is None:
        hit = _STRIP_CACHE[src] = _strip_code(src)
    return hit


def _strip_code(src):
    out = list(src)
    n = len(src)
    i = 0

    def blank(a, b):
        for k in range(a, min(b, n)):
            if out[k] != "\n":
                out[k] = " "

    def skip_plain_string(j):   # src[j] == '"'  → คืนตำแหน่งหลัง " ปิด
        j += 1
        while j < n and src[j] != '"' and src[j] != "\n":
            j += 2 if src[j] == "\\" else 1
        return j + 1

    def skip_char(j):
        j += 1
        while j < n and src[j] != "'" and src[j] != "\n":
            j += 2 if src[j] == "\\" else 1
        return j + 1

    while i < n:
        c = src[i]
        if src.startswith("//", i):
            j = src.find("\n", i)
            j = n if j < 0 else j
            blank(i, j); i = j; continue
        if src.startswith("/*", i):
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j); i = j; continue
        # คำนำหน้าสตริง: $ @ $@ @$ (และ raw """)
        m = re.match(r'(\$@|@\$|\$|@)?("""|")', src[i:i + 5])
        if m and (i == 0 or not (src[i - 1].isalnum() or src[i - 1] == "_")):
            prefix, q = m.group(1) or "", m.group(2)
            start = i
            j = i + len(prefix)
            if q == '"""':
                k = src.find('"""', j + 3)
                k = n if k < 0 else k + 3
                blank(start, k); i = k; continue
            verbatim = "@" in prefix
            interp = "$" in prefix
            j += 1
            depth = 0
            while j < n:
                ch = src[j]
                if interp and ch == "{":
                    if src.startswith("{{", j) and depth == 0:
                        j += 2; continue
                    depth += 1; j += 1; continue
                if interp and ch == "}" and depth > 0:
                    depth -= 1; j += 1; continue
                if depth > 0 and ch == '"':
                    j = skip_plain_string(j); continue
                if depth > 0 and ch == "'":
                    j = skip_char(j); continue
                if not verbatim and ch == "\\":
                    j += 2; continue
                if ch == '"':
                    if verbatim and src.startswith('""', j):
                        j += 2; continue
                    j += 1; break
                if ch == "\n" and not verbatim:
                    break
                j += 1
            blank(start, j); i = j; continue
        if c == "'":
            j = skip_char(i)
            blank(i, j); i = j; continue
        i += 1
    return "".join(out)


DECL_LINE = re.compile(r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(?:public|private|protected|internal)\b", re.M)
DECL_NAME = re.compile(r"[\w>\]\?)]\s+([A-Za-z_]\w*)\s*\(")


def _match_paren(s, i):
    """s[i] == '(' → ตำแหน่งหลัง ')' ที่คู่กัน"""
    depth = 0
    while i < len(s):
        if s[i] == "(":
            depth += 1
        elif s[i] == ")":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return len(s)


def methods_ex(stripped):
    """คืน list ของ (name, decl_start, body_start, body_end) — body = ช่วงหลัง signature ถึงปิดเมธอด"""
    res = []
    for dm in DECL_LINE.finditer(stripped):
        line_end = stripped.find("\n", dm.start())
        line_end = len(stripped) if line_end < 0 else line_end
        nm = DECL_NAME.search(stripped, dm.start(), line_end)
        if not nm:
            continue
        p = _match_paren(stripped, nm.end() - 1)
        rest = stripped[p:]
        ws = len(rest) - len(rest.lstrip())
        k = p + ws
        # ข้าม where-clause / base(...) ของ constructor
        while k < len(stripped) and stripped[k] not in "{;=":
            k += 1
        if k >= len(stripped):
            continue
        if stripped[k] == "{":
            depth = 0
            j = k
            while j < len(stripped):
                if stripped[j] == "{":
                    depth += 1
                elif stripped[j] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                j += 1
            res.append((nm.group(1), dm.start(), k, j + 1))
        elif stripped.startswith("=>", k):
            depth = 0
            j = k + 2
            while j < len(stripped):
                ch = stripped[j]
                if ch in "({[":
                    depth += 1
                elif ch in ")}]":
                    depth -= 1
                elif ch == ";" and depth == 0:
                    break
                j += 1
            res.append((nm.group(1), dm.start(), k, j + 1))
    return res


def methods(stripped):
    """คืน list ของ (name, body_start, body_end)"""
    return [(n, a, b) for n, _, a, b in methods_ex(stripped)]


def find_method(stripped, name):
    for nm, a, b in methods(stripped):
        if nm == name:
            return a, b
    return None



# ─────────────────────────── ตัวตรวจ ───────────────────────────

def _human(rx):
    """regex ของชื่อด่าน → ชื่อที่คนอ่าน (\\.DenyScanAsync\\s*\\( → .DenyScanAsync()"""
    return re.sub(r"\\s\*", "", rx).replace("\\.", ".").replace("\\(", "(").replace("\\b", "") + ")"


def _find_gate_assignments(body, gate_re):
    """คืน list ของ (match ของการ assign, ตำแหน่ง '(' ของการเรียก) ใน body ที่ตัดสตริงแล้ว"""
    out = []
    for m in re.finditer(r"(?:\bvar|[\w<>\?\[\],]+)\s+(\w+)\s*=\s*await\s+[\w\.\s]*?(" + gate_re + ")", body):
        out.append((m, m.end() - 1))
    return out


def _depth_at(body, pos):
    """ความลึกปีกกาที่ตำแหน่ง pos (body เริ่มที่ '{' ของเมธอด ⇒ ระดับบนสุด = 1)"""
    return body.count("{", 0, pos) - body.count("}", 0, pos)


def _unconditional(body, pos):
    """ด่านที่ pos วิ่งทุกครั้งที่เมธอดวิ่งไหม — ระดับบนสุด หรือในบล็อก `try { }` ที่ตัวมันเองอยู่ระดับบนสุด
    (ฝ่ายค้านรอบสอง A-FP: ห่อด้วย try/catch ไม่ได้ทำให้ด่านมีเงื่อนไข · if/loop/else/catch ยังนับเป็นเงื่อนไข)"""
    d = _depth_at(body, pos)
    if d == 1:
        return True
    if d != 2:
        return False
    # หา '{' ที่เปิดบล็อกปัจจุบัน
    depth = 0
    i = pos - 1
    while i >= 0:
        if body[i] == "}":
            depth += 1
        elif body[i] == "{":
            if depth == 0:
                break
            depth -= 1
        i -= 1
    if i < 0:
        return False
    return re.search(r"(?:^|[;{}\s])try\s*$", body[:i]) is not None and _depth_at(body, i) == 1


def check_target(text, name, gate_re, use_re, sinks, arg_res=()):
    """คืนข้อความปัญหา (str) หรือ None"""
    s = strip_code(text)
    span = find_method(s, name)
    if span is None:
        return f"ไม่พบเมธอด {name} — เปลี่ยนชื่อแล้วให้แก้ TARGETS ด้วย (ห้ามเขียวเงียบ)"
    a, b = span
    body = s[a:b]
    if not body.startswith("{"):
        return f"{name}: ต้องเป็นเมธอดแบบมีปีกกา (expression-bodied ใส่ด่านไม่ได้)"
    found = _find_gate_assignments(body, gate_re)
    if not found:
        if re.search(gate_re, body):
            return f"{name}: เรียกด่านแต่ไม่ได้ await/เก็บผลไว้ตัดสิน"
        return f"{name}: ไม่เรียกด่านในตัวเมธอด ({_human(gate_re)})"
    problems = []
    for m, paren in found:
        var = m.group(1)
        if not _unconditional(body, m.start()):
            problems.append(f"{name}: ด่าน {_human(gate_re)} อยู่ในบล็อกเงื่อนไข/วนซ้ำ — ต้องเรียกที่ระดับบนสุดของเมธอด")
            continue
        after = body[m.end():]
        if not re.search(use_re.format(v=re.escape(var)), after):
            kind = "if (… ) return" if use_re == DENY_USE else ".Contains(…)"
            problems.append(f"{name}: เรียกด่านแล้วไม่ได้ใช้ผล `{var}` ตัดสินแบบ {kind} (เรียกแล้วทิ้ง = ไม่มีด่าน)")
            continue
        close = _match_paren(s, a + paren)
        args = text[a + paren + 1:close - 1]
        miss = [r for r in arg_res if not re.search(r, args)]
        if miss:
            problems.append(f"{name}: เรียกด่านด้วยอาร์กิวเมนต์ผิด (ต้องมี {', '.join(miss)}) — ชนิด/ทิศผิดคือไม่มีด่าน")
            continue
        first_sink = min((x.start() for r in sinks for x in [re.search(r, body)] if x), default=None)
        if first_sink is not None and first_sink < m.start():
            problems.append(f"{name}: แตะไฟล์/ข้อมูลก่อนเรียกด่าน (sink ที่ตำแหน่งก่อนด่าน)")
            continue
        return None   # มีการเรียกที่ถูกต้องอย่างน้อยหนึ่งครั้ง
    return problems[0]


def check_delegate(text, name, required, raw=False):
    s = strip_code(text)
    span = find_method(s, name)
    if span is None:
        return f"ไม่พบเมธอด {name} (ด่านชั้นใน) — เปลี่ยนชื่อแล้วให้แก้ DELEGATES"
    body = (text if raw else s)[span[0]:span[1]]
    miss = [r for r in required if not re.search(r, body)]
    if miss:
        return f"{name}: ไม่เรียก {', '.join(miss)} ในตัวเมธอด"
    return None


HTTP_ATTR = re.compile(r"\[Http(Get|Post|Put|Delete|Patch)\b")


def check_delegate_args(text, name, callee_re, arg_res):
    """ทุกการเรียก callee ในเมธอด name ต้องมีอาร์กิวเมนต์ครบ (ตรวจบนข้อความจริง ช่วงวงเล็บของการเรียก)"""
    s = strip_code(text)
    span = find_method(s, name)
    if span is None:
        return f"ไม่พบเมธอด {name} (ด่านชั้นใน) — เปลี่ยนชื่อแล้วให้แก้ DELEGATE_ARGS"
    body = s[span[0]:span[1]]
    calls = list(re.finditer(callee_re, body))
    if not calls:
        return f"{name}: ไม่เรียก {_human(callee_re)}"
    for m in calls:
        open_pos = span[0] + m.end() - 1
        close = _match_paren(s, open_pos)
        args = text[open_pos + 1:close - 1]
        miss = [r for r in arg_res if not re.search(r, args)]
        if miss:
            return f"{name}: เรียก {_human(callee_re)} โดยไม่ส่ง {', '.join(miss)} (ถอยแกนของด่าน)"
    return None


def actions(text):
    """(ชื่อ, ข้อความ attribute+signature, span ของ body) ของ action ที่มี [Http…] — ข้อความที่ตัดคอมเมนต์/สตริงแล้ว"""
    s = strip_code(text)
    out = []
    for nm, ds, a, b in methods_ex(s):
        k = ds
        while True:   # ย้อนขึ้นไปเก็บบรรทัด attribute/บรรทัดว่าง (คอมเมนต์ถูกตัดเป็นช่องว่างแล้ว) เหนือ signature
            prev_end = s.rfind("\n", 0, k)
            if prev_end < 0:
                break
            prev_start = s.rfind("\n", 0, prev_end) + 1
            line = s[prev_start:prev_end].strip()
            if line == "" or line.startswith("["):
                k = prev_start
                continue
            break
        head = s[k:a]
        if HTTP_ATTR.search(head):
            out.append((nm, head, (a, b)))
    return out


def check_param_rules(text, rules, rel=OCR):
    problems = []
    for nm, head, _ in actions(text):
        verb = HTTP_ATTR.search(head).group(1)
        for param_re, gate_re, arg_res in rules:
            if re.search(param_re, head):
                args = list(arg_res) + (WRITE_ARGS.get(gate_re, []) if verb != "Get" else [])
                msg = check_target(text, nm, gate_re, DENY_USE, [], args)
                if msg:
                    problems.append(msg + f" — action [{verb}] ที่รับ {param_re.replace(chr(92) + 'b', '').replace(chr(92) + 's+', ' ')} ต้องผ่านด่าน"
                                    + (" ทิศเขียน" if verb != "Get" else ""))
        for g in re.findall(r"\bGuid\??\s+(\w+)", head):
            if g not in KNOWN_GUID_PARAMS and (rel, nm, g) not in OTHER_GUID_PARAMS:
                problems.append(f"{nm}: รับ `Guid {g}` ซึ่งไม่อยู่ในกติกาพารามิเตอร์ — ถ้าเป็น id ของสแกน/ไฟล์/เอกสาร ให้ใช้ชื่อ "
                                "scanId/fileAttachmentId/documentId (ด่านตามชื่อ) · ถ้าไม่ใช่ ให้เพิ่มใน OTHER_GUID_PARAMS พร้อมเหตุผล")
    return problems


def attachment_touchers(text):
    """เมธอดในไฟล์ controller ที่แตะไฟล์แนบ/สแกนจาก id (service ไฟล์แนบ · DbSet FileAttachments · IOcrService ที่รับ id)"""
    s = strip_code(text)
    fields = set(re.findall(r"\bIFileAttachmentService\??\s+(\w+)\s*[;,=)]", s))
    ocr_fields = set(re.findall(r"\bIOcrService\??\s+(\w+)\s*[;,=)]", s))
    pats = [r"\.FileAttachments\b", r"\bSet\s*<\s*(?:[\w\.]*\.)?FileAttachment\s*>"]
    for f in fields:
        pats.append(r"\b" + re.escape(f) + r"\s*\.\s*(UploadAsync|UploadBytesAsync|GetByEntityAsync|GetByIdAsync|DeleteAsync)\s*\(")
    for f in ocr_fields:
        pats.append(r"\b" + re.escape(f) + r"\s*\.\s*(ScanAsync|DeleteScanAsync|LinkScanToExistingDocumentAsync)\s*\(")
    out = []
    for nm, a, b in methods(s):
        if any(re.search(p, s[a:b]) for p in pats):
            out.append(nm)
    return out


def discover(root=ROOT):
    found = set()
    base = os.path.join(root, "Accounting", "Controllers")
    for d, dirs, files in os.walk(base):
        dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
        for f in files:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(d, f)
            rel = os.path.relpath(p, root).replace(os.sep, "/")
            try:
                txt = open(p, encoding="utf-8").read()
            except (OSError, UnicodeDecodeError):
                continue
            for nm in attachment_touchers(txt):
                found.add((rel, nm))
    return found


def read(rel, root=ROOT):
    with open(os.path.join(root, rel), encoding="utf-8") as f:
        return f.read()


def run_checks(root=ROOT, overrides=None, with_discover=True):
    """overrides: {rel: text} แทนเนื้อไฟล์ (ใช้ใน negative test) · with_discover=False ข้ามการกวาดทั้งโฟลเดอร์ controller
    (negative test ของกลายพันธุ์ในไฟล์เดียว — การกวาดทดสอบแยกในข้อ 8)"""
    overrides = overrides or {}
    problems = []

    def text_of(rel):
        return overrides[rel] if rel in overrides else read(rel, root)

    for rel, name, gate, use, sinks, args in TARGETS:
        try:
            msg = check_target(text_of(rel), name, gate, use, sinks, args)
        except FileNotFoundError:
            msg = f"ไม่พบไฟล์ {rel}"
        if msg:
            problems.append(f"{rel}: {msg}")
    for rel, rules in PARAM_RULES.items():
        for msg in check_param_rules(text_of(rel), rules, rel):
            problems.append(f"{rel}: {msg}")
    for rel, name, callee, args in DELEGATE_ARGS:
        try:
            msg = check_delegate_args(text_of(rel), name, callee, args)
        except FileNotFoundError:
            msg = f"ไม่พบไฟล์ {rel}"
        if msg:
            problems.append(f"{rel}: {msg}")
    for rel, name, req, raw in DELEGATES:
        try:
            msg = check_delegate(text_of(rel), name, req, raw)
        except FileNotFoundError:
            msg = f"ไม่พบไฟล์ {rel}"
        if msg:
            problems.append(f"{rel}: {msg}")
    listed = {(t[0], t[1]) for t in TARGETS} | set(EXEMPT)
    for rel in PARAM_RULES:
        for nm, head, _ in actions(text_of(rel)):
            if any(re.search(pr, head) for pr, _, _ in PARAM_RULES[rel]):
                listed.add((rel, nm))
    for rel, nm in (sorted(discover(root)) if with_discover else []):
        if (rel, nm) not in listed:
            problems.append(f"{rel}: {nm} แตะไฟล์แนบ/สแกนแต่ไม่อยู่ใน TARGETS/กติกา OcrController/EXEMPT — "
                            "ต้องเรียก IAttachmentAccessGate แล้วเพิ่มเข้า TARGETS (หรือ EXEMPT พร้อมเหตุผล)")
    return problems


# ─────────────────────────── negative test ───────────────────────────

def _remove_gate_statement(text, name, gate_re):
    """ถอดประโยคที่เรียกด่านออกจากเมธอด (ทั้งประโยคจนถึง ';')"""
    s = strip_code(text)
    span = find_method(s, name)
    if span is None:
        return None
    m = re.search(gate_re, s[span[0]:span[1]])
    if not m:
        return None
    pos = span[0] + m.start()
    st = s.rfind(";", span[0], pos)
    st = max(st, s.rfind("{", span[0], pos)) + 1
    en = s.find(";", pos) + 1
    return text[:st] + "\n" + text[en:]


SYNTH_OK = '''
public class FakeController : ControllerBase
{
    private readonly IFileAttachmentService _files;
    [HttpGet("{id}")]
    public async Task<IActionResult> Download(Guid companyId, Guid id, [FromServices] IAttachmentAccessGate gate)
    {
        var s = $"{(id == Guid.Empty ? "a{" : "b}")}";   // สตริงที่มีปีกกา/อัญประกาศซ้อน
        var deny = await gate.DenyAttachmentAsync(companyId, uid, "X", id, AttachmentAccess.Read, "ดู");
        if (deny is { } d) return StatusCode(d.Status, d.Message);
        var att = await _files.GetByIdAsync(companyId, id);
        return PhysicalFile(att.StoragePath, "x");
    }
}
'''

SYNTH_OCR = '''
public class OcrLike : ControllerBase
{
    private readonly IOcrService _service;
    [HttpGet("{scanId:guid}/good")]
    public async Task<IActionResult> Good(Guid companyId, Guid scanId)
    {
        var deny = await ScanGateAsync(companyId, scanId, "ดู", write: false);
        if (deny != null) return deny;
        return Ok();
    }

    [HttpPost("{scanId:guid}/bad")]
    public async Task<IActionResult> NewActionWithoutGate(Guid companyId, Guid scanId)
        => Ok(await _service.MatchContactAsync(companyId, scanId, Guid.Empty));

    private async Task<IActionResult> Helper(Guid companyId, Guid scanId) => Ok();
}
'''


def self_test(root=ROOT):
    ok = True

    def expect(cond, msg):
        nonlocal ok
        if not cond:
            print("❌ self-test: " + msg)
            ok = False

    # 1. ถอดการเรียกด่านออกจากไฟล์จริงทีละ target ⇒ ต้องฟ้อง target นั้น
    for rel, name, gate, use, sinks, args in TARGETS:
        mutated = _remove_gate_statement(read(rel, root), name, gate)
        expect(mutated is not None, f"หาประโยคด่านใน {rel}:{name} ไม่เจอ (สร้าง mutation ไม่ได้)")
        if mutated is None:
            continue
        expect(check_target(mutated, name, gate, use, sinks, args) is not None, f"ถอดด่านจาก {rel}:{name} แล้วไม่ฟ้อง")
    # 1b. ถอด ScanGateAsync ออกจาก action จริงใน OcrController ทีละตัว ⇒ กติกาเชิงโครงสร้างต้องฟ้อง
    ocr_text = read(OCR, root)
    scan_actions = [nm for nm, head, _ in actions(ocr_text) if re.search(r"\bGuid\s+scanId\b", head)]
    expect(len(scan_actions) >= 20, f"หา action ที่รับ scanId ใน OcrController ได้แค่ {len(scan_actions)} ตัว (parser พัง?)")
    for nm in scan_actions:
        mutated = _remove_gate_statement(ocr_text, nm, r"\bScanGateAsync\s*\(")
        if mutated is None:
            expect(False, f"{nm}: ไม่พบ ScanGateAsync ให้ถอด")
            continue
        expect(any(nm + ":" in p for p in check_param_rules(mutated, PARAM_RULES[OCR])),
               f"ถอด ScanGateAsync จาก {nm} แล้วกติกา OcrController ไม่ฟ้อง")

    gate = r"\.DenyAttachmentAsync\s*\("
    base = SYNTH_OK
    sink = [r"\bPhysicalFile\s*\("]
    expect(check_target(base, "Download", gate, DENY_USE, sink) is None, "ไฟล์ที่ถูกต้อง (มีสตริงปีกกาซ้อน) ถูกฟ้องผิด")
    # 2. ชื่อด่านอยู่ในคอมเมนต์/สตริง ≠ เรียกจริง
    in_comment = base.replace("        var deny = await gate.DenyAttachmentAsync",
                              "        var deny = (AttachmentDenial?)null; // await gate.DenyAttachmentAsync")
    expect(check_target(in_comment, "Download", gate, DENY_USE, sink) is not None, "ด่านที่อยู่ในคอมเมนต์ถูกนับว่าเรียกจริง")
    in_string = base.replace('var deny = await gate.DenyAttachmentAsync(companyId, uid, "X", id, AttachmentAccess.Read, "ดู");',
                             'var deny = "await gate.DenyAttachmentAsync(";')
    expect(check_target(in_string, "Download", gate, DENY_USE, sink) is not None, "ด่านที่อยู่ในสตริงถูกนับว่าเรียกจริง")
    # 3. เรียกแล้วทิ้งผล · ใช้ผลแต่ไม่ return (S2-C4)
    unused = base.replace("        if (deny is { } d) return StatusCode(d.Status, d.Message);\n", "")
    expect(check_target(unused, "Download", gate, DENY_USE, sink) is not None, "เรียกด่านแล้วไม่ใช้ผล ไม่ถูกฟ้อง")
    no_return = base.replace("if (deny is { } d) return StatusCode(d.Status, d.Message);",
                             "if (deny is { } d) { Console.WriteLine(d.Message); }")
    expect(check_target(no_return, "Download", gate, DENY_USE, sink) is not None, "ใช้ผลด่านแต่ไม่ return ไม่ถูกฟ้อง")
    # 4. แตะไฟล์ก่อนเรียกด่าน
    late = base.replace("        var att = await _files.GetByIdAsync(companyId, id);\n", "").replace(
        "        var s = $", "        var att0 = PhysicalFile(\"p\", \"x\");\n        var s = $")
    expect(check_target(late, "Download", gate, DENY_USE, sink) is not None, "แตะไฟล์ก่อนเรียกด่าน ไม่ถูกฟ้อง")
    # 5. ห่อด่านไว้ใน if (S2-C4)
    wrapped = base.replace(
        '        var deny = await gate.DenyAttachmentAsync(companyId, uid, "X", id, AttachmentAccess.Read, "ดู");\n'
        '        if (deny is { } d) return StatusCode(d.Status, d.Message);\n',
        '        if (id == Guid.Empty)\n        {\n'
        '            var deny = await gate.DenyAttachmentAsync(companyId, uid, "X", id, AttachmentAccess.Read, "ดู");\n'
        '            if (deny is { } d) return StatusCode(d.Status, d.Message);\n        }\n')
    expect(check_target(wrapped, "Download", gate, DENY_USE, sink) is not None, "ด่านที่ห่อไว้ใน if ไม่ถูกฟ้อง")
    # 6. อาร์กิวเมนต์ผิด (ชนิด/ทิศ) บนไฟล์จริง (S2-C4)
    rel = "Accounting/Controllers/StatutoryRemittanceController.cs"
    t = next(x for x in TARGETS if x[1] == "UploadReceipt")
    real = read(rel, root)
    expect(check_target(real.replace('"StatutoryRemittance", remittanceId, AttachmentAccess.Write',
                                     '"Document", Guid.Empty, AttachmentAccess.Read'),
                        t[1], t[2], t[3], t[4], t[5]) is not None,
           "เรียกด่านด้วยชนิด/ทิศผิด (Document · Guid.Empty · Read) ไม่ถูกฟ้อง")
    expect(check_target(real.replace("AttachmentAccess.Write, \"แนบใบเสร็จนำส่ง\"", "AttachmentAccess.Read, \"แนบใบเสร็จนำส่ง\""),
                        t[1], t[2], t[3], t[4], t[5]) is not None, "เรียกด่านทิศอ่านแทนทิศเขียน ไม่ถูกฟ้อง")
    # 7. กติกา OcrController: action ใหม่ที่รับ scanId โดยไม่มีด่าน (รวม expression-bodied) ต้องถูกฟ้อง · ตัวที่มีด่านไม่ฟ้อง ·
    #    เมธอด private ที่ไม่มี [Http…] ไม่นับ
    probs = check_param_rules(SYNTH_OCR, PARAM_RULES[OCR])
    expect(any("NewActionWithoutGate" in p for p in probs), f"action ใหม่ที่รับ scanId โดยไม่มีด่านไม่ถูกฟ้อง: {probs}")
    expect(not any("Good:" in p or "Helper" in p for p in probs), f"กติกา OcrController ฟ้องผิด: {probs}")
    # 8. ค้นทางเข้าใหม่: field nullable · Set<FileAttachment> · IOcrService.ScanAsync — และเมธอดในคอมเมนต์ไม่นับ
    synth = '''
public class X : ControllerBase
{
    private readonly IFileAttachmentService? _x;
    private readonly IOcrService _ocr;
    public async Task<IActionResult> A(Guid c, Guid id) { var f = await _x.GetByIdAsync(c, id); return Ok(); }
    public async Task<IActionResult> B(Guid c, Guid id) { var f = await _db.Set<FileAttachment>().FindAsync(id); return Ok(); }
    public async Task<IActionResult> C(Guid c, Guid id) { return Ok(await _ocr.ScanAsync(c, id)); }
    public async Task<IActionResult> D(Guid c) { return Ok(); }
}
// public Task E() { _x.GetByIdAsync(a, b); }
'''
    touch = sorted(attachment_touchers(synth))
    expect(touch == ["A", "B", "C"], f"ค้นทางเข้าที่แตะไฟล์แนบ/สแกนผิด: {touch}")
    # 9. wrapper ที่ไม่ส่งต่อไป service ต้องถูกฟ้อง
    rel, name, req, raw = DELEGATES[0]
    broken = read(rel, root).replace("=> _gate.DenyAttachmentAsync(", "=> NotTheGate(")
    expect(check_delegate(broken, name, req, raw) is not None, "wrapper ที่ไม่ส่งต่อไป IAttachmentAccessGate ไม่ถูกฟ้อง")

    # ── กลายพันธุ์ของฝ่ายค้านรอบสอง (review193-r2-sec-tax §5) บนไฟล์จริง ──
    def fires(rel_, old, new, what):
        src = read(rel_, root)
        expect(old in src, f"{what}: หา `{old[:60]}` ใน {rel_} ไม่เจอ (โค้ดขยับ — ปรับเคสให้ตรง)")
        if old not in src:
            return
        expect(bool(run_checks(root, {rel_: src.replace(old, new, 1)}, with_discover=False)), f"{what} แล้วไม่ฟ้อง")
    fires(OCR, 'ScanGateAsync(companyId, scanId, "ลบสแกน", write: true)', 'ScanGateAsync(companyId, scanId, "ลบสแกน", write: false)',
          "A1 Delete ถอยเป็นด่านทิศอ่าน")
    fires(OCR, 'DocGateAsync(companyId, documentId, AttachmentAccess.Write, "ตรวจความครบถ้วนซ้ำ")',
          'DocGateAsync(companyId, documentId, AttachmentAccess.Read, "ตรวจความครบถ้วนซ้ำ")', "A1b RecheckCompliance ถอยเป็นทิศอ่าน")
    fires(OCR, 'var deny = await ScanGateAsync(companyId, scanId, "ดูผลสแกน", write: false);\n        if (deny != null) return deny;',
          'var deny = await ScanGateAsync(companyId, scanId, "ดูผลสแกน", write: false);\n        if (deny != null && User.Identity == null) return deny;',
          "A2 GetResult เงื่อนไขใช้ผลอ่อนลง")
    fires(OCR, "    [HttpGet(\"{scanId:guid}\")]",
          "    [HttpGet(\"{id:guid}/raw\")]\n    public async Task<IActionResult> RawText(Guid companyId, Guid id)\n"
          "        => Ok(await _service.GetResultAsync(companyId, id));\n\n    [HttpGet(\"{scanId:guid}\")]",
          "A3 action ใหม่รับ Guid id (ชื่ออื่น) ไม่มีด่าน")
    fires(GATE, "AttachmentPermissionScope.UnlinkedScanKeys(ocrRule, access, unlinkedEditIsMemberLevel)", "ocrRule.ReadAnyOf",
          "A5 ทิศเขียนของสแกนที่ยังไม่ผูกถอยเป็น ReadAnyOf")
    fires(GATE, "AttachmentPermissionScope.ScanSourceGate(file.EntityType) == ScanSourceCheck.FileOwnerReadGate",
          'string.Equals(file.EntityType, "Document", StringComparison.OrdinalIgnoreCase)', "A6 DenyScanSourceAsync ตรวจเฉพาะ Document")
    fires(GATE, "AttachmentPermissionScope.ScanOwner(file?.EntityType, file?.EntityId,\n            aliveDoc",
          "AttachmentPermissionScope.ScanOwner(null, null,\n            aliveDoc", "A7 ScanOwner ไม่ได้รับเจ้าของไฟล์")
    fires(GATE, "aliveDoc, aliveDoc != null, aliveJe, aliveJe != null, fileOwnerExists);\n        if (owner",
          "aliveDoc, aliveDoc != null, aliveJe, aliveJe != null);\n        if (owner", "R2-C1 ScanOwner ไม่ได้รับ fileOwnerExists")
    fires(FAS, "Accounting.Helpers.AttachmentPermissionScope.ScanFileRelinkable(f.EntityType, f.EntityId, entityId,",
          "AlwaysTrue(f.EntityType, f.EntityId, entityId,", "A8 relink-on-read ไม่ผ่าน ScanFileRelinkable")
    fires(SELFCORR, "Accounting.Helpers.AttachmentRetention.ScanFilePurgeable(", "AlwaysPurge(",
          "R2-C4 งาน purge ไม่ถาม AttachmentRetention")
    # A-FP: ห่อด่านด้วย try/catch ระดับบนสุด (ด่านยังอยู่บนสุดของ try) ⇒ ต้องไม่ฟ้อง · แต่ด่านใน catch/if ยังต้องฟ้อง
    src = read(OCR, root)
    old = ('        var deny = await ScanGateAsync(companyId, scanId, "ดูผลสแกน", write: false);\n'
           '        if (deny != null) return deny;\n'
           '        return Ok(new ApiResponse<OcrResultResponse>(true, await _service.GetResultAsync(companyId, scanId)));\n')
    expect(old in src, "A-FP: หา GetResult ไม่เจอ (โค้ดขยับ — ปรับเคสให้ตรง)")
    if old in src:
        wrapped_try = src.replace(old, "        try\n        {\n" + old.replace("        ", "            ")
                                  + "        }\n        catch (KeyNotFoundException) { return NotFound(); }\n", 1)
        fp = [p for p in run_checks(root, {OCR: wrapped_try}, with_discover=False) if "GetResult:" in p]
        expect(not fp, f"A-FP ห่อ GetResult ด้วย try/catch แล้วถูกฟ้องผิด: {fp}")
        in_catch = src.replace(old, "        try { await Task.CompletedTask; }\n        catch (KeyNotFoundException)\n        {\n"
                               + old.replace("        ", "            ") + "        }\n        return NotFound();\n", 1)
        expect(any("GetResult:" in p for p in run_checks(root, {OCR: in_catch}, with_discover=False)),
               "ด่านที่อยู่ใน catch (มีเงื่อนไข) ไม่ถูกฟ้อง")
    return ok


def main():
    if "--self-test" in sys.argv:
        ok = self_test()
        print("✅ self-test ผ่าน" if ok else "❌ self-test ล้ม")
        return 0 if ok else 1
    problems = run_checks()
    for p in problems:
        print("❌ " + p)
    ok = self_test()
    if problems or not ok:
        print(f"\n❌ attachment_gate_check: พบ {len(problems)} จุด"
              + ("" if ok else " + negative test ล้ม (checker จับบั๊กที่ใส่กลับไม่ได้ = ไม่มีด่าน)"))
        return 1
    n_scan = len([1 for nm, head, _ in actions(read(OCR)) if re.search(r"\bGuid\s+(scanId|fileAttachmentId|documentId)\b", head)])
    print(f"✅ ทุกทางเข้าไฟล์แนบ/สแกนเรียกด่านกลางจริง ({len(TARGETS)} target · OcrController {n_scan} action ตามกติกาพารามิเตอร์ · "
          f"{len(DELEGATES)} ด่านชั้นใน · ยกเว้นพร้อมเหตุผล {len(EXEMPT)}) · negative test ผ่าน")
    return 0


if __name__ == "__main__":
    sys.exit(main())
