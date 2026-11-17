# TG7: mutants that no input can tell apart

Every mutant listed here survived a full run of the Motilal or Groww tests after the tests were extended for it. Each has an argument for why no input changes the result, taken from the code named, not from the run. A mutant that a test could kill has one (see `tg7.md`); these have none that is not a restatement of the original.

## Groww feed codec (`GrowwFeedCodec.cs`, set `tg7-groww-feed-3.json`)

| Change | Why it cannot be seen |
|---|---|
| Shadow price subscription stored with mode 2 | `OnData` filters `!Shadow` before it reads `Mode`, and unsubscribe compares exchange, segment and token only. A shadow's mode is never read. |
| Key prefix `depth` and `ltp` swapped | The key only names an entry of the codec's own dictionaries. The swap is a one-to-one rename, and no code tests the prefix. |
| `Wire` numeric mode written as 4, 2 or swapped | `NumericMode` is never read (the only references are its declaration and the two writes). |
| `start >= 0` as `> 0`, `end > start` as `>= start` | An INFO frame starts with the text `INFO`, so the opening brace is never at 0; the line end is after the brace, so the two are never equal. |
| `headerEnd < 0` as `<= 0`; `end < 0` as `<= 0` | A header end or line end found after the text `MSG` is at index 3 or later. |
| Frame scan `i + 2 < len` as `<=` or `i + 3 <` | The scan stops at the first `MSG`, which `DecodeBinary` has already found within the first 47 characters; it never reaches the end. |
| Line-end scan `i + 1 < len` as `<=`; `'\r' && '\n'` as `\|\|`; not-found `-1` as `-2` | The header's line end is known to exist (`headerEnd >= 0`); the first CR in the header is the line end; both -1 and -2 are negative and tested with `< 0`. (`i + 2 <` was a different change and is killed.) |
| Sid-found message also searched by subject (`found is null \|\| ...`) | The second search finds the wire of the same token and kind, which is the wire the subscription is already on, so it publishes the same tick. |
| Tick kind 1 written as 2, depth-or-none kind written as 2 | The kind is compared only with 3. 1 and 2 behave the same. |
| `Missing field = volume ? 0 : 0.0` swapped | In C# the two arms of the conditional unify to `double`, so both are the double 0. |
| Reader `Done` as `>`; `Tag` end as `>` | At the end of the buffer `Varint` reads nothing and returns 0, so `Tag` gives field 0 and the loop breaks, as it does for `>=`. |
| Varint shift bound `< 64` as `<= 64` or `< 65` | Shifts go 0, 7, ..., 63, 70. 64 is never reached; 70 fails every one of the three tests. |
| Field 1 wire skip `2 == wire ? 2 : wire` as `3 == wire` | For wire 2 both skip as 2; wire 3 (start group) is not sent by Groww. |
| `case 2 or 3` as `case 3 or 3` | Field 2 then falls to the default `Skip(wire)`; for wire 0, which is what the server sends, that reads one varint, as the case did. |

## Motilal (`tg7-motilal-3.json`)

| Change | Why it cannot be seen |
|---|---|
| Auth part of the stored token `Length > 0` as `>= 0` | `AddHeaders` sends Authorization only when `!string.IsNullOrEmpty(auth)`, so an empty part and null are the same. |
| Error code and order number `Length > 0` as `>= 0` | Both come from `Truthy(...) ? Str(...) : null`; a truthy string is not empty. |
| Tick size `t > 0` as `>= 0` | `MarketPriceProtection.ProtectedPrice` ignores a tick of 0 or less (`tick <= 0` returns the unrounded price). |
| Refusal status `>= 400 ? status : 400` as `> 400` or `>= 401` (place, change, cancel) | For a status of 400 both arms give 400; below 400 both give 400; above, both give the status. |
| Order precision `\|\|` as `&&` | With no precision, the original passes the value through; the mutant reads `null` as scale 0, which divides by 1. |
| Position average `sellQty > 0` as `>= 0` | The branch is `net < 0`, and `net = buy - sell` with `buy >= 0`, so `sell > 0` already holds. |
| Margin rows `matched > 0` as `>= 0` | With no matched rows the sum is 0, the same as the else arm. |
| Quote low `positive.Count > 0` as `>= 0` | The guard above returns when both close and open are 0 or less, so at least one of low, open, close is positive. |
| Packet wire and type byte `< 128` as `<= 128` or `< 129` | Byte 128 gives a non-ASCII wire name or type that no registration and no packet kind has; the same as the empty name. |
| Scrip, rounded value and 16-bit read over a slice one byte longer | `ReadInt32LittleEndian`, `ReadSingleLittleEndian` and `ReadInt16LittleEndian` read their own width from the front of the slice. |
| Whole-book check `&&` as `\|\|` | Every level packet sets the bid and the ask of its level together (`Depth`), so the two sides fill at the same time. |
| Codec key `mode == 3` as `!=`, or `3` as `4` | The key names an entry of the codec's own dictionary; both are one-to-one renames. |
| Codec quote `Count > 0` as `>= 0` (modes 1, 2 and the snapshot read) | `Quote(key)` creates a dictionary and the caller assigns into it in the same call, so a stored quote is never empty. |
| Codec depth `Bids.Count == 0 && Asks.Count == 0` and its two changes | `Bids` and `Asks` are arrays of five, so a count of 0 never happens; every variant is false. |
| Snapshot delay between batches (`<` as `<=`, 100 ms as 101 ms) | A pause of 1 to 100 ms between batches of a call that already waits seconds; there is no clock in the test to see it. |
| Socket connect wait 10 s as 11 s, read buffer 16 KB as 17 KB, close test inverted | A constant of the connection (the first two) and the close frame sent or not at the end of the connection, which the server cannot observe. |
