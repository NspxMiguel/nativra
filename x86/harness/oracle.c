/*
 * Hardware oracle for the x86 translator.
 *
 * Built as a 32-bit program (gcc -m32 on Linux, cl for x86 on Windows) and
 * run natively — under WoW64 on the Windows runner. For every snippet in
 * snippets.txt it builds a few random machine states, executes the snippet
 * on the real processor and prints the state before and after. The C# tests
 * replay the same inputs through the interpreter and the JIT and compare.
 *
 * The snippet runs inside a generated trampoline that loads the general
 * registers, EFLAGS and a full FXSAVE image (x87, MMX, SSE), falls through
 * the snippet, and stores everything back. Data and stack live at fixed
 * addresses so both sides agree on every pointer the snippet sees.
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#define ALIGN16 __declspec(align(16))
#else
#include <sys/mman.h>
#define ALIGN16 __attribute__((aligned(16)))
#ifndef MAP_FIXED_NOREPLACE
#define MAP_FIXED_NOREPLACE 0x100000
#endif
#endif

#define DATA_BASE   0x10000000u
#define DATA_SIZE   256u
#define STACK_BASE  0x20000000u
#define STACK_SIZE  0x2000u
#define STACK_TOP   0x20001000u  /* initial ESP */
#define STACK_WIN   64u          /* bytes either side of ESP that are compared */
#define CODE_BASE   0x30000000u

typedef struct {
    uint32_t r[8];
    uint32_t eflags;
    uint32_t pad[3];
    uint8_t fx[512];
} State;

static ALIGN16 State st;

static void *alloc_at(uint32_t address, uint32_t size, int exec)
{
#ifdef _WIN32
    return VirtualAlloc((void *)(uintptr_t)address, size, MEM_RESERVE | MEM_COMMIT,
                        exec ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE);
#else
    void *p = mmap((void *)(uintptr_t)address, size,
                   PROT_READ | PROT_WRITE | (exec ? PROT_EXEC : 0),
                   MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED_NOREPLACE, -1, 0);
    return p == MAP_FAILED ? NULL : p;
#endif
}

/* xorshift32, seeded per case so both runs of a case see the same inputs. */
static uint32_t rng;
static uint32_t next(void)
{
    rng ^= rng << 13;
    rng ^= rng >> 17;
    rng ^= rng << 5;
    return rng;
}

static uint32_t hash(const char *s)
{
    uint32_t h = 2166136261u;
    while (*s) { h ^= (uint8_t)*s++; h *= 16777619u; }
    return h ? h : 1;
}

/* ---------------------------------------------------------- code emitter */

static uint8_t *emit_at;
static void b(uint8_t v) { *emit_at++ = v; }
static void d32(uint32_t v) { memcpy(emit_at, &v, 4); emit_at += 4; }

static uint32_t *save_ptr, *save_esp, *tmp_eax;

static void build(const uint8_t *snippet, int length)
{
    uint8_t *code = (uint8_t *)(uintptr_t)CODE_BASE;
    save_ptr = (uint32_t *)(uintptr_t)(CODE_BASE + 0xF00);
    save_esp = (uint32_t *)(uintptr_t)(CODE_BASE + 0xF04);
    tmp_eax  = (uint32_t *)(uintptr_t)(CODE_BASE + 0xF08);
    emit_at = code;

    b(0x55); b(0x53); b(0x56); b(0x57);             /* push ebp/ebx/esi/edi */
    b(0x8B); b(0x44); b(0x24); b(0x14);             /* mov eax,[esp+20]     */
    b(0xA3); d32((uint32_t)(uintptr_t)save_ptr);    /* mov [save_ptr],eax   */
    b(0x89); b(0x25); d32((uint32_t)(uintptr_t)save_esp); /* mov [save_esp],esp */
    b(0x0F); b(0xAE); b(0x48); b(0x30);             /* fxrstor [eax+48]     */
    b(0xFF); b(0x70); b(0x20);                      /* push dword [eax+32]  */
    b(0x9D);                                        /* popfd                */
    b(0x8B); b(0x48); b(0x04);                      /* mov ecx,[eax+4]      */
    b(0x8B); b(0x50); b(0x08);                      /* mov edx,[eax+8]      */
    b(0x8B); b(0x58); b(0x0C);                      /* mov ebx,[eax+12]     */
    b(0x8B); b(0x68); b(0x14);                      /* mov ebp,[eax+20]     */
    b(0x8B); b(0x70); b(0x18);                      /* mov esi,[eax+24]     */
    b(0x8B); b(0x78); b(0x1C);                      /* mov edi,[eax+28]     */
    b(0x8B); b(0x60); b(0x10);                      /* mov esp,[eax+16]     */
    b(0x8B); b(0x00);                               /* mov eax,[eax]        */

    memcpy(emit_at, snippet, length);
    emit_at += length;

    b(0xA3); d32((uint32_t)(uintptr_t)tmp_eax);     /* mov [tmp_eax],eax    */
    b(0xA1); d32((uint32_t)(uintptr_t)save_ptr);    /* mov eax,[save_ptr]   */
    b(0x89); b(0x48); b(0x04);                      /* mov [eax+4],ecx      */
    b(0x89); b(0x50); b(0x08);                      /* mov [eax+8],edx      */
    b(0x89); b(0x58); b(0x0C);                      /* mov [eax+12],ebx     */
    b(0x89); b(0x60); b(0x10);                      /* mov [eax+16],esp     */
    b(0x89); b(0x68); b(0x14);                      /* mov [eax+20],ebp     */
    b(0x89); b(0x70); b(0x18);                      /* mov [eax+24],esi     */
    b(0x89); b(0x78); b(0x1C);                      /* mov [eax+28],edi     */
    b(0x8B); b(0x25); d32((uint32_t)(uintptr_t)save_esp); /* mov esp,[save_esp] */
    b(0x9C);                                        /* pushfd (on host stack) */
    b(0x8F); b(0x40); b(0x20);                      /* pop dword [eax+32]   */
    b(0x0F); b(0xAE); b(0x40); b(0x30);             /* fxsave [eax+48]      */
    b(0x8B); b(0x0D); d32((uint32_t)(uintptr_t)tmp_eax); /* mov ecx,[tmp_eax] */
    b(0x89); b(0x08);                               /* mov [eax],ecx        */
    b(0x8B); b(0x25); d32((uint32_t)(uintptr_t)save_esp); /* mov esp,[save_esp] */
    b(0x5F); b(0x5E); b(0x5B); b(0x5D);             /* pop edi/esi/ebx/ebp  */
    b(0xC3);                                        /* ret                  */
}

/* -------------------------------------------------------- random inputs */

static void put80(uint8_t *at, double value)
{
    uint64_t bits, mant;
    uint16_t se;
    int exponent;
    memcpy(&bits, &value, 8);
    exponent = (int)((bits >> 52) & 0x7FF);
    se = (uint16_t)((bits >> 48) & 0x8000);
    if (exponent == 0) { mant = 0; }
    else {
        mant = 0x8000000000000000ull | ((bits & 0xFFFFFFFFFFFFFull) << 11);
        se |= (uint16_t)(exponent - 1023 + 16383);
    }
    memcpy(at, &mant, 8);
    memcpy(at + 8, &se, 2);
}

static double random_double(void)
{
    /* Moderate magnitudes: no overflow, no denormals, exact in 80 bits. */
    uint64_t bits = ((uint64_t)(next() & 1) << 63) |
                    ((uint64_t)(1023 - 16 + (next() % 33)) << 52) |
                    (((uint64_t)next() << 20) ^ next()) & 0xFFFFFFFFFFFFFull;
    double d;
    memcpy(&d, &bits, 8);
    return d;
}

static float random_float(void)
{
    uint32_t bits = ((next() & 1) << 31) | ((127 - 16 + (next() % 33)) << 23) | (next() & 0x7FFFFF);
    float f;
    memcpy(&f, &bits, 4);
    return f;
}

static int reg_index(const char *name)
{
    static const char *names[] = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
    int i;
    for (i = 0; i < 8; i++) if (strncmp(name, names[i], 3) == 0) return i;
    return -1;
}

/* Options: "esi=ptr", "ecx=nz", "edx=0", "ecx=5", "fpu=3", "xmm=float|double|int", "count=N", "flags=HEX". */
static void setup(const char *options)
{
    char copy[512], *tok;
    int i;
    for (i = 0; i < 8; i++) st.r[i] = next();
    st.r[4] = STACK_TOP;
    st.eflags = 0x202 | (next() & 0x8D5);  /* random CF PF AF ZF SF OF, DF clear */
    memset(st.fx, 0, sizeof st.fx);
    st.fx[0] = 0x7F; st.fx[1] = 0x02;       /* FCW 0x027F: 53-bit precision, all masked */
    st.fx[24] = 0x80; st.fx[25] = 0x1F;     /* MXCSR 0x1F80 */

    strncpy(copy, options, sizeof copy - 1);
    copy[sizeof copy - 1] = 0;
    for (tok = strtok(copy, " ,"); tok; tok = strtok(NULL, " ,")) {
        char *eq = strchr(tok, '=');
        if (!eq) continue;
        *eq = 0;
        if (reg_index(tok) >= 0 && strlen(tok) == 3) {
            int r = reg_index(tok);
            if (strcmp(eq + 1, "ptr") == 0) st.r[r] = DATA_BASE + (next() % 128);
            else if (strcmp(eq + 1, "ptr0") == 0) st.r[r] = DATA_BASE;
            else if (strcmp(eq + 1, "nz") == 0) { do st.r[r] = next(); while (!st.r[r]); }
            else if (strcmp(eq + 1, "small") == 0) st.r[r] = next() % 64;
            else st.r[r] = (uint32_t)strtoul(eq + 1, NULL, 0);
        } else if (strcmp(tok, "flags") == 0) {
            st.eflags = 0x202 | ((uint32_t)strtoul(eq + 1, NULL, 16) & 0xCD5);
        } else if (strcmp(tok, "fpu") == 0) {
            int k = atoi(eq + 1), top = (8 - k) & 7;
            uint16_t fsw = (uint16_t)(top << 11);
            memcpy(st.fx + 2, &fsw, 2);
            for (i = 0; i < k; i++) {
                put80(st.fx + 32 + i * 16, random_double());
                st.fx[4] |= (uint8_t)(1 << ((top + i) & 7));
            }
        } else if (strcmp(tok, "xmm") == 0) {
            for (i = 0; i < 8; i++) {
                uint8_t *x = st.fx + 160 + i * 16;
                int lane;
                if (strcmp(eq + 1, "float") == 0)
                    for (lane = 0; lane < 4; lane++) { float f = random_float(); memcpy(x + lane * 4, &f, 4); }
                else if (strcmp(eq + 1, "double") == 0)
                    for (lane = 0; lane < 2; lane++) { double v = random_double(); memcpy(x + lane * 8, &v, 8); }
                else
                    for (lane = 0; lane < 4; lane++) { uint32_t v = next(); memcpy(x + lane * 4, &v, 4); }
            }
        }
    }
}

static void hex(const uint8_t *p, int n)
{
    int i;
    for (i = 0; i < n; i++) printf("%02X", p[i]);
}

static void dump(char tag)
{
    int i;
    printf("%c", tag);
    for (i = 0; i < 8; i++) printf(" %08X", st.r[i]);
    printf(" %08X ", st.eflags);
    hex(st.fx, 288);          /* x87 + MMX + XMM0-7; the rest of FXSAVE is reserved */
    printf(" ");
    hex((const uint8_t *)(uintptr_t)DATA_BASE, DATA_SIZE);
    printf(" ");
    hex((const uint8_t *)(uintptr_t)(STACK_TOP - STACK_WIN), STACK_WIN * 2);
    printf("\n");
}

int main(int argc, char **argv)
{
    FILE *input;
    char line[1024];
    const char *path = argc > 1 ? argv[1] : "snippets.txt";

    if (!alloc_at(DATA_BASE, 0x1000, 0) || !alloc_at(STACK_BASE, STACK_SIZE, 0) ||
        !alloc_at(CODE_BASE, 0x1000, 1)) {
        fprintf(stderr, "oracle: could not map the fixed regions\n");
        return 2;
    }
    input = fopen(path, "r");
    if (!input) { fprintf(stderr, "oracle: cannot open %s\n", path); return 2; }

    while (fgets(line, sizeof line, input)) {
        /* name | hex | ignored-flags | options */
        char *name = strtok(line, "|"), *hexbytes = strtok(NULL, "|");
        char *mask = strtok(NULL, "|"), *options = strtok(NULL, "|\r\n");
        uint8_t code[64];
        int length = 0, count = 8, c;
        const char *h;
        if (!name || !hexbytes || name[0] == '#') continue;
        while (*name == ' ') name++;
        { char *e = name + strlen(name); while (e > name && e[-1] == ' ') *--e = 0; }
        for (h = hexbytes; *h; ) {
            unsigned v;
            while (*h == ' ') h++;
            if (!*h || sscanf(h, "%2x", &v) != 1) break;
            code[length++] = (uint8_t)v;
            h += 2;
        }
        if (options && strstr(options, "count=")) count = atoi(strstr(options, "count=") + 6);
        build(code, length);
        printf("T %s ", name);
        hex(code, length);
        printf(" %s\n", mask ? mask + strspn(mask, " ") : "0");
        for (c = 0; c < count; c++) {
            rng = hash(name) ^ (uint32_t)(c * 0x9E3779B9u);
            if (!rng) rng = 1;
            setup(options ? options : "");
            for (length = 0; length < (int)DATA_SIZE; length++)
                ((uint8_t *)(uintptr_t)DATA_BASE)[length] = (uint8_t)next();
            memset((void *)(uintptr_t)(STACK_TOP - STACK_WIN), 0, STACK_WIN * 2);
            dump('I');
            ((void (*)(State *))(uintptr_t)CODE_BASE)(&st);
            dump('O');
        }
        fflush(stdout);
    }
    return 0;
}
