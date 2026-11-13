# TG7: remaining brokers, verification status

This file records, per broker, what is ported and how it was checked. It is the Gate 7 artifact in progress; the gate closes when every broker in the task list has a row and every row says what is and is not covered.

| Broker | Orders and books | Funds | Quotes and depth | History | Margin | GTT | Login | Feed |
|---|---|---|---|---|---|---|---|---|
| Dhan | yes | yes | yes | yes | yes | no | no | no |
| Dhan Sandbox | yes | yes | yes | yes | no | no | no | no |
| Upstox | yes | yes | yes | yes | yes | no | yes | no |
| Fyers | yes | yes | yes | yes | yes | no | no | no |
| Delta Exchange | yes | yes | yes | yes | yes | no | no | no |

The other 30 brokers in the task list are not started.

## Upstox sign-in

- Ported from `broker/upstox/api/auth_api.py` and the address `utils/broker_login.py` builds: the trader signs in on Upstox's page, which sends back a `code`; the code is exchanged at `/v2/login/authorization/token` with the same redirect address. There is no separate feed token.
- The redirect address is a server setting, `Brokers:Keys:upstox:RedirectUrl`, next to `ApiKey` and `ApiSecret`. Without all three the sign-in answers 409 and says which are missing; the start call answers no address.
- `/broker-login/complete` now accepts `code` as well as `request_token`.
- Contract: five recorded cases (`tools/contract-fixtures/login_upstox.fixtures.json`, recorded with `record.py login_upstox`) check the form sent to Upstox field by field and the token stored. The words of a refusal are EP Trade bot's own, as for the other brokers.
- Integration: one test starts a sign-in, checks the address (key, redirect address and state) and completes it with the issued state.
- Mutation: `tools/mutation/tg7-upstox-login.json`, 8 mutants, all caught. The first run left one alive, an empty `access_token` taken as a token; the recorded case `login_empty_access_token` now covers it.
