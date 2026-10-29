# Secure maintenance namespace

The production capability is obtained only from `SecureMaintenanceRoot.CreateFresh`
or `OpenExisting(namespaceId)`. Both require SYSTEM, choose the default OS-known
x64 Program Files directory themselves, and inspect/retain its local non-reparse
ancestor chain. Neither accepts a peer-supplied path or an `isTrusted` boolean.
Runtime maintenance data is outside System32. The namespace is deliberately small
and installer-owned; it is not a location for ordinary user documents or media.

The security premise is the v0.6 trusted OS/installer boundary. Administrator or
kernel compromise, and an administrator previously weakening the installation
anchor, are outside the claim. This is a named supported-installation premise,
not a claim that current ACLs reveal all historical handles. If that premise is
unacceptable on a particular installation, do not issue this capability.

The directory name is fixed: `DeskFuse-Maintenance` under that OS-known anchor.
Its atomic creation is a machine-wide durable bootstrap reservation. Its random
namespace GUID is stored only in the private marker; it never selects another
directory or another admission registry. A second fresh bootstrap always fails,
including after a crash before marker completion. Subsequent test runs and
version upgrades reopen the same namespace and preserve the same global run
registry. An unresolved old run cannot be bypassed by choosing a fresh suffix.

SYSTEM ownership and the
protected SYSTEM/Administrators DACL are supplied to the creation APIs. The volume
must support persistent ACLs before creation is attempted. An ordinary caller
cannot obtain an old write, metadata or security handle to an object that did not
exist and was private on creation. Ordinary users may not mutate its protected
descendants. Trusted code must preserve this invariant: create or byte-copy files
under the protected namespace, never move in a previously exposed directory,
change the DACL to grant ordinary writers, or pass its privileged handles to them.

The protected bootstrap record binds the namespace GUID to the volume and 128-bit
directory file identifier. Reopen uses the same fixed directory and checks the
requested GUID against its marker; it rejects a missing, noncanonical or
mismatched record. Reparse points,
unsafe owners/permissions and alternate file streams are rejected. Held directory
handles omit delete sharing, and protected read streams retain the inspected
parent chain through disposal. The stream name is the checked absolute path so
Windows SetupAPI and catalog verification can resolve the same held identity.

File sharing is not the whole security argument: it does not cover all metadata
access or other NTFS streams. The private-at-creation ACL and trusted lifecycle
close those gaps. A failed create is left as an incomplete namespace; it is never
adopted, retried as another run, or automatically deleted. Existing arbitrary
paths still have only the non-authorizing inspection API.

Source/fixture tests do not qualify SYSTEM execution, restrictive ACL behavior,
file-system filter behavior or live driver recovery on a customer machine. Those
remain explicit platform qualification steps before any installation entrypoint
is enabled.

Microsoft API contracts used:

- [Security descriptors for new objects](https://learn.microsoft.com/en-us/windows/win32/secauthz/security-descriptors-for-new-objects)
  and [CreateDirectoryW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createdirectoryw):
  supply the descriptor at object creation and suppress inherited ACL expansion
  with the protected DACL bit; verify persistent ACL support.
- [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew):
  `CREATE_NEW` rejects existing objects; `FILE_FLAG_OPEN_REPARSE_POINT` exposes
  reparse objects for rejection; omitted delete sharing retains rename/delete
  fencing. Attribute/extended-attribute access is not covered by share modes.
- [SHGetKnownFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)
  and [KNOWN_FOLDER_FLAG](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/ne-shlobj_core-known_folder_flag):
  obtain the default installation anchor without caller environment input and
  release the returned allocation. COM initialization is balanced by the caller.
- [FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info):
  combine the volume and 128-bit identifier to compare the held directory identity.
- [File streams](https://learn.microsoft.com/en-us/windows/win32/fileio/file-streams):
  independently opened named streams require the ACL/lifecycle protection too.
