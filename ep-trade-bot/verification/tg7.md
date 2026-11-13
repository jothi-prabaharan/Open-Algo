# TG7: remaining brokers, verification status

This file records, per broker, what is ported and how it was checked. It is the Gate 7 artifact in progress; the gate closes when every broker in the task list has a row and every row says what is and is not covered.

| Broker | Orders and books | Funds | Quotes and depth | History | Margin | GTT | Login | Feed |
|---|---|---|---|---|---|---|---|---|
| Dhan | yes | yes | yes | yes | yes | no | yes | no |
| Dhan Sandbox | yes | yes | yes | yes | no | no | no | no |
| Upstox | yes | yes | yes | yes | yes | no | yes | no |
| Fyers | yes | yes | yes | yes | yes | no | yes | no |
| Delta Exchange | yes | yes | yes | yes | yes | no | yes | no |

The other 30 brokers in the task list are not started.

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
