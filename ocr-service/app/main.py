import logging
import os
from contextlib import asynccontextmanager

from fastapi import FastAPI, File, UploadFile, HTTPException
from fastapi.middleware.cors import CORSMiddleware

from .models import OcrResult, CorrectionRequest, HealthResponse, SuggestedAccounts, LineItem
from .ocr_engine import extract_text, extract_text_from_pdf
from .ai_engine import extract_with_fallback, check_ollama_health, AI_MODEL
from .learning import save_correction, get_training_stats, export_for_finetuning

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("OCR+AI Service starting...")
    logger.info(f"Ollama URL: {os.environ.get('OLLAMA_BASE_URL', 'http://localhost:11434')}")
    logger.info(f"AI Model: {AI_MODEL}")

    # Pre-load PaddleOCR model
    try:
        from .ocr_engine import get_ocr
        get_ocr()
        logger.info("PaddleOCR loaded successfully")
    except Exception as e:
        logger.warning(f"PaddleOCR pre-load failed (will retry on first request): {e}")

    yield
    logger.info("OCR+AI Service shutting down")


app = FastAPI(
    title="Thai Accounting OCR+AI Service",
    version="1.0.0",
    lifespan=lifespan,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.get("/health", response_model=HealthResponse)
async def health_check():
    ai_ready, ai_info = await check_ollama_health()
    try:
        from .ocr_engine import get_ocr
        get_ocr()
        ocr_ready = True
    except Exception:
        ocr_ready = False

    return HealthResponse(
        status="ok" if ocr_ready else "degraded",
        ocr_ready=ocr_ready,
        ai_ready=ai_ready,
        ai_model=AI_MODEL if ai_ready else ai_info,
    )


@app.post("/ocr/extract", response_model=OcrResult)
async def extract_document(file: UploadFile = File(...)):
    """Extract structured data from uploaded document image/PDF."""
    if not file.content_type:
        raise HTTPException(400, "Missing content type")

    allowed_types = [
        "image/jpeg", "image/png", "image/tiff", "image/webp", "image/bmp",
        "application/pdf",
    ]
    if file.content_type not in allowed_types:
        raise HTTPException(400, f"Unsupported file type: {file.content_type}")

    file_bytes = await file.read()
    if len(file_bytes) == 0:
        raise HTTPException(400, "Empty file")
    if len(file_bytes) > 50 * 1024 * 1024:
        raise HTTPException(400, "File too large (max 50MB)")

    # Step 1: OCR
    try:
        if file.content_type == "application/pdf":
            raw_text, detailed = extract_text_from_pdf(file_bytes)
        else:
            raw_text, detailed = extract_text(file_bytes)
    except Exception as e:
        logger.error(f"OCR failed: {e}")
        raise HTTPException(500, f"OCR extraction failed: {e}")

    if not raw_text.strip():
        return OcrResult(
            raw_text="",
            confidence=0.0,
            reasoning="OCR ไม่สามารถอ่านข้อความจากไฟล์นี้ได้",
            ocr_engine="paddleocr",
        )

    # Step 2: AI structured extraction
    ai_result = await extract_with_fallback(raw_text)

    if ai_result:
        suggested = ai_result.get("suggested_accounts")
        sa = SuggestedAccounts(**suggested) if isinstance(suggested, dict) else None

        items_raw = ai_result.get("items") or []
        items = [LineItem(**it) if isinstance(it, dict) else it for it in items_raw]

        return OcrResult(
            raw_text=raw_text,
            document_type=ai_result.get("document_type"),
            confidence=float(ai_result.get("confidence", 0.5)),
            vendor_name=ai_result.get("vendor_name"),
            vendor_tax_id=ai_result.get("vendor_tax_id"),
            document_number=ai_result.get("document_number"),
            document_date=ai_result.get("document_date"),
            subtotal=ai_result.get("subtotal"),
            vat_amount=ai_result.get("vat_amount"),
            total_amount=ai_result.get("total_amount"),
            items=items,
            expense_category=ai_result.get("expense_category"),
            suggested_accounts=sa,
            has_wht=bool(ai_result.get("has_wht", False)),
            wht_rate=ai_result.get("wht_rate"),
            payment_terms_days=ai_result.get("payment_terms_days"),
            reasoning=ai_result.get("reasoning"),
            ocr_engine="paddleocr",
            ai_engine=AI_MODEL if ai_result.get("reasoning") != "Rule-based extraction (AI unavailable)" else "rule-based",
        )

    return OcrResult(
        raw_text=raw_text,
        confidence=0.3,
        reasoning="ไม่สามารถวิเคราะห์โครงสร้างเอกสารได้",
        ocr_engine="paddleocr",
    )


@app.post("/ocr/correct")
async def submit_correction(correction: CorrectionRequest):
    """Submit a user correction to improve future extractions."""
    filepath = save_correction(
        original_text=correction.original_text,
        original_result=correction.original_result.model_dump(),
        corrected_result=correction.corrected_result.model_dump(),
        document_type=correction.document_type,
    )
    return {"status": "saved", "file": filepath}


@app.get("/ocr/training/stats")
async def training_stats():
    """Get training data collection statistics."""
    return get_training_stats()


@app.post("/ocr/training/export")
async def export_training_data():
    """Export collected corrections as fine-tuning dataset."""
    output = export_for_finetuning()
    stats = get_training_stats()
    return {
        "status": "exported",
        "output_file": output,
        "total_entries": stats["total_corrections"],
    }
