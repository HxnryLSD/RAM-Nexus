#!/usr/bin/env bash
# Build the offline installer + portable ZIP for Roblox Account Manager.
#
#   ./scripts/build-installer.sh
#
# Produces, in artifacts/:
#   RobloxAccountManager-Setup.exe       single-file offline installer (self-contained)
#   RobloxAccountManager-portable.zip    portable copy (deflate — cheap to extract)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="$ROOT/artifacts"

# --- Code signing ------------------------------------------------------------
# By default the binaries get an Authenticode signature from a self-signed
# code-signing cert this script creates once and reuses (CurrentUser\My,
# CN=Roblox Account Manager (Dev)). That gives an intact, verifiable
# signature — but it does NOT clear SmartScreen: only a real CA-issued
# code-signing cert does. To use one, set CERT_PFX and CERT_PASSWORD.
SIGNTOOL="$(ls -d "/c/Program Files (x86)/Windows Kits/10/bin/"*/x64/signtool.exe 2>/dev/null | sort -V | tail -1 || true)"
SIGN_ARGS=()
if [ -n "${SIGNTOOL:-}" ] && [ -n "${CERT_PFX:-}" ]; then
  SIGN_ARGS=("$SIGNTOOL" sign /f "$CERT_PFX" /p "${CERT_PASSWORD:-}" /fd SHA256)
elif [ -n "${SIGNTOOL:-}" ]; then
  TP="$(powershell -NoProfile -Command "Get-ChildItem Cert:\CurrentUser\My | Where-Object { \$_.Subject -eq 'CN=Roblox Account Manager (Dev)' -and \$_.HasPrivateKey } | Select-Object -First 1 -ExpandProperty Thumbprint")"
  if [ -z "$TP" ]; then
    echo "==> Creating self-signed code-signing cert (CN=Roblox Account Manager (Dev))"
    TP="$(powershell -NoProfile -Command "\$c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Roblox Account Manager (Dev)' -FriendlyName 'Roblox Account Manager (Dev)' -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3); \$c.Thumbprint")"
  fi
  SIGN_ARGS=("$SIGNTOOL" sign /s my /sha1 "$TP" /fd SHA256)
fi

sign_file() {
  [ -n "${SIGN_ARGS:-}" ] || { echo "    (signtool unavailable — skipping signing)"; return; }
  local f="$1"
  # signtool needs Windows paths; cygpath converts the /c/... form. The
  # MSYS_NO_PATHCONV prefix (this command only) stops Git-Bash from mangling
  # the /switches; a global export would break dotnet's own path handling.
  command -v cygpath >/dev/null 2>&1 && f="$(cygpath -w "$f")"
  if MSYS_NO_PATHCONV=1 "${SIGN_ARGS[@]}" /tr http://timestamp.digicert.com /td SHA256 "$f"; then
    echo "    signed: $(basename "$f")"
  elif MSYS_NO_PATHCONV=1 "${SIGN_ARGS[@]}" /tr http://timestamp.sectigo.com /td SHA256 "$f"; then
    echo "    signed: $(basename "$f") (sectigo timestamp)"
  elif MSYS_NO_PATHCONV=1 "${SIGN_ARGS[@]}" "$f"; then
    echo "    signed (no timestamp — signature expires with the cert): $(basename "$f")"
  else
    echo "    !! signing FAILED: $(basename "$f")"
  fi
}

rm -rf "$ART"
mkdir -p "$ART"

echo "==> [1/4] Publishing app (Release, self-contained, win-x64)"
PUBLISH_ARGS=(-c Release -p:Platform=x64 -r win-x64 --self-contained)
# UPDATE_MANIFEST_URL (set by the release workflow) bakes the fork's releases API URL into
# the build so UpdateService.DefaultManifestUrl points at the repo the artifacts are
# released from; local builds fall back to the upstream default.
if [ -n "${UPDATE_MANIFEST_URL:-}" ]; then
  PUBLISH_ARGS+=(-p:UpdateManifestUrl="$UPDATE_MANIFEST_URL")
fi
dotnet publish "$ROOT/src/RAM.App/RAM.App.csproj" "${PUBLISH_ARGS[@]}" -o "$ART/app-publish" --nologo -v q
# dotnet publish omits the compiled XAML (a known WinUI 3 publish gap): App.xbf,
# MainWindow.xbf, the Views/ and Dialogs/ XBF folders, and the .pri resource index.
# Without them the app dies at MainWindow.InitializeComponent with 0x802B000A, so copy
# them over from the build output that the publish just produced.
SRC_BIN="$ROOT/src/RAM.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64"
cp "$SRC_BIN/App.xbf" "$SRC_BIN/MainWindow.xbf" "$SRC_BIN/Roblox Account Manager.pri" "$ART/app-publish/"
cp -r "$SRC_BIN/Views" "$SRC_BIN/Dialogs" "$ART/app-publish/"

# Signed before packing so the installed copy and the portable ZIP share it.
echo "==> Signing app executable"
sign_file "$ART/app-publish/Roblox Account Manager.exe"

echo "==> [2/5] Packing payload (Brotli q11 — fast, low-memory decompression at install time)"
dotnet run --project "$ROOT/src/RAM.Installer/RAM.Installer.csproj" -c Release -- --pack "$ART/app-publish" "$ART/payload.br"

echo "==> [3/5] Building single-file installer"
dotnet publish "$ROOT/src/RAM.Installer/RAM.Installer.csproj" -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages=en \
  -p:PayloadPath="$ART/payload.br" -o "$ART/installer" --nologo -v q
cp "$ART/installer/RAM.Installer.exe" "$ART/RobloxAccountManager-Setup.exe"

echo "==> Signing installer"
sign_file "$ART/RobloxAccountManager-Setup.exe"

echo "==> [4/5] Building portable ZIP"
PORTABLE_DIR="$ART/Roblox Account Manager"
mv "$ART/app-publish" "$PORTABLE_DIR"
# Drop the same unused WindowsAppSDK ML/AI files the installer payload skips.
dotnet run --project "$ROOT/src/RAM.Installer/RAM.Installer.csproj" -c Release -- --trim "$PORTABLE_DIR" >/dev/null
cat > "$PORTABLE_DIR/README.txt" <<'EOF'
Roblox Account Manager — portable edition
=========================================
No installation needed: unzip anywhere and run "Roblox Account Manager.exe".
The app stores all its data (accounts, settings, logs, RDD installs) under
%LOCALAPPDATA%\Roblox Account Manager, so the folder can be moved or deleted
freely without losing anything. To remove a portable copy, just delete it.

Note: the app requests administrator rights at launch (UAC) because it may
need to write fast flags into a Roblox install under Program Files.

Need a normal install instead? Use RobloxAccountManager-Setup.exe.
EOF
if command -v zip >/dev/null 2>&1; then
  (cd "$ART" && zip -r -9 "RobloxAccountManager-portable.zip" "Roblox Account Manager" >/dev/null)
else
  # Git Bash paths (/c/...) are meaningless to PowerShell — convert to a Windows path.
  WIN_ART="$(cygpath -w "$ART" 2>/dev/null || echo "$ART")"
  powershell -NoProfile -Command "Compress-Archive -Path '$WIN_ART\\Roblox Account Manager' -DestinationPath '$WIN_ART\\RobloxAccountManager-portable.zip' -CompressionLevel Optimal -Force"
fi

echo "==> [5/5] Building update zip (root-level layout for in-app updates)"
# In-app updates (About page) download this zip and swap it into the app folder, so its
# entries must sit at the root (the portable zip wraps everything in a folder).
if command -v zip >/dev/null 2>&1; then
  (cd "$PORTABLE_DIR" && zip -r -9 "$ART/RobloxAccountManager-update.zip" . >/dev/null)
else
  WIN_DIR="$(cygpath -w "$PORTABLE_DIR" 2>/dev/null || echo "$PORTABLE_DIR")"
  powershell -NoProfile -Command "Compress-Archive -Path '$WIN_DIR\\*' -DestinationPath '$WIN_ART\\RobloxAccountManager-update.zip' -CompressionLevel Optimal -Force"
fi

echo
echo "Done:"
ls -lh "$ART/RobloxAccountManager-Setup.exe" "$ART/RobloxAccountManager-portable.zip" "$ART/RobloxAccountManager-update.zip"
