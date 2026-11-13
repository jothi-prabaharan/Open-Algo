# TG7: remaining brokers, verification status

This file records, per broker, what is ported and how it was checked. It is the Gate 7 artifact in progress; the gate closes when every broker in the task list has a row and every row says what is and is not covered.

| Broker | Orders and books | Funds | Quotes and depth | History | Margin | GTT | Login | Feed |
|---|---|---|---|---|---|---|---|---|
| Dhan | yes | yes | yes | yes | yes | yes | yes | no |
| Dhan Sandbox | yes | yes | yes | yes | yes | no | no | no |
| Upstox | yes | yes | yes | yes | yes | yes | yes | no |
| Fyers | yes | yes | yes | yes | yes | yes | yes | no |
| Delta Exchange | yes | yes | yes | yes | yes | no | yes | yes |

The other 30 brokers in the task list are not started.

## Not done, and why Gate 7 is open

- **Market-data feeds** for Dhan, Upstox and Fyers. What the Upstox one needs beyond the codec itself: subscribe messages that are JSON sent as binary frames (the feed contract carries text only), a stream address fetched from Upstox with the token before connecting (the contract builds it without a call), a protobuf reader for its `FeedResponse` messages, and the per-mode key limits of its adapter. Each is a separate wire protocol (Dhan binary, Upstox protobuf, Fyers HSM binary) and needs its own codec held to the Python adapter, as the Zerodha, Angel One, Paytm Money and Delta Exchange feeds were.
- **Delta Exchange fractional (spot) quantities.** The `/api/v1` request already accepts a fractional quantity on a crypto exchange, but the order intent, the guards, the risk checks, the sandbox and the broker order all carry a whole number, so it needs a decision on how quantity is represented from the request to the broker, not a change in the plugin.
- **GTT for Delta Exchange** has no OpenAlgo counterpart (its Python plugin has no GTT module), so there is nothing to port.
- **The 30 brokers not started** (fivepaisa, fivepaisaxts, aliceblue, arrow, compositedge, definedge, firstock, flattrade, groww, hdfcsecurities, hdfcsky, ibulls, iifl, iiflcapital, indmoney, jainamxts, kotak, motilal, mstock, nubra, pocketful, rmoney, rupeezy, sahi, samco, shoonya, tradejini, tradesmart, wisdom, zebu). Each needs the full recipe: orders and books, funds, quotes and depth, history and margin where the broker has them, GTT where it has it, login, feed, with recorded fixtures and mutation checks.

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
