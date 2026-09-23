#include <windows.h>
#include <intrin.h>
#include <stdio.h>

typedef int (*Configure)(const unsigned char *, unsigned int, unsigned int);
typedef int (*Ensure)(void);
static int slots[2];
static Ensure ensure[2];

static DWORD WINAPI VerifyThread(void *unused) {
    (void)unused;
    for (int i = 0; i < 2; ++i) {
        if (ensure[i]() < 0) return 1;
        unsigned char **table = (unsigned char **)__readgsqword(0x58);
        if (table[slots[i]][0] != 40 + i || table[slots[i]][31] != 0)
            return 2;
        table[slots[i]][0] = 90 + i;
    }
    return 0;
}

int main(void) {
    for (int i = 0; i < 2; ++i) {
        wchar_t name[32];
        swprintf_s(name, 32, L"NativraTls%d.dll", i);
        HMODULE module = LoadLibraryW(name);
        if (!module) return 10;
        Configure configure = (Configure)GetProcAddress(module, "NativraTlsConfigure");
        ensure[i] = (Ensure)GetProcAddress(module, "NativraTlsEnsure");
        if (!configure || !ensure[i]) return 11;
        unsigned char value = 40 + i;
        slots[i] = configure(&value, 1, 32);
        if (slots[i] < 0 || ensure[i]() != 1 || ensure[i]() != 0) return 12;
        if (configure(&value, 1, 32) != -1) return 13;
    }
    if (slots[0] == slots[1]) return 14;
    for (int i = 0; i < 3; ++i) {
        HANDLE thread = CreateThread(NULL, 0, VerifyThread, NULL, 0, NULL);
        if (!thread) return 15;
        if (WaitForSingleObject(thread, 10000) != WAIT_OBJECT_0) return 16;
        DWORD result;
        if (!GetExitCodeThread(thread, &result) || result) return 17;
        CloseHandle(thread);
    }
    unsigned char **table = (unsigned char **)__readgsqword(0x58);
    if (table[slots[0]][0] != 40 || table[slots[1]][0] != 41) return 18;
    printf("TLS carriers: distinct static slots, templates, isolation, thread teardown passed\n");
    return 0;
}
