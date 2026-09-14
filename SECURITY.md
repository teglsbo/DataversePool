# Security Policy

## Supported versions

Pre-1.0: only the latest published preview/release is supported. There is no LTS branch yet.

## Reporting a vulnerability

Please do **not** open a public issue for security vulnerabilities. Instead, use GitHub's
["Report a vulnerability"](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)
private advisory feature on this repository, or email the maintainers directly if that's
unavailable.

Please include:
- A description of the vulnerability and its potential impact.
- Steps to reproduce (a minimal repro project/snippet helps a lot).
- Affected version(s)/commit.

We aim to acknowledge reports within 5 business days.

## Scope notes specific to this library

DataversePool pools `ServiceClient` instances and their authenticated connections/tokens. Of particular
security interest:
- Any bug that could cause a `CallerId`/token/credential to leak across a pooled resource handed
  to a different logical caller (see ADR-0003 for the current isolation contract and its known
  limits).
- Any bug that could cause connection strings, secrets, or tokens to be logged.
