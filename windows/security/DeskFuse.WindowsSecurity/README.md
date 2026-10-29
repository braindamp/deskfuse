# Fixed Windows private storage

`DeskFuse.WindowsSecurity` targets `net8.0` and has no runtime dependency on
maintenance tools or audio code. T02 maintenance and broker authority enrollment
records use the same private creation, retained identity, ACL inspection, bounded
copy, reparse rejection, and held-handle alternate-stream verification code.

`SecurePrivateStorageRoot` supports exactly these profiles:

| Profile | Directory under the default OS x64 Program Files anchor | Initial children |
| --- | --- | --- |
| `PrivateStorageProfile.Maintenance` | `DeskFuse-Maintenance` | `approvals`, `runs` |
| `PrivateStorageProfile.BrokerAuthority` | `DeskFuse-Authority` | `enrollments` plus private `owner.lock` file |

The anchor is resolved with `SHGetKnownFolderPath(FOLDERID_ProgramFilesX64,
KF_FLAG_DEFAULT_PATH)`. Environment variables, caller-selected roots, arbitrary
directories, and boolean security modes cannot select a privileged namespace.
The supported trusted Windows installation/installer boundary is the same as the
existing maintenance implementation; administrator/kernel compromise or an
administrator's prior weakening of the anchor is outside that boundary.

Both factories require SYSTEM. `CreateFresh(profile)` atomically reserves the
single fixed directory using its private security descriptor at creation; a
collision or incomplete bootstrap is retained and fails closed. It never adopts
an exposed existing directory. `OpenExisting(profile, expectedNamespaceId)`
requires an independently supplied nonempty GUID and checks the canonical
`bootstrap.json` against that identity and the held directory's volume/full file
identity. It cannot discover or bless an arbitrary old namespace.

Keep the root alive for every use. It retains the OS anchor, directory identity,
and read-locked bootstrap, and validates them at each boundary. Relative directory
and file operations reject traversal, DOS device names, alternate-stream syntax,
and ambiguous path components. `CopyProtectedFile` uses CreateNew into an object
born private, bounds the copy, verifies the persisted bytes, flushes, and returns
read custody. Failed/incomplete files remain private; callers must fail closed
and must not treat a subsequent existence check as verification or overwrite them.
Directory inspection alone is non-authorizing and has no public path-based
factory.

`AcquireBrokerOwnerLease()` accepts only the `BrokerAuthority` profile and opens
the fixed, privately bootstrapped `owner.lock` with `OPEN_EXISTING`, read/write
access, and `FileShare.None`. The root and SYSTEM identity are validated first;
the acquired file is checked for its exact final path, protected ACL, regular
non-reparse type, and unnamed data stream. The returned `IDisposable` retains the
parent directory chain and exclusive handle without exposing a mutable stream.
A second process cannot own the authority concurrently. Disposal or process death
releases the lease; acquiring it never restores active registrations or grants.
A missing or contested owner file fails closed and is never recreated by acquire.

`SecureMaintenanceRoot` remains a thin T02 compatibility facade selecting only
`Maintenance`. Its existing bootstrap record encoding and directory name remain
unchanged. `DeskFuse.DriverTestOwner.Core.WindowsFileStreams` delegates to the
shared held-handle implementation. The helper's required bundle includes
`DeskFuse.WindowsSecurity.dll`.

Pure profile, bootstrap, copy, descriptor, and ABI checks can run without creating
privileged directories, changing ACLs, handling certificates, accessing devices,
or starting workers. Live storage creation/reopen is reserved for an explicitly
authorized SYSTEM installation/service path.
