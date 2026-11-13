# TG7: remaining brokers, verification status

This file records, per broker, what is ported and how it was checked. It is the Gate 7 artifact in progress; the gate closes when every broker in the task list has a row and every row says what is and is not covered.

| Broker | Orders and books | Funds | Quotes and depth | History | Margin | GTT | Login | Feed |
|---|---|---|---|---|---|---|---|---|
| Dhan | yes | yes | yes | yes | yes | yes | yes | yes (5-level) |
| Dhan Sandbox | yes | yes | yes | yes | yes | no | no | no |
| Upstox | yes | yes | yes | yes | yes | yes | yes | yes |
| Fyers | yes | yes | yes | yes | yes | yes | yes | yes |
| Delta Exchange | yes | yes | yes | yes | yes | no | yes | yes |
| Wisdom Capital (XTS) | yes | yes | yes | yes | no | no | yes | no |
| 5paisa XTS | yes | yes | yes | yes | no | no | yes | no |
| IIFL (XTS) | yes | yes | yes | yes | no | no | yes | no |
| iBulls (XTS) | yes | yes | yes | yes | no | no | yes | no |
| Jainam (XTS) | yes | yes | yes | yes | no | no | yes | no |
| CompositEdge (XTS) | yes | yes | yes | yes | no | no | yes | no |
| RMoney (XTS) | yes | yes | yes | yes | yes | no | yes | no |
| Shoonya | yes | yes | yes | yes | yes | no | yes | yes |
| Zebu | yes | yes | yes | yes | n/a (no calculator) | no | yes | no |
| Flattrade | yes | yes | yes | yes | yes | no | yes | no |

The other 20 brokers in the task list are not started (Shoonya, Zebu and Flattrade are partly done). All seven XTS brokers share the XTS base class written for Wisdom, each with its own address, order mapping and sign-in differences.

## Not done, and why Gate 7 is open

- Dhan's 20-level depth connection is not ported (a depth request is the 5-level packet).
- **Delta Exchange fractional (spot) quantities.** The `/api/v1` request already accepts a fractional quantity on a crypto exchange, but the order intent, the guards, the risk checks, the sandbox and the broker order all carry a whole number, so it needs a decision on how quantity is represented from the request to the broker, not a change in the plugin.
- **GTT for Delta Exchange** has no OpenAlgo counterpart (its Python plugin has no GTT module), so there is nothing to port.
- **The 20 brokers not started** (fivepaisa, aliceblue, arrow, definedge, firstock, groww, hdfcsecurities, hdfcsky, iiflcapital, indmoney, kotak, motilal, mstock, nubra, pocketful, rupeezy, sahi, samco, tradejini, tradesmart), and the feed for Shoonya and Zebu. Each needs the full recipe: orders and books, funds, quotes and depth, history and margin where the broker has them, GTT where it has it, login, feed, with recorded fixtures and mutation checks.

## Upstox sign-in

- Ported from `broker/upstox/api/auth_api.py` and the address `utils/broker_login.py` builds: the trader signs in on Upstox's page, which sends back a `code`; the code is exchanged at `/v2/login/authorization/token` with the same redirect address. There is no separate feed token.
- The redirect address is a server setting, `Brokers:Keys:upstox:RedirectUrl`, next to `ApiKey` and `ApiSecret`. Without all three the sign-in answers 409 and says which are missing; the start call answers no address.
- `/broker-login/complete` now accepts `code` as well as `request_token`.
- Contract: five recorded cases (`tools/contract-fixtures/login_upstox.fixtures.json`, recorded with `record.py login_upstox`) check the form sent to Upstox field by field and the token stored. The words of a refusal are EP Trade bot's own, as for the other brokers.
- Integration: one test starts a sign-in, checks the address (key, redirect address and state) and completes it with the issued state.
- Mutation: `tools/mutation/tg7-upstox-login.json`, 8 mutants, all caught. The first run left one alive, an empty `access_token` taken as a token; the recorded case `login_empty_access_token` now covers it.

## Fyers sign-in

- Ported from `broker/fyers/api/auth_api.py` and the address `utils/broker_login.py` builds: the trader signs in on Fyers' page, which sends back an auth code (`request_token` in the complete call); it is exchanged at `/api/v3/validate-authcode` with `appIdHash`, the SHA-256 of `app id:secret`. The app id travels with the token on every call, so there is no feed token.
- Settings: `ApiKey` (the app id), `ApiSecret` and `RedirectUrl` under `Brokers:Keys:fyers`.
- Contract: six recorded cases, including an HTTP error with an ok-looking body and an empty token. The recorder now accepts the dictionary Fyers returns.
- Integration: start and complete through the API. Mutation: `tools/mutation/tg7-fyers-login.json`, 9 mutants, all caught (two survived the first run and each got a recorded case).

## Delta Exchange sign-in

- Ported from `broker/deltaexchange/api/auth_api.py`: no redirect and nothing typed. The key and secret from settings are proven by one signed `GET /v2/profile`; the key is stored as the session token. Setting `ApiKey` and `ApiSecret` under `Brokers:Keys:deltaexchange`; either missing answers 409 without calling Delta.
- Contract: five recorded cases (success, profile not successful, key refused, blocked by the address allow-list, other status). An unexpected status is a 502 here (Python answers 400), worded as a retry.
- Mutation: `tools/mutation/tg7-delta-login.json`, 7 mutants, all caught.

## Dhan sign-in (live Dhan only)

- Ported from `broker/dhan/api/auth_api.py` and the Dhan branch of `blueprints/brlogin.py`. Starting a sign-in asks Dhan for a consent under the client id and returns Dhan's consent page; the token id Dhan sends back is exchanged at `/app/consumeApp-consent`; a pasted access token (over 100 characters) is taken instead. Either token is proven with a read of the fund limits and refused when it is for another account than the configured client id, or when Dhan names no account.
- New in the shared code: `IBrokerLoginStart` (a broker whose sign-in page has to be asked for first), used by `/broker-login/start`; the complete call passes the account's client id to the plugin.
- Differences from OpenAlgo, on purpose: the configured client id is the key's `client_id:::` prefix, else the account's own client id (OpenAlgo checks only the prefix); a pasted token is also taken through the state-checked complete call, where OpenAlgo takes it from its own form; a pasted token is proven with one read of the fund limits where OpenAlgo makes two.
- Contract: ten recorded cases, with the steps of `brlogin.py` replayed over the real Dhan functions by `sign_in_steps` in the scenario (the recorder runs it when a scenario names one). Integration: four tests (consent request and page, missing client id, account mismatch with nothing stored, headers). Mutation: `tools/mutation/tg7-dhan-login.json`, 13 mutants, all caught. One more was removed as dead code: a pasted token is by definition longer than 100 characters, so a length check below 50 could never run.
- Dhan Sandbox has no sign-in here; its token is entered by hand as before.

## Upstox GTT

- Ported from `broker/upstox/api/gtt_api.py` and `mapping/gtt_data.py` (v3 `/order/gtt`). Upstox has no limit price on a trigger: the order fires at the trigger price, and a MARKET request becomes a market protection percentage from the same slabs the other brokers use. Every Upstox trigger must carry an entry rule, so an OCO is sent as a bracket that opens the position first (OpenAlgo does the same and logs a warning; it is kept as is, and a stop or target on a holding already owned should be a SINGLE sell trigger).
- The entry direction comes from the trigger against the last price (from the quotes call); without a last price a SINGLE falls back to the field the trader filled in, and an OCO is refused (502).
- Contract: 40 recorded cases (`upstox_gtt.fixtures.json`), covering the entry direction, market protection, brackets in both directions, the broker's error list, the book (finished triggers dropped, bracket entry hidden, legs sorted, epoch unit read from size, exchange and product mapped back).
- Deviations, each in the contract file with its reason: an unknown symbol names the symbol; a rejection that arrives inside an OK answer to a modify is reported as a failure (OpenAlgo reports success).
- Mutation: `tools/mutation/tg7-upstox-gtt.json`, 22 mutants, all caught. Three equivalent mutants were removed with the code they covered: a 25 percent cap that the slabs (at most 5) cannot reach, a second fallback for the trigger id, and the plugin's own missing-id check, which the request validation makes unreachable.

## Dhan GTT (Forever Orders, live Dhan only)

- Ported from `broker/dhan/api/gtt_api.py` and `mapping/gtt_data.py`. A SINGLE is one leg, an OCO is two legs of one order (stop-loss leg and target leg); Dhan changes one leg per call, so an OCO modify is two PUTs and stops at the first refusal, and a SINGLE modify first reads the order's own book entry to learn which leg name Dhan stored it under (entry leg when the book cannot say).
- The client id is the one saved with the key (`client_id:::key`), else the account's. The strategy name travels as Dhan's correlation id, cut to 30 characters; this needed `Strategy` on the shared `GttOrder`.
- Contract: 39 recorded cases (`dhan_gtt.fixtures.json`). The recorder now lets a scenario replace a broker's own HTTP client (Dhan builds one for HTTP/1.1).
- Deviations, each with its reason in the contract file: a symbol missing from the master contract is refused by name where OpenAlgo sends the order with the security id "None"; a refused placement carries Dhan's reason where OpenAlgo says only "Failed to place GTT"; a modify or cancel answered with an OK status and no order id is a failure here, where OpenAlgo reports success.
- Mutation: `tools/mutation/tg7-dhan-gtt.json`, 24 mutants, all caught. Not exercised: the network-failure branch of the book read before a SINGLE modify (the test harness cannot make a call fail to connect); it only falls back to the entry leg, as OpenAlgo does.

## Fyers GTT

- Ported from `broker/fyers/api/gtt_api.py` and `mapping/gtt_data.py`. A trigger leg has only a price, a trigger and a quantity, so the order it fires is always a LIMIT: a MARKET request becomes a protected limit (the last price for a SINGLE, each leg's own trigger for an OCO, moved by the protection slab and rounded to the tick; if the last price cannot be read the given price is sent). An OCO is two legs of one trigger, the one above the last price first.
- The strategy name is the order tag, reduced to letters and digits, at most 20, else "openalgo".
- Contract: 43 recorded cases (`fyers_gtt.fixtures.json`). Deviations, each with its reason in the contract file: a missing symbol is refused by name where OpenAlgo sends the trigger with no symbol; a modify answered with an OK status but an error word is a failure where OpenAlgo reports success; a book refused inside an OK answer is a failure status where OpenAlgo passes the OK status on.
- Not ported: OpenAlgo swaps the two OCO legs when a caller gives the stoploss trigger at or above the target trigger. The request validation refuses that before it reaches the broker, so the swap could never run here.
- Mutation: `tools/mutation/tg7-fyers-gtt.json`, 29 mutants, all caught.

## Dhan Sandbox margin

- Ported from `broker/dhan_sandbox/api/margin_api.py` and `mapping/margin_data.py`. The sandbox asks Dhan's multi-order calculator first, even for one position; if it refuses (an error type, or the word "failed"), it asks the single-order calculator once per position. The same client id and product mapping as the live Dhan are used.
- Contract: 16 recorded cases (`margin_dhan_sandbox.fixtures.json`). Deviations, each with its reason in the contract file: the three figures every broker gives instead of OpenAlgo's long sandbox answer (same values); the trader-worded refusals and statuses used for the other brokers; and a basket with one refused position is refused, where OpenAlgo adds up the positions that answered and so understates the margin.
- Mutation: `tools/mutation/tg7-dhan-sandbox-margin.json`, 11 mutants, all caught. One more was removed as equivalent (a blank figure already reads as zero).

## Delta Exchange feed

- Ported from `broker/deltaexchange/streaming` (`delta_adapter.py`, `delta_websocket.py`, `delta_mapping.py`) as `DeltaFeedCodec`: JSON over Delta's public socket, which needs no sign-in. LTP and Quote read the ticker channel; Depth reads the order book channel as well as the ticker (the book carries no price). The ticker takes any number of symbols in one message, the book one symbol per message; a reconnect repeats each channel's symbols sorted.
- A field the ticker leaves out is left out of the tick rather than sent as zero, so one channel never blanks what the other published; a present array reads a null inside it as zero. The cache per symbol is dropped with the last subscription of it. Sizes are whole numbers, truncated.
- `FeedRequest` and `FeedTarget` now carry the broker's symbol, which Delta puts on the wire and in every frame.
- Contract: 37 scenarios replayed against the real Python adapter and socket client (`feed_deltaexchange.fixtures.json`; the recorder feeds JSON text frames).
- Not part of this feed: Delta's signed account socket (order, position and margin events). OpenAlgo publishes those to its order topic from the same adapter; EP Trade bot's order updates come from its own order stream.
- Mutation: `tools/mutation/tg7-delta-feed.json`, 27 mutants, all caught (one equivalent mutant removed).

## Delta Exchange leverage

- Ported from `broker/deltaexchange/api/order_api.py` (`_set_leverage`). Delta sets leverage with a call of its own, `POST /v2/products/{id}/orders/leverage`, before the order. The saved common leverage (the `leverage_config` row) decides; with none saved, the configured default (`Brokers:Keys:deltaexchange:DefaultLeverage`); a value of 0 or nothing means no call and the product keeps its leverage.
- A refused leverage call lets the order go at the leverage the product has, as OpenAlgo does, unless `Brokers:Keys:deltaexchange:AbortOnLeverageFailure` is `true` (case and spaces ignored); then the order is refused with a plain message naming the leverage, where OpenAlgo answers an internal error (deviation, recorded in the contract file).
- OpenAlgo also accepts a `leverage` field on the order itself. The `/api/v1` order request here has no such field, so only the saved and default values apply.
- Contract: 11 recorded cases (`deltaexchange_leverage.fixtures.json`; the recorder can now seed the saved leverage and environment settings per scenario). Mutation: `tools/mutation/tg7-delta-leverage.json`, 11 mutants, all caught.

## Upstox feed

- Ported from `broker/upstox/streaming/upstox_client.py` and `upstox_adapter.py`. Two contract changes it needed, now shared: a codec can say its messages go as binary frames (`SendsBinary`; Upstox sends its JSON subscribe messages that way), and a broker can hand out its socket address per connection (`IBrokerFeedAuthorize`; Upstox gives one for the login token, and a refusal at that step is the login having expired or the account being at its limit of concurrent feeds).
- `UpstoxProtobuf` reads the market-data protobuf from the published schema without a protobuf library, into the shape OpenAlgo gets from `MessageToDict`: 64-bit numbers as text, enums by name, defaults left out, the wrapped indicative price flattened. A malformed or cut-off frame publishes nothing.
- `UpstoxFeedCodec` holds to the adapter: LTP is the `ltpc` mode, Quote and Depth share the `full` mode and one message; Upstox's per-mode key caps (5000, 3000, 2000, 50) and the combined cap across modes (the smallest of those in use) are applied, a mode that has an entry counts as in use even when emptied, and a reconnect repeats everything. A tick without a price takes the last one seen for the instrument; the indicative auction fields are never carried forward. The timestamps in depth and carried-forward ticks stay the text Upstox sent, as OpenAlgo's do.
- Contract: 50 scenarios replayed against the real Python adapter and client (`feed_upstox.fixtures.json`, built with the real protobuf classes plus hand-built frames for zero values on the wire, unknown fields and a length past the frame). The unordered ticks of a frame with several instruments are compared in any order, because Python reads them from a hash map. Integration: four tests of the address request.
- Mutation: `tools/mutation/tg7-upstox-feed.json`, 38 mutants, all caught after nine cases were added for the ones that first survived. One equivalent mutant was removed (a fallback for a depth timestamp that is always set).
- Not part of this feed: Upstox's health check that forces a reconnect after 90 seconds of silence; the feed host reconnects on its own schedule.

## Dhan feed (5-level connection, live Dhan only)

- Ported from `broker/dhan/streaming/dhan_websocket.py` and `dhan_adapter.py` as `DhanFeedCodec`. Requests are JSON with a request code per mode (ticker, quote, full, and one to stop each), at most 100 instruments to a request; frames are little-endian binary packets with an 8-byte header (ticker, quote, full with five levels; open interest, previous close and disconnect packets publish nothing). The socket address carries the token and client id (the one saved with the key, else the account's).
- Dhan tracks one mode per instrument, so asking for another replaces the first and stopping an instrument stops it in the mode it was last asked for; an unsubscribe for something not subscribed sends nothing. A tick is matched on security id and segment, else on the security id alone, and is published under the kind of packet that arrived, whatever mode was asked for.
- Differences from OpenAlgo, on purpose: a reconnect sends each request once (OpenAlgo sends it twice, once from the socket's list and once from the adapter's); the cap of 5000 instruments per connection is the feed host's, not the codec's; a packet that claims zero length ends the frame where OpenAlgo's loop would never move on.
- Contract: 36 scenarios replayed against the real Python adapter and client (`feed_dhan.fixtures.json`); integration: four tests of the address. Mutation: `tools/mutation/tg7-dhan-feed.json`, 26 mutants, all caught after five cases were added for the ones that first survived; two were removed as equivalent or moved to the host.

## Fyers feed

- Ported from `broker/fyers/streaming` (HSM binary protocol): the `hsm_key` is read from the access token (refused when expired or absent), sign-in is a binary message, subscriptions wait until the sign-in is accepted, and snapshot/update frames keep per-topic state and scaling. Quote, depth and index depth shapes match the Python adapter.
- Contract: 43 recorded cases (`feed_fyers.fixtures.json`) plus 4 key tests. Deviations, each with its reason in the contract file: a subscription made before the sign-in is accepted is held and sent after it (OpenAlgo drops it), and a feed nobody asked for is never given to the only subscription (OpenAlgo guesses).
- Mutation: `tools/mutation/tg7-fyers-feed.json`, all caught after survivors each got a recorded case or the equivalent code was removed.

## Wisdom Capital (XTS): orders, books, funds and sign-in

- Ported from `broker/wisdom` (`order_api.py`, `funds.py`, `mapping/order_data.py`, `mapping/transform_data.py`, `api/auth_api.py`) into `Brokers/Xts/XtsPluginBase.cs`, the base the other XTS brokers will use.
- Contract: 32 recorded cases for orders, books and funds (`wisdom.fixtures.json`) and 8 for sign-in (`login_wisdom.fixtures.json`). Deviations, each with its reason in the contract file:
  - An unknown symbol is refused by name; OpenAlgo sends a null instrument id.
  - A broker refusal passes the broker's reason on (place, modify, cancel); OpenAlgo says "Failed to ...". A place reply with an error type answers a refusal; OpenAlgo answers success with no order id.
  - Order ids are text.
  - A stop order waiting for its trigger is "trigger pending" in the book (OpenAlgo leaves "Trigger Pending"), the spelling cancel-all reads.
  - Smart order: OpenAlgo looks for the open position under keys the XTS reply does not have, so it never sees one and BUYS the target quantity; EP Trade bot reads the position and trades the difference.
  - A position whose symbol is not in the master contract cannot be closed by symbol; the answer names it as still open.
  - An expired session on funds is reported as an expired session; OpenAlgo shows an empty balance.
- Mutation: `tools/mutation/tg7-wisdom.json`, 29 mutants, all caught (ten survived the first run; each got a recorded case or a unit test, and one equivalent product default was removed).
- Not ported, and refused rather than guessed: quotes, depth and history (a separate market-data service), margin, and the feed. Position and holding profit and loss are zero, as in OpenAlgo (the XTS position reply carries no last price).

## 5paisa XTS and IIFL (XTS): orders, books, funds and sign-in

- Same XTS base class as Wisdom Capital, each held to its own Python plugin: 36 recorded cases each for orders, books and funds (`fivepaisaxts.fixtures.json`, `iifl.fixtures.json`) and 8 each for sign-in. The recording uses the broker's address without a trailing slash (5paisa's OpenAlgo address ends in one, which would make every path start with a double slash).
- Extra deviations for these two: OpenAlgo fails with an internal error on a trade that has no price and on a position reply without a result; EP Trade bot answers as it does for a well-formed reply. Order status reads the average price from the trade book, so it differs for the same reason.
- Not mutation-tested again: the mapping code is the shared Wisdom base, whose 29 mutants are all caught; what is specific to these two (display name, address) is held by the contract paths and hosts.
- iBulls is not done: its Python plugin turns a market order into a limit order priced from a quote (bid plus 0.1 percent to buy, ask minus 0.1 percent to sell), so it needs the XTS market-data service ported first.

## XTS quotes, depth and several quotes

- Ported from `broker/wisdom/api/data.py` into the shared XTS base: a quote and its open interest are two calls to `/apimarketdata/instruments/quotes` (message codes 1502 and 1510) with the feed token; several quotes go in batches of 50. 17 recorded cases for Wisdom Capital (`wisdom_data.fixtures.json`) and the same set recorded against 5paisa XTS and IIFL.
- Deviations: a refused or empty quote is an error naming the broker and its reason (OpenAlgo says "Failed to fetch market data"); a refused depth is an error (OpenAlgo answers success with a depth of zeros, which reads as an empty market).
- Not modelled: OpenAlgo refreshes an expired feed token itself (a market-data sign-in in the middle of a quote). EP Trade bot's plugin cannot store a new token, so an expired market-data session shows as the broker's refusal until the next sign-in.
- Mutation: `tools/mutation/tg7-xts-data.json`, 11 mutants, all caught (three equivalent ones removed or simplified).
- Still not ported for XTS: the feed (all seven brokers). Margin: only RMoney has a calculator (below); OpenAlgo's other six XTS plugins raise "does not support margin calculator API" and EP Trade bot answers the same 501.

## XTS history

- Ported from `broker/wisdom/api/data.py` `get_history` (the ohlc service, `compressionValue` per interval, a week at a time). 19 recorded cases for Wisdom Capital (`history_wisdom.fixtures.json`) and the same set against 5paisa XTS and IIFL. Intraday timestamps move from XTS's clock to IST and start on the candle boundary; daily candles sit at midnight; duplicate timestamps keep the first candle; short or unreadable rows are left out.
- Deviations: OpenAlgo ends every week but the last at midnight of its seventh day and starts the next on the day after, so one day in seven is never asked for (EP Trade bot asks for whole days); no candles is an empty success (OpenAlgo fails with "No objects to concatenate"); a refused week says which days and why; an unsupported interval is a 400 naming the choices; IIFL's retry after a market-data sign-in is not done.
- Not ported: today's daily candle built from a quote when the day's history is empty.
- Mutation: `tools/mutation/tg7-xts-history.json`, 14 mutants, all caught (two survived the first run and each got a recorded case).

## iBulls (XTS)

- Same XTS base class, with the market-data service under `/apibinarymarketdata`. Held to `broker/ibulls`: 40 recorded cases for orders, books and funds (`ibulls.fixtures.json`), 8 for sign-in, 17 for quotes and depth, 19 for history.
- iBulls turns a market order into a limit order priced from a quote: bid plus 0.1 percent to buy, ask minus 0.1 percent to sell, rounded to two places; with no usable quote (no market-data token, a refused quote, a bid or ask of zero) the order goes as a market order. Seven recorded cases pin this. OpenAlgo looks the market-data token up by the logged-in user's name, falling back to a name hard-coded in the plugin; EP Trade bot uses the account's own token.
- Mutation: `tools/mutation/tg7-ibulls.json`, 10 mutants, all caught.

## Jainam (XTS)

- Same XTS base class, market data under `/apibinarymarketdata`, address `https://jtrade.jainam.in:5000`. Held to `broker/jainamxts`: 36 recorded cases for orders, books and funds, 8 for sign-in, 17 for quotes and depth, 19 for history (80 in all).
- Its interactive sign-in sends an `accessToken` with the fixed word "jainamxts" instead of a source (the sign-in handler in OpenAlgo passes that word); the market-data sign-in is the usual one. Like IIFL, Jainam signs in to market data again when told the token is invalid; EP Trade bot reports the refusal instead.
- OpenAlgo's Jainam plugin fails with an internal error on a trade with no price and on a position reply with no result (as 5paisa XTS and IIFL do); EP Trade bot answers as for a well-formed reply.
- Mutation: `tools/mutation/tg7-jainam.json`, 2 mutants, both caught (the rest is the shared base).

## CompositEdge (XTS)

- Same XTS base class. Held to `broker/compositedge`: 36 recorded cases for orders, books and funds, 17 for quotes and depth, 19 for history, and 10 for sign-in (82 in all), plus 6 integration tests for the sign-in address and the session the page sends back.
- A redirect sign-in: the trader signs in on CompositEdge's page (`.../interactive/thirdparty?appKey=...&returnURL=...`, with the state on the return address), which sends the session back holding an access token; that token is exchanged for the interactive session and the market-data session is signed in with its own keys. The two sessions must name the same account (compared without regard to letters or spaces; if either does not name one, no check is made, as in OpenAlgo); otherwise the sign-in is refused with a plain sentence and a conflict status. The return address is a server setting, `Brokers:Keys:compositedge:RedirectUrl`.
- The complete call takes the access token as `request_token`, or the session JSON (also when it is a JSON string holding JSON), from which the token is read.
- Mutation: `tools/mutation/tg7-compositedge.json`, 10 mutants, all caught (three first needed a rewritten pattern).

## RMoney (XTS)

- Same XTS base class, market data under `/apibinarymarketdata`, address `https://xts.rmoneyindia.co.in:3000`. Held to `broker/rmoney`: 41 recorded cases for orders, books and funds, 19 for quotes and depth, 19 for history and 13 for sign-in (92 in all).
- Differences from the others, all recorded: funds come from the balance entry for everything ("ALL|ALL|ALL"), else the first; a position reply's result may be a list and its instrument id key may be spelled either way; several quotes carry the best bid and ask sizes and the open interest (a second call, merged where not zero, ignored if refused).
- Sign-in: the trader signs in on RMoney's page, which posts back a session holding the interactive token itself (JSON, sometimes a JSON string holding JSON). RMoney is asked who the token belongs to (its `/interactive/user/profile`, under any of four spellings of the client id), and the user id posted beside the token is never trusted. The market-data keys' session must name the same account (letters and spaces ignored; no check if it names none); a refused market-data sign-in leaves the session without a feed token rather than failing, as in OpenAlgo.
- OpenAlgo's RMoney plugin fails with an internal error on a position reply whose result is a list, on a trade with no price and on a position reply with no result; EP Trade bot lists the positions as for a well-formed reply. RMoney's host lookup (`/hostlookup`) is not called by OpenAlgo's sign-in and is not ported.
- Mutation: `tools/mutation/tg7-rmoney.json`, 14 mutants, all caught (four first needed a recorded case or a rewritten pattern; one equivalent mutant was dropped).

## RMoney margin, and XTS brokers without one

- RMoney: ported from `broker/rmoney` `margin_api.py` and `mapping/margin_data.py` (POST `/interactive/orders/margindetails` with a portfolio of positions); the answer is the margin required, the margin available, any shortfall, whether the basket is valid and the broker's error message. 17 recorded cases (`margin_rmoney.fixtures.json`). Deviations: a position whose symbol is not in the master contract refuses the basket and names it (OpenAlgo leaves it out and understates the margin); a refusal sent with an OK status is an error status; an index has no calculation and says so; an expired session says so.
- Mutation: `tools/mutation/tg7-rmoney-margin.json`, 12 mutants, all caught (the order side's upper-casing is already done before the plugin, so that mutant was dropped as equivalent).
- Wisdom Capital, iBulls, Jainam, 5paisa XTS, IIFL and CompositEdge have no margin calculator in OpenAlgo; one recorded case (`margin_wisdom.fixtures.json`) holds the 501 answer.

## Shoonya (Noren API): orders, books, funds, quotes, depth and several quotes

- Ported from `broker/shoonya` (`order_api.py`, `funds.py`, `api/data.py` quotes and depth, `mapping/`). Every call is a `jData=<json>` form in a text/plain body with a bearer token. The recorder and the test fake now read that form as JSON (`record.py`, `FakeBroker`), so the request bodies are compared field by field. 38 recorded cases for orders, books and funds (`shoonya.fixtures.json`) and 18 for quotes (`shoonya_data.fixtures.json`).
- Market and stop-market orders are priced like OpenAlgo's market price protection (Shoonya refuses both over the API): the last price moved by the slab percentage (option slab for CE and PE), rounded to the quote's tick, sent as a limit or stop-limit; with no usable quote a market order goes as a market order, and a stop-market becomes a stop-limit priced off its trigger. A quote that answers for another instrument is asked for again, up to three times, then refused.
- Deviations: an unknown symbol is refused by name; a refused place, modify or cancel is a refusal with Shoonya's reason (OpenAlgo answers success); an empty book answered with Shoonya's "no data" refusal is an empty book (OpenAlgo fails with an internal error on cancel-all and close-all); a lapsed session on funds or quotes says so; a position whose symbol is not in the master contract cannot be closed by symbol and is named as still open; error wording for quotes.
- Platform fix found by this broker: order status read the trade's average price only when it was a JSON number; a broker that sends it as text ("1499.95") showed 0. It is now read as OpenAlgo reads it.
- Held as OpenAlgo has them (not corrected, flagged): a holding's quantity counts held shares and the demat quantity both (it takes the larger of the two other quantities and adds the held ones), as the Python plugin does.
- Mutation: `tools/mutation/tg7-shoonya.json`, 34 mutants, all caught (three first needed recorded cases; two equivalent ones were dropped: a spelling that falls through to the same text, and a short-position formula that is algebraically the same).
- Not yet ported for Shoonya: the feed. History, margin and the sign-in are below.

## Shoonya feed (Noren websocket)

- `NorenFeedCodec` (Core) is the protocol with no socket in it, held to `broker/shoonya/streaming` by 75 recorded scenarios (`feed_shoonya.fixtures.json`): the sign-in is the first message and nothing else is sent until the broker accepts it; touchline (LTP and Quote) and depth are separate lists of "EXCHANGE|token" scrips joined with "#" in batches of 100; the broker is told when the first subscription of a kind arrives and when the last one leaves (counted per instrument and mode, and per scrip when two names share one); an accepted sign-in sends every subscription held again and forgets the cache; frames are routed by exchange and token, or by token alone when only one subscribed scrip has it; a zero open, high, low, close or average price does not replace a non-zero one already held.
- Recorded difference: OpenAlgo refuses an unsubscribe while the socket is signed out and keeps the instrument, so the next sign-in subscribes it again. EP Trade bot lets go of it at once.
- Mutation set `tools/mutation/tg7-noren-feed.json`: 71 mutants, all caught (five more were dropped as equivalent: a de-duplication the counts already make, a zero that replaces a zero, and three unreachable routing branches).

## Shoonya sign-in

- Ported from `broker/shoonya/api/auth_api.py` and the address `blueprints/brlogin.py` builds: the trader signs in on Shoonya's OAuth page (`.../OAuthlogin/authorize/oauth?client_id=...`, the client id being the part of the saved key after ":::"), which sends back a code; the code is exchanged at GenAcsTok with the SHA-256 (lower-case hex) of the client id, the secret and the code. There is no feed token.
- Contract: 7 recorded cases (`login_shoonya.fixtures.json`) check the exchange body field by field (the checksum included) and the token stored; 5 integration tests cover the address and the keys. Refusals are worded for the trader; an HTTP error from Shoonya and an answer with no token say the sign-in was not accepted.
- Mutation: `tools/mutation/tg7-shoonya-login.json`, 10 mutants, all caught (one equivalent mutant, an HTTP status check whose refusal reads the same as the check after it, was dropped).

## Zebu (Noren API at go.mynt.in)

- Same Noren base class as Shoonya (`NorenPluginBase`), held to `broker/zebu`: 38 recorded cases for orders, books and funds (`zebu.fixtures.json`), 18 for quotes (`zebu_data.fixtures.json`) and 7 for sign-in (`login_zebu.fixtures.json`).
- Differences from Shoonya, recorded: a stop-market order is sent as it is (only market orders are priced from a quote); holdings carry the product and profit and loss on each row, the held quantity is held shares plus the larger of the T1 and demat quantities, and the totals are Zebu's own (value counts every quantity, invested counts the held quantity only, profit is zero); the sign-in address is `go.mynt.in/OAuthlogin/authorize/oauth`.
- OpenAlgo's Zebu plugin takes whatever quote comes back, including another instrument's. EP Trade bot checks every quote as it does for Shoonya (the Noren backend is the same) and asks again, then refuses; the three recorded cases that show the difference are deviations.
- Mutation: `tools/mutation/tg7-zebu.json`, 11 mutants for what is specific to Zebu, all caught (one first needed a holding whose product is not CNC); the shared Noren code is covered by Shoonya's 34.
- Not yet ported for Zebu: the feed. Zebu has no position-specific margin calculator (OpenAlgo raises "does not support"; EP Trade bot answers 501, one recorded case in `margin_zebu.fixtures.json`).

## Shoonya margin

- Ported from `broker/shoonya` `margin_api.py` and `mapping/margin_data.py`: the basket goes to GetBasketMargin (the first position at the top level of the request, the rest in `basketlists`), and the answer is the margin used (span and exposure are not split out and are 0). Market and stop-market positions are converted to limit and stop-limit as on orders, priced from a quote; with no usable quote the converted position carries the price given (market) or the trigger (stop-market).
- 17 recorded cases (`margin_shoonya.fixtures.json`). Deviations: a refusal is an error status with the reason (OpenAlgo answers a success status with an error message); a lapsed session says so; a position whose symbol is not in the master contract refuses the basket and names it (OpenAlgo leaves it out and understates the margin).
- Mutation: `tools/mutation/tg7-shoonya-margin.json`, 16 mutants, all caught (two needed a rewritten pattern).

## Shoonya history

- Ported from `broker/shoonya/api/data.py` `get_history`: TPSeries for intraday candles and EODChartData for daily ones (an index is asked for by the name Shoonya's daily chart knows it by), in windows sized to what Shoonya answers (1 minute: 5 days, 5 minutes: 20 days, daily: 2 years). The chart calls carry the JSON and the token in one form body (`jData=...&jKey=...`), so the recorder and the test fake read `jData` out of a form as well as out of a text body.
- Candles: sorted, one per timestamp (the last received kept), bars with no price dropped, a bar whose range leaves out its own open or close widened to cover them, a volume below zero set to zero. A bar with no epoch second has its time text read (a time as IST, a day as midnight UTC, as OpenAlgo does). A daily range that includes today gets today's candle from a quote when the history does not hold it; the clock is stopped at the recording for this.
- 26 recorded cases (`history_shoonya.fixtures.json`) plus an integration test of the form body. Deviations: wording of the refusals (a lapsed session is a 403, an unsupported interval a 400 naming the choices, every window failing says Shoonya did not send the candles).
- Mutation: `tools/mutation/tg7-shoonya-history.json`, 26 mutants, all caught (four first needed a recorded case or an integration test; one mutant that looped forever was replaced).

## Zebu history

- Ported from `broker/zebu/api/data.py` `get_history`: one EODChartData or TPSeries call for the whole range (no windows), intraday bars read by their time text (IST), daily bars by their epoch second, and today's candle built from a quote when a daily range includes today and the history does not hold it. 16 recorded cases (`history_zebu.fixtures.json`), the clock stopped at the recording.
- Deviations: OpenAlgo's Zebu plugin passes bars on as received, so a bar whose range leaves out its own open or close, a negative volume or two bars with one timestamp reach the chart (which rejects the whole series, as seen on Shoonya); EP Trade bot sorts, keeps one bar per timestamp, widens and zeroes as it does for Shoonya. When the chart answers with an error or with something that is not a list OpenAlgo fails with an internal error; EP Trade bot says Zebu did not send the candles, and for a daily range still builds today's bar from a quote. An unsupported interval is a 400 naming the choices.
- Mutation: `tools/mutation/tg7-zebu-history.json`, 11 mutants, all caught (one needed a rewritten pattern).

## Flattrade (Noren API at piconnect.flattrade.in)

- Same Noren base class as Shoonya and Zebu (the base now takes the path, the way a call is sent and the books as overridable parts), held to `broker/flattrade`: 40 recorded cases for orders, books and funds (`flattrade.fixtures.json`), 18 for quotes and depth (`flattrade_data.fixtures.json`) and 7 for sign-in (`login_flattrade.fixtures.json`).
- Differences, recorded: the token travels in the body (`jData=<json>&jKey=<token>`), as a form for orders, holdings, funds and quotes and as JSON for the books; the recorder and the test fake read that body whatever type it declares. A read the broker turns away for going over its rate limit is asked again after 2, 4 and 8 seconds (never an order); the pacing windows OpenAlgo keeps in front of every call are not reproduced. An order shows filled and pending quantity and its average price, and a market order the broker reports a price for shows that price; a position shows realised and unrealised profit (from the broker's figures, else last price against average price); trades are rounded to two places; depth carries the number of orders per level and the open interest; holdings carry their average price. Sign-in is at `auth.flattrade.in` and the code is exchanged at `authapi.flattrade.in/trade/apitoken` with the SHA-256 of the app key, the code and the secret.
- Same deviations as Shoonya (refusals carry the broker's reason; an empty book is an empty book; a position whose symbol is not in the master contract is named as still open); and, as for Zebu, a quote for another instrument is refused and asked for again where OpenAlgo takes it.
- Margin (27 recorded cases, `margin_flattrade.fixtures.json`): a basket to GetBasketMargin; a market leg is priced off the last price and a stop-market leg off its trigger (last price only when it has none), moved by the protection slab and rounded to the quote's tick, else the master contract's; with no tick the reference goes unprotected; a leg that cannot be priced refuses the whole basket with the trader-worded message. The total is the margin after the trade, else the total margin. A refusal is a success status in OpenAlgo and an error status here (recorded deviation).
- Mutation sets: 26 mutants on orders, books and funds and 22 on margin, all caught (one margin mutant, protection returning a non-positive price, is dropped as unreachable: the slab never yields zero).
- History (31 recorded cases, `history_flattrade.fixtures.json`): one TPSeries call for intraday bars and one EODChartData call for daily ones; the 09:14 pre-open bar on cash and F&O is left out (commodities keep theirs), bars missing a price are skipped, negative intraday volume is zeroed, a daily bar whose range leaves out its open or close is widened, and today's bar is built from a quote when the daily range includes today and the chart has none. Differences, recorded: OpenAlgo stamps today's bar 5 hours 30 minutes late and fails the whole request over one unreadable row or a chart it cannot reach; EP Trade bot stamps midnight India time, skips the row, builds today's bar from the quote, keeps one bar per timestamp and words errors for the trader (a lapsed session says to log in again). Mutation set: 29 mutants, all caught.
- Not yet ported for Flattrade: the feed.
