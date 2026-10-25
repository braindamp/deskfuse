package com.focuswindow.client.media

import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Process-wide ownership barrier. No audio or JNI work runs under a UI lock. */
internal class NativeOwnerCoordinator(
    private val scope: CoroutineScope,
    private val prepare: (String) -> Boolean,
    private val stopNative: () -> Unit,
    private val dispatcher: CoroutineDispatcher = Dispatchers.IO,
) {
    data class Lease(val client: Long, val generation: Long)
    private data class Owner(val lease: Lease, var job: Job? = null)
    private val clients = AtomicLong()
    private val generation = AtomicLong()
    private val visible = AtomicReference<Lease?>(null)
    private val serial = Mutex()
    private var owner: Owner? = null

    fun register(): Long = clients.incrementAndGet()
    fun isCurrent(lease: Lease?): Boolean = lease != null && visible.get() == lease

    suspend fun claim(client: Long): Lease = serial.withLock {
        check(client == clients.get()) { "Retired native controller" }
        retireLocked()
        Lease(client, generation.incrementAndGet()).also {
            owner = Owner(it)
            visible.set(it)
        }
    }

    suspend fun start(lease: Lease, library: String, run: () -> Int, finished: (Int) -> Unit) = serial.withLock {
        val current = checkNotNull(owner)
        check(current.lease == lease && visible.get() == lease && lease.client == clients.get())
        check(current.job == null) { "Native owner already started" }
        check(prepare(library)) { "Native owner unavailable" }
        // This persistent scope survives a replaced Service/controller. Joining
        // this job means synchronous JNI really returned, not just cancellation.
        val job = scope.launch(dispatcher, start = CoroutineStart.LAZY) {
            val result = runCatching { run() }.getOrDefault(-1)
            if (isCurrent(lease)) finished(result)
        }
        current.job = job
        job.start()
    }

    suspend fun retire(lease: Lease) = serial.withLock {
        if (owner?.lease == lease) retireLocked()
    }
    fun requestRetire(lease: Lease) { scope.launch { retire(lease) } }

    private suspend fun retireLocked() {
        val previous = owner ?: return
        visible.set(null) // stale completion is invalid before requesting stop
        if (previous.job != null) {
            stopNative()
            previous.job!!.join() // job completion never needs this mutex
        }
        owner = null
    }
}
