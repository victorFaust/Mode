"""Turns an uploaded file into a thumbnail, a preview image and plain text. Pure functions; no I/O."""

from __future__ import annotations

import io
import warnings
import zipfile
from dataclasses import dataclass

from PIL import Image

THUMBNAIL_SIZE = (320, 320)
PREVIEW_SIZE = (1600, 1600)
MAX_TEXT_CHARS = 1_000_000
MAX_PDF_TEXT_PAGES = 200
MAX_SHEET_ROWS = 5_000
MAX_UNZIPPED_BYTES = 200 * 1024 * 1024
MAX_IMAGE_PIXELS = 40_000_000

IMAGE_EXTENSIONS = {"png", "jpg", "jpeg", "gif", "webp"}
OFFICE_EXTENSIONS = {"docx", "xlsx", "pptx"}
TEXT_EXTENSIONS = {"txt", "csv", "md"}


class ProcessingError(Exception):
    """The file is corrupt or unsafe to process (for example a decompression bomb)."""


@dataclass(frozen=True)
class Result:
    supported: bool
    thumbnail: bytes | None = None
    preview: bytes | None = None
    text: str | None = None


def process(data: bytes, extension: str) -> Result:
    extension = extension.lower()
    try:
        if extension in IMAGE_EXTENSIONS:
            return _image(data)
        if extension == "pdf":
            return _pdf(data)
        if extension in OFFICE_EXTENSIONS:
            _ensure_safe_zip(data)
            return Result(True, text=_cap({"docx": _docx, "xlsx": _xlsx, "pptx": _pptx}[extension](data)))
        if extension in TEXT_EXTENSIONS:
            return Result(True, text=_cap(data.decode("utf-8", errors="replace").lstrip("\ufeff")))
    except ProcessingError:
        raise
    except Exception as error:  # third-party parsers raise many types for corrupt input
        raise ProcessingError(f"Could not read the {extension} file: {error}") from error
    return Result(False)


def _png(image: Image.Image, size: tuple[int, int]) -> bytes:
    copy = image.copy()
    copy.thumbnail(size)
    if copy.mode not in ("RGB", "RGBA", "L", "LA"):
        copy = copy.convert("RGBA")
    buffer = io.BytesIO()
    copy.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def _open_image(data: bytes) -> Image.Image:
    Image.MAX_IMAGE_PIXELS = MAX_IMAGE_PIXELS
    with warnings.catch_warnings():
        warnings.simplefilter("error", Image.DecompressionBombWarning)
        try:
            image = Image.open(io.BytesIO(data))
            image.seek(0)
            image.load()
        except (Image.DecompressionBombError, Image.DecompressionBombWarning) as error:
            raise ProcessingError("The image is too large to process.") from error
    return image


def _image(data: bytes) -> Result:
    with _open_image(data) as image:
        return Result(True, thumbnail=_png(image, THUMBNAIL_SIZE), preview=_png(image, PREVIEW_SIZE))


def _pdf(data: bytes) -> Result:
    import pypdfium2 as pdfium
    from pypdf import PdfReader

    thumbnail = preview = None
    document = pdfium.PdfDocument(data)
    try:
        if len(document) > 0:
            page = document[0]
            scale = min(2.0, PREVIEW_SIZE[0] / max(page.get_width(), 1))
            rendered = page.render(scale=scale).to_pil()
            thumbnail, preview = _png(rendered, THUMBNAIL_SIZE), _png(rendered, PREVIEW_SIZE)
            page.close()
    finally:
        document.close()

    reader = PdfReader(io.BytesIO(data))
    text = "\n".join((page.extract_text() or "") for page in reader.pages[:MAX_PDF_TEXT_PAGES])
    return Result(True, thumbnail=thumbnail, preview=preview, text=_cap(text))


def _ensure_safe_zip(data: bytes) -> None:
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        if sum(info.file_size for info in archive.infolist()) > MAX_UNZIPPED_BYTES:
            raise ProcessingError("The document expands to more than the allowed size.")


def _docx(data: bytes) -> str:
    import docx

    document = docx.Document(io.BytesIO(data))
    parts = [paragraph.text for paragraph in document.paragraphs]
    for table in document.tables:
        for row in table.rows:
            parts.append("\t".join(cell.text for cell in row.cells))
    return "\n".join(parts)


def _xlsx(data: bytes) -> str:
    import openpyxl

    workbook = openpyxl.load_workbook(io.BytesIO(data), read_only=True, data_only=True)
    try:
        lines = []
        for sheet in workbook.worksheets:
            lines.append(sheet.title)
            for row in sheet.iter_rows(values_only=True, max_row=MAX_SHEET_ROWS):
                values = [str(value) for value in row if value is not None]
                if values:
                    lines.append("\t".join(values))
        return "\n".join(lines)
    finally:
        workbook.close()


def _pptx(data: bytes) -> str:
    from pptx import Presentation

    presentation = Presentation(io.BytesIO(data))
    lines = []
    for slide in presentation.slides:
        for shape in slide.shapes:
            if shape.has_text_frame:
                lines.append(shape.text_frame.text)
    return "\n".join(lines)


def _cap(text: str | None) -> str | None:
    if text is None or not text.strip():
        return None
    return text.replace("\x00", "")[:MAX_TEXT_CHARS]
