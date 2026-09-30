#pragma once

#include "common.h"

#include <chrono>
#include <optional>
#include <string>
#include <vector>

namespace mdv {

struct ProcessResult {
    unsigned long exit_code = 0;
    std::string output;  // stdout bytes (UTF-8 for Pandoc)
    std::string error;   // stderr bytes
};

// Runs a console program without a window, feeding stdin and capturing stdout/stderr.
// The child lives in a job object, so cancelling or timing out ends it (and anything it started).
// Throws Cancelled, or Error("... timed out.") after the timeout.
ProcessResult run_process(const std::string& executable, const std::vector<std::string>& arguments,
                          const std::optional<std::string>& input, const std::string& working_directory,
                          std::optional<std::chrono::milliseconds> timeout, const Progress& progress);

}  // namespace mdv
