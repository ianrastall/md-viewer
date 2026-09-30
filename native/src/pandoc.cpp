#include "pandoc.h"

#include "files.h"
#include "subprocess.h"

#include <windows.h>

#include <mutex>

namespace mdv {

const std::vector<ExportFormat> ExportFormats = {
    {"Word document", ".docx", "docx"},
    {"HTML document", ".html", "html"},
    {"EPUB publication", ".epub", "epub"},
    {"Rich Text Format", ".rtf", "rtf"},
    {"OpenDocument text", ".odt", "odt"},
    {"LaTeX document", ".tex", "latex"},
    {"Typst document", ".typ", "typst"},
    {"reStructuredText", ".rst", "rst"},
    {"Org mode document", ".org", "org"},
};

const std::vector<std::pair<const char*, const char*>> PandocImports = {
    {".docx", "docx"}, {".html", "html"}, {".htm", "html"}, {".epub", "epub"}, {".odt", "odt"}, {".rtf", "rtf"},
};

namespace {

// Pandoc's Markdown minus what md-viewer's renderer cannot display (attribute blocks, fenced divs,
// bracketed spans, non-pipe tables), with ATX headings and no hard wrapping.
constexpr const char* MarkdownWriter =
    "markdown-header_attributes-link_attributes-fenced_code_attributes-fenced_divs-native_divs"
    "-bracketed_spans-native_spans-grid_tables-multiline_tables-simple_tables";
// With raw HTML enabled, Pandoc would still write <div>/<span> wrappers; unwrap them and keep their content.
constexpr std::string_view UnwrapFilter = "function Div(el) return el.content end\nfunction Span(el) return el.content end\n";

std::mutex data_mutex;
std::string data_folder;

std::string default_data_directory() { return join_path(environment("LOCALAPPDATA"), "md-viewer"); }

std::string module_directory() {
    wchar_t path[32768];
    const DWORD length = GetModuleFileNameW(nullptr, path, 32768);
    return directory_of(narrow(std::wstring_view(path, length)));
}

std::string filter_path() {
    const auto directory = data_directory();
    create_directories(directory);
    const auto path = join_path(directory, "unwrap-v1.lua");
    std::string existing;
    try { existing = read_file(path, 4096); } catch (const Error&) {}
    if (existing != UnwrapFilter) write_file_atomic(path, UnwrapFilter);
    return path;
}

std::vector<std::string> writer_options() {
    return {"--wrap=none", "--markdown-headings=atx", "--lua-filter=" + filter_path()};
}

std::string run(std::vector<std::string> arguments, const std::optional<std::string>& input, const std::string& working_directory,
                std::optional<std::chrono::milliseconds> timeout, const Progress& progress) {
    const auto result = run_process(pandoc_path(), arguments, input, working_directory, timeout, progress);
    if (result.exit_code != 0)
        throw Error(std::string(trim("Pandoc exited with code " + std::to_string(result.exit_code) + ". " + std::string(trim(result.error)))));
    return result.output;
}

}  // namespace

void set_data_directory(std::string directory) {
    std::lock_guard lock(data_mutex);
    data_folder = std::move(directory);
}

const std::string& data_directory() {
    std::lock_guard lock(data_mutex);
    if (data_folder.empty()) data_folder = default_data_directory();
    return data_folder;
}

std::optional<std::string> find_pandoc() {
    std::vector<std::string> candidates = {join_path(join_path(data_directory(), "pandoc"), "pandoc.exe"), join_path(module_directory(), "pandoc.exe")};
    const auto path = environment("PATH");
    for (size_t start = 0; start <= path.size();) {
        auto end = path.find(';', start);
        if (end == std::string::npos) end = path.size();
        const auto entry = trim(std::string_view(path).substr(start, end - start));
        if (!entry.empty()) candidates.push_back(join_path(replace_all(std::string(entry), "\"", ""), "pandoc.exe"));
        start = end + 1;
    }
    candidates.push_back(join_path(join_path(environment("LOCALAPPDATA"), "Pandoc"), "pandoc.exe"));
    candidates.push_back(join_path(join_path(environment("ProgramFiles"), "Pandoc"), "pandoc.exe"));
    for (const auto& candidate : candidates)
        if (file_exists(candidate)) return candidate;
    return std::nullopt;
}

std::string pandoc_path() {
    if (auto found = find_pandoc()) return *found;
    throw Error("Pandoc was not found. Use Fetch Pandoc (in the \xE2\x8B\xAF menu) to download it, or install Pandoc and add it to PATH.");
}

std::string pandoc_import(const std::string& path, const Progress& progress) {
    if (!file_exists(path)) throw Error(path + " does not exist.");
    std::vector<std::string> arguments = {"-s", path, "-t", MarkdownWriter};
    for (auto& option : writer_options()) arguments.push_back(std::move(option));
    const auto extension = extension_of(path);
    if (extension == ".html" || extension == ".htm") arguments.insert(arguments.begin(), {"-f", "html"});
    return run(arguments, std::nullopt, directory_of(path), std::nullopt, progress);
}

std::string pandoc_format(std::string_view markdown, const Progress& progress) {
    auto [metadata, body] = split_metadata_block(markdown);
    std::vector<std::string> arguments = {"-f", "markdown-yaml_metadata_block", "-t", MarkdownWriter};
    for (auto& option : writer_options()) arguments.push_back(std::move(option));
    // The metadata block is kept verbatim; Pandoc would otherwise rewrite or drop it.
    auto formatted = run(arguments, body, "", std::nullopt, progress);
    if (!metadata) return formatted;
    return trim(formatted).empty() ? *metadata + "\n" : *metadata + "\n\n" + std::string(trim_start(formatted));
}

void pandoc_export(std::string_view markdown, const std::string& target, const std::string& resource_directory, const Progress& progress) {
    const auto extension = extension_of(target);
    const ExportFormat* format = nullptr;
    for (const auto& candidate : ExportFormats)
        if (extension == candidate.extension) format = &candidate;
    if (!format) throw Error("Pandoc export does not support " + (extension.empty() ? std::string("extensionless") : extension) + " files.");
    if (!directory_exists(directory_of(target))) throw Error("The folder for " + target + " does not exist.");
    std::vector<std::string> arguments = {"-s", "-f", "markdown", "-t", format->writer, "-o", target};
    if (!resource_directory.empty()) arguments.push_back("--resource-path=" + resource_directory);
    run(arguments, std::string(markdown), resource_directory, std::nullopt, progress);
}

std::string pandoc_convert(const std::string& from, std::string_view input, const Progress& progress) {
    std::vector<std::string> arguments = {"--from", from, "--to", MarkdownWriter};
    for (auto& option : writer_options()) arguments.push_back(std::move(option));
    arguments.push_back("--strip-comments");
    try {
        return run(arguments, std::string(input), "", std::chrono::seconds(90), progress);
    } catch (const Error& ex) {
        progress.report(std::string("PANDOC failed; using empty fallback. ") + ex.what());
        return "";
    }
}

std::pair<std::optional<std::string>, std::string> split_metadata_block(std::string_view markdown) {
    const auto normalized = normalize_newlines(markdown);
    if (!normalized.starts_with("---\n") || normalized.size() <= 4 || normalized[4] == '\n') return {std::nullopt, std::string(markdown)};
    for (size_t position = 4; position < normalized.size();) {
        const auto end = normalized.find('\n', position);
        const auto next = end == std::string::npos ? normalized.size() : end + 1;
        const auto line = trim(std::string_view(normalized).substr(position, (end == std::string::npos ? normalized.size() : end) - position));
        if (line == "---" || line == "...") {
            auto body = normalized.substr(next);
            body.erase(0, body.find_first_not_of('\n') == std::string::npos ? body.size() : body.find_first_not_of('\n'));
            return {std::string(trim_end(std::string_view(normalized).substr(0, next))), body};
        }
        position = next;
    }
    return {std::nullopt, std::string(markdown)};
}

}  // namespace mdv
