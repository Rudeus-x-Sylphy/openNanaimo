# Event-card reward configuration

The adapter implements dedicated `C3FD/C3FE` redemption with explicitly configured reward pools. Each enabled page defines its item IDs, quantities and probabilities; the default configuration keeps redemption disabled while card drops, collection and album display remain active.

## Configuration

The UTF-8 file `event-card-rewards.json` resides beside the active `game.db`, normally in `adapter_data`. The adapter reads it for each new redemption. The disabled configuration is:

```json
{
  "Version": 1,
  "Pages": []
}
```

Each page has these exact fields:

- `Page`: unique integer from 1 through 10.
- `Rewards`: a nonempty array of objects with exactly `Code`, `Quantity` and `Weight`.
- `Code`: an existing shop-catalog item, including clothing, pets, pet materials, furniture or ordinary items. Catalog membership also covers items with purchasing disabled.
- `Quantity`: **1** per redemption.
- `Weight`: integer from 1 through 10000; each page totals **10000**, with unique item codes. These are adapter-defined probabilities.

The size limit is 64 KiB. Strict parsing checks required fields, duplicate keys, item eligibility and page uniqueness. Invalid or absent configuration returns a bounded failure and preserves all cards. Rewards use the ordinary-item response branch. Atomic configuration replacement keeps each redemption on one complete configuration.

Page P consumes one of each of the ten codes starting at `50000001 + (P - 1) * 10`. Pages1–4 describe secret-stage warrior series, page5 special outfits/diamonds, page6 mascot pets/outfits/items, and pages7–10 are placeholders. Redemption values come from the explicit configuration.

## Transaction and retries

- The current account/character session authorizes the request. Ten-card consumption, one-item grant, quick-slot/furniture remapping and the receipt commit in one transaction.
- Insufficient capacity preserves the chosen item in `EventCardPendingDraws` and keeps all ten cards. The same pending reward remains selected when space becomes available.
- A pet whose catalog entry carries an authored duration is not a conflict: it holds a lifespan on the same terms as timed clothing, so the redemption extends its remaining period on the single existing row (level and experience preserved, no extra slot), stores a real wire expiration, and starts counting from that grant when the row has no stored expiry yet. A pet authored with zero duration keeps the permanent sentinel and preserves the pending prize and the card set instead. Materials stack and consume capacity by quantity.
- Reward-distribution changes with pending draws require administrative review.
- A committed `(character, session, request ID)` receipt returns its original reward, including after the page is disabled. Request correlation uses a ten-second window with at most256 entries; persistent receipts enforce committed transaction identity.
- Responses carry fixed operator-facing text encoded as GBK (the client's own text encoding), at most 23 encoded bytes, followed by an explicit NUL terminator. Control characters, percent signs and characters outside GBK are rejected; longer text is truncated to the byte budget instead of failing the response.

## Validation scope

`CardUseRegression` covers parser and dispatcher behavior, session authorization, atomic inventory transactions, capacity preservation, retries and configured redemption using isolated databases and synthetic reward pools. These checks establish adapter behavior; client interaction has its own acceptance record in knowledge topic09.
## Shipped default pool

The reviewed default for both card-use flows ships with the repository at
`release/components/cards/event-card-rewards.json` (page 1-4 pools: boss pet by
duration, whole warrior outfit set, three warrior gems, whole warrior decoration
set, bread plus drink, and a direct 200-Hans grant). It is registered in
`manifest/open_release_manifest.json` and the patch allowlist, but it is **not**
auto-installed: copy it beside the active database before starting the adapter.

```powershell
Copy-Item release/components/cards/event-card-rewards.json <install-root>/adapter_data/event-card-rewards.json
```

Groups (`Bundle`) grant every listed code in one redemption, clothing codes are
resolved to the character's gender, `Hans` grants gold instead of an item, and
timed clothing extends its stored expiration instead of becoming permanent.
