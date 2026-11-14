"""/margin on HDFC Sky: one calculator for any number of legs (POST /oapi/v1/margin); a derivative leg also carries its underlying's last price, read with one /fetch-ltp call."""

import scenarios_hdfcsky as s
from scenarios_hdfcsky import AUTH_TOKEN, B, ENV, SEED_API_KEY_ROW, SYMBOLS, ltp  # noqa: F401

BROKER = "hdfcsky"
READ_POSTS = ["/oapi/v1/margin", "/oapi/v1/fetch-ltp"]
MEP, LB = f"POST {B}/margin", f"PUT {B}/fetch-ltp"


def result(combined=None, individual=None):
    return {"body": {"status": "success", "result": {"combined_margin": combined or {}, "individual_margin_values": individual or []}}}


COMBINED = result({"span": 100000.456, "exposure_margin": 20000.25, "premium_margin": 500.5, "var_margin": 10, "extreme_loss_margin": 5, "delivery_margin": 1, "additional_margin": 2.5, "span_spread_margin": 3, "somtier_margin": 4, "premium_benefit": 30.25})
SINGLE = result({"span": 1000.5, "exposure_margin": 200.25})
INDIVIDUAL = result({}, [{"span": 1000.5, "exposure_margin": 100.25, "premium_benefit": 5}, {"span": 2000.5, "exposure_margin": 300.75, "premium_margin": 10}])
ZERO = result({}, [])
BENEFIT_ONLY = result({"span": 10, "premium_benefit": 50}, [{"span": 700, "exposure_margin": 70}])
TEXT_FIGURES = result({"span": "1000.5", "exposure_margin": "x", "premium_margin": None})
ERROR_ENVELOPE = {"body": {"status": "error", "message": "Rejected"}}
ERROR_BLOCK = {"body": {"error": {"code": 1001, "message": "Unable to read request Data"}}}
ERROR_BLOCK_WITHOUT_MESSAGE = {"body": {"error": {"code": 1001}}}
ERROR_BAD_STATUS = {"status": 400, "body": {"status": "error", "message": "Invalid token"}}
UNDERLYING_NIFTY = ltp((26000, "NSE_INDEX", 23767.45, 23700.0))
UNDERLYING_ALL = ltp((26000, "NSE_INDEX", 23767.5, 23700.0), (2885, "NSE", 2901.5, 2890.0), (26001, "BSE_INDEX", 79050.49, 78980.0))


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


FUT = pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML", price="24005")
SCENARIOS = [
    margin("margin_one_equity_position", [pos(price="805.5")], {MEP: SINGLE}),
    margin("margin_without_a_price_sends_zero", [pos()], {MEP: SINGLE}),
    margin("margin_cnc_equity", [pos(product="CNC", price="805")], {MEP: SINGLE}),
    margin("margin_an_unknown_product_is_intraday", [pos(product="XYZ")], {MEP: SINGLE}),
    margin("margin_a_future_reads_its_underlying", [FUT], {LB: UNDERLYING_NIFTY, MEP: COMBINED}),
    margin("margin_the_underlying_price_is_rounded_to_a_whole_number", [FUT], {LB: ltp((26000, "NSE_INDEX", 23767.5, 23700.0)), MEP: SINGLE}),
    margin("margin_an_option", [pos("NIFTY25NOV2624000CE", "NFO", "BUY", "75", "NRML", price="7.35")], {LB: UNDERLYING_NIFTY, MEP: SINGLE}),
    margin("margin_a_stock_future_reads_its_cash_underlying", [pos("RELIANCE25NOV26FUT", "NFO", "SELL", "250", "NRML", price="2900")], {LB: ltp((2885, "NSE", 2901.5, 2890.0)), MEP: SINGLE}),
    margin("margin_a_bse_derivative", [pos("SENSEX26NOVFUT", "BFO", "BUY", "20", "NRML", price="80000")], {LB: ltp((26001, "BSE_INDEX", 79050.49, 78980.0)), MEP: SINGLE}),
    margin("margin_a_commodity_has_no_underlying_read", [pos("GOLDM25NOV26FUT", "MCX", "BUY", "10", "NRML", price="75000")], {MEP: SINGLE}),
    margin("margin_a_currency_has_no_underlying_read", [pos("USDINR25NOV26FUT", "CDS", "BUY", "1000", "NRML", price="83.5")], {MEP: SINGLE}),
    margin("margin_an_index_has_no_underlying_read", [pos("NIFTY", "NSE_INDEX", "BUY", "1", "MIS")], {MEP: SINGLE}),
    margin("margin_several_legs_read_each_underlying_once", [FUT, pos("NIFTY25NOV2624000CE", "NFO", "BUY", "75", "NRML", price="7.35"), pos("RELIANCE25NOV26FUT", "NFO", "SELL", "250", "NRML", price="2900"), pos("SENSEX26NOVFUT", "BFO", "BUY", "20", "NRML", price="80000"), pos(price="805")],
           {LB: UNDERLYING_ALL, MEP: COMBINED}),
    margin("margin_the_underlying_price_is_unavailable", [FUT], {LB: {"status": 500, "body": {"x": 1}}, MEP: SINGLE}),
    margin("margin_the_underlying_read_is_rate_limited_then_answered", [FUT], {LB: [{"status": 429, "body": {}}, UNDERLYING_NIFTY], MEP: SINGLE}),
    margin("margin_the_combined_figure_is_used", [pos(price="805"), pos("INFY", price="1500")], {MEP: COMBINED}),
    margin("margin_without_a_combined_figure_the_legs_are_summed", [pos(price="805"), pos("INFY", price="1500")], {MEP: INDIVIDUAL}),
    margin("margin_a_benefit_larger_than_the_margin_gives_zero_and_the_legs_are_summed", [pos(price="805")], {MEP: BENEFIT_ONLY}),
    margin("margin_nothing_at_all", [pos(price="805")], {MEP: ZERO}),
    margin("margin_text_figures", [pos(price="805")], {MEP: TEXT_FIGURES}),
    margin("margin_answer_without_a_result", [pos(price="805")], {MEP: {"body": {"status": "success"}}}),
    margin("margin_refused_with_an_error_envelope", [pos(price="805")], {MEP: ERROR_ENVELOPE}),
    margin("margin_refused_with_an_error_block", [pos(price="805")], {MEP: ERROR_BLOCK}),
    margin("margin_refused_with_an_error_block_without_a_message", [pos(price="805")], {MEP: ERROR_BLOCK_WITHOUT_MESSAGE}),
    margin("margin_refused_with_an_error_status", [pos(price="805")], {MEP: ERROR_BAD_STATUS}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos(price="805")], {MEP: SINGLE}),
    margin("margin_no_positions", [], {}),
    {"name": "margin_invalid_apikey", "path": "/api/v1/margin", "auth": "none", "request": {"apikey": "not-a-key", "positions": [pos()]}, "broker": {}},
]
