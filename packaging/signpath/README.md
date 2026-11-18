# Code signing with SignPath Foundation

Maroom releases can be signed for free through the [SignPath Foundation](https://signpath.org)
open-source program. The certificate is issued to SignPath Foundation, so Windows shows
**"SignPath Foundation"** as the publisher.

Until the steps below are done, `release.yml` skips signing and publishes unsigned builds as before.

## One-time setup

1. Apply at <https://signpath.org/apply> with this repository. Requirements include an OSI license
   (MIT), builds only from GitHub Actions, and 2FA for everyone with access.
2. After approval, in SignPath:
   - Project slug: `maroom` (or change `SIGNPATH_PROJECT` in `.github/workflows/release.yml`).
   - Trusted build system: link the predefined **GitHub.com** connector to the project.
   - Default artifact configuration: paste [`artifact-configuration.xml`](artifact-configuration.xml).
   - Signing policy `release-signing` (or change `SIGNPATH_POLICY`), with yourself as approver.
   - Create a CI user, add it as submitter to the policy, and create an API token for it.
3. In GitHub: Settings > Secrets and variables > Actions:
   - Variable `SIGNPATH_ORGANIZATION_ID` = your SignPath organization ID
   - Secret `SIGNPATH_API_TOKEN` = the CI user's API token

## What a release does

1. Builds `Maroom.exe` and checks the tag against its version.
2. Submits `Maroom.exe` to SignPath and waits (approve the request on signpath.io).
3. Rebuilds `Maroom-Setup.exe` around the signed EXE (`build.ps1 -NoInstall -SetupOnly`).
4. Submits `Maroom-Setup.exe` and waits for the second approval.
5. Checks both signatures with `Get-AuthenticodeSignature`, then writes the checksum and publishes.

After a signed release, update `InstallerSha256` in the winget manifest: the hash changes with the signature.
