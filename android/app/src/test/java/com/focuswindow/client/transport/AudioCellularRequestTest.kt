package com.focuswindow.client.transport

import android.content.Context
import android.net.ConnectivityManager
import android.net.IpPrefix
import android.net.LinkAddress
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.RouteInfo
import java.io.FileDescriptor
import java.net.InetAddress
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowNetwork
import org.robolectric.shadows.ShadowNetworkCapabilities

/** Framework callbacks/socket binding are simulated. No radio, DNS or JNI runs. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class AudioCellularRequestTest {
    private val context: Context get() = RuntimeEnvironment.getApplication()
    private val manager get() = context.getSystemService(ConnectivityManager::class.java)
    private val access = AudioCellularAccess(true, false, false, true, AudioCellularAccess.Route.DIRECT_ALLOWED)
    private fun request(policy: AudioCellularAccess = access) = AudioCellularRequest(context, 1000, 1000,
        AudioCellularTarget("192.0.2.9", 44002), policy) { }
    private fun reject(action: () -> Unit) { assertTrue(runCatching(action).isFailure) }
    private fun capabilities(metered: Boolean = false, roaming: Boolean = false, vpn: Boolean = false): NetworkCapabilities =
        ShadowNetworkCapabilities.newInstance().apply {
            shadowOf(this).apply {
                addTransportType(NetworkCapabilities.TRANSPORT_CELLULAR)
                addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                if (!metered) addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
                if (!roaming) addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_ROAMING)
                if (vpn) addTransportType(NetworkCapabilities.TRANSPORT_VPN)
                else addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
            }
        }
    private fun link(): LinkProperties = LinkProperties().apply {
        interfaceName = "test-cellular"
        val address = LinkAddress::class.java.getConstructor(InetAddress::class.java, Int::class.javaPrimitiveType)
            .newInstance(InetAddress.getByName("192.0.2.2"), 24)
        LinkProperties::class.java.getDeclaredMethod("addLinkAddress", LinkAddress::class.java).invoke(this, address)
        // RouteInfo's constructor is hidden from the public SDK; only the test
        // fixture uses reflection to construct the callback snapshot.
        val route = RouteInfo::class.java.getConstructor(IpPrefix::class.java, InetAddress::class.java, String::class.java)
            .newInstance(IpPrefix(InetAddress.getByName("0.0.0.0"), 0), InetAddress.getByName("192.0.2.1"), interfaceName)
        addRoute(route)
    }
    private fun snapshots(callback: ConnectivityManager.NetworkCallback, network: Network,
        caps: NetworkCapabilities = capabilities(), blocked: Boolean = false) {
        callback.onCapabilitiesChanged(network, caps); callback.onLinkPropertiesChanged(network, link())
        callback.onBlockedStatusChanged(network, blocked)
    }
    @Test fun absent_consent_route_or_background_policy_creates_no_network_request() {
        for (policy in listOf(access.copy(mobileDataConsent = false), access.copy(backgroundDataAllowed = false),
            access.copy(route = AudioCellularAccess.Route.UNCONFIRMED), access.copy(route = AudioCellularAccess.Route.VPN_REQUIRED))) {
            val request = request(policy); assertFalse(request.start())
            assertEquals(AudioCellularCustody.Phase.RELEASED, request.phase)
            assertTrue(shadowOf(manager).networkCallbacks.isEmpty())
        }
    }
    @Test fun stop_before_start_releases_without_requesting_or_resurrecting_a_network() {
        val request = request(); request.retire()
        assertEquals(AudioCellularCustody.Phase.RELEASED, request.phase); reject { request.start() }
        assertTrue(shadowOf(manager).networkCallbacks.isEmpty())
    }
    @Test fun available_is_not_ready_and_the_same_callback_survives_promotion_until_real_native_return() {
        val request = request(); assertTrue(request.start())
        val callback = shadowOf(manager).networkCallbacks.single()
        val cellular = ShadowNetwork.newInstance(91); val incumbent = ShadowNetwork.newInstance(92)
        callback.onAvailable(cellular); reject { request.runNative { fail("Missing network snapshots"); 0 } }
        snapshots(callback, cellular)
        val fd = FileDescriptor()
        assertEquals(7, request.runNative { binding ->
            binding.bindUnconnectedSocket(fd)
            assertTrue(shadowOf(cellular).isSocketBound(fd)); assertFalse(shadowOf(incumbent).isSocketBound(fd))
            val pin = binding.pin(ByteArray(32) { 1 }); request.promote(pin); request.cancelPreparation()
            assertEquals(AudioCellularCustody.Phase.ACTIVE, request.phase)
            assertTrue(shadowOf(manager).networkCallbacks.contains(callback)); assertNull(manager.boundNetworkForProcess)
            request.retire(); assertTrue(shadowOf(manager).networkCallbacks.contains(callback))
            reject { binding.requireCurrent() }; 7
        })
        assertTrue(shadowOf(manager).networkCallbacks.isEmpty())
        assertEquals(AudioCellularCustody.Phase.RELEASED, request.phase)
        callback.onAvailable(incumbent); snapshots(callback, incumbent)
        reject { request.runNative { fail("Stale callback revived retired job"); 0 } }
    }
    @Test fun metering_roaming_blocking_and_vpn_capabilities_deny_candidate_work() {
        for ((caps, blocked) in listOf(capabilities(metered = true) to false, capabilities(roaming = true) to false,
            capabilities(vpn = true) to false, capabilities() to true)) {
            val request = request(); assertTrue(request.start())
            val callback = shadowOf(manager).networkCallbacks.single(); val network = ShadowNetwork.newInstance(93)
            callback.onAvailable(network); snapshots(callback, network, caps, blocked)
            reject { request.runNative { fail("Denied route started work"); 0 } }
            assertEquals(AudioCellularCustody.Phase.RELEASED, request.phase)
            assertTrue(shadowOf(manager).networkCallbacks.isEmpty())
        }
    }
    @Test fun callback_network_replacement_cannot_rebind_or_release_a_possibly_committed_path() {
        val request = request(); assertTrue(request.start())
        val callback = shadowOf(manager).networkCallbacks.single()
        val first = ShadowNetwork.newInstance(94); val replacement = ShadowNetwork.newInstance(95)
        callback.onAvailable(first); snapshots(callback, first)
        request.runNative { binding ->
            binding.pin(ByteArray(32) { 1 })
            callback.onAvailable(replacement); snapshots(callback, replacement)
            reject { binding.bindUnconnectedSocket(FileDescriptor()) }
            assertEquals(0, shadowOf(replacement).boundSocketCount())
            assertEquals(AudioCellularCustody.Phase.RECONCILING, request.phase)
            request.cancelPreparation(); assertTrue(shadowOf(manager).networkCallbacks.contains(callback)); 0
        }
        assertEquals(AudioCellularCustody.Phase.RECONCILING, request.phase)
        assertTrue(shadowOf(manager).networkCallbacks.contains(callback))
        request.retire(); assertTrue(shadowOf(manager).networkCallbacks.isEmpty())
    }
}
