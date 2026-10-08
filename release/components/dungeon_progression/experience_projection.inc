#ifndef NANAIMO_CHARACTER_EXPERIENCE_PROJECTION_INC
#define NANAIMO_CHARACTER_EXPERIENCE_PROJECTION_INC
/* Shared retail display boundary. Internal values always stay 64-bit. */
struct progression_client_experience { unsigned level,lower,current,next; };
static int progression_project_client_experience(unsigned level,unsigned long long total,struct progression_client_experience*out){
    unsigned long long start,cost,current;
    if(!out||level<1u||level>200u||total>progression_experience_thresholds[200])return 0;
    start=progression_experience_thresholds[level-1u];
    if(total<start)return 0;
    out->level=level;out->lower=0u;
    cost=progression_experience_thresholds[level]-start;current=total-start;
    if(!cost||cost>2147483647ULL||current>cost||(current==cost&&level<200u))return 0;
    out->current=(unsigned)current;out->next=(unsigned)cost;return 1;
}
#endif
