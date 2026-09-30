#include "files.h"

#include "common.h"

#include <windows.h>

#include <algorithm>

namespace mdv {
namespace {

constexpr UINT Ansi = 1252;

struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    ~Handle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};

std::string from_code_page(std::string_view bytes, UINT code_page) {
    if (bytes.empty()) return {};
    const int length = MultiByteToWideChar(code_page, 0, bytes.data(), static_cast<int>(bytes.size()), nullptr, 0);
    std::wstring wide(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(code_page, 0, bytes.data(), static_cast<int>(bytes.size()), wide.data(), length);
    return narrow(wide);
}

bool valid_utf8(std::string_view bytes) {
    return bytes.empty() || MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data(), static_cast<int>(bytes.size()), nullptr, 0) > 0;
}

std::string utf16(std::string_view bytes, bool big_endian) {
    std::wstring wide(bytes.size() / 2, L'\0');
    for (size_t i = 0; i < wide.size(); ++i) {
        const auto a = static_cast<unsigned char>(bytes[2 * i]), b = static_cast<unsigned char>(bytes[2 * i + 1]);
        wide[i] = static_cast<wchar_t>(big_endian ? (a << 8) | b : (b << 8) | a);
    }
    return narrow(wide);
}

// Returns false when a character has no Windows-1252 equivalent.
bool to_ansi(const std::wstring& wide, std::string& out) {
    if (wide.empty()) return true;
    BOOL lossy = FALSE;
    const int length = WideCharToMultiByte(Ansi, WC_NO_BEST_FIT_CHARS, wide.data(), static_cast<int>(wide.size()), nullptr, 0, nullptr, &lossy);
    if (lossy) return false;
    out.resize(static_cast<size_t>(length));
    WideCharToMultiByte(Ansi, WC_NO_BEST_FIT_CHARS, wide.data(), static_cast<int>(wide.size()), out.data(), length, nullptr, &lossy);
    return !lossy;
}

}  // namespace

std::string read_file(const std::string& path, unsigned long long limit) {
    Handle file;
    file.value = CreateFileW(widen(path).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file.value == INVALID_HANDLE_VALUE) {
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) throw Error(path + " does not exist.");
        throw_win32("Could not open " + path + ".", error);
    }
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.value, &size)) throw_win32("Could not read " + path + ".", GetLastError());
    if (static_cast<unsigned long long>(size.QuadPart) > limit)
        throw Error(file_name_of(path) + " is larger than the " + std::to_string(limit / (1024 * 1024)) + " MB limit.");
    std::string bytes(static_cast<size_t>(size.QuadPart), '\0');
    size_t offset = 0;
    while (offset < bytes.size()) {
        DWORD read = 0;
        const DWORD chunk = static_cast<DWORD>(std::min<size_t>(bytes.size() - offset, 1u << 24));
        if (!ReadFile(file.value, bytes.data() + offset, chunk, &read, nullptr)) throw_win32("Could not read " + path + ".", GetLastError());
        if (read == 0) break;
        offset += read;
    }
    bytes.resize(offset);
    return bytes;
}

TextFile decode_text(std::string_view bytes) {
    if (bytes.starts_with("\xEF\xBB\xBF")) return {std::string(bytes.substr(3)), "UTF-8 BOM"};
    if (bytes.starts_with("\xFF\xFE")) return {utf16(bytes.substr(2), false), "UTF-16 LE"};
    if (bytes.starts_with("\xFE\xFF")) return {utf16(bytes.substr(2), true), "UTF-16 BE"};
    if (valid_utf8(bytes)) return {std::string(bytes), "UTF-8"};
    // Not valid UTF-8: most likely a legacy ANSI file. Keep that encoding for saving.
    return {from_code_page(bytes, Ansi), "Windows-1252"};
}

TextFile read_document(const std::string& path) { return decode_text(read_file(path)); }

std::string write_document(const std::string& path, std::string_view utf8, const std::string& encoding) {
    const auto wide = widen(utf8);
    std::string bytes;
    std::string used = encoding;
    if (encoding == "UTF-16 LE" || encoding == "UTF-16 BE") {
        const bool big = encoding == "UTF-16 BE";
        bytes = big ? "\xFE\xFF" : "\xFF\xFE";
        for (const wchar_t c : wide) {
            const auto hi = static_cast<char>((c >> 8) & 0xFF), lo = static_cast<char>(c & 0xFF);
            bytes += big ? hi : lo;
            bytes += big ? lo : hi;
        }
    } else if (encoding == "Windows-1252" && to_ansi(wide, bytes)) {
        // encoded above
    } else {
        used = encoding == "UTF-8 BOM" ? encoding : "UTF-8";
        bytes = (used == "UTF-8 BOM" ? "\xEF\xBB\xBF" : "") + std::string(utf8);
    }
    write_file_atomic(path, bytes);
    return used;
}

void write_file_atomic(const std::string& path, std::string_view bytes) {
    const auto target = widen(path);
    wchar_t full[32768];
    const DWORD length = GetFullPathNameW(target.c_str(), 32768, full, nullptr);
    if (length == 0 || length >= 32768) throw_win32("Could not resolve " + path + ".", GetLastError());
    const std::string full_path = narrow(std::wstring_view(full, length));
    const std::string directory = directory_of(full_path);
    if (!directory_exists(directory)) throw Error("The folder for " + path + " does not exist.");
    const std::string temporary = join_path(directory, "." + file_name_of(full_path) + "." + new_guid() + ".tmp");
    const auto temporary_wide = widen(temporary);
    {
        Handle file;
        file.value = CreateFileW(temporary_wide.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file.value == INVALID_HANDLE_VALUE) throw_win32("Could not write " + path + ".", GetLastError());
        size_t offset = 0;
        bool ok = true;
        DWORD error = 0;
        while (ok && offset < bytes.size()) {
            DWORD written = 0;
            const DWORD chunk = static_cast<DWORD>(std::min<size_t>(bytes.size() - offset, 1u << 24));
            ok = WriteFile(file.value, bytes.data() + offset, chunk, &written, nullptr) != FALSE;
            offset += written;
        }
        if (ok) ok = FlushFileBuffers(file.value) != FALSE;
        if (!ok) {
            error = GetLastError();
            CloseHandle(file.value);
            file.value = INVALID_HANDLE_VALUE;
            DeleteFileW(temporary_wide.c_str());
            throw_win32("Could not write " + path + ".", error);
        }
    }
    if (!MoveFileExW(temporary_wide.c_str(), full, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        const DWORD error = GetLastError();
        DeleteFileW(temporary_wide.c_str());
        throw_win32("Could not replace " + path + ".", error);
    }
}

}  // namespace mdv
