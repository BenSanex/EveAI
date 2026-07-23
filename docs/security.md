# Security and privacy

- ESI is read-only: the transport permits only GET and there is no raw route.
- OAuth uses PKCE and a cryptographically random state value.
- Tokens are stored in Secret Service and are never CLI options.
- Errors are redacted before serialization.
- Audio uses a private temporary directory and is deleted after transcription.
- Eva changes the GNOME shortcut only through an explicit Settings action and
  refuses to replace an existing binding.
- The Codex runtime workspace contains instructions, not secrets.

JWT claims are checked for issuer, audience, expiry, character identity, and
required scopes. Production SSO must also validate the signature from EVE's
published signing keys.
