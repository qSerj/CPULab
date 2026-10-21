# Parser qualification

Parser cases are constructed in the tests. They are **not captured Windows logs**.
The R23 line shape comes from the CinebenchBatch author's README example:
`CB 6258.00 (0.00)`. The 7-Zip `Tot:` shape is based on its source code.
See [research](../../docs/research.md) for source versions and URLs.

Before enabling real adapters, obtain stdout/stderr, exit behavior, encoding,
locale, exact executable version and hash from a permitted bounded Windows run.
Do not silently expand a parser to accept unrelated GUI status/ranking text.
