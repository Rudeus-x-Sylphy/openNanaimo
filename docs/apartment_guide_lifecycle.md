# Apartment Guide and Scene Lifecycle

## Entry

Apartment entry synchronizes the room state, then returns the player state and the interior object snapshot. Owner and visitor sessions remain independent, and the local player entity is published exactly once in a single-player room.

## Guide completion

After the Jack Dudu guide finishes, the player remains in the current apartment scene with valid input, actor, and object state. Later interactions continue normally, and leaving the apartment follows the ordinary return-to-village flow. The current session receives exactly one authoritative publication of its own actor.

## Validation

- A single-player entry produces exactly one local actor publication.
- In a shared room, a newly entered actor is sent only to other sessions in that room, while existing actors are sent to the newcomer.
- Guide completion leaves the room session available for refresh, recommendation, decoration, and exit operations.
- Visit counters, recommendation points, and recovery state remain separate persisted values.
