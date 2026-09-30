/* md-viewer native core: the whole program behind a versioned UTF-8 C ABI.
 *
 * The C# frame draws the window and handles the outside world (dialogs,
 * settings, the package); everything md-viewer *does* happens here: Markdown
 * parsing and reflow, reading and saving documents, importing (Pandoc and
 * PDF with Windows OCR), exporting, crawling documentation sites, and
 * fetching Pandoc.
 *
 * Conventions
 *  - Strings are UTF-8. Paths are UTF-8 and may be any Windows path.
 *  - Every function is exception-contained and never throws across the ABI.
 *  - Functions that produce data return an mdv_result*, which the caller
 *    must release with mdv_result_free. NULL means memory ran out.
 *  - Long operations take an mdv_progress_fn. It is called with a status
 *    line, or with NULL to poll; returning nonzero cancels the operation,
 *    which then returns MDV_CANCELLED. It may be called from worker threads.
 */
#ifndef MDV_NATIVE_H
#define MDV_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  if defined(MDV_NATIVE_BUILD)
#    define MDV_API __declspec(dllexport)
#  else
#    define MDV_API __declspec(dllimport)
#  endif
#else
#  define MDV_API __attribute__((visibility("default")))
#endif

#define MDV_ABI_VERSION 2

#define MDV_OK 0
#define MDV_ERROR 1        /* data holds a message for the user */
#define MDV_CANCELLED 2

typedef struct mdv_result {
    int32_t status;
    int32_t reserved;
    char* data;            /* payload or error message; NUL-terminated */
    size_t size;           /* payload bytes, excluding the terminator */
} mdv_result;

typedef int32_t (*mdv_progress_fn)(void* context, const char* message);

MDV_API int32_t mdv_abi_version(void);
MDV_API void mdv_result_free(mdv_result* result);

/* Where md-viewer keeps fetched Pandoc and its Pandoc filter. The frame passes
 * the package's LocalState folder; the default is %LOCALAPPDATA%\md-viewer. */
MDV_API void mdv_configure(const char* data_directory);

/* Supported file types, one per line: "<kind>\t<extension>\t<label>", where
 * kind is markdown, pandoc, pdf, or export. */
MDV_API mdv_result* mdv_file_types(void);

/* ---- Markdown ----------------------------------------------------------- */

/* Parses Markdown into the binary document model described in markdown.cpp. */
MDV_API mdv_result* mdv_parse(const char* utf8, size_t length);

/* Repairs skipped heading levels, editing only heading markers.
 * Payload: "<changed>\t<total>\t<warning count>\n<warning>\n...<markdown>". */
MDV_API mdv_result* mdv_reflow_headings(const char* utf8, size_t length);

/* ---- Documents ---------------------------------------------------------- */

/* Opens a document: Markdown and text directly (detecting its encoding),
 * Word/HTML/EPUB/ODT/RTF through Pandoc, and PDF with text extraction plus
 * Windows OCR. Payload: "<markdown|imported>\n<encoding>\n<summary>\n<text>". */
MDV_API mdv_result* mdv_open(const char* path, mdv_progress_fn progress, void* context);

/* Saves through a temporary file in the requested encoding ("UTF-8",
 * "UTF-8 BOM", "UTF-16 LE", "UTF-16 BE", "Windows-1252"), falling back to
 * UTF-8 when the text does not fit a legacy encoding. Payload: encoding used. */
MDV_API mdv_result* mdv_save(const char* path, const char* utf8, size_t length, const char* encoding);

/* ---- Pandoc ------------------------------------------------------------- */

/* Payload: the pandoc.exe that will be used. */
MDV_API mdv_result* mdv_pandoc_path(void);
/* Normalizes Markdown through Pandoc, keeping a leading YAML block verbatim. */
MDV_API mdv_result* mdv_pandoc_format(const char* utf8, size_t length, mdv_progress_fn progress, void* context);
/* Writes the format implied by the target's extension. resource_directory may be NULL. */
MDV_API mdv_result* mdv_pandoc_export(const char* utf8, size_t length, const char* target, const char* resource_directory,
                                      mdv_progress_fn progress, void* context);
/* Downloads the latest official Windows x64 Pandoc. Payload: "<version>\n<path>". */
MDV_API mdv_result* mdv_pandoc_fetch(mdv_progress_fn progress, void* context);

/* ---- Crawling ----------------------------------------------------------- */

/* Collects a documentation site into one Markdown document: same folder as
 * the start page, one request at a time, honoring robots.txt, up to max_pages. */
MDV_API mdv_result* mdv_crawl(const char* start_url, int32_t max_pages, mdv_progress_fn progress, void* context);

#ifdef __cplusplus
}
#endif

#endif
