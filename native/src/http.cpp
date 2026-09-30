#include "http.h"

#include <windows.h>
#include <winhttp.h>

#include <atomic>
#include <condition_variable>
#include <mutex>
#include <thread>

namespace mdv {
namespace {

struct Internet {
    HINTERNET value = nullptr;
    explicit Internet(HINTERNET h) : value(h) {}
    ~Internet() { if (value) WinHttpCloseHandle(value); }
};

[[noreturn]] void fail(const std::string& what) {
    const DWORD code = GetLastError();
    if (code == ERROR_WINHTTP_TIMEOUT) throw HttpError(what + " timed out.");
    throw HttpError(what + ": " + win32_message(code, L"winhttp.dll"));
}

std::wstring query_header(HINTERNET request, DWORD info) {
    DWORD size = 0;
    WinHttpQueryHeaders(request, info, WINHTTP_HEADER_NAME_BY_INDEX, WINHTTP_NO_OUTPUT_BUFFER, &size, WINHTTP_NO_HEADER_INDEX);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || size == 0) return {};
    std::wstring value(size / sizeof(wchar_t), L'\0');
    if (!WinHttpQueryHeaders(request, info, WINHTTP_HEADER_NAME_BY_INDEX, value.data(), &size, WINHTTP_NO_HEADER_INDEX)) return {};
    value.resize(size / sizeof(wchar_t));
    return value;
}

std::optional<std::chrono::seconds> parse_retry_after(const std::string& value) {
    const auto text = trim(value);
    if (text.empty()) return std::nullopt;
    if (text.find_first_not_of("0123456789") == std::string_view::npos && text.size() < 9)
        return std::chrono::seconds(std::stoll(std::string(text)));
    SYSTEMTIME when{};
    if (!WinHttpTimeToSystemTime(widen(text).c_str(), &when)) return std::nullopt;
    FILETIME file{}, now{};
    SystemTimeToFileTime(&when, &file);
    GetSystemTimeAsFileTime(&now);
    const auto target = (static_cast<long long>(file.dwHighDateTime) << 32) | file.dwLowDateTime;
    const auto current = (static_cast<long long>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
    if (target <= current) return std::nullopt;
    return std::chrono::seconds((target - current) / 10'000'000);
}

UINT code_page_for(const std::string& charset) {
    struct Entry { const char* name; UINT page; };
    static constexpr Entry table[] = {
        {"utf-8", CP_UTF8}, {"utf8", CP_UTF8}, {"us-ascii", 20127}, {"ascii", 20127}, {"iso-8859-1", 28591}, {"latin1", 28591},
        {"iso-8859-2", 28592}, {"iso-8859-5", 28595}, {"iso-8859-7", 28597}, {"iso-8859-15", 28605}, {"windows-1250", 1250},
        {"windows-1251", 1251}, {"windows-1252", 1252}, {"cp1252", 1252}, {"windows-1253", 1253}, {"windows-1254", 1254},
        {"windows-1256", 1256}, {"koi8-r", 20866}, {"koi8-u", 21866}, {"shift_jis", 932}, {"shift-jis", 932}, {"euc-jp", 51932},
        {"gb2312", 936}, {"gbk", 936}, {"gb18030", 54936}, {"big5", 950}, {"euc-kr", 949}, {"ks_c_5601-1987", 949},
    };
    for (const auto& entry : table)
        if (charset == entry.name) return entry.page;
    return CP_UTF8;
}

}  // namespace

HttpClient::HttpClient(const std::string& user_agent, std::chrono::milliseconds timeout) {
    session_ = WinHttpOpen(widen(user_agent).c_str(), WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session_) fail("Could not start the HTTP client");
    DWORD decompression = WINHTTP_DECOMPRESSION_FLAG_ALL;
    WinHttpSetOption(session_, WINHTTP_OPTION_DECOMPRESSION, &decompression, sizeof decompression);
    DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3;
    WinHttpSetOption(session_, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof protocols);
    const int ms = static_cast<int>(timeout.count());
    WinHttpSetTimeouts(session_, ms, ms, ms, ms);
}

HttpClient::~HttpClient() {
    if (session_) WinHttpCloseHandle(session_);
}

HttpResponse HttpClient::get(const std::string& url, const std::vector<std::string>& accept, unsigned long long max_bytes, const Progress& progress,
                             const Sink& sink) {
    progress.check();
    const auto wide_url = widen(url);
    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof parts;
    parts.dwHostNameLength = parts.dwUrlPathLength = parts.dwExtraInfoLength = parts.dwSchemeLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(wide_url.c_str(), 0, 0, &parts)) throw HttpError(url + " is not a valid address.");
    const std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    std::wstring path(parts.lpszUrlPath, parts.dwUrlPathLength);
    path.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);
    if (path.empty()) path = L"/";

    Internet connection(WinHttpConnect(static_cast<HINTERNET>(session_), host.c_str(), parts.nPort, 0));
    if (!connection.value) fail("Could not connect to " + narrow(host));
    const DWORD flags = parts.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET request = WinHttpOpenRequest(connection.value, L"GET", path.c_str(), nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!request) fail("Could not request " + url);

    // Closing the request handle from another thread aborts a blocking WinHTTP call.
    std::atomic<bool> closed = false, finished = false, cancelled = false;
    std::mutex mutex;
    std::condition_variable wake;
    std::thread watchdog([&] {
        std::unique_lock lock(mutex);
        while (!finished) {
            wake.wait_for(lock, std::chrono::milliseconds(150));
            if (finished) break;
            if (progress.cancelled()) {
                cancelled = true;
                if (!closed.exchange(true)) WinHttpCloseHandle(request);
                break;
            }
        }
    });
    auto finish = [&] {
        if (!watchdog.joinable()) return;
        { std::lock_guard lock(mutex); finished = true; }
        wake.notify_all();
        watchdog.join();
        if (!closed.exchange(true)) WinHttpCloseHandle(request);
    };

    try {
        std::string accept_header = "Accept: ";
        for (size_t i = 0; i < accept.size(); ++i) accept_header += (i ? ", " : "") + accept[i];
        if (!accept.empty()) WinHttpAddRequestHeaders(request, widen(accept_header).c_str(), static_cast<DWORD>(-1), WINHTTP_ADDREQ_FLAG_ADD);
        if (!WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) || !WinHttpReceiveResponse(request, nullptr)) {
            if (cancelled) throw Cancelled();
            fail("Request to " + url + " failed");
        }

        HttpResponse response;
        DWORD status = 0, size = sizeof status;
        WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, WINHTTP_NO_HEADER_INDEX);
        response.status = status;
        response.reason = narrow(query_header(request, WINHTTP_QUERY_STATUS_TEXT));
        const auto content_type = lower(narrow(query_header(request, WINHTTP_QUERY_CONTENT_TYPE)));
        response.content_type = std::string(trim(std::string_view(content_type).substr(0, content_type.find(';'))));
        if (const auto charset = content_type.find("charset="); charset != std::string::npos) {
            auto value = content_type.substr(charset + 8);
            value = value.substr(0, value.find(';'));
            response.charset = replace_all(replace_all(std::string(trim(value)), "\"", ""), "'", "");
        }
        response.retry_after = parse_retry_after(narrow(query_header(request, WINHTTP_QUERY_RETRY_AFTER)));
        if (const auto length = narrow(query_header(request, WINHTTP_QUERY_CONTENT_LENGTH)); !length.empty() && length.find_first_not_of("0123456789") == std::string::npos)
            response.content_length = std::stoull(length);

        unsigned long long total = 0;
        std::vector<char> buffer(1 << 17);
        while (true) {
            DWORD read = 0;
            if (!WinHttpReadData(request, buffer.data(), static_cast<DWORD>(buffer.size()), &read)) {
                if (cancelled) throw Cancelled();
                fail("Reading " + url + " failed");
            }
            if (read == 0) break;
            total += read;
            if (total > max_bytes) throw HttpError("response exceeded maximum page size of " + format_count(static_cast<long long>(max_bytes)) + " bytes");
            if (sink) sink(buffer.data(), read, total, response.content_length);
            else response.body.append(buffer.data(), read);
            progress.check();
        }
        finish();
        return response;
    } catch (...) {
        finish();
        if (cancelled) throw Cancelled();
        throw;
    }
}

std::string decode_body(const HttpResponse& response) {
    const UINT page = code_page_for(response.charset);
    if (page == CP_UTF8 || response.body.empty()) {
        // Replace invalid sequences the way .NET does, via a UTF-16 round trip.
        return narrow(widen(response.body));
    }
    const int length = MultiByteToWideChar(page, 0, response.body.data(), static_cast<int>(response.body.size()), nullptr, 0);
    std::wstring wide(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(page, 0, response.body.data(), static_cast<int>(response.body.size()), wide.data(), length);
    return narrow(wide);
}

}  // namespace mdv
