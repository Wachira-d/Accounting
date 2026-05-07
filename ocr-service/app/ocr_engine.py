import io
import logging
from PIL import Image
import numpy as np

logger = logging.getLogger(__name__)

_ocr_instance = None


def get_ocr():
    global _ocr_instance
    if _ocr_instance is None:
        from paddleocr import PaddleOCR
        _ocr_instance = PaddleOCR(
            use_angle_cls=True,
            lang="th",
            use_gpu=False,
            show_log=False,
            det_db_thresh=0.3,
            rec_batch_num=6,
        )
        logger.info("PaddleOCR initialized (Thai)")
    return _ocr_instance


def extract_text(image_bytes: bytes) -> tuple[str, list[dict]]:
    """Extract text from image bytes using PaddleOCR.
    Returns (full_text, detailed_results_with_boxes)."""
    ocr = get_ocr()

    image = Image.open(io.BytesIO(image_bytes))
    if image.mode != "RGB":
        image = image.convert("RGB")
    img_array = np.array(image)

    results = ocr.ocr(img_array, cls=True)

    if not results or not results[0]:
        return "", []

    lines = []
    detailed = []
    for line in results[0]:
        box, (text, conf) = line[0], line[1]
        lines.append(text)
        detailed.append({
            "text": text,
            "confidence": float(conf),
            "box": [[float(p[0]), float(p[1])] for p in box],
        })

    detailed.sort(key=lambda d: (min(p[1] for p in d["box"]), min(p[0] for p in d["box"])))
    full_text = "\n".join(d["text"] for d in detailed)
    return full_text, detailed


def extract_text_from_pdf(pdf_bytes: bytes) -> tuple[str, list[dict]]:
    """Extract text from PDF by converting pages to images."""
    try:
        from pdf2image import convert_from_bytes
        images = convert_from_bytes(pdf_bytes, dpi=300)
    except ImportError:
        logger.warning("pdf2image not installed, trying first page only")
        return extract_text(pdf_bytes)

    all_text = []
    all_detailed = []
    for i, img in enumerate(images):
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        text, detailed = extract_text(buf.getvalue())
        for d in detailed:
            d["page"] = i
        all_text.append(text)
        all_detailed.extend(detailed)

    return "\n---PAGE BREAK---\n".join(all_text), all_detailed
