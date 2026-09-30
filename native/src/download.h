#pragma once

#include "common.h"

#include <string>

namespace mdv {

struct FetchedPandoc {
    std::string version;
    std::string path;
};

// Downloads the latest official Windows x64 Pandoc release from GitHub, verifies GitHub's
// published SHA-256 digest when present, and installs pandoc.exe in the data directory.
FetchedPandoc fetch_pandoc(const Progress& progress);

}  // namespace mdv
