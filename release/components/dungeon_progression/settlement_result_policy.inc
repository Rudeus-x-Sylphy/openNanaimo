#ifndef DUNGEON_SETTLEMENT_RESULT_POLICY_INC
#define DUNGEON_SETTLEMENT_RESULT_POLICY_INC
static unsigned dungeon_settlement_visible_rating(unsigned allow_progress){return allow_progress?5u:0u;}
/* Only a completed battle can authorize progression at the result boundary. */
static unsigned dungeon_settlement_progress_allowed(unsigned requested,unsigned epoch,unsigned tuple_valid,unsigned terminal_required,unsigned terminal_seen){
    return requested&&epoch&&tuple_valid&&(!terminal_required||terminal_seen)?1u:0u;
}
#endif
