#include <intrin.h>
#include <stdio.h>
#include <windows.h>

typedef int (*Configure)(const unsigned char *, unsigned int, unsigned int);
typedef int (*Ensure)(void);
static int slots[2];
static Ensure ensure[2];

static LONG ReportException(EXCEPTION_POINTERS *error) {
  printf("Exception %08lx at %p accessing %p\n",
         error->ExceptionRecord->ExceptionCode,
         error->ExceptionRecord->ExceptionAddress,
         (void *)error->ExceptionRecord->ExceptionInformation[1]);
  fflush(stdout);
  return EXCEPTION_EXECUTE_HANDLER;
}

static DWORD WINAPI VerifyThread(void *unused) {
  (void)unused;
  for (int i = 0; i < 2; ++i) {
    if (ensure[i]() < 0)
      return 1;
    unsigned char **table = (unsigned char **)__readgsqword(0x58);
    if (table[slots[i]][0] != 40 + i || table[slots[i]][31] != 0)
      return 2;
    table[slots[i]][0] = 90 + i;
  }
  return 0;
}

int main(void) {
  SetUnhandledExceptionFilter(ReportException);
  setvbuf(stdout, NULL, _IONBF, 0);
  for (int i = 0; i < 2; ++i) {
    wchar_t name[32];
    swprintf_s(name, 32, L"NativraTls%d.dll", i);
    HMODULE module = LoadLibraryW(name);
    if (!module)
      return 10;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)module;
    IMAGE_NT_HEADERS64 *pe =
        (IMAGE_NT_HEADERS64 *)((unsigned char *)module + dos->e_lfanew);
    DWORD tlsRva = pe->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_TLS]
                       .VirtualAddress;
    printf("Carrier %d base=%p TLS RVA=%lx\n", i, module, tlsRva);
    Configure configure =
        (Configure)GetProcAddress(module, "NativraTlsConfigure");
    ensure[i] = (Ensure)GetProcAddress(module, "NativraTlsEnsure");
    if (!configure || !ensure[i])
      return 11;
    unsigned char value = 40 + i;
    slots[i] = configure(&value, 1, 32);
    printf("Carrier %d slot=%d table=%p ensure=%p\n", i, slots[i],
           (void *)__readgsqword(0x58), ensure[i]);
    IMAGE_TLS_DIRECTORY64 *directory =
        (IMAGE_TLS_DIRECTORY64 *)((unsigned char *)module + tlsRva);
    unsigned char **currentTable = (unsigned char **)__readgsqword(0x58);
    int firstEnsure = ensure[i]();
    printf("Carrier %d raw=%p indexAddress=%p block=%p ensured=%d first=%u\n",
           i, (void *)directory->StartAddressOfRawData,
           (void *)directory->AddressOfIndex, currentTable[slots[i]],
           firstEnsure, currentTable[slots[i]][0]);
    if (slots[i] < 0 || firstEnsure < 0 || ensure[i]() != 0 ||
        currentTable[slots[i]][0] != value)
      return 12;
    if (configure(&value, 1, 32) != -1)
      return 13;
  }
  if (slots[0] == slots[1])
    return 14;
  for (int i = 0; i < 3; ++i) {
    HANDLE thread = CreateThread(NULL, 0, VerifyThread, NULL, 0, NULL);
    if (!thread)
      return 15;
    if (WaitForSingleObject(thread, 10000) != WAIT_OBJECT_0)
      return 16;
    DWORD result;
    if (!GetExitCodeThread(thread, &result) || result)
      return 17;
    CloseHandle(thread);
  }
  unsigned char **table = (unsigned char **)__readgsqword(0x58);
  if (table[slots[0]][0] != 40 || table[slots[1]][0] != 41)
    return 18;
  printf("TLS carriers: distinct static slots, templates, isolation, thread "
         "teardown passed\n");
  return 0;
}
