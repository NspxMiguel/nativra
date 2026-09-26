// A small but real MSVC program for the x86 layer: C runtime start-up and
// shutdown, the heap through std::vector/std::string, a C++ throw caught by
// type, a __try/__except around RaiseException, stdio to a file and back,
// floating-point formatting, threads (std::thread, std::mutex,
// std::condition_variable, thread_local, _beginthreadex), and printf to
// standard output. Built twice by
// build-guests.cmd: /MT (static runtime, kernel32 only) and /MD (ucrtbase,
// vcruntime140 and msvcp140 mapped into the guest). Exits with 42 when
// every check passed; its stdout line says which failed otherwise.
#include <windows.h>
#include <process.h>
#include <cmath>
#include <condition_variable>
#include <mutex>
#include <queue>
#include <thread>
#include <cstdio>
#include <cstring>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

static int SehCheck()
{
    __try
    {
        RaiseException(0xE0000001, 0, 0, nullptr);
    }
    __except (GetExceptionCode() == 0xE0000001 ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
    {
        return 1;
    }
    return 0;
}

struct Counted
{
    static int live;
    Counted() { ++live; }
    ~Counted() { --live; }
};
int Counted::live = 0;

static void Thrower(int depth)
{
    Counted guard;   // destroyed during unwinding
    if (depth == 0) throw std::runtime_error("boom");
    Thrower(depth - 1);
}

// Threads: a producer/consumer pair over std::mutex and
// std::condition_variable (SRW locks and condition variables underneath),
// thread_local (static TLS in each new thread), and _beginthreadex with a
// wait on its handle.
static thread_local int perThread = 5;
static unsigned __stdcall Worker(void* arg)
{
    perThread += 1;   // this thread's copy, starting from the template's 5
    *static_cast<int*>(arg) = perThread;
    return 77;
}

static int ThreadCheck()
{
    std::mutex lock;
    std::condition_variable ready;
    std::queue<int> items;
    bool finished = false;
    long long total = 0;
    std::thread consumer([&] {
        std::unique_lock<std::mutex> hold(lock);
        for (;;)
        {
            ready.wait(hold, [&] { return !items.empty() || finished; });
            while (!items.empty()) { total += items.front(); items.pop(); }
            if (finished) break;
        }
    });
    for (int i = 1; i <= 1000; i++)
    {
        std::lock_guard<std::mutex> hold(lock);
        items.push(i);
        ready.notify_one();
    }
    {
        std::lock_guard<std::mutex> hold(lock);
        finished = true;
    }
    ready.notify_all();
    consumer.join();

    int seen = 0;
    auto handle = reinterpret_cast<HANDLE>(_beginthreadex(nullptr, 0, Worker, &seen, 0, nullptr));
    if (!handle) return 0;
    WaitForSingleObject(handle, INFINITE);
    DWORD code = 0;
    GetExitCodeThread(handle, &code);
    CloseHandle(handle);

    // 500500 = 1 + ... + 1000; the worker's thread_local started at 5 and
    // the main thread's copy was never touched.
    return total == 500500 && seen == 6 && perThread == 5 && code == 77;
}

int main(int argc, char** argv)
{
    std::vector<std::string> squares;
    for (int i = 0; i < 100; i++) squares.push_back(std::to_string(i * i));
    std::map<std::string, int> index;
    for (int i = 0; i < 100; i++) index[squares[i]] = i;

    int passed = 0;
    if (squares[99] == "9801" && index["2500"] == 50) passed |= 1;

    try
    {
        Thrower(3);
    }
    catch (const std::exception& e)
    {
        if (std::strcmp(e.what(), "boom") == 0 && Counted::live == 0) passed |= 2;
    }

    if (SehCheck()) passed |= 4;

    FILE* f = std::fopen("guest-out.txt", "w");
    if (f)
    {
        std::fprintf(f, "%d %s\n", 1234, "written");
        std::fclose(f);
    }
    f = std::fopen("guest-out.txt", "r");
    if (f)
    {
        int n = 0;
        char word[32] = {};
        if (std::fscanf(f, "%d %31s", &n, word) == 2 && n == 1234 && std::strcmp(word, "written") == 0) passed |= 8;
        std::fclose(f);
    }

    char text[64];
    std::snprintf(text, sizeof text, "%.4f", std::sqrt(2.0) * argc);
    if (std::strcmp(text, "1.4142") == 0) passed |= 16;

    if (ThreadCheck()) passed |= 32;

    std::printf("guest passed=%d\n", passed);
    std::fflush(stdout);
    return passed == 63 ? 42 : 1;
}
