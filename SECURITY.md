# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Use GitHub's private reporting
instead: open the repository's **Security** tab and choose **Report a vulnerability**. We aim to
acknowledge a report within a few days and to tell you what we plan to do about it.

Useful things to include: the add-in version, your Revit version, what you did, what you expected,
and what happened. If the problem involves a plugin, name it.

## Supported versions

Only the [latest release](https://github.com/loadingcloud001/LoadingAI-Revit/releases/latest) gets
fixes.

## What is in scope

- The add-in and its plugins, including the plugin load allowlist and the trust prompt.
- The confirmation prompts that guard model edits (L2 and L3 tools).
- How API keys are stored (`%AppData%\LoadingAI\settings.json`, DPAPI).
- The free-tier gateway in [`gateway/`](gateway/README.md).

## Things that are by design

- The `X-LoadingAI-Client` value in this repository is **not a secret**. It only filters casual
  traffic; the quotas are the real limit.
- The built-in free tier's daily allowance is per device and can be reset by a determined user. It
  is a courtesy limit, not licensing.
- The plugin allowlist stops accidents and third-party drop-ins. It does not defend against software
  already running as your Windows user.
- Whichever model provider you choose receives your chat messages and the results of the tools it
  calls. See *What leaves your computer* in the [README](README.md#models-keys-and-privacy).

If you find that a real provider key has been committed anywhere in this repository's history,
please report it privately so it can be rotated.
