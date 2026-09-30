#include "pdf.h"

#include <windows.h>

#include "fpdf_text.h"
#include "fpdfview.h"

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Media.Ocr.h>
#include <winrt/Windows.Storage.Streams.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <mutex>

namespace mdv {
namespace {

using winrt::Windows::Graphics::Imaging::BitmapAlphaMode;
using winrt::Windows::Graphics::Imaging::BitmapPixelFormat;
using winrt::Windows::Graphics::Imaging::SoftwareBitmap;
using winrt::Windows::Media::Ocr::OcrEngine;

constexpr double OcrTargetScale = 3.0;  // pixels per PDF point
constexpr int ReadableLetters = 10, ReadableWords = 3, CandidateLetters = 40, CandidateWords = 8;

// PDFium is not thread-safe; every use goes through this lock.
std::mutex pdfium_mutex;

void ensure_pdfium() {
    static std::once_flag once;
    std::call_once(once, [] {
        FPDF_LIBRARY_CONFIG config{};
        config.version = 2;
        FPDF_InitLibraryWithConfig(&config);
    });
}

struct FileAccess {
    HANDLE file = INVALID_HANDLE_VALUE;
    ~FileAccess() { if (file != INVALID_HANDLE_VALUE) CloseHandle(file); }
    static int get_block(void* param, unsigned long position, unsigned char* buffer, unsigned long size) {
        auto* self = static_cast<FileAccess*>(param);
        OVERLAPPED at{};
        at.Offset = position;
        DWORD read = 0;
        return ReadFile(self->file, buffer, size, &read, &at) && read == size ? 1 : 0;
    }
};

// ---- Text heuristics (UTF-16, matching the original importer) ----

bool is_letter(wchar_t c) { return IsCharAlphaW(c) != FALSE; }
bool is_control(wchar_t c) { return (c < 0x20 || (c >= 0x7F && c <= 0x9F)) && c != L'\r' && c != L'\n' && c != L'\t'; }
bool blank(const std::wstring& text) { return std::all_of(text.begin(), text.end(), [](wchar_t c) { return iswspace(c); }); }

int count_letters(const std::wstring& text) { return static_cast<int>(std::count_if(text.begin(), text.end(), is_letter)); }

int count_words(const std::wstring& text) {  // runs of two or more letters
    int words = 0, run = 0;
    for (const wchar_t c : text) {
        if (is_letter(c)) { if (++run == 2) ++words; }
        else run = 0;
    }
    return words;
}

int quality(const std::wstring& text) {
    if (blank(text)) return 0;
    const auto replacements = std::count(text.begin(), text.end(), L'�');
    const auto controls = std::count_if(text.begin(), text.end(), is_control);
    return count_letters(text) + count_words(text) * 5 - static_cast<int>(replacements) * 20 - static_cast<int>(controls) * 10;
}

bool readable(const std::wstring& text) { return quality(text) >= ReadableLetters || count_words(text) >= ReadableWords; }

bool weak_text_layer(const std::wstring& text) {
    if (blank(text)) return true;
    const int letters = count_letters(text);
    const double ratio = static_cast<double>(letters) / static_cast<double>(text.size());
    return count_words(text) < CandidateWords || letters < CandidateLetters || std::count(text.begin(), text.end(), L'�') > 0 ||
           std::count_if(text.begin(), text.end(), is_control) > 0 || ratio < 0.25;
}

bool prefer_ocr(const std::wstring& embedded, const std::wstring& ocr) {
    if (!readable(ocr)) return false;
    if (!readable(embedded)) return true;
    const int embedded_score = quality(embedded), ocr_score = quality(ocr);
    return ocr_score > embedded_score * 2 || ocr_score >= embedded_score + 120;
}

// Keeps line structure; joins words hyphenated across lines ("infor-\nmation").
std::wstring normalize_page(std::wstring text) {
    std::wstring normalized;
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] == L'\r') { normalized += L'\n'; if (i + 1 < text.size() && text[i + 1] == L'\n') ++i; }
        else normalized += text[i];
    }
    std::wstring joined;
    for (size_t i = 0; i < normalized.size(); ++i) {
        if (normalized[i] == L'-' && i >= 3 && i + 4 < normalized.size() && normalized[i + 1] == L'\n' &&
            is_letter(normalized[i - 1]) && is_letter(normalized[i - 2]) && is_letter(normalized[i - 3]) &&
            IsCharLowerW(normalized[i + 2]) && IsCharLowerW(normalized[i + 3]) && IsCharLowerW(normalized[i + 4])) {
            ++i;
            continue;
        }
        joined += normalized[i];
    }
    std::wstring out;
    size_t start = 0;
    while (start <= joined.size()) {
        auto end = joined.find(L'\n', start);
        if (end == std::wstring::npos) end = joined.size();
        std::wstring line = joined.substr(start, end - start);
        std::replace(line.begin(), line.end(), L'\t', L' ');
        while (!line.empty() && iswspace(line.back())) line.pop_back();
        if (start) out += L'\n';
        out += line;
        start = end + 1;
    }
    const auto first = out.find_first_not_of(L" \t\r\n");
    const auto last = out.find_last_not_of(L" \t\r\n");
    out = first == std::wstring::npos ? std::wstring() : out.substr(first, last - first + 1);
    std::wstring collapsed;
    size_t newlines = 0;
    for (const wchar_t c : out) {
        newlines = c == L'\n' ? newlines + 1 : 0;
        if (newlines <= 2) collapsed += c;
    }
    return collapsed;
}

std::wstring page_text(FPDF_PAGE page) {
    FPDF_TEXTPAGE text_page = FPDFText_LoadPage(page);
    if (!text_page) return {};
    std::wstring text;
    const int count = FPDFText_CountChars(text_page);
    if (count > 0) {
        std::vector<unsigned short> buffer(static_cast<size_t>(count) + 1);
        const int written = FPDFText_GetText(text_page, 0, count, buffer.data());
        if (written > 0) text.assign(buffer.begin(), buffer.begin() + (written - 1));
    }
    FPDFText_ClosePage(text_page);
    // PDFium marks some generated characters as U+0002 (hyphen) or U+FFFE; keep text honest for the heuristics.
    std::replace(text.begin(), text.end(), static_cast<wchar_t>(2), L'-');
    text.erase(std::remove(text.begin(), text.end(), static_cast<wchar_t>(0xFFFE)), text.end());
    return text;
}

std::wstring recognize(FPDF_PAGE page, const OcrEngine& engine) {
    const double width = FPDF_GetPageWidthF(page), height = FPDF_GetPageHeightF(page);
    if (width <= 0 || height <= 0) throw Error("PDF page has invalid dimensions.");
    const double limit = OcrEngine::MaxImageDimension();
    const double scale = std::min({OcrTargetScale, limit / width, limit / height});
    const int pixels_wide = std::max(1, static_cast<int>(std::lround(width * scale)));
    const int pixels_high = std::max(1, static_cast<int>(std::lround(height * scale)));
    FPDF_BITMAP bitmap = FPDFBitmap_CreateEx(pixels_wide, pixels_high, FPDFBitmap_BGRA, nullptr, 0);
    if (!bitmap) throw Error("Could not render the page for OCR.");
    FPDFBitmap_FillRect(bitmap, 0, 0, pixels_wide, pixels_high, 0xFFFFFFFF);
    FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixels_wide, pixels_high, 0, FPDF_ANNOT | FPDF_PRINTING);
    const int stride = FPDFBitmap_GetStride(bitmap);
    const auto* pixels = static_cast<const unsigned char*>(FPDFBitmap_GetBuffer(bitmap));
    const uint32_t row = static_cast<uint32_t>(pixels_wide) * 4;
    winrt::Windows::Storage::Streams::Buffer buffer(row * static_cast<uint32_t>(pixels_high));
    for (int y = 0; y < pixels_high; ++y) {
        auto* target = buffer.data() + static_cast<size_t>(y) * row;
        std::memcpy(target, pixels + static_cast<size_t>(y) * static_cast<size_t>(stride), row);
        for (uint32_t x = 3; x < row; x += 4) target[x] = 0xFF;  // opaque
    }
    buffer.Length(row * static_cast<uint32_t>(pixels_high));
    FPDFBitmap_Destroy(bitmap);
    const auto software = SoftwareBitmap::CreateCopyFromBuffer(buffer, BitmapPixelFormat::Bgra8, pixels_wide, pixels_high, BitmapAlphaMode::Premultiplied);
    const auto result = engine.RecognizeAsync(software).get();
    std::wstring text;
    for (const auto& line : result.Lines()) {
        const std::wstring value(line.Text());
        if (blank(value)) continue;
        if (!text.empty()) text += L'\n';
        text += value;
    }
    return text;
}

std::string sanitize_title(const std::string& title) {
    std::string out;
    for (const char c : title)
        if (std::string_view("#*_`[]<>\\").find(c) == std::string_view::npos) out += c;
    const auto trimmed = std::string(trim(out));
    return trimmed.empty() ? "Untitled" : trimmed;
}

std::string join_numbers(const std::vector<int>& numbers) {
    if (numbers.empty()) return "none";
    std::string out;
    for (size_t i = 0; i < numbers.size(); ++i) out += (i ? ", " : "") + std::to_string(numbers[i]);
    return out;
}

PdfImport import(const std::string& path, const Progress& progress) {
    progress.report("Reading PDF text...");
    std::lock_guard lock(pdfium_mutex);
    ensure_pdfium();

    FileAccess file;
    file.file = CreateFileW(widen(path).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file.file == INVALID_HANDLE_VALUE) {
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) throw Error("Input PDF not found at " + path);
        throw_win32("Could not open " + path + ".", error);
    }
    LARGE_INTEGER size{};
    GetFileSizeEx(file.file, &size);
    if (size.QuadPart > 0xFFFFFFFFll) throw Error("PDFs larger than 4 GB are not supported.");
    FPDF_FILEACCESS access{};
    access.m_FileLen = static_cast<unsigned long>(size.QuadPart);
    access.m_GetBlock = &FileAccess::get_block;
    access.m_Param = &file;
    FPDF_DOCUMENT document = FPDF_LoadCustomDocument(&access, nullptr);
    if (!document) {
        if (FPDF_GetLastError() == FPDF_ERR_PASSWORD) throw Error("The PDF is password-protected.");
        throw Error("The file is not a readable PDF.");
    }
    struct Closer { FPDF_DOCUMENT document; ~Closer() { FPDF_CloseDocument(document); } } closer{document};

    PdfImport result;
    result.page_count = FPDF_GetPageCount(document);
    if (result.page_count <= 0) throw Error("The PDF does not contain any pages.");
    std::vector<std::wstring> texts(static_cast<size_t>(result.page_count));
    for (int i = 0; i < result.page_count; ++i) {
        progress.check();
        if (FPDF_PAGE page = FPDF_LoadPage(document, i)) {
            texts[static_cast<size_t>(i)] = page_text(page);
            FPDF_ClosePage(page);
        }
    }

    if (std::any_of(texts.begin(), texts.end(), weak_text_layer)) {
        progress.report("Running Windows OCR for pages with little or no extractable text...");
        try { winrt::init_apartment(winrt::apartment_type::multi_threaded); } catch (const winrt::hresult_error&) {}
        OcrEngine engine{nullptr};
        try { engine = OcrEngine::TryCreateFromUserProfileLanguages(); } catch (const winrt::hresult_error&) {}
        if (!engine) {
            result.warnings.push_back("Windows OCR is unavailable for the current user languages.");
            progress.report(result.warnings.back());
        } else {
            for (int i = 0; i < result.page_count; ++i) {
                progress.check();
                auto& text = texts[static_cast<size_t>(i)];
                if (!weak_text_layer(text)) continue;
                const int number = i + 1;
                progress.report("Running OCR on page " + format_count(number) + " of " + format_count(result.page_count) + "...");
                try {
                    FPDF_PAGE page = FPDF_LoadPage(document, i);
                    if (!page) throw Error("the page could not be loaded.");
                    std::wstring recognized;
                    try { recognized = recognize(page, engine); } catch (...) { FPDF_ClosePage(page); throw; }
                    FPDF_ClosePage(page);
                    if (blank(recognized)) continue;
                    if (prefer_ocr(text, recognized)) {
                        text = recognized;
                        result.ocr_pages.push_back(number);
                    } else if (readable(recognized)) {
                        result.warnings.push_back("OCR on page " + format_count(number) + " was not used because embedded text looked better.");
                    }
                } catch (const Cancelled&) {
                    throw;
                } catch (const winrt::hresult_error& ex) {
                    result.warnings.push_back("OCR failed on page " + format_count(number) + ": " + winrt::to_string(ex.message()));
                    progress.report(result.warnings.back());
                } catch (const std::exception& ex) {
                    result.warnings.push_back("OCR failed on page " + format_count(number) + ": " + ex.what());
                    progress.report(result.warnings.back());
                }
            }
        }
    }

    for (int i = 0; i < result.page_count; ++i)
        if (!readable(texts[static_cast<size_t>(i)])) result.missing_pages.push_back(i + 1);
    if (static_cast<int>(result.missing_pages.size()) == result.page_count) throw Error("No readable text could be extracted from this PDF.");
    if (!result.missing_pages.empty()) result.warnings.push_back("No readable text was extracted from page(s): " + join_numbers(result.missing_pages) + ".");
    for (int i = 0; i < result.page_count; ++i) {
        const bool ocr = std::find(result.ocr_pages.begin(), result.ocr_pages.end(), i + 1) != result.ocr_pages.end();
        if (!ocr && readable(texts[static_cast<size_t>(i)])) ++result.embedded_pages;
    }

    std::string markdown = "<!--\nPDF import: " + std::to_string(result.page_count) + " page(s)\nEmbedded text pages: " +
                           std::to_string(result.embedded_pages) + "\nOCR pages: " + join_numbers(result.ocr_pages) +
                           "\nMissing pages: " + join_numbers(result.missing_pages) + "\n";
    for (const auto& warning : result.warnings) markdown += "Warning: " + warning + "\n";
    markdown += "-->\n\n# " + sanitize_title(stem_of(path)) + "\n\n";
    for (int i = 0; i < result.page_count; ++i) {
        const auto& text = texts[static_cast<size_t>(i)];
        if (!readable(text)) continue;
        const auto page = narrow(normalize_page(text));
        if (trim(page).empty()) continue;
        markdown += "<!-- Page " + std::to_string(i + 1) + " -->\n\n" + page + "\n\n";
    }
    result.markdown = std::string(trim_end(markdown)) + "\n";
    return result;
}

}  // namespace

PdfImport import_pdf(const std::string& path, const Progress& progress) {
    try {
        return import(path, progress);
    } catch (const Cancelled&) {
        throw;
    } catch (const Error&) {
        throw;
    } catch (const winrt::hresult_error& ex) {
        throw Error("PDF import failed for '" + file_name_of(path) + "'. " + winrt::to_string(ex.message()));
    } catch (const std::exception& ex) {
        throw Error("PDF import failed for '" + file_name_of(path) + "'. " + ex.what());
    }
}

}  // namespace mdv
