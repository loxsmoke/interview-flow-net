# Free macOS updates

The macOS bundle uses Sparkle 2.10.0 for checking, downloading, verifying,
installing, and relaunching updates. Windows uses the existing portable ZIP
packaging and has no Sparkle binaries, packages, or update UI.

No Apple account, subscription, Developer ID certificate, or notarization is
required for this setup. The app remains ad-hoc signed. macOS may require approval
in System Settings > Privacy & Security when opening a downloaded app; this
integration does not disable Gatekeeper or remove quarantine attributes.

## One-time setup (on your Mac)

From the repository root, download the pinned, checksum-verified Sparkle tools:

```bash
sparkle="$(bash tools/macos/get-sparkle.sh)"
"$sparkle/bin/generate_keys" --account interview-flow
"$sparkle/bin/generate_keys" --account interview-flow -p
```

In GitHub repository Settings > Secrets and variables > Actions:

1. Create a **repository variable** named `SPARKLE_PUBLIC_ED_KEY` with the public
   key printed by the last command.
2. Export the private key to a file outside the repository:

   ```bash
   umask 077
   "$sparkle/bin/generate_keys" --account interview-flow -x "$HOME/interview-flow-sparkle.key"
   ```

3. Create a **repository secret** named `SPARKLE_PRIVATE_ED_KEY` with the file's
   text contents. Do not base64-encode it again. Keep a secure backup of this key,
   and remove the temporary export after saving it. Never commit it.

Use the same keys for both architectures and all future releases. Losing or
changing the key will require a manual reinstall for existing users. There are
no Apple credentials involved. The release workflow fails rather than shipping
an updater without a public key or an unsigned update feed enclosure.

## Release behavior

Run the existing Release workflow. Each macOS job embeds Sparkle plus a tiny
Objective-C bridge, builds its usual ZIP, and generates a signed appcast named
`appcast-osx-arm64.xml` or `appcast-osx-x64.xml`. Both feeds and ZIPs are attached
to the GitHub release. The app reads its architecture's feed through GitHub's
`releases/latest/download` URL. Prereleases do not replace the stable feed.
The build checks that both native updater binaries contain the requested
architecture and verifies nested bundle signatures before packaging.

The build-time C# tool in `tools/InterviewFlow.MacPackaging` configures the XML
plist and validates the generated appcast. Bash invokes it through `dotnet run`;
no Python is required for updates. Its tests run with the normal `dotnet test
InterviewFlow.slnx` suite on both Windows and macOS. The tool is not shipped in
the app or uploaded to GitHub Releases.

Updates are checked automatically; installation requires user interaction.
**About > Check for updates…** opens Sparkle's progress and installation dialog.
The initial version containing Sparkle needs to be installed manually once.
The native bridge is initialized after the main window opens. If it is missing
or fails to start, the app still runs and About explains the failure.

Local `publish-macos.sh` builds without `SPARKLE_PUBLIC_ED_KEY` remain ordinary
bundles with updating disabled. To build with the updater locally:

```bash
export SPARKLE_PUBLIC_ED_KEY="$("$sparkle/bin/generate_keys" --account interview-flow -p)"
bash tools/publish-macos.sh osx-arm64 # use osx-x64 for an Intel build
```

## Test on a Mac

1. Configure the keys above, then publish a stable release A with this code.
2. Extract its ZIP and install `Interview Flow.app` into `~/Applications`.
   Launch it, granting macOS approval if necessary.
3. Open About. The update button should be enabled. Check for updates; it should
   report that A is current. Check the diagnostic log and macOS Console for
   `Interview Flow updater` or `Sparkle` if it fails.
4. Add some test data and record its location and the settings file location.
   Both must be outside the `.app`, because Sparkle replaces the bundle.
5. Publish a higher stable release B with the same signing key. Keep A installed.
6. In A, select About > Check for updates, download B, and choose Install and
   Relaunch. Finish active agent runs before restarting.
7. Confirm the About version is B and the settings and test data still exist.
8. Check again: there should be no update. Repeat on Intel when possible, and
   test cancelling a download and retrying after a network failure.

The current checkout can be built and tested on Windows, but compiling the
Objective-C bridge, verifying ad-hoc signing, and exercising installation and
relaunch require macOS. In particular, first-launch Gatekeeper behavior and
Avalonia's interaction with Sparkle's restart must be verified on the target Mac.

## References

- https://sparkle-project.org/documentation/programmatic-setup/
- https://sparkle-project.org/documentation/
- https://sparkle-project.org/documentation/publishing/
