#pragma once

#include "common.h"

#include <chrono>
#include <functional>
#include <optional>
#include <string>
#include <vector>

namespace mdv {

struct HttpResponse {
    unsigned status = 0;
    std::string reason;
    std::string content_type;  // media type only, lower-case
    std::string charset;       // lower-case, may be empty
    std::optional<std::chrono::seconds> retry_after;
    std::optional<unsigned long long> content_length;
    std::string body;          // raw bytes
    bool ok() const { return status >= 200 && status < 300; }
};

struct HttpError : Error {
    using Error::Error;
};

// A synchronous WinHTTP session. Cancellation closes the request from a watchdog thread.
class HttpClient {
public:
    HttpClient(const std::string& user_agent, std::chrono::milliseconds timeout);
    ~HttpClient();
    HttpClient(const HttpClient&) = delete;
    HttpClient& operator=(const HttpClient&) = delete;

    // Receives each chunk, the running total, and the Content-Length when the server sent one.
    using Sink = std::function<void(const char* data, size_t size, unsigned long long total, std::optional<unsigned long long> length)>;

    // Reads the whole body; throws HttpError when it exceeds max_bytes. With a sink, the body is
    // streamed to it instead of being kept.
    HttpResponse get(const std::string& url, const std::vector<std::string>& accept, unsigned long long max_bytes, const Progress& progress,
                     const Sink& sink = {});

private:
    void* session_ = nullptr;
};

// Decodes a body using the response charset (UTF-8 when absent or unknown).
std::string decode_body(const HttpResponse& response);

}  // namespace mdv
