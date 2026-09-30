/* md-viewer native core: a versioned UTF-8 C ABI over md4c.
 *
 * Every function is exception-contained. Buffers returned by the library are
 * owned by the caller and must be released with mdv_free.
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

#define MDV_ABI_VERSION 1

MDV_API int32_t mdv_abi_version(void);

/* Parses Markdown (CommonMark + GitHub tables, strikethrough, task lists,
 * autolinks, and a leading YAML front-matter block) into the binary document
 * model described in mdv_native.cpp. Returns NULL only when memory runs out;
 * *out_size receives the model's byte count. */
MDV_API uint8_t* mdv_parse(const char* utf8, size_t length, size_t* out_size);

/* Rewrites heading levels so each heading is at most one level deeper than
 * its parent, editing only the heading markers. Returns
 *   "OK\n<changed>\t<total>\t<warning count>\n<warning>\n...<markdown>"
 * or "ERROR\n<message>". *out_size receives the byte count, excluding the
 * terminating NUL (the Markdown itself may contain NUL characters). */
MDV_API char* mdv_reflow_headings(const char* utf8, size_t length, size_t* out_size);

MDV_API void mdv_free(void* buffer);

#ifdef __cplusplus
}
#endif

#endif
