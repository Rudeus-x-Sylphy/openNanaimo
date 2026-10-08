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
- Unique-pet conflicts preserve the pending prize and card set. Materials stack and consume capacity by quantity.
- Reward-distribution changes with pending draws require administrative review.
- A committed `(character, session, request ID)` receipt returns its original reward, including after the page is disabled. Request correlation uses a ten-second window with at most256 entries; persistent receipts enforce committed transaction identity.
- Responses use fixed printable ASCII text, at most23 bytes, followed by an explicit NUL terminator. The accepted alphabet excludes percent signs and embedded NULs.

## Validation scope

`CardUseRegression` covers parser and dispatcher behavior, session authorization, atomic inventory transactions, capacity preservation, retries and configured redemption using isolated databases and synthetic reward pools. These checks establish adapter behavior; client interaction has its own acceptance record in knowledge topic09.
