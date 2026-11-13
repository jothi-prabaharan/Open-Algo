"""Zebu quotes, depth and several quotes: the Shoonya cases against Zebu's own plugin."""

import importlib

import scenarios_shoonya_data as _d
from scenarios_shoonya_data import SCENARIOS, SYMBOLS, patch_clients, AUTH_TOKEN, READ_POSTS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "zebu"
ENV = {"BROKER_API_KEY": "FA12345:::zebu-client-id", "BROKER_API_SECRET": "zebu-secret"}


def patch():
    module = importlib.import_module("broker.zebu.api.data")
    if hasattr(module, "USE_ASYNC"):
        module.USE_ASYNC = False
