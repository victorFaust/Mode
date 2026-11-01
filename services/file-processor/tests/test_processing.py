import io

import docx
import openpyxl
import pytest
from PIL import Image
from pptx import Presentation
from pptx.util import Inches

from app import processing
from app.processing import ProcessingError, process


def make_png(width: int, height: int) -> bytes:
    buffer = io.BytesIO()
    Image.new("RGB", (width, height), (40, 120, 200)).save(buffer, format="PNG")
    return buffer.getvalue()


def make_pdf(text: str) -> bytes:
    stream = f"BT /F1 18 Tf 20 70 Td ({text}) Tj ET".encode()
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 144] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for number, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % number + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    for offset in offsets:
        out += b"%010d 00000 n \n" % offset
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, xref)
    return bytes(out)


def png_size(data: bytes) -> tuple[int, int]:
    with Image.open(io.BytesIO(data)) as image:
        assert image.format == "PNG"
        return image.size


def test_images_get_bounded_png_thumbnail_and_preview() -> None:
    result = process(make_png(2400, 1200), "png")

    assert result.supported and result.text is None
    assert png_size(result.thumbnail) == (320, 160)
    assert png_size(result.preview) == (1600, 800)


def test_pdf_gets_first_page_render_and_text() -> None:
    result = process(make_pdf("Hello Mode"), "pdf")

    assert "Hello Mode" in result.text
    assert max(png_size(result.thumbnail)) <= 320
    assert png_size(result.preview)[0] > 320


def test_docx_text_includes_paragraphs_and_tables() -> None:
    document = docx.Document()
    document.add_paragraph("Quarterly plan")
    document.add_table(rows=1, cols=2).rows[0].cells[0].text = "Budget"
    buffer = io.BytesIO()
    document.save(buffer)

    result = process(buffer.getvalue(), "docx")

    assert "Quarterly plan" in result.text and "Budget" in result.text
    assert result.thumbnail is None


def test_xlsx_text_includes_sheet_values() -> None:
    workbook = openpyxl.Workbook()
    workbook.active.title = "Sales"
    workbook.active.append(["Region", 42])
    buffer = io.BytesIO()
    workbook.save(buffer)

    assert "Region\t42" in process(buffer.getvalue(), "xlsx").text


def test_pptx_text_includes_slide_text() -> None:
    presentation = Presentation()
    slide = presentation.slides.add_slide(presentation.slide_layouts[6])
    slide.shapes.add_textbox(Inches(1), Inches(1), Inches(4), Inches(1)).text_frame.text = "Launch day"
    buffer = io.BytesIO()
    presentation.save(buffer)

    assert "Launch day" in process(buffer.getvalue(), "pptx").text


def test_plain_text_is_decoded_and_nul_stripped() -> None:
    assert process("\ufeffhéllo\x00 world".encode(), "txt").text == "héllo world"


def test_legacy_office_formats_are_not_supported() -> None:
    assert process(b"\xd0\xcf\x11\xe0", "doc").supported is False


def test_decompression_bombs_are_refused(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(processing, "MAX_IMAGE_PIXELS", 1_000)

    with pytest.raises(ProcessingError):
        process(make_png(100, 100), "png")


def test_zip_bombs_are_refused(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(processing, "MAX_UNZIPPED_BYTES", 100)
    buffer = io.BytesIO()
    docx.Document().save(buffer)

    with pytest.raises(ProcessingError):
        process(buffer.getvalue(), "docx")


def test_corrupt_files_raise_processing_error() -> None:
    with pytest.raises(ProcessingError):
        process(b"%PDF-1.4 not really", "pdf")
