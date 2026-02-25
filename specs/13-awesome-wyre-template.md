# Awesome Wyre [![Awesome](https://awesome.re/badge.svg)](https://awesome.re)

> A curated list of awesome modules, root modules, tools, and resources built on the [Wyre Connector](https://wyre.zombidev.me) platform.

Wyre Connector is an open, modular mesh networking platform. This list indexes community-built extensions. All modules listed here are independently maintained — please check each repo's license and README before using.

## Contents

- [Root Modules](#root-modules)
- [Stream Modules](#stream-modules)
- [Files Modules](#files-modules)
- [Mesh Modules](#mesh-modules)
- [Central Server Modules](#central-server-modules)
- [Tools & Utilities](#tools--utilities)
- [SDKs & Libraries](#sdks--libraries)
- [Resources](#resources)

---

## Root Modules

Root modules are full standalone applications built on Wyre Connector. Install via their own installer on top of Wyre Connector.

*No community root modules yet — be the first!*

---

## Stream Modules

Modules that extend Wyre Stream functionality.

*No community stream modules yet.*

---

## Files Modules

Modules that extend Wyre Files functionality.

*No community files modules yet.*

---

## Mesh Modules

Modules that extend Wyre Connector's mesh networking capabilities.

*No community mesh modules yet.*

---

## Central Server Modules

Optional server-side modules for self-hosted Wyre Connector Central instances.

*No community central server modules yet.*

---

## Tools & Utilities

CLIs, scripts, integrations, and developer tools that work with Wyre.

*No community tools yet.*

---

## SDKs & Libraries

Third-party SDKs and helper libraries for building Wyre modules.

*No third-party SDKs yet.*

---

## Resources

- [Wyre Official Site](https://wyre.zombidev.me) — Downloads, documentation
- [Wyre Docs](https://wyre.zombidev.me/docs) — Full technical documentation
- [WyreConnector.Sdk NuGet](https://nuget.org/packages/WyreConnector.Sdk) — Client module SDK
- [WyreConnector.Server.Sdk NuGet](https://nuget.org/packages/WyreConnector.Server.Sdk) — Server module SDK
- [WyreInstaller.Sdk NuGet](https://nuget.org/packages/WyreInstaller.Sdk) — Installer SDK

---

## Contributing

To add your module to this list:

1. Your module must be open source (any OSI-approved license)
2. Must use the official `WyreConnector.Sdk` — no direct references to `wyre-connector` internals
3. Must have a README explaining what it does and how to install it
4. Submit a PR adding your entry in the correct category

Entry format:
```markdown
- [Module Name](https://github.com/you/your-module) — Brief one-line description.
```

Modules may be removed if they become unmaintained, break with current Wyre versions, or are found to be malicious.
