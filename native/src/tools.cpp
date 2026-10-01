#include "tools.h"

#include "files.h"
#include "http.h"
#include "pandoc.h"
#include "pdf.h"
#include "subprocess.h"

#include "miniz.h"
#include "nlohmann/json.hpp"

#include <windows.h>
#include <bcrypt.h>
#include <shellapi.h>

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Media.Ocr.h>

#include <algorithm>
#include <cctype>

namespace mdv {
namespace {

using nlohmann::json;

constexpr const char* UserAgent = "md-viewer/2.1";
constexpr const char* PandocLatest = "https://api.github.com/repos/jgm/pandoc/releases/latest";
constexpr const char* PandocDownloads = "https://github.com/jgm/pandoc/releases/download/";
constexpr const char* TesseractReleases = "https://api.github.com/repos/tesseract-ocr/tesseract/releases?per_page=10";
constexpr const char* TesseractDownloads = "https://github.com/tesseract-ocr/tesseract/releases/download/";

class Sha256 {
public:
    Sha256() {
        if (BCryptOpenAlgorithmProvider(&algorithm_, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0 ||
            BCryptCreateHash(algorithm_, &hash_, nullptr, 0, nullptr, 0, 0) != 0)
            throw Error("SHA-256 is unavailable.");
    }
    ~Sha256() {
        if (hash_) BCryptDestroyHash(hash_);
        if (algorithm_) BCryptCloseAlgorithmProvider(algorithm_, 0);
    }
    Sha256(const Sha256&) = delete;
    Sha256& operator=(const Sha256&) = delete;
    void add(const char* data, size_t size) { BCryptHashData(hash_, reinterpret_cast<PUCHAR>(const_cast<char*>(data)), static_cast<ULONG>(size), 0); }
    std::string hex() {
        unsigned char digest[32];
        BCryptFinishHash(hash_, digest, sizeof digest, 0);
        static constexpr char digits[] = "0123456789abcdef";
        std::string text;
        for (const unsigned char byte : digest) { text += digits[byte >> 4]; text += digits[byte & 15]; }
        return text;
    }

private:
    BCRYPT_ALG_HANDLE algorithm_ = nullptr;
    BCRYPT_HASH_HANDLE hash_ = nullptr;
};

struct Release {
    std::string version, url, name;
    std::optional<std::string> sha256;
};

std::optional<std::string> sha256_of(const json& asset) {
    const auto digest = asset.value("digest", std::string());
    return istarts_with(digest, "sha256:") ? std::optional(lower(digest.substr(7))) : std::nullopt;
}

json get_json(HttpClient& http, const std::string& url, const Progress& progress) {
    const auto response = http.get(url, {"application/vnd.github+json"}, 16ull << 20, progress);
    if (!response.ok()) throw Error("GitHub returned HTTP " + std::to_string(response.status) + ".");
    auto parsed = json::parse(response.body, nullptr, false);
    if (parsed.is_discarded()) throw Error("GitHub's release information could not be read.");
    return parsed;
}

Release latest_pandoc(HttpClient& http, const Progress& progress) {
    const auto release = get_json(http, PandocLatest, progress);
    for (const auto& asset : release.value("assets", json::array())) {
        const auto name = asset.value("name", std::string());
        const auto url = asset.value("browser_download_url", std::string());
        constexpr std::string_view prefix = "pandoc-", suffix = "-windows-x86_64.zip";
        if (!istarts_with(name, prefix) || !iends_with(name, suffix) || name.size() <= prefix.size() + suffix.size() || !istarts_with(url, PandocDownloads)) continue;
        return {name.substr(prefix.size(), name.size() - prefix.size() - suffix.size()), url, name, sha256_of(asset)};
    }
    throw Error("The latest Pandoc release has no Windows x64 ZIP.");
}

// The Tesseract project publishes the UB Mannheim Windows installer with some releases; take the newest that has one.
Release latest_tesseract(HttpClient& http, const Progress& progress) {
    for (const auto& release : get_json(http, TesseractReleases, progress)) {
        if (release.value("draft", false) || release.value("prerelease", false)) continue;
        for (const auto& asset : release.value("assets", json::array())) {
            const auto name = asset.value("name", std::string());
            const auto url = asset.value("browser_download_url", std::string());
            constexpr std::string_view prefix = "tesseract-ocr-w64-setup-", suffix = ".exe";
            if (!istarts_with(name, prefix) || !iends_with(name, suffix) || name.size() <= prefix.size() + suffix.size() || !istarts_with(url, TesseractDownloads)) continue;
            return {name.substr(prefix.size(), name.size() - prefix.size() - suffix.size()), url, name, sha256_of(asset)};
        }
    }
    throw Error("No recent Tesseract release includes a Windows installer.");
}

std::string first_line(const std::string& text) {
    const auto line = trim(std::string_view(text).substr(0, text.find_first_of("\r\n")));
    return std::string(line);
}

// "pandoc 3.8.3" or "tesseract v5.5.3.20260724": the last word, without a leading v.
std::optional<std::string> program_version(const std::string& executable) {
    try {
        const auto result = run_process(executable, {"--version"}, std::nullopt, "", std::chrono::seconds(20), Progress());
        auto line = first_line(result.output.empty() ? result.error : result.output);
        auto word = line.substr(line.find_last_of(' ') == std::string::npos ? 0 : line.find_last_of(' ') + 1);
        if (!word.empty() && (word[0] == 'v' || word[0] == 'V')) word.erase(0, 1);
        if (word.empty() || !std::isdigit(static_cast<unsigned char>(word[0]))) return std::nullopt;
        return word;
    } catch (const std::exception&) {
        return std::nullopt;
    }
}

std::optional<std::string> registry_string(HKEY root, const wchar_t* key, const wchar_t* name) {
    wchar_t buffer[1024];
    DWORD size = sizeof buffer;
    if (RegGetValueW(root, key, name, RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, buffer, &size) != ERROR_SUCCESS) return std::nullopt;
    return narrow(buffer);
}

std::string download(HttpClient& http, const Release& release, const std::string& what, const Progress& progress) {
    progress.report("Downloading " + what + " " + release.version + "...");
    std::string bytes;
    Sha256 hash;
    int next_report = 5;
    const auto response = http.get(release.url, {"application/octet-stream", "*/*"}, 1ull << 30, progress,
                                   [&](const char* data, size_t size, unsigned long long total, std::optional<unsigned long long> length) {
                                       bytes.append(data, size);
                                       hash.add(data, size);
                                       if (length && *length > 0 && static_cast<int>(total * 100 / *length) >= next_report) {
                                           const int percent = static_cast<int>(total * 100 / *length);
                                           progress.report("Downloading " + what + " " + std::to_string(percent) + "%...");
                                           next_report = percent + 5;
                                       }
                                   });
    if (!response.ok()) throw Error("The " + what + " download failed with HTTP " + std::to_string(response.status) + ".");
    if (release.sha256 && hash.hex() != *release.sha256) throw Error("The " + what + " download did not match GitHub's published SHA-256 digest.");
    return bytes;
}

std::string extract_pandoc(const std::string& zip) {
    mz_zip_archive archive{};
    if (!mz_zip_reader_init_mem(&archive, zip.data(), zip.size(), 0)) throw Error("The Pandoc download is not a valid ZIP archive.");
    std::string executable;
    const mz_uint count = mz_zip_reader_get_num_files(&archive);
    for (mz_uint i = 0; i < count && executable.empty(); ++i) {
        char name[1024];
        mz_zip_reader_get_filename(&archive, i, name, sizeof name);
        const std::string_view entry = name;
        if (!(iequals(entry, "pandoc.exe") || iends_with(entry, "/pandoc.exe"))) continue;
        size_t size = 0;
        void* data = mz_zip_reader_extract_to_heap(&archive, i, &size, 0);
        if (!data) break;
        executable.assign(static_cast<const char*>(data), size);
        mz_free(data);
    }
    mz_zip_reader_end(&archive);
    if (executable.empty()) throw Error("The Pandoc archive does not contain pandoc.exe.");
    return executable;
}

std::string install_pandoc(const Progress& progress) {
    HttpClient http(UserAgent, std::chrono::seconds(60));
    progress.report("Finding the latest Pandoc release...");
    const auto release = latest_pandoc(http, progress);
    const auto zip = download(http, release, "Pandoc", progress);
    progress.report("Installing Pandoc...");
    const auto directory = join_path(data_directory(), "pandoc");
    create_directories(directory);
    const auto path = join_path(directory, "pandoc.exe");
    write_file_atomic(path, extract_pandoc(zip));
    return "Pandoc " + release.version + " is ready (" + path + ").";
}

std::string install_tesseract(const Progress& progress) {
    HttpClient http(UserAgent, std::chrono::seconds(60));
    progress.report("Finding the latest Tesseract release...");
    const auto release = latest_tesseract(http, progress);
    const auto installer = download(http, release, "Tesseract", progress);
    const auto folder = join_path(temp_directory(), "md-viewer-tesseract-" + new_guid());
    create_directories(folder);
    const auto path = join_path(folder, release.name);
    write_file_atomic(path, installer);

    progress.report("Running the Tesseract installer. Windows will ask for permission; choose any extra languages there.");
    const auto wide_path = widen(path);
    SHELLEXECUTEINFOW info{sizeof info};
    info.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC;
    info.lpVerb = L"open";
    info.lpFile = wide_path.c_str();
    info.nShow = SW_SHOWNORMAL;
    const BOOL started = ShellExecuteExW(&info);
    const DWORD error = GetLastError();
    auto cleanup = [&] {
        DeleteFileW(wide_path.c_str());
        RemoveDirectoryW(widen(folder).c_str());
    };
    if (!started) {
        cleanup();
        if (error == ERROR_CANCELLED) throw Error("The Tesseract installer was not allowed to run.");
        throw_win32("The Tesseract installer could not start.", error);
    }
    DWORD exit_code = 0;
    if (info.hProcess) {
        try {
            while (WaitForSingleObject(info.hProcess, 250) == WAIT_TIMEOUT) progress.check();
        } catch (const Cancelled&) {
            CloseHandle(info.hProcess);
            throw Error("Stopped waiting; the Tesseract installer is still open. Check for updates again when it finishes.");
        }
        GetExitCodeProcess(info.hProcess, &exit_code);
        CloseHandle(info.hProcess);
    }
    cleanup();
    if (exit_code != 0) throw Error("The Tesseract installer did not finish (exit code " + std::to_string(exit_code) + ").");
    const auto found = find_tesseract();
    if (!found) throw Error("The Tesseract installer finished, but tesseract.exe was not found.");
    return "Tesseract " + program_version(*found).value_or(release.version) + " is ready (" + *found + ").";
}

json tool_entry(const std::string& id, const std::string& name, const std::string& purpose) {
    return {{"id", id}, {"name", name}, {"purpose", purpose}, {"installed", false}, {"languages", json::array()}};
}

}  // namespace

int compare_versions(const std::string& a, const std::string& b) {
    auto parts = [](const std::string& text) {
        std::vector<unsigned long long> numbers;
        size_t i = 0;
        while (i < text.size()) {
            if (!std::isdigit(static_cast<unsigned char>(text[i]))) { ++i; continue; }
            unsigned long long value = 0;
            while (i < text.size() && std::isdigit(static_cast<unsigned char>(text[i]))) value = value * 10 + static_cast<unsigned>(text[i++] - '0');
            numbers.push_back(value);
        }
        return numbers;
    };
    const auto x = parts(a), y = parts(b);
    for (size_t i = 0; i < std::max(x.size(), y.size()); ++i) {
        const auto left = i < x.size() ? x[i] : 0, right = i < y.size() ? y[i] : 0;
        if (left != right) return left < right ? -1 : 1;
    }
    return 0;
}

std::optional<std::string> find_tesseract() {
    std::vector<std::string> candidates;
    const auto path = environment("PATH");
    for (size_t start = 0; start <= path.size();) {
        auto end = path.find(';', start);
        if (end == std::string::npos) end = path.size();
        const auto entry = trim(std::string_view(path).substr(start, end - start));
        if (!entry.empty()) candidates.push_back(join_path(replace_all(std::string(entry), "\"", ""), "tesseract.exe"));
        start = end + 1;
    }
    for (const auto root : {HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER})
        if (const auto directory = registry_string(root, L"SOFTWARE\\Tesseract-OCR", L"InstallDir")) candidates.push_back(join_path(*directory, "tesseract.exe"));
    candidates.push_back(join_path(join_path(environment("ProgramFiles"), "Tesseract-OCR"), "tesseract.exe"));
    candidates.push_back(join_path(join_path(environment("LOCALAPPDATA"), "Programs\\Tesseract-OCR"), "tesseract.exe"));
    candidates.push_back(join_path(join_path(environment("LOCALAPPDATA"), "Tesseract-OCR"), "tesseract.exe"));
    for (const auto& candidate : candidates)
        if (file_exists(candidate)) return candidate;
    return std::nullopt;
}

std::vector<std::string> tesseract_languages(const std::string& executable) {
    std::vector<std::string> languages;
    try {
        const auto result = run_process(executable, {"--list-langs"}, std::nullopt, "", std::chrono::seconds(20), Progress());
        bool header = true;
        for (const auto& line : split_lines(normalize_newlines(result.output + "\n" + result.error))) {
            const auto name = std::string(trim(line));
            if (header) { header = name.find("List of available languages") == std::string::npos; continue; }
            if (!name.empty() && name.find(' ') == std::string::npos) languages.push_back(name);
        }
    } catch (const std::exception&) {}
    return languages;
}

std::string tools_status_json(bool check_latest, const Progress& progress) {
    json status;
    std::optional<HttpClient> http;
    if (check_latest) http.emplace(UserAgent, std::chrono::seconds(30));

    auto pandoc = tool_entry("pandoc", "Pandoc", "Imports Word, HTML, EPUB, ODT, and RTF; exports; formats; converts crawled pages");
    progress.report("Checking Pandoc...");
    if (const auto path = find_pandoc()) {
        const auto own = iequals(*path, join_path(join_path(data_directory(), "pandoc"), "pandoc.exe"));
        pandoc["installed"] = true;
        pandoc["path"] = *path;
        pandoc["source"] = own ? "md-viewer" : "system";
        if (const auto version = program_version(*path)) pandoc["version"] = *version;
    }
    pandoc["action"] = pandoc["installed"].get<bool>() ? "update" : "install";
    pandoc["action_note"] = "Downloads the official Windows build into md-viewer's own folder. Your system copy, if any, is left alone.";

    auto tesseract = tool_entry("tesseract", "Tesseract OCR", "Reads scanned PDF pages, in more than 100 languages");
    progress.report("Checking Tesseract...");
    if (const auto path = find_tesseract()) {
        tesseract["installed"] = true;
        tesseract["path"] = *path;
        tesseract["source"] = "system";
        if (const auto version = program_version(*path)) tesseract["version"] = *version;
        tesseract["languages"] = tesseract_languages(*path);
    }
    tesseract["action"] = tesseract["installed"].get<bool>() ? "update" : "install";
    tesseract["action_note"] = "Runs the official Windows installer from the Tesseract project, which asks for administrator permission.";

    auto windows = tool_entry("windows-ocr", "Windows OCR", "Built into Windows; used when Tesseract is not installed");
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
    } catch (const winrt::hresult_error&) {}
    try {
        json languages = json::array();
        for (const auto& language : winrt::Windows::Media::Ocr::OcrEngine::AvailableRecognizerLanguages()) languages.push_back(winrt::to_string(language.LanguageTag()));
        windows["installed"] = !languages.empty();
        windows["languages"] = languages;
        windows["note"] = languages.empty() ? "No OCR languages are installed. Add one in Settings > Time & language > Language & region." : "";
    } catch (const winrt::hresult_error& ex) {
        windows["note"] = winrt::to_string(ex.message());
    }
    windows["action"] = "none";

    if (http) {
        auto check = [&](json& tool, auto latest) {
            try {
                const auto release = latest(*http, progress);
                tool["latest"] = release.version;
                tool["update_available"] = tool.contains("version") && compare_versions(tool["version"].get<std::string>(), release.version) < 0;
            } catch (const Cancelled&) {
                throw;
            } catch (const std::exception& ex) {
                tool["latest_error"] = ex.what();
            }
        };
        progress.report("Checking for Pandoc updates...");
        check(pandoc, latest_pandoc);
        progress.report("Checking for Tesseract updates...");
        check(tesseract, latest_tesseract);
    }

    const auto ocr = ocr_preferences();
    status["tools"] = json::array({pandoc, tesseract, windows});
    status["ocr"] = {{"engine", ocr.engine}, {"languages", ocr.languages}, {"active", active_ocr_engine()}};
    status["checked_latest"] = check_latest;
    return status.dump();
}

std::string install_tool(const std::string& tool, const Progress& progress) {
    if (tool == "pandoc") return install_pandoc(progress);
    if (tool == "tesseract") return install_tesseract(progress);
    throw Error("md-viewer cannot install " + tool + ".");
}

}  // namespace mdv
