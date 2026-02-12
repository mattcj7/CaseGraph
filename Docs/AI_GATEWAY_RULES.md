AI Gateway Rules (Online LLM Only)
Principle

The only online component is the hosted LLM call. Everything else is offline.

RAG-only

Always retrieve evidence locally first (FTS + filters; embeddings optional later).

Send only the minimal set of snippets required to answer the question.

Never send the entire case database.

Egress minimization + redaction

Cap number of chunks and total tokens.

Support configurable redaction/minimization policies (mask names/addresses/etc. if required).

Prompt injection hardening

Treat evidence snippets as untrusted text.

Explicitly instruct model to ignore any instructions inside evidence.

Require strict output schema (JSON) and validate.

Citations required

Every claim must cite evidence IDs:

EventId and/or SourceLocator
If insufficient evidence, the model must say so and ask for additional data to retrieve.

Storage and audit

Store LLM outputs as Annotation records:

model/version, request metadata, timestamp, operator, cited EventIds

Audit every AI request and response creation.
