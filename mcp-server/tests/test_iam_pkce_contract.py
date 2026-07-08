"""Static discovery-contract for ADR-IC-021 C2 — IAM_OAUTH21_PKCE_ENFORCED.

Plain English: OAuth 2.1 requires PKCE with the strong ``S256`` challenge method and forbids the weak
``plain`` method (a downgrade a MITM could force). This test locks that as a CI-enforced contract against
a committed snapshot of Logto's live ``/.well-known/openid-configuration``: if Logto is ever reconfigured
to advertise ``plain`` (or drop ``S256``), the test fails and the drift must be acknowledged in the same
change. It is the honest, hermetic (no live Logto) half of C2 that CAN run in CI — the full "an
authorization-code request WITHOUT a code_challenge is refused" enforcement needs an interactive flow
against live Logto and stays Planned (documented empirically, bd babelstone-zla1.10.5).
"""

import json
from pathlib import Path

_GOLDEN = Path(__file__).parent / "fixtures" / "logto-openid-configuration.golden.json"


def _discovery() -> dict:
    return json.loads(_GOLDEN.read_text())


def test_IAM_OAUTH21_PKCE_ENFORCED_s256_is_the_only_code_challenge_method() -> None:
    # Realises catalogue Test ID IAM_OAUTH21_PKCE_ENFORCED (ADR-IC-021 C2): the AS advertises S256 and
    # ONLY S256 — the weak `plain` method (a rejectable downgrade) is absent. OAuth 2.1 / RFC 7636.
    methods = _discovery()["code_challenge_methods_supported"]
    assert methods == ["S256"], f"expected S256-only PKCE, got {methods!r}"
    assert "plain" not in methods


def test_IAM_OAUTH21_PKCE_ENFORCED_code_flow_is_offered_for_pkce() -> None:
    # PKCE rides the authorization_code grant; assert the AS offers it (tokens are obtained via the code
    # flow, not an implicit/query-string leak — the no-token-in-query posture, C2's second half).
    assert "authorization_code" in _discovery()["grant_types_supported"]
