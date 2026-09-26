// A small but real MSVC program for the x86 layer: C runtime start-up and
// shutdown, the heap through std::vector/std::string, a C++ throw caught by
// type, a __try/__except around RaiseException, stdio to a file and back,
// floating-point formatting, and printf to standard output. Built twice by
// build-guests.cmd: /MT (static runtime, kernel32 only) and /MD (ucrtbase,
// vcruntime140 and msvcp140 mapped into the guest). Exits with 42 when
// every check passed; its stdout line says which failed otherwise.
#include <windows.h>
#include <cmath>
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

    std::printf("guest passed=%d\n", passed);
    std::fflush(stdout);
    return passed == 31 ? 42 : 1;
}
