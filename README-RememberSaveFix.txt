WTTG2 Remember Save Fix 1.0.1
For the installed Epic Remember Networks 1.0.0 / BepInEx 5.4.23.5 / Unity 2017.3.

What failed
Epic Remember Networks keeps successful Wi-Fi credentials under a private GUID.
It changes that GUID whenever DataManager.ClearGameData() runs. The GUID is not
part of the game's DataLookUp or Epic SaveGame slot. Loading a previous world
therefore can leave Remember Networks looking under a different GUID while the
correct records remain in remembered-networks.state. The user's existing log
contains this exact sequence: credential commit, ClearGameData identity rotation,
then a stage load with the new identity.

What this add-on does
It gives the loaded world a stable identity derived only from the sorted set of
secure BSSID/security pairs. The game randomly creates those BSSIDs for a new
world and stores/restores them in WifiNetworkData. A different world therefore
has a different identity; reloading the same world's snapshot keeps it.
Previously remembered records can be recovered only when their BSSID and
security match the loaded world. Conflicting old passwords are skipped. Records
from global mode are excluded. No hidden game networkPassword field is read.
The original Epic Remember Networks continues handling password capture, masking,
manual Connect, success checking, icons, and its atomic state writer/backups.

World staging can precede Wi-Fi initialization. The add-on waits for all secure
network identities, retries every 0.25 seconds for up to 90 seconds, and also
checks immediately before Remember Networks handles a row/password dialog.
Original game methods are never skipped, replaced, or called to authenticate.
Recovery only starts after the legacy stage-loaded notification, so rows built
while another hotspot is still staging cannot cache a mixed-world identity.
The completed identity is cached until stage reload, clear, or teardown.

If vanilla authentication rejects a migrated password, exact old copies with
the same BSSID/security/password are forgotten too. Otherwise those copies
could restore the rejected password again after opening a dialog or restarting.
Global records, unrelated networks and records with different passwords remain.

Installation
1. Close the game.
2. Keep Epic_RememberNetworks.dll installed.
3. Back up BepInEx/config/Epic_RememberNetworks/ (contains saved credentials).
4. Copy WTTG2RememberSaveFix.dll into BepInEx/plugins/.
5. Start the game and load/continue your save. Open the Wi-Fi list as usual.
   The add-on never connects automatically. Press Connect yourself.

Configuration
BepInEx/config/local.wttg2.remember-save-fix.cfg is created on first startup.
EnableScopeHook=false disables this add-on after restarting.
RecoverPreviouslyRememberedNetworks=false disables legacy-record recovery;
stable identities still work for connections captured after installation.
The existing Epic Remember Networks Enabled, RememberAcrossSessions, and
SeparateCredentialsBySave settings remain in effect.

Rollback
Close the game and remove only WTTG2RememberSaveFix.dll. Restore the saved
Epic_RememberNetworks config directory if exact previous credential state is
needed. No original plugin, game assembly, or game save is replaced by this DLL.
NoSaveDelete and Remote VPN Green Fix are not modified by this add-on.

Build and checks
Run build.ps1 -GamePath '<game folder>' -MonoPath '<Mono folder>' -Test.
Build outputs and synthetic test-state files go into ignored bin/ by default;
use -OutputPath to choose another folder. Install only WTTG2RememberSaveFix.dll.
The build deliberately targets CLR 2.0 plus System.Core 3.5. Using the Mono
compiler's unavailable -sdk:3.5 on this machine silently selected CLR 4; do not
substitute it for -sdk:2.
Thirty-nine tests pass against the installed Epic record type and state writer/parser,
using synthetic credentials only. They include isolation between different
BSSIDs, ambiguous-record rejection, not inventing unknown credentials,
idempotent migration, correct SaveId serialization, atomic backup creation and
verified-rejection cleanup that persists through the real state writer/parser.
Actual Unity runtime/load/autofill results are recorded separately by the main QA
run. Compilation and these tests alone are not proof of live keyboard/UI behavior.

Source
WTTG2RememberSaveFix.cs, RememberScopePolicy.cs, RememberFixTests.cs, build.ps1.
The add-on uses reflection only against the installed legacy plugin's verified
fields/methods. Unsupported future legacy versions fail closed and log an
exception type without input, password text, password length, or BSSID values.
