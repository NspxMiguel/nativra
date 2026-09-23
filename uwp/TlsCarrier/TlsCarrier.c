#include <windows.h>
#include <intrin.h>

// Each packaged copy owns one Windows-managed static TLS index. No CRT,
// DllMain, heap allocation, or replacement of the system TLS vector.
#define TLS_CAPACITY 16384
#pragma section(".tls", read, write)
#pragma section(".rdata$T", read)
__declspec(allocate(".tls")) unsigned char tls_template[TLS_CAPACITY + 16];
unsigned long _tls_index;
PIMAGE_TLS_CALLBACK tls_callbacks[] = { 0 };
__declspec(allocate(".rdata$T")) const IMAGE_TLS_DIRECTORY64 _tls_used = {
    (ULONGLONG)tls_template,
    (ULONGLONG)(tls_template + sizeof(tls_template)),
    (ULONGLONG)&_tls_index, (ULONGLONG)tls_callbacks, 0, IMAGE_SCN_ALIGN_16BYTES
};

__declspec(dllexport) int NativraTlsConfigure(const unsigned char *source,
                                            unsigned int size,
                                            unsigned int total) {
    if (size > total || total > TLS_CAPACITY || tls_template[TLS_CAPACITY])
        return -1;
    __movsb(tls_template, source, size);
    tls_template[TLS_CAPACITY] = 1;
    return (int)_tls_index;
}

__declspec(dllexport) int NativraTlsEnsure(void) {
    unsigned char **table = (unsigned char **)__readgsqword(0x58);
    unsigned char *block = table[_tls_index];
    if (!block) return -1;
    if (block[TLS_CAPACITY]) return 0;
    __movsb(block, tls_template, sizeof(tls_template));
    return 1;
}
