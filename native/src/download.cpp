#include "download.h"

#include "files.h"
#include "http.h"
#include "pandoc.h"

#include "miniz.h"
#include "nlohmann/json.hpp"

#include <windows.h>
#include <bcrypt.h>

namespace mdv {
namespace {

constexpr const char* LatestRelease = "https://api.github.com/repos/jgm/pandoc/releases/latest";
constexpr const char* DownloadPrefix = "https://github.com/jgm/pandoc/releases/download/";

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

struct Asset {
    std::string name, version, url;
    std::optional<std::string> sha256;
};

Asset latest_asset(HttpClient& http, const Progress& progress) {
    const auto response = http.get(LatestRelease, {"application/vnd.github+json"}, 16ull << 20, progress);
    if (!response.ok()) throw Error("GitHub returned HTTP " + std::to_string(response.status) + " for the latest Pandoc release.");
    const auto json = nlohmann::json::parse(response.body, nullptr, false);
    if (json.is_discarded() || !json.contains("assets") || !json["assets"].is_array()) throw Error("GitHub's release information could not be read.");
    for (const auto& asset : json["assets"]) {
        const auto name = asset.value("name", std::string());
        const auto url = asset.value("browser_download_url", std::string());
        constexpr std::string_view prefix = "pandoc-", suffix = "-windows-x86_64.zip";
        if (!istarts_with(name, prefix) || !iends_with(name, suffix) || name.size() <= prefix.size() + suffix.size()) continue;
        if (!istarts_with(url, DownloadPrefix)) continue;
        Asset result{name, name.substr(prefix.size(), name.size() - prefix.size() - suffix.size()), url, std::nullopt};
        if (const auto digest = asset.value("digest", std::string()); istarts_with(digest, "sha256:")) result.sha256 = lower(digest.substr(7));
        return result;
    }
    throw Error("The latest Pandoc release has no Windows x64 ZIP.");
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

}  // namespace

FetchedPandoc fetch_pandoc(const Progress& progress) {
    HttpClient http("md-viewer/2.0", std::chrono::seconds(60));
    progress.report("Finding the latest Pandoc release...");
    const auto asset = latest_asset(http, progress);

    progress.report("Downloading Pandoc " + asset.version + "...");
    std::string zip;
    Sha256 hash;
    int next_report = 5;
    const auto response = http.get(asset.url, {"application/octet-stream", "*/*"}, 1ull << 30, progress,
                                   [&](const char* data, size_t size, unsigned long long total, std::optional<unsigned long long> length) {
                                       zip.append(data, size);
                                       hash.add(data, size);
                                       if (length && *length > 0 && static_cast<int>(total * 100 / *length) >= next_report) {
                                           const int percent = static_cast<int>(total * 100 / *length);
                                           progress.report("Downloading Pandoc " + std::to_string(percent) + "%...");
                                           next_report = percent + 5;
                                       }
                                   });
    if (!response.ok()) throw Error("The Pandoc download failed with HTTP " + std::to_string(response.status) + ".");
    if (asset.sha256 && hash.hex() != *asset.sha256) throw Error("The Pandoc download did not match GitHub's published SHA-256 digest.");

    progress.report("Installing Pandoc...");
    const auto executable = extract_pandoc(zip);
    const auto directory = join_path(data_directory(), "pandoc");
    create_directories(directory);
    const auto path = join_path(directory, "pandoc.exe");
    write_file_atomic(path, executable);
    return {asset.version, path};
}

}  // namespace mdv
