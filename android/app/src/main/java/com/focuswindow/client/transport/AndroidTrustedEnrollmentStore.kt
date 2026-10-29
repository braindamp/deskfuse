package com.focuswindow.client.transport

import android.app.Application
import android.content.Context
import android.system.ErrnoException
import android.system.Os
import android.system.OsConstants
import java.io.File
import java.io.FileDescriptor
import java.nio.ByteBuffer
import java.util.UUID

/** One process-owned cold writer. Descriptors never escape into proof/media code. */
internal class AndroidTrustedEnrollmentStore(context: Context) : TrustedEnrollmentPersistence {
    private val app = context.applicationContext
    private val root = File(app.noBackupFilesDir, "trusted-enrollments")
    private val owner: Owner
    init {
        check(Application.getProcessName() == app.packageName) { "Enrollment writes belong to the main authority process" }
        owner = synchronized(owners) { owners.getOrPut(root.absolutePath) { Owner(app.noBackupFilesDir, root) } }
    }

    override fun requireOwnerCurrent() {
        check(!owner.failed) { "Enrollment persistence owner is quarantined" } // Memory only, including during proof checks.
    }

    override fun hasRevocation(host: ByteArray, enrollment: UUID): Boolean = synchronized(owner) {
        requireOwnerCurrent()
        owner.ensureRoot()
        val file = record(host, enrollment, "revoked")
        // Presence precedes all content/type validation. Legacy negative residue also denies.
        owner.present(file) || owner.present(File(file.path + ".new")) || owner.present(File(file.path + ".bak"))
    }

    override fun read(host: ByteArray, enrollment: UUID, suffix: String): ByteArray? = synchronized(owner) {
        requireOwnerCurrent(); owner.ensureRoot()
        val file = record(host, enrollment, suffix)
        if (suffix == "revoked") return@synchronized if (hasRevocation(host, enrollment)) byteArrayOf(1) else null
        owner.requireNoResidue(file)
        val held = owner.hold(file, create = false) ?: return@synchronized null
        held.completed?.copyOf() ?: owner.read(held) // Raw bytes are not a completed capability.
    }

    override fun preparedRecords(): List<ByteArray> = synchronized(owner) {
        requireOwnerCurrent(); owner.ensureRoot()
        val result = mutableListOf<ByteArray>()
        java.nio.file.Files.newDirectoryStream(root.toPath()).use { entries ->
            var count = 0
            for (entry in entries) {
                check(++count <= 16384) { "Enrollment store exceeds its bound" }
                val name = entry.fileName.toString()
                if (name.endsWith(".prepared.new") || name.endsWith(".prepared.bak")) {
                    owner.failed = true; error("Pending enrollment publication requires explicit recovery")
                }
                if (name.endsWith(".prepared")) {
                    check(name.matches(Regex("[0-9a-f]{64}\\.prepared")))
                    result.add(owner.read(owner.hold(entry.toFile(), create = false) ?: error("Prepared record disappeared")))
                }
            }
        }
        result
    }

    override fun writeOnce(host: ByteArray, enrollment: UUID, suffix: String, bytes: ByteArray) = synchronized(owner) {
        requireOwnerCurrent(); require(bytes.size in 1..TrustedEnrollmentProtocol.MAX_FRAME)
        owner.ensureRoot()
        val file = record(host, enrollment, suffix)
        if (suffix != "revoked") owner.requireNoResidue(file)
        val held = owner.hold(file, create = true)!!
        try {
            if (suffix == "revoked") {
                if (!held.denialCompleted) {
                    // An empty/torn negative artifact needs no positive payload repair.
                    Os.fsync(held.fd); Os.fsync(owner.directory!!)
                    held.denialCompleted = true
                }
            } else {
                val snapshot = bytes.copyOf()
                if (held.completed != null) {
                    require(held.completed!!.contentEquals(snapshot)) { "Immutable enrollment changed" }
                    return@synchronized
                }
                if (!held.created || Os.fstat(held.fd).st_size != 0L) {
                    require(owner.read(held).contentEquals(snapshot)) { "Refusing to repair different immutable enrollment bytes" }
                }
                // Repository fully validated the exact existing bytes before calling this method.
                var written = 0
                while (written < snapshot.size) {
                    val count = try { Os.pwrite(held.fd, snapshot, written, snapshot.size - written, written.toLong()) }
                    catch (interrupted: ErrnoException) { if (interrupted.errno == OsConstants.EINTR) continue else throw interrupted }
                    check(count > 0) { "Enrollment write made no progress" }; written += count
                }
                check(owner.read(held).contentEquals(snapshot)) { "Enrollment write verification failed" }
                Os.fsync(held.fd); Os.fsync(owner.directory!!)
                owner.validate(held)
                check(Os.fstat(held.fd).st_size == snapshot.size.toLong())
                held.completed = snapshot
            }
        } catch (failure: Exception) {
            owner.failed = true // Do not erase a reported error with a later successful fsync.
            throw failure
        }
        Unit
    }

    private fun record(host: ByteArray, enrollment: UUID, suffix: String): File {
        require(host.size == 32 && enrollment != UUID(0, 0) && suffix in setOf("proposed", "prepared", "active", "revoked"))
        val id = ByteBuffer.allocate(16).putLong(enrollment.mostSignificantBits).putLong(enrollment.leastSignificantBits).array()
        val name = TrustedEnrollmentProtocol.hash("DeskFuse.Enrollment.Store.v1\u0000".toByteArray(Charsets.US_ASCII) + host + id)
            .joinToString("") { "%02x".format(it.toInt() and 255) }
        return File(root, "$name.$suffix")
    }

    private class Held(val file: File, val fd: FileDescriptor, val device: Long, val inode: Long, val created: Boolean) {
        var completed: ByteArray? = null
        var denialCompleted = false
    }
    private class Owner(private val parent: File, private val root: File) {
        @Volatile var failed = false
        var directory: FileDescriptor? = null
        private var parentDirectory: FileDescriptor? = null
        private val held = mutableMapOf<String, Held>()

        fun present(file: File): Boolean = try { Os.lstat(file.absolutePath); true }
        catch (missing: ErrnoException) {
            if (missing.errno == OsConstants.ENOENT) false else { failed = true; throw missing }
        }
        fun requireNoResidue(file: File) {
            if (present(File(file.path + ".new")) || present(File(file.path + ".bak"))) {
                failed = true
                error("Legacy enrollment publication requires explicit recovery")
            }
        }
        fun ensureRoot() {
            if (directory != null) return
            var parentFd: FileDescriptor? = null
            var rootFd: FileDescriptor? = null
            try {
                // O_DIRECTORY is not exposed by the public Android SDK; verify
                // both opened descriptors as directories before using either.
                val flags = OsConstants.O_RDONLY or OsConstants.O_CLOEXEC or OsConstants.O_NOFOLLOW
                parentFd = Os.open(parent.absolutePath, flags, 0)
                check(OsConstants.S_ISDIR(Os.fstat(parentFd).st_mode))
                if (!present(root)) Os.mkdir(root.absolutePath, 448) // 0700, private at creation.
                rootFd = Os.open(root.absolutePath, flags, 0)
                val stat = Os.fstat(rootFd)
                check(OsConstants.S_ISDIR(stat.st_mode) && stat.st_uid == Os.getuid() && (stat.st_mode and 63) == 0)
                val named = Os.lstat(root.absolutePath)
                check(named.st_dev == stat.st_dev && named.st_ino == stat.st_ino)
                Os.fsync(rootFd); Os.fsync(parentFd)
                directory = rootFd; parentDirectory = parentFd
            } catch (failure: Exception) {
                failed = true
                rootFd?.let { runCatching { Os.close(it) } }
                parentFd?.let { runCatching { Os.close(it) } }
                throw failure
            }
        }
        fun hold(file: File, create: Boolean): Held? {
            held[file.name]?.let { return it }
            check(held.size < 16384) { "Enrollment file custody exceeds its bound" }
            val flags = OsConstants.O_RDWR or OsConstants.O_CLOEXEC or OsConstants.O_NOFOLLOW
            var created = false
            val fd = try { Os.open(file.absolutePath, flags, 0) }
            catch (missing: ErrnoException) {
                if (missing.errno != OsConstants.ENOENT) { failed = true; throw missing }
                if (!create) return null
                try {
                    Os.open(file.absolutePath, flags or OsConstants.O_CREAT or OsConstants.O_EXCL, 384).also { created = true } // 0600.
                } catch (failure: Exception) { failed = true; throw failure }
            }
            try {
                val stat = Os.fstat(fd)
                return Held(file, fd, stat.st_dev, stat.st_ino, created).also { validate(it); held[file.name] = it }
            } catch (failure: Exception) { failed = true; runCatching { Os.close(fd) }; throw failure }
        }
        fun validate(value: Held) {
            val stat = Os.fstat(value.fd)
            check(OsConstants.S_ISREG(stat.st_mode) && stat.st_uid == Os.getuid() && (stat.st_mode and 63) == 0 &&
                stat.st_nlink == 1L && stat.st_dev == value.device && stat.st_ino == value.inode)
            val named = Os.lstat(value.file.absolutePath)
            check(named.st_dev == value.device && named.st_ino == value.inode && OsConstants.S_ISREG(named.st_mode))
        }
        fun read(value: Held): ByteArray = try {
            validate(value)
            val size = Os.fstat(value.fd).st_size
            require(size in 1..TrustedEnrollmentProtocol.MAX_FRAME.toLong()) { "Invalid saved enrollment size" }
            val bytes = ByteArray(size.toInt()); var count = 0
            while (count < bytes.size) {
                val read = try { Os.pread(value.fd, bytes, count, bytes.size - count, count.toLong()) }
                catch (interrupted: ErrnoException) { if (interrupted.errno == OsConstants.EINTR) continue else throw interrupted }
                check(read > 0) { "Enrollment record changed during read" }; count += read
            }
            check(Os.fstat(value.fd).st_size == size)
            bytes
        } catch (failure: Exception) { failed = true; throw failure }
    }
    private companion object { val owners = mutableMapOf<String, Owner>() }
}
