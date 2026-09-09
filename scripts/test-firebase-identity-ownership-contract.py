#!/usr/bin/env python3
"""Offline negative controls for the gateway's subject-bound owner-only identity seam.

Mutations are in-memory strings only; no source checkout, config, keys, or refs
are modified. The full unchanged deployment/configuration gate also runs first.
"""

from __future__ import annotations

import contextlib
import io
from pathlib import Path
import runpy
import unittest


with contextlib.redirect_stdout(io.StringIO()):
    contract = runpy.run_path(str(Path(__file__).with_name("validate-jeeb-firebase-contract.py")))


class FirebaseIdentityOwnershipTests(unittest.TestCase):
    def validate(self, *, startup=None, controller=None, client=None, sources=None):
        contract["validate_chat_identity_ownership"](
            contract["program"] if startup is None else startup,
            contract["identity_controller"] if controller is None else controller,
            contract["identity_client"] if client is None else client,
            contract["identity_sources"] if sources is None else sources,
        )

    def test_real_source_passes(self):
        self.validate()

    def test_authentication_capability_claim_and_response_binding_are_required(self):
        for marker in (
            "[Authorize]", "[RequireCapability(Capabilities.ChatRead)]",
            "!string.Equals(claimUid, userId, StringComparison.Ordinal)",
            "await _identity.MintAsync(userId, ct)",
            "!string.Equals(minted.Uid, userId, StringComparison.Ordinal)",
            "string.IsNullOrWhiteSpace(minted.Token)",
            "minted.ExpiresAt <= DateTime.UtcNow",
            "StatusCodes.Status503ServiceUnavailable", '"private, no-store"',
        ):
            with self.subTest(marker=marker), self.assertRaises(SystemExit):
                self.validate(controller=contract["identity_controller"].replace(marker, "removed-guard"))

    def test_owner_route_success_check_and_di_are_required(self):
        for marker in (
            'PostAsJsonAsync("api/firebase/token", new { uid }, ct)',
            "response.EnsureSuccessStatusCode()", "ReadFromJsonAsync<FirebaseTokenResponse>",
        ):
            with self.subTest(marker=marker), self.assertRaises(SystemExit):
                self.validate(client=contract["identity_client"].replace(marker, "removed-owner-contract"))
        with self.assertRaises(SystemExit):
            self.validate(startup=contract["program"].replace(
                "AddHttpClient<JeebGateway.Chat.Firebase.IChatFirebaseIdentityClient,", "removed-di"))
        with self.assertRaises(SystemExit):
            self.validate(startup=contract["program"].replace(
                'builder.Configuration["ChatServiceApi:BaseUrl"]', 'builder.Configuration["OtherOwner:BaseUrl"]'))

    def test_local_signer_fallback_or_caller_uid_surface_is_rejected(self):
        for forbidden in ("[FromBody]", "[FromQuery]", "_minter.Mint(", "IFirebaseCustomTokenMinter"):
            with self.subTest(forbidden=forbidden), self.assertRaises(SystemExit):
                self.validate(controller=contract["identity_controller"] + "\n" + forbidden)
        with self.assertRaises(SystemExit):
            self.validate(startup=contract["program"] + "\nFirebaseCustomTokenStartupValidator")

    def test_deleted_or_renamed_local_signer_cannot_return(self):
        for name, text in (
            ("FirebaseCustomTokenMinter.cs", "local signer"),
            ("FirebaseCustomTokenOptions.cs", "local key config"),
            ("RenamedFallback.cs", "RSA.Create("),
            ("RenamedFallback.cs", "new SigningCredentials("),
            ("RenamedFallback.cs", "File.ReadAllText("),
        ):
            with self.subTest(name=name, text=text), self.assertRaises(SystemExit):
                self.validate(sources={**contract["identity_sources"], name: text})


if __name__ == "__main__":
    unittest.main()
