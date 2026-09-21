"""Focused host checks for shop currency and face-coupon protocol closure."""
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / "tools/tcc/tcc.exe"
CATALOG = ROOT / "release/components/shop_catalogs/shop_item_catalog.inc"
PROTOCOL = ROOT / "release/components/shop_catalogs/shop_protocol.inc"
RUNTIME = ROOT / "release/components/inventory_instances/shop_runtime.inc"
GS = ROOT / "release/components/game_session/gs_runtime.inc"
MANAGED_NET = ROOT / "managed/Services/NetworkAdapterService.cs"
MANAGED_DB = ROOT / "managed/Services/DatabaseService.cs"
MANAGED_CATALOG = ROOT / "managed/Services/PetShopCatalog.cs"

EXPECTED_HANS = {
    14000001: 24, 14000003: 24, 14000005: 30, 14000177: 198,
    21000001: 500, 21000003: 100, 21000004: 100, 21000005: 100,
    21000016: 1000, 21000019: 100, 21000020: 50, 21000022: 2000,
    21000023: 4000, 21000074: 300, 21000076: 400, 21000080: 100,
    21000086: 200, 21000111: 500,
}


def text(path):
    return path.read_text(encoding="utf-8")


def function_source(source, name):
    signature = source.index(name + "(")
    start = source.rfind("static ", 0, signature)
    brace = source.index("{", signature)
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[start:index + 1]
    raise AssertionError(f"unterminated function {name}")


class ShopAppearanceProtocolTests(unittest.TestCase):
    def test_hans_catalog_is_complete_and_currency_typed(self):
        source = text(CATALOG)
        rows = [tuple(map(int, match)) for match in re.findall(
            r"\{(\d+)u,(\d+)u,(\d+)u,(\d+)u\}", source)]
        self.assertEqual(len(rows), 53)
        self.assertEqual([row[0] for row in rows], sorted(row[0] for row in rows))
        self.assertEqual(len({row[0] for row in rows}), len(rows))
        hans = {code: price for code, price, carrier, currency in rows
                if carrier == 1 and currency == 1}
        self.assertEqual(hans, EXPECTED_HANS)
        self.assertTrue(all(price > 0 for _, price, _, _ in rows))
        self.assertIn("currency1=Hans, currency2=NaNa/Cash", source)

    def test_c3d2_builder_exact_layout(self):
        if not TCC.is_file():
            self.fail(f"bundled TCC missing: {TCC}")
        builder = function_source(text(PROTOCOL), "send_c3d2_face_coupon_result")
        harness = r'''
#include <assert.h>
#include <string.h>
typedef int SOCKET;
static unsigned char captured[128]; static int captured_len;
static long sec(void){return 0;}
static int printf(const char*a,...){return 0;} static int fflush(void*a){return 0;}
static void mkpkt(char*p,unsigned op,int len,int seq){memset(p,0,4096);p[4]=len;p[5]=len>>8;p[6]=op;p[7]=op>>8;}
static void stable_resource_put16(char*p,int o,unsigned v){p[o]=v;p[o+1]=v>>8;}
static void stable_put32(char*p,int o,unsigned v){p[o]=v;p[o+1]=v>>8;p[o+2]=v>>16;p[o+3]=v>>24;}
static void sendbuf(SOCKET c,char*p,int n){memcpy(captured,p,n);captured_len=n;}
''' + builder + r'''
static unsigned rd32(int o){return captured[o]|captured[o+1]<<8|captured[o+2]<<16|captured[o+3]<<24;}
int main(void){unsigned a[9]={10130337u,10100005u,10110337u,10120352u,0u,10150103u,10060036u,15009205u,1u};
send_c3d2_face_coupon_result(1,1,a);assert(captured_len==52);assert(captured[6]==0xD2&&captured[7]==0xC3);assert((captured[8]|captured[9]<<8)==1000);for(int i=0;i<9;i++)assert(rd32(16+i*4)==a[i]);
send_c3d2_face_coupon_result(1,0,a);assert(captured_len==52);assert((captured[8]|captured[9]<<8)==0);for(int i=16;i<52;i++)assert(captured[i]==0);return 0;}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-shop-protocol-") as directory:
            directory = Path(directory)
            c_path = directory / "test.c"
            exe = directory / "test.exe"
            c_path.write_text(harness, encoding="utf-8")
            for command in ([str(TCC), str(c_path), "-o", str(exe)], [str(exe)]):
                result = subprocess.run(command, cwd=directory, capture_output=True,
                                        text=True, errors="replace", timeout=60)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_native_currency_purchase_mutates_only_selected_balance(self):
        if not TCC.is_file():
            self.fail(f"bundled TCC missing: {TCC}")
        runtime = text(RUNTIME)
        functions = "\n".join(function_source(runtime, name) for name in (
            "shop_purchase_balance", "shop_purchase_currency_label",
            "shop_purchase_purchase_stackable"))
        harness = r'''
#include <assert.h>
#include <string.h>
#include <stdio.h>
typedef unsigned long long stable_u64;
#define SHOP_PURCHASE_CURRENCY_HANS 1u
#define SHOP_PURCHASE_CURRENCY_NANA 2u
#define CARD_SYNTH_ITEM_CAP 16u
struct shop_purchase_shop_item_row { unsigned code,price; unsigned char carrier,currency; };
struct card_synth_item_row { unsigned code,count; };
static struct card_synth_item_row g_card_synth_items[CARD_SYNTH_ITEM_CAP];
static unsigned g_card_synth_item_n;
static stable_u64 g_profile_coin_hans,g_profile_nana_cash;
static int fail_card_save,fail_shop_save;
static const struct shop_purchase_shop_item_row rows[]={
 {14000001u,24u,1u,1u},{14002486u,16u,1u,2u}};
static const struct shop_purchase_shop_item_row*shop_purchase_shop_item_find(unsigned code){for(unsigned i=0;i<2;i++)if(rows[i].code==code)return &rows[i];return 0;}
static int inventory_instances_instances_can_add(unsigned qty){return qty<=8u;}
static void card_synth_items_load(void){}
static int card_synth_item_index(unsigned code){for(unsigned i=0;i<g_card_synth_item_n;i++)if(g_card_synth_items[i].code==code)return (int)i;return -1;}
static int card_synth_items_save(void){return !fail_card_save;}
static int inventory_instances_instances_reconcile(void){return 1;}
static int shopping_state_save(void){return !fail_shop_save;}
static unsigned inventory_instances_instance_count(unsigned code){int i=card_synth_item_index(code);return i<0?0u:g_card_synth_items[i].count;}
''' + functions + r'''
int main(void){unsigned price=0;g_profile_coin_hans=100;g_profile_nana_cash=100;
assert(shop_purchase_purchase_stackable(14000001u,2u,&price));assert(price==24u);assert(g_profile_coin_hans==52u&&g_profile_nana_cash==100u);assert(inventory_instances_instance_count(14000001u)==2u);
assert(shop_purchase_purchase_stackable(14002486u,1u,&price));assert(price==16u);assert(g_profile_coin_hans==52u&&g_profile_nana_cash==84u);assert(inventory_instances_instance_count(14002486u)==1u);
assert(!shop_purchase_purchase_stackable(14000001u,3u,&price));assert(g_profile_coin_hans==52u&&inventory_instances_instance_count(14000001u)==2u);
fail_shop_save=1;assert(!shop_purchase_purchase_stackable(14002486u,1u,&price));assert(g_profile_nana_cash==84u&&inventory_instances_instance_count(14002486u)==1u);return 0;}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-shop-currency-") as directory:
            directory = Path(directory)
            c_path = directory / "test.c"
            exe = directory / "test.exe"
            c_path.write_text(harness, encoding="utf-8")
            for command in ([str(TCC), str(c_path), "-o", str(exe)], [str(exe)]):
                result = subprocess.run(command, cwd=directory, capture_output=True,
                                        text=True, errors="replace", timeout=60)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_native_face_coupon_consumes_exact_identity_and_rolls_back(self):
        if not TCC.is_file():
            self.fail(f"bundled TCC missing: {TCC}")
        runtime = text(RUNTIME)
        functions = "\n".join(function_source(runtime, name) for name in (
            "shop_purchase_face_allowed", "shop_purchase_use_face_coupon"))
        harness = r'''
#include <assert.h>
#include <string.h>
#define CARD_SYNTH_ITEM_CAP 16u
#define QUICKBAR_SLOT_COUNT 6u
#define SHOPPING_MAX_OWNED_EQUIP 16u
struct card_synth_item_row { unsigned code,count; };
static struct card_synth_item_row g_card_synth_items[CARD_SYNTH_ITEM_CAP];
static unsigned g_card_synth_item_n;
static unsigned g_quickbar_handles[256],g_quickbar_code[QUICKBAR_SLOT_COUNT],g_quickbar_instance[QUICKBAR_SLOT_COUNT];
static unsigned g_owned_equipment[SHOPPING_MAX_OWNED_EQUIP],g_owned_equipment_n;
static unsigned g_stable_equip[5]={10130337u,10100004u,10110337u,10120352u,10150103u};
static unsigned g_stable_effect=10060036u,g_stable_pet=15009205u,g_stable_gender=1u;
static unsigned fixture_coupon=45000001u,fixture_identity=7u;static int fail_shop_save;
static void shopping_fill_appearance(unsigned ids[9]){ids[0]=g_stable_equip[0];ids[1]=g_stable_equip[1];ids[2]=g_stable_equip[2];ids[3]=g_stable_equip[3];ids[4]=0;ids[5]=g_stable_equip[4];ids[6]=g_stable_effect;ids[7]=g_stable_pet;ids[8]=g_stable_gender;}
static int shop_purchase_c430_slot(unsigned slot,unsigned*code,unsigned*identity){if(slot!=3u)return 0;if(code)*code=fixture_coupon;if(identity)*identity=fixture_identity;return 1;}
static int shopping_equip_slot_for_code(unsigned code){return code>=10100000u&&code<=10109999u?1:-1;}
static void card_synth_items_load(void){} static void quickbar_handles_load(void){} static void quickbar_load(void){}
static int shopping_add_unique(unsigned*a,unsigned*n,unsigned cap,unsigned v){for(unsigned i=0;i<*n;i++)if(a[i]==v)return 1;if(*n>=cap)return 0;a[(*n)++]=v;return 1;}
static int inventory_instances_instance_exchange(unsigned code,unsigned identity,unsigned produce,unsigned*remaining){if(code!=fixture_coupon||identity!=fixture_identity||g_quickbar_handles[identity]!=code||g_card_synth_items[0].code!=code||!g_card_synth_items[0].count)return 0;g_card_synth_items[0].count--;g_quickbar_handles[identity]=0;if(remaining)*remaining=g_card_synth_items[0].count;return 1;}
static void shopping_sync_legacy_equipped(void){} static int shopping_state_save(void){return !fail_shop_save;}
static int card_synth_items_save(void){return 1;} static int quickbar_handles_save(void){return 1;} static int quickbar_save(void){return 1;}
''' + functions + r'''
static void reset(void){memset(g_card_synth_items,0,sizeof(g_card_synth_items));memset(g_quickbar_handles,0,sizeof(g_quickbar_handles));memset(g_owned_equipment,0,sizeof(g_owned_equipment));g_card_synth_item_n=1;g_card_synth_items[0].code=fixture_coupon;g_card_synth_items[0].count=1;g_quickbar_handles[fixture_identity]=fixture_coupon;g_owned_equipment_n=1;g_owned_equipment[0]=10100004u;g_stable_equip[1]=10100004u;fail_shop_save=0;}
int main(void){unsigned r[9],coupon=0,identity=0,remaining=99;reset();shopping_fill_appearance(r);r[1]=10100005u;assert(shop_purchase_use_face_coupon(r,3,&coupon,&identity,&remaining));assert(coupon==45000001u&&identity==7u&&remaining==0u);assert(g_stable_equip[1]==10100005u&&g_owned_equipment_n==2u&&g_card_synth_items[0].count==0u&&g_quickbar_handles[7]==0u);
reset();shopping_fill_appearance(r);r[1]=10100010u;assert(!shop_purchase_use_face_coupon(r,3,&coupon,&identity,&remaining));assert(g_stable_equip[1]==10100004u&&g_card_synth_items[0].count==1u);
reset();shopping_fill_appearance(r);r[0]++;r[1]=10100005u;assert(!shop_purchase_use_face_coupon(r,3,&coupon,&identity,&remaining));assert(g_card_synth_items[0].count==1u);
reset();shopping_fill_appearance(r);r[1]=10100005u;fail_shop_save=1;assert(!shop_purchase_use_face_coupon(r,3,&coupon,&identity,&remaining));assert(g_stable_equip[1]==10100004u&&g_owned_equipment_n==1u&&g_card_synth_items[0].count==1u&&g_quickbar_handles[7]==45000001u);return 0;}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-face-coupon-") as directory:
            directory = Path(directory)
            c_path = directory / "test.c"
            exe = directory / "test.exe"
            c_path.write_text(harness, encoding="utf-8")
            for command in ([str(TCC), str(c_path), "-o", str(exe)], [str(exe)]):
                result = subprocess.run(command, cwd=directory, capture_output=True,
                                        text=True, errors="replace", timeout=60)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_native_request_chain_and_transaction_guards(self):
        gs = text(GS)
        runtime = text(RUNTIME)
        self.assertIn("type==0xC3D1 && len==0x4C", gs)
        for offset in ("0x44", "0x46", "0x48", "0x4A"):
            self.assertIn("off+" + offset, gs)
        self.assertIn("send_c3d2_face_coupon_result(c,ok,requested)", gs)
        self.assertIn("if(ok){sleep(0);send_c47f_playable(c);}", gs)
        self.assertIn("shop_kind==0u||shop_kind==2u||shop_kind==3u||shop_kind==4u", gs)
        self.assertIn("shop_purchase_balance", runtime)
        self.assertIn("inventory_instances_instance_exchange(coupon,identity,0u,&remaining)", runtime)
        self.assertIn("shopping_state_save()", runtime)
        self.assertIn("g_stable_equip[1]=requested[1]", runtime)
        self.assertIn("shopping_add_unique(g_owned_equipment", runtime)
        for coupon, low, high in ((45000001, 10100004, 10100008),
                                  (45000002, 10100009, 10100013)):
            self.assertRegex(runtime, rf"case {coupon}u:return face>={low}u&&face<={high}u")

    def test_managed_c3d1_offsets_and_face_scope_match_client(self):
        network = text(MANAGED_NET)
        database = text(MANAGED_DB)
        catalog = text(MANAGED_CATALOG)
        for offset in (60, 62, 64, 66):
            self.assertIn(f"payload.AsSpan({offset}, 2)", network)
        section = network[network.index("case 0xC3D1"):network.index("case 0xC3CF")]
        self.assertNotIn("payload.AsSpan(42, 2)", section)
        self.assertIn("faceOptionsFirstField: 8", catalog)
        self.assertIn("FaceOptions = faceOptions", catalog)
        self.assertIn("coupon.FaceOptions.Contains(requestedFace)", database)
        self.assertIn("int[] immutableOffsets = [0, 8, 12, 16, 20, 24, 28, 32]", database)


if __name__ == "__main__":
    unittest.main()
