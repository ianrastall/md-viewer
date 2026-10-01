// Native integration checks for md-viewer's C++ core, through its C ABI and internal modules.
// Exits nonzero on failure. Set MDV_NETWORK_TESTS=1 to add one live Wikipedia crawl.
#include "mdv_native.h"

#include "common.h"
#include "crawl.h"
#include "files.h"
#include "html.h"
#include "pandoc.h"
#include "tools.h"
#include "url.h"

#include <windows.h>

#include <chrono>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <functional>
#include <iostream>
#include <string>
#include <vector>

namespace fs = std::filesystem;
using namespace std::string_literals;

namespace {

int failures = 0, checks = 0;

void check(const std::string& name, bool condition, const std::string& detail = "") {
    ++checks;
    if (condition) return;
    ++failures;
    std::cout << "FAIL  " << name << (detail.empty() ? "" : " \xE2\x80\x94 " + detail) << "\n";
}

struct Result {
    int status = -1;
    std::string data;
    bool ok() const { return status == MDV_OK; }
};

Result take(mdv_result* result) {
    Result out;
    if (!result) return out;
    out.status = result->status;
    out.data.assign(result->data, result->size);
    mdv_result_free(result);
    return out;
}

// ---- A compact reader for the binary document model ----

struct Span { uint8_t type; std::string href, title, text; };
struct Block {
    uint8_t type = 0, level = 0, task = 0, align = 0, tight = 0;
    uint32_t start = 1;
    std::string language, text;
    std::vector<Span> spans;
    std::vector<Block> children;
};
struct Heading { int level; uint32_t line, block; std::string title, slug; };
struct Model {
    uint32_t lines = 0, words = 0, characters = 0;
    std::vector<Heading> headings;
    std::vector<Block> blocks;
};

struct Reader {
    const std::string& data;
    size_t position = 4;
    uint8_t u8() { return static_cast<uint8_t>(data[position++]); }
    uint32_t u32() {
        uint32_t value = 0;
        for (int i = 0; i < 4; ++i) value |= static_cast<uint32_t>(static_cast<uint8_t>(data[position++])) << (8 * i);
        return value;
    }
    std::string str() {
        const auto length = u32();
        auto text = data.substr(position, length);
        position += length;
        return text;
    }
};

Model read_model(const std::string& data) {
    Model model;
    Reader reader{data};
    model.lines = reader.u32();
    model.words = reader.u32();
    model.characters = reader.u32();
    for (auto count = reader.u32(); count > 0; --count) {
        Heading heading;
        heading.level = reader.u8();
        heading.line = reader.u32();
        heading.block = reader.u32();
        heading.title = reader.str();
        heading.slug = reader.str();
        model.headings.push_back(heading);
    }
    reader.u32();
    std::vector<Block*> stack;
    std::vector<size_t> open_spans;
    for (uint8_t op = reader.u8(); op != 0; op = reader.u8()) {
        if (op == 1) {
            Block block;
            block.type = reader.u8();
            if (block.type == 6) block.level = reader.u8();
            else if (block.type == 7) block.language = reader.str();
            else if (block.type == 2) block.tight = reader.u8();
            else if (block.type == 3) { block.tight = reader.u8(); block.start = reader.u32(); }
            else if (block.type == 4) block.task = reader.u8();
            else if (block.type == 14 || block.type == 15) block.align = reader.u8();
            auto& siblings = stack.empty() ? model.blocks : stack.back()->children;
            siblings.push_back(block);
            stack.push_back(&siblings.back());
            open_spans.clear();
        } else if (op == 2) {
            stack.pop_back();
        } else if (op == 3) {
            Span span{reader.u8()};
            if (span.type == 2 || span.type == 3) { span.href = reader.str(); span.title = reader.str(); }
            stack.back()->spans.push_back(span);
            open_spans.push_back(stack.back()->spans.size() - 1);
        } else if (op == 4) {
            open_spans.pop_back();
        } else if (op == 5) {
            const auto type = reader.u8();
            const auto text = reader.str();
            const auto piece = type == 3 || type == 2 ? " "s : text;
            stack.back()->text += piece;
            for (const auto index : open_spans) stack.back()->spans[index].text += piece;
        }
    }
    return model;
}

Model parse(const std::string& markdown) {
    const auto result = take(mdv_parse(markdown.data(), markdown.size()));
    if (!result.ok() || !result.data.starts_with("MDV1")) {
        std::cout << "parse failed: " << result.data << "\n";
        return {};
    }
    return read_model(result.data);
}

struct Reflow { int changed = -1, total = -1; std::vector<std::string> warnings; std::string markdown; };

Reflow reflow(const std::string& markdown) {
    const auto result = take(mdv_reflow_headings(markdown.data(), markdown.size()));
    Reflow out;
    if (!result.ok()) return out;
    const auto header_end = result.data.find('\n');
    int warnings = 0;
    sscanf_s(result.data.substr(0, header_end).c_str(), "%d\t%d\t%d", &out.changed, &out.total, &warnings);
    size_t position = header_end + 1;
    for (int i = 0; i < warnings; ++i) {
        const auto end = result.data.find('\n', position);
        out.warnings.push_back(result.data.substr(position, end - position));
        position = end + 1;
    }
    out.markdown = result.data.substr(position);
    return out;
}

std::string read_all(const fs::path& path) { return mdv::read_file(path.string(), 1ull << 30); }

void write_all(const fs::path& path, std::string_view bytes) {
    FILE* file = nullptr;
    _wfopen_s(&file, path.c_str(), L"wb");
    fwrite(bytes.data(), 1, bytes.size(), file);
    fclose(file);
}

std::string minimal_pdf(const std::string& text) {
    std::vector<std::string> objects = {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        "",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    };
    const auto stream = "BT /F1 12 Tf 72 720 Td (" + text + ") Tj ET";
    objects[3] = "<< /Length " + std::to_string(stream.size()) + " >>\nstream\n" + stream + "\nendstream";
    std::string pdf = "%PDF-1.4\n";
    std::vector<size_t> offsets;
    for (size_t i = 0; i < objects.size(); ++i) {
        offsets.push_back(pdf.size());
        pdf += std::to_string(i + 1) + " 0 obj\n" + objects[i] + "\nendobj\n";
    }
    const auto xref = pdf.size();
    pdf += "xref\n0 " + std::to_string(objects.size() + 1) + "\n0000000000 65535 f \n";
    for (const auto offset : offsets) {
        char line[32];
        snprintf(line, sizeof line, "%010zu 00000 n \n", offset);
        pdf += line;
    }
    pdf += "trailer\n<< /Size " + std::to_string(objects.size() + 1) + " /Root 1 0 R >>\nstartxref\n" + std::to_string(xref) + "\n%%EOF\n";
    return pdf;
}

// An image-only PDF (no text layer) holding rendered words, to exercise OCR.
std::string scanned_pdf(const std::wstring& text) {
    constexpr int width = 1600, height = 360;
    BITMAPINFO info{};
    info.bmiHeader = {sizeof(BITMAPINFOHEADER), width, -height, 1, 24, BI_RGB};
    void* bits = nullptr;
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    auto previous = SelectObject(dc, bitmap);
    RECT area{0, 0, width, height};
    FillRect(dc, &area, static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH)));
    HFONT font = CreateFontW(64, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, ANTIALIASED_QUALITY, 0, L"Segoe UI");
    auto previous_font = SelectObject(dc, font);
    area.left = 60;
    area.top = 80;
    DrawTextW(dc, text.c_str(), -1, &area, DT_LEFT | DT_WORDBREAK);
    GdiFlush();
    const int stride = (width * 3 + 3) & ~3;
    std::string rgb(static_cast<size_t>(width) * height * 3, '\0');
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x) {
            const auto* pixel = static_cast<const unsigned char*>(bits) + y * stride + x * 3;
            auto* target = rgb.data() + (static_cast<size_t>(y) * width + x) * 3;
            target[0] = static_cast<char>(pixel[2]);
            target[1] = static_cast<char>(pixel[1]);
            target[2] = static_cast<char>(pixel[0]);
        }
    SelectObject(dc, previous_font);
    SelectObject(dc, previous);
    DeleteObject(font);
    DeleteObject(bitmap);
    DeleteDC(dc);

    const std::string content = "q 612 0 0 138 0 600 cm /Im1 Do Q";
    std::vector<std::string> objects = {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>",
        "<< /Length " + std::to_string(content.size()) + " >>\nstream\n" + content + "\nendstream",
        "<< /Type /XObject /Subtype /Image /Width " + std::to_string(width) + " /Height " + std::to_string(height) +
            " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length " + std::to_string(rgb.size()) + " >>\nstream\n" + rgb + "\nendstream",
    };
    std::string pdf = "%PDF-1.4\n";
    std::vector<size_t> offsets;
    for (size_t i = 0; i < objects.size(); ++i) {
        offsets.push_back(pdf.size());
        pdf += std::to_string(i + 1) + " 0 obj\n" + objects[i] + "\nendobj\n";
    }
    const auto xref = pdf.size();
    pdf += "xref\n0 6\n0000000000 65535 f \n";
    for (const auto offset : offsets) {
        char line[32];
        snprintf(line, sizeof line, "%010zu 00000 n \n", offset);
        pdf += line;
    }
    pdf += "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" + std::to_string(xref) + "\n%%EOF\n";
    return pdf;
}

int32_t cancel_immediately(void*, const char*) { return 1; }
int32_t collect(void* context, const char* message) {
    if (message) static_cast<std::vector<std::string>*>(context)->push_back(message);
    return 0;
}

}  // namespace

int main() {
    SetConsoleOutputCP(CP_UTF8);
    check("abi version", mdv_abi_version() == MDV_ABI_VERSION);
    const auto scratch = fs::temp_directory_path() / ("md-viewer-native-" + mdv::new_guid());
    fs::create_directories(scratch);
    mdv_configure((scratch / "data").string().c_str());

    // ---- Parsing ---------------------------------------------------------------
    {
        const auto doc = parse("# Title\n\nSome *emphasis*, **strong**, `code`, ~~gone~~ and a [link](https://example.com \"tip\").\n\n## Second & more\n\n```csharp\nvar x = 1;\n```\n");
        check("block count", doc.blocks.size() == 4, std::to_string(doc.blocks.size()));
        check("headings", doc.headings.size() == 2 && doc.headings[1].title == "Second & more" && doc.headings[1].level == 2);
        check("heading lines", doc.headings.size() == 2 && doc.headings[0].line == 1 && doc.headings[1].line == 5);
        check("heading blocks", doc.headings.size() == 2 && doc.headings[0].block == 0 && doc.headings[1].block == 2);
        check("heading slugs", doc.headings.size() == 2 && doc.headings[0].slug == "title" && doc.headings[1].slug == "second--more");
        if (doc.blocks.size() == 4) {
            const auto& paragraph = doc.blocks[1];
            check("inline spans", paragraph.spans.size() == 5, std::to_string(paragraph.spans.size()));
            const auto link = std::find_if(paragraph.spans.begin(), paragraph.spans.end(), [](const Span& s) { return s.type == 2; });
            check("link", link != paragraph.spans.end() && link->href == "https://example.com" && link->title == "tip" && link->text == "link");
            const auto code = std::find_if(paragraph.spans.begin(), paragraph.spans.end(), [](const Span& s) { return s.type == 4; });
            check("code span", code != paragraph.spans.end() && code->text == "code");
            check("code block", doc.blocks[3].type == 7 && doc.blocks[3].language == "csharp" && doc.blocks[3].text == "var x = 1;\n", doc.blocks[3].text);
        }
        check("line count", doc.lines == 10, std::to_string(doc.lines));
        check("word count", doc.words == 14, std::to_string(doc.words));

        const auto stats = parse("a\r\nb\rc\n\xF0\x9F\x98\x80");
        check("mixed line endings", stats.lines == 4, std::to_string(stats.lines));
        check("utf-16 characters", stats.characters == 9, std::to_string(stats.characters));
        const auto empty = parse("");
        check("empty document", empty.lines == 1 && empty.blocks.empty() && empty.words == 0);

        const std::string with_nul = "&copy; &#169; &#x1F600; &bogus; a"s + '\0' + "b";
        const auto entities = parse(with_nul);
        check("entities", !entities.blocks.empty() && entities.blocks[0].text == "\xC2\xA9 \xC2\xA9 \xF0\x9F\x98\x80 &bogus; a\xEF\xBF\xBD" "b", entities.blocks.empty() ? "" : entities.blocks[0].text);

        const auto lists = parse("- [x] done\n- [ ] open\n- plain\n\n3. three\n4. four\n");
        check("task list", lists.blocks.size() == 2 && lists.blocks[0].tight && lists.blocks[0].children.size() == 3 &&
                               lists.blocks[0].children[0].task == 2 && lists.blocks[0].children[1].task == 1 && lists.blocks[0].children[2].task == 0);
        check("ordered start", lists.blocks.size() == 2 && lists.blocks[1].type == 3 && lists.blocks[1].start == 3);

        const auto table = parse("| a | b |\n|:--|--:|\n| 1 | 2 |\n");
        check("table", table.blocks.size() == 1 && table.blocks[0].type == 10 && table.blocks[0].children[0].children[0].children[1].align == 3);

        const auto front = parse("---\ntitle: x\n---\n\n# After\n");
        check("front matter", front.blocks.size() == 2 && front.blocks[0].type == 100 && front.blocks[0].text.find("title: x") != std::string::npos &&
                                  front.headings.size() == 1 && front.headings[0].line == 5 && front.headings[0].block == 1);
        check("thematic break is not front matter", parse("---\n\ntext\n").blocks[0].type == 5);
        check("html comment block", parse("<!--\nPDF import\n-->\n\ntext\n").blocks[0].type == 8);
        check("fenced code hides headings", parse("```\n# not a heading\n```\n\n~~~\n## nor this\n~~~\n").headings.empty());
        const auto duplicate = parse("# Intro\n# Intro\n## Title {#custom .class}\n");
        check("duplicate slugs", duplicate.headings.size() == 3 && duplicate.headings[1].slug == "intro-1");
        check("pandoc attributes stripped", duplicate.headings.size() == 3 && duplicate.headings[2].title == "Title");
        const auto setext = parse("Title\n=====\n");
        check("setext heading", setext.headings.size() == 1 && setext.headings[0].level == 1 && setext.headings[0].line == 1);
        const auto quoted = parse("> ## Inside\n");
        check("quoted heading", quoted.headings.size() == 1 && quoted.headings[0].level == 2 && quoted.headings[0].block == 0);

        std::string big;
        for (int i = 0; i < 20000; ++i) big += "## Section " + std::to_string(i) + "\n\nParagraph with **bold** text and a [link](#section-" + std::to_string(i) + ").\n\n";
        const auto started = std::chrono::steady_clock::now();
        const auto large = parse(big);
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count();
        check("large document", large.headings.size() == 20000 && large.blocks.size() == 40000);
        std::cout << "      parsed " << big.size() / 1024 << " KB in " << elapsed << " ms\n";
    }

    // ---- Heading reflow -------------------------------------------------------
    {
        auto r = reflow("# A\n\n### B\n\n### C\n\n##### D\n\ntext\n");
        check("reflow levels", r.markdown == "# A\n\n## B\n\n## C\n\n### D\n\ntext\n", r.markdown);
        check("reflow counts", r.changed == 3 && r.total == 4);
        check("reflow promotes and keeps CRLF", reflow("### Only\r\n\r\nbody ## not heading\r\n").markdown == "# Only\r\n\r\nbody ## not heading\r\n");
        check("reflow setext unchanged when level fits", reflow("# Top\n\nSub\n---\n\n#### Deep\n").markdown == "# Top\n\nSub\n---\n\n### Deep\n");
        r = reflow("Lead\n----\n\nLine one\nline two\n---------\n");
        check("reflow converts setext", r.markdown == "# Lead\n\n# Line one line two\n", r.markdown);
        r = reflow("# A\n\n> ### Quoted *heading* ###\n\n- #### In a list\n");
        check("reflow in containers", r.markdown == "# A\n\n> ## Quoted *heading* ###\n\n- ### In a list\n", r.markdown);
        r = reflow("---\ntitle: T\n---\n\n## Body\n");
        check("reflow ignores front matter", r.markdown == "---\ntitle: T\n---\n\n# Body\n" && r.changed == 1, r.markdown);
        check("reflow code fences", reflow("```\n### x\n```\n").changed == 0);
        r = reflow("# T\n\n## U\n\n<h2>x</h2>\n\n[jump](#t)\n");
        check("reflow warnings", r.warnings.size() == 3);
        r = reflow("plain");
        check("reflow no headings", r.warnings.size() == 1 && r.warnings[0].starts_with("No Markdown headings"));
        const std::string with_nul = "## a"s + '\0' + "b\n";
        check("reflow keeps NUL", reflow(with_nul).markdown == "# a"s + '\0' + "b\n");
    }

    // ---- Documents: open and save ----------------------------------------------
    {
        const auto ansi = scratch / "ansi.md";
        write_all(ansi, "caf\xE9");
        auto opened = take(mdv_open(ansi.string().c_str(), nullptr, nullptr));
        check("windows-1252 detection", opened.ok() && opened.data == "markdown\nWindows-1252\nOpened ansi.md.\ncaf\xC3\xA9", opened.data);
        auto saved = take(mdv_save(ansi.string().c_str(), "caff\xC3\xA8", 6, "Windows-1252"));
        check("windows-1252 round trip", saved.ok() && saved.data == "Windows-1252" && read_all(ansi) == "caff\xE8");
        saved = take(mdv_save(ansi.string().c_str(), "\xCE\xA3", 2, "Windows-1252"));
        check("legacy encoding falls back to UTF-8", saved.ok() && saved.data == "UTF-8" && read_all(ansi) == "\xCE\xA3");

        const auto bom = scratch / "bom.md";
        write_all(bom, "\xEF\xBB\xBF# x");
        opened = take(mdv_open(bom.string().c_str(), nullptr, nullptr));
        check("utf-8 bom", opened.ok() && opened.data == "markdown\nUTF-8 BOM\nOpened bom.md.\n# x");
        take(mdv_save(bom.string().c_str(), "# y", 3, "UTF-8 BOM"));
        check("utf-8 bom preserved", read_all(bom) == "\xEF\xBB\xBF# y");

        const auto wide = scratch / "wide.txt";
        write_all(wide, std::string("\xFF\xFE" "h\0i\0", 6));
        opened = take(mdv_open(wide.string().c_str(), nullptr, nullptr));
        check("utf-16", opened.ok() && opened.data.ends_with("UTF-16 LE\nOpened wide.txt.\nhi"), opened.data);

        const auto unicode_dir = scratch / L"café folder";
        fs::create_directories(unicode_dir);
        const auto unicode_file = unicode_dir / L"über.md";
        const auto unicode_path = mdv::narrow(unicode_file.wstring());
        saved = take(mdv_save(unicode_path.c_str(), "# \xC3\xBC", 4, "UTF-8"));
        opened = take(mdv_open(unicode_path.c_str(), nullptr, nullptr));
        check("unicode paths", saved.ok() && opened.ok() && opened.data.ends_with("# \xC3\xBC"), opened.data);

        check("missing file", take(mdv_open((scratch / "missing.md").string().c_str(), nullptr, nullptr)).data.ends_with("does not exist."));
        const auto png = scratch / "image.png";
        write_all(png, "x");
        const auto unsupported = take(mdv_open(png.string().c_str(), nullptr, nullptr));
        check("unsupported type", unsupported.status == MDV_ERROR && unsupported.data == "md-viewer cannot open .png files.", unsupported.data);
        check("save into missing folder", take(mdv_save((scratch / "nope" / "x.md").string().c_str(), "x", 1, "UTF-8")).status == MDV_ERROR);
        check("no temporary files left", std::none_of(fs::directory_iterator(scratch), fs::directory_iterator(), [](const fs::directory_entry& e) { return e.path().extension() == ".tmp"; }));

        const auto types = take(mdv_file_types());
        check("file types", types.ok() && types.data.find("markdown\t.md\t") != std::string::npos && types.data.find("pandoc\t.docx\t") != std::string::npos &&
                                types.data.find("pdf\t.pdf\t") != std::string::npos && types.data.find("export\t.typ\tTypst document") != std::string::npos);
    }

    // ---- PDF import ---------------------------------------------------------------
    {
        const auto pdf = scratch / "sample.pdf";
        write_all(pdf, minimal_pdf("Imported text from a small PDF document for md-viewer verification purposes only."));
        const auto opened = take(mdv_open(pdf.string().c_str(), nullptr, nullptr));
        check("pdf import", opened.ok() && opened.data.starts_with("imported\nUTF-8\nImported sample.pdf.\n<!--\r\nPDF import: 1 page(s)\r\n") &&
                                opened.data.find("# sample") != std::string::npos && opened.data.find("md-viewer verification") != std::string::npos, opened.data);
        const auto cancelled = take(mdv_open(pdf.string().c_str(), cancel_immediately, nullptr));
        check("pdf cancel", cancelled.status == MDV_CANCELLED);
        const auto scanned = scratch / "scanned.pdf";
        write_all(scanned, scanned_pdf(L"Scanned pages need optical character recognition"));
        mdv_configure_ocr("windows", "eng");
        auto recognized = take(mdv_open(scanned.string().c_str(), nullptr, nullptr));
        if (recognized.data.find("unavailable for the current user languages") != std::string::npos) std::cout << "      Windows OCR unavailable; skipped the Windows OCR check\n";
        else check("pdf windows ocr", recognized.ok() && recognized.data.starts_with("imported\nUTF-8\nImported scanned.pdf; OCR on 1 page(s) with Windows OCR.") &&
                                          recognized.data.find("optical character recognition") != std::string::npos, recognized.data);
        if (const auto tesseract = mdv::find_tesseract()) {
            mdv_configure_ocr("tesseract", "eng+zzz");
            recognized = take(mdv_open(scanned.string().c_str(), nullptr, nullptr));
            check("pdf tesseract ocr", recognized.ok() && recognized.data.starts_with("imported\nUTF-8\nImported scanned.pdf; OCR on 1 page(s) with Tesseract (eng); 1 warning(s).") &&
                                           recognized.data.find("optical character recognition") != std::string::npos &&
                                           recognized.data.find("Tesseract language data is not installed for: zzz.") != std::string::npos, recognized.data);
            mdv_configure_ocr("auto", "eng");
            recognized = take(mdv_open(scanned.string().c_str(), nullptr, nullptr));
            check("auto ocr prefers tesseract", recognized.data.find("with Tesseract (eng).") != std::string::npos, recognized.data.substr(0, 200));
            std::cout << "      tesseract: " << *tesseract << "\n";
        } else {
            std::cout << "      tesseract not found; skipped the Tesseract checks\n";
        }
        mdv_configure_ocr("auto", "eng");
        const auto broken = scratch / "broken.pdf";
        write_all(broken, "not a pdf");
        check("broken pdf", take(mdv_open(broken.string().c_str(), nullptr, nullptr)).data == "The file is not a readable PDF.");
    }

    // ---- Tools ------------------------------------------------------------------------
    {
        check("version order", mdv::compare_versions("5.5.3.20260724", "5.5.0.20241111") > 0 && mdv::compare_versions("3.8", "3.8.0") == 0 &&
                                   mdv::compare_versions("3.9", "3.10") < 0);
        const auto status = take(mdv_tools_status(0, nullptr, nullptr));
        check("tools status", status.ok() && status.data.find("\"id\":\"pandoc\"") != std::string::npos && status.data.find("\"id\":\"tesseract\"") != std::string::npos &&
                                  status.data.find("\"id\":\"windows-ocr\"") != std::string::npos && status.data.find("\"checked_latest\":false") != std::string::npos, status.data);
        check("unknown tool", take(mdv_tool_install("photoshop", nullptr, nullptr)).data == "md-viewer cannot install photoshop.");
        if (mdv::environment("MDV_NETWORK_TESTS") == "1") {
            const auto latest = take(mdv_tools_status(1, nullptr, nullptr));
            check("tools latest", latest.ok() && latest.data.find("\"latest\":") != std::string::npos && latest.data.find("latest_error") == std::string::npos, latest.data);
            std::cout << "      " << latest.data.substr(0, 400) << "\n";
        }
    }

    // ---- Crawler pieces -------------------------------------------------------------
    {
        const auto robots = mdv::RobotsTxt::parse("User-agent: *\nDisallow: /private\nAllow: /private/ok\nDisallow: /*.json$\nCrawl-delay: 3\n", mdv::CrawlerUserAgent);
        const auto at = [](const char* url) { return *mdv::Url::parse(url); };
        check("robots rules", !robots.allowed(at("https://x.test/private/a")) && robots.allowed(at("https://x.test/private/ok/b")) &&
                                  !robots.allowed(at("https://x.test/data/a.json")) && robots.allowed(at("https://x.test/data/a.json?x")) &&
                                  robots.crawl_delay == std::chrono::milliseconds(3000));
        const auto specific = mdv::RobotsTxt::parse("User-agent: *\nDisallow: /\n\nUser-agent: md-viewer\nAllow: /\n", mdv::CrawlerUserAgent);
        check("robots picks the most specific agent", specific.allowed(at("https://x.test/a")));

        const auto base = at("https://Docs.Example.com:443/guide/intro.html#top");
        check("url normalization", base.str() == "https://docs.example.com/guide/intro.html#top", base.str());
        check("url resolution", base.resolve("../api/a b.html?x=1#frag")->str() == "https://docs.example.com/api/a%20b.html?x=1#frag" &&
                                    base.resolve("//cdn.example.com/x")->str() == "https://cdn.example.com/x" && base.resolve("?q")->str() == "https://docs.example.com/guide/intro.html?q" &&
                                    base.resolve("mailto:a@b.c")->scheme == "mailto");
        const auto crawl_base = mdv::crawl_base_url(base);
        check("crawl base", crawl_base.str() == "https://docs.example.com/guide/");
        check("base path", mdv::is_under_base_path(at("https://docs.example.com/Guide/x.html"), crawl_base) && !mdv::is_under_base_path(at("https://docs.example.com/other/"), crawl_base));
        check("html-like extensions", mdv::has_html_like_extension(at("https://x.test/a/")) && mdv::has_html_like_extension(at("https://x.test/a/b")) &&
                                          mdv::has_html_like_extension(at("https://x.test/a.php")) && !mdv::has_html_like_extension(at("https://x.test/a.png")));
        const auto wiki = mdv::MediaWikiPage::from(at("https://en.wikipedia.org/wiki/Hello_World#History"));
        check("mediawiki page", wiki && wiki->title == "Hello World" && wiki->raw_url.str() == "https://en.wikipedia.org/wiki/Hello_World?action=raw" &&
                                    wiki->api_url.str().find("/w/api.php?action=parse&page=Hello_World&") != std::string::npos);
        check("mediawiki namespaces", !mdv::MediaWikiPage::from(at("https://en.wikipedia.org/wiki/Special:Random")));
        check("dynamic shell", mdv::looks_like_dynamic_shell("<html><body><div id=\"root\"></div><script src=\"main.chunk.js\"></script></body></html>") &&
                                   !mdv::looks_like_dynamic_shell("<html><body><p>" + std::string(1500, 'x') + "</p><div id=\"root\"></div></body></html>"));

        check("cleanup", mdv::clean_crawled_markdown("Text[^1] here .\n\n<div>\n\n![img](a.png)\n") == "Text here.", mdv::clean_crawled_markdown("Text[^1] here .\n\n<div>\n\n![img](a.png)\n"));
        check("cleanup keeps nesting and code", mdv::clean_crawled_markdown("- a\n    - b\n\n```\nx  =  1\nreturn value;\nreturn value;\nreturn value;\n```\n") ==
                                                    "- a\n    - b\n\n```\nx  =  1\nreturn value;\nreturn value;\nreturn value;\n```");
        check("cleanup anchors", mdv::clean_crawled_markdown("See <a href=\"https://x.test/a b\">the <b>docs</b></a> now.") == "See [the docs](https://x.test/a%20b) now.");
        check("cleanup spans and divs", mdv::clean_crawled_markdown("<span class=\"x\">kept</span> <span> </span>text</div>") == "kept text");
        check("cleanup boilerplate", mdv::clean_crawled_markdown("Cookie settings apply\n\nA\n\nCookie settings apply\n\nB\n\nCookie settings apply\n") == "A\n\nB");
        const auto wiki_url = at("https://en.wikipedia.org/wiki/Page");
        check("cleanup wiki links", mdv::clean_crawled_markdown("A [link](Other page) and [ext](https://x.test).\n\n## References\n\ngone\n", &wiki_url) ==
                                        "A [link](https://en.wikipedia.org/wiki/Other_page) and [ext](https://x.test).");

        auto document = mdv::html::parse("<html><head><title> T </title></head><body><nav>menu</nav><main id=\"m\" class=\"a b\"><p>One</p><span>x<span>y</span></span><p hidden>z</p></main></body></html>");
        check("html title", document.title() == "T");
        auto* main = mdv::html::select_first(*document.root, mdv::html::Selector("main.b#m, article"));
        check("html selectors", main && mdv::html::select_all(*document.root, mdv::html::Selector("p, [hidden]")).size() == 2);
        for (auto* span : mdv::html::select_all(*main, mdv::html::Selector("span"))) mdv::html::unwrap(span);
        for (auto* hidden : mdv::html::select_all(*main, mdv::html::Selector("[hidden]"))) mdv::html::remove(hidden);
        check("html mutation", main && main->inner_html() == "<p>One</p>xy", main ? main->inner_html() : "");
        std::string nested = "<html><body>";
        for (int i = 0; i < 5000; ++i) nested += "<div>";
        nested += "deep";
        check("html depth limit", mdv::html::parse(nested).root != nullptr);
    }

    // ---- Pandoc (optional) ------------------------------------------------------------
    if (mdv::find_pandoc()) {
        const auto markdown = "---\ntitle: T\n---\nSetext\n======\n\n| a | b |\n|---|---|\n| 1 | 2 |\n"s;
        const auto formatted = take(mdv_pandoc_format(markdown.data(), markdown.size(), nullptr, nullptr));
        check("pandoc format", formatted.ok() && formatted.data.starts_with("---\r\ntitle: T\r\n---\r\n\r\n# Setext") && formatted.data.find("| a") != std::string::npos, formatted.data);
        auto format = [](const std::string& text) { return take(mdv_pandoc_format(text.data(), text.size(), nullptr, nullptr)).data; };
        // Produced text uses the platform's line endings (CRLF on Windows), whatever came in.
        check("format writes platform line endings", format("# T\n\nOne\ntwo.\n\nThree.\n") == "# T\r\n\r\nOne two.\r\n\r\nThree.\r\n", format("# T\n\nOne\ntwo.\n\nThree.\n"));
        check("format keeps CRLF", format("# T\r\n\r\nOne.\r\n\r\nTwo.\r\n") == "# T\r\n\r\nOne.\r\n\r\nTwo.\r\n");
        check("format keeps paragraphs with CR-only endings", format("# T\r\rOne.\r\rTwo.\r") == "# T\r\n\r\nOne.\r\n\r\nTwo.\r\n", format("# T\r\rOne.\r\rTwo.\r"));
        check("format keeps paragraphs with CR CR LF endings", format("# T\r\r\nOne.\r\r\nTwo.\r\r\n") == "# T\r\n\r\nOne.\r\n\r\nTwo.\r\n", format("# T\r\r\nOne.\r\r\nTwo.\r\r\n"));
        check("format keeps CRLF front matter", format("---\r\ntitle: T\r\n---\r\nBody\r\n") == "---\r\ntitle: T\r\n---\r\n\r\nBody\r\n", format("---\r\ntitle: T\r\n---\r\nBody\r\n"));
        const auto html = scratch / "page.html";
        write_all(html, "<html><body><h1 id=\"x\" class=\"c\">Hello</h1><div class=\"note\"><p>World <span class=\"k\">here</span></p></div></body></html>");
        const auto imported = take(mdv_open(html.string().c_str(), nullptr, nullptr));
        check("pandoc import html", imported.ok() && imported.data.starts_with("imported\nUTF-8\nImported page.html.") && imported.data.find("# Hello") != std::string::npos &&
                                        imported.data.find("{#") == std::string::npos && imported.data.find(":::") == std::string::npos &&
                                        imported.data.find("<div") == std::string::npos && imported.data.find("World here") != std::string::npos, imported.data);
        const auto target = scratch / "out.html";
        const auto exported = take(mdv_pandoc_export("# Out\n\ntext\n", 13, target.string().c_str(), scratch.string().c_str(), nullptr, nullptr));
        check("pandoc export", exported.ok() && read_all(target).find("<h1") != std::string::npos, exported.data);
        check("pandoc export rejects unknown types", take(mdv_pandoc_export("x", 1, (scratch / "out.xyz").string().c_str(), nullptr, nullptr, nullptr)).data ==
                                                         "Pandoc export does not support .xyz files.");
        std::vector<std::string> messages;
        const auto cancelled = take(mdv_crawl("http://127.0.0.1:9/", 1, cancel_immediately, nullptr));
        check("crawl cancel", cancelled.status == MDV_CANCELLED);
        std::cout << "      pandoc: " << take(mdv_pandoc_path()).data << "\n";

        if (mdv::environment("MDV_NETWORK_TESTS") == "1") {
            const auto crawled = take(mdv_crawl("https://en.wikipedia.org/wiki/Markdown", 1, collect, &messages));
            const auto model = parse(crawled.data);
            check("live wikipedia crawl", crawled.ok() && crawled.data.starts_with("# Markdown") && crawled.data.find("Gruber") != std::string::npos &&
                                              crawled.data.find(":::") == std::string::npos && crawled.data.find("<span") == std::string::npos && model.headings.size() > 3,
                  crawled.data.substr(0, 600));
            std::string log;
            for (const auto& message : messages)
                if (message.starts_with("WIKI") || message.starts_with("OK")) log += " | " + message;
            std::cout << "      crawled " << crawled.data.size() << " bytes, " << model.headings.size() << " headings" << log << "\n";
        }
    } else {
        std::cout << "      pandoc not found; skipped Pandoc checks\n";
    }

    std::error_code ignored;
    fs::remove_all(scratch, ignored);
    std::cout << (failures == 0 ? "PASS  " + std::to_string(checks) + " native checks" : std::to_string(failures) + " of " + std::to_string(checks) + " native checks failed") << "\n";
    return failures == 0 ? 0 : 1;
}
