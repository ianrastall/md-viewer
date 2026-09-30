#pragma once

#include "common.h"
#include "url.h"

#include <chrono>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace mdv {

struct CrawlOptions {
    std::chrono::milliseconds default_delay{2000};
    std::chrono::milliseconds request_timeout{30000};
    int max_pages = 250;
    unsigned long long max_page_bytes = 5ull * 1024 * 1024;
    int max_retries = 3;
    bool same_base_path_only = true;
    bool respect_robots_txt = true;
};

extern const char* const CrawlerUserAgent;

// Collects a documentation site (or one MediaWiki article) into a single Markdown document.
std::string crawl(const std::string& start_url, const CrawlOptions& options, const Progress& progress);

// ---- Pieces exposed for the native tests ----

class RobotsTxt {
public:
    static RobotsTxt allow_all();
    static RobotsTxt disallow_all();
    static RobotsTxt parse(std::string_view text, std::string_view user_agent);
    bool allowed(const Url& url) const;
    std::optional<std::chrono::milliseconds> crawl_delay;

private:
    struct Rule { bool allow; std::string pattern; };
    bool default_allow_ = true;
    std::vector<Rule> rules_;
};

struct MediaWikiPage {
    Url page_url, raw_url, api_url;
    std::string title;
    static std::optional<MediaWikiPage> from(const Url& url);
};

Url crawl_base_url(const Url& start);
bool is_under_base_path(const Url& candidate, const Url& base);
bool has_html_like_extension(const Url& url);
bool looks_like_dynamic_shell(std::string_view html);
// Cleans Pandoc's Markdown for a crawled page (artifacts, images, stray HTML, footnote markers, boilerplate).
std::string clean_crawled_markdown(std::string_view markdown, const Url* wiki_page = nullptr);

}  // namespace mdv
