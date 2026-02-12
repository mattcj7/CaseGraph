Fixtures & Test Data Strategy
Principle

Use sanitized fixtures that preserve structure, not sensitive content.

What goes in /fixtures

UFDR structure samples (multiple variants/versions)

ZIP-HTML folder structures (multiple platforms)

PLIST examples (XML + binary)

Geo feeds (CSV/JSON)

“Large case” synthetic stress fixture (no real names/content)

Rules

Do not store real PII, victim names, or sensitive case content.

Prefer generated or heavily anonymized content.

Store fixtures with a short README describing:

what it represents

what parsers it should exercise

expected outputs for golden tests (later)

Golden test philosophy (future tickets)

Same fixture input should produce the same normalized output (deterministic).

Parser changes should update expected outputs deliberately.
