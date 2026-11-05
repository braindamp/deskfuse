package com.focuswindow.client.transport

/** One candidate's cellular request. This owns network custody, never media authority.
 * The independent audio owner serializes events and supplies BOOTTIME, deadlines
 * and the exact N2 request. No callback/default-route change authorizes Detach.
 */
internal class AudioCellularCustody(
    acquisitionMilliseconds: Int,
    private val warmMilliseconds: Int,
    private val clock: () -> Long,
) {
    enum class Phase { ACQUIRING, WARMING, PINNED, RECONCILING, ACTIVE, RETIRING, RELEASING, RELEASED, QUARANTINED }
    class Work internal constructor()
    class Pin internal constructor(request: ByteArray) {
        private val request = request.copyOf()
        fun requestId() = request.copyOf()
    }
    private val gate = Any()
    private var lastNow = clock()
    private val acquisitionUntil: Long
    private var warmUntil: Long? = null
    private var registrationPending = true
    private var registered = false
    private var automaticallyReleased = false
    private var work: Work? = null
    private var workStarted = false
    private var pin: Pin? = null
    private var network: Long? = null
    private var usable = false
    private var networkDenied = false
    private var state = Phase.ACQUIRING
    init {
        require(acquisitionMilliseconds > 0 && warmMilliseconds > 0 && lastNow >= 0)
        acquisitionUntil = Math.addExact(lastNow, acquisitionMilliseconds.toLong() * 1_000_000L)
    }
    private fun sample(): Long {
        val n = clock()
        if (n < lastNow || n < 0) {
            retireLocked(); error("Cellular owner clock regressed")
        }
        lastNow = n
        if (state == Phase.ACQUIRING && n >= acquisitionUntil ||
            state == Phase.WARMING && n >= checkNotNull(warmUntil)) cancelLocked()
        return n
    }
    fun phase(): Phase = synchronized(gate) { sample(); state }
    fun deadline(): Long? = synchronized(gate) {
        sample()
        when (state) { Phase.ACQUIRING -> acquisitionUntil; Phase.WARMING -> warmUntil; else -> null }
    }
    fun registrationFinished(succeeded: Boolean) = synchronized(gate) {
        check(registrationPending); registrationPending = false; registered = succeeded && !automaticallyReleased
        if (!succeeded) retireLocked()
    }
    /** A requestNetwork callback can change its selected Network. Never move the
     * existing socket/TLS owner to that new Network or revive an old attempt. */
    fun available(handle: Long): Boolean = synchronized(gate) {
        val n = sample(); require(handle != 0L)
        if (state != Phase.ACQUIRING && state != Phase.WARMING && state != Phase.PINNED &&
            state != Phase.RECONCILING && state != Phase.ACTIVE) return@synchronized false
        if (network != null && network != handle) { unusableLocked(); return@synchronized false }
        if (network == null) {
            val until = try { Math.addExact(n, warmMilliseconds.toLong() * 1_000_000L) }
                catch (error: ArithmeticException) { retireLocked(); throw error }
            network = handle; warmUntil = until; state = Phase.WARMING
        }
        true
    }
    fun observation(handle: Long, allowed: Boolean) = synchronized(gate) {
        sample()
        if (network == handle && state in livePhases) {
            usable = allowed && !networkDenied
            if (!allowed) unusableLocked()
        }
    }
    fun lost(handle: Long) = synchronized(gate) {
        sample(); if (network == handle) unusableLocked()
    }
    /** Android automatically releases a request that reports onUnavailable. */
    fun unavailable() = synchronized(gate) {
        registered = false; automaticallyReleased = true; unusableLocked()
    }
    fun beginWork(handle: Long): Work = synchronized(gate) {
        sample(); check(state == Phase.WARMING && usable && network == handle && !workStarted)
        Work().also { work = it; workStarted = true }
    }
    fun requireWork(value: Work, handle: Long) = synchronized(gate) {
        sample(); check(work === value && network == handle && usable && state in livePhases) { "Retired cellular setup" }
    }
    fun pin(value: Work, requestId: ByteArray): Pin = synchronized(gate) {
        sample(); check(work === value && usable && state == Phase.WARMING ||
            work === value && pin != null && state in setOf(Phase.PINNED, Phase.RECONCILING, Phase.ACTIVE))
        AudioRequestId(requestId)
        pin?.let { require(it.requestId().contentEquals(requestId)); return@synchronized it }
        Pin(requestId).also { pin = it; state = Phase.PINNED }
    }
    /** Custody transfer only. The verified receipt/native owner separately gates media. */
    fun promote(value: Pin) = synchronized(gate) {
        sample(); check(pin === value && state in setOf(Phase.PINNED, Phase.RECONCILING, Phase.ACTIVE))
        state = Phase.ACTIVE
    }
    fun cancelPreparation() = synchronized(gate) { sample(); cancelLocked() }
    private fun cancelLocked() {
        when (state) {
            Phase.ACQUIRING, Phase.WARMING -> retireLocked()
            Phase.PINNED -> state = Phase.RECONCILING
            else -> Unit // A preparation timer never releases a promoted lease.
        }
    }
    private fun unusableLocked() {
        usable = false; networkDenied = true
        when (state) {
            Phase.ACQUIRING, Phase.WARMING -> retireLocked()
            Phase.PINNED -> state = Phase.RECONCILING
            else -> Unit
        }
    }
    /** Local Stop/expiry first denies the source; request actual socket/native
     * shutdown next. Only workCompleted after real I/O return permits release. */
    fun retire() = synchronized(gate) { retireLocked() }
    private fun retireLocked() {
        usable = false; networkDenied = true
        if (state !in setOf(Phase.RELEASING, Phase.RELEASED, Phase.QUARANTINED)) state = Phase.RETIRING
    }
    fun workCompleted(value: Work) = synchronized(gate) {
        check(work === value); work = null; usable = false
        when (state) {
            Phase.WARMING -> retireLocked()
            Phase.PINNED -> state = Phase.RECONCILING
            else -> Unit
        }
    }
    /** null: retain custody. false: no platform registration to release. */
    fun beginRelease(): Boolean? = synchronized(gate) {
        if (state != Phase.RETIRING || registrationPending || work != null) return@synchronized null
        state = Phase.RELEASING; registered
    }
    fun releaseCompleted(succeeded: Boolean) = synchronized(gate) {
        check(state == Phase.RELEASING)
        if (succeeded) registered = false
        state = if (succeeded) Phase.RELEASED else Phase.QUARANTINED
    }
    private companion object {
        val livePhases = setOf(Phase.WARMING, Phase.PINNED, Phase.RECONCILING, Phase.ACTIVE)
    }
}
