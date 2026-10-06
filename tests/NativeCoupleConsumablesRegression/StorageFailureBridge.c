/* The owner reload must not depend on the CRT temporary-file allocator. */
#define tmpfile couple_test_tmpfile
#define main couple_test_bridge_main
#include "../../adapter/nanaimo_gameplay_bridge.c"
#undef main
#undef tmpfile
extern void* __attribute__((cdecl)) tmpfile(void);
extern int __attribute__((cdecl)) memcmp(const void*, const void*, unsigned);

void* __attribute__((cdecl)) couple_test_tmpfile(void)
{
    if (g_stable_name_len == 6u && !memcmp(g_stable_name, "ShareA", 6u)
        && !g_card_synth_item_n
        && !memcmp(g_card_synth_items_account, "p_536861726542", 14u))
        return 0;
    return tmpfile();
}

int main(int argc, char** argv) { return couple_test_bridge_main(argc, argv); }
