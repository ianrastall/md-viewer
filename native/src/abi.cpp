// The exported C ABI: argument checks, exception containment, and result buffers.
#include "mdv_native.h"

#include "common.h"
#include "crawl.h"
#include "tools.h"
#include "files.h"
#include "markdown.h"
#include "pandoc.h"
#include "pdf.h"

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <new>

namespace mdv {
namespace {

constexpr const char* MarkdownExtensions[][2] = {{".md", "Markdown"}, {".markdown", "Markdown"}, {".mdown", "Markdown"}, {".mkd", "Markdown"}, {".txt", "Plain text"}};

mdv_result* make_result(int32_t status, std::string_view data) {
    auto* result = static_cast<mdv_result*>(std::malloc(sizeof(mdv_result)));
    if (!result) return nullptr;
    result->status = status;
    result->reserved = 0;
    result->size = data.size();
    result->data = static_cast<char*>(std::malloc(data.size() + 1));
    if (!result->data) {
        std::free(result);
        return nullptr;
    }
    if (!data.empty()) std::memcpy(result->data, data.data(), data.size());
    result->data[data.size()] = '\0';
    return result;
}

template <typename Work>
mdv_result* guard(Work&& work) noexcept {
    try {
        const auto payload = work();
        return make_result(MDV_OK, std::string_view(reinterpret_cast<const char*>(payload.data()), payload.size()));
    } catch (const Cancelled&) {
        return make_result(MDV_CANCELLED, "Cancelled.");
    } catch (const Error& ex) {
        return make_result(MDV_ERROR, ex.what());
    } catch (const std::bad_alloc&) {
        return nullptr;
    } catch (const std::exception& ex) {
        return make_result(MDV_ERROR, std::string("Unexpected error: ") + ex.what());
    } catch (...) {
        return make_result(MDV_ERROR, "Unexpected error in the native core.");
    }
}

std::string_view text_argument(const char* utf8, size_t length) {
    if (!utf8 && length) throw Error("Missing text.");
    return utf8 ? std::string_view(utf8, length) : std::string_view();
}

std::string required(const char* value, const char* what) {
    if (!value || !*value) throw Error(std::string("Missing ") + what + ".");
    return value;
}

bool is_markdown_path(const std::string& path) {
    const auto extension = extension_of(path);
    return extension.empty() || std::any_of(std::begin(MarkdownExtensions), std::end(MarkdownExtensions), [&](const auto& entry) { return extension == entry[0]; });
}

bool is_pandoc_import(const std::string& path) {
    const auto extension = extension_of(path);
    return std::any_of(PandocImports.begin(), PandocImports.end(), [&](const auto& entry) { return extension == entry.first; });
}

std::string open_document(const std::string& path, const Progress& progress) {
    const auto name = file_name_of(path);
    if (is_markdown_path(path)) {
        const auto file = read_document(path);
        return "markdown\n" + file.encoding + "\nOpened " + name + ".\n" + file.text;
    }
    if (is_pandoc_import(path)) {
        progress.report("Converting " + name + " with Pandoc...");
        return "imported\nUTF-8\nImported " + name + ". Save to keep the Markdown.\n" + pandoc_import(path, progress);
    }
    if (extension_of(path) == ".pdf") {
        const auto pdf = import_pdf(path, progress);
        std::string summary = "Imported " + name;
        if (!pdf.ocr_pages.empty()) summary += "; OCR on " + format_count(static_cast<long long>(pdf.ocr_pages.size())) + " page(s) with " + pdf.ocr_engine;
        if (!pdf.missing_pages.empty()) summary += "; " + format_count(static_cast<long long>(pdf.missing_pages.size())) + " unreadable page(s)";
        else if (!pdf.warnings.empty()) summary += "; " + format_count(static_cast<long long>(pdf.warnings.size())) + " warning(s)";
        return "imported\nUTF-8\n" + summary + ".\n" + pdf.markdown;
    }
    throw Error("md-viewer cannot open " + extension_of(path) + " files.");
}

}  // namespace
}  // namespace mdv

using namespace mdv;

MDV_API int32_t mdv_abi_version(void) { return MDV_ABI_VERSION; }

MDV_API void mdv_result_free(mdv_result* result) {
    if (!result) return;
    std::free(result->data);
    std::free(result);
}

MDV_API void mdv_configure(const char* data_directory) {
    try { set_data_directory(data_directory ? data_directory : ""); } catch (...) {}
}

MDV_API void mdv_configure_ocr(const char* engine, const char* languages) {
    try { set_ocr_preferences({engine ? engine : "auto", languages ? languages : "eng"}); } catch (...) {}
}

MDV_API mdv_result* mdv_file_types(void) {
    return guard([] {
        std::string lines;
        for (const auto& [extension, label] : MarkdownExtensions) lines += std::string("markdown\t") + extension + "\t" + label + "\n";
        for (const auto& [extension, reader] : PandocImports) lines += std::string("pandoc\t") + extension + "\t" + reader + "\n";
        lines += "pdf\t.pdf\tPDF\n";
        for (const auto& format : ExportFormats) lines += std::string("export\t") + format.extension + "\t" + format.label + "\n";
        return lines;
    });
}

MDV_API mdv_result* mdv_parse(const char* utf8, size_t length) {
    return guard([&] { return parse_markdown(text_argument(utf8, length)); });
}

MDV_API mdv_result* mdv_reflow_headings(const char* utf8, size_t length) {
    return guard([&] { return reflow_headings(text_argument(utf8, length)); });
}

MDV_API mdv_result* mdv_open(const char* path, mdv_progress_fn progress, void* context) {
    return guard([&] { return open_document(required(path, "path"), Progress(progress, context)); });
}

MDV_API mdv_result* mdv_save(const char* path, const char* utf8, size_t length, const char* encoding) {
    return guard([&] { return write_document(required(path, "path"), text_argument(utf8, length), encoding ? encoding : "UTF-8"); });
}

MDV_API mdv_result* mdv_pandoc_path(void) {
    return guard([] { return pandoc_path(); });
}

MDV_API mdv_result* mdv_pandoc_format(const char* utf8, size_t length, mdv_progress_fn progress, void* context) {
    return guard([&] { return pandoc_format(text_argument(utf8, length), Progress(progress, context)); });
}

MDV_API mdv_result* mdv_pandoc_export(const char* utf8, size_t length, const char* target, const char* resource_directory,
                                      mdv_progress_fn progress, void* context) {
    return guard([&] {
        pandoc_export(text_argument(utf8, length), required(target, "target path"), resource_directory ? resource_directory : "", Progress(progress, context));
        return std::string();
    });
}

MDV_API mdv_result* mdv_tools_status(int32_t check_latest, mdv_progress_fn progress, void* context) {
    return guard([&] { return tools_status_json(check_latest != 0, Progress(progress, context)); });
}

MDV_API mdv_result* mdv_tool_install(const char* tool, mdv_progress_fn progress, void* context) {
    return guard([&] { return install_tool(required(tool, "tool"), Progress(progress, context)); });
}

MDV_API mdv_result* mdv_crawl(const char* start_url, int32_t max_pages, mdv_progress_fn progress, void* context) {
    return guard([&] {
        CrawlOptions options;
        options.max_pages = std::clamp(max_pages, 1, 250);
        return crawl(required(start_url, "address"), options, Progress(progress, context));
    });
}
