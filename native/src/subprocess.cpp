#include "subprocess.h"

#include <windows.h>

#include <algorithm>
#include <thread>

namespace mdv {
namespace {

struct Owned {
    HANDLE value = nullptr;
    Owned() = default;
    explicit Owned(HANDLE h) : value(h) {}
    Owned(const Owned&) = delete;
    Owned& operator=(const Owned&) = delete;
    ~Owned() { reset(); }
    void reset(HANDLE h = nullptr) {
        if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value);
        value = h;
    }
};

// Quotes one argument by the rules CommandLineToArgvW (and the C runtime) use to split it again.
std::wstring quote(const std::wstring& argument) {
    if (!argument.empty() && argument.find_first_of(L" \t\n\v\"") == std::wstring::npos) return argument;
    std::wstring quoted = L"\"";
    for (size_t i = 0;; ++i) {
        size_t backslashes = 0;
        while (i < argument.size() && argument[i] == L'\\') { ++i; ++backslashes; }
        if (i == argument.size()) { quoted.append(backslashes * 2, L'\\'); break; }
        if (argument[i] == L'"') quoted.append(backslashes * 2 + 1, L'\\');
        else quoted.append(backslashes, L'\\');
        quoted += argument[i];
    }
    return quoted + L"\"";
}

void pipe(Owned& read, Owned& write, bool child_reads) {
    SECURITY_ATTRIBUTES security{sizeof security, nullptr, TRUE};
    HANDLE r = nullptr, w = nullptr;
    if (!CreatePipe(&r, &w, &security, 1 << 16)) throw_win32("Could not create a pipe.", GetLastError());
    read.reset(r);
    write.reset(w);
    // Only the child's end is inheritable.
    SetHandleInformation(child_reads ? write.value : read.value, HANDLE_FLAG_INHERIT, 0);
}

void drain(HANDLE source, std::string& target) {
    char buffer[1 << 16];
    DWORD read = 0;
    while (ReadFile(source, buffer, sizeof buffer, &read, nullptr) && read > 0) target.append(buffer, read);
}

}  // namespace

ProcessResult run_process(const std::string& executable, const std::vector<std::string>& arguments,
                          const std::optional<std::string>& input, const std::string& working_directory,
                          std::optional<std::chrono::milliseconds> timeout, const Progress& progress) {
    Owned in_read, in_write, out_read, out_write, err_read, err_write;
    pipe(in_read, in_write, true);
    pipe(out_read, out_write, false);
    pipe(err_read, err_write, false);

    std::wstring command = quote(widen(executable));
    for (const auto& argument : arguments) command += L" " + quote(widen(argument));

    // Pass exactly the three pipe ends, never other inheritable handles the host may hold.
    HANDLE inherit[] = {in_read.value, out_write.value, err_write.value};
    SIZE_T attribute_size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attribute_size);
    std::vector<unsigned char> attribute_buffer(attribute_size);
    auto* attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attribute_buffer.data());
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &attribute_size) ||
        !UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherit, sizeof inherit, nullptr, nullptr))
        throw_win32("Could not prepare " + file_name_of(executable) + ".", GetLastError());

    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof startup;
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = in_read.value;
    startup.StartupInfo.hStdOutput = out_write.value;
    startup.StartupInfo.hStdError = err_write.value;
    startup.lpAttributeList = attributes;

    Owned job(CreateJobObjectW(nullptr, nullptr));
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof limits);

    PROCESS_INFORMATION info{};
    const auto directory = widen(working_directory);
    const BOOL started = CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE,
                                        CREATE_NO_WINDOW | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT,
                                        nullptr, directory.empty() ? nullptr : directory.c_str(), &startup.StartupInfo, &info);
    const DWORD start_error = GetLastError();
    DeleteProcThreadAttributeList(attributes);
    if (!started) throw_win32(file_name_of(executable) + " could not be started.", start_error);
    Owned process(info.hProcess), thread(info.hThread);
    AssignProcessToJobObject(job.value, process.value);
    ResumeThread(thread.value);

    // The parent keeps only its own ends, so reads end when the child exits.
    in_read.reset();
    out_write.reset();
    err_write.reset();

    ProcessResult result;
    std::thread out_reader(drain, out_read.value, std::ref(result.output));
    std::thread err_reader(drain, err_read.value, std::ref(result.error));
    std::thread writer([&] {
        if (input) {
            size_t offset = 0;
            while (offset < input->size()) {
                DWORD written = 0;
                const DWORD chunk = static_cast<DWORD>(std::min<size_t>(input->size() - offset, 1 << 16));
                if (!WriteFile(in_write.value, input->data() + offset, chunk, &written, nullptr)) break;
                offset += written;
            }
        }
        in_write.reset();
    });

    const auto deadline = timeout ? std::optional(std::chrono::steady_clock::now() + *timeout) : std::nullopt;
    bool stopped = false;
    std::exception_ptr failure;
    while (WaitForSingleObject(process.value, 100) == WAIT_TIMEOUT) {
        try {
            progress.check();
            if (deadline && std::chrono::steady_clock::now() > *deadline) throw Error(file_name_of(executable) + " timed out.");
        } catch (...) {
            failure = std::current_exception();
            TerminateJobObject(job.value, 1);
            stopped = true;
            break;
        }
    }
    if (stopped) WaitForSingleObject(process.value, 5000);
    writer.join();
    out_reader.join();
    err_reader.join();
    if (failure) std::rethrow_exception(failure);
    GetExitCodeProcess(process.value, &result.exit_code);
    return result;
}

}  // namespace mdv
