# Apartment Guide and Scene Lifecycle

## Entry and room ownership

Apartment entry synchronizes room state, player state, and the interior object snapshot. Owner and visitor sessions remain independent. A single-player room publishes its local actor exactly once; existing actors are sent to a newcomer, and a newly entered actor is sent only to other sessions in the room.

The original client starts the apartment guide for an owner room at LV3 or above while its apartment-guide completion bit is unset. The server accepts apartment guide completion after the main tutorial, including older profiles without a separate introduction ledger.

## Guide completion and resource consistency

The Jack Dudu guide uses guide ID 5. Its completion response carries the same effective maximum HP and MP as the room's current resource snapshot. The client applies these maximum values before it displays the guide reward window, while retaining its existing current HP and MP. Keeping both values in the same resource model prevents the guide health bars from exceeding their texture bounds.

Completion retains the existing actor, furniture, recovery schedule, current HP/MP, and saved base attributes. A character without an effective-resource snapshot uses the same profile maxima as ordinary room synchronization. The welcome reward is one 100-Hans certificate in the ordinary game-item inventory. Using the certificate credits 100 Hans in the same transaction that consumes it. Reward and completion are committed together, and repeated confirmation grants the certificate once.

Subsequent refresh, recommendation, decoration, and exit operations use the ordinary room lifecycle. Visit counters, recommendation points, and recovery state remain separate persisted values.

## Validation scope

Construction checks cover response fields and lengths, resource consistency for pet-equipped and unequipped characters, repeated and out-of-order completion, delayed completion, room entry failure, local actor uniqueness, owner/visitor separation, and unchanged saved HP/MP.

Original-client acceptance of the changed adapter must confirm the final dialogue, reward confirmation, room interaction, recovery, exit, and reentry with an effective-resource bonus equipped.
