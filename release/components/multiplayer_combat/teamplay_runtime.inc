#ifndef NANAIMO_MULTIPLAYER_RUNTIME_ROOM_START_BASE_INC
#define NANAIMO_MULTIPLAYER_RUNTIME_ROOM_START_BASE_INC
#define multiplayer_send_roster_current multiplayer_send_roster_current_roster_base_legacy
#define send_cf71_member send_cf71_member_roster_base_legacy
#include "realtime_runtime.inc"
#undef send_cf71_member
#undef multiplayer_send_roster_current

static int multiplayer_room_owner_index(void){int i,best=-1;unsigned ord=0xFFFFFFFFu;
if(g_multi_current>=0&&g_multi_conn[g_multi_current].managed_room_identity){unsigned uid=g_multi_conn[g_multi_current].managed_owner_uid;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active&&g_multi_conn[i].uid==uid)return i;return -1;}
for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active&&g_multi_conn[i].join_order<ord){best=i;ord=g_multi_conn[i].join_order;}return best;}
static unsigned multiplayer_room_slot_idx(int idx){int i,owner;unsigned slot=1u,target_order;if(idx<0||idx>=MULTI_MAX_PLAYERS||!g_multi_conn[idx].active)return 0u;if(g_multi_conn[idx].managed_room_identity)return g_multi_conn[idx].managed_room_slot;owner=multiplayer_room_owner_index();if(owner<0||idx==owner)return 0u;target_order=g_multi_conn[idx].join_order;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(i!=owner&&i!=idx&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active&&(g_multi_conn[i].join_order<target_order||(g_multi_conn[i].join_order==target_order&&i<idx)))slot++;return slot;}
static unsigned multiplayer_current_room_slot(void){return multiplayer_room_slot_idx(g_multi_current);}
static void multiplayer_send_roster_current(void){int i,src=g_multi_current;unsigned owner,slot;struct multiplayer_profile target;if(src<0)return;owner=multiplayer_owner_uid();multi_capture_profile(&target);for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){slot=multiplayer_room_slot_idx(i);multi_apply_profile(&g_multi_conn[i].profile);send_cf71_member(g_multi_conn[src].socket,owner,g_multi_conn[i].uid,slot);}multi_apply_profile(&target);}
#endif
