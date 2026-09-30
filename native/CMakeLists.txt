cmake_minimum_required(VERSION 3.24)
project(mdv_native VERSION 2.0.0 LANGUAGES C CXX)

# Prebuilt PDFium from pdfium-binaries; scripts/build.ps1 downloads the pinned release here.
set(PDFIUM_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../build/pdfium" CACHE PATH "Extracted pdfium-win-x64 release")
if(NOT EXISTS "${PDFIUM_DIR}/include/fpdfview.h")
  message(FATAL_ERROR "PDFium not found in ${PDFIUM_DIR}. Run scripts/build.ps1, which downloads it.")
endif()

# Vendored C libraries, unchanged; see each folder's REVISION.
add_library(md4c STATIC vendor/md4c/md4c.c vendor/md4c/entity.c)
target_include_directories(md4c PUBLIC vendor/md4c)
target_compile_definitions(md4c PRIVATE MD4C_USE_UTF8)

file(GLOB GUMBO_SOURCES vendor/gumbo/*.c)
add_library(gumbo STATIC ${GUMBO_SOURCES})
target_include_directories(gumbo PUBLIC vendor/gumbo PRIVATE vendor/gumbo/msvc)
if(MSVC)
  target_compile_options(gumbo PRIVATE /wd4244 /wd4267 /wd4018 /wd4996 /wd4146)
endif()

add_library(miniz STATIC vendor/miniz/miniz.c)
target_include_directories(miniz PUBLIC vendor/miniz)

foreach(library md4c gumbo miniz)
  set_property(TARGET ${library} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endforeach()

# The md-viewer core, compiled once and linked into both the DLL and the test program.
add_library(mdv_core OBJECT
  src/abi.cpp src/common.cpp src/crawl.cpp src/download.cpp src/files.cpp src/html.cpp
  src/http.cpp src/markdown.cpp src/pandoc.cpp src/pdf.cpp src/subprocess.cpp src/url.cpp)
target_compile_features(mdv_core PUBLIC cxx_std_20)
target_include_directories(mdv_core PUBLIC include src vendor "${PDFIUM_DIR}/include")
target_link_libraries(mdv_core PUBLIC md4c gumbo miniz)
target_compile_definitions(mdv_core PUBLIC MDV_NATIVE_BUILD UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN NDEBUG)
set_property(TARGET mdv_core PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
if(MSVC)
  target_compile_options(mdv_core PRIVATE /W4 /EHsc /utf-8 /permissive- /bigobj)
endif()

set(MDV_SYSTEM_LIBRARIES winhttp bcrypt windowsapp ole32 user32 "${PDFIUM_DIR}/lib/pdfium.dll.lib")

add_library(mdv_native SHARED)
target_link_libraries(mdv_native PRIVATE mdv_core ${MDV_SYSTEM_LIBRARIES})
set_property(TARGET mdv_native PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_executable(mdv_tests tests/native_tests.cpp)
target_link_libraries(mdv_tests PRIVATE mdv_core ${MDV_SYSTEM_LIBRARIES})
set_property(TARGET mdv_tests PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
if(MSVC)
  target_compile_options(mdv_tests PRIVATE /W4 /EHsc /utf-8 /permissive-)
endif()

# pdfium.dll ships beside mdv_native.dll and the test program.
foreach(target mdv_native mdv_tests)
  add_custom_command(TARGET ${target} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${PDFIUM_DIR}/bin/pdfium.dll" "$<TARGET_FILE_DIR:${target}>")
endforeach()
